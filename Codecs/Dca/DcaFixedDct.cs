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

namespace Ffmpeg.CsPort.Decoder.Codecs.Dca
{
	/// <summary>
	/// Ports FFmpeg's scalar fixed-point 32-point DTS half-IMDCT with its exact clipping and rounding schedule.
	/// </summary>
	internal sealed class DcaFixedDct
	{
		private static readonly int[] s_DctA =
		{
			8348215, 8027397, 7398092, 6484482, 5321677, 3954362, 2435084, 822227,
			8027397, 5321677, 822227, -3954362, -7398092, -8348215, -6484482, -2435084,
			7398092, 822227, -6484482, -8027397, -2435084, 5321677, 8348215, 3954362,
			6484482, -3954362, -8027397, 822227, 8348215, 2435084, -7398092, -5321677,
			5321677, -7398092, -2435084, 8348215, -822227, -8027397, 3954362, 6484482,
			3954362, -8348215, 5321677, 2435084, -8027397, 6484482, 822227, -7398092,
			2435084, -6484482, 8348215, -7398092, 3954362, 822227, -5321677, 8027397,
			822227, -2435084, 3954362, -5321677, 6484482, -7398092, 8027397, -8348215
		};
		private static readonly int[] s_DctB =
		{
			8227423, 7750063, 6974873, 5931642, 4660461, 3210181, 1636536,
			6974873, 3210181, -1636536, -5931642, -8227423, -7750063, -4660461,
			4660461, -3210181, -8227423, -5931642, 1636536, 7750063, 6974873,
			1636536, -7750063, -4660461, 5931642, 6974873, -3210181, -8227423,
			-1636536, -7750063, 4660461, 5931642, -6974873, -3210181, 8227423,
			-4660461, -3210181, 8227423, -5931642, -1636536, 7750063, -6974873,
			-6974873, 3210181, 1636536, -5931642, 8227423, -7750063, 4660461,
			-8227423, 7750063, -6974873, 5931642, -4660461, 3210181, -1636536
		};
		private static readonly int[] s_ModA = { 4199362, 4240198, 4323885, 4454708, 4639772, 4890013, 5221943, 5660703, -6245623, -7040975, -8158494, -9809974, -12450076, -17261920, -28585092, -85479984 };
		private static readonly int[] s_ModB = { 4214598, 4383036, 4755871, 5425934, 6611520, 8897610, 14448934, 42791536 };
		private static readonly int[] s_ModC = { 1048892, 1051425, 1056522, 1064244, 1074689, 1087987, 1104313, 1123884, 1146975, 1173922, 1205139, 1241133, 1282529, 1330095, 1384791, 1447815, -1520688, -1605358, -1704360, -1821051, -1959964, -2127368, -2332183, -2587535, -2913561, -3342802, -3931480, -4785806, -6133390, -8566050, -14253820, -42727120 };
		private static readonly int[] s_Mod64A = { 4195568, 4205700, 4226086, 4256977, 4298755, 4351949, 4417251, 4495537, 4587901, 4695690, 4820557, 4964534, 5130115, 5320382, 5539164, 5791261, -6082752, -6421430, -6817439, -7284203, -7839855, -8509474, -9328732, -10350140, -11654242, -13371208, -15725922, -19143224, -24533560, -34264200, -57015280, -170908480 };
		private static readonly int[] s_Mod64B = { 4199362, 4240198, 4323885, 4454708, 4639772, 4890013, 5221943, 5660703, 6245623, 7040975, 8158494, 9809974, 12450076, 17261920, 28585092, 85479984 };
		private static readonly int[] s_Mod64C = { 741511, 741958, 742853, 744199, 746001, 748262, 750992, 754197, 757888, 762077, 766777, 772003, 777772, 784105, 791021, 798546, 806707, 815532, 825054, 835311, 846342, 858193, 870912, 884554, 899181, 914860, 931667, 949686, 969011, 989747, 1012012, 1035941, -1061684, -1089412, -1119320, -1151629, -1186595, -1224511, -1265719, -1310613, -1359657, -1413400, -1472490, -1537703, -1609974, -1690442, -1780506, -1881904, -1996824, -2128058, -2279225, -2455101, -2662128, -2909200, -3208956, -3579983, -4050785, -4667404, -5509372, -6726913, -8641940, -12091426, -20144284, -60420720 };
		private readonly int[] _BufferA = new int[64];
		private readonly int[] _BufferB = new int[64];

