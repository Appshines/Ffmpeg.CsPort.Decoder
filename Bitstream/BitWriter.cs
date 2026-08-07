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

namespace Ffmpeg.CsPort.Decoder.Bitstream
{
	/// <summary>
	/// Ports FFmpeg PutBitContext and bitstream.c helpers for codec-side headers and copied bit ranges.
	/// </summary>
	internal sealed class BitWriter
	{
		private const int BufferBits = 64;
		private byte[] _Buffer;
		private ulong _BitBuffer;
		private int _BitsLeft;
		private int _BytePosition;

		public int BitCount => _BytePosition * 8 + BufferBits - _BitsLeft;
		public int BitsLeft => (_Buffer.Length - _BytePosition) * 8 - BufferBits + _BitsLeft;
		public int ByteCount => _BytePosition + ((BufferBits - _BitsLeft + 7) >> 3);

		public void Initialize(byte[] buffer)
		{
			_Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
			_BitBuffer = 0;
			_BitsLeft = BufferBits;
			_BytePosition = 0;
		}

		public void WriteBits(int bitCount, uint value)
		{
			if (bitCount < 0 || bitCount > 31 || value >= 1UL << bitCount)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}

			WriteBitsUnchecked(bitCount, value);
		}

		public void WriteBits64(int bitCount, ulong value)
		{
			if (bitCount < 0 || bitCount > 64 || bitCount < 64 && value >= 1UL << bitCount)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}
			if (bitCount < 64)
			{
				WriteBitsUnchecked(bitCount, value);
			} else
			{
				WriteBitsUnchecked(32, value >> 32);
				WriteBitsUnchecked(32, (uint)value);
			}
		}

		public void WriteSignedBits(int bitCount, int value)
		{
			WriteBits(bitCount, unchecked((uint)value) & ((1U << bitCount) - 1));
		}

		public void Align()
		{
			WriteBits(_BitsLeft & 7, 0);
		}

		public void Flush()
		{
			if (_BitsLeft < BufferBits)
			{
				_BitBuffer <<= _BitsLeft;
			}
			while (_BitsLeft < BufferBits)
			{
				EnsureOutputBytes(1);
				_Buffer[_BytePosition++] = (byte)(_BitBuffer >> (BufferBits - 8));
				_BitBuffer <<= 8;
				_BitsLeft += 8;
			}
			_BitsLeft = BufferBits;
			_BitBuffer = 0;
		}

		public void WriteString(string value, bool terminate)
		{
			if (value == null)
			{
				throw new ArgumentNullException(nameof(value));
			}
			for (var index = 0; index < value.Length; index++)
			{
				WriteBits(8, value[index]);
			}
			if (terminate)
			{
				WriteBits(8, 0);
			}
		}

		/// <summary>
		/// Preserves bitstream.c's sixteen-bit copy loop and final partial-word extraction.
		/// </summary>
		public void CopyBits(byte[] source, int bitLength)
		{
			if (source == null)
			{
				throw new ArgumentNullException(nameof(source));
			}
			if (bitLength < 0 || bitLength > BitsLeft)
			{
				throw new ArgumentOutOfRangeException(nameof(bitLength));
			}
			if (bitLength == 0)
			{
				return;
			}

			var words = bitLength >> 4;
			var bits = bitLength & 15;
			for (var index = 0; index < words; index++)
			{
				var offset = 2 * index;
				WriteBits(16, (uint)(source[offset] << 8 | source[offset + 1]));
			}
			if (bits != 0)
			{
				var offset = 2 * words;
				var value = (uint)(source[offset] << 8 | (offset + 1 < source.Length ? source[offset + 1] : 0));
				WriteBits(bits, value >> (16 - bits));
			}
		}

		private void WriteBitsUnchecked(int bitCount, ulong value)
		{
			var bitBuffer = _BitBuffer;
			var bitsLeft = _BitsLeft;
			if (bitCount < bitsLeft)
			{
				bitBuffer = bitBuffer << bitCount | value;
				bitsLeft -= bitCount;
			} else
			{
				bitBuffer <<= bitsLeft;
				bitBuffer |= value >> (bitCount - bitsLeft);
				EnsureOutputBytes(sizeof(ulong));
				WriteBigEndian64(_BytePosition, bitBuffer);
				_BytePosition += sizeof(ulong);
				bitsLeft += BufferBits - bitCount;
				bitBuffer = value;
			}
			_BitBuffer = bitBuffer;
			_BitsLeft = bitsLeft;
		}

		private void WriteBigEndian64(int offset, ulong value)
		{
			_Buffer[offset] = (byte)(value >> 56);
			_Buffer[offset + 1] = (byte)(value >> 48);
			_Buffer[offset + 2] = (byte)(value >> 40);
			_Buffer[offset + 3] = (byte)(value >> 32);
			_Buffer[offset + 4] = (byte)(value >> 24);
			_Buffer[offset + 5] = (byte)(value >> 16);
			_Buffer[offset + 6] = (byte)(value >> 8);
			_Buffer[offset + 7] = (byte)value;
		}

		private void EnsureOutputBytes(int byteCount)
		{
			if (_Buffer == null || _BytePosition + byteCount > _Buffer.Length)
			{
				throw new InvalidOperationException("FFmpeg bit writer buffer is too small.");
			}
		}
	}
}
