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
using System.IO;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Provides the bounded integer and seek operations used by FFmpeg's AVIO-based demuxer code.
	/// </summary>
	internal sealed class FormatReader
	{
		private readonly Stream _Stream;
		private readonly byte[] _IntegerBuffer = new byte[8];

		public FormatReader(Stream stream)
		{
			_Stream = stream;
		}

		public long Position => _Stream.Position;
		public long Length => _Stream.Length;
		public bool CanSeek => _Stream.CanSeek;

		public bool ReadByte(out byte value)
		{
			var result = _Stream.ReadByte();
			value = unchecked((byte)result);
			return result >= 0;
		}

		public bool ReadUInt16LittleEndian(out ushort value)
		{
			if (!ReadExactly(_IntegerBuffer.AsSpan(0, 2)))
			{
				value = 0;
				return false;
			}
			value = BinaryPrimitives.ReadUInt16LittleEndian(_IntegerBuffer.AsSpan(0, 2));
			return true;
		}

		public bool ReadUInt16BigEndian(out ushort value)
		{
			if (!ReadExactly(_IntegerBuffer.AsSpan(0, 2)))
			{
				value = 0;
				return false;
			}
			value = BinaryPrimitives.ReadUInt16BigEndian(_IntegerBuffer.AsSpan(0, 2));
			return true;
		}

		public bool ReadUInt32LittleEndian(out uint value)
		{
			if (!ReadExactly(_IntegerBuffer.AsSpan(0, 4)))
			{
				value = 0;
				return false;
			}
			value = BinaryPrimitives.ReadUInt32LittleEndian(_IntegerBuffer.AsSpan(0, 4));
			return true;
		}

		public bool ReadUInt32BigEndian(out uint value)
		{
			if (!ReadExactly(_IntegerBuffer.AsSpan(0, 4)))
			{
				value = 0;
				return false;
			}
			value = BinaryPrimitives.ReadUInt32BigEndian(_IntegerBuffer.AsSpan(0, 4));
			return true;
		}

		public bool ReadUInt64LittleEndian(out ulong value)
		{
			if (!ReadExactly(_IntegerBuffer.AsSpan(0, 8)))
			{
				value = 0;
				return false;
			}
			value = BinaryPrimitives.ReadUInt64LittleEndian(_IntegerBuffer);
			return true;
		}

		public bool ReadUInt64BigEndian(out ulong value)
		{
			if (!ReadExactly(_IntegerBuffer.AsSpan(0, 8)))
			{
				value = 0;
				return false;
			}
			value = BinaryPrimitives.ReadUInt64BigEndian(_IntegerBuffer);
			return true;
		}

		public bool ReadExactly(Span<byte> destination)
		{
			var offset = 0;
			while (offset < destination.Length)
			{
				var read = _Stream.Read(destination.Slice(offset));
				if (read <= 0)
					return false;
				offset += read;
			}
			return true;
		}

		public int Read(Span<byte> destination)
		{
			var offset = 0;
			while (offset < destination.Length)
			{
				var read = _Stream.Read(destination.Slice(offset));
				if (read <= 0)
					break;
				offset += read;
			}
			return offset;
		}

		public bool Seek(long position)
		{
			if (!_Stream.CanSeek || position < 0)
				return false;
			return _Stream.Seek(position, SeekOrigin.Begin) == position;
		}

		public bool Skip(long count)
		{
			if (count < 0 || Position > long.MaxValue - count)
				return false;
			return Seek(Position + count);
		}
	}
}
