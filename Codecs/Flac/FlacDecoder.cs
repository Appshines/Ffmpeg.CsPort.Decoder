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
 * PORT-NOTE: 1:1 translation. Performance-motivated, semantics-preserving transformations
 * applied (see repository history); bit-exactness remains verified by the conformance tests.
 */
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Codecs.Flac
{
	/// <summary>
	/// Ports FFmpeg's scalar FLAC decoder, including fixed/LPC prediction, Rice residuals, and stereo decorrelation.
	/// </summary>
	public sealed class FlacDecoder
	{
		private readonly FlacStreamInfo _StreamInfo;
		private readonly int[][] _Decoded;
		private readonly long[] _Decoded33;
		private readonly int[] _Coefficients = new int[32];
		private readonly BitReader _Reader = new BitReader();
		private int _BlockSize;
		private int _ChannelMode;

		private FlacDecoder(FlacStreamInfo streamInfo)
		{
			_StreamInfo = streamInfo;
			_Decoded = new int[streamInfo.Channels][];
			for (var channel = 0; channel < _Decoded.Length; channel++)
				_Decoded[channel] = new int[streamInfo.MaximumBlockSize];
			_Decoded33 = streamInfo.BitsPerSample == 32 && streamInfo.Channels == 2
				? new long[streamInfo.MaximumBlockSize]
				: null;
		}

		public FlacStreamInfo StreamInfo => _StreamInfo;

		public static int Initialize(byte[] streamInfoBlock, out FlacDecoder decoder)
		{
			decoder = null;
			if (streamInfoBlock == null)
				return FfmpegError.InvalidArgument;
			var streamInfo = new FlacStreamInfo();
			var result = streamInfo.Parse(streamInfoBlock);
			if (result < 0)
				return result;
			decoder = new FlacDecoder(streamInfo);
			return 0;
		}

		/// <summary>
		/// Decodes one complete FLAC frame and writes packed S16 or S32 samples in FFmpeg's native little-endian layout.
		/// </summary>
		public int DecodeFrame(
			byte[] packet,
			int packetOffset,
			int packetLength,
			Span<byte> output,
			out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < 10 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;

			var headerResult = FlacFrameHeaderParser.Parse(packet, packetOffset, packetLength, _Reader, out var header);
			if (headerResult < 0)
				return headerResult;
			if (header.Channels != _StreamInfo.Channels)
				return FfmpegError.InvalidData;
			var bitsPerSample = header.BitsPerSample == 0 ? _StreamInfo.BitsPerSample : header.BitsPerSample;
			if (bitsPerSample != _StreamInfo.BitsPerSample || header.BlockSize > _StreamInfo.MaximumBlockSize)
				return FfmpegError.InvalidData;

			_BlockSize = header.BlockSize;
			_ChannelMode = header.ChannelMode;
			var bytesPerSample = bitsPerSample > 16 ? 4 : 2;
			var outputSize = _BlockSize * header.Channels * bytesPerSample;
			if (output.Length < outputSize)
				return FfmpegError.InvalidArgument;

			for (var channel = 0; channel < header.Channels; channel++)
			{
				var result = DecodeSubframe(channel);
				if (result < 0)
					return result;
			}

			_Reader.Align();
			_Reader.SkipBits(16);
			var bytesRead = _Reader.Position / 8;
			if (bytesRead > packetLength)
				return FfmpegError.InvalidData;

			WriteDecorrelated(output.Slice(0, outputSize), bitsPerSample, bytesPerSample);
			var sampleFormat = bytesPerSample == 2 ? AudioSampleFormat.Signed16 : AudioSampleFormat.Signed32;
			frame = new AudioFrameInfo(_BlockSize, header.Channels, sampleFormat, 1, outputSize, outputSize);
			return bytesRead;
		}

		/// <summary>
		/// Decodes one subframe with its channel-dependent width, predictor, residuals, and wasted-bit restoration.
		/// </summary>
		private int DecodeSubframe(int channel)
		{
			var decoded = _Decoded[channel];
			var type = 0;
			var wasted = 0;
			var bitsPerSample = _StreamInfo.BitsPerSample;
			if (channel == 0)
			{
				if (_ChannelMode == 2)
					bitsPerSample++;
			} else if (_ChannelMode == 1 || _ChannelMode == 3)
			{
				bitsPerSample++;
			}

			if (_Reader.ReadBit() != 0)
				return FfmpegError.InvalidData;
			type = (int)_Reader.ReadBits(6);
			if (_Reader.ReadBit() != 0)
			{
				if (_Reader.BitsLeft <= 0)
					return FfmpegError.InvalidData;
				wasted = 1;
				while (_Reader.BitsLeft > 0 && _Reader.ReadBit() == 0)
					wasted++;
				bitsPerSample -= wasted;
				if (bitsPerSample <= 0)
					return FfmpegError.InvalidData;
			}

			int result;
			if (type == 0)
			{
				if (bitsPerSample < 33)
				{
					var value = _Reader.ReadSignedBits(bitsPerSample);
					for (var index = 0; index < _BlockSize; index++)
						decoded[index] = value;
				} else
				{
					var value = _Reader.ReadSignedBits64(33);
					for (var index = 0; index < _BlockSize; index++)
						_Decoded33[index] = value;
				}
			} else if (type == 1)
			{
				if (bitsPerSample < 33)
				{
					for (var index = 0; index < _BlockSize; index++)
						decoded[index] = _Reader.ReadSignedBits(bitsPerSample);
				} else
				{
					for (var index = 0; index < _BlockSize; index++)
						_Decoded33[index] = _Reader.ReadSignedBits64(33);
				}
			} else if (type >= 8 && type <= 12)
			{
				var predictorOrder = type & ~8;
				result = bitsPerSample < 33
					? DecodeFixed(decoded, predictorOrder, bitsPerSample)
					: DecodeFixed33(decoded, predictorOrder);
				if (result < 0)
					return result;
			} else if (type >= 32)
			{
				var predictorOrder = (type & ~32) + 1;
				result = bitsPerSample < 33
					? DecodeLpc(decoded, predictorOrder, bitsPerSample)
					: DecodeLpc33(decoded, predictorOrder);
				if (result < 0)
					return result;
			} else
			{
				return FfmpegError.InvalidData;
			}

			if (wasted != 0)
			{
				if (wasted + bitsPerSample == 33)
					FlacPrediction.RestoreWasted33(_Decoded33, decoded, wasted, _BlockSize);
				else if (wasted < 32)
					FlacPrediction.RestoreWasted32(decoded, wasted, _BlockSize);
			}
			return 0;
		}

		private int DecodeFixed(int[] decoded, int predictorOrder, int bitsPerSample)
		{
			for (var index = 0; index < predictorOrder; index++)
				decoded[index] = _Reader.ReadSignedBits(bitsPerSample);
			var result = DecodeResiduals(decoded, predictorOrder);
			if (result < 0)
				return result;
			return bitsPerSample + predictorOrder <= 32
				? FlacPrediction.DecodeFixed(decoded, predictorOrder, _BlockSize)
				: FlacPrediction.DecodeFixedWide(decoded, predictorOrder, _BlockSize);
		}

		private int DecodeFixed33(int[] residual, int predictorOrder)
		{
			for (var index = 0; index < predictorOrder; index++)
				_Decoded33[index] = _Reader.ReadSignedBits64(33);
			var result = DecodeResiduals(residual, predictorOrder);
			return result < 0 ? result : FlacPrediction.DecodeFixed33(_Decoded33, residual, predictorOrder, _BlockSize);
		}

		private int DecodeLpc(int[] decoded, int predictorOrder, int bitsPerSample)
		{
			for (var index = 0; index < predictorOrder; index++)
				decoded[index] = _Reader.ReadSignedBits(bitsPerSample);
			var coefficientPrecision = (int)_Reader.ReadBits(4) + 1;
			if (coefficientPrecision == 16)
				return FfmpegError.InvalidData;
			var quantizationLevel = _Reader.ReadSignedBits(5);
			if (quantizationLevel < 0)
				return FfmpegError.InvalidData;
			for (var index = 0; index < predictorOrder; index++)
				_Coefficients[predictorOrder - index - 1] = _Reader.ReadSignedBits(coefficientPrecision);

			var result = DecodeResiduals(decoded, predictorOrder);
			if (result < 0)
				return result;
			if (bitsPerSample <= 16 && bitsPerSample + coefficientPrecision + FfmpegMath.Log2((uint)predictorOrder) <= 32)
			{
				FlacPrediction.DecodeLpc16(decoded, _Coefficients, predictorOrder, quantizationLevel, _BlockSize);
			} else
			{
				FlacPrediction.DecodeLpc32(decoded, _Coefficients, predictorOrder, quantizationLevel, _BlockSize);
				if (_StreamInfo.BitsPerSample <= 16)
					FlacPrediction.AnalyzeRemodulate(decoded, _Coefficients, predictorOrder, quantizationLevel, _BlockSize, bitsPerSample);
			}
			return 0;
		}

		private int DecodeLpc33(int[] residual, int predictorOrder)
		{
			for (var index = 0; index < predictorOrder; index++)
				_Decoded33[index] = _Reader.ReadSignedBits64(33);
			var coefficientPrecision = (int)_Reader.ReadBits(4) + 1;
			if (coefficientPrecision == 16)
				return FfmpegError.InvalidData;
			var quantizationLevel = _Reader.ReadSignedBits(5);
			if (quantizationLevel < 0)
				return FfmpegError.InvalidData;
			for (var index = 0; index < predictorOrder; index++)
				_Coefficients[predictorOrder - index - 1] = _Reader.ReadSignedBits(coefficientPrecision);

			var result = DecodeResiduals(residual, predictorOrder);
			if (result < 0)
				return result;
			FlacPrediction.DecodeLpc33(_Decoded33, residual, _Coefficients, predictorOrder, quantizationLevel, _BlockSize);
			return 0;
		}

		/// <summary>
		/// Ports FLAC partitioned Rice residual decoding and preserves the escape and signed mapping behavior.
		/// </summary>
		private int DecodeResiduals(int[] decoded, int predictorOrder)
		{
			var bitReader = _Reader.OpenLocal();
			// The method has one result exit so the local reader is closed after success and every decode error.
			var methodType = (int)bitReader.ReadBits(2);
			var riceOrder = (int)bitReader.ReadBits(4);
			var samples = _BlockSize >> riceOrder;
			var riceBits = 4 + methodType;
			var riceEscape = (1 << riceBits) - 1;
			var outputIndex = predictorOrder;
			var index = predictorOrder;
			var result = 0;
			if (methodType > 1 || samples << riceOrder != _BlockSize || predictorOrder > samples)
			{
				result = FfmpegError.InvalidData;
			} else
			{
				for (var partition = 0; partition < 1 << riceOrder; partition++)
				{
					var parameter = (int)bitReader.ReadBits(riceBits);
					if (parameter == riceEscape)
					{
						var rawBits = (int)bitReader.ReadBits(5);
						for (; index < samples; index++)
							decoded[outputIndex++] = bitReader.ReadSignedBits(rawBits);
					} else
					{
						var realLimit = parameter > 1 ? (int.MaxValue >> (parameter - 1)) + 2 : int.MaxValue;
						for (; index < samples; index++)
						{
							var value = GolombReader.ReadSignedFlac(ref bitReader, parameter, realLimit, 1);
							if (value == GolombReader.InvalidVlc)
							{
								result = FfmpegError.InvalidData;
								break;
							}
							decoded[outputIndex++] = value;
						}
					}
					if (result < 0)
						break;
					index = 0;
				}
			}
			bitReader.Close();
			return result;
		}

		/// <summary>
		/// Applies FLAC channel assignment arithmetic and source sample shifts directly into the packed output buffer.
		/// </summary>
		private void WriteDecorrelated(Span<byte> output, int bitsPerSample, int bytesPerSample)
		{
			if (bitsPerSample == 32 && _ChannelMode > 0)
				Decorrelate33();
			var shift = bytesPerSample * 8 - bitsPerSample;
			if (BitConverter.IsLittleEndian)
			{
				if (bytesPerSample == 2)
					WriteDecorrelated16(output, shift);
				else
					WriteDecorrelated32(output, shift);
				return;
			}

			var outputOffset = 0;
			for (var sample = 0; sample < _BlockSize; sample++)
			{
				for (var channel = 0; channel < _StreamInfo.Channels; channel++)
				{
					uint value;
					if (_ChannelMode == 1)
						value = channel == 0 ? (uint)_Decoded[0][sample] : unchecked((uint)_Decoded[0][sample] - (uint)_Decoded[1][sample]);
					else if (_ChannelMode == 2)
						value = channel == 0 ? unchecked((uint)_Decoded[0][sample] + (uint)_Decoded[1][sample]) : (uint)_Decoded[1][sample];
					else if (_ChannelMode == 3)
					{
						var middle = (uint)_Decoded[0][sample];
						var side = _Decoded[1][sample];
						middle = unchecked(middle - (uint)(side >> 1));
						value = channel == 0 ? unchecked(middle + (uint)side) : middle;
					} else
					{
						value = (uint)_Decoded[channel][sample];
					}

					value = unchecked(value << shift);
					if (bytesPerSample == 2)
						BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(outputOffset, 2), (ushort)value);
					else
						BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(outputOffset, 4), value);
					outputOffset += bytesPerSample;
				}
			}
		}

		private void WriteDecorrelated16(Span<byte> output, int shift)
		{
			var destination = MemoryMarshal.Cast<byte, ushort>(output);
			var outputOffset = 0;
			for (var sample = 0; sample < _BlockSize; sample++)
			{
				for (var channel = 0; channel < _StreamInfo.Channels; channel++)
				{
					uint value;
					if (_ChannelMode == 1)
						value = channel == 0 ? (uint)_Decoded[0][sample] : unchecked((uint)_Decoded[0][sample] - (uint)_Decoded[1][sample]);
					else if (_ChannelMode == 2)
						value = channel == 0 ? unchecked((uint)_Decoded[0][sample] + (uint)_Decoded[1][sample]) : (uint)_Decoded[1][sample];
					else if (_ChannelMode == 3)
					{
						var middle = (uint)_Decoded[0][sample];
						var side = _Decoded[1][sample];
						middle = unchecked(middle - (uint)(side >> 1));
						value = channel == 0 ? unchecked(middle + (uint)side) : middle;
					} else
					{
						value = (uint)_Decoded[channel][sample];
					}
					destination[outputOffset++] = (ushort)unchecked(value << shift);
				}
			}
		}

		private void WriteDecorrelated32(Span<byte> output, int shift)
		{
			var destination = MemoryMarshal.Cast<byte, uint>(output);
			var outputOffset = 0;
			for (var sample = 0; sample < _BlockSize; sample++)
			{
				for (var channel = 0; channel < _StreamInfo.Channels; channel++)
				{
					uint value;
					if (_ChannelMode == 1)
						value = channel == 0 ? (uint)_Decoded[0][sample] : unchecked((uint)_Decoded[0][sample] - (uint)_Decoded[1][sample]);
					else if (_ChannelMode == 2)
						value = channel == 0 ? unchecked((uint)_Decoded[0][sample] + (uint)_Decoded[1][sample]) : (uint)_Decoded[1][sample];
					else if (_ChannelMode == 3)
					{
						var middle = (uint)_Decoded[0][sample];
						var side = _Decoded[1][sample];
						middle = unchecked(middle - (uint)(side >> 1));
						value = channel == 0 ? unchecked(middle + (uint)side) : middle;
					} else
					{
						value = (uint)_Decoded[channel][sample];
					}
					destination[outputOffset++] = unchecked(value << shift);
				}
			}
		}

		private void Decorrelate33()
		{
			if (_ChannelMode == 1)
			{
				for (var index = 0; index < _BlockSize; index++)
					_Decoded[1][index] = unchecked((int)((uint)_Decoded[0][index] - (ulong)_Decoded33[index]));
			} else if (_ChannelMode == 2)
			{
				for (var index = 0; index < _BlockSize; index++)
					_Decoded[0][index] = unchecked((int)((uint)_Decoded[1][index] + (ulong)_Decoded33[index]));
			} else if (_ChannelMode == 3)
			{
				for (var index = 0; index < _BlockSize; index++)
				{
					ulong middle = unchecked((uint)_Decoded[0][index]);
					var side = _Decoded33[index];
					middle = unchecked(middle - (ulong)(side >> 1));
					_Decoded[0][index] = unchecked((int)(middle + (ulong)side));
					_Decoded[1][index] = unchecked((int)middle);
				}
			}
			_ChannelMode = 0;
		}
	}
}
