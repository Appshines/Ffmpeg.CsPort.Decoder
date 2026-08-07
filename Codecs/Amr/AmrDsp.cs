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

namespace Ffmpeg.CsPort.Decoder.Codecs.Amr
{
	/// <summary>Ports the scalar CELP, ACELP, LSP, and AMR gain kernels used by FFmpeg 8.1.2.</summary>
	internal static class AmrDsp
	{
		internal static readonly float[] Pow07 =
		{
			0.700000f, 0.490000f, 0.343000f, 0.240100f, 0.168070f,
			0.117649f, 0.082354f, 0.057648f, 0.040354f, 0.028248f
		};

		internal static readonly float[] Pow075 =
		{
			0.750000f, 0.562500f, 0.421875f, 0.316406f, 0.237305f,
			0.177979f, 0.133484f, 0.100113f, 0.075085f, 0.056314f
		};

		internal static readonly float[] Pow055 =
		{
			0.550000f, 0.302500f, 0.166375f, 0.091506f, 0.050328f,
			0.027681f, 0.015224f, 0.008373f, 0.004605f, 0.002533f
		};

		internal static readonly float[] B60Sinc =
		{
			0.898529f, 0.865051f, 0.769257f, 0.624054f, 0.448639f, 0.265289f,
			0.0959167f, -0.0412598f, -0.134338f, -0.178986f, -0.178528f, -0.142609f,
			-0.0849304f, -0.0205078f, 0.0369568f, 0.0773926f, 0.0955200f, 0.0912781f,
			0.0689392f, 0.0357056f, 0.0f, -0.0305481f, -0.0504150f, -0.0570068f,
			-0.0508423f, -0.0350037f, -0.0141602f, 0.00665283f, 0.0230713f, 0.0323486f,
			0.0335388f, 0.0275879f, 0.0167847f, 0.00411987f, -0.00747681f, -0.0156860f,
			-0.0193481f, -0.0183716f, -0.0137634f, -0.00704956f, 0.0f, 0.00582886f,
			0.00939941f, 0.0103760f, 0.00903320f, 0.00604248f, 0.00238037f, -0.00109863f,
			-0.00366211f, -0.00497437f, -0.00503540f, -0.00402832f, -0.00241089f, -0.000579834f,
			0.00103760f, 0.00222778f, 0.00277710f, 0.00271606f, 0.00213623f, 0.00115967f, 0.0f
		};

		internal static void WeightedVectorSum(float[] output, int outputOffset, float[] first, int firstOffset,
			float[] second, int secondOffset, float firstWeight, float secondWeight, int length)
		{
			for (var index = 0; index < length; index++)
				output[outputOffset + index] = firstWeight * first[firstOffset + index] + secondWeight * second[secondOffset + index];
		}

		internal static float ScalarProduct(float[] first, int firstOffset, float[] second, int secondOffset, int length)
		{
			var result = 0.0f;
			for (var index = 0; index < length; index++) result += first[firstOffset + index] * second[secondOffset + index];
			return result;
		}

		internal static void SetMinimumLsfDistance(float[] lsf, int offset, double minimumSpacing, int length)
		{
			var previous = 0.0f;
			for (var index = 0; index < length; index++)
			{
				previous = (float)Math.Max(lsf[offset + index], previous + minimumSpacing);
				lsf[offset + index] = previous;
			}
		}

		internal static void LsfToLsp(double[] lsp, int lspOffset, float[] lsf, int lsfOffset, int order)
		{
			for (var index = 0; index < order; index++) lsp[lspOffset + index] = Math.Cos(2.0 * Math.PI * lsf[lsfOffset + index]);
		}

		internal static void LspToLpc(double[] lsp, int lspOffset, float[] lpc, int lpcOffset, int halfOrder,
			double[] firstPolynomial, double[] secondPolynomial)
		{
			LspToPolynomial(lsp, lspOffset, firstPolynomial, halfOrder);
			LspToPolynomial(lsp, lspOffset + 1, secondPolynomial, halfOrder);
			var outputEnd = lpcOffset + (halfOrder << 1) - 1;
			while (halfOrder-- != 0)
			{
				var first = firstPolynomial[halfOrder + 1] + firstPolynomial[halfOrder];
				var second = secondPolynomial[halfOrder + 1] - secondPolynomial[halfOrder];
				lpc[lpcOffset + halfOrder] = (float)(0.5 * (first + second));
				lpc[outputEnd - halfOrder] = (float)(0.5 * (first - second));
			}
		}

