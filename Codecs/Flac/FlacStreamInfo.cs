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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Flac
{
	/// <summary>
	/// Mirrors FLACStreaminfo and parses the mandatory 34-byte STREAMINFO metadata block.
	/// </summary>
	public sealed class FlacStreamInfo
	{
		public int MinimumBlockSize { get; private set; }
		public int MaximumBlockSize { get; private set; }
		public int MinimumFrameSize { get; private set; }
		public int MaximumFrameSize { get; private set; }
		public int SampleRate { get; private set; }
		public int Channels { get; private set; }
		public int BitsPerSample { get; private set; }
		public long TotalSamples { get; private set; }

		public int Parse(ReadOnlySpan<byte> data)
		{
			if (data.Length < 34)
				return FfmpegError.InvalidData;

			MinimumBlockSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0, 2));
			MaximumBlockSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
			MinimumFrameSize = ReadUInt24BigEndian(data, 4);
			MaximumFrameSize = ReadUInt24BigEndian(data, 7);
			var packed = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(10, 8));
			SampleRate = (int)(packed >> 44);
			Channels = (int)(packed >> 41 & 7) + 1;
			BitsPerSample = (int)(packed >> 36 & 31) + 1;
			TotalSamples = (long)(packed & 0x0000000fffffffffUL);
			if (MaximumBlockSize < 16 || BitsPerSample < 4)
				return FfmpegError.InvalidData;
			return 0;
		}

		private static int ReadUInt24BigEndian(ReadOnlySpan<byte> data, int offset)
		{
			return data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2];
		}
	}
}
