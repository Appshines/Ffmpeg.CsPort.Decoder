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
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Flac
{
	/// <summary>
	/// Ports ff_flac_decode_frame_header, including UTF-8 frame numbers and source CRC-8 validation.
	/// </summary>
	internal static class FlacFrameHeaderParser
	{
		private static readonly int[] SampleSizeTable = { 0, 8, 12, 0, 16, 20, 24, 32 };
		private static readonly int[] SampleRateTable =
		{
			0, 88200, 176400, 192000, 8000, 16000, 22050, 24000,
			32000, 44100, 48000, 96000, 0, 0, 0, 0
		};
		private static readonly int[] BlockSizeTable =
		{
			0, 192, 576, 1152, 2304, 4608, 0, 0,
			256, 512, 1024, 2048, 4096, 8192, 16384, 32768
		};

		/// <summary>
		/// Consumes the complete frame header through its CRC byte and returns source-compatible validation errors.
		/// </summary>
		public static int Parse(byte[] buffer, int offset, int length, BitReader reader, out FlacFrameHeader header)
		{
			header = default;
			if (length < 6 || reader.Initialize(buffer, offset, length * 8) < 0)
				return FfmpegError.InvalidData;
			if ((reader.ReadBits(15) & 0x7fff) != 0x7ffc)
				return FfmpegError.InvalidData;

			var variableBlockSize = reader.ReadBit() != 0;
			var blockSizeCode = (int)reader.ReadBits(4);
			var sampleRateCode = (int)reader.ReadBits(4);
			var encodedChannelMode = (int)reader.ReadBits(4);
			int channels;
			int channelMode;
			if (encodedChannelMode < 8)
			{
				channels = encodedChannelMode + 1;
				channelMode = 0;
			} else if (encodedChannelMode < 11)
			{
				channels = 2;
				channelMode = encodedChannelMode - 7;
			} else
			{
				return FfmpegError.InvalidData;
			}

			var bitsPerSampleCode = (int)reader.ReadBits(3);
			if (bitsPerSampleCode == 3 || reader.ReadBit() != 0)
				return FfmpegError.InvalidData;
			var bitsPerSample = SampleSizeTable[bitsPerSampleCode];
			var numberResult = ReadUtf8Number(reader, out var frameOrSampleNumber);
			if (numberResult < 0)
				return numberResult;

			int blockSize;
			if (blockSizeCode == 0)
				return FfmpegError.InvalidData;
			if (blockSizeCode == 6)
				blockSize = (int)reader.ReadBits(8) + 1;
			else if (blockSizeCode == 7)
				blockSize = (int)reader.ReadBits(16) + 1;
			else
				blockSize = BlockSizeTable[blockSizeCode];

			int sampleRate;
			if (sampleRateCode < 12)
				sampleRate = SampleRateTable[sampleRateCode];
			else if (sampleRateCode == 12)
				sampleRate = (int)reader.ReadBits(8) * 1000;
			else if (sampleRateCode == 13)
				sampleRate = (int)reader.ReadBits(16);
			else if (sampleRateCode == 14)
				sampleRate = (int)reader.ReadBits(16) * 10;
			else
				return FfmpegError.InvalidData;

			reader.SkipBits(8);
			var headerSize = reader.Position / 8;
			if (FlacCrc.Compute8(buffer.AsSpan(offset, headerSize)) != 0)
				return FfmpegError.InvalidData;

			header = new FlacFrameHeader(
				blockSize,
				sampleRate,
				channels,
				channelMode,
				bitsPerSample,
				frameOrSampleNumber,
				variableBlockSize);
			return 0;
		}

		private static int ReadUtf8Number(BitReader reader, out long value)
		{
			value = 0;
			var first = (byte)reader.ReadBits(8);
			if ((first & 0x80) == 0)
			{
				value = first;
				return 0;
			}

			var leadingOnes = 0;
			for (var mask = 0x80; (first & mask) != 0; mask >>= 1)
				leadingOnes++;
			if (leadingOnes < 2 || leadingOnes > 7)
				return FfmpegError.InvalidData;

			value = first & (0x7f >> leadingOnes);
			for (var index = 1; index < leadingOnes; index++)
			{
				var next = (byte)reader.ReadBits(8);
				if ((next & 0xc0) != 0x80)
					return FfmpegError.InvalidData;
				value = value << 6 | (uint)(next & 0x3f);
			}
			return 0;
		}
	}
}
