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

namespace Ffmpeg.CsPort.Decoder.Codecs.Alac
{
	/// <summary>
	/// Ports ALAC adaptive LPC prediction, stereo decorrelation, and low-bit restoration in source operation order.
	/// </summary>
	internal static class AlacPrediction
	{
		/// <summary>
		/// Reconstructs one channel and adapts its coefficient vector from each signed residual exactly as alac.c does.
		/// </summary>
		public static void Predict(
			int[] errorBuffer,
			int[] output,
			int numberOfSamples,
			int bitsPerSample,
			short[] coefficients,
			int order,
			int quantization)
		{
			output[0] = errorBuffer[0];
			if (numberOfSamples <= 1)
				return;
			if (order == 0)
			{
				Array.Copy(errorBuffer, 1, output, 1, numberOfSamples - 1);
				return;
			}
			if (order == 31)
			{
				for (var index = 1; index < numberOfSamples; index++)
					output[index] = FfmpegMath.SignExtend(unchecked((uint)(output[index - 1] + errorBuffer[index])), bitsPerSample);
				return;
			}

			var sample = 1;
			for (; sample <= order && sample < numberOfSamples; sample++)
				output[sample] = FfmpegMath.SignExtend(unchecked((uint)(output[sample - 1] + errorBuffer[sample])), bitsPerSample);

			var predictionOffset = 0;
			for (; sample < numberOfSamples; sample++, predictionOffset++)
			{
				var prediction = 0;
				var errorValue = unchecked((uint)errorBuffer[sample]);
				var baseValue = output[predictionOffset];
				for (var coefficient = 0; coefficient < order; coefficient++)
					prediction = unchecked(prediction + (output[predictionOffset + coefficient + 1] - baseValue) * coefficients[coefficient]);
				prediction = unchecked((int)((prediction + (1L << (quantization - 1))) >> quantization));
				prediction = unchecked((int)((uint)prediction + (uint)baseValue + errorValue));
				output[sample] = FfmpegMath.SignExtend(unchecked((uint)prediction), bitsPerSample);

				var errorSign = SignOnly(unchecked((int)errorValue));
				if (errorSign == 0)
					continue;
				for (var coefficient = 0; coefficient < order && unchecked((int)(errorValue * (uint)errorSign)) > 0; coefficient++)
				{
					var difference = baseValue - output[predictionOffset + coefficient + 1];
					var sign = SignOnly(difference) * errorSign;
					coefficients[coefficient] = unchecked((short)(coefficients[coefficient] - sign));
					difference = unchecked((int)((uint)difference * (uint)sign));
					errorValue = unchecked(errorValue - (uint)(difference >> quantization) * (uint)(coefficient + 1));
				}
			}
		}

		public static void DecorrelateStereo(int[] first, int[] second, int numberOfSamples, int shift, int leftWeight)
		{
			for (var index = 0; index < numberOfSamples; index++)
			{
				var firstValue = unchecked((uint)first[index]);
				var secondValue = unchecked((uint)second[index]);
				firstValue = unchecked(firstValue - (uint)(unchecked((int)(secondValue * (uint)leftWeight)) >> shift));
				secondValue = unchecked(secondValue + firstValue);
				first[index] = unchecked((int)secondValue);
				second[index] = unchecked((int)firstValue);
			}
		}

		public static void AppendExtraBits(int[] output, int[] extraBits, int bitCount, int numberOfSamples)
		{
			for (var index = 0; index < numberOfSamples; index++)
				output[index] = unchecked((int)(((uint)output[index] << bitCount) | (uint)extraBits[index]));
		}

		private static int SignOnly(int value)
		{
			return value > 0 ? 1 : value < 0 ? -1 : 0;
		}
	}
}
