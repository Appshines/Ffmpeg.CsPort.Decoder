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
namespace Ffmpeg.CsPort.Decoder.Codecs.Ape
{
	/// <summary>
	/// Stores the immutable entropy and adaptive-filter tables from FFmpeg's apedec.c.
	/// </summary>
	internal static class ApeTables
	{
		public static readonly ushort[,] FilterOrders =
		{
			{ 0, 0, 0 },
			{ 16, 0, 0 },
			{ 64, 0, 0 },
			{ 32, 256, 0 },
			{ 16, 256, 1280 }
		};

		public static readonly byte[,] FilterFractionBits =
		{
			{ 0, 0, 0 },
			{ 11, 0, 0 },
			{ 11, 0, 0 },
			{ 10, 13, 0 },
			{ 11, 13, 15 }
		};

		public static readonly ushort[] Counts3970 =
		{
			0, 14824, 28224, 39348, 47855, 53994, 58171, 60926,
			62682, 63786, 64463, 64878, 65126, 65276, 65365, 65419,
			65450, 65469, 65480, 65487, 65491, 65493
		};

		public static readonly ushort[] CountsDifference3970 =
		{
			14824, 13400, 11124, 8507, 6139, 4177, 2755, 1756,
			1104, 677, 415, 248, 150, 89, 54, 31, 19, 11, 7, 4, 2
		};

		public static readonly ushort[] Counts3980 =
		{
			0, 19578, 36160, 48417, 56323, 60899, 63265, 64435,
			64971, 65232, 65351, 65416, 65447, 65466, 65476, 65482,
			65485, 65488, 65490, 65491, 65492, 65493
		};

		public static readonly ushort[] CountsDifference3980 =
		{
			19578, 16582, 12257, 7906, 4576, 2366, 1170, 536,
			261, 119, 65, 31, 19, 10, 6, 3, 3, 2, 1, 1, 1
		};
	}
}
