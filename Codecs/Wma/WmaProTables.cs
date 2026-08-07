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

namespace Ffmpeg.CsPort.Decoder.Codecs.Wma
{
	/// <summary>Builds FFmpeg's canonical WMA Pro VLCs once and exposes the literal coefficient metadata.</summary>
	internal static partial class WmaProTables
	{
		internal static readonly Vlc ScaleVlc;
		internal static readonly Vlc ScaleRunLevelVlc;
		internal static readonly Vlc[] CoefficientVlcs = new Vlc[2];
		internal static readonly Vlc Vector4Vlc;
		internal static readonly Vlc Vector2Vlc;
		internal static readonly Vlc Vector1Vlc;
		internal static readonly float[] Sine64 = new float[33];

		static WmaProTables()
		{
			ScaleVlc = Initialize(8, ScaleLengths, ScaleSymbols, -60, "scale-factor");
			ScaleRunLevelVlc = Initialize(9, ScaleRunLevelLengths, ScaleRunLevelSymbols, 0, "scale-factor run-level");
			CoefficientVlcs[0] = Initialize(9, Coefficient0Lengths, Coefficient0Symbols, 0, "coefficient zero");
			CoefficientVlcs[1] = Initialize(9, Coefficient1Lengths, Coefficient1Symbols, 0, "coefficient one");
			Vector4Vlc = Initialize(9, Vector4Lengths, Vector4Symbols, -1, "four-vector");
			Vector2Vlc = Initialize(9, Vector2Lengths, Vector2Symbols, -1, "two-vector");
			Vector1Vlc = Initialize(9, Vector1Lengths, Vector1Symbols, 0, "scalar-vector");
			for (var index = 0; index < Sine64.Length; index++)
				Sine64[index] = (float)Math.Sin(index * Math.PI / 64.0);
		}

		private static Vlc Initialize(int rootBits, sbyte[] lengths, short[] symbols, int offset, string name)
		{
			var result = new Vlc();
			if (result.InitializeFromLengths(rootBits, lengths, symbols, offset) < 0)
				throw new InvalidOperationException("FFmpeg WMA Pro " + name + " VLC initialization failed.");
			return result;
		}
	}
}
