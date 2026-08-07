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

namespace Ffmpeg.CsPort.Decoder.Codecs.Vorbis
{
	/// <summary>
	/// Exposes the literal FFmpeg 8.1.2 Vorbis windows, inverse-dB values, channel permutation, and reciprocal table.
	/// </summary>
	internal static partial class VorbisTables
	{
		internal static readonly int[][] ChannelLayoutOffsets =
		{
			new[] { 0 },
			new[] { 0, 1 },
			new[] { 0, 2, 1 },
			new[] { 0, 1, 2, 3 },
			new[] { 0, 2, 1, 3, 4 },
			new[] { 0, 2, 1, 5, 3, 4 },
			new[] { 0, 2, 1, 6, 5, 3, 4 },
			new[] { 0, 2, 1, 7, 5, 6, 3, 4 }
		};

		internal static float[] GetWindow(int exponent)
		{
			return exponent switch
			{
				6 => Vwin64,
				7 => Vwin128,
				8 => Vwin256,
				9 => Vwin512,
				10 => Vwin1024,
				11 => Vwin2048,
				12 => Vwin4096,
				13 => Vwin8192,
				_ => throw new ArgumentOutOfRangeException(nameof(exponent))
			};
		}
	}
}
