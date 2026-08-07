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
using Ffmpeg.CsPort.Decoder.Codecs.Aac;

namespace Ffmpeg.CsPort.Decoder.Codecs.Wma
{
	/// <summary>Owns the WMA v1/v2 canonical and sparse VLCs plus their FFmpeg run/level expansion.</summary>
	internal static partial class WmaTables
	{
		internal static readonly ushort[] CriticalFrequencies =
		{
			100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000,
			2320, 2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 24500
		};

		internal static readonly Vlc ExponentVlc;
		internal static readonly Vlc HighGainVlc;
		internal static readonly WmaCoefficientTable[] CoefficientVlcs = new WmaCoefficientTable[6];

		static WmaTables()
		{
			ExponentVlc = new Vlc();
			if (ExponentVlc.InitializeSparse(8, AacTables.ScaleFactorBits, AacTables.ScaleFactorCodes) < 0)
				throw new InvalidOperationException("FFmpeg WMA exponent VLC initialization failed.");
			HighGainVlc = new Vlc();
			if (HighGainVlc.InitializeFromLengths(9, HighGainLengths, HighGainSymbols, -18) < 0)
				throw new InvalidOperationException("FFmpeg WMA high-band gain VLC initialization failed.");
			for (var tableIndex = 0; tableIndex < CoefficientVlcs.Length; tableIndex++)
			{
				var vlc = new Vlc();
				if (vlc.InitializeSparse(9, CoefficientBits[tableIndex], CoefficientCodes[tableIndex]) < 0)
					throw new InvalidOperationException("FFmpeg WMA coefficient VLC initialization failed.");
				var run = new ushort[CoefficientBits[tableIndex].Length];
				var level = new float[CoefficientBits[tableIndex].Length];
				var code = 2;
				var currentLevel = 1;
				var levelIndex = 0;
				while (code < run.Length)
				{
					var count = CoefficientLevels[tableIndex][levelIndex++];
					for (var runValue = 0; runValue < count; runValue++)
					{
						run[code] = (ushort)runValue;
						level[code] = currentLevel;
						code++;
					}
					currentLevel++;
				}
				CoefficientVlcs[tableIndex] = new WmaCoefficientTable(vlc, run, level);
			}
		}
	}

	/// <summary>
	/// Couples one WMA coefficient VLC with the run and level values addressed by its symbols.
	/// </summary>
	internal sealed class WmaCoefficientTable
	{
		public WmaCoefficientTable(Vlc vlc, ushort[] run, float[] level)
		{
			Vlc = vlc;
			Run = run;
			Level = level;
		}

		public Vlc Vlc { get; }
		public ushort[] Run { get; }
		public float[] Level { get; }
	}
}
