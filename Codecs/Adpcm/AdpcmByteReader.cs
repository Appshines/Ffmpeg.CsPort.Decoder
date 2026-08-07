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

namespace Ffmpeg.CsPort.Decoder.Codecs.Adpcm
{
	/// <summary>
	/// Implements the bounded byte-access operations used by FFmpeg's ADPCM GetByteContext paths.
	/// </summary>
	internal sealed class AdpcmByteReader
	{
		private byte[] _Buffer;
		private int _Start;
		private int _End;
		private int _Position;

		public int Position => _Position - _Start;
		public int BytesLeft => _End - _Position;

		public void Initialize(byte[] buffer, int offset, int length)
		{
			_Buffer = buffer;
			_Start = offset;
			_End = offset + length;
			_Position = offset;
		}

		public int ReadByte()
		{
			return _Position < _End ? _Buffer[_Position++] : 0;
		}

		public int ReadInt16LittleEndian()
		{
			var value = ReadUInt16LittleEndian();
			return unchecked((short)value);
		}

		public int ReadInt16BigEndian()
		{
			var value = ReadUInt16BigEndian();
			return unchecked((short)value);
		}

		public ushort ReadUInt16LittleEndian()
		{
			if (BytesLeft < 2)
			{
				_Position = _End;
				return 0;
			}
			var value = BinaryPrimitives.ReadUInt16LittleEndian(_Buffer.AsSpan(_Position, 2));
			_Position += 2;
			return value;
		}

		public ushort ReadUInt16BigEndian()
		{
			if (BytesLeft < 2)
			{
				_Position = _End;
				return 0;
			}
			var value = BinaryPrimitives.ReadUInt16BigEndian(_Buffer.AsSpan(_Position, 2));
			_Position += 2;
			return value;
		}

		public uint ReadUInt32LittleEndian()
		{
			if (BytesLeft < 4)
			{
				_Position = _End;
				return 0;
			}
			var value = BinaryPrimitives.ReadUInt32LittleEndian(_Buffer.AsSpan(_Position, 4));
			_Position += 4;
			return value;
		}

		public uint ReadUInt32BigEndian()
		{
			if (BytesLeft < 4)
			{
				_Position = _End;
				return 0;
			}
			var value = BinaryPrimitives.ReadUInt32BigEndian(_Buffer.AsSpan(_Position, 4));
			_Position += 4;
			return value;
		}

		public void Skip(int count)
		{
			_Position = Math.Clamp(_Position + count, _Start, _End);
		}

		public void Seek(int position)
		{
			_Position = Math.Clamp(_Start + position, _Start, _End);
		}
	}
}
