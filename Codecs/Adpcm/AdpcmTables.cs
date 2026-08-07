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
namespace Ffmpeg.CsPort.Decoder.Codecs.Adpcm
{
	/// <summary>
	/// Stores the immutable lookup tables copied from FFmpeg's adpcm_data.c and adpcm.c.
	/// </summary>
	internal static class AdpcmTables
	{
		public static readonly sbyte[] Index =
		{
			-1, -1, -1, -1, 2, 4, 6, 8,
			-1, -1, -1, -1, 2, 4, 6, 8
		};

		public static readonly short[] Step =
		{
			7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
			19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
			50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
			130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
			337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
			876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
			2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
			5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
			15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
		};

		public static readonly short[] Adaptation =
		{
			230, 230, 230, 230, 307, 409, 512, 614,
			768, 614, 512, 409, 307, 230, 230, 230
		};

		public static readonly byte[] AdaptCoefficient1 = { 64, 128, 0, 48, 60, 115, 98 };
		public static readonly sbyte[] AdaptCoefficient2 = { 0, -64, 0, 16, 0, -52, -58 };

		public static readonly short[] YamahaIndexScale =
		{
			230, 230, 230, 230, 307, 409, 512, 614,
			230, 230, 230, 230, 307, 409, 512, 614
		};

		public static readonly sbyte[] YamahaDifference =
		{
			1, 3, 5, 7, 9, 11, 13, 15,
			-1, -3, -5, -7, -9, -11, -13, -15
		};

		public static readonly byte[] ImaBlockSizes = { 4, 12, 4, 20 };
		public static readonly byte[] ImaBlockSamples = { 16, 32, 8, 32 };

		public static readonly sbyte[][] SwfIndex =
		{
			new sbyte[] { -1, 2 },
			new sbyte[] { -1, -1, 2, 4 },
			new sbyte[] { -1, -1, -1, -1, 2, 4, 6, 8 },
			new sbyte[] { -1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16 }
		};

		public static readonly sbyte[] CunningIndex = { -1, -1, -1, -1, 1, 2, 3, 4, -1 };

		public static readonly short[] CunningStep =
		{
			1, 1, 1, 1, 2, 2, 3, 3, 4, 5,
			6, 7, 8, 10, 12, 14, 16, 20, 24, 28,
			32, 40, 48, 56, 64, 80, 96, 112, 128, 160,
			192, 224, 256, 320, 384, 448, 512, 640, 768, 896,
			1024, 1280, 1536, 1792, 2048, 2560, 3072, 3584, 4096, 5120,
			6144, 7168, 8192, 10240, 12288, 14336, 16384, 20480, 24576, 28672, 0
		};

		public static readonly short[] OkiStep =
		{
			16, 17, 19, 21, 23, 25, 28, 31, 34, 37,
			41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
			107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
			279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
			724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552
		};

		public static readonly sbyte[] MtfIndex =
		{
			8, 6, 4, 2, -1, -1, -1, -1,
			-1, -1, -1, -1, 2, 4, 6, 8
		};

		public static readonly sbyte[] ZorkIndex = { -1, -1, -1, 1, 4, 7, 10, 12 };

		public static readonly sbyte[,] Xa =
		{
			{ 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 }
		};

		public static readonly short[] Ea =
		{
			0, 240, 460, 392,
			0, 0, -208, -220,
			0, 1, 3, 4,
			7, 8, 10, 11,
			0, -1, -3, -4
		};

		public static readonly short[,] Afc =
		{
			{ 0, 2048, 0, 1024, 4096, 3584, 3072, 4608, 4200, 4800, 5120, 2048, 1024, -1024, -1024, -2048 },
			{ 0, 0, 2048, 1024, -2048, -1536, -1024, -2560, -2248, -2300, -3072, -2048, -1024, 1024, 0, 0 }
		};

