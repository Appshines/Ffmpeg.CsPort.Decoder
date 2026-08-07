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
using System.Security.Cryptography;

namespace Ffmpeg.CsPort.Decoder.Codecs.Ac3
{
	/// <summary>
	/// Initializes FFmpeg's grouped AC-3 mantissa tables and deterministic lagged-Fibonacci dither state.
	/// </summary>
	internal static class Ac3MantissaTables
	{
		public static readonly int[,] BitAllocationPointer1 = new int[32, 3];
		public static readonly int[,] BitAllocationPointer2 = new int[128, 3];
		public static readonly int[] BitAllocationPointer3 = new int[8];
		public static readonly int[,] BitAllocationPointer4 = new int[128, 2];
		public static readonly int[] BitAllocationPointer5 = new int[16];

		static Ac3MantissaTables()
		{
			for (var code = 0; code < 32; code++)
			{
				BitAllocationPointer1[code, 0] = SymmetricDequantize(Ac3Tables.UngroupThreeInFiveBits[code, 0], 3);
				BitAllocationPointer1[code, 1] = SymmetricDequantize(Ac3Tables.UngroupThreeInFiveBits[code, 1], 3);
				BitAllocationPointer1[code, 2] = SymmetricDequantize(Ac3Tables.UngroupThreeInFiveBits[code, 2], 3);
			}
			for (var code = 0; code < 128; code++)
			{
				BitAllocationPointer2[code, 0] = SymmetricDequantize(code / 25, 5);
				BitAllocationPointer2[code, 1] = SymmetricDequantize(code % 25 / 5, 5);
				BitAllocationPointer2[code, 2] = SymmetricDequantize(code % 25 % 5, 5);
				BitAllocationPointer4[code, 0] = SymmetricDequantize(code / 11, 11);
				BitAllocationPointer4[code, 1] = SymmetricDequantize(code % 11, 11);
			}
			for (var code = 0; code < 7; code++) BitAllocationPointer3[code] = SymmetricDequantize(code, 7);
			for (var code = 0; code < 15; code++) BitAllocationPointer5[code] = SymmetricDequantize(code, 15);
		}

		public static void InitializeDither(uint[] state)
		{
			System.Array.Clear(state, 0, state.Length);
			Span<byte> temporary = stackalloc byte[16];
			Span<byte> digest = stackalloc byte[16];
			temporary.Clear();
			for (var index = 8; index < 64; index += 4)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(temporary, 0);
				temporary[4] = (byte)index;
				MD5.HashData(temporary, digest);
				state[index] = BinaryPrimitives.ReadUInt32LittleEndian(digest);
				state[index + 1] = BinaryPrimitives.ReadUInt32LittleEndian(digest.Slice(4));
				state[index + 2] = BinaryPrimitives.ReadUInt32LittleEndian(digest.Slice(8));
				state[index + 3] = BinaryPrimitives.ReadUInt32LittleEndian(digest.Slice(12));
				digest.CopyTo(temporary);
			}
		}

		private static int SymmetricDequantize(int code, int levels)
		{
			return ((code - (levels >> 1)) * (1 << 24)) / levels;
		}
	}
}
