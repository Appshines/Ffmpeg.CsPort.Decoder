// SPDX-FileCopyrightText: The respective FFmpeg copyright holders; see UPSTREAM-COPYRIGHTS.txt
// SPDX-FileCopyrightText: 2026 Ffmpeg.CsPort.Decoder contributors
// SPDX-License-Identifier: LGPL-2.1-or-later
/*
 * This file is part of Ffmpeg.CsPort.Decoder, an independent C# port of FFmpeg.
 *
 * This file belongs to a C# translation and modified work of FFmpeg source code from commit
 * 9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b:
 * https://github.com/FFmpeg/FFmpeg/tree/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b
 * The exact upstream-file mapping and the original copyright notices are recorded in
 * PORTED-FROM-FFMPEG.md and UPSTREAM-COPYRIGHTS.txt.
 *
 * Created or modified 2026-08-06: translated from C to C# or added as managed port support.
 *
 * This library is free software; you can redistribute it and/or modify it under the
 * terms of the GNU Lesser General Public License as published by the Free Software
 * Foundation; either version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful, but WITHOUT ANY
 * WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
 * PARTICULAR PURPOSE. See the GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License along with
 * this library. If not, see <https://www.gnu.org/licenses/>.
 *
 * PORT-NOTE: 1:1 translation. Do not refactor, reorder, or simplify; bit-exactness
 * against the FFmpeg reference is verified by the conformance tests.
 */
