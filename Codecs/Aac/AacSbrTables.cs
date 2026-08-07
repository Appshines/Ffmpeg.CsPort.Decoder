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
using Ffmpeg.CsPort.Decoder.Bitstream;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Owns FFmpeg's scalar SBR QMF, noise, frequency-offset, and canonical Huffman tables.</summary>
	internal static partial class AacSbrTables
	{
		internal static readonly Vlc[] HuffmanVlcs = new Vlc[10];

		static AacSbrTables()
		{
			var tableOffset = 0;
			for (var tableIndex = 0; tableIndex < HuffmanVlcs.Length; tableIndex++)
			{
				var count = HuffmanCodeCounts[tableIndex];
				var lengths = new sbyte[count];
				var symbols = new short[count];
				Array.Copy(HuffmanLengths, tableOffset, lengths, 0, count);
				Array.Copy(HuffmanSymbols, tableOffset, symbols, 0, count);
				var vlc = new Vlc();
				if (vlc.InitializeFromLengths(9, lengths, symbols, HuffmanOffsets[tableIndex]) < 0)
					throw new InvalidOperationException("FFmpeg AAC SBR VLC initialization failed.");
				HuffmanVlcs[tableIndex] = vlc;
				tableOffset += count;
			}
		}
	}
}