		internal static void WideBandLspToLpc(double[] lsp, int lspOffset, float[] lpc, int lpcOffset,
			double[] firstPolynomial, double[] secondPolynomial)
		{
			const int order = 16;
			const int halfOrder = order / 2;
			LspToPolynomial(lsp, lspOffset, firstPolynomial, 0, halfOrder);
			secondPolynomial[0] = 0.0;
			LspToPolynomial(lsp, lspOffset + 1, secondPolynomial, 1, halfOrder - 1);
			for (var index = 1; index < halfOrder; index++)
			{
				var first = firstPolynomial[index] * (1 + lsp[lspOffset + order - 1]);
				var second = (secondPolynomial[index + 1] - secondPolynomial[index - 1]) * (1 - lsp[lspOffset + order - 1]);
				lpc[lpcOffset + index - 1] = (float)((first + second) * 0.5);
				lpc[lpcOffset + order - index - 1] = (float)((first - second) * 0.5);
			}
			lpc[lpcOffset + halfOrder - 1] = (float)((1.0 + lsp[lspOffset + order - 1]) * firstPolynomial[halfOrder] * 0.5);
			lpc[lpcOffset + order - 1] = (float)lsp[lspOffset + order - 1];
		}

		private static void LspToPolynomial(double[] lsp, int lspOffset, double[] polynomial, int halfOrder)
		{
			LspToPolynomial(lsp, lspOffset, polynomial, 0, halfOrder);
		}

		private static void LspToPolynomial(double[] lsp, int lspOffset, double[] polynomial, int polynomialOffset, int halfOrder)
		{
			polynomial[polynomialOffset] = 1.0;
			polynomial[polynomialOffset + 1] = -2 * lsp[lspOffset];
			for (var index = 2; index <= halfOrder; index++)
			{
				var value = -2 * lsp[lspOffset + 2 * (index - 1)];
				polynomial[polynomialOffset + index] = value * polynomial[polynomialOffset + index - 1] + 2 * polynomial[polynomialOffset + index - 2];
				for (var inner = index - 1; inner > 1; inner--)
					polynomial[polynomialOffset + inner] += polynomial[polynomialOffset + inner - 1] * value + polynomial[polynomialOffset + inner - 2];
				polynomial[polynomialOffset + 1] += value;
			}
		}

		internal static void Interpolate(float[] output, int outputOffset, float[] input, int inputOffset,
			float[] coefficients, int precision, int fractionalPosition, int filterLength, int length)
		{
			for (var sample = 0; sample < length; sample++)
			{
				var coefficientIndex = 0;
				var value = 0.0f;
				for (var index = 0; index < filterLength;)
				{
					value += input[inputOffset + sample + index] * coefficients[coefficientIndex + fractionalPosition];
					coefficientIndex += precision;
					index++;
					value += input[inputOffset + sample - index] * coefficients[coefficientIndex - fractionalPosition];
				}
				output[outputOffset + sample] = value;
			}
		}

		internal static void DecodeTenPulses35Bits(ushort[] indexes, int indexesOffset, AmrFixedVector vector,
			byte[] grayDecode, int halfPulseCount, int bits)
		{
			var mask = (1 << bits) - 1;
			vector.NoRepeatMask = 0;
			vector.Count = 2 * halfPulseCount;
			for (var index = 0; index < halfPulseCount; index++)
			{
				var position1 = grayDecode[indexes[indexesOffset + 2 * index + 1] & mask] + index;
				var position2 = grayDecode[indexes[indexesOffset + 2 * index] & mask] + index;
				var sign = (indexes[indexesOffset + 2 * index + 1] & 1 << bits) != 0 ? -1.0f : 1.0f;
				vector.Positions[2 * index + 1] = position1;
				vector.Positions[2 * index] = position2;
				vector.Values[2 * index + 1] = sign;
				vector.Values[2 * index] = position2 < position1 ? -sign : sign;
			}
		}

		internal static void SetFixedVector(float[] output, int outputOffset, AmrFixedVector vector, float scale, int size)
		{
			for (var index = 0; index < vector.Count; index++)
			{
				var position = vector.Positions[index];
				var repeats = (vector.NoRepeatMask >> index & 1) == 0;
				var value = vector.Values[index] * scale;
				if (vector.PitchLag > 0)
					do
					{
						output[outputOffset + position] += value;
						value *= vector.PitchFactor;
						position += vector.PitchLag;
					} while (position < size && repeats);
			}
		}

