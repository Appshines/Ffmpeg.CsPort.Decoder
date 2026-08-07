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

namespace Ffmpeg.CsPort.Decoder.Transforms
{
	/// <summary>
	/// Implements FFmpeg 8.1.2's scalar float MDCT and IMDCT codelets, including optimized PFA layouts.
	/// </summary>
	public sealed class FfmpegFloatMdct
	{
		private readonly bool inverse;
		private readonly int factor;
		private readonly int subLength;
		private readonly int[] inputMap;
		private readonly int[] outputMap;
		private readonly int[] subMap;
		private readonly FfmpegComplexFloat[] exponents;
		private readonly FfmpegComplexFloat[] work;
		private readonly FfmpegComplexFloat[] factorInput;
		private readonly FfmpegComplexFloat[] pfaOutput;

		public int Length { get; }

		public bool Inverse => inverse;

		public float Scale { get; }

		public bool FullInverse { get; }

		/// <summary>
		/// Builds FFmpeg-compatible maps, phase factors, and scratch buffers before decoding begins.
		/// </summary>
		public FfmpegFloatMdct(int length, bool inverse, float scale, bool fullInverse = false)
		{
			if (length < 2 || length > 262144 || (length & 1) != 0)
			{
				throw new ArgumentOutOfRangeException(nameof(length));
			}

			Length = length;
			this.inverse = inverse;
			Scale = scale;
			if (fullInverse && !inverse)
			{
				throw new ArgumentException("A full MDCT is available only for the inverse transform.", nameof(fullInverse));
			}
			FullInverse = fullInverse;
			FfmpegFloatTransformKernel.InitializeTables();
			var halfLength = length >> 1;
			work = new FfmpegComplexFloat[halfLength];
			factor = SelectPfaFactor(halfLength);
			if (factor == 0)
			{
				subLength = halfLength;
				subMap = GenerateSubMap(subLength, inverse, !inverse);
				inputMap = new int[halfLength];
				Array.Copy(subMap, inputMap, halfLength);
				exponents = CreateExponents(length, scale, inverse ? inputMap : null);
				if (inverse)
				{
					DoubleInputMap(inputMap);
				}
				return;
			}

			subLength = halfLength / factor;
			(inputMap, outputMap) = FfmpegFloatFft.GenerateCompoundMap(factor, subLength, inverse);
			if (factor == 15)
			{
				EmbedFifteenPointMap(inputMap);
			}
			exponents = CreateExponents(length, scale, inverse ? inputMap : null);
			DoubleInputMap(inputMap);
			subMap = GenerateSubMap(subLength, inverse, true);
			factorInput = new FfmpegComplexFloat[factor];
			pfaOutput = new FfmpegComplexFloat[halfLength];
		}

		/// <summary>
		/// Runs a forward MDCT from 2*Length samples to Length coefficients or a half IMDCT from Length coefficients.
		/// </summary>
		public void Transform(ReadOnlySpan<float> input, Span<float> output, int outputStride = 1)
		{
			var requiredInput = inverse ? Length : checked(Length * 2);
			if (input.Length < requiredInput)
			{
				throw new ArgumentException("MDCT input is shorter than the configured transform requires.", nameof(input));
			}
			var outputLength = FullInverse ? checked(Length * 2) : Length;
			if (outputStride <= 0 || output.Length < checked((outputLength - 1) * outputStride + 1))
			{
				throw new ArgumentException("MDCT output cannot hold the configured transform and stride.", nameof(output));
			}

			if (FullInverse)
			{
				TransformFullInverse(input, output, outputStride);
				return;
			}

			TransformHalf(input, output, outputStride);
		}

		private void TransformHalf(ReadOnlySpan<float> input, Span<float> output, int outputStride)
		{
			if (factor == 0)
			{
				if (inverse)
				{
					TransformInverse(input, output, outputStride);
				} else
				{
					TransformForward(input, output, outputStride);
				}
				return;
			}

			if (inverse)
			{
				TransformPfaInverse(input, output, outputStride);
			} else
			{
				TransformPfaForward(input, output, outputStride);
			}
		}

