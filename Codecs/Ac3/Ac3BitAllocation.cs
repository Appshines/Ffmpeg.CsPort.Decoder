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

namespace Ffmpeg.CsPort.Decoder.Codecs.Ac3
{
	/// <summary>
	/// Ports FFmpeg's scalar AC-3 power-spectral-density, masking, and bit-allocation-pointer calculations.
	/// </summary>
	internal static class Ac3BitAllocation
	{
		public static void CalculatePowerSpectralDensity(sbyte[] exponents, int start, int end, short[] powerSpectralDensity, short[] bandPowerSpectralDensity)
		{
			for (var bin = start; bin < end; bin++) powerSpectralDensity[bin] = (short)(3072 - (exponents[bin] << 7));
			var currentBin = start;
			var band = Ac3Tables.BinToBand[start];
			do
			{
				var value = (int)powerSpectralDensity[currentBin++];
				var bandEnd = Math.Min(Ac3Tables.BandStarts[band + 1], end);
				for (; currentBin < bandEnd; currentBin++)
				{
					var maximum = Math.Max(value, powerSpectralDensity[currentBin]);
					var address = Math.Min(maximum - ((value + powerSpectralDensity[currentBin] + 1) >> 1), 255);
					value = maximum + Ac3Tables.LogAdd[address];
				}
				bandPowerSpectralDensity[band++] = (short)value;
			} while (end > Ac3Tables.BandStarts[band]);
		}

		/// <summary>
		/// Reproduces FFmpeg's low-frequency compensation, fast/slow leakage, hearing threshold, and delta-allocation schedule.
		/// </summary>
		public static int CalculateMask(
			ref Ac3BitAllocationParameters parameters,
			short[] bandPowerSpectralDensity,
			int start,
			int end,
			int fastGain,
			bool isLowFrequencyEffects,
			int deltaMode,
			int deltaSegmentCount,
			byte[] deltaOffsets,
			byte[] deltaLengths,
			byte[] deltaValues,
			short[] mask)
		{
			if (end <= 0) return FfmpegError.InvalidData;
			Span<short> excitation = stackalloc short[50];
			var bandStart = Ac3Tables.BinToBand[start];
			var bandEnd = Ac3Tables.BinToBand[end - 1] + 1;
			var begin = 0;
			var lowCompensation = 0;
			var fastLeak = 0;
			var slowLeak = 0;
			if (bandStart == 0)
			{
				lowCompensation = CalculateLowCompensation1(lowCompensation, bandPowerSpectralDensity[0], bandPowerSpectralDensity[1], 384);
				excitation[0] = (short)(bandPowerSpectralDensity[0] - fastGain - lowCompensation);
				lowCompensation = CalculateLowCompensation1(lowCompensation, bandPowerSpectralDensity[1], bandPowerSpectralDensity[2], 384);
				excitation[1] = (short)(bandPowerSpectralDensity[1] - fastGain - lowCompensation);
				begin = 7;
				for (var band = 2; band < 7; band++)
				{
					if (!(isLowFrequencyEffects && band == 6)) lowCompensation = CalculateLowCompensation1(lowCompensation, bandPowerSpectralDensity[band], bandPowerSpectralDensity[band + 1], 384);
					fastLeak = bandPowerSpectralDensity[band] - fastGain;
					slowLeak = bandPowerSpectralDensity[band] - parameters.SlowGain;
					excitation[band] = (short)(fastLeak - lowCompensation);
					if (!(isLowFrequencyEffects && band == 6) && bandPowerSpectralDensity[band] <= bandPowerSpectralDensity[band + 1])
					{
						begin = band + 1;
						break;
					}
				}
				var firstEnd = Math.Min(bandEnd, 22);
				for (var band = begin; band < firstEnd; band++)
				{
					if (!(isLowFrequencyEffects && band == 6)) lowCompensation = CalculateLowCompensation(lowCompensation, bandPowerSpectralDensity[band], bandPowerSpectralDensity[band + 1], band);
					fastLeak = Math.Max(fastLeak - parameters.FastDecay, bandPowerSpectralDensity[band] - fastGain);
					slowLeak = Math.Max(slowLeak - parameters.SlowDecay, bandPowerSpectralDensity[band] - parameters.SlowGain);
					excitation[band] = (short)Math.Max(fastLeak - lowCompensation, slowLeak);
				}
				begin = 22;
			} else
			{
				begin = bandStart;
				fastLeak = (parameters.CouplingFastLeak << 8) + 768;
				slowLeak = (parameters.CouplingSlowLeak << 8) + 768;
			}

			for (var band = begin; band < bandEnd; band++)
			{
				fastLeak = Math.Max(fastLeak - parameters.FastDecay, bandPowerSpectralDensity[band] - fastGain);
				slowLeak = Math.Max(slowLeak - parameters.SlowDecay, bandPowerSpectralDensity[band] - parameters.SlowGain);
				excitation[band] = (short)Math.Max(fastLeak, slowLeak);
			}
			for (var band = bandStart; band < bandEnd; band++)
			{
				var difference = parameters.DecibelsPerBit - bandPowerSpectralDensity[band];
				if (difference > 0) excitation[band] = (short)(excitation[band] + (difference >> 2));
				mask[band] = (short)Math.Max(Ac3Tables.HearingThreshold[band >> parameters.SampleRateShift, parameters.SampleRateCode], excitation[band]);
			}
			if (deltaMode == 0 || deltaMode == 1)
			{
				if (deltaSegmentCount > 8) return -1;
				var band = bandStart;
				for (var segment = 0; segment < deltaSegmentCount; segment++)
				{
					band += deltaOffsets[segment];
					if (band >= 50 || deltaLengths[segment] > 50 - band) return -1;
					var delta = deltaValues[segment] >= 4 ? (deltaValues[segment] - 3) * 128 : (deltaValues[segment] - 4) * 128;
					for (var index = 0; index < deltaLengths[segment]; index++) mask[band++] += (short)delta;
				}
			}
			return 0;
		}

		public static void CalculatePointers(short[] mask, short[] powerSpectralDensity, int start, int end, int signalToNoiseOffset, int floor, byte[] pointerTable, byte[] pointers)
		{
			if (signalToNoiseOffset == -960)
			{
				Array.Clear(pointers, 0, 256);
				return;
			}
			var bin = start;
			var band = Ac3Tables.BinToBand[start];
			int bandEnd;
			do
			{
				var value = (Math.Max(mask[band] - signalToNoiseOffset - floor, 0) & 0x1fe0) + floor;
				bandEnd = Math.Min(Ac3Tables.BandStarts[++band], end);
				for (; bin < bandEnd; bin++)
				{
					var address = (powerSpectralDensity[bin] - value) >> 5;
					if (address < 0) address = 0; else if (address > 63) address = 63;
					pointers[bin] = pointerTable[address];
				}
			} while (end > bandEnd);
		}

		private static int CalculateLowCompensation1(int value, int first, int second, int replacement)
		{
			if (first + 256 == second) value = replacement;
			else if (first > second) value = Math.Max(value - 64, 0);
			return value;
		}

		private static int CalculateLowCompensation(int value, int first, int second, int bin)
		{
			if (bin < 7) return CalculateLowCompensation1(value, first, second, 384);
			if (bin < 20) return CalculateLowCompensation1(value, first, second, 320);
			return Math.Max(value - 128, 0);
		}
	}
}
