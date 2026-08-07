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
	/// Implements FFmpeg 8.1.2's scalar float FFT codelet selection, input mapping, and arithmetic order.
	/// </summary>
	public sealed class FfmpegFloatFft
	{
		private readonly bool inverse;
		private readonly int factor;
		private readonly int[] inputMap;
		private readonly int[] outputMap;
		private readonly int[] powerScatterMap;
		private readonly bool secondFactorIsPowerOfTwo;
		private readonly bool firstFactorUsesNaiveKernel;
		private readonly bool secondFactorUsesNaiveKernel;
		private readonly FfmpegComplexFloat[] work;
		private readonly FfmpegComplexFloat[] factorInput;
		private readonly FfmpegComplexFloat[] naiveExponents;
		private readonly FfmpegComplexFloat[] naiveInput;
		private readonly FfmpegComplexFloat[] firstNaiveExponents;

		public int Length { get; }

		public bool Inverse => inverse;

		/// <summary>
		/// Initializes all maps and scratch storage so transform execution performs no allocations.
		/// </summary>
		public FfmpegFloatFft(int length, bool inverse)
		{
			if (length < 2 || length > 131072)
			{
				throw new ArgumentOutOfRangeException(nameof(length));
			}

			Length = length;
			this.inverse = inverse;
			FfmpegFloatTransformKernel.InitializeTables();
			work = new FfmpegComplexFloat[length];
			if (IsPowerOfTwo(length))
			{
				inputMap = GeneratePowerOfTwoMap(length, inverse, false);
				return;
			}

			factor = inverse && length == 15 ? 3 : SelectOddFactor(length);
			if (factor == length)
			{
				inputMap = GenerateFactorMap(length, inverse);
				return;
			}

			var powerLength = length / factor;
			(inputMap, outputMap) = GenerateCompoundMap(factor, powerLength, false);
			firstFactorUsesNaiveKernel = !IsDirectFactor(factor);
			if (!firstFactorUsesNaiveKernel) EmbedFactorMap(inputMap, factor, powerLength, inverse);
			secondFactorIsPowerOfTwo = IsPowerOfTwo(powerLength);
			secondFactorUsesNaiveKernel = !secondFactorIsPowerOfTwo && !IsDirectFactor(powerLength);
			powerScatterMap = secondFactorIsPowerOfTwo
				? GeneratePowerOfTwoMap(powerLength, inverse, true)
				: GenerateFactorScatterMap(powerLength, inverse);
			factorInput = new FfmpegComplexFloat[factor];
			if (firstFactorUsesNaiveKernel) firstNaiveExponents = CreateNaiveExponents(factor, inverse);
			if (secondFactorUsesNaiveKernel)
			{
				naiveExponents = CreateNaiveExponents(powerLength, inverse);
				naiveInput = new FfmpegComplexFloat[powerLength];
			}
		}

		/// <summary>
		/// Transforms interleaved complex samples with an optional complex-element output stride.
		/// </summary>
		public void Transform(ReadOnlySpan<FfmpegComplexFloat> input, Span<FfmpegComplexFloat> output, int outputStride = 1)
		{
			if (input.Length < Length)
			{
				throw new ArgumentException("FFT input is shorter than the configured transform length.", nameof(input));
			}
			if (outputStride <= 0 || output.Length < checked((Length - 1) * outputStride + 1))
			{
				throw new ArgumentException("FFT output cannot hold the configured transform and stride.", nameof(output));
			}

			if (factor == 0)
			{
				for (var index = 0; index < Length; index++)
				{
					work[index] = input[inputMap[index]];
				}

				FfmpegFloatTransformKernel.PowerOfTwoFft(work, 0, Length);
				CopyOutput(work, output, outputStride);
				return;
			}

			if (factor == Length)
			{
				for (var index = 0; index < Length; index++)
				{
					work[index] = input[inputMap[index]];
				}

				FfmpegFloatTransformKernel.FactorFft(work, 0, work, 0, 1, factor);
				CopyOutput(work, output, outputStride);
				return;
			}

			TransformCompound(input, output, outputStride);
		}

		/// <summary>
		/// Runs FFmpeg's two-stage PFA layout: factor codelets write scattered columns before in-place split-radix rows.
		/// </summary>
		private void TransformCompound(ReadOnlySpan<FfmpegComplexFloat> input, Span<FfmpegComplexFloat> output, int outputStride)
		{
			var powerLength = Length / factor;
			for (var row = 0; row < powerLength; row++)
			{
				for (var column = 0; column < factor; column++)
				{
					factorInput[column] = input[inputMap[row * factor + column]];
				}

				if (firstFactorUsesNaiveKernel)
					NaiveFactorFft(work, powerScatterMap[row], factorInput, powerLength, factor, firstNaiveExponents);
				else
					FfmpegFloatTransformKernel.FactorFft(
						work,
						powerScatterMap[row],
						factorInput,
						0,
						powerLength,
						factor);
			}

			for (var column = 0; column < factor; column++)
			{
				if (secondFactorIsPowerOfTwo)
				{
					FfmpegFloatTransformKernel.PowerOfTwoFft(work, column * powerLength, powerLength);
				} else if (secondFactorUsesNaiveKernel)
				{
					NaiveFft(work, column * powerLength, powerLength, naiveExponents, naiveInput);
				} else
				{
					FfmpegFloatTransformKernel.FactorFft(
						work,
						column * powerLength,
						work,
						column * powerLength,
						1,
						powerLength);
				}
			}

			for (var index = 0; index < Length; index++)
			{
				output[index * outputStride] = work[outputMap[index]];
			}
		}

		private void CopyOutput(FfmpegComplexFloat[] source, Span<FfmpegComplexFloat> output, int outputStride)
		{
			for (var index = 0; index < Length; index++)
			{
				output[index * outputStride] = source[index];
			}
		}

		private static int SelectOddFactor(int length)
		{
			var odd = length;
			while ((odd & 1) == 0) odd >>= 1;
			if (IsDirectFactor(odd)) return odd;
			if (length % 13 == 0 && GreatestCommonDivisor(13, length / 13) == 1) return 13;
			if (length % 7 == 0 && GreatestCommonDivisor(7, length / 7) == 1) return 7;
			if (length % 5 == 0 && GreatestCommonDivisor(5, length / 5) == 1) return 5;
			if (length % 3 == 0 && GreatestCommonDivisor(3, length / 3) == 1) return 3;

			throw new ArgumentException("The scalar FFmpeg FFT port supports factors 2, 3, 5, 7, 9, and 15.", nameof(length));
		}

		private static bool IsDirectFactor(int length) => length == 3 || length == 5 || length == 7 || length == 9 || length == 15;

		private static int GreatestCommonDivisor(int first, int second)
		{
			while (second != 0)
			{
				var remainder = first % second;
				first = second;
				second = remainder;
			}
			return first;
		}

		private static FfmpegComplexFloat[] CreateNaiveExponents(int length, bool inverse)
		{
			var result = new FfmpegComplexFloat[length * length];
			var phase = (inverse ? 2.0 : -2.0) * Math.PI / length;
			for (var first = 0; first < length; first++)
				for (var second = 0; second < length; second++)
				{
					var factor = phase * first * second;
					result[first * second].Real = (float)Math.Cos(factor);
					result[first * second].Imaginary = (float)Math.Sin(factor);
				}
			return result;
		}

		private static void NaiveFft(FfmpegComplexFloat[] values, int offset, int length, FfmpegComplexFloat[] exponents, FfmpegComplexFloat[] source)
		{
			Array.Copy(values, offset, source, 0, length);
			for (var first = 0; first < length; first++)
			{
				var real = 0.0f;
				var imaginary = 0.0f;
				for (var second = 0; second < length; second++)
				{
					var exponent = exponents[first * second];
					var value = source[second];
					var productReal = value.Real * exponent.Real - value.Imaginary * exponent.Imaginary;
					var productImaginary = value.Real * exponent.Imaginary + value.Imaginary * exponent.Real;
					real += productReal;
					imaginary += productImaginary;
				}
				values[offset + first].Real = real;
				values[offset + first].Imaginary = imaginary;
			}
		}

		private static void NaiveFactorFft(FfmpegComplexFloat[] output, int outputOffset, FfmpegComplexFloat[] input,
			int outputStride, int length, FfmpegComplexFloat[] exponents)
		{
			for (var first = 0; first < length; first++)
			{
				var real = 0.0f;
				var imaginary = 0.0f;
				for (var second = 0; second < length; second++)
				{
					var exponent = exponents[first * second];
					var value = input[second];
					var productReal = value.Real * exponent.Real - value.Imaginary * exponent.Imaginary;
					var productImaginary = value.Real * exponent.Imaginary + value.Imaginary * exponent.Real;
					real += productReal;
					imaginary += productImaginary;
				}
				output[outputOffset + first * outputStride].Real = real;
				output[outputOffset + first * outputStride].Imaginary = imaginary;
			}
		}

		internal static int[] GenerateFactorMap(int length, bool inverse, bool scatter = false)
		{
			if (length == 15)
			{
				var map = new int[length];
				for (var m = 0; m < 5; m++)
				{
					for (var n = 0; n < 3; n++)
					{
						if (inverse || scatter)
						{
							map[(m * 3 + n * 5) % 15] = m * 3 + n;
						} else
						{
							map[m * 3 + n] = (m * 3 + n * 5) % 15;
						}
					}
				}

				if (inverse)
				{
					for (var index = 1; index <= length >> 1; index++)
					{
						(map[index], map[length - index]) = (map[length - index], map[index]);
					}
				}

				return map;
			}

			var result = new int[length];
			result[0] = 0;
			for (var index = 1; index < length; index++)
			{
				result[index] = inverse ? length - index : index;
			}

			return result;
		}

		private static int[] GenerateFactorScatterMap(int length, bool inverse)
		{
			var gather = GenerateFactorMap(length, inverse, false);
			if (length == 15)
			{
				return GenerateFactorMap(length, inverse, true);
			}
			var scatter = new int[length];
			for (var index = 0; index < length; index++)
			{
				scatter[gather[index]] = index;
			}
			return scatter;
		}

		private static void EmbedFactorMap(int[] compoundInputMap, int factor, int powerLength, bool inverse)
		{
			var factorMap = GenerateFactorMap(factor, inverse);
			var temporary = new int[factor];
			for (var offset = 0; offset < factor * powerLength; offset += factor)
			{
				Array.Copy(compoundInputMap, offset, temporary, 0, factor);
				for (var index = 0; index < factor; index++)
				{
					compoundInputMap[offset + index] = temporary[factorMap[index]];
				}
			}
		}

		internal static (int[] Input, int[] Output) GenerateCompoundMap(int n, int m, bool inverse)
		{
			var length = n * m;
			var input = new int[length];
			var output = new int[length];
			var mInverse = MultiplicativeInverse(m, n);
			var nInverse = MultiplicativeInverse(n, m);
			for (var j = 0; j < m; j++)
			{
				for (var i = 0; i < n; i++)
				{
					input[j * n + i] = (i * m + j * n) % length;
					output[(i * m * mInverse + j * n * nInverse) % length] = i * m + j;
				}
			}

			if (inverse)
			{
				for (var row = 0; row < m; row++)
				{
					for (var index = 0; index < (n - 1) >> 1; index++)
					{
						var left = row * n + 1 + index;
						var right = row * n + n - index - 1;
						(input[left], input[right]) = (input[right], input[left]);
					}
				}
			}

			return (input, output);
		}

		internal static int[] GeneratePowerOfTwoMap(int length, bool inverse, bool scatter)
		{
			var map = new int[length];
			for (var index = 0; index < length; index++)
			{
				var permutation = -SplitRadixPermutation(index, length, inverse) & (length - 1);
				if (scatter)
				{
					map[permutation] = index;
				} else
				{
					map[index] = permutation;
				}
			}

			return map;
		}

		private static int SplitRadixPermutation(int index, int length, bool inverse)
		{
			length >>= 1;
			if (length <= 1)
			{
				return index & 1;
			}
			if ((index & length) == 0)
			{
				return SplitRadixPermutation(index, length, inverse) * 2;
			}

			length >>= 1;
			var differing = ((index & length) == 0) != inverse;
			return SplitRadixPermutation(index, length, inverse) * 4 + 1 - 2 * (differing ? 1 : 0);
		}

		private static int MultiplicativeInverse(int value, int modulus)
		{
			value %= modulus;
			for (var candidate = 1; candidate < modulus; candidate++)
			{
				if (value * candidate % modulus == 1)
				{
					return candidate;
				}
			}

			throw new ArgumentException("Transform factors must be coprime.");
		}

		private static bool IsPowerOfTwo(int value)
		{
			return (value & (value - 1)) == 0;
		}
	}
}