		/// <summary>
		/// Expands FFmpeg's half IMDCT into AV_TX_FULL_IMDCT order using the same two mirror assignments.
		/// </summary>
		private void TransformFullInverse(ReadOnlySpan<float> input, Span<float> output, int outputStride)
		{
			var halfLength = Length >> 1;
			TransformHalf(input, output.Slice(halfLength * outputStride), outputStride);
			for (var index = 0; index < halfLength; index++)
			{
				output[index * outputStride] = -output[(Length - index - 1) * outputStride];
				output[(Length * 2 - index - 1) * outputStride] = output[(Length + index) * outputStride];
			}
		}

		/// <summary>
		/// Performs FFmpeg's general forward pre-rotation, preshuffled FFT, and paired post-rotation.
		/// </summary>
		private void TransformForward(ReadOnlySpan<float> source, Span<float> destination, int stride)
		{
			var halfLength = Length >> 1;
			var quarterLength = Length >> 2;
			var threeHalves = halfLength * 3;
			for (var index = 0; index < halfLength; index++)
			{
				var k = 2 * index;
				float real;
				float imaginary;
				if (k < halfLength)
				{
					real = -source[halfLength + k] + source[halfLength - 1 - k];
					imaginary = -source[threeHalves + k] + -source[threeHalves - 1 - k];
				} else
				{
					real = -source[halfLength + k] + -source[5 * halfLength - 1 - k];
					imaginary = source[-halfLength + k] + -source[threeHalves - 1 - k];
				}

				var exponent = exponents[index];
				work[inputMap[index]].Imaginary = real * exponent.Real - imaginary * exponent.Imaginary;
				work[inputMap[index]].Real = real * exponent.Imaginary + imaginary * exponent.Real;
			}

			ExecuteSubTransform(work, 0, subLength);
			CopyComplexOutput(destination, stride);
			if (quarterLength == 0)
			{
				return;
			}
			for (var index = 0; index < quarterLength; index++)
			{
				var i0 = quarterLength + index;
				var i1 = quarterLength - index - 1;
				var source1 = work[i1];
				var source0 = work[i0];
				MultiplyToPair(source0, exponents[i0].Imaginary, exponents[i0].Real,
					out destination[(2 * i1 + 1) * stride], out destination[2 * i0 * stride]);
				MultiplyToPair(source1, exponents[i1].Imaginary, exponents[i1].Real,
					out destination[(2 * i0 + 1) * stride], out destination[2 * i1 * stride]);
			}
		}

		/// <summary>
		/// Performs FFmpeg's general inverse pre-rotation, preshuffled FFT, and in-place half-IMDCT post-rotation.
		/// </summary>
		private void TransformInverse(ReadOnlySpan<float> source, Span<float> destination, int stride)
		{
			var halfLength = Length >> 1;
			var quarterLength = Length >> 2;
			for (var index = 0; index < halfLength; index++)
			{
				var k = inputMap[index];
				var value = new FfmpegComplexFloat(source[Length - 1 - k], source[k]);
				work[index] = Multiply(value, exponents[index]);
			}

			ExecuteSubTransform(work, 0, subLength);
			for (var index = 0; index < quarterLength; index++)
			{
				var i0 = quarterLength + index;
				var i1 = quarterLength - index - 1;
				var source1 = new FfmpegComplexFloat(work[i1].Imaginary, work[i1].Real);
				var source0 = new FfmpegComplexFloat(work[i0].Imaginary, work[i0].Real);
				MultiplyToPair(source1, exponents[halfLength + i1].Imaginary, exponents[halfLength + i1].Real,
					out work[i1].Real, out work[i0].Imaginary);
				MultiplyToPair(source0, exponents[halfLength + i0].Imaginary, exponents[halfLength + i0].Real,
					out work[i0].Real, out work[i1].Imaginary);
			}

			CopyComplexOutput(destination, stride);
		}