		internal static void ClearFixedVector(float[] output, int outputOffset, AmrFixedVector vector, int size)
		{
			for (var index = 0; index < vector.Count; index++)
			{
				var position = vector.Positions[index];
				var repeats = (vector.NoRepeatMask >> index & 1) == 0;
				if (vector.PitchLag > 0)
					do
					{
						output[outputOffset + position] = 0.0f;
						position += vector.PitchLag;
					} while (position < size && repeats);
			}
		}

		internal static void DecodePitchLag(out int lagInteger, out int lagFraction, int pitchIndex, int previousLagInteger,
			int subframe, bool thirdAsFirst, int resolution, int minimumDelay, int maximumDelay)
		{
			if (subframe == 0 || subframe == 2 && thirdAsFirst)
			{
				if (pitchIndex < 197) pitchIndex += 59;
				else pitchIndex = 3 * pitchIndex - 335;
			} else if (resolution == 4)
			{
				var searchMinimum = Math.Clamp(previousLagInteger - 5, minimumDelay, maximumDelay - 9);
				if (pitchIndex < 4) pitchIndex = 3 * (pitchIndex + searchMinimum) + 1;
				else if (pitchIndex < 12) pitchIndex += 3 * searchMinimum + 7;
				else pitchIndex = 3 * (pitchIndex + searchMinimum - 6) + 1;
			} else
			{
				pitchIndex--;
				if (resolution == 5) pitchIndex += 3 * Math.Clamp(previousLagInteger - 10, minimumDelay, maximumDelay - 19);
				else pitchIndex += 3 * Math.Clamp(previousLagInteger - 5, minimumDelay, maximumDelay - 9);
			}
			lagInteger = pitchIndex * 10923 >> 15;
			lagFraction = pitchIndex - 3 * lagInteger - 1;
		}

		internal static float SetAmrFixedGain(float gainFactor, float fixedMeanEnergy, float[] predictionError,
			float energyMean, float[] predictionTable)
		{
			var value = (float)(gainFactor * Math.Pow(2.0, 3.32192809488736234787 * 0.05 *
				(ScalarProduct(predictionTable, 0, predictionError, 0, 4) + energyMean)) /
				MathF.Sqrt(fixedMeanEnergy != 0.0f ? fixedMeanEnergy : 1.0f));
			predictionError[0] = predictionError[1];
			predictionError[1] = predictionError[2];
			predictionError[2] = predictionError[3];
			predictionError[3] = (float)(20.0 * MathF.Log10(gainFactor));
			return value;
		}

		internal static void CircularAdd(float[] output, int outputOffset, float[] input, int inputOffset,
			float[] lagged, int laggedOffset, int lag, float factor, int length)
		{
			var index = 0;
			for (; index < lag; index++) output[outputOffset + index] = input[inputOffset + index] + factor * lagged[laggedOffset + length + index - lag];
			for (; index < length; index++) output[outputOffset + index] = input[inputOffset + index] + factor * lagged[laggedOffset + index - lag];
		}

		/// <summary>Executes FFmpeg's four-sample scalar CELP synthesis loop in its original arithmetic order.</summary>
		internal static void SynthesisFilter(float[] output, int outputOffset, float[] coefficients, int coefficientsOffset,
			float[] input, int inputOffset, int bufferLength, int filterLength)
		{
			var a = coefficients[coefficientsOffset];
			var b = coefficients[coefficientsOffset + 1];
			var c = coefficients[coefficientsOffset + 2];
			b -= coefficients[coefficientsOffset] * coefficients[coefficientsOffset];
			c -= coefficients[coefficientsOffset + 1] * coefficients[coefficientsOffset];
			c -= coefficients[coefficientsOffset] * b;
			var old0 = output[outputOffset - 4];
			var old1 = output[outputOffset - 3];
			var old2 = output[outputOffset - 2];
			var old3 = output[outputOffset - 1];
			var sample = 0;
			for (; sample <= bufferLength - 4; sample += 4)
			{
				var current = outputOffset + sample;
				var inputCurrent = inputOffset + sample;
				var out0 = input[inputCurrent];
				var out1 = input[inputCurrent + 1];
				var out2 = input[inputCurrent + 2];
				var out3 = input[inputCurrent + 3];
				out0 -= coefficients[coefficientsOffset + 2] * old1;
				out1 -= coefficients[coefficientsOffset + 2] * old2;
				out2 -= coefficients[coefficientsOffset + 2] * old3;
				out0 -= coefficients[coefficientsOffset + 1] * old2;
				out1 -= coefficients[coefficientsOffset + 1] * old3;
				out0 -= coefficients[coefficientsOffset] * old3;
				var value = coefficients[coefficientsOffset + 3];
				out0 -= value * old0; out1 -= value * old1; out2 -= value * old2; out3 -= value * old3;
				for (var index = 5; index < filterLength; index += 2)
				{
					old3 = output[current - index]; value = coefficients[coefficientsOffset + index - 1];
					out0 -= value * old3; out1 -= value * old0; out2 -= value * old1; out3 -= value * old2;
					old2 = output[current - index - 1]; value = coefficients[coefficientsOffset + index];
					out0 -= value * old2; out1 -= value * old3; out2 -= value * old0; out3 -= value * old1;
					(old0, old2) = (old2, old0); old1 = old3;
				}
				var temporary0 = out0; var temporary1 = out1; var temporary2 = out2;
				out3 -= a * temporary2; out2 -= a * temporary1; out1 -= a * temporary0;
				out3 -= b * temporary1; out2 -= b * temporary0; out3 -= c * temporary0;
				output[current] = out0; output[current + 1] = out1; output[current + 2] = out2; output[current + 3] = out3;
				old0 = out0; old1 = out1; old2 = out2; old3 = out3;
			}
			for (; sample < bufferLength; sample++)
			{
				output[outputOffset + sample] = input[inputOffset + sample];
				for (var index = 1; index <= filterLength; index++)
					output[outputOffset + sample] -= coefficients[coefficientsOffset + index - 1] * output[outputOffset + sample - index];
			}
		}