		public static readonly short[] MtafStep =
		{
			1, 5, 9, 13, 16, 20, 24, 28, -1, -5, -9, -13, -16, -20, -24, -28,
			2, 6, 11, 15, 20, 24, 29, 33, -2, -6, -11, -15, -20, -24, -29, -33,
			2, 7, 13, 18, 23, 28, 34, 39, -2, -7, -13, -18, -23, -28, -34, -39,
			3, 9, 15, 21, 28, 34, 40, 46, -3, -9, -15, -21, -28, -34, -40, -46,
			3, 11, 18, 26, 33, 41, 48, 56, -3, -11, -18, -26, -33, -41, -48, -56,
			4, 13, 22, 31, 40, 49, 58, 67, -4, -13, -22, -31, -40, -49, -58, -67,
			5, 16, 26, 37, 48, 59, 69, 80, -5, -16, -26, -37, -48, -59, -69, -80,
			6, 19, 31, 44, 57, 70, 82, 95, -6, -19, -31, -44, -57, -70, -82, -95,
			7, 22, 38, 53, 68, 83, 99, 114, -7, -22, -38, -53, -68, -83, -99, -114,
			9, 27, 45, 63, 81, 99, 117, 135, -9, -27, -45, -63, -81, -99, -117, -135,
			10, 32, 53, 75, 96, 118, 139, 161, -10, -32, -53, -75, -96, -118, -139, -161,
			12, 38, 64, 90, 115, 141, 167, 193, -12, -38, -64, -90, -115, -141, -167, -193,
			15, 45, 76, 106, 137, 167, 198, 228, -15, -45, -76, -106, -137, -167, -198, -228,
			18, 54, 91, 127, 164, 200, 237, 273, -18, -54, -91, -127, -164, -200, -237, -273,
			21, 65, 108, 152, 195, 239, 282, 326, -21, -65, -108, -152, -195, -239, -282, -326,
			25, 77, 129, 181, 232, 284, 336, 388, -25, -77, -129, -181, -232, -284, -336, -388,
			30, 92, 153, 215, 276, 338, 399, 461, -30, -92, -153, -215, -276, -338, -399, -461,
			36, 109, 183, 256, 329, 402, 476, 549, -36, -109, -183, -256, -329, -402, -476, -549,
			43, 130, 218, 305, 392, 479, 567, 654, -43, -130, -218, -305, -392, -479, -567, -654,
			52, 156, 260, 364, 468, 572, 676, 780, -52, -156, -260, -364, -468, -572, -676, -780,
			62, 186, 310, 434, 558, 682, 806, 930, -62, -186, -310, -434, -558, -682, -806, -930,
			73, 221, 368, 516, 663, 811, 958, 1106, -73, -221, -368, -516, -663, -811, -958, -1106,
			87, 263, 439, 615, 790, 966, 1142, 1318, -87, -263, -439, -615, -790, -966, -1142, -1318,
			104, 314, 523, 733, 942, 1152, 1361, 1571, -104, -314, -523, -733, -942, -1152, -1361, -1571,
			124, 374, 623, 873, 1122, 1372, 1621, 1871, -124, -374, -623, -873, -1122, -1372, -1621, -1871,
			148, 445, 743, 1040, 1337, 1634, 1932, 2229, -148, -445, -743, -1040, -1337, -1634, -1932, -2229,
			177, 531, 885, 1239, 1593, 1947, 2301, 2655, -177, -531, -885, -1239, -1593, -1947, -2301, -2655,
			210, 632, 1053, 1475, 1896, 2318, 2739, 3161, -210, -632, -1053, -1475, -1896, -2318, -2739, -3161,
			251, 753, 1255, 1757, 2260, 2762, 3264, 3766, -251, -753, -1255, -1757, -2260, -2762, -3264, -3766,
			299, 897, 1495, 2093, 2692, 3290, 3888, 4486, -299, -897, -1495, -2093, -2692, -3290, -3888, -4486,
			356, 1068, 1781, 2493, 3206, 3918, 4631, 5343, -356, -1068, -1781, -2493, -3206, -3918, -4631, -5343,
			424, 1273, 2121, 2970, 3819, 4668, 5516, 6365, -424, -1273, -2121, -2970, -3819, -4668, -5516, -6365
		};

		public static readonly sbyte[][] ImaIndexTables =
		{
			new sbyte[] { -1, 2, -1, 2 },
			new sbyte[] { -1, -1, 1, 2, -1, -1, 1, 2 },
			Index,
			new sbyte[]
			{
				-1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16,
				-1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 4, 6, 8, 10, 13, 16
			}
		};
	}
}