using System;
using System.Buffers.Binary;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Codecs.Alac
{
	/// <summary>
	/// Ports FFmpeg's ALAC decoder for 1–8 channels and 16/20/24/32-bit planar integer output.
	/// </summary>
	public sealed class AlacDecoder
	{
		private static readonly byte[][] ChannelLayoutOffsets =
		{
			new byte[] { 0 },
			new byte[] { 0, 1 },
			new byte[] { 2, 0, 1 },
			new byte[] { 2, 0, 1, 3 },
			new byte[] { 2, 0, 1, 3, 4 },
			new byte[] { 2, 0, 1, 4, 5, 3 },
			new byte[] { 2, 0, 1, 4, 5, 6, 3 },
			new byte[] { 2, 6, 7, 0, 1, 4, 5, 3 }
		};

		private readonly BitReader _Reader = new BitReader();
		private readonly int[][] _PredictionError = new int[2][];
		private readonly int[][] _ExtraBits = new int[2][];
		private readonly int[][] _FrameSamples;
		private readonly int[][] _CurrentOutput = new int[2][];
		private readonly short[][] _Coefficients = { new short[32], new short[32] };
		private readonly int _MaximumSamplesPerFrame;
		private readonly int _SampleSize;
		private readonly int _RiceHistoryMultiplier;
		private readonly int _RiceInitialHistory;
		private readonly int _RiceLimit;
		private readonly int _Channels;
		private readonly int _SampleRate;
		private int _NumberOfSamples;
		private int _CurrentExtraBitCount;

		private AlacDecoder(
			int maximumSamplesPerFrame,
			int sampleSize,
			int riceHistoryMultiplier,
			int riceInitialHistory,
			int riceLimit,
			int channels,
			int sampleRate)
		{
			_MaximumSamplesPerFrame = maximumSamplesPerFrame;
			_SampleSize = sampleSize;
			_RiceHistoryMultiplier = riceHistoryMultiplier;
			_RiceInitialHistory = riceInitialHistory;
			_RiceLimit = riceLimit;
			_Channels = channels;
			_SampleRate = sampleRate;
			_FrameSamples = new int[channels][];
			for (var channel = 0; channel < channels; channel++)
				_FrameSamples[channel] = new int[maximumSamplesPerFrame];
			for (var channel = 0; channel < 2; channel++)
			{
				_PredictionError[channel] = new int[maximumSamplesPerFrame];
				_ExtraBits[channel] = new int[maximumSamplesPerFrame];
			}
		}

		public int MaximumSamplesPerFrame => _MaximumSamplesPerFrame;
		public int SampleSize => _SampleSize;
		public int Channels => _Channels;
		public int SampleRate => _SampleRate;

		public static int Initialize(byte[] extraData, out AlacDecoder decoder)
		{
			decoder = null;
			if (extraData == null || extraData.Length < 36)
				return FfmpegError.InvalidData;
			var maximumSamples = unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(extraData.AsSpan(12, 4)));
			var sampleSize = extraData[17];
			var historyMultiplier = extraData[18];
			var initialHistory = extraData[19];
			var riceLimit = extraData[20];
			var channels = extraData[21];
			var sampleRate = unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(extraData.AsSpan(32, 4)));
			if (maximumSamples <= 0 || maximumSamples > 4096 * 4096)
				return FfmpegError.InvalidData;
			if (sampleSize != 16 && sampleSize != 20 && sampleSize != 24 && sampleSize != 32)
				return FfmpegError.PatchWelcome;
			if (channels < 1)
				return FfmpegError.InvalidArgument;
			if (channels > 8)
				return FfmpegError.PatchWelcome;

			decoder = new AlacDecoder(maximumSamples, sampleSize, historyMultiplier, initialHistory, riceLimit, channels, sampleRate);
			return 0;
		}

		/// <summary>
		/// Decodes all syntax elements in one ALAC packet and writes FFmpeg-compatible planar integer samples.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < 0 || packetLength > packet.Length - packetOffset ||
				_Reader.Initialize(packet, packetOffset, packetLength * 8) < 0)
			{
				return FfmpegError.InvalidArgument;
			}

			_NumberOfSamples = 0;
			var decodedChannels = 0;
			var gotEnd = false;
			while (_Reader.BitsLeft >= 3)
			{
				var element = (int)_Reader.ReadBits(3);
				if (element == 7)
				{
					gotEnd = true;
					break;
				}
				if (element > 1 && element != 3)
					return FfmpegError.PatchWelcome;
				var elementChannels = element == 1 ? 2 : 1;
				if (decodedChannels + elementChannels > _Channels ||
					ChannelLayoutOffsets[_Channels - 1][decodedChannels] + elementChannels > _Channels)
				{
					return FfmpegError.InvalidData;
				}
				var result = DecodeElement(ChannelLayoutOffsets[_Channels - 1][decodedChannels], elementChannels);
				if (result < 0 && _Reader.BitsLeft != 0)
					return result;
				decodedChannels += elementChannels;
			}
			if (!gotEnd)
				return FfmpegError.InvalidData;
			if (decodedChannels != _Channels || _NumberOfSamples == 0)
				return packetLength;

			var bytesPerSample = _SampleSize == 16 ? 2 : 4;
			var planeSize = _NumberOfSamples * bytesPerSample;
			var outputSize = planeSize * _Channels;
			if (output.Length < outputSize)
				return FfmpegError.InvalidArgument;
			WriteOutput(output.Slice(0, outputSize), bytesPerSample, planeSize);
			var sampleFormat = bytesPerSample == 2 ? AudioSampleFormat.Signed16Planar : AudioSampleFormat.Signed32Planar;
			frame = new AudioFrameInfo(_NumberOfSamples, _Channels, sampleFormat, _Channels, planeSize, outputSize);
			return packetLength;
		}

		/// <summary>
		/// Decodes one single- or paired-channel ALAC element, including Rice coding, prediction, extra bits, and decorrelation.
		/// </summary>
		private int DecodeElement(int channelIndex, int channels)
		{
			_Reader.SkipBits(4);
			_Reader.SkipBits(12);
			var hasSize = _Reader.ReadBit() != 0;
			_CurrentExtraBitCount = (int)_Reader.ReadBits(2) << 3;
			var bitsPerSample = _SampleSize - _CurrentExtraBitCount + channels - 1;
			if (bitsPerSample > 32)
				return FfmpegError.PatchWelcome;
			if (bitsPerSample < 1)
				return FfmpegError.InvalidData;
			var isCompressed = _Reader.ReadBit() == 0;
			var outputSamples = hasSize ? (int)_Reader.ReadBitsLong(32) : _MaximumSamplesPerFrame;
			if (outputSamples <= 0 || outputSamples > _MaximumSamplesPerFrame)
				return FfmpegError.InvalidData;
			if (_NumberOfSamples == 0)
				_NumberOfSamples = outputSamples;
			else if (outputSamples != _NumberOfSamples)
				return FfmpegError.InvalidData;

			for (var channel = 0; channel < channels; channel++)
				_CurrentOutput[channel] = _FrameSamples[channelIndex + channel];
			int decorrelationShift;
			int decorrelationLeftWeight;
			if (isCompressed)
			{
				var result = DecodeCompressed(channels, bitsPerSample, out decorrelationShift, out decorrelationLeftWeight);
				if (result < 0)
					return result;
			} else
			{
				if (_Reader.BitsLeft < (long)_NumberOfSamples * channels * _SampleSize)
					return FfmpegError.InvalidData;
				for (var sample = 0; sample < _NumberOfSamples; sample++)
					for (var channel = 0; channel < channels; channel++)
						_CurrentOutput[channel][sample] = _Reader.ReadSignedBits(_SampleSize);
				_CurrentExtraBitCount = 0;
				decorrelationShift = 0;
				decorrelationLeftWeight = 0;
			}

			if (channels == 2 && decorrelationLeftWeight != 0)
				AlacPrediction.DecorrelateStereo(_CurrentOutput[0], _CurrentOutput[1], _NumberOfSamples, decorrelationShift, decorrelationLeftWeight);
			if (_CurrentExtraBitCount != 0)
				for (var channel = 0; channel < channels; channel++)
					AlacPrediction.AppendExtraBits(_CurrentOutput[channel], _ExtraBits[channel], _CurrentExtraBitCount, _NumberOfSamples);

			if (_SampleSize == 20 || _SampleSize == 24)
			{
				var shift = _SampleSize == 20 ? 12 : 8;
				for (var channel = 0; channel < channels; channel++)
					for (var sample = 0; sample < _NumberOfSamples; sample++)
						_CurrentOutput[channel][sample] = unchecked((int)((uint)_CurrentOutput[channel][sample] * (1U << shift)));
			}
			return 0;
		}

		/// <summary>
		/// Reads both channel predictor descriptions and then performs their independent Rice and LPC reconstruction.
		/// </summary>
		private int DecodeCompressed(int channels, int bitsPerSample, out int decorrelationShift, out int decorrelationLeftWeight)
		{
			decorrelationShift = 0;
			decorrelationLeftWeight = 0;
			if (_RiceLimit == 0)
				return FfmpegError.NotImplemented;
			decorrelationShift = (int)_Reader.ReadBits(8);
			decorrelationLeftWeight = (int)_Reader.ReadBits(8);
			if (channels == 2 && decorrelationLeftWeight != 0 && decorrelationShift > 31)
				return FfmpegError.InvalidData;

			Span<int> predictionType = stackalloc int[2];
			Span<int> quantization = stackalloc int[2];
			Span<int> historyMultiplier = stackalloc int[2];
			Span<int> order = stackalloc int[2];
			for (var channel = 0; channel < channels; channel++)
			{
				predictionType[channel] = (int)_Reader.ReadBits(4);
				quantization[channel] = (int)_Reader.ReadBits(4);
				historyMultiplier[channel] = (int)_Reader.ReadBits(3);
				order[channel] = (int)_Reader.ReadBits(5);
				if (order[channel] >= _MaximumSamplesPerFrame || quantization[channel] == 0)
					return FfmpegError.InvalidData;
				for (var index = order[channel] - 1; index >= 0; index--)
					_Coefficients[channel][index] = (short)_Reader.ReadSignedBits(16);
			}

			if (_CurrentExtraBitCount != 0)
			{
				if (_Reader.BitsLeft < (long)_NumberOfSamples * channels * _CurrentExtraBitCount)
					return FfmpegError.InvalidData;
				for (var sample = 0; sample < _NumberOfSamples; sample++)
					for (var channel = 0; channel < channels; channel++)
						_ExtraBits[channel][sample] = (int)_Reader.ReadBits(_CurrentExtraBitCount);
			}

			for (var channel = 0; channel < channels; channel++)
			{
				var result = DecompressRice(
					_PredictionError[channel],
					bitsPerSample,
					historyMultiplier[channel] * _RiceHistoryMultiplier / 4);
				if (result < 0)
					return result;
				if (predictionType[channel] == 15)
					AlacPrediction.Predict(_PredictionError[channel], _PredictionError[channel], _NumberOfSamples, bitsPerSample, null, 31, 0);
				AlacPrediction.Predict(
					_PredictionError[channel],
					_CurrentOutput[channel],
					_NumberOfSamples,
					bitsPerSample,
					_Coefficients[channel],
					order[channel],
					quantization[channel]);
			}
			return 0;
		}

		private int DecompressRice(int[] output, int bitsPerSample, int historyMultiplier)
		{
			var history = unchecked((uint)_RiceInitialHistory);
			var signModifier = 0;
			for (var sample = 0; sample < _NumberOfSamples; sample++)
			{
				if (_Reader.BitsLeft <= 0)
					return FfmpegError.InvalidData;
				var parameter = Math.Min(FfmpegMath.Log2((history >> 9) + 3), _RiceLimit);
				var value = DecodeScalar(parameter, bitsPerSample) + (uint)signModifier;
				signModifier = 0;
				output[sample] = unchecked((int)((value >> 1) ^ (uint)-(int)(value & 1)));
				if (value > 0xffff)
					history = 0xffff;
				else
					history = unchecked(history + value * (uint)historyMultiplier - ((history * (uint)historyMultiplier) >> 9));

				if (history < 128 && sample + 1 < _NumberOfSamples)
				{
					parameter = Math.Min(7 - FfmpegMath.Log2(history) + (int)((history + 16) >> 6), _RiceLimit);
					var blockSize = (int)DecodeScalar(parameter, 16);
					if (blockSize > 0)
					{
						if (blockSize >= _NumberOfSamples - sample)
							blockSize = _NumberOfSamples - sample - 1;
						Array.Clear(output, sample + 1, blockSize);
						sample += blockSize;
					}
					if (blockSize <= 0xffff)
						signModifier = 1;
					history = 0;
				}
			}
			return 0;
		}

		private uint DecodeScalar(int parameter, int bitsPerSample)
		{
			uint value = 0;
			while (value < 9 && _Reader.ReadBit() != 0)
				value++;
			if (value > 8)
				return _Reader.ReadBitsLong(bitsPerSample);
			if (parameter != 1)
			{
				var extraBits = _Reader.ShowBits(parameter);
				value = (value << parameter) - value;
				if (extraBits > 1)
				{
					value += extraBits - 1;
					_Reader.SkipBits(parameter);
				} else
				{
					_Reader.SkipBits(parameter - 1);
				}
			}
			return value;
		}

		private void WriteOutput(Span<byte> output, int bytesPerSample, int planeSize)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				var outputOffset = channel * planeSize;
				for (var sample = 0; sample < _NumberOfSamples; sample++)
				{
					if (bytesPerSample == 2)
						BinaryPrimitives.WriteInt16LittleEndian(output.Slice(outputOffset, 2), (short)_FrameSamples[channel][sample]);
					else
						BinaryPrimitives.WriteInt32LittleEndian(output.Slice(outputOffset, 4), _FrameSamples[channel][sample]);
					outputOffset += bytesPerSample;
				}
			}
		}
	}
}
