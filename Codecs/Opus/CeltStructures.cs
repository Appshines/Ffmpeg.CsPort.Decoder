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
using Ffmpeg.CsPort.Decoder.Transforms;

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	internal enum CeltSpread
	{
		None,
		Light,
		Normal,
		Aggressive
	}

	/// <summary>
	/// Holds one CELT channel's decoded band energies, coefficients, and postfilter history.
	/// </summary>
	internal sealed class CeltBlock
	{
		public readonly float[] Energy = new float[21];
		public readonly float[] LinearEnergy = new float[21];
		public readonly float[] ErrorEnergy = new float[21];
		public readonly float[] PreviousEnergy = new float[42];
		public readonly byte[] CollapseMasks = new byte[21];
		public readonly float[] Buffer = new float[2048];
		public readonly float[] Coefficients = new float[960];
		public readonly float[] Overlap = new float[120];
		public readonly float[] Samples = new float[960];
		public int PostfilterPeriodNew;
		public readonly float[] PostfilterGainsNew = new float[3];
		public int PostfilterPeriod;
		public readonly float[] PostfilterGains = new float[3];
		public int PostfilterPeriodOld;
		public readonly float[] PostfilterGainsOld = new float[3];
		public float EmphasisCoefficient;
	}

	/// <summary>
	/// Holds packet-wide CELT allocation, stereo, transient, and band-decoding state.
	/// </summary>
	internal sealed class CeltFrame
	{
		public readonly CeltBlock[] Blocks = { new CeltBlock(), new CeltBlock() };
		public readonly FfmpegFloatMdct[] Mdct = new FfmpegFloatMdct[4];
		public readonly int[] AllocationBoost = new int[21];
		public readonly int[] Caps = new int[21];
		public readonly int[] FineBits = new int[21];
		public readonly int[] FinePriority = new int[21];
		public readonly int[] Pulses = new int[21];
		public readonly int[] TfChange = new int[21];
		public int Channels;
		public int OutputChannels;
		public bool ApplyPhaseInversion;
		public int Size;
		public int StartBand;
		public int EndBand;
		public int CodedBands;
		public int Transient;
		public int Postfilter;
		public int SkipBandFloor;
		public int TfSelect;
		public int AllocationTrim;
		public int BlocksCount;
		public int BlockSize;
		public int Silence;
		public int AnticollapseNeeded;
		public int Anticollapse;
		public int IntensityStereo;
		public int DualStereo;
		public bool Flushed;
		public uint Seed;
		public CeltSpread Spread;
		public int PostfilterOctave;
		public int PostfilterPeriod;
		public int PostfilterTapset;
		public float PostfilterGain;
		public int FrameBits;
		public int Remaining;
		public int Remaining2;

		public uint NextRandom()
		{
			Seed = unchecked(1664525 * Seed + 1013904223);
			return Seed;
		}
	}
}
