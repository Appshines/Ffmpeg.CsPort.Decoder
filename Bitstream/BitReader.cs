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
using System.Runtime.CompilerServices;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Bitstream
{
	/// <summary>
	/// Ports FFmpeg's safe GetBitContext reader for big- and little-endian compressed audio bitstreams.
	/// </summary>
	internal sealed class BitReader
	{
		private byte[] _Buffer;
		private int _ByteOffset;
		private int _ByteLength;
		private int _Index;
		private int _SizeInBits;
		private int _SizeInBitsPlusEight;
		private bool _LittleEndian;

		public int Position => _Index;
		public int SizeInBits => _SizeInBits;
		public int BitsLeft => _SizeInBits - _Index;

		public int Initialize(byte[] buffer, int bitSize, bool littleEndian = false)
		{
			return Initialize(buffer, 0, bitSize, littleEndian);
		}

		public int Initialize(byte[] buffer, int byteOffset, int bitSize, bool littleEndian = false)
		{
			var result = 0;
			if (bitSize >= int.MaxValue - Math.Max(7, 64 * 8) || bitSize < 0 || buffer == null ||
				byteOffset < 0 || byteOffset > buffer.Length || (bitSize + 7L) / 8 > buffer.Length - byteOffset)
			{
				bitSize = 0;
				buffer = null;
				byteOffset = 0;
				result = FfmpegError.InvalidData;
			}

			_Buffer = buffer;
			_ByteOffset = byteOffset;
			_ByteLength = (bitSize + 7) / 8;
			_SizeInBits = bitSize;
			_SizeInBitsPlusEight = bitSize + 8;
			_Index = 0;
			_LittleEndian = littleEndian;
			return result;
		}

		public int InitializeBytes(byte[] buffer, int byteSize, bool littleEndian = false)
		{
			if (byteSize > int.MaxValue / 8 || byteSize < 0)
			{
				byteSize = -1;
			}

			return Initialize(buffer, byteSize * 8, littleEndian);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint ReadBits(int bitCount)
		{
			if (bitCount <= 0 || bitCount > 25)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}

			var result = ShowBits(bitCount);
			SkipBits(bitCount);
			return result;
		}

		public uint ReadBitsOrZero(int bitCount)
		{
			return bitCount != 0 ? ReadBits(bitCount) : 0;
		}

		public uint ReadBitsLong(int bitCount)
		{
			if (bitCount < 0 || bitCount > 32)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}
			if (bitCount == 0)
			{
				return 0;
			}

			var result = ShowBitsLong(bitCount);
			SkipBits(bitCount);
			return result;
		}

		public ulong ReadBits64(int bitCount)
		{
			if (bitCount <= 32)
			{
				return ReadBitsLong(bitCount);
			}
			if (bitCount > 64)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}

			if (_LittleEndian)
			{
				var result = ReadBitsLong(32);
				return result | (ulong)ReadBitsLong(bitCount - 32) << 32;
			} else
			{
				var result = (ulong)ReadBitsLong(bitCount - 32) << 32;
				return result | ReadBitsLong(32);
			}
		}

		public int ReadSignedBits(int bitCount)
		{
			return bitCount != 0 ? FfmpegMath.SignExtend(ReadBitsLong(bitCount), bitCount) : 0;
		}

		public long ReadSignedBits64(int bitCount)
		{
			return bitCount != 0 ? FfmpegMath.SignExtend64(ReadBits64(bitCount), bitCount) : 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint ReadBit()
		{
			var index = _Index;
			var byteIndex = index >> 3;
			var result = _Buffer != null && (uint)byteIndex < (uint)_ByteLength ?
				_Buffer[_ByteOffset + byteIndex] : (byte)0;
			if (_LittleEndian)
			{
				result >>= index & 7;
				result &= 1;
			} else
			{
				result <<= index & 7;
				result >>= 7;
			}

			if (_Index < _SizeInBitsPlusEight)
			{
				index++;
			}
			_Index = index;
			return result;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint ShowBits(int bitCount)
		{
			if (bitCount <= 0 || bitCount > 25)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}

			return _LittleEndian ? ShowLittleEndianBits(bitCount) : ShowBigEndianBits(bitCount);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint ShowBitsLong(int bitCount)
		{
			if (bitCount <= 0 || bitCount > 32)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}

			return _LittleEndian ? ShowLittleEndianBits(bitCount) : ShowBigEndianBits(bitCount);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void SkipBits(int bitCount)
		{
			if (bitCount < -_Index)
			{
				_Index = 0;
			} else if (bitCount > _SizeInBitsPlusEight - _Index)
			{
				_Index = _SizeInBitsPlusEight;
			} else
			{
				_Index += bitCount;
			}
		}

		public void Seek(int position)
		{
			_Index = 0;
			SkipBits(position);
		}

		public int Align()
		{
			var bitCount = -_Index & 7;
			if (bitCount != 0)
			{
				SkipBits(bitCount);
			}

			return _Index >> 3;
		}

		public int ReadExtendedBits(int bitCount)
		{
			if (bitCount <= 0 || bitCount > 25)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}

			var cache = unchecked((int)ShowBigEndianCache());
			var sign = ~cache >> 31;
			SkipBits(bitCount);
			return unchecked((int)((((uint)(sign ^ cache) >> (32 - bitCount)) ^ (uint)sign) - (uint)sign));
		}

		public int Decode012()
		{
			var value = ReadBit();
			return value == 0 ? 0 : (int)ReadBit() + 1;
		}

		public int Decode210()
		{
			return ReadBit() != 0 ? 0 : 2 - (int)ReadBit();
		}

		public int ApplySign(int value)
		{
			var sign = ReadSignedBits(1);
			return (value ^ sign) - sign;
		}

		public int SkipOneStopEightData()
		{
			if (BitsLeft <= 0)
			{
				return FfmpegError.InvalidData;
			}

			while (ReadBit() != 0)
			{
				SkipBits(8);
				if (BitsLeft <= 0)
				{
					return FfmpegError.InvalidData;
				}
			}

			return 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int ReadVlc(VlcElement[] table, int rootBits, int maximumDepth)
		{
			var index = ShowBits(rootBits);
			var code = table[index].Symbol;
			var length = table[index].Length;
			if (maximumDepth > 1 && length < 0)
			{
				SkipBits(rootBits);
				var nextBits = -length;
				index = (uint)(ShowBits(nextBits) + code);
				code = table[index].Symbol;
				length = table[index].Length;
				if (maximumDepth > 2 && length < 0)
				{
					SkipBits(nextBits);
					nextBits = -length;
					index = (uint)(ShowBits(nextBits) + code);
					code = table[index].Symbol;
					length = table[index].Length;
				}
			}
			SkipBits(length);
			return code;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public BitReaderLocal OpenLocal()
		{
			if (_LittleEndian)
			{
				throw new InvalidOperationException("Local bit-reader state supports big-endian bitstreams only.");
			}

			return new BitReaderLocal(this);
		}

		/// <summary>
		/// Holds FFmpeg-style open-reader state locally and writes only the final bit index back to its owner.
		/// </summary>
		internal ref struct BitReaderLocal
		{
			private readonly BitReader _Reader;
			private int _Index;
			private ulong _Cache;
			private int _CacheBits;

			internal BitReaderLocal(BitReader reader)
			{
				_Reader = reader;
				_Index = reader._Index;
				_Cache = 0;
				_CacheBits = 0;
			}

			public int Position => _Index;
			public int BitsLeft => _Reader._SizeInBits - _Index;
			public int CacheBits => _CacheBits;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public uint ReadBit()
			{
				EnsureCache(1);
				var result = (uint)(_Cache >> 63);
				ConsumeBits(1);
				return result;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public uint ReadBits(int bitCount)
			{
				if (bitCount <= 0 || bitCount > 25)
				{
					throw new ArgumentOutOfRangeException(nameof(bitCount));
				}

				var result = ShowBits(bitCount);
				ConsumeBits(bitCount);
				return result;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public uint ReadBitsOrZero(int bitCount)
			{
				return bitCount != 0 ? ReadBits(bitCount) : 0;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public uint ReadBitsLong(int bitCount)
			{
				if (bitCount < 0 || bitCount > 32)
				{
					throw new ArgumentOutOfRangeException(nameof(bitCount));
				}
				if (bitCount == 0)
				{
					return 0;
				}

				EnsureCache(bitCount);
				var result = (uint)(_Cache >> (64 - bitCount));
				ConsumeBits(bitCount);
				return result;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public int ReadSignedBits(int bitCount)
			{
				return bitCount != 0 ? FfmpegMath.SignExtend(ReadBitsLong(bitCount), bitCount) : 0;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public uint ShowBits(int bitCount)
			{
				if (bitCount <= 0 || bitCount > 25)
				{
					throw new ArgumentOutOfRangeException(nameof(bitCount));
				}

				EnsureCache(bitCount);
				return (uint)(_Cache >> (64 - bitCount));
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ulong PeekCache()
			{
				EnsureCache(32);
				return _Cache;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void SkipBits(int bitCount)
			{
				var previousIndex = _Index;
				if (bitCount < -_Index)
				{
					_Index = 0;
				} else if (bitCount > _Reader._SizeInBitsPlusEight - _Index)
				{
					_Index = _Reader._SizeInBitsPlusEight;
				} else
				{
					_Index += bitCount;
				}

				var skippedBits = _Index - previousIndex;
				if (skippedBits < 0 || skippedBits >= _CacheBits)
				{
					_CacheBits = 0;
				} else if (skippedBits != 0)
				{
					_Cache <<= skippedBits;
					_CacheBits -= skippedBits;
				}
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public int ReadVlc(VlcElement[] table, int rootBits, int maximumDepth)
			{
				var index = ShowBits(rootBits);
				var code = table[index].Symbol;
				var length = table[index].Length;
				if (maximumDepth > 1 && length < 0)
				{
					ConsumeBits(rootBits);
					var nextBits = -length;
					index = (uint)(ShowBits(nextBits) + code);
					code = table[index].Symbol;
					length = table[index].Length;
					if (maximumDepth > 2 && length < 0)
					{
						ConsumeBits(nextBits);
						nextBits = -length;
						index = (uint)(ShowBits(nextBits) + code);
						code = table[index].Symbol;
						length = table[index].Length;
					}
				}
				ConsumeBits(length);
				return code;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Close()
			{
				_Reader._Index = _Index;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private void EnsureCache(int bitCount)
			{
				if (_CacheBits < bitCount)
				{
					_Cache = _Reader.LoadBigEndianCache(_Index);
					_CacheBits = 64 - (_Index & 7);
				}
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private void ConsumeBits(int bitCount)
			{
				var remainingBits = _Reader._SizeInBitsPlusEight - _Index;
				if (bitCount > remainingBits)
				{
					bitCount = remainingBits;
				}

				_Index += bitCount;
				if (bitCount >= _CacheBits)
				{
					_CacheBits = 0;
				} else if (bitCount != 0)
				{
					_Cache <<= bitCount;
					_CacheBits -= bitCount;
				}
			}

		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private ulong LoadBigEndianCache(int index)
		{
			var offset = index >> 3;
			var bitOffset = index & 7;
			if (BitConverter.IsLittleEndian && (uint)(offset + 8) <= (uint)_ByteLength)
			{
				// The explicit eight-byte availability check keeps this unaligned read inside the logical bitstream buffer.
				return BinaryPrimitives.ReverseEndianness(
					Unsafe.ReadUnaligned<ulong>(ref _Buffer[_ByteOffset + offset])) << bitOffset;
			}

			return ((ulong)ReadPaddedByte(offset) << 56 |
				(ulong)ReadPaddedByte(offset + 1) << 48 |
				(ulong)ReadPaddedByte(offset + 2) << 40 |
				(ulong)ReadPaddedByte(offset + 3) << 32 |
				(ulong)ReadPaddedByte(offset + 4) << 24 |
				(ulong)ReadPaddedByte(offset + 5) << 16 |
				(ulong)ReadPaddedByte(offset + 6) << 8 |
				ReadPaddedByte(offset + 7)) << bitOffset;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private uint ShowBigEndianBits(int bitCount)
		{
			var offset = _Index >> 3;
			if (BitConverter.IsLittleEndian && (uint)(offset + 8) <= (uint)_ByteLength)
			{
				// The explicit eight-byte availability check keeps this unaligned read inside the logical bitstream buffer.
				var raw = Unsafe.ReadUnaligned<ulong>(ref _Buffer[_ByteOffset + offset]);
				raw = BinaryPrimitives.ReverseEndianness(raw);
				return (uint)((raw << (_Index & 7)) >> (64 - bitCount));
			}

			return ShowBigEndianBitsSlow(bitCount);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private uint ShowBigEndianBitsSlow(int bitCount)
		{
			var offset = _Index >> 3;
			var bitOffset = _Index & 7;
			var cache = (ulong)ReadPaddedByte(offset) << 32 |
				(ulong)ReadPaddedByte(offset + 1) << 24 |
				(ulong)ReadPaddedByte(offset + 2) << 16 |
				(ulong)ReadPaddedByte(offset + 3) << 8 |
				ReadPaddedByte(offset + 4);
			var value = (uint)(cache >> (40 - bitOffset - bitCount));
			return bitCount == 32 ? value : value & ((1U << bitCount) - 1);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private uint ShowLittleEndianBits(int bitCount)
		{
			var offset = _Index >> 3;
			if (BitConverter.IsLittleEndian && (uint)(offset + 8) <= (uint)_ByteLength)
			{
				// The explicit eight-byte availability check keeps this unaligned read inside the logical bitstream buffer.
				var raw = Unsafe.ReadUnaligned<ulong>(ref _Buffer[_ByteOffset + offset]);
				var value = (uint)(raw >> (_Index & 7));
				return bitCount == 32 ? value : value & ((1U << bitCount) - 1);
			}

			return ShowLittleEndianBitsSlow(bitCount);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private uint ShowLittleEndianBitsSlow(int bitCount)
		{
			var offset = _Index >> 3;
			var cache = (ulong)ReadPaddedByte(offset) |
				(ulong)ReadPaddedByte(offset + 1) << 8 |
				(ulong)ReadPaddedByte(offset + 2) << 16 |
				(ulong)ReadPaddedByte(offset + 3) << 24 |
				(ulong)ReadPaddedByte(offset + 4) << 32;
			var value = (uint)(cache >> (_Index & 7));
			return bitCount == 32 ? value : value & ((1U << bitCount) - 1);
		}

		private uint ShowBigEndianCache()
		{
			return ShowBigEndianBits(32);
		}

		private byte ReadPaddedByte(int index)
		{
			return _Buffer != null && (uint)index < (uint)_ByteLength ? _Buffer[_ByteOffset + index] : (byte)0;
		}
	}
}