		/// <summary>
		/// Runs the optimized forward MDCT factor codelets in columns, then the preshuffled FFT rows and mapped post-rotation.
		/// </summary>
		private void TransformPfaForward(ReadOnlySpan<float> source, Span<float> destination, int stride)
		{
			var halfLength = Length >> 1;
			var threeHalves = halfLength * 3;
			var quarterLength = Length >> 2;
			for (var row = 0; row < subLength; row++)
			{
				for (var column = 0; column < factor; column++)
				{
					var k = inputMap[row * factor + column];
					float real;
					float imaginary;
					if (k < halfLength)
					{
						real = -source[halfLength + k] + source[halfLength - 1 - k];
						imaginary = -source[threeHalves + k] + -source[threeHalves - 1 - k];
					} else
					{
						real = -source[halfLength + k] + -source[5 * halfLength - 1 - k];
						imaginary = source[-halfLength + k] + -source[threeHalves - 1 - k];
					}

					var exponent = exponents[k >> 1];
					factorInput[column].Imaginary = real * exponent.Real - imaginary * exponent.Imaginary;
					factorInput[column].Real = real * exponent.Imaginary + imaginary * exponent.Real;
				}

				FfmpegFloatTransformKernel.FactorFft(work, subMap[row], factorInput, 0, subLength, factor);
			}

			for (var column = 0; column < factor; column++)
			{
				ExecuteSubTransform(work, column * subLength, subLength);
			}
			for (var index = 0; index < quarterLength; index++)
			{
				var i0 = quarterLength + index;
				var i1 = quarterLength - index - 1;
				var source1 = work[outputMap[i1]];
				var source0 = work[outputMap[i0]];
				MultiplyToPair(source0, exponents[i0].Imaginary, exponents[i0].Real,
					out destination[(2 * i1 + 1) * stride], out destination[2 * i0 * stride]);
				MultiplyToPair(source1, exponents[i1].Imaginary, exponents[i1].Real,
					out destination[(2 * i0 + 1) * stride], out destination[2 * i1 * stride]);
			}
		}

		/// <summary>
		/// Runs the optimized inverse MDCT factor columns and FFT rows before FFmpeg's mapped half-IMDCT post-rotation.
		/// </summary>
		private void TransformPfaInverse(ReadOnlySpan<float> source, Span<float> destination, int stride)
		{
			var halfLength = Length >> 1;
			var quarterLength = Length >> 2;
			var exponentOffset = 0;
			for (var row = 0; row < halfLength; row += factor)
			{
				for (var column = 0; column < factor; column++)
				{
					var k = inputMap[row + column];
					var value = new FfmpegComplexFloat(source[Length - 1 - k], source[k]);
					factorInput[column] = Multiply(value, exponents[exponentOffset + column]);
				}

				FfmpegFloatTransformKernel.FactorFft(
					work,
					subMap[row / factor],
					factorInput,
					0,
					subLength,
					factor);
				exponentOffset += factor;
			}

			for (var column = 0; column < factor; column++)
			{
				ExecuteSubTransform(work, column * subLength, subLength);
			}
			for (var index = 0; index < quarterLength; index++)
			{
				var i0 = quarterLength + index;
				var i1 = quarterLength - index - 1;
				var source1Value = work[outputMap[i1]];
				var source0Value = work[outputMap[i0]];
				var source1 = new FfmpegComplexFloat(source1Value.Imaginary, source1Value.Real);
				var source0 = new FfmpegComplexFloat(source0Value.Imaginary, source0Value.Real);
				MultiplyToPair(source1, exponents[halfLength + i1].Imaginary, exponents[halfLength + i1].Real,
					out pfaOutput[i1].Real, out pfaOutput[i0].Imaginary);
				MultiplyToPair(source0, exponents[halfLength + i0].Imaginary, exponents[halfLength + i0].Real,
					out pfaOutput[i0].Real, out pfaOutput[i1].Imaginary);
			}

			CopyComplexOutput(pfaOutput, destination, stride);
		}