		/// <summary>
		/// Applies the integer factorization used by FFmpeg instead of approximating the fixed synthesis through floats.
		/// </summary>
		public void Transform32(int[] output, int outputOffset, int[] input)
		{
			var magnitude = 0;
			for (var index = 0; index < 32; index++) magnitude += Math.Abs(input[index]);
			var shift = magnitude > 0x400000 ? 2 : 0;
			var round = shift > 0 ? 1 << (shift - 1) : 0;
			for (var index = 0; index < 32; index++) _BufferA[index] = (input[index] + round) >> shift;

			SumA(_BufferA, 0, _BufferB, 0, 16);
			SumB(_BufferA, 0, _BufferB, 16, 16);
			Clip(_BufferB, 32);
			SumA(_BufferB, 0, _BufferA, 0, 8);
			SumB(_BufferB, 0, _BufferA, 8, 8);
			SumC(_BufferB, 16, _BufferA, 16, 8);
			SumD(_BufferB, 16, _BufferA, 24, 8);
			Clip(_BufferA, 32);
			DctA(_BufferA, 0, _BufferB, 0);
			DctB(_BufferA, 8, _BufferB, 8);
			DctB(_BufferA, 16, _BufferB, 16);
			DctB(_BufferA, 24, _BufferB, 24);
			Clip(_BufferB, 32);
			ModA(_BufferB, 0, _BufferA, 0);
			ModB(_BufferB, 16, _BufferA, 16);
			Clip(_BufferA, 32);
			ModC(_BufferA, _BufferB);
			for (var index = 0; index < 32; index++) _BufferB[index] = DcaMath.Clip23(unchecked(_BufferB[index] * (1 << shift)));
			for (var index = 0; index < 16; index++)
			{
				var reverse = 31 - index;
				output[outputOffset + index] = DcaMath.Clip23(unchecked(_BufferB[index] - _BufferB[reverse]));
				output[outputOffset + 16 + index] = DcaMath.Clip23(unchecked(_BufferB[index] + _BufferB[reverse]));
			}
		}

		/// <summary>
		/// Applies FFmpeg's 64-point extension of the fixed DTS factorization for X96 synthesis.
		/// </summary>
		public void Transform64(int[] output, int outputOffset, int[] input)
		{
			var magnitude = 0;
			for (var index = 0; index < 64; index++) magnitude += Math.Abs(input[index]);
			var shift = magnitude > 0x400000 ? 2 : 0;
			var round = shift > 0 ? 1 << (shift - 1) : 0;
			for (var index = 0; index < 64; index++) _BufferA[index] = (input[index] + round) >> shift;
			SumA(_BufferA, 0, _BufferB, 0, 32);
			SumB(_BufferA, 0, _BufferB, 32, 32);
			Clip(_BufferB, 64);
			SumA(_BufferB, 0, _BufferA, 0, 16);
			SumB(_BufferB, 0, _BufferA, 16, 16);
			SumC(_BufferB, 32, _BufferA, 32, 16);
			SumD(_BufferB, 32, _BufferA, 48, 16);
			Clip(_BufferA, 64);
			SumA(_BufferA, 0, _BufferB, 0, 8);
			SumB(_BufferA, 0, _BufferB, 8, 8);
			SumC(_BufferA, 16, _BufferB, 16, 8);
			SumD(_BufferA, 16, _BufferB, 24, 8);
			SumC(_BufferA, 32, _BufferB, 32, 8);
			SumD(_BufferA, 32, _BufferB, 40, 8);
			SumC(_BufferA, 48, _BufferB, 48, 8);
			SumD(_BufferA, 48, _BufferB, 56, 8);
			Clip(_BufferB, 64);
			DctA(_BufferB, 0, _BufferA, 0);
			for (var offset = 8; offset < 64; offset += 8) DctB(_BufferB, offset, _BufferA, offset);
			Clip(_BufferA, 64);
			ModA(_BufferA, 0, _BufferB, 0);
			ModB(_BufferA, 16, _BufferB, 16);
			ModB(_BufferA, 32, _BufferB, 32);
			ModB(_BufferA, 48, _BufferB, 48);
			Clip(_BufferB, 64);
			Mod64A(_BufferB, _BufferA);
			Mod64B(_BufferB, 32, _BufferA, 32);
			Clip(_BufferA, 64);
			Mod64C(_BufferA, _BufferB);
			for (var index = 0; index < 64; index++) _BufferB[index] = DcaMath.Clip23(unchecked(_BufferB[index] * (1 << shift)));
			for (var index = 0; index < 32; index++)
			{
				var reverse = 63 - index;
				output[outputOffset + index] = DcaMath.Clip23(unchecked(_BufferB[index] - _BufferB[reverse]));
				output[outputOffset + 32 + index] = DcaMath.Clip23(unchecked(_BufferB[index] + _BufferB[reverse]));
			}
		}

		private static void SumA(int[] input, int inputOffset, int[] output, int outputOffset, int length)
		{
			for (var index = 0; index < length; index++) output[outputOffset + index] = unchecked(input[inputOffset + index * 2] + input[inputOffset + index * 2 + 1]);
		}

		private static void SumB(int[] input, int inputOffset, int[] output, int outputOffset, int length)
		{
			output[outputOffset] = input[inputOffset];
			for (var index = 1; index < length; index++) output[outputOffset + index] = unchecked(input[inputOffset + index * 2] + input[inputOffset + index * 2 - 1]);
		}

