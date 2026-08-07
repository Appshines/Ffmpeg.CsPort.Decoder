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
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Bitstream
{
	/// <summary>
	/// Preserves the literal FFmpeg Golomb lookup tables; they must not be replaced with runtime generation.
	/// </summary>
	internal static class GolombTables
	{
		internal static readonly byte[] GolombVlcLength =
		{
			19,17,15,15,13,13,13,13,11,11,11,11,11,11,11,11,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,
			7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
			5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,
			5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1
		};

		internal static readonly byte[] UnsignedGolombVlcCode =
		{
			32,32,32,32,32,32,32,32,31,32,32,32,32,32,32,32,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,
			 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9,10,10,10,10,11,11,11,11,12,12,12,12,13,13,13,13,14,14,14,14,
			 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
			 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
			 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
			 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
		};

		internal static readonly sbyte[] SignedGolombVlcCode =
		{
			17, 17, 17, 17, 17, 17, 17, 17, 16, 17, 17, 17, 17, 17, 17, 17,  8, -8,  9, -9, 10,-10, 11,-11, 12,-12, 13,-13, 14,-14, 15,-15,
			  4,  4,  4,  4, -4, -4, -4, -4,  5,  5,  5,  5, -5, -5, -5, -5,  6,  6,  6,  6, -6, -6, -6, -6,  7,  7,  7,  7, -7, -7, -7, -7,
			  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2, -2,
			  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3, -3, -3, -3, -3, -3, -3, -3, -3, -3, -3, -3, -3, -3, -3, -3, -3,
			  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
			  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
			 -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
			 -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
		};

		internal static readonly byte[] UnsignedGolombLength =
		{
			1, 3, 3, 5, 5, 5, 5, 7, 7, 7, 7, 7, 7, 7, 7, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,11,
			11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,13,
			13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,
			13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,13,15,
			15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,
			15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,
			15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,
			15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,17,
		};

		internal static readonly byte[] InterleavedGolombVlcLength =
		{
			9,9,7,7,9,9,7,7,5,5,5,5,5,5,5,5,
			9,9,7,7,9,9,7,7,5,5,5,5,5,5,5,5,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			9,9,7,7,9,9,7,7,5,5,5,5,5,5,5,5,
			9,9,7,7,9,9,7,7,5,5,5,5,5,5,5,5,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
			1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
		};

		internal static readonly byte[] InterleavedUnsignedGolombVlcCode =
		{
			15,16,7, 7, 17,18,8, 8, 3, 3, 3, 3, 3, 3, 3, 3,
			 19,20,9, 9, 21,22,10,10,4, 4, 4, 4, 4, 4, 4, 4,
			 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			 23,24,11,11,25,26,12,12,5, 5, 5, 5, 5, 5, 5, 5,
			 27,28,13,13,29,30,14,14,6, 6, 6, 6, 6, 6, 6, 6,
			 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
			 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
		};

		internal static readonly sbyte[] InterleavedSignedGolombVlcCode =
		{
			8, -8,  4,  4,  9, -9, -4, -4,  2,  2,  2,  2,  2,  2,  2,  2,
			 10,-10,  5,  5, 11,-11, -5, -5, -2, -2, -2, -2, -2, -2, -2, -2,
			  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
			  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
			 12,-12,  6,  6, 13,-13, -6, -6,  3,  3,  3,  3,  3,  3,  3,  3,
			 14,-14,  7,  7, 15,-15, -7, -7, -3, -3, -3, -3, -3, -3, -3, -3,
			 -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
			 -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
			  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
		};

		internal static readonly byte[] InterleavedDiracGolombVlcCode =
		{
			0, 1, 0, 0, 2, 3, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
			4, 5, 2, 2, 6, 7, 3, 3, 1, 1, 1, 1, 1, 1, 1, 1,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			8, 9, 4, 4, 10,11,5, 5, 2, 2, 2, 2, 2, 2, 2, 2,
			12,13,6, 6, 14,15,7, 7, 3, 3, 3, 3, 3, 3, 3, 3,
			1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
		};

	}

	/// <summary>
	/// Ports FFmpeg unsigned, signed, interleaved, Dirac, Rice, JPEG-LS, FLAC, and Shorten Golomb readers.
	/// </summary>
	internal static class GolombReader
	{
		public const int InvalidVlc = unchecked((int)0x80000000U);

		public static int ReadUnsigned(BitReader reader)
		{
			var buffer = reader.ShowBitsLong(32);
			if (buffer >= 1U << 27)
			{
				buffer >>= 32 - 9;
				reader.SkipBits(GolombTables.GolombVlcLength[buffer]);
				return GolombTables.UnsignedGolombVlcCode[buffer];
			}

			var log = 2 * FfmpegMath.Log2(buffer) - 31;
			reader.SkipBits(32 - log);
			if (log < 7)
			{
				return FfmpegError.InvalidData;
			}
			buffer >>= log;
			buffer--;
			return (int)buffer;
		}

		public static uint ReadUnsignedLong(BitReader reader)
		{
			var buffer = reader.ShowBitsLong(32);
			var log = 31 - FfmpegMath.Log2(buffer);
			reader.SkipBits(log);
			return reader.ReadBitsLong(log + 1) - 1;
		}

		public static int ReadUnsigned31(BitReader reader)
		{
			var buffer = reader.ShowBitsLong(32) >> (32 - 9);
			reader.SkipBits(GolombTables.GolombVlcLength[buffer]);
			return GolombTables.UnsignedGolombVlcCode[buffer];
		}

		public static int ReadSigned(BitReader reader)
		{
			var buffer = reader.ShowBitsLong(32);
			if (buffer >= 1U << 27)
			{
				buffer >>= 32 - 9;
				reader.SkipBits(GolombTables.GolombVlcLength[buffer]);
				return GolombTables.SignedGolombVlcCode[buffer];
			}

			var log = 2 * FfmpegMath.Log2(buffer) - 31;
			buffer >>= log;
			reader.SkipBits(32 - log);
			if ((buffer & 1) != 0)
			{
				buffer = unchecked((uint)-(int)(buffer >> 1));
			} else
			{
				buffer >>= 1;
			}
			return unchecked((int)buffer);
		}

		public static int ReadSignedLong(BitReader reader)
		{
			var buffer = ReadUnsignedLong(reader);
			var sign = (int)(buffer & 1) - 1;
			return unchecked((int)(((buffer >> 1) ^ (uint)sign) + 1));
		}

		public static uint ReadInterleavedUnsigned(BitReader reader)
		{
			var buffer = reader.ShowBitsLong(32);
			if ((buffer & 0xaa800000U) != 0)
			{
				buffer >>= 32 - 8;
				reader.SkipBits(GolombTables.InterleavedGolombVlcLength[buffer]);
				return GolombTables.InterleavedUnsignedGolombVlcCode[buffer];
			}

			uint result = 1;
			do
			{
				buffer >>= 32 - 8;
				var length = (int)GolombTables.InterleavedGolombVlcLength[buffer];
				reader.SkipBits(Math.Min(length, 8));
				if (length != 9)
				{
					result <<= (length - 1) >> 1;
					result |= GolombTables.InterleavedDiracGolombVlcCode[buffer];
					break;
				}
				result = result << 4 | GolombTables.InterleavedDiracGolombVlcCode[buffer];
				buffer = reader.ShowBitsLong(32);
			} while (reader.BitsLeft > 0);
			return result - 1;
		}

		public static int ReadInterleavedSigned(BitReader reader)
		{
			var buffer = reader.ShowBitsLong(32);
			if ((buffer & 0xaa800000U) != 0)
			{
				buffer >>= 32 - 8;
				reader.SkipBits(GolombTables.InterleavedGolombVlcLength[buffer]);
				return GolombTables.InterleavedSignedGolombVlcCode[buffer];
			}

			reader.SkipBits(8);
			buffer |= 1U | reader.ShowBits(24);
			if ((buffer & 0xaaaaaaaaU) == 0)
			{
				return InvalidVlc;
			}

			var log = 31;
			for (; (buffer & 0x80000000U) == 0; log--)
			{
				buffer = unchecked((buffer << 2) - ((buffer << log) >> (log - 1)) + (buffer >> 30));
			}
			reader.SkipBits(63 - 2 * log - 8);
			var signed = unchecked((int)((((buffer << log) >> log) - 1) ^ unchecked((uint)-(int)(buffer & 1))) + 1);
			return signed >> 1;
		}

		public static int ReadDiracSigned(BitReader reader)
		{
			var result = ReadInterleavedUnsigned(reader);
			if (result != 0)
			{
				var sign = -(int)reader.ReadBit();
				result = unchecked((result ^ (uint)sign) - (uint)sign);
			}
			return unchecked((int)result);
		}

		public static int ReadTruncatedZero(BitReader reader, int range)
		{
			if (range < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(range));
			}
			if (range == 1)
			{
				return 0;
			}
			return range == 2 ? (int)(reader.ReadBit() ^ 1) : ReadUnsigned(reader);
		}

		public static int ReadTruncated(BitReader reader, int range)
		{
			if (range < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(range));
			}
			return range == 2 ? (int)(reader.ReadBit() ^ 1) : ReadUnsigned(reader);
		}

		public static int ReadUnsignedRice(BitReader reader, int parameter, int limit, int escapeLength)
		{
			var buffer = reader.ShowBitsLong(32);
			var log = FfmpegMath.Log2(buffer);
			if (log > 31 - limit)
			{
				buffer >>= log - parameter;
				buffer += (uint)((30 - log) << parameter);
				reader.SkipBits(32 + parameter - log);
				return unchecked((int)buffer);
			}

			reader.SkipBits(limit);
			buffer = reader.ReadBitsLong(escapeLength);
			return unchecked((int)(buffer + limit - 1));
		}

		/// <summary>
		/// Preserves the cached fast path and unary fallback of FFmpeg get_ur_golomb_jpegls.
		/// </summary>
		public static int ReadUnsignedJpegLs(BitReader reader, int parameter, int limit, int escapeLength)
		{
			var buffer = reader.ShowBitsLong(32);
			var log = FfmpegMath.Log2(buffer);
			if (log - parameter >= 1 && 32 - log < limit)
			{
				buffer >>= log - parameter;
				buffer += (uint)((30 - log) << parameter);
				reader.SkipBits(32 + parameter - log);
				return unchecked((int)buffer);
			}

			var index = 0;
			for (; index < limit && reader.ReadBit() == 0 && reader.BitsLeft > 0; index++)
			{
			}
			if (index < limit - 1)
			{
				buffer = reader.ReadBitsLong(parameter);
				return unchecked((int)(buffer + (index << parameter)));
			}
			if (escapeLength != 0 && index == limit - 1)
			{
				buffer = reader.ReadBitsLong(escapeLength);
				return unchecked((int)buffer + 1);
			}
			return -1;
		}

		public static int ReadSignedRice(BitReader reader, int parameter, int limit, int escapeLength)
		{
			var value = unchecked((uint)ReadUnsignedRice(reader, parameter, limit, escapeLength));
			return unchecked((int)((value >> 1) ^ (uint)-(int)(value & 1)));
		}

		public static int ReadSignedFlac(BitReader reader, int parameter, int limit, int escapeLength)
		{
			var value = unchecked((uint)ReadUnsignedJpegLs(reader, parameter, limit, escapeLength));
			return unchecked((int)((value >> 1) ^ (uint)-(int)(value & 1)));
		}

		public static uint ReadUnsignedShorten(BitReader reader, int parameter)
		{
			return unchecked((uint)ReadUnsignedJpegLs(reader, parameter, int.MaxValue, 0));
		}

		public static int ReadSignedShorten(BitReader reader, int parameter)
		{
			var value = ReadUnsignedJpegLs(reader, parameter + 1, int.MaxValue, 0);
			return value >> 1 ^ -(value & 1);
		}
	}
}