		private void ExecuteSubTransform(FfmpegComplexFloat[] values, int offset, int length)
		{
			if ((length & (length - 1)) == 0)
			{
				FfmpegFloatTransformKernel.PowerOfTwoFft(values, offset, length);
			} else
			{
				FfmpegFloatTransformKernel.FactorFft(values, offset, values, offset, 1, length);
			}
		}

		private void CopyComplexOutput(Span<float> destination, int stride)
		{
			CopyComplexOutput(work, destination, stride);
		}

		private static void CopyComplexOutput(FfmpegComplexFloat[] source, Span<float> destination, int stride)
		{
			for (var index = 0; index < source.Length; index++)
			{
				destination[index * 2 * stride] = source[index].Real;
				destination[(index * 2 + 1) * stride] = source[index].Imaginary;
			}
		}

		private static FfmpegComplexFloat Multiply(FfmpegComplexFloat first, FfmpegComplexFloat second)
		{
			return new FfmpegComplexFloat(
				first.Real * second.Real - first.Imaginary * second.Imaginary,
				first.Real * second.Imaginary + first.Imaginary * second.Real);
		}

		private static void MultiplyToPair(FfmpegComplexFloat value, float real, float imaginary, out float first, out float second)
		{
			first = value.Real * real - value.Imaginary * imaginary;
			second = value.Real * imaginary + value.Imaginary * real;
		}

		private static FfmpegComplexFloat[] CreateExponents(int length, float inputScale, int[] preTable)
		{
			var halfLength = length >> 1;
			var result = new FfmpegComplexFloat[preTable == null ? halfLength : halfLength * 2];
			var offset = preTable == null ? 0 : halfLength;
			var theta = (inputScale < 0 ? halfLength : 0) + 1.0 / 8.0;
			var scale = Math.Sqrt(Math.Abs((double)inputScale));
			for (var index = 0; index < halfLength; index++)
			{
				var alpha = Math.PI / 2.0 * (index + theta) / halfLength;
				result[offset + index] = new FfmpegComplexFloat(
					(float)(Math.Cos(alpha) * scale),
					(float)(Math.Sin(alpha) * scale));
			}
			if (preTable != null)
			{
				for (var index = 0; index < halfLength; index++)
				{
					result[index] = result[halfLength + preTable[index]];
				}
			}
			return result;
		}

		private static int[] GenerateSubMap(int length, bool inverse, bool scatter)
		{
			if ((length & (length - 1)) == 0)
			{
				return FfmpegFloatFft.GeneratePowerOfTwoMap(length, inverse, scatter);
			}

			var gather = FfmpegFloatFft.GenerateFactorMap(length, inverse, scatter);
			if (!scatter || length == 15)
			{
				return gather;
			}
			var result = new int[length];
			for (var index = 0; index < length; index++)
			{
				result[gather[index]] = index;
			}
			return result;
		}

		private static int SelectPfaFactor(int halfLength)
		{
			if ((halfLength & (halfLength - 1)) == 0 || halfLength == 3 || halfLength == 5 ||
				halfLength == 7 || halfLength == 9)
			{
				return 0;
			}
			if (halfLength == 15)
			{
				return 5;
			}

			var odd = halfLength;
			while ((odd & 1) == 0)
			{
				odd >>= 1;
			}
			if (odd == 3 || odd == 5 || odd == 7 || odd == 9 || odd == 15)
			{
				return odd;
			}
			return 0;
		}

		private static void DoubleInputMap(int[] map)
		{
			for (var index = 0; index < map.Length; index++)
			{
				map[index] <<= 1;
			}
		}

		private static void EmbedFifteenPointMap(int[] map)
		{
			Span<int> temporary = stackalloc int[15];
			for (var offset = 0; offset < map.Length; offset += 15)
			{
				for (var index = 0; index < 15; index++)
				{
					temporary[index] = map[offset + index];
				}
				for (var m = 0; m < 5; m++)
				{
					for (var n = 0; n < 3; n++)
					{
						map[offset + m * 3 + n] = temporary[(m * 3 + n * 5) % 15];
					}
				}
			}
		}
	}
}
