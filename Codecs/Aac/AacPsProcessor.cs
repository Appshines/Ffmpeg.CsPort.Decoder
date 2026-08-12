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
 * PORT-NOTE: 1:1 translation. Performance-motivated, semantics-preserving transformations
 * applied (see repository history); bit-exactness remains verified by the conformance tests.
 */
using System;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Applies FFmpeg's scalar AAC Parametric Stereo hybrid analysis, decorrelation, stereo mixing, and hybrid synthesis.</summary>
	internal static class AacPsProcessor
	{
		private static readonly float[] HybridTwoFilter =
		{
			0.0f, 0.01899487526049f, 0.0f, -0.07293139167538f, 0.0f, 0.30596630545168f, 0.5f
		};
		private static readonly int[] ParameterBands = { 20, 34 };
		private static readonly int[] HybridBands = { 71, 91 };
		private static readonly int[] DecayCutoff = { 10, 32 };
		private static readonly int[] AllPassBands = { 30, 50 };
		private static readonly int[] ShortDelayBand = { 42, 62 };

		/// <summary>Transforms one SBR QMF frame to PS hybrid bands, creates the stereo image, and writes both QMF channels back.</summary>
		public static void Apply(AacParametricStereo ps, float[] output, int top)
		{
			var is34 = ps.Common.Is34Bands ? 1 : 0;
			top += HybridBands[is34] - 64;
			ClearDelayFrom(ps, top, HybridBands[is34]);
			if (top < AllPassBands[is34])
				ClearAllPassDelayFrom(ps, top, AllPassBands[is34]);
			HybridAnalysis(ps, output, is34 != 0);
			Decorrelate(ps, is34 != 0);
			AacPsStereo.Process(ps, is34 != 0);
			HybridSynthesis(ps, output, is34 != 0);
		}

		/// <summary>Splits the first five QMF bands into the 20- or 34-band PS hybrid layout and interleaves remaining QMF bands.</summary>
		private static void HybridAnalysis(AacParametricStereo ps, float[] output, bool is34)
		{
			for (var qmf = 0; qmf < 5; qmf++)
			{
				for (var time = 0; time < 38; time++)
				{
					var outputIndex = time * AacSpectralBandReplication.OutputTimeStride + qmf;
					ps.InputBuffer[qmf, time + 6, 0] = output[outputIndex];
					ps.InputBuffer[qmf, time + 6, 1] = output[outputIndex + AacSpectralBandReplication.OutputComponentStride];
				}
			}

			if (is34)
			{
				HybridComplex(ps, 0, 0, AacPsTables.Hybrid34Filter12, 12);
				HybridComplex(ps, 1, 12, AacPsTables.Hybrid34Filter8, 8);
				HybridComplex(ps, 2, 20, AacPsTables.Hybrid34Filter4, 4);
				HybridComplex(ps, 3, 24, AacPsTables.Hybrid34Filter4, 4);
				HybridComplex(ps, 4, 28, AacPsTables.Hybrid34Filter4, 4);
				for (var qmf = 5; qmf < 64; qmf++)
				{
					for (var time = 0; time < 32; time++)
					{
						var outputIndex = time * AacSpectralBandReplication.OutputTimeStride + qmf;
						ps.LeftBuffer[27 + qmf, time, 0] = output[outputIndex];
						ps.LeftBuffer[27 + qmf, time, 1] = output[outputIndex + AacSpectralBandReplication.OutputComponentStride];
					}
				}
			} else
			{
				HybridSix(ps);
				HybridTwo(ps, 1, 6, true);
				HybridTwo(ps, 2, 8, false);
				for (var qmf = 3; qmf < 64; qmf++)
				{
					for (var time = 0; time < 32; time++)
					{
						var outputIndex = time * AacSpectralBandReplication.OutputTimeStride + qmf;
						ps.LeftBuffer[7 + qmf, time, 0] = output[outputIndex];
						ps.LeftBuffer[7 + qmf, time, 1] = output[outputIndex + AacSpectralBandReplication.OutputComponentStride];
					}
				}
			}

			for (var qmf = 0; qmf < 5; qmf++)
			{
				for (var time = 0; time < 6; time++)
				{
					ps.InputBuffer[qmf, time, 0] = ps.InputBuffer[qmf, time + 32, 0];
					ps.InputBuffer[qmf, time, 1] = ps.InputBuffer[qmf, time + 32, 1];
				}
			}
		}

		private static void HybridComplex(AacParametricStereo ps, int inputBand, int outputBand, float[] filter, int count)
		{
			for (var time = 0; time < 32; time++)
				AacPsDsp.HybridAnalysis(ps.LeftBuffer, outputBand, time, ps.InputBuffer, inputBand, time, filter, 0, 1, count);
		}

		private static void HybridSix(AacParametricStereo ps)
		{
			for (var time = 0; time < 32; time++)
			{
				AacPsDsp.HybridAnalysis(ps.LeftBuffer, 83, time, ps.InputBuffer, 0, time,
					AacPsTables.Hybrid20Filter8, 0, 1, 8);
				ps.LeftBuffer[0, time, 0] = ps.LeftBuffer[89, time, 0];
				ps.LeftBuffer[0, time, 1] = ps.LeftBuffer[89, time, 1];
				ps.LeftBuffer[1, time, 0] = ps.LeftBuffer[90, time, 0];
				ps.LeftBuffer[1, time, 1] = ps.LeftBuffer[90, time, 1];
				ps.LeftBuffer[2, time, 0] = ps.LeftBuffer[83, time, 0];
				ps.LeftBuffer[2, time, 1] = ps.LeftBuffer[83, time, 1];
				ps.LeftBuffer[3, time, 0] = ps.LeftBuffer[84, time, 0];
				ps.LeftBuffer[3, time, 1] = ps.LeftBuffer[84, time, 1];
				ps.LeftBuffer[4, time, 0] = ps.LeftBuffer[85, time, 0] + ps.LeftBuffer[88, time, 0];
				ps.LeftBuffer[4, time, 1] = ps.LeftBuffer[85, time, 1] + ps.LeftBuffer[88, time, 1];
				ps.LeftBuffer[5, time, 0] = ps.LeftBuffer[86, time, 0] + ps.LeftBuffer[87, time, 0];
				ps.LeftBuffer[5, time, 1] = ps.LeftBuffer[86, time, 1] + ps.LeftBuffer[87, time, 1];
			}
		}

		private static void HybridTwo(AacParametricStereo ps, int inputBand, int outputBand, bool reverse)
		{
			for (var time = 0; time < 32; time++)
			{
				var realInput = HybridTwoFilter[6] * ps.InputBuffer[inputBand, time + 6, 0];
				var realOdd = 0.0f;
				var imaginaryInput = HybridTwoFilter[6] * ps.InputBuffer[inputBand, time + 6, 1];
				var imaginaryOdd = 0.0f;
				for (var tap = 0; tap < 6; tap += 2)
				{
					realOdd += HybridTwoFilter[tap + 1] * (ps.InputBuffer[inputBand, time + tap + 1, 0] +
						ps.InputBuffer[inputBand, time + 12 - tap - 1, 0]);
					imaginaryOdd += HybridTwoFilter[tap + 1] * (ps.InputBuffer[inputBand, time + tap + 1, 1] +
						ps.InputBuffer[inputBand, time + 12 - tap - 1, 1]);
				}
				var first = outputBand + (reverse ? 1 : 0);
				var second = outputBand + (reverse ? 0 : 1);
				ps.LeftBuffer[first, time, 0] = realInput + realOdd;
				ps.LeftBuffer[first, time, 1] = imaginaryInput + imaginaryOdd;
				ps.LeftBuffer[second, time, 0] = realInput - realOdd;
				ps.LeftBuffer[second, time, 1] = imaginaryInput - imaginaryOdd;
			}
		}

		/// <summary>Builds transient suppression gains and updates the long, short, and all-pass decorrelation delays.</summary>
		private static void Decorrelate(AacParametricStereo ps, bool is34Bands)
		{
			var mode = is34Bands ? 1 : 0;
			var mapping = is34Bands ? AacPsTables.KToI34 : AacPsTables.KToI20;
			Array.Clear(ps.Power, 0, ps.Power.Length);
			if (is34Bands != ps.Common.Was34Bands)
			{
				Array.Clear(ps.PeakDecayEnergy, 0, ps.PeakDecayEnergy.Length);
				Array.Clear(ps.SmoothedPower, 0, ps.SmoothedPower.Length);
				Array.Clear(ps.SmoothedPeakDifference, 0, ps.SmoothedPeakDifference.Length);
				Array.Clear(ps.Delay, 0, ps.Delay.Length);
				Array.Clear(ps.AllPassDelay, 0, ps.AllPassDelay.Length);
			}

			for (var band = 0; band < HybridBands[mode]; band++)
				AacPsDsp.AddSquares(ps.Power, mapping[band], ps.LeftBuffer, band, 32);
			for (var parameterBand = 0; parameterBand < ParameterBands[mode]; parameterBand++)
			{
				for (var time = 0; time < 32; time++)
				{
					var decayedPeak = 0.76592833836465f * ps.PeakDecayEnergy[parameterBand];
					ps.PeakDecayEnergy[parameterBand] = Math.Max(decayedPeak, ps.Power[parameterBand, time]);
					ps.SmoothedPower[parameterBand] += 0.25f *
						(ps.Power[parameterBand, time] - ps.SmoothedPower[parameterBand]);
					ps.SmoothedPeakDifference[parameterBand] += 0.25f *
						(ps.PeakDecayEnergy[parameterBand] - ps.Power[parameterBand, time] - ps.SmoothedPeakDifference[parameterBand]);
					var denominator = 1.5f * ps.SmoothedPeakDifference[parameterBand];
					ps.TransientGain[parameterBand, time] = denominator > ps.SmoothedPower[parameterBand]
						? ps.SmoothedPower[parameterBand] / denominator : 1.0f;
				}
			}

			var hybridBand = 0;
			for (; hybridBand < AllPassBands[mode]; hybridBand++)
			{
				var parameterBand = mapping[hybridBand];
				var decaySlope = 1.0f - 0.05f * (hybridBand - DecayCutoff[mode]);
				decaySlope = Math.Max(0.0f, Math.Min(1.0f, decaySlope));
				ShiftDelay(ps, hybridBand);
				for (var link = 0; link < 3; link++)
				{
					for (var time = 0; time < 5; time++)
					{
						ps.AllPassDelay[hybridBand, link, time, 0] = ps.AllPassDelay[hybridBand, link, time + 32, 0];
						ps.AllPassDelay[hybridBand, link, time, 1] = ps.AllPassDelay[hybridBand, link, time + 32, 1];
					}
				}
				AacPsDsp.Decorrelate(ps, hybridBand, parameterBand, decaySlope);
			}
			for (; hybridBand < ShortDelayBand[mode]; hybridBand++)
			{
				var parameterBand = mapping[hybridBand];
				ShiftDelay(ps, hybridBand);
				AacPsDsp.MultiplyPair(ps.RightBuffer, hybridBand, ps.Delay, hybridBand, 0,
					ps.TransientGain, parameterBand, 32);
			}
			for (; hybridBand < HybridBands[mode]; hybridBand++)
			{
				var parameterBand = mapping[hybridBand];
				ShiftDelay(ps, hybridBand);
				AacPsDsp.MultiplyPair(ps.RightBuffer, hybridBand, ps.Delay, hybridBand, 13,
					ps.TransientGain, parameterBand, 32);
			}
		}

		private static void ShiftDelay(AacParametricStereo ps, int band)
		{
			for (var time = 0; time < 14; time++)
			{
				ps.Delay[band, time, 0] = ps.Delay[band, time + 32, 0];
				ps.Delay[band, time, 1] = ps.Delay[band, time + 32, 1];
			}
			for (var time = 0; time < 32; time++)
			{
				ps.Delay[band, time + 14, 0] = ps.LeftBuffer[band, time, 0];
				ps.Delay[band, time + 14, 1] = ps.LeftBuffer[band, time, 1];
			}
		}

		/// <summary>Recombines PS hybrid subbands into the two 64-band complex QMF channel matrices.</summary>
		private static void HybridSynthesis(AacParametricStereo ps, float[] output, bool is34)
		{
			if (is34)
			{
				for (var time = 0; time < 32; time++)
				{
					var leftReal = output.AsSpan(time * AacSpectralBandReplication.OutputTimeStride,
						AacSpectralBandReplication.OutputTimeStride);
					var leftImaginary = output.AsSpan(AacSpectralBandReplication.OutputComponentStride +
						time * AacSpectralBandReplication.OutputTimeStride, AacSpectralBandReplication.OutputTimeStride);
					var rightReal = output.AsSpan(AacSpectralBandReplication.OutputChannelStride +
						time * AacSpectralBandReplication.OutputTimeStride, AacSpectralBandReplication.OutputTimeStride);
					var rightImaginary = output.AsSpan(AacSpectralBandReplication.OutputChannelStride +
						AacSpectralBandReplication.OutputComponentStride + time * AacSpectralBandReplication.OutputTimeStride,
						AacSpectralBandReplication.OutputTimeStride);
					for (var qmf = 0; qmf < 5; qmf++)
					{
						leftReal[qmf] = 0.0f;
						leftImaginary[qmf] = 0.0f;
						rightReal[qmf] = 0.0f;
						rightImaginary[qmf] = 0.0f;
					}
					for (var band = 0; band < 12; band++)
					{
						leftReal[0] += ps.LeftBuffer[band, time, 0];
						leftImaginary[0] += ps.LeftBuffer[band, time, 1];
						rightReal[0] += ps.RightBuffer[band, time, 0];
						rightImaginary[0] += ps.RightBuffer[band, time, 1];
					}
					for (var band = 0; band < 8; band++)
					{
						leftReal[1] += ps.LeftBuffer[12 + band, time, 0];
						leftImaginary[1] += ps.LeftBuffer[12 + band, time, 1];
						rightReal[1] += ps.RightBuffer[12 + band, time, 0];
						rightImaginary[1] += ps.RightBuffer[12 + band, time, 1];
					}
					for (var band = 0; band < 4; band++)
					{
						AddHybrid(output, ps, time, 2, 20 + band);
						AddHybrid(output, ps, time, 3, 24 + band);
						AddHybrid(output, ps, time, 4, 28 + band);
					}
				}
				Deinterleave(output, ps, 27, 5);
			} else
			{
				for (var time = 0; time < 32; time++)
				{
					var leftReal = output.AsSpan(time * AacSpectralBandReplication.OutputTimeStride,
						AacSpectralBandReplication.OutputTimeStride);
					var leftImaginary = output.AsSpan(AacSpectralBandReplication.OutputComponentStride +
						time * AacSpectralBandReplication.OutputTimeStride, AacSpectralBandReplication.OutputTimeStride);
					var rightReal = output.AsSpan(AacSpectralBandReplication.OutputChannelStride +
						time * AacSpectralBandReplication.OutputTimeStride, AacSpectralBandReplication.OutputTimeStride);
					var rightImaginary = output.AsSpan(AacSpectralBandReplication.OutputChannelStride +
						AacSpectralBandReplication.OutputComponentStride + time * AacSpectralBandReplication.OutputTimeStride,
						AacSpectralBandReplication.OutputTimeStride);
					leftReal[0] = ps.LeftBuffer[0, time, 0] + ps.LeftBuffer[1, time, 0] +
						ps.LeftBuffer[2, time, 0] + ps.LeftBuffer[3, time, 0] + ps.LeftBuffer[4, time, 0] + ps.LeftBuffer[5, time, 0];
					leftImaginary[0] = ps.LeftBuffer[0, time, 1] + ps.LeftBuffer[1, time, 1] +
						ps.LeftBuffer[2, time, 1] + ps.LeftBuffer[3, time, 1] + ps.LeftBuffer[4, time, 1] + ps.LeftBuffer[5, time, 1];
					rightReal[0] = ps.RightBuffer[0, time, 0] + ps.RightBuffer[1, time, 0] +
						ps.RightBuffer[2, time, 0] + ps.RightBuffer[3, time, 0] + ps.RightBuffer[4, time, 0] + ps.RightBuffer[5, time, 0];
					rightImaginary[0] = ps.RightBuffer[0, time, 1] + ps.RightBuffer[1, time, 1] +
						ps.RightBuffer[2, time, 1] + ps.RightBuffer[3, time, 1] + ps.RightBuffer[4, time, 1] + ps.RightBuffer[5, time, 1];
					leftReal[1] = ps.LeftBuffer[6, time, 0] + ps.LeftBuffer[7, time, 0];
					leftImaginary[1] = ps.LeftBuffer[6, time, 1] + ps.LeftBuffer[7, time, 1];
					rightReal[1] = ps.RightBuffer[6, time, 0] + ps.RightBuffer[7, time, 0];
					rightImaginary[1] = ps.RightBuffer[6, time, 1] + ps.RightBuffer[7, time, 1];
					leftReal[2] = ps.LeftBuffer[8, time, 0] + ps.LeftBuffer[9, time, 0];
					leftImaginary[2] = ps.LeftBuffer[8, time, 1] + ps.LeftBuffer[9, time, 1];
					rightReal[2] = ps.RightBuffer[8, time, 0] + ps.RightBuffer[9, time, 0];
					rightImaginary[2] = ps.RightBuffer[8, time, 1] + ps.RightBuffer[9, time, 1];
				}
				Deinterleave(output, ps, 7, 3);
			}
		}

		private static void AddHybrid(float[] output, AacParametricStereo ps, int time, int qmf, int band)
		{
			var outputIndex = time * AacSpectralBandReplication.OutputTimeStride + qmf;
			output[outputIndex] += ps.LeftBuffer[band, time, 0];
			output[outputIndex + AacSpectralBandReplication.OutputComponentStride] += ps.LeftBuffer[band, time, 1];
			output[outputIndex + AacSpectralBandReplication.OutputChannelStride] += ps.RightBuffer[band, time, 0];
			output[outputIndex + AacSpectralBandReplication.OutputChannelStride + AacSpectralBandReplication.OutputComponentStride] +=
				ps.RightBuffer[band, time, 1];
		}

		private static void Deinterleave(float[] output, AacParametricStereo ps, int inputBase, int firstQmf)
		{
			for (var qmf = firstQmf; qmf < 64; qmf++)
			{
				for (var time = 0; time < 32; time++)
				{
					var outputIndex = time * AacSpectralBandReplication.OutputTimeStride + qmf;
					output[outputIndex] = ps.LeftBuffer[inputBase + qmf, time, 0];
					output[outputIndex + AacSpectralBandReplication.OutputComponentStride] = ps.LeftBuffer[inputBase + qmf, time, 1];
					output[outputIndex + AacSpectralBandReplication.OutputChannelStride] = ps.RightBuffer[inputBase + qmf, time, 0];
					output[outputIndex + AacSpectralBandReplication.OutputChannelStride + AacSpectralBandReplication.OutputComponentStride] =
						ps.RightBuffer[inputBase + qmf, time, 1];
				}
			}
		}

		private static void ClearDelayFrom(AacParametricStereo ps, int start, int end)
		{
			for (var band = Math.Max(start, 0); band < end; band++)
			{
				for (var time = 0; time < 46; time++)
				{
					ps.Delay[band, time, 0] = 0.0f;
					ps.Delay[band, time, 1] = 0.0f;
				}
			}
		}

		private static void ClearAllPassDelayFrom(AacParametricStereo ps, int start, int end)
		{
			for (var band = Math.Max(start, 0); band < end; band++)
			{
				for (var link = 0; link < 3; link++)
				{
					for (var time = 0; time < 37; time++)
					{
						ps.AllPassDelay[band, link, time, 0] = 0.0f;
						ps.AllPassDelay[band, link, time, 1] = 0.0f;
					}
				}
			}
		}
	}
}
