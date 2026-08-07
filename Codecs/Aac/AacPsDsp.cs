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

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Implements FFmpeg's scalar AAC Parametric Stereo hybrid-filter, decorrelator, and matrix-interpolation kernels.</summary>
	internal static class AacPsDsp
	{
		private static readonly float[] AllPassFeedback =
		{
			0.65143905753106f, 0.56471812200776f, 0.48954165955695f
		};

		public static void AddSquares(float[,] destination, int destinationBand, float[,,] source, int sourceBand, int length)
		{
			for (var time = 0; time < length; time++)
				destination[destinationBand, time] += source[sourceBand, time, 0] * source[sourceBand, time, 0] +
					source[sourceBand, time, 1] * source[sourceBand, time, 1];
		}

		public static void MultiplyPair(float[,,] destination, int destinationBand, float[,,] source, int sourceBand,
			int sourceTime, float[,] multiplier, int multiplierBand, int length)
		{
			for (var time = 0; time < length; time++)
			{
				destination[destinationBand, time, 0] = source[sourceBand, sourceTime + time, 0] * multiplier[multiplierBand, time];
				destination[destinationBand, time, 1] = source[sourceBand, sourceTime + time, 1] * multiplier[multiplierBand, time];
			}
		}

		/// <summary>Evaluates one complex 13-tap PS hybrid analysis bank in FFmpeg's scalar accumulation order.</summary>
		public static void HybridAnalysis(float[,,] output, int outputBand, int outputTime, float[,,] input,
			int inputBand, int inputTime, float[] filter, int filterOffset, int stride, int count)
		{
			Span<float> realEven = stackalloc float[6];
			Span<float> realOdd = stackalloc float[6];
			Span<float> imaginaryEven = stackalloc float[6];
			Span<float> imaginaryOdd = stackalloc float[6];
			for (var tap = 0; tap < 6; tap++)
			{
				realEven[tap] = input[inputBand, inputTime + tap, 0] + input[inputBand, inputTime + 12 - tap, 0];
				realOdd[tap] = input[inputBand, inputTime + tap, 1] - input[inputBand, inputTime + 12 - tap, 1];
				imaginaryEven[tap] = input[inputBand, inputTime + tap, 1] + input[inputBand, inputTime + 12 - tap, 1];
				imaginaryOdd[tap] = input[inputBand, inputTime + tap, 0] - input[inputBand, inputTime + 12 - tap, 0];
			}
			for (var band = 0; band < count; band++)
			{
				var baseOffset = filterOffset + band * 16;
				var real = filter[baseOffset + 12] * input[inputBand, inputTime + 6, 0];
				var imaginary = filter[baseOffset + 12] * input[inputBand, inputTime + 6, 1];
				for (var tap = 0; tap < 6; tap++)
				{
					real += filter[baseOffset + tap * 2] * realEven[tap] - filter[baseOffset + tap * 2 + 1] * realOdd[tap];
					imaginary += filter[baseOffset + tap * 2] * imaginaryEven[tap] + filter[baseOffset + tap * 2 + 1] * imaginaryOdd[tap];
				}
				output[outputBand + band * stride, outputTime, 0] = real;
				output[outputBand + band * stride, outputTime, 1] = imaginary;
			}
		}

		/// <summary>Runs the three-link fractional all-pass decorrelator with FFmpeg's exact delay update sequence.</summary>
		public static void Decorrelate(AacParametricStereo ps, int band, int parameterBand, float decaySlope)
		{
			Span<float> feedback = stackalloc float[3];
			for (var link = 0; link < 3; link++)
				feedback[link] = AllPassFeedback[link] * decaySlope;
			var tableBase = ((ps.Common.Is34Bands ? 1 : 0) * 50 + band) * 6;
			var phaseBase = ((ps.Common.Is34Bands ? 1 : 0) * 50 + band) * 2;
			for (var time = 0; time < 32; time++)
			{
				var delayReal = ps.Delay[band, time + 12, 0];
				var delayImaginary = ps.Delay[band, time + 12, 1];
				var inputReal = delayReal * AacPsTables.FractionalDelay[phaseBase] -
					delayImaginary * AacPsTables.FractionalDelay[phaseBase + 1];
				var inputImaginary = delayReal * AacPsTables.FractionalDelay[phaseBase + 1] +
					delayImaginary * AacPsTables.FractionalDelay[phaseBase];
				for (var link = 0; link < 3; link++)
				{
					var feedbackReal = feedback[link] * inputReal;
					var feedbackImaginary = feedback[link] * inputImaginary;
					var linkDelayReal = ps.AllPassDelay[band, link, time + 2 - link, 0];
					var linkDelayImaginary = ps.AllPassDelay[band, link, time + 2 - link, 1];
					var fractionalReal = AacPsTables.FractionalAllPass[tableBase + link * 2];
					var fractionalImaginary = AacPsTables.FractionalAllPass[tableBase + link * 2 + 1];
					var previousReal = inputReal;
					var previousImaginary = inputImaginary;
					inputReal = linkDelayReal * fractionalReal - linkDelayImaginary * fractionalImaginary;
					inputReal -= feedbackReal;
					inputImaginary = linkDelayReal * fractionalImaginary + linkDelayImaginary * fractionalReal;
					inputImaginary -= feedbackImaginary;
					ps.AllPassDelay[band, link, time + 5, 0] = previousReal + feedback[link] * inputReal;
					ps.AllPassDelay[band, link, time + 5, 1] = previousImaginary + feedback[link] * inputImaginary;
				}
				ps.RightBuffer[band, time, 0] = ps.TransientGain[parameterBand, time] * inputReal;
				ps.RightBuffer[band, time, 1] = ps.TransientGain[parameterBand, time] * inputImaginary;
			}
		}

		/// <summary>
		/// Applies PS hybrid-band stereo interpolation with FFmpeg's coefficient and optional phase-update schedule.
		/// </summary>
		public static void StereoInterpolate(AacParametricStereo ps, int band, int start, int length, bool phase)
		{
			var h0 = ps.Matrix[0, 0];
			var h1 = ps.Matrix[0, 1];
			var h2 = ps.Matrix[0, 2];
			var h3 = ps.Matrix[0, 3];
			var hs0 = ps.MatrixStep[0, 0];
			var hs1 = ps.MatrixStep[0, 1];
			var hs2 = ps.MatrixStep[0, 2];
			var hs3 = ps.MatrixStep[0, 3];
			if (!phase)
			{
				for (var offset = 0; offset < length; offset++)
				{
					var time = start + offset;
					var leftReal = ps.LeftBuffer[band, time, 0];
					var leftImaginary = ps.LeftBuffer[band, time, 1];
					var rightReal = ps.RightBuffer[band, time, 0];
					var rightImaginary = ps.RightBuffer[band, time, 1];
					h0 += hs0;
					h1 += hs1;
					h2 += hs2;
					h3 += hs3;
					ps.LeftBuffer[band, time, 0] = h0 * leftReal + h2 * rightReal;
					ps.LeftBuffer[band, time, 1] = h0 * leftImaginary + h2 * rightImaginary;
					ps.RightBuffer[band, time, 0] = h1 * leftReal + h3 * rightReal;
					ps.RightBuffer[band, time, 1] = h1 * leftImaginary + h3 * rightImaginary;
				}
				return;
			}

			var hi0 = ps.Matrix[1, 0];
			var hi1 = ps.Matrix[1, 1];
			var hi2 = ps.Matrix[1, 2];
			var hi3 = ps.Matrix[1, 3];
			var his0 = ps.MatrixStep[1, 0];
			var his1 = ps.MatrixStep[1, 1];
			var his2 = ps.MatrixStep[1, 2];
			var his3 = ps.MatrixStep[1, 3];
			for (var offset = 0; offset < length; offset++)
			{
				var time = start + offset;
				var leftReal = ps.LeftBuffer[band, time, 0];
				var leftImaginary = ps.LeftBuffer[band, time, 1];
				var rightReal = ps.RightBuffer[band, time, 0];
				var rightImaginary = ps.RightBuffer[band, time, 1];
				h0 += hs0;
				h1 += hs1;
				h2 += hs2;
				h3 += hs3;
				hi0 += his0;
				hi1 += his1;
				hi2 += his2;
				hi3 += his3;
				ps.LeftBuffer[band, time, 0] = h0 * leftReal + h2 * rightReal - hi0 * leftImaginary - hi2 * rightImaginary;
				ps.LeftBuffer[band, time, 1] = h0 * leftImaginary + h2 * rightImaginary + hi0 * leftReal + hi2 * rightReal;
				ps.RightBuffer[band, time, 0] = h1 * leftReal + h3 * rightReal - hi1 * leftImaginary - hi3 * rightImaginary;
				ps.RightBuffer[band, time, 1] = h1 * leftImaginary + h3 * rightImaginary + hi1 * leftReal + hi3 * rightReal;
			}
		}
	}
}