		internal static void ZeroSynthesisFilter(float[] output, int outputOffset, float[] coefficients, int coefficientsOffset,
			float[] input, int inputOffset, int bufferLength, int filterLength)
		{
			for (var sample = 0; sample < bufferLength; sample++)
			{
				output[outputOffset + sample] = input[inputOffset + sample];
				for (var index = 1; index <= filterLength; index++)
					output[outputOffset + sample] += coefficients[coefficientsOffset + index - 1] * input[inputOffset + sample - index];
			}
		}

		internal static void TiltCompensation(ref float memory, float tilt, float[] samples, int offset, int size)
		{
			var newMemory = samples[offset + size - 1];
			for (var index = size - 1; index > 0; index--) samples[offset + index] -= tilt * samples[offset + index - 1];
			samples[offset] -= tilt * memory;
			memory = newMemory;
		}

		internal static void AdaptiveGainControl(float[] output, int outputOffset, float[] input, int inputOffset,
			float speechEnergy, int size, float alpha, ref float gainMemory)
		{
			var filterEnergy = ScalarProduct(input, inputOffset, input, inputOffset, size);
			var gainScale = 1.0f;
			var memory = gainMemory;
			if (filterEnergy != 0.0f) gainScale = (float)Math.Sqrt(speechEnergy / filterEnergy);
			gainScale *= 1.0f - alpha;
			for (var index = 0; index < size; index++)
			{
				memory = alpha * memory + gainScale;
				output[outputOffset + index] = input[inputOffset + index] * memory;
			}
			gainMemory = memory;
		}

		internal static void ScaleToEnergy(float[] output, int outputOffset, float[] input, int inputOffset,
			float sumOfSquares, int length)
		{
			var scale = ScalarProduct(input, inputOffset, input, inputOffset, length);
			if (scale != 0.0f) scale = (float)Math.Sqrt(sumOfSquares / scale);
			for (var index = 0; index < length; index++) output[outputOffset + index] = input[inputOffset + index] * scale;
		}

		internal static void ApplySecondOrderTransfer(float[] output, int outputOffset, float[] input, int inputOffset,
			float[] zeros, float[] poles, float gain, float[] memory, int length)
		{
			for (var index = 0; index < length; index++)
			{
				var temporary = gain * input[inputOffset + index];
				temporary -= poles[0] * memory[0];
				temporary -= poles[1] * memory[1];
				var sample = temporary + zeros[0] * memory[0];
				sample += zeros[1] * memory[1];
				output[outputOffset + index] = sample;
				memory[1] = memory[0];
				memory[0] = temporary;
			}
		}
	}

	/// <summary>Holds one sparse AMR algebraic-codebook vector without per-frame allocation.</summary>
	internal sealed class AmrFixedVector
	{
		internal int Count;
		internal int NoRepeatMask;
		internal int PitchLag;
		internal float PitchFactor;
		internal int[] Positions { get; } = new int[16];
		internal float[] Values { get; } = new float[16];
	}
}
