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

namespace Ffmpeg.CsPort.Decoder.Codecs.Dca
{
	/// <summary>
	/// Defines FFmpeg's DTS/DCA structural constants and constructs its scalar VLC lookup tables once.
	/// </summary>
	internal static partial class DcaTables
	{
		internal static readonly int[] SampleRates = { 0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 96000, 192000 };
		internal static readonly int[] SamplingFrequencies = { 8000, 16000, 32000, 64000, 128000, 22050, 44100, 88200, 176400, 352800, 12000, 24000, 48000, 96000, 192000, 384000 };
		internal static readonly byte[] FrequencyRanges = { 0, 1, 2, 3, 4, 1, 2, 3, 4, 4, 0, 1, 2, 3, 4, 4 };
		internal static readonly byte[] BitsPerSample = { 16, 16, 20, 20, 0, 24, 24, 0 };
		internal static readonly sbyte[] PrimaryChannelToSpeaker =
		{
			0, -1, -1, -1, -1,
			1, 2, -1, -1, -1,
			1, 2, -1, -1, -1,
			1, 2, -1, -1, -1,
			1, 2, -1, -1, -1,
			0, 1, 2, -1, -1,
			1, 2, 6, -1, -1,
			0, 1, 2, 6, -1,
			1, 2, 3, 4, -1,
			0, 1, 2, 3, 4
		};
		internal static readonly int[] AudioModeChannelMasks = { 1, 6, 6, 6, 6, 7, 70, 71, 30, 31 };
		internal static readonly byte[] BlockCodeBits = { 7, 10, 12, 13, 15, 17, 19 };
		internal static readonly byte[] DcaToWaveNormal = { 2, 0, 1, 9, 10, 3, 8, 4, 5, 9, 10, 6, 7, 12, 13, 14, 3, 6, 7, 11, 12, 14, 16, 15, 17, 8, 4, 5 };
		internal static readonly byte[] DcaToWaveWide = { 2, 0, 1, 4, 5, 3, 8, 4, 5, 9, 10, 6, 7, 12, 13, 14, 3, 9, 10, 11, 12, 14, 16, 15, 17, 8, 4, 5 };

		internal static readonly Vlc[][] QuantIndexVlc = CreateVlcMatrix(10, 7);
		internal static readonly Vlc[] BitAllocationVlc = CreateVlcArray(5);
		internal static readonly Vlc[] ScaleFactorVlc = CreateVlcArray(5);
		internal static readonly Vlc[] TransitionModeVlc = CreateVlcArray(4);
		internal static readonly Vlc[] TonalGroupVlc = CreateVlcArray(5);
		internal static readonly Vlc TonalScaleFactorVlc;
		internal static readonly Vlc DampingVlc;
		internal static readonly Vlc PhaseDifferenceVlc;
		internal static readonly Vlc FirstResidualAmplitudeVlc;
		internal static readonly Vlc ResidualApproximationVlc;
		internal static readonly Vlc ResidualAmplitudeVlc;
		internal static readonly Vlc AverageGroupThreeVlc;
		internal static readonly Vlc StereoGridVlc;
		internal static readonly Vlc GridTwoVlc;
		internal static readonly Vlc GridThreeVlc;
		internal static readonly Vlc ResidualVlc;

		static DcaTables()
		{
			var sourceOffset = 0;
			for (var codebook = 0; codebook < 10; codebook++)
			{
				for (var group = 0; group < QuantIndexGroupSize[codebook]; group++)
					InitializeVlc(QuantIndexVlc[codebook][group], BitallocMaxbits[codebook * 7 + group], BitallocSizes[codebook], BitallocOffsets[codebook], false, ref sourceOffset);
			}
			for (var index = 0; index < BitAllocationVlc.Length; index++) InitializeVlc(BitAllocationVlc[index], Bitalloc12VlcBits[index], 12, 1, false, ref sourceOffset);
			for (var index = 0; index < ScaleFactorVlc.Length; index++) InitializeVlc(ScaleFactorVlc[index], 9, 129, -64, false, ref sourceOffset);
			for (var index = 0; index < TransitionModeVlc.Length; index++) InitializeVlc(TransitionModeVlc[index], 3, 4, 0, false, ref sourceOffset);
			for (var index = 0; index < TonalGroupVlc.Length; index++) InitializeVlc(TonalGroupVlc[index], 9, TnlGrpSizes[index], -1, true, ref sourceOffset);
			TonalScaleFactorVlc = CreateVlc(9, 20, -1, true, ref sourceOffset);
			DampingVlc = CreateVlc(6, 7, -1, true, ref sourceOffset);
			PhaseDifferenceVlc = CreateVlc(6, 9, -1, true, ref sourceOffset);
			FirstResidualAmplitudeVlc = CreateVlc(9, 24, -1, true, ref sourceOffset);
			ResidualApproximationVlc = CreateVlc(5, 6, -1, true, ref sourceOffset);
			ResidualAmplitudeVlc = CreateVlc(9, 33, -1, true, ref sourceOffset);
			AverageGroupThreeVlc = CreateVlc(9, 18, -1, true, ref sourceOffset);
			StereoGridVlc = CreateVlc(9, 22, -1, true, ref sourceOffset);
			GridTwoVlc = CreateVlc(9, 20, -1, true, ref sourceOffset);
			GridThreeVlc = CreateVlc(9, 13, -1, true, ref sourceOffset);
			ResidualVlc = CreateVlc(6, 9, 0, true, ref sourceOffset);
			if (sourceOffset * 2 != VlcSrcTables.Length) throw new InvalidOperationException("Invalid DTS VLC source table length.");
		}

		private static Vlc CreateVlc(int rootBits, int count, int symbolOffset, bool littleEndian, ref int sourceOffset)
		{
			var result = new Vlc();
			InitializeVlc(result, rootBits, count, symbolOffset, littleEndian, ref sourceOffset);
			return result;
		}

		private static void InitializeVlc(Vlc vlc, int rootBits, int count, int symbolOffset, bool littleEndian, ref int sourceOffset)
		{
			var lengths = new sbyte[count];
			var symbols = new short[count];
			for (var index = 0; index < count; index++)
			{
				symbols[index] = (short)VlcSrcTables[(sourceOffset + index) * 2];
				lengths[index] = (sbyte)VlcSrcTables[(sourceOffset + index) * 2 + 1];
			}
			var flags = VlcFlags.StaticOverlong | (littleEndian ? VlcFlags.LittleEndian : VlcFlags.None);
			if (vlc.InitializeFromLengths(rootBits, lengths, symbols, symbolOffset, flags) < 0) throw new InvalidOperationException("Invalid DTS VLC table.");
			sourceOffset += count;
		}

		private static Vlc[] CreateVlcArray(int length)
		{
			var result = new Vlc[length];
			for (var index = 0; index < length; index++) result[index] = new Vlc();
			return result;
		}

		private static Vlc[][] CreateVlcMatrix(int rows, int columns)
		{
			var result = new Vlc[rows][];
			for (var row = 0; row < rows; row++) result[row] = CreateVlcArray(columns);
			return result;
		}
	}
}