		private static void SumC(int[] input, int inputOffset, int[] output, int outputOffset, int length)
		{
			for (var index = 0; index < length; index++) output[outputOffset + index] = input[inputOffset + index * 2];
		}

		private static void SumD(int[] input, int inputOffset, int[] output, int outputOffset, int length)
		{
			output[outputOffset] = input[inputOffset + 1];
			for (var index = 1; index < length; index++) output[outputOffset + index] = unchecked(input[inputOffset + index * 2 - 1] + input[inputOffset + index * 2 + 1]);
		}

		private static void DctA(int[] input, int inputOffset, int[] output, int outputOffset)
		{
			for (var row = 0; row < 8; row++)
			{
				long value = 0;
				for (var column = 0; column < 8; column++) value += (long)s_DctA[row * 8 + column] * input[inputOffset + column];
				output[outputOffset + row] = DcaMath.Normalize(value, 23);
			}
		}

		private static void DctB(int[] input, int inputOffset, int[] output, int outputOffset)
		{
			for (var row = 0; row < 8; row++)
			{
				long value = (long)input[inputOffset] << 23;
				for (var column = 0; column < 7; column++) value += (long)s_DctB[row * 7 + column] * input[inputOffset + 1 + column];
				output[outputOffset + row] = DcaMath.Normalize(value, 23);
			}
		}

		private static void ModA(int[] input, int inputOffset, int[] output, int outputOffset)
		{
			for (var index = 0; index < 8; index++) output[outputOffset + index] = DcaMath.Multiply(s_ModA[index], unchecked(input[inputOffset + index] + input[inputOffset + 8 + index]), 23);
			for (var index = 8; index < 16; index++)
			{
				var reverse = 15 - index;
				output[outputOffset + index] = DcaMath.Multiply(s_ModA[index], unchecked(input[inputOffset + reverse] - input[inputOffset + 8 + reverse]), 23);
			}
		}

		private static void ModB(int[] input, int inputOffset, int[] output, int outputOffset)
		{
			for (var index = 0; index < 8; index++) input[inputOffset + 8 + index] = DcaMath.Multiply(s_ModB[index], input[inputOffset + 8 + index], 23);
			for (var index = 0; index < 8; index++) output[outputOffset + index] = unchecked(input[inputOffset + index] + input[inputOffset + 8 + index]);
			for (var index = 8; index < 16; index++)
			{
				var reverse = 15 - index;
				output[outputOffset + index] = unchecked(input[inputOffset + reverse] - input[inputOffset + 8 + reverse]);
			}
		}

		private static void ModC(int[] input, int[] output)
		{
			for (var index = 0; index < 16; index++) output[index] = DcaMath.Multiply(s_ModC[index], unchecked(input[index] + input[16 + index]), 23);
			for (var index = 16; index < 32; index++)
			{
				var reverse = 31 - index;
				output[index] = DcaMath.Multiply(s_ModC[index], unchecked(input[reverse] - input[16 + reverse]), 23);
			}
		}

		private static void Mod64A(int[] input, int[] output)
		{
			for (var index = 0; index < 16; index++) output[index] = DcaMath.Multiply(s_Mod64A[index], unchecked(input[index] + input[16 + index]), 23);
			for (var index = 16; index < 32; index++)
			{
				var reverse = 31 - index;
				output[index] = DcaMath.Multiply(s_Mod64A[index], unchecked(input[reverse] - input[16 + reverse]), 23);
			}
		}

		private static void Mod64B(int[] input, int inputOffset, int[] output, int outputOffset)
		{
			for (var index = 0; index < 16; index++) input[inputOffset + 16 + index] = DcaMath.Multiply(s_Mod64B[index], input[inputOffset + 16 + index], 23);
			for (var index = 0; index < 16; index++) output[outputOffset + index] = unchecked(input[inputOffset + index] + input[inputOffset + 16 + index]);
			for (var index = 16; index < 32; index++)
			{
				var reverse = 31 - index;
				output[outputOffset + index] = unchecked(input[inputOffset + reverse] - input[inputOffset + 16 + reverse]);
			}
		}

		private static void Mod64C(int[] input, int[] output)
		{
			for (var index = 0; index < 32; index++) output[index] = DcaMath.Multiply(s_Mod64C[index], unchecked(input[index] + input[32 + index]), 23);
			for (var index = 32; index < 64; index++)
			{
				var reverse = 63 - index;
				output[index] = DcaMath.Multiply(s_Mod64C[index], unchecked(input[reverse] - input[32 + reverse]), 23);
			}
		}

		private static void Clip(int[] values, int length)
		{
			for (var index = 0; index < length; index++) values[index] = DcaMath.Clip23(values[index]);
		}
	}
}
