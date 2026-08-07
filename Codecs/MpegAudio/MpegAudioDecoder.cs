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

namespace Ffmpeg.CsPort.Decoder.Codecs.MpegAudio
{
	/// <summary>
	/// Ports FFmpeg's scalar floating-point MPEG Audio Layer I, II, and III decoder with planar float output.
	/// </summary>
	public sealed partial class MpegAudioDecoder
	{
		private const int SubbandLimit = 32;
		private const int MaximumFrameSamples = 1152;
		private readonly BitReader _Reader = new BitReader();
		private readonly float[][] _SynthBuffers = { new float[1024], new float[1024] };
		private readonly int[] _SynthOffsets = new int[2];
		private readonly float[][] _SubbandSamples = { new float[36 * SubbandLimit], new float[36 * SubbandLimit] };
		private readonly float[][] _MdctBuffers = { new float[SubbandLimit * 18], new float[SubbandLimit * 18] };
		private readonly float[][] _FrameSamples = { new float[MaximumFrameSamples], new float[MaximumFrameSamples] };
		private readonly MpegAudioGranule[,] _Granules =
		{
			{ new MpegAudioGranule(), new MpegAudioGranule() },
			{ new MpegAudioGranule(), new MpegAudioGranule() }
		};
		private readonly byte[] _Allocation = new byte[2 * SubbandLimit];
		private readonly byte[] _ScaleCodes = new byte[2 * SubbandLimit];
		private readonly byte[] _ScaleFactors = new byte[2 * SubbandLimit * 3];
		private readonly byte[] _Reservoir = new byte[512];
		private readonly byte[] _MainData = new byte[512 + 8192];
		private int _ReservoirLength;

		public int SampleRate { get; private set; }
		public int Channels { get; private set; }
		public int Layer { get; private set; }
		public int BitRate { get; private set; }

		/// <summary>
		/// Decodes one complete MPEG audio frame and preserves Layer III synthesis/reservoir state across calls.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < 0 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			var skipped = 0;
			while (packetLength != 0 && packet[packetOffset] == 0)
			{
				packetOffset++; packetLength--; skipped++;
			}
			if (packetLength < 4)
				return FfmpegError.InvalidData;
			if (packet[packetOffset] == (byte)'T' && packet[packetOffset + 1] == (byte)'A' && packet[packetOffset + 2] == (byte)'G')
				return packetLength + skipped;

			var header = new MpegAudioHeader();
			var headerResult = header.Decode(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(packetOffset, 4)));
			if (headerResult != 0 || header.CodedFrameSize <= 0)
				return FfmpegError.InvalidData;
			var codedFrameSize = Math.Min(header.CodedFrameSize, packetLength);
			if (_Reader.Initialize(packet, packetOffset + 4, (codedFrameSize - 4) * 8) < 0)
				return FfmpegError.InvalidData;
			if (header.ErrorProtection)
				_Reader.ReadBits(16);

			SampleRate = header.SampleRate;
			Channels = header.Channels;
			Layer = header.Layer;
			BitRate = header.BitRate;
			int synthesisBlocks;
			switch (header.Layer)
			{
				case 1:
					synthesisBlocks = DecodeLayer1(header);
					break;
				case 2:
					synthesisBlocks = DecodeLayer2(header);
					break;
				default:
					synthesisBlocks = DecodeLayer3(header, packet, packetOffset, codedFrameSize);
					break;
			}
			if (synthesisBlocks < 0)
				return synthesisBlocks;

