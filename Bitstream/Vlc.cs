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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Bitstream
{
	/// <summary>
	/// Stores one FFmpeg VLC lookup entry, including negative lengths that redirect into subtables.
	/// </summary>
	internal struct VlcElement
	{
		public short Symbol;
		public short Length;
	}

	/// <summary>
	/// Ports FFmpeg's sparse and canonical VLC table construction without allocations during decoding.
	/// </summary>
	internal sealed class Vlc
	{
		private const int LocalBufferElements = 1500;
		private VlcElement[] _Table;
		private static readonly byte[] s_Reverse =
		{
			0x00,0x80,0x40,0xc0,0x20,0xa0,0x60,0xe0,0x10,0x90,0x50,0xd0,0x30,0xb0,0x70,0xf0,
			0x08,0x88,0x48,0xc8,0x28,0xa8,0x68,0xe8,0x18,0x98,0x58,0xd8,0x38,0xb8,0x78,0xf8,
			0x04,0x84,0x44,0xc4,0x24,0xa4,0x64,0xe4,0x14,0x94,0x54,0xd4,0x34,0xb4,0x74,0xf4,
			0x0c,0x8c,0x4c,0xcc,0x2c,0xac,0x6c,0xec,0x1c,0x9c,0x5c,0xdc,0x3c,0xbc,0x7c,0xfc,
			0x02,0x82,0x42,0xc2,0x22,0xa2,0x62,0xe2,0x12,0x92,0x52,0xd2,0x32,0xb2,0x72,0xf2,
			0x0a,0x8a,0x4a,0xca,0x2a,0xaa,0x6a,0xea,0x1a,0x9a,0x5a,0xda,0x3a,0xba,0x7a,0xfa,
			0x06,0x86,0x46,0xc6,0x26,0xa6,0x66,0xe6,0x16,0x96,0x56,0xd6,0x36,0xb6,0x76,0xf6,
			0x0e,0x8e,0x4e,0xce,0x2e,0xae,0x6e,0xee,0x1e,0x9e,0x5e,0xde,0x3e,0xbe,0x7e,0xfe,
			0x01,0x81,0x41,0xc1,0x21,0xa1,0x61,0xe1,0x11,0x91,0x51,0xd1,0x31,0xb1,0x71,0xf1,
			0x09,0x89,0x49,0xc9,0x29,0xa9,0x69,0xe9,0x19,0x99,0x59,0xd9,0x39,0xb9,0x79,0xf9,
			0x05,0x85,0x45,0xc5,0x25,0xa5,0x65,0xe5,0x15,0x95,0x55,0xd5,0x35,0xb5,0x75,0xf5,
			0x0d,0x8d,0x4d,0xcd,0x2d,0xad,0x6d,0xed,0x1d,0x9d,0x5d,0xdd,0x3d,0xbd,0x7d,0xfd,
			0x03,0x83,0x43,0xc3,0x23,0xa3,0x63,0xe3,0x13,0x93,0x53,0xd3,0x33,0xb3,0x73,0xf3,
			0x0b,0x8b,0x4b,0xcb,0x2b,0xab,0x6b,0xeb,0x1b,0x9b,0x5b,0xdb,0x3b,0xbb,0x7b,0xfb,
			0x07,0x87,0x47,0xc7,0x27,0xa7,0x67,0xe7,0x17,0x97,0x57,0xd7,0x37,0xb7,0x77,0xf7,
			0x0f,0x8f,0x4f,0xcf,0x2f,0xaf,0x6f,0xef,0x1f,0x9f,0x5f,0xdf,0x3f,0xbf,0x7f,0xff
		};

		public int RootBits { get; private set; }
		public VlcElement[] Table => _Table;
		public int TableSize { get; private set; }
		public int TableAllocated { get; private set; }

		/// <summary>
		/// Builds FFmpeg's sparse VLC lookup hierarchy from explicit code lengths, codewords, and optional symbols.
		/// </summary>
		public int InitializeSparse(int rootBits, byte[] lengths, uint[] codes, short[] symbols = null, VlcFlags flags = VlcFlags.None)
		{
			if (lengths == null || codes == null || lengths.Length != codes.Length || (symbols != null && symbols.Length != lengths.Length))
			{
				return FfmpegError.InvalidArgument;
			}
			if (symbols != null && symbols.Length > short.MaxValue)
			{
				return FfmpegError.InvalidArgument;
			}

			InitializeCommon(rootBits);
			var buffer = new VlcCode[lengths.Length];
			var count = 0;
			for (var index = 0; index < lengths.Length; index++)
			{
				var length = (int)lengths[index];
				if (length <= rootBits)
				{
					continue;
				}
				var result = CopySparseCode(buffer, ref count, index, length, codes[index], symbols, rootBits, flags);
				if (result < 0)
				{
					return result;
				}
			}

			SortCodes(buffer, count);
			for (var index = 0; index < lengths.Length; index++)
			{
				var length = (int)lengths[index];
				if (length == 0 || length > rootBits)
				{
					continue;
				}
				var result = CopySparseCode(buffer, ref count, index, length, codes[index], symbols, rootBits, flags);
				if (result < 0)
				{
					return result;
				}
			}

			return FinishInitialization(buffer, count, flags);
		}

		/// <summary>
		/// Reconstructs canonical codewords from signed lengths and builds FFmpeg's root and subtable layout.
		/// </summary>
		public int InitializeFromLengths(int rootBits, sbyte[] lengths, short[] symbols = null, int offset = 0, VlcFlags flags = VlcFlags.None)
		{
			if (lengths == null || (symbols != null && symbols.Length != lengths.Length))
			{
				return FfmpegError.InvalidArgument;
			}

			InitializeCommon(rootBits);
			var buffer = new VlcCode[lengths.Length];
			ulong code = 0;
			var count = 0;
			var maximumLength = Math.Min(32, 3 * rootBits);
			for (var index = 0; index < lengths.Length; index++)
			{
				var length = (int)lengths[index];
				if (length > 0)
				{
					buffer[count].Bits = (byte)length;
					buffer[count].Symbol = (short)((symbols != null ? symbols[index] : index) + offset);
					buffer[count].Code = (uint)code;
					count++;
				} else if (length < 0)
				{
					length = -length;
				} else
				{
					continue;
				}

				if (length > maximumLength || ((uint)code & ((1U << (32 - length)) - 1)) != 0)
				{
					return FfmpegError.InvalidData;
				}
				code += 1UL << (32 - length);
				if (code > uint.MaxValue + 1UL)
				{
					return FfmpegError.InvalidData;
				}
			}

			return FinishInitialization(buffer, count, flags);
		}

		private void InitializeCommon(int rootBits)
		{
			RootBits = rootBits;
			TableSize = 0;
			TableAllocated = 0;
			_Table = null;
		}

		private int CopySparseCode(VlcCode[] buffer, ref int count, int index, int length, uint code, short[] symbols, int rootBits, VlcFlags flags)
		{
			if (length > 3 * rootBits || length > 32 || code >= (1UL << (int)length))
			{
				return FfmpegError.InvalidArgument;
			}

			buffer[count].Bits = (byte)length;
			buffer[count].Code = (flags & VlcFlags.InputLittleEndian) != 0 ? BitSwap32(code) : code << (32 - (int)length);
			buffer[count].Symbol = symbols != null ? symbols[index] : (short)index;
			count++;
			return 0;
		}

		private int FinishInitialization(VlcCode[] codes, int count, VlcFlags flags)
		{
			var result = BuildTable(RootBits, codes, 0, count, flags);
			if (result < 0)
			{
				_Table = null;
				TableAllocated = 0;
				TableSize = 0;
				return result;
			}

			return 0;
		}

		/// <summary>
		/// Recursively fills FFmpeg's fixed-width root table and its at-most-three nested subtables.
		/// </summary>
		private int BuildTable(int tableBitCount, VlcCode[] codes, int codeOffset, int codeCount, VlcFlags flags)
		{
			if (tableBitCount > 30)
			{
				return FfmpegError.InvalidArgument;
			}

			var tableLength = 1 << tableBitCount;
			var tableIndex = AllocateTable(tableLength);
			if (tableIndex < 0)
			{
				return tableIndex;
			}

			for (var relativeIndex = 0; relativeIndex < codeCount; relativeIndex++)
			{
				var sourceIndex = codeOffset + relativeIndex;
				var length = (int)codes[sourceIndex].Bits;
				var code = codes[sourceIndex].Code;
				var symbol = codes[sourceIndex].Symbol;
				if (length <= tableBitCount)
				{
					var destination = (int)(code >> (32 - tableBitCount));
					var repeat = 1 << (tableBitCount - length);
					var increment = 1;
					if ((flags & VlcFlags.OutputLittleEndian) != 0)
					{
						destination = (int)BitSwap32(code);
						increment = 1 << length;
					}
					for (var repeatIndex = 0; repeatIndex < repeat; repeatIndex++)
					{
						var entryIndex = tableIndex + destination;
						var oldLength = _Table[entryIndex].Length;
						var oldSymbol = _Table[entryIndex].Symbol;
						if ((oldLength != 0 || oldSymbol != 0) && (oldLength != length || oldSymbol != symbol))
						{
							return FfmpegError.InvalidData;
						}
						_Table[entryIndex].Length = (short)length;
						_Table[entryIndex].Symbol = symbol;
						destination += increment;
					}
				} else
				{
					var originalPrefix = code >> (32 - tableBitCount);
					length -= tableBitCount;
					var subtableBits = length;
					codes[sourceIndex].Bits = (byte)length;
					codes[sourceIndex].Code = code << tableBitCount;
					var groupedCount = relativeIndex + 1;
					for (; groupedCount < codeCount; groupedCount++)
					{
						var groupedIndex = codeOffset + groupedCount;
						var groupedLength = codes[groupedIndex].Bits - tableBitCount;
						if (groupedLength <= 0)
						{
							break;
						}
						code = codes[groupedIndex].Code;
						if (code >> (32 - tableBitCount) != originalPrefix)
						{
							break;
						}
						codes[groupedIndex].Bits = (byte)groupedLength;
						codes[groupedIndex].Code = code << tableBitCount;
						subtableBits = Math.Max(subtableBits, groupedLength);
					}

					subtableBits = Math.Min(subtableBits, tableBitCount);
					var destination = (flags & VlcFlags.OutputLittleEndian) != 0
						? (int)(BitSwap32(originalPrefix) >> (32 - tableBitCount))
						: (int)originalPrefix;
					_Table[tableIndex + destination].Length = (short)-subtableBits;
					var subtableIndex = BuildTable(subtableBits, codes, sourceIndex, groupedCount - relativeIndex, flags);
					if (subtableIndex < 0)
					{
						return subtableIndex;
					}
					_Table[tableIndex + destination].Symbol = (short)subtableIndex;
					relativeIndex = groupedCount - 1;
				}
			}

			for (var index = 0; index < tableLength; index++)
			{
				if (_Table[tableIndex + index].Length == 0)
				{
					_Table[tableIndex + index].Symbol = -1;
				}
			}

			return tableIndex;
		}

		private int AllocateTable(int size)
		{
			var index = TableSize;
			TableSize += size;
			if (TableSize > TableAllocated)
			{
				TableAllocated += 1 << RootBits;
				try
				{
					Array.Resize(ref _Table, TableAllocated);
				} catch (OutOfMemoryException)
				{
					TableAllocated = 0;
					TableSize = 0;
					return FfmpegError.OutOfMemory;
				}
			}

			return index;
		}

		private static uint BitSwap32(uint value)
		{
			return (uint)s_Reverse[value & 0xff] << 24 |
				(uint)s_Reverse[(value >> 8) & 0xff] << 16 |
				(uint)s_Reverse[(value >> 16) & 0xff] << 8 |
				s_Reverse[value >> 24];
		}

		/// <summary>
		/// Implements AV_QSORT verbatim so equal-prefix VLC codes retain FFmpeg's construction order.
		/// </summary>
		private static void SortCodes(VlcCode[] codes, int count)
		{
			var starts = new int[64];
			var ends = new int[64];
			var stackPosition = 1;
			starts[0] = 0;
			ends[0] = count - 1;
			while (stackPosition != 0)
			{
				stackPosition--;
				var start = starts[stackPosition];
				var end = ends[stackPosition];
				while (start < end)
				{
					if (start < end - 1)
					{
						var checkSorted = false;
						var right = end - 2;
						var left = start + 1;
						var middle = start + ((end - start) >> 1);
						if (Compare(codes[start], codes[end]) > 0)
						{
							if (Compare(codes[end], codes[middle]) > 0)
							{
								Swap(codes, start, middle);
							} else
							{
								Swap(codes, start, end);
							}
						} else if (Compare(codes[start], codes[middle]) > 0)
						{
							Swap(codes, start, middle);
						} else
						{
							checkSorted = true;
						}
						if (Compare(codes[middle], codes[end]) > 0)
						{
							Swap(codes, middle, end);
							checkSorted = false;
						}
						if (start == end - 2)
						{
							break;
						}
						Swap(codes, end - 1, middle);
						while (left <= right)
						{
							while (left <= right && Compare(codes[left], codes[end - 1]) < 0)
							{
								left++;
							}
							while (left <= right && Compare(codes[right], codes[end - 1]) > 0)
							{
								right--;
							}
							if (left <= right)
							{
								Swap(codes, left, right);
								left++;
								right--;
							}
						}
						Swap(codes, end - 1, left);
						if (checkSorted && (middle == left - 1 || middle == left))
						{
							middle = start;
							while (middle < end && Compare(codes[middle], codes[middle + 1]) <= 0)
							{
								middle++;
							}
							if (middle == end)
							{
								break;
							}
						}
						if (end - left < left - start)
						{
							starts[stackPosition] = start;
							ends[stackPosition] = right;
							stackPosition++;
							start = left + 1;
						} else
						{
							starts[stackPosition] = left + 1;
							ends[stackPosition] = end;
							stackPosition++;
							end = right;
						}
					} else
					{
						if (Compare(codes[start], codes[end]) > 0)
						{
							Swap(codes, start, end);
						}
						break;
					}
				}
			}
		}

		private static int Compare(VlcCode first, VlcCode second)
		{
			return unchecked((int)((first.Code >> 1) - (second.Code >> 1)));
		}

		private static void Swap(VlcCode[] codes, int first, int second)
		{
			var temporary = codes[first];
			codes[first] = codes[second];
			codes[second] = temporary;
		}

		/// <summary>
		/// Mirrors FFmpeg's temporary VLCcode used only while constructing lookup tables.
		/// </summary>
		private struct VlcCode
		{
			public byte Bits;
			public short Symbol;
			public uint Code;
		}
	}

	/// <summary>
	/// Matches FFmpeg's input and output bit-order flags used during VLC construction.
	/// </summary>
	[Flags]
	internal enum VlcFlags
	{
		None = 0,
		UseStatic = 1,
		StaticOverlong = 3,
		InputLittleEndian = 4,
		OutputLittleEndian = 8,
		LittleEndian = InputLittleEndian | OutputLittleEndian
	}
}
