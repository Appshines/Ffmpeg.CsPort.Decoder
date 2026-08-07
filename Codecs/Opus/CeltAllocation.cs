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

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>Ports FFmpeg's CELT dynamic bit allocation and ordered band-PVQ dispatch.</summary>
	internal sealed class CeltAllocation
	{
		private readonly CeltPvq pvq;
		private readonly int[] boost = new int[21];
		private readonly int[] trimOffset = new int[21];
		private readonly int[] threshold = new int[21];
		private readonly int[] bits1 = new int[21];
		private readonly int[] bits2 = new int[21];
		private readonly float[] lowbandScratch = new float[176];
		private readonly float[] norm1 = new float[1600];
		private readonly float[] norm2 = new float[1600];

		public CeltAllocation(CeltPvq pvq)
		{
			this.pvq = pvq;
		}

		/// <summary>Decodes each coded frequency band with FFmpeg's folding source and remaining-bit rebalance.</summary>
		public void DecodeBands(CeltFrame frame, OpusRangeDecoder range)
		{
			Array.Clear(norm1, 0, norm1.Length);
			Array.Clear(norm2, 0, norm2.Length);
			var totalBits = (frame.FrameBits << 3) - frame.AnticollapseNeeded;
			var updateLowband = true;
			var lowbandOffset = 0;
			for (var band = frame.StartBand; band < frame.EndBand; band++)
			{
				var fullMask = (1u << frame.BlocksCount) - 1;
				var mask0 = fullMask;
				var mask1 = fullMask;
				var bandOffset = OpusTables.CeltFreqBands[band] << frame.Size;
				var bandSize = OpusTables.CeltFreqRange[band] << frame.Size;
				var consumed = (int)range.TellFraction;
				var effectiveLowband = -1;
				var allocated = 0;
				if (band != frame.StartBand) frame.Remaining -= consumed;
				frame.Remaining2 = totalBits - consumed - 1;
				if (band < frame.CodedBands)
				{
					var balance = frame.Remaining / Math.Min(3, frame.CodedBands - band);
					allocated = Math.Min(frame.Remaining2 + 1, frame.Pulses[band] + balance);
					allocated = Math.Clamp(allocated, 0, (1 << 14) - 1);
				}
				if ((OpusTables.CeltFreqBands[band] - OpusTables.CeltFreqRange[band] >= OpusTables.CeltFreqBands[frame.StartBand] || band == frame.StartBand + 1) && (updateLowband || lowbandOffset == 0))
					lowbandOffset = band;
				if (band == frame.StartBand + 1)
				{
					var count = (OpusTables.CeltFreqRange[band] - OpusTables.CeltFreqRange[band - 1]) << frame.Size;
					Array.Copy(norm1, bandOffset - count, norm1, bandOffset, count);
					if (frame.Channels == 2) Array.Copy(norm2, bandOffset - count, norm2, bandOffset, count);
				}
				if (lowbandOffset != 0 && (frame.Spread != CeltSpread.Aggressive || frame.BlocksCount > 1 || frame.TfChange[band] < 0))
				{
					effectiveLowband = Math.Max(OpusTables.CeltFreqBands[frame.StartBand], OpusTables.CeltFreqBands[lowbandOffset] - OpusTables.CeltFreqRange[band]);
					var foldStart = lowbandOffset;
					while (OpusTables.CeltFreqBands[--foldStart] > effectiveLowband) { }
					var foldEnd = lowbandOffset - 1;
					while (++foldEnd < band && OpusTables.CeltFreqBands[foldEnd] < effectiveLowband + OpusTables.CeltFreqRange[band]) { }
					mask0 = mask1 = 0;
					for (var fold = foldStart; fold < foldEnd; fold++)
					{
						mask0 |= frame.Blocks[0].CollapseMasks[fold];
						mask1 |= frame.Blocks[frame.Channels - 1].CollapseMasks[fold];
					}
				}
				if (frame.DualStereo != 0 && band == frame.IntensityStereo)
				{
					frame.DualStereo = 0;
					for (var index = OpusTables.CeltFreqBands[frame.StartBand] << frame.Size; index < bandOffset; index++) norm1[index] = (norm1[index] + norm2[index]) / 2;
				}
				var lowOffset = effectiveLowband < 0 ? 0 : effectiveLowband << frame.Size;
				if (frame.DualStereo != 0)
				{
					mask0 = pvq.DecodeBand(frame, range, band, frame.Blocks[0].Coefficients, bandOffset, null, 0, bandSize, allocated >> 1,
						(uint)frame.BlocksCount, effectiveLowband < 0 ? null : norm1, lowOffset, frame.Size, norm1, bandOffset, 0, 1.0f, lowbandScratch, (int)mask0);
					mask1 = pvq.DecodeBand(frame, range, band, frame.Blocks[1].Coefficients, bandOffset, null, 0, bandSize, allocated >> 1,
						(uint)frame.BlocksCount, effectiveLowband < 0 ? null : norm2, lowOffset, frame.Size, norm2, bandOffset, 0, 1.0f, lowbandScratch, (int)mask1);
				} else
				{
					mask0 = pvq.DecodeBand(frame, range, band, frame.Blocks[0].Coefficients, bandOffset,
						frame.Channels == 2 ? frame.Blocks[1].Coefficients : null, bandOffset, bandSize, allocated,
						(uint)frame.BlocksCount, effectiveLowband < 0 ? null : norm1, lowOffset, frame.Size, norm1, bandOffset, 0, 1.0f, lowbandScratch, (int)(mask0 | mask1));
					mask1 = mask0;
				}
				frame.Blocks[0].CollapseMasks[band] = (byte)mask0;
				frame.Blocks[frame.Channels - 1].CollapseMasks[band] = (byte)mask1;
				frame.Remaining += frame.Pulses[band] + consumed;
				updateLowband = allocated > bandSize << 3;
			}
		}

		/// <summary>Runs FFmpeg's vector interpolation, skip-band, stereo, fine-energy, and pulse allocation schedule.</summary>
		public void DecodeAllocation(CeltFrame frame, OpusRangeDecoder range)
		{
			Array.Clear(boost, 0, boost.Length);
			var skipStartBand = frame.StartBand;
			var skipBit = 0;
			var intensityBit = 0;
			var dualBit = 0;
			var dynamicAllocation = 6;
			var extraBits = 0;
			frame.Spread = range.Tell + 4 <= frame.FrameBits ? (CeltSpread)range.DecodeCdf(OpusTables.CeltModelSpread) : CeltSpread.Normal;
			for (var band = 0; band < 21; band++)
			{
				var cap = (OpusTables.CeltStaticCaps[(frame.Size * 2 + frame.Channels - 1) * 21 + band] + 64) * OpusTables.CeltFreqRange[band];
				frame.Caps[band] = Normalize(frame, cap);
			}
			var available = frame.FrameBits << 3;
			for (var band = frame.StartBand; band < frame.EndBand; band++)
			{
				var quanta = OpusTables.CeltFreqRange[band] << (frame.Channels - 1) << frame.Size;
				var probability = dynamicAllocation;
				quanta = Math.Min(quanta << 3, Math.Max(48, quanta));
				while (range.TellFraction + (uint)(probability << 3) < available && boost[band] < frame.Caps[band])
				{
					if (range.DecodeLog((uint)probability) == 0) break;
					boost[band] += quanta; available -= quanta; probability = 1;
				}
				if (boost[band] != 0) dynamicAllocation = Math.Max(dynamicAllocation - 1, 2);
			}
			frame.AllocationTrim = 5;
			if (range.TellFraction + 48 <= available) frame.AllocationTrim = (int)range.DecodeCdf(OpusTables.CeltModelAllocTrim);
			available = (frame.FrameBits << 3) - (int)range.TellFraction - 1;
			frame.AnticollapseNeeded = frame.Transient != 0 && frame.Size >= 2 && available >= (frame.Size + 2 << 3) ? 8 : 0;
			available -= frame.AnticollapseNeeded;
			if (available >= 8) skipBit = 8;
			available -= skipBit;
			if (frame.Channels == 2)
			{
				intensityBit = OpusTables.CeltLog2Frac[frame.EndBand - frame.StartBand];
				if (intensityBit <= available) { available -= intensityBit; if (available >= 8) { dualBit = 8; available -= 8; } }
				else intensityBit = 0;
			}
			for (var band = frame.StartBand; band < frame.EndBand; band++)
			{
				var trim = frame.AllocationTrim - 5 - frame.Size;
				var position = OpusTables.CeltFreqRange[band] * (frame.EndBand - band - 1);
				var duration = frame.Size + 3;
				var scale = duration + frame.Channels - 1;
				threshold[band] = Math.Max(3 * OpusTables.CeltFreqRange[band] << duration >> 4, frame.Channels << 3);
				trimOffset[band] = trim * (position << scale) >> 6;
				if (OpusTables.CeltFreqRange[band] << frame.Size == 1) trimOffset[band] -= frame.Channels << 3;
			}
			var low = 1; var high = 10;
			while (low <= high)
			{
				var center = (low + high) >> 1; var done = false; var total = 0;
				for (var band = frame.EndBand - 1; band >= frame.StartBand; band--)
				{
					var bandBits = Normalize(frame, OpusTables.CeltFreqRange[band] * OpusTables.CeltStaticAlloc[center * 21 + band]);
					if (bandBits != 0) bandBits = Math.Max(bandBits + trimOffset[band], 0);
					bandBits += boost[band];
					if (bandBits >= threshold[band] || done) { done = true; total += Math.Min(bandBits, frame.Caps[band]); }
					else if (bandBits >= frame.Channels << 3) total += frame.Channels << 3;
				}
				if (total > available) high = center - 1; else low = center + 1;
			}
			high = low--;
			for (var band = frame.StartBand; band < frame.EndBand; band++)
			{
				bits1[band] = Normalize(frame, OpusTables.CeltFreqRange[band] * OpusTables.CeltStaticAlloc[low * 21 + band]);
				bits2[band] = high >= 11 ? frame.Caps[band] : Normalize(frame, OpusTables.CeltFreqRange[band] * OpusTables.CeltStaticAlloc[high * 21 + band]);
				if (bits1[band] != 0) bits1[band] = Math.Max(bits1[band] + trimOffset[band], 0);
				if (bits2[band] != 0) bits2[band] = Math.Max(bits2[band] + trimOffset[band], 0);
				if (low != 0) bits1[band] += boost[band];
				bits2[band] += boost[band];
				if (boost[band] != 0) skipStartBand = band;
				bits2[band] = Math.Max(bits2[band] - bits1[band], 0);
			}
			low = 0; high = 64;
			for (var step = 0; step < 6; step++)
			{
				var center = (low + high) >> 1; var done = false; var total = 0;
				for (var band = frame.EndBand - 1; band >= frame.StartBand; band--)
				{
					var bandBits = bits1[band] + (center * bits2[band] >> 6);
					if (bandBits >= threshold[band] || done) { done = true; total += Math.Min(bandBits, frame.Caps[band]); }
					else if (bandBits >= frame.Channels << 3) total += frame.Channels << 3;
				}
				if (total > available) high = center; else low = center;
			}
			var allocationTotal = 0; var allocationDone = false;
			for (var band = frame.EndBand - 1; band >= frame.StartBand; band--)
			{
				var bandBits = bits1[band] + (low * bits2[band] >> 6);
				if (bandBits >= threshold[band] || allocationDone) allocationDone = true; else bandBits = bandBits >= frame.Channels << 3 ? frame.Channels << 3 : 0;
				frame.Pulses[band] = Math.Min(bandBits, frame.Caps[band]); allocationTotal += frame.Pulses[band];
			}
			for (frame.CodedBands = frame.EndBand; ; frame.CodedBands--)
			{
				var band = frame.CodedBands - 1;
				if (band == skipStartBand) { available += skipBit; break; }
				var remaining = available - allocationTotal;
				var width = OpusTables.CeltFreqBands[band + 1] - OpusTables.CeltFreqBands[frame.StartBand];
				var perUnit = remaining / width; remaining -= perUnit * width;
				var allocation = frame.Pulses[band] + perUnit * OpusTables.CeltFreqRange[band] + Math.Max(remaining - (OpusTables.CeltFreqBands[band] - OpusTables.CeltFreqBands[frame.StartBand]), 0);
				if (allocation >= Math.Max(threshold[band], (frame.Channels + 1) << 3))
				{
					if (range.DecodeLog(1) != 0) break;
					allocationTotal += 8; allocation -= 8;
				}
				allocationTotal -= frame.Pulses[band];
				if (intensityBit != 0) { allocationTotal -= intensityBit; intensityBit = OpusTables.CeltLog2Frac[band - frame.StartBand]; allocationTotal += intensityBit; }
				frame.Pulses[band] = allocation >= frame.Channels << 3 ? frame.Channels << 3 : 0; allocationTotal += frame.Pulses[band];
			}
			frame.IntensityStereo = frame.DualStereo = 0;
			if (intensityBit != 0) frame.IntensityStereo = frame.StartBand + (int)range.DecodeUInt((uint)(frame.CodedBands + 1 - frame.StartBand));
			if (frame.IntensityStereo <= frame.StartBand) available += dualBit; else if (dualBit != 0) frame.DualStereo = (int)range.DecodeLog(1);
			var finalRemaining = available - allocationTotal;
			var codedWidth = OpusTables.CeltFreqBands[frame.CodedBands] - OpusTables.CeltFreqBands[frame.StartBand];
			var finalPerUnit = finalRemaining / codedWidth; finalRemaining -= finalPerUnit * codedWidth;
			for (var band = frame.StartBand; band < frame.CodedBands; band++) { var bits = Math.Min(finalRemaining, OpusTables.CeltFreqRange[band]); frame.Pulses[band] += bits + finalPerUnit * OpusTables.CeltFreqRange[band]; finalRemaining -= bits; }
			var index = frame.StartBand;
			for (; index < frame.CodedBands; index++)
			{
				var samples = OpusTables.CeltFreqRange[index] << frame.Size; var previousExtra = extraBits; frame.Pulses[index] += extraBits;
				if (samples > 1)
				{
					extraBits = Math.Max(frame.Pulses[index] - frame.Caps[index], 0); frame.Pulses[index] -= extraBits;
					var degrees = samples * frame.Channels + (frame.Channels == 2 && samples > 2 && frame.DualStereo == 0 && index < frame.IntensityStereo ? 1 : 0);
					var temporary = degrees * (OpusTables.CeltLogFreqRange[index] + (frame.Size << 3)); var offset = (temporary >> 1) - degrees * 21;
					if (samples == 2) offset += degrees << 1;
					if (frame.Pulses[index] + offset < 2 * (degrees << 3)) offset += temporary >> 2; else if (frame.Pulses[index] + offset < 3 * (degrees << 3)) offset += temporary >> 3;
					var fine = (frame.Pulses[index] + offset + (degrees << 2)) / (degrees << 3);
					var maximum = Math.Max(Math.Min((frame.Pulses[index] >> 3) >> (frame.Channels - 1), 8), 0);
					frame.FineBits[index] = Math.Clamp(fine, 0, maximum);
					frame.FinePriority[index] = frame.FineBits[index] * (degrees << 3) >= frame.Pulses[index] + offset ? 1 : 0;
					frame.Pulses[index] -= frame.FineBits[index] << (frame.Channels - 1) << 3;
				} else { extraBits = Math.Max(frame.Pulses[index] - (frame.Channels << 3), 0); frame.Pulses[index] -= extraBits; frame.FineBits[index] = 0; frame.FinePriority[index] = 1; }
				if (extraBits > 0) { var extraFine = Math.Min(extraBits >> (frame.Channels + 2), 8 - frame.FineBits[index]); frame.FineBits[index] += extraFine; extraFine <<= frame.Channels + 2; frame.FinePriority[index] = extraFine >= extraBits - previousExtra ? 1 : 0; extraBits -= extraFine; }
			}
			frame.Remaining = extraBits;
			for (; index < frame.EndBand; index++) { frame.FineBits[index] = frame.Pulses[index] >> (frame.Channels - 1) >> 3; frame.Pulses[index] = 0; frame.FinePriority[index] = frame.FineBits[index] < 1 ? 1 : 0; }
		}

		private static int Normalize(CeltFrame frame, int bits) => bits << (frame.Channels - 1) << frame.Size >> 2;
	}
}