			var numberOfSamples = synthesisBlocks * 32;
			var planeSize = numberOfSamples * sizeof(float);
			var dataSize = planeSize * header.Channels;
			if (output.Length < dataSize)
				return FfmpegError.InvalidArgument;
			for (var channel = 0; channel < header.Channels; channel++)
			{
				for (var block = 0; block < synthesisBlocks; block++)
					MpegAudioDsp.Synthesize(_SynthBuffers[channel], ref _SynthOffsets[channel], _SubbandSamples[channel], block * SubbandLimit, _FrameSamples[channel], block * 32);
				for (var sample = 0; sample < numberOfSamples; sample++)
				{
					var bits = BitConverter.SingleToInt32Bits(_FrameSamples[channel][sample]);
					BinaryPrimitives.WriteInt32LittleEndian(output.Slice(channel * planeSize + sample * 4, 4), bits);
				}
			}
			frame = new AudioFrameInfo(numberOfSamples, header.Channels, AudioSampleFormat.FloatPlanar, header.Channels, planeSize, dataSize);
			return codedFrameSize + skipped;
		}

		public void Flush()
		{
			Array.Clear(_SynthBuffers[0]); Array.Clear(_SynthBuffers[1]);
			Array.Clear(_MdctBuffers[0]); Array.Clear(_MdctBuffers[1]);
			_SynthOffsets[0] = 0; _SynthOffsets[1] = 0; _ReservoirLength = 0;
		}

		/// <summary>
		/// Decodes MPEG Layer I allocation, scale factors, and all twelve subband sample groups in bitstream order.
		/// </summary>
		private int DecodeLayer1(MpegAudioHeader header)
		{
			var bound = header.Mode == 1 ? (header.ModeExtension + 1) * 4 : SubbandLimit;
			for (var subband = 0; subband < bound; subband++)
				for (var channel = 0; channel < header.Channels; channel++)
					_Allocation[channel * SubbandLimit + subband] = (byte)_Reader.ReadBits(4);
			for (var subband = bound; subband < SubbandLimit; subband++)
				_Allocation[subband] = (byte)_Reader.ReadBits(4);

			for (var subband = 0; subband < bound; subband++)
				for (var channel = 0; channel < header.Channels; channel++)
					if (_Allocation[channel * SubbandLimit + subband] != 0)
						_ScaleFactors[(channel * SubbandLimit + subband) * 3] = (byte)_Reader.ReadBits(6);
			for (var subband = bound; subband < SubbandLimit; subband++)
			{
				if (_Allocation[subband] == 0) continue;
				_ScaleFactors[subband * 3] = (byte)_Reader.ReadBits(6);
				_ScaleFactors[(SubbandLimit + subband) * 3] = (byte)_Reader.ReadBits(6);
			}

			for (var block = 0; block < 12; block++)
			{
				for (var subband = 0; subband < bound; subband++)
				{
					for (var channel = 0; channel < header.Channels; channel++)
					{
						var allocation = _Allocation[channel * SubbandLimit + subband];
						_SubbandSamples[channel][block * SubbandLimit + subband] = allocation != 0
							? UnscaleLayer1(allocation, (int)_Reader.ReadBits(allocation + 1), _ScaleFactors[(channel * SubbandLimit + subband) * 3]) : 0;
					}
				}
				for (var subband = bound; subband < SubbandLimit; subband++)
				{
					var allocation = _Allocation[subband];
					if (allocation != 0)
					{
						var mantissa = (int)_Reader.ReadBits(allocation + 1);
						_SubbandSamples[0][block * SubbandLimit + subband] = UnscaleLayer1(allocation, mantissa, _ScaleFactors[subband * 3]);
						_SubbandSamples[1][block * SubbandLimit + subband] = UnscaleLayer1(allocation, mantissa, _ScaleFactors[(SubbandLimit + subband) * 3]);
					} else
					{
						_SubbandSamples[0][block * SubbandLimit + subband] = 0; _SubbandSamples[1][block * SubbandLimit + subband] = 0;
					}
				}
			}
			return 12;
		}

		/// <summary>
		/// Decodes Layer II allocation/scfsi and all 36 subband groups without allocations in the frame loop.
		/// </summary>
		private int DecodeLayer2(MpegAudioHeader header)
		{
			var table = SelectLayer2Table(header.BitRate / 1000, header.Channels, header.SampleRate, header.LowSamplingFrequency);
			var subbandLimit = MpegAudioTables.SubbandLimits[table];
			var allocationTable = MpegAudioTables.GetAllocationTable(table);
			var bound = header.Mode == 1 ? (header.ModeExtension + 1) * 4 : subbandLimit;
			if (bound > subbandLimit) bound = subbandLimit;
			var allocationOffset = 0;
			for (var subband = 0; subband < bound; subband++)
			{
				var bits = allocationTable[allocationOffset];
				for (var channel = 0; channel < header.Channels; channel++)
					_Allocation[channel * SubbandLimit + subband] = (byte)_Reader.ReadBits(bits);
				allocationOffset += 1 << bits;
			}
			for (var subband = bound; subband < subbandLimit; subband++)
			{
				var bits = allocationTable[allocationOffset]; var value = (byte)_Reader.ReadBits(bits);
				_Allocation[subband] = value; _Allocation[SubbandLimit + subband] = value; allocationOffset += 1 << bits;
			}
			for (var subband = 0; subband < subbandLimit; subband++)
				for (var channel = 0; channel < header.Channels; channel++)
					if (_Allocation[channel * SubbandLimit + subband] != 0)
						_ScaleCodes[channel * SubbandLimit + subband] = (byte)_Reader.ReadBits(2);

			for (var subband = 0; subband < subbandLimit; subband++)
			{
				for (var channel = 0; channel < header.Channels; channel++)
				{
					if (_Allocation[channel * SubbandLimit + subband] == 0) continue;
					var factorOffset = (channel * SubbandLimit + subband) * 3;
					switch (_ScaleCodes[channel * SubbandLimit + subband])
					{
						case 2:
							_ScaleFactors[factorOffset] = (byte)_Reader.ReadBits(6);
							_ScaleFactors[factorOffset + 1] = _ScaleFactors[factorOffset]; _ScaleFactors[factorOffset + 2] = _ScaleFactors[factorOffset];
							break;
						case 1:
							_ScaleFactors[factorOffset] = (byte)_Reader.ReadBits(6); _ScaleFactors[factorOffset + 2] = (byte)_Reader.ReadBits(6);
							_ScaleFactors[factorOffset + 1] = _ScaleFactors[factorOffset];
							break;
						case 3:
							_ScaleFactors[factorOffset] = (byte)_Reader.ReadBits(6); _ScaleFactors[factorOffset + 2] = (byte)_Reader.ReadBits(6);
							_ScaleFactors[factorOffset + 1] = _ScaleFactors[factorOffset + 2];
							break;
						default:
							_ScaleFactors[factorOffset] = (byte)_Reader.ReadBits(6); _ScaleFactors[factorOffset + 1] = (byte)_Reader.ReadBits(6); _ScaleFactors[factorOffset + 2] = (byte)_Reader.ReadBits(6);
							break;
					}
				}
			}

			for (var scaleGroup = 0; scaleGroup < 3; scaleGroup++)
			{
				for (var block = 0; block < 12; block += 3)
				{
					allocationOffset = 0;
					for (var subband = 0; subband < bound; subband++)
					{
						var allocationBits = allocationTable[allocationOffset];
						for (var channel = 0; channel < header.Channels; channel++)
							DecodeLayer2Subband(channel, subband, scaleGroup, block, _Allocation[channel * SubbandLimit + subband], allocationTable, allocationOffset, false);
						allocationOffset += 1 << allocationBits;
					}
					for (var subband = bound; subband < subbandLimit; subband++)
					{
						var allocationBits = allocationTable[allocationOffset];
						DecodeLayer2JointSubband(subband, scaleGroup, block, _Allocation[subband], allocationTable, allocationOffset);
						allocationOffset += 1 << allocationBits;
					}
					for (var subband = subbandLimit; subband < SubbandLimit; subband++)
						for (var channel = 0; channel < header.Channels; channel++)
							for (var item = 0; item < 3; item++) _SubbandSamples[channel][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = 0;
				}
			}
			return 36;
		}

		private void DecodeLayer2Subband(int channel, int subband, int scaleGroup, int block, int allocation, byte[] allocationTable, int allocationOffset, bool joint)
		{
			if (allocation == 0)
			{
				for (var item = 0; item < 3; item++) _SubbandSamples[channel][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = 0;
				return;
			}
			var scale = _ScaleFactors[(channel * SubbandLimit + subband) * 3 + scaleGroup];
			var quantizer = allocationTable[allocationOffset + allocation]; var bits = MpegAudioTables.QuantizationBits[quantizer];
			if (bits < 0)
			{
				var value = (int)_Reader.ReadBits(-bits); var packed = MpegAudioTables.DivisionTables[quantizer][value]; var steps = MpegAudioTables.QuantizationSteps[quantizer];
				_SubbandSamples[channel][(scaleGroup * 12 + block) * SubbandLimit + subband] = UnscaleLayer2Group(steps, packed & 15, scale);
				_SubbandSamples[channel][(scaleGroup * 12 + block + 1) * SubbandLimit + subband] = UnscaleLayer2Group(steps, packed >> 4 & 15, scale);
				_SubbandSamples[channel][(scaleGroup * 12 + block + 2) * SubbandLimit + subband] = UnscaleLayer2Group(steps, packed >> 8, scale);
			} else
			{
				for (var item = 0; item < 3; item++)
					_SubbandSamples[channel][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = UnscaleLayer1(bits - 1, (int)_Reader.ReadBits(bits), scale);
			}
		}

		private void DecodeLayer2JointSubband(int subband, int scaleGroup, int block, int allocation, byte[] allocationTable, int allocationOffset)
		{
			if (allocation == 0)
			{
				for (var channel = 0; channel < 2; channel++) for (var item = 0; item < 3; item++) _SubbandSamples[channel][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = 0;
				return;
			}
			var scale0 = _ScaleFactors[subband * 3 + scaleGroup]; var scale1 = _ScaleFactors[(SubbandLimit + subband) * 3 + scaleGroup];
			var quantizer = allocationTable[allocationOffset + allocation]; var bits = MpegAudioTables.QuantizationBits[quantizer];
			if (bits < 0)
			{
				var value = (int)_Reader.ReadBits(-bits); var steps = MpegAudioTables.QuantizationSteps[quantizer];
				for (var item = 0; item < 3; item++)
				{
					var mantissa = value % steps; value /= steps;
					_SubbandSamples[0][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = UnscaleLayer2Group(steps, mantissa, scale0);
					_SubbandSamples[1][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = UnscaleLayer2Group(steps, mantissa, scale1);
				}
			} else
			{
				for (var item = 0; item < 3; item++)
				{
					var mantissa = (int)_Reader.ReadBits(bits);
					_SubbandSamples[0][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = UnscaleLayer1(bits - 1, mantissa, scale0);
					_SubbandSamples[1][(scaleGroup * 12 + block + item) * SubbandLimit + subband] = UnscaleLayer1(bits - 1, mantissa, scale1);
				}
			}
		}

		private static int UnscaleLayer1(int bits, int mantissa, int scaleFactor)
		{
			var shift = (int)MpegAudioTables.ScaleFactorModShift[scaleFactor]; var modifier = shift & 3; shift >>= 2;
			var signedMantissa = unchecked((int)((uint)mantissa + (uint.MaxValue << bits) + 1));
			var value = (long)signedMantissa * MpegAudioTables.ScaleFactorMultipliers[(bits - 1) * 3 + modifier]; shift += bits;
			return (int)((value + (1L << (shift - 1))) >> shift);
		}

		private static int UnscaleLayer2Group(int steps, int mantissa, int scaleFactor)
		{
			var shift = (int)MpegAudioTables.ScaleFactorModShift[scaleFactor]; var modifier = shift & 3; shift >>= 2;
			var value = (mantissa - (steps >> 1)) * MpegAudioTables.ScaleFactorMultipliers2[(steps >> 2) * 3 + modifier];
			if (shift > 0) value = (value + (1 << (shift - 1))) >> shift;
			return value;
		}

		private static int SelectLayer2Table(int bitRate, int channels, int frequency, int lowSamplingFrequency)
		{
			var channelBitRate = bitRate / channels;
			if (lowSamplingFrequency == 0)
			{
				if ((frequency == 48000 && channelBitRate >= 56) || channelBitRate >= 56 && channelBitRate <= 80) return 0;
				if (frequency != 48000 && channelBitRate >= 96) return 1;
				if (frequency != 32000 && channelBitRate <= 48) return 2;
				return 3;
			}
			return 4;
		}
	}
}
