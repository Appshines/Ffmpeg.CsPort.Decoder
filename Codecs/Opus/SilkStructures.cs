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
namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>
	/// Stores one SILK channel's subframe parameters, synthesis output, and LPC history.
	/// </summary>
	internal sealed class SilkFrame
	{
		public bool Coded;
		public int LogGain;
		public readonly short[] NormalizedLsf = new short[16];
		public readonly float[] Lpc = new float[16];
		public readonly float[] Output = new float[644];
		public readonly float[] LpcHistory = new float[644];
		public int PrimaryLag;
		public bool PreviousVoiced;
	}

	/// <summary>
	/// Stores the pitch, gain, LTP, and LPC parameters decoded for one SILK subframe.
	/// </summary>
	internal sealed class SilkSubframe
	{
		public float Gain;
		public int PitchLag;
		public readonly float[] LtpTaps = new float[5];
	}
}
