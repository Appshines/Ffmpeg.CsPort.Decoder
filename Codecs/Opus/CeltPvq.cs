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
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>Ports FFmpeg's scalar CELT pyramid-vector dequantizer and recursive band splitter.</summary>
	internal sealed class CeltPvq
	{
		private readonly int[] quantizedCoefficients = new int[256];
		private readonly float[] hadamardTemporary = new float[960];

		public uint DecodeBand(CeltFrame frame, OpusRangeDecoder range, int band, float[] x, int xOffset, float[] y, int yOffset,
			int length, int bits, uint blocks, float[] lowband, int lowbandOffset, int duration, float[] lowbandOutput,
			int lowbandOutputOffset, int level, float gain, float[] lowbandScratch, int fill)
		{
			return DecodeBandCore(frame, range, band, x, xOffset, y, yOffset, length, bits, blocks, lowband, lowbandOffset,
				duration, lowbandOutput, lowbandOutputOffset, level, gain, lowbandScratch, fill);
		}

		/// <summary>Follows quant_band_template's split/recombine schedule while retaining pointer offsets explicitly.</summary>
		private uint DecodeBandCore(CeltFrame frame, OpusRangeDecoder range, int band, float[] x, int xOffset, float[] y, int yOffset,
			int length, int bits, uint blocks, float[] lowband, int lowbandOffset, int duration, float[] lowbandOutput,
			int lowbandOutputOffset, int level, float gain, float[] lowbandScratch, int fill)
		{
			var stereo = y != null;
			var split = stereo;
			var originalLength = length;
			var samplesPerBlock = length / (int)blocks;
			var originalSamplesPerBlock = samplesPerBlock;
			var originalBlocks = blocks;
			var timeDivide = 0;
			var recombine = 0;
			var inversion = 0;
			var mid = 0.0f;
			var side = 0.0f;
			var longBlocks = originalBlocks == 1;
			uint collapseMask = 0;

			if (length == 1)
			{
				var sign = 0;
				if (frame.Remaining2 >= 8) { sign = (int)range.GetRaw(1); frame.Remaining2 -= 8; }
				x[xOffset] = 1.0f - 2.0f * sign;
				if (stereo)
				{
					sign = 0;
					if (frame.Remaining2 >= 8) { sign = (int)range.GetRaw(1); frame.Remaining2 -= 8; }
					y[yOffset] = 1.0f - 2.0f * sign;
				}
				if (lowbandOutput != null) lowbandOutput[lowbandOutputOffset] = x[xOffset];
				return 1;
			}

			if (!stereo && level == 0)
			{
				var tfChange = frame.TfChange[band];
				if (tfChange > 0) recombine = tfChange;
				if (lowband != null && (recombine != 0 || ((samplesPerBlock & 1) == 0 && tfChange < 0) || originalBlocks > 1))
				{
					Array.Copy(lowband, lowbandOffset, lowbandScratch, 0, length);
					lowband = lowbandScratch;
					lowbandOffset = 0;
				}
				for (var index = 0; index < recombine; index++)
				{
					if (lowband != null) Haar(lowband, lowbandOffset, length >> index, 1 << index);
					fill = OpusTables.CeltBitInterleave[fill & 15] | OpusTables.CeltBitInterleave[fill >> 4] << 2;
				}
				blocks >>= recombine;
				samplesPerBlock <<= recombine;
				while ((samplesPerBlock & 1) == 0 && tfChange < 0)
				{
					if (lowband != null) Haar(lowband, lowbandOffset, samplesPerBlock, (int)blocks);
					fill |= fill << (int)blocks;
					blocks <<= 1;
					samplesPerBlock >>= 1;
					timeDivide++;
					tfChange++;
				}
				originalBlocks = blocks;
				originalSamplesPerBlock = samplesPerBlock;
				if (originalBlocks > 1 && lowband != null)
					Deinterleave(lowband, lowbandOffset, samplesPerBlock >> recombine, (int)originalBlocks << recombine, longBlocks);
			}

			var cacheOffset = OpusTables.CeltCacheIndex[(duration + 1) * 21 + band];
			if (!stereo && duration >= 0 && bits > OpusTables.CeltCacheBits[cacheOffset + OpusTables.CeltCacheBits[cacheOffset]] + 12 && length > 2)
			{
				length >>= 1;
				y = x;
				yOffset = xOffset + length;
				split = true;
				duration--;
				if (blocks == 1) fill = (fill & 1) | fill << 1;
				blocks = (blocks + 1) >> 1;
			}

			if (split)
			{
				var pulseCap = OpusTables.CeltLogFreqRange[band] + duration * 8;
				var thetaOffset = (pulseCap >> 1) - (stereo && length == 2 ? 16 : 4);
				var qn = stereo && band >= frame.IntensityStereo ? 1 : ComputeQn(length, bits, thetaOffset, pulseCap, stereo);
				var tell = (int)range.TellFraction;
				var theta = 0;
				if (qn != 1)
				{
					if (stereo && length > 2) theta = (int)range.DecodeUIntStep(qn / 2);
					else if (stereo || originalBlocks > 1) theta = (int)range.DecodeUInt((uint)(qn + 1));
					else theta = (int)range.DecodeUIntTriangular(qn);
					theta = theta * 16384 / qn;
				} else if (stereo)
				{
					inversion = bits > 16 && frame.Remaining2 > 16 ? (int)range.DecodeLog(2) : 0;
					if (!frame.ApplyPhaseInversion) inversion = 0;
				}
				var allocated = (int)range.TellFraction - tell;
				bits -= allocated;
				var originalFill = fill;
				int midInteger;
				int sideInteger;
				int delta;
				if (theta == 0) { midInteger = 32767; sideInteger = 0; fill &= (1 << (int)blocks) - 1; delta = -16384; }
				else if (theta == 16384) { midInteger = 0; sideInteger = 32767; fill &= ((1 << (int)blocks) - 1) << (int)blocks; delta = 16384; }
				else
				{
					midInteger = CeltCos(theta);
					sideInteger = CeltCos(16384 - theta);
					delta = RoundMultiply((length - 1) << 7, CeltLog2Tan(sideInteger, midInteger));
				}
				mid = midInteger / 32768.0f;
				side = sideInteger / 32768.0f;

				if (length == 2 && stereo)
				{
					var sideBits = theta != 0 && theta != 16384 ? 8 : 0;
					var midBits = bits - sideBits;
					var chooseSide = theta > 8192;
					frame.Remaining2 -= allocated + sideBits;
					var first = chooseSide ? y : x; var firstOffset = chooseSide ? yOffset : xOffset;
					var second = chooseSide ? x : y; var secondOffset = chooseSide ? xOffset : yOffset;
					var sign = sideBits != 0 ? 1 - 2 * (int)range.GetRaw(1) : 1;
					collapseMask = DecodeBandCore(frame, range, band, first, firstOffset, null, 0, length, midBits, blocks,
						lowband, lowbandOffset, duration, lowbandOutput, lowbandOutputOffset, level, gain, lowbandScratch, originalFill);
					second[secondOffset] = -sign * first[firstOffset + 1]; second[secondOffset + 1] = sign * first[firstOffset];
					x[xOffset] *= mid; x[xOffset + 1] *= mid; y[yOffset] *= side; y[yOffset + 1] *= side;
					var temporary = x[xOffset]; x[xOffset] = temporary - y[yOffset]; y[yOffset] = temporary + y[yOffset];
					temporary = x[xOffset + 1]; x[xOffset + 1] = temporary - y[yOffset + 1]; y[yOffset + 1] = temporary + y[yOffset + 1];
				} else
				{
					if (originalBlocks > 1 && !stereo && (theta & 0x3fff) != 0)
					{
						if (theta > 8192) delta -= delta >> (4 - duration);
						else delta = Math.Min(0, delta + (length << 3 >> (5 - duration)));
					}
					var midBits = Math.Clamp((bits - delta) / 2, 0, bits);
					var sideBits = bits - midBits;
					frame.Remaining2 -= allocated;
					var nextLowband = !stereo && lowband != null ? lowbandOffset + length : 0;
					var rebalance = frame.Remaining2;
					if (midBits >= sideBits)
					{
						collapseMask = DecodeBandCore(frame, range, band, x, xOffset, null, 0, length, midBits, blocks, lowband, lowbandOffset,
							duration, stereo ? lowbandOutput : null, lowbandOutputOffset, stereo ? level : level + 1, stereo ? 1.0f : gain * mid, lowbandScratch, fill);
						rebalance = midBits - (rebalance - frame.Remaining2);
						if (rebalance > 24 && theta != 0) sideBits += rebalance - 24;
						var sideMask = DecodeBandCore(frame, range, band, y, yOffset, null, 0, length, sideBits, blocks, stereo ? null : lowband,
							nextLowband, duration, null, 0, stereo ? level : level + 1, gain * side, null, fill >> (int)blocks);
						collapseMask |= sideMask << ((int)(originalBlocks >> 1) & ((stereo ? 1 : 0) - 1));
					} else
					{
						collapseMask = DecodeBandCore(frame, range, band, y, yOffset, null, 0, length, sideBits, blocks, stereo ? null : lowband,
							nextLowband, duration, null, 0, stereo ? level : level + 1, gain * side, null, fill >> (int)blocks);
						collapseMask <<= ((int)(originalBlocks >> 1) & ((stereo ? 1 : 0) - 1));
						rebalance = sideBits - (rebalance - frame.Remaining2);
						if (rebalance > 24 && theta != 16384) midBits += rebalance - 24;
						collapseMask |= DecodeBandCore(frame, range, band, x, xOffset, null, 0, length, midBits, blocks, lowband, lowbandOffset,
							duration, stereo ? lowbandOutput : null, lowbandOutputOffset, stereo ? level : level + 1, stereo ? 1.0f : gain * mid, lowbandScratch, fill);
					}
				}
			} else
			{
				var pulses = BitsToPulses(cacheOffset, bits);
				var currentBits = PulsesToBits(cacheOffset, pulses);
				frame.Remaining2 -= currentBits;
				while (frame.Remaining2 < 0 && pulses > 0)
				{
					frame.Remaining2 += currentBits;
					currentBits = PulsesToBits(cacheOffset, --pulses);
					frame.Remaining2 -= currentBits;
				}
				if (pulses != 0)
				{
					var pulseCount = pulses < 8 ? pulses : (8 + (pulses & 7)) << ((pulses >> 3) - 1);
					collapseMask = Unquantize(range, x, xOffset, (uint)length, (uint)pulseCount, frame.Spread, blocks, gain);
				} else
				{
					var mask = (1u << (int)blocks) - 1;
					fill &= (int)mask;
					if (fill != 0)
					{
						if (lowband == null)
							for (var index = 0; index < length; index++) x[xOffset + index] = unchecked((int)frame.NextRandom()) >> 20;
						else
							for (var index = 0; index < length; index++) x[xOffset + index] = lowband[lowbandOffset + index] + ((frame.NextRandom() & 0x8000) != 0 ? 1.0f / 256 : -1.0f / 256);
						collapseMask = lowband == null ? mask : (uint)fill;
						Renormalize(x, xOffset, length, gain);
					} else Array.Clear(x, xOffset, length);
				}
			}

			if (stereo)
			{
				if (length > 2) StereoMerge(x, xOffset, y, yOffset, mid, length);
				if (inversion != 0) for (var index = 0; index < length; index++) y[yOffset + index] *= -1;
			} else if (level == 0)
			{
				if (originalBlocks > 1) Interleave(x, xOffset, originalSamplesPerBlock >> recombine, (int)originalBlocks << recombine, longBlocks);
				samplesPerBlock = originalSamplesPerBlock; blocks = originalBlocks;
				for (var index = 0; index < timeDivide; index++) { blocks >>= 1; samplesPerBlock <<= 1; collapseMask |= collapseMask >> (int)blocks; Haar(x, xOffset, samplesPerBlock, (int)blocks); }
				for (var index = 0; index < recombine; index++) { collapseMask = OpusTables.CeltBitDeinterleave[collapseMask]; Haar(x, xOffset, originalLength >> index, 1 << index); }
				blocks <<= recombine;
				if (lowbandOutput != null)
				{
					var scale = MathF.Sqrt(originalLength);
					for (var index = 0; index < originalLength; index++) lowbandOutput[lowbandOutputOffset + index] = scale * x[xOffset + index];
				}
				collapseMask &= (1u << (int)blocks) - 1;
			}
			return collapseMask;
		}

		private uint Unquantize(OpusRangeDecoder range, float[] x, int offset, uint length, uint pulses, CeltSpread spread, uint blocks, float gain)
		{
			var norm = DecodePulses(range, length, pulses);
			gain /= MathF.Sqrt(norm);
			for (var index = 0; index < length; index++) x[offset + index] = gain * quantizedCoefficients[index];
			Rotate(x, offset, length, blocks, pulses, spread);
			return ExtractCollapseMask(length, blocks);
		}

		private float DecodePulses(OpusRangeDecoder range, uint length, uint pulses)
		{
			var index = range.DecodeUInt(PvqV(length, pulses));
			return DecodeCombinatorial(length, pulses, index);
		}

		private ulong DecodeCombinatorial(uint length, uint pulses, uint index)
		{
			ulong norm = 0; var output = 0;
			while (length > 2)
			{
				uint p; int sign; var original = pulses;
				if (pulses >= length)
				{
					p = PvqU(length, pulses + 1); sign = -(index >= p ? 1 : 0); index -= p & (uint)sign;
					var q = PvqU(length, length);
					if (q > index) { pulses = length; do p = PvqU(--pulses, length); while (p > index); }
					else for (p = PvqU(length, pulses); p > index; p = PvqU(length, pulses)) pulses--;
				} else
				{
					p = PvqU(pulses, length); var q = PvqU(pulses + 1, length);
					if (p <= index && index < q) { index -= p; quantizedCoefficients[output++] = 0; length--; continue; }
					sign = -(index >= q ? 1 : 0); index -= q & (uint)sign; original = pulses;
					do p = PvqU(--pulses, length); while (p > index);
				}
				index -= p; var value = ((int)(original - pulses) + sign) ^ sign; norm += (ulong)(value * value); quantizedCoefficients[output++] = value; length--;
			}
			var boundary = 2 * pulses + 1; var finalSign = -(index >= boundary ? 1 : 0); index -= boundary & (uint)finalSign;
			var oldPulses = pulses; pulses = (index + 1) / 2; if (pulses != 0) index -= 2 * pulses - 1;
			var firstValue = ((int)(oldPulses - pulses) + finalSign) ^ finalSign; norm += (ulong)(firstValue * firstValue); quantizedCoefficients[output++] = firstValue;
			finalSign = -(int)index; var lastValue = ((int)pulses + finalSign) ^ finalSign; norm += (ulong)(lastValue * lastValue); quantizedCoefficients[output] = lastValue;
			return norm;
		}

		private void Rotate(float[] x, int offset, uint length, uint stride, uint pulses, CeltSpread spread)
		{
			if (2 * pulses >= length || spread == CeltSpread.None) return;
			var rotationIndex = ((int)spread - 1) * CeltRotationTables.ValuesPerSpread +
				CeltRotationTables.LengthOffsets[length - 1] + (int)pulses;
			var cosine = BitConverter.Int32BitsToSingle(unchecked((int)CeltRotationTables.CosineBits[rotationIndex]));
			var sine = BitConverter.Int32BitsToSingle(unchecked((int)CeltRotationTables.SineBits[rotationIndex]));
			uint secondStride = 0;
			if (length >= stride << 3) { secondStride = 1; while ((secondStride * secondStride + secondStride) * stride + (stride >> 2) < length) secondStride++; }
			length /= stride;
			for (var index = 0; index < stride; index++)
			{
				if (secondStride != 0) RotateCore(x, offset + index * (int)length, length, secondStride, sine, cosine);
				RotateCore(x, offset + index * (int)length, length, 1, cosine, sine);
			}
		}

		private static void RotateCore(float[] x, int offset, uint length, uint stride, float cosine, float sine)
		{
			for (var index = 0; index < length - stride; index++) { var p = offset + index; var a = x[p]; var b = x[p + stride]; x[p + stride] = FfmpegMath.MultiplyFloat(cosine, b) + FfmpegMath.MultiplyFloat(sine, a); x[p] = FfmpegMath.MultiplyFloat(cosine, a) - FfmpegMath.MultiplyFloat(sine, b); }
			for (var index = (int)(length - 2 * stride - 1); index >= 0; index--) { var p = offset + index; var a = x[p]; var b = x[p + stride]; x[p + stride] = FfmpegMath.MultiplyFloat(cosine, b) + FfmpegMath.MultiplyFloat(sine, a); x[p] = FfmpegMath.MultiplyFloat(cosine, a) - FfmpegMath.MultiplyFloat(sine, b); }
		}

		private uint ExtractCollapseMask(uint length, uint blocks)
		{
			if (blocks <= 1) return 1;
			uint mask = 0; var perBlock = length / blocks;
			for (var block = 0; block < blocks; block++) for (var index = 0; index < perBlock; index++) if (quantizedCoefficients[block * perBlock + index] != 0) mask |= 1u << (int)block;
			return mask;
		}

		private static uint PvqU(uint n, uint k) { var row = Math.Min(n, k); return OpusTables.CeltPvqU[OpusTables.CeltPvqURowOffsets[row] + Math.Max(n, k)]; }
		private static uint PvqV(uint n, uint k) => PvqU(n, k) + PvqU(n, k + 1);
		private static int RoundMultiply(int a, int b) => (a * b + 16384) >> 15;
		private static int CeltCos(int value) { value = (value * value + 4096) >> 13; value = 32767 - value + RoundMultiply(value, -7651 + RoundMultiply(value, 8277 + RoundMultiply(-626, value))); return value + 1; }
		private static int CeltLog2Tan(int sine, int cosine) { var lc = IntegerLog(cosine); var ls = IntegerLog(sine); cosine <<= 15 - lc; sine <<= 15 - ls; return (ls - lc << 11) + RoundMultiply(sine, RoundMultiply(sine, -2597) + 7932) - RoundMultiply(cosine, RoundMultiply(cosine, -2597) + 7932); }
		private static int IntegerLog(int value) { var result = 0; while (value != 0) { result++; value >>= 1; } return result; }
		private static int ComputeQn(int length, int bits, int offset, int cap, bool stereo) { var n2 = 2 * length - 1 - (stereo && length == 2 ? 1 : 0); var qb = Math.Min(Math.Min(bits - cap - 32, (bits + n2 * offset) / n2), 64); return qb < 4 ? 1 : ((OpusTables.CeltQnExp2[qb & 7] >> (14 - (qb >> 3))) + 1) >> 1 << 1; }
		private static int BitsToPulses(int offset, int bits) { var low = 0; var high = (int)OpusTables.CeltCacheBits[offset]; bits--; for (var i = 0; i < 6; i++) { var center = (low + high + 1) >> 1; if (OpusTables.CeltCacheBits[offset + center] >= bits) high = center; else low = center; } return bits - (low == 0 ? -1 : OpusTables.CeltCacheBits[offset + low]) <= OpusTables.CeltCacheBits[offset + high] - bits ? low : high; }
		private static int PulsesToBits(int offset, int pulses) => pulses == 0 ? 0 : OpusTables.CeltCacheBits[offset + pulses] + 1;

		private static void Renormalize(float[] values, int offset, int length, float gain) { var energy = 1e-15f; for (var i = 0; i < length; i++) energy += FfmpegMath.MultiplyFloat(values[offset + i], values[offset + i]); var scale = gain / MathF.Sqrt(energy); for (var i = 0; i < length; i++) values[offset + i] = FfmpegMath.MultiplyFloat(values[offset + i], scale); }
		private static void StereoMerge(float[] x, int xo, float[] y, int yo, float mid, int length) { var product = 0.0f; var side = 0.0f; for (var i = 0; i < length; i++) { product += FfmpegMath.MultiplyFloat(x[xo + i], y[yo + i]); side += FfmpegMath.MultiplyFloat(y[yo + i], y[yo + i]); } product = FfmpegMath.MultiplyFloat(product, mid); var midSquared = FfmpegMath.MultiplyFloat(mid, mid); var e0 = midSquared + side - 2 * product; var e1 = midSquared + side + 2 * product; if (e0 < 6e-4f || e1 < 6e-4f) { Array.Copy(x, xo, y, yo, length); return; } var g0 = 1.0f / MathF.Sqrt(e0); var g1 = 1.0f / MathF.Sqrt(e1); for (var i = 0; i < length; i++) { var a = FfmpegMath.MultiplyFloat(mid, x[xo + i]); var b = y[yo + i]; x[xo + i] = FfmpegMath.MultiplyFloat(g0, a - b); y[yo + i] = FfmpegMath.MultiplyFloat(g1, a + b); } }
		private void Interleave(float[] x, int offset, int n0, int stride, bool hadamard) { var orderOffset = hadamard ? stride - 2 : 30; for (var i = 0; i < stride; i++) for (var j = 0; j < n0; j++) hadamardTemporary[j * stride + i] = x[offset + OpusTables.CeltHadamardOrder[orderOffset + i] * n0 + j]; Array.Copy(hadamardTemporary, 0, x, offset, n0 * stride); }
		private void Deinterleave(float[] x, int offset, int n0, int stride, bool hadamard) { var orderOffset = hadamard ? stride - 2 : 30; for (var i = 0; i < stride; i++) for (var j = 0; j < n0; j++) hadamardTemporary[OpusTables.CeltHadamardOrder[orderOffset + i] * n0 + j] = x[offset + j * stride + i]; Array.Copy(hadamardTemporary, 0, x, offset, n0 * stride); }
		private static void Haar(float[] x, int offset, int n0, int stride) { n0 >>= 1; for (var i = 0; i < stride; i++) for (var j = 0; j < n0; j++) { var p0 = offset + stride * (2 * j) + i; var p1 = p0 + stride; var a = x[p0]; var b = x[p1]; x[p0] = (float)((a + b) * 0.70710678118654752440); x[p1] = (float)((a - b) * 0.70710678118654752440); } }
	}
}
