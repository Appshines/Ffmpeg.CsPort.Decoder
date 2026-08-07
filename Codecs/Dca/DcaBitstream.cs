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
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Dca
{
	/// <summary>
	/// Ports FFmpeg's DTS sync-word conversion and core-frame header parser.
	/// </summary>
	internal static class DcaBitstream
	{
		public const uint CoreBigEndianSyncWord = 0x7ffe8001;
		public const uint CoreLittleEndianSyncWord = 0xfe7f0180;
		public const uint Core14BigEndianSyncWord = 0x1fffe800;
		public const uint Core14LittleEndianSyncWord = 0xff1f00e8;
		public const uint ExtensionSubstreamSyncWord = 0x64582025;
		public const int CoreFrameHeaderSize = 18;
		private static readonly int[] s_SampleRates = { 0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 96000, 192000 };
		private static readonly int[] s_BitsPerSample = { 16, 16, 20, 20, 0, 24, 24, 0 };
		private static readonly int[] s_Channels = { 1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 6, 6, 7, 8, 8 };

		public static bool IsSyncWord(uint marker)
		{
			return marker == CoreBigEndianSyncWord || marker == CoreLittleEndianSyncWord ||
				marker == Core14BigEndianSyncWord || marker == Core14LittleEndianSyncWord ||
				marker == ExtensionSubstreamSyncWord;
		}

		/// <summary>
		/// Normalizes all four DTS core packing variants to 16-bit big-endian bytes in source word order.
		/// </summary>
		public static int ConvertBitstream(byte[] source, int sourceOffset, int sourceSize, byte[] destination, int maximumSize)
		{
			if (source == null || destination == null || sourceOffset < 0 || sourceSize < 4 ||
				sourceOffset > source.Length - sourceSize || maximumSize < 0 || maximumSize > destination.Length)
				return FfmpegError.InvalidArgument;
			if (sourceSize > maximumSize) sourceSize = maximumSize;
			var marker = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(sourceOffset, 4));
			if (marker == CoreBigEndianSyncWord || marker == ExtensionSubstreamSyncWord)
			{
				source.AsSpan(sourceOffset, sourceSize).CopyTo(destination);
				return sourceSize;
			}
			if (marker == CoreLittleEndianSyncWord)
			{
				for (var index = 0; index < (sourceSize + 1) >> 1; index++)
				{
					var position = index << 1;
					if (position + 1 < sourceSize)
					{
						destination[position] = source[sourceOffset + position + 1];
						destination[position + 1] = source[sourceOffset + position];
					} else destination[position] = source[sourceOffset + position];
				}
				return sourceSize;
			}
			if (marker != Core14BigEndianSyncWord && marker != Core14LittleEndianSyncWord) return FfmpegError.InvalidData;

			Array.Clear(destination, 0, maximumSize);
			var outputBitPosition = 0;
			for (var index = 0; index < (sourceSize + 1) >> 1; index++)
			{
				var position = sourceOffset + (index << 1);
				var value = marker == Core14BigEndianSyncWord
					? source[position] << 8 | (position + 1 < sourceOffset + sourceSize ? source[position + 1] : 0)
					: (position + 1 < sourceOffset + sourceSize ? source[position + 1] : 0) << 8 | source[position];
				value &= 0x3fff;
				for (var bit = 13; bit >= 0; bit--)
				{
					var bytePosition = outputBitPosition >> 3;
					if (bytePosition >= maximumSize) return bytePosition;
					destination[bytePosition] |= (byte)(((value >> bit) & 1) << (7 - (outputBitPosition & 7)));
					outputBitPosition++;
				}
			}
			return (outputBitPosition + 7) >> 3;
		}

		/// <summary>
		/// Reads the normalized 18-byte DTS core header in the same field order and with the same validation as FFmpeg.
		/// </summary>
		public static int ParseCoreFrameHeader(byte[] data, int offset, int size, out DcaCoreFrameHeader header)
		{
			header = default;
			if (data == null || offset < 0 || size < CoreFrameHeaderSize || offset > data.Length - size) return FfmpegError.InvalidData;
			var bits = new BitReader();
			if (bits.Initialize(data, offset, size * 8) < 0 || bits.ReadBitsLong(32) != CoreBigEndianSyncWord) return FfmpegError.InvalidData;
			header.NormalFrame = (int)bits.ReadBit();
			header.DeficitSamples = (int)bits.ReadBits(5) + 1;
			if (header.DeficitSamples != 32) return FfmpegError.InvalidData;
			header.CrcPresent = (int)bits.ReadBit();
			header.PcmBlocks = (int)bits.ReadBits(7) + 1;
			if ((header.PcmBlocks & 7) != 0) return FfmpegError.InvalidData;
			header.FrameSize = (int)bits.ReadBits(14) + 1;
			if (header.FrameSize < 96) return FfmpegError.InvalidData;
			header.AudioMode = (int)bits.ReadBits(6);
			if (header.AudioMode >= 10) return FfmpegError.InvalidData;
			header.SampleRateCode = (int)bits.ReadBits(4);
			header.SampleRate = s_SampleRates[header.SampleRateCode];
			if (header.SampleRate == 0) return FfmpegError.InvalidData;
			header.BitRateCode = (int)bits.ReadBits(5);
			if (bits.ReadBit() != 0) return FfmpegError.InvalidData;
			header.DynamicRangePresent = (int)bits.ReadBit();
			header.TimestampPresent = (int)bits.ReadBit();
			header.AuxiliaryPresent = (int)bits.ReadBit();
			header.HdcdMaster = (int)bits.ReadBit();
			header.ExtensionAudioType = (int)bits.ReadBits(3);
			header.ExtensionAudioPresent = (int)bits.ReadBit();
			header.SyncSubSubframes = (int)bits.ReadBit();
			header.LowFrequencyEffects = (int)bits.ReadBits(2);
			if (header.LowFrequencyEffects == 3) return FfmpegError.InvalidData;
			header.PredictorHistory = (int)bits.ReadBit();
			if (header.CrcPresent != 0) bits.SkipBits(16);
			header.FilterPerfect = (int)bits.ReadBit();
			header.EncoderRevision = (int)bits.ReadBits(4);
			header.CopyHistory = (int)bits.ReadBits(2);
			header.PcmResolutionCode = (int)bits.ReadBits(3);
			if (s_BitsPerSample[header.PcmResolutionCode] == 0) return FfmpegError.InvalidData;
			header.BitsPerSample = s_BitsPerSample[header.PcmResolutionCode];
			header.SumDifferenceFront = (int)bits.ReadBit();
			header.SumDifferenceSurround = (int)bits.ReadBit();
			header.DialogNormalizationCode = (int)bits.ReadBits(4);
			header.Channels = s_Channels[header.AudioMode] + (header.LowFrequencyEffects != 0 ? 1 : 0);
			return 0;
		}
	}

	internal struct DcaCoreFrameHeader
	{
		public int NormalFrame, DeficitSamples, CrcPresent, PcmBlocks, FrameSize, AudioMode, SampleRateCode, SampleRate;
		public int BitRateCode, DynamicRangePresent, TimestampPresent, AuxiliaryPresent, HdcdMaster;
		public int ExtensionAudioType, ExtensionAudioPresent, SyncSubSubframes, LowFrequencyEffects, PredictorHistory;
		public int FilterPerfect, EncoderRevision, CopyHistory, PcmResolutionCode, BitsPerSample;
		public int SumDifferenceFront, SumDifferenceSurround, DialogNormalizationCode, Channels;
	}
}
