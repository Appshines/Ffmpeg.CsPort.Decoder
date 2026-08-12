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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Flac
{
	/// <summary>
	/// Ports FLAC fixed and LPC reconstruction, including guarded AVX2 integer kernels, plus wasted-bit restoration.
	/// </summary>
	internal static class FlacPrediction
	{
		/// <summary>
		/// Reconstructs FLAC fixed-predictor residuals with the order-specific unsigned accumulator schedule.
		/// </summary>
		public static int DecodeFixed(int[] decoded, int predictorOrder, int blockSize)
		{
			uint a = 0;
			uint b = 0;
			uint c = 0;
			uint d = 0;
			if (predictorOrder > 0)
				a = unchecked((uint)decoded[predictorOrder - 1]);
			if (predictorOrder > 1)
				b = unchecked(a - (uint)decoded[predictorOrder - 2]);
			if (predictorOrder > 2)
				c = unchecked(b - (uint)decoded[predictorOrder - 2] + (uint)decoded[predictorOrder - 3]);
			if (predictorOrder > 3)
				d = unchecked(c - (uint)decoded[predictorOrder - 2] + 2U * (uint)decoded[predictorOrder - 3] - (uint)decoded[predictorOrder - 4]);

			switch (predictorOrder)
			{
				case 0:
					break;
				case 1:
					for (var index = predictorOrder; index < blockSize; index++)
						decoded[index] = unchecked((int)(a += (uint)decoded[index]));
					break;
				case 2:
					for (var index = predictorOrder; index < blockSize; index++)
					{
						b = unchecked(b + (uint)decoded[index]);
						a = unchecked(a + b);
						decoded[index] = unchecked((int)a);
					}
					break;
				case 3:
					for (var index = predictorOrder; index < blockSize; index++)
					{
						c = unchecked(c + (uint)decoded[index]);
						b = unchecked(b + c);
						a = unchecked(a + b);
						decoded[index] = unchecked((int)a);
					}
					break;
				case 4:
					for (var index = predictorOrder; index < blockSize; index++)
					{
						d = unchecked(d + (uint)decoded[index]);
						c = unchecked(c + d);
						b = unchecked(b + c);
						a = unchecked(a + b);
						decoded[index] = unchecked((int)a);
					}
					break;
				default:
					return FfmpegError.InvalidData;
			}
			return 0;
		}

		public static int DecodeFixedWide(int[] decoded, int predictorOrder, int blockSize)
		{
			for (var index = predictorOrder; index < blockSize; index++)
			{
				ulong value;
				switch (predictorOrder)
				{
					case 0:
						value = unchecked((uint)decoded[index]);
						break;
					case 1:
						value = unchecked((uint)decoded[index] + (ulong)(uint)decoded[index - 1]);
						break;
					case 2:
						value = unchecked((uint)decoded[index] + 2UL * (uint)decoded[index - 1] - (uint)decoded[index - 2]);
						break;
					case 3:
						value = unchecked((uint)decoded[index] + 3UL * (uint)decoded[index - 1] - 3UL * (uint)decoded[index - 2] + (uint)decoded[index - 3]);
						break;
					case 4:
						value = unchecked((uint)decoded[index] + 4UL * (uint)decoded[index - 1] - 6UL * (uint)decoded[index - 2] + 4UL * (uint)decoded[index - 3] - (uint)decoded[index - 4]);
						break;
					default:
						return FfmpegError.InvalidData;
				}
				decoded[index] = unchecked((int)value);
			}
			return 0;
		}

		public static int DecodeFixed33(long[] decoded, int[] residual, int predictorOrder, int blockSize)
		{
			for (var index = predictorOrder; index < blockSize; index++)
			{
				ulong value;
				switch (predictorOrder)
				{
					case 0:
						value = unchecked((ulong)(long)residual[index]);
						break;
					case 1:
						value = unchecked((ulong)(long)residual[index] + (ulong)decoded[index - 1]);
						break;
					case 2:
						value = unchecked((ulong)(long)residual[index] + 2UL * (ulong)decoded[index - 1] - (ulong)decoded[index - 2]);
						break;
					case 3:
						value = unchecked((ulong)(long)residual[index] + 3UL * (ulong)decoded[index - 1] - 3UL * (ulong)decoded[index - 2] + (ulong)decoded[index - 3]);
						break;
					case 4:
						value = unchecked((ulong)(long)residual[index] + 4UL * (ulong)decoded[index - 1] - 6UL * (ulong)decoded[index - 2] + 4UL * (ulong)decoded[index - 3] - (ulong)decoded[index - 4]);
						break;
					default:
						return FfmpegError.InvalidData;
				}
				decoded[index] = unchecked((long)value);
			}
			return 0;
		}

		/// <summary>
		/// Reproduces the two-sample scalar C loop used when LPC intermediates fit the source 32-bit path.
		/// </summary>
		public static void DecodeLpc16(int[] decoded, int[] coefficients, int predictorOrder, int quantizationLevel, int length)
		{
			if (Avx2.IsSupported && predictorOrder >= 8 && predictorOrder <= 32)
			{
				if (predictorOrder <= 8)
					DecodeLpc16Avx2Order8(decoded, coefficients, quantizationLevel, length);
				else if (predictorOrder <= 16)
					DecodeLpc16Avx2Order16(decoded, coefficients, predictorOrder, quantizationLevel, length);
				else
					DecodeLpc16Avx2Order32(decoded, coefficients, predictorOrder, quantizationLevel, length);
				return;
			}

			var index = predictorOrder;
			var decodedOffset = 0;
			for (; index < length - 1; index += 2, decodedOffset += 2)
			{
				var coefficient = unchecked((uint)coefficients[0]);
				var sample = unchecked((uint)decoded[decodedOffset]);
				var firstSum = 0;
				var secondSum = 0;
				var coefficientIndex = 1;
				for (; coefficientIndex < predictorOrder; coefficientIndex++)
				{
					firstSum = unchecked(firstSum + (int)(coefficient * sample));
					sample = unchecked((uint)decoded[decodedOffset + coefficientIndex]);
					secondSum = unchecked(secondSum + (int)(coefficient * sample));
					coefficient = unchecked((uint)coefficients[coefficientIndex]);
				}
				firstSum = unchecked(firstSum + (int)(coefficient * sample));
				var predicted = unchecked((uint)(firstSum >> quantizationLevel));
				decoded[decodedOffset + coefficientIndex] = unchecked((int)((uint)decoded[decodedOffset + coefficientIndex] + predicted));
				sample = unchecked((uint)decoded[decodedOffset + coefficientIndex]);
				secondSum = unchecked(secondSum + (int)(coefficient * sample));
				predicted = unchecked((uint)(secondSum >> quantizationLevel));
				decoded[decodedOffset + coefficientIndex + 1] = unchecked((int)((uint)decoded[decodedOffset + coefficientIndex + 1] + predicted));
			}
			if (index < length)
			{
				var sum = 0;
				var coefficientIndex = 0;
				for (; coefficientIndex < predictorOrder; coefficientIndex++)
					sum = unchecked(sum + coefficients[coefficientIndex] * unchecked((int)(uint)decoded[decodedOffset + coefficientIndex]));
				decoded[decodedOffset + coefficientIndex] = unchecked(
					(int)((uint)decoded[decodedOffset + coefficientIndex] + (uint)(sum >> quantizationLevel)));
			}
		}

		public static void DecodeLpc32(int[] decoded, int[] coefficients, int predictorOrder, int quantizationLevel, int length)
		{
			if (Avx2.IsSupported && predictorOrder >= 8 && predictorOrder <= 32)
			{
				if (predictorOrder <= 8)
					DecodeLpc32Avx2Order8(decoded, coefficients, quantizationLevel, length);
				else if (predictorOrder <= 16)
					DecodeLpc32Avx2Order16(decoded, coefficients, predictorOrder, quantizationLevel, length);
				else
					DecodeLpc32Avx2Order32(decoded, coefficients, predictorOrder, quantizationLevel, length);
				return;
			}

			var decodedOffset = 0;
			for (var index = predictorOrder; index < length; index++, decodedOffset++)
			{
				long sum = 0;
				var coefficientIndex = 0;
				for (; coefficientIndex < predictorOrder; coefficientIndex++)
					sum = unchecked(sum + (long)coefficients[coefficientIndex] * decoded[decodedOffset + coefficientIndex]);
				decoded[decodedOffset + coefficientIndex] = unchecked((int)(decoded[decodedOffset + coefficientIndex] + (sum >> quantizationLevel)));
			}
		}

		private static void DecodeLpc16Avx2Order8(int[] decoded, int[] coefficients, int quantizationLevel, int length)
		{
			const int predictorOrder = 8;
			var coefficient0 = LoadVector(coefficients, 0);
			for (var decodedOffset = 0; decodedOffset + predictorOrder < length; decodedOffset++)
			{
				var accumulator = Avx2.MultiplyLow(coefficient0, LoadVector(decoded, decodedOffset));
				var sum = Vector256.Sum(accumulator);
				var outputIndex = decodedOffset + predictorOrder;
				decoded[outputIndex] = unchecked((int)((uint)decoded[outputIndex] + (uint)(sum >> quantizationLevel)));
			}
		}

		private static void DecodeLpc16Avx2Order16(int[] decoded, int[] coefficients, int predictorOrder, int quantizationLevel, int length)
		{
			var coefficient0 = LoadVector(coefficients, 0);
			if (predictorOrder == 16)
			{
				var coefficient1 = LoadVector(coefficients, 8);
				for (var decodedOffset = 0; decodedOffset + predictorOrder < length; decodedOffset++)
				{
					var accumulator = Avx2.MultiplyLow(coefficient0, LoadVector(decoded, decodedOffset));
					accumulator = Avx2.Add(accumulator, Avx2.MultiplyLow(coefficient1, LoadVector(decoded, decodedOffset + 8)));
					var sum = Vector256.Sum(accumulator);
					var outputIndex = decodedOffset + predictorOrder;
					decoded[outputIndex] = unchecked((int)((uint)decoded[outputIndex] + (uint)(sum >> quantizationLevel)));
				}
				return;
			}

			for (var decodedOffset = 0; decodedOffset + predictorOrder < length; decodedOffset++)
			{
				var accumulator = Avx2.MultiplyLow(coefficient0, LoadVector(decoded, decodedOffset));
				var sum = Vector256.Sum(accumulator);
				for (var coefficientIndex = 8; coefficientIndex < predictorOrder; coefficientIndex++)
					sum = unchecked(sum + coefficients[coefficientIndex] * decoded[decodedOffset + coefficientIndex]);
				var outputIndex = decodedOffset + predictorOrder;
				decoded[outputIndex] = unchecked((int)((uint)decoded[outputIndex] + (uint)(sum >> quantizationLevel)));
			}
		}

		private static void DecodeLpc16Avx2Order32(int[] decoded, int[] coefficients, int predictorOrder, int quantizationLevel, int length)
		{
			var coefficient0 = LoadVector(coefficients, 0);
			var coefficient1 = LoadVector(coefficients, 8);
			var coefficient2 = predictorOrder >= 24 ? LoadVector(coefficients, 16) : Vector256<int>.Zero;
			var coefficient3 = predictorOrder == 32 ? LoadVector(coefficients, 24) : Vector256<int>.Zero;
			for (var decodedOffset = 0; decodedOffset + predictorOrder < length; decodedOffset++)
			{
				var accumulator = Avx2.MultiplyLow(coefficient0, LoadVector(decoded, decodedOffset));
				accumulator = Avx2.Add(accumulator, Avx2.MultiplyLow(coefficient1, LoadVector(decoded, decodedOffset + 8)));
				var coefficientIndex = 16;
				if (predictorOrder >= 24)
				{
					accumulator = Avx2.Add(accumulator, Avx2.MultiplyLow(coefficient2, LoadVector(decoded, decodedOffset + 16)));
					coefficientIndex = 24;
				}
				if (predictorOrder == 32)
				{
					accumulator = Avx2.Add(accumulator, Avx2.MultiplyLow(coefficient3, LoadVector(decoded, decodedOffset + 24)));
					coefficientIndex = 32;
				}
				var sum = Vector256.Sum(accumulator);
				for (; coefficientIndex < predictorOrder; coefficientIndex++)
					sum = unchecked(sum + coefficients[coefficientIndex] * decoded[decodedOffset + coefficientIndex]);
				var outputIndex = decodedOffset + predictorOrder;
				decoded[outputIndex] = unchecked((int)((uint)decoded[outputIndex] + (uint)(sum >> quantizationLevel)));
			}
		}

		private static void DecodeLpc32Avx2Order8(int[] decoded, int[] coefficients, int quantizationLevel, int length)
		{
			const int predictorOrder = 8;
			var coefficient0 = LoadVector(coefficients, 0);
			for (var decodedOffset = 0; decodedOffset + predictorOrder < length; decodedOffset++)
			{
				AccumulateLpc32(Vector256<long>.Zero, Vector256<long>.Zero, coefficient0, LoadVector(decoded, decodedOffset), out var even, out var odd);
				var sum = unchecked(Vector256.Sum(even) + Vector256.Sum(odd));
				var outputIndex = decodedOffset + predictorOrder;
				decoded[outputIndex] = unchecked((int)(decoded[outputIndex] + (sum >> quantizationLevel)));
			}
		}

		private static void DecodeLpc32Avx2Order16(int[] decoded, int[] coefficients, int predictorOrder, int quantizationLevel, int length)
		{
			var coefficient0 = LoadVector(coefficients, 0);
			var coefficient1 = predictorOrder == 16 ? LoadVector(coefficients, 8) : Vector256<int>.Zero;
			for (var decodedOffset = 0; decodedOffset + predictorOrder < length; decodedOffset++)
			{
				AccumulateLpc32(Vector256<long>.Zero, Vector256<long>.Zero, coefficient0, LoadVector(decoded, decodedOffset), out var even, out var odd);
				var coefficientIndex = 8;
				if (predictorOrder == 16)
				{
					AccumulateLpc32(even, odd, coefficient1, LoadVector(decoded, decodedOffset + 8), out even, out odd);
					coefficientIndex = 16;
				}
				var sum = unchecked(Vector256.Sum(even) + Vector256.Sum(odd));
				for (; coefficientIndex < predictorOrder; coefficientIndex++)
					sum = unchecked(sum + (long)coefficients[coefficientIndex] * decoded[decodedOffset + coefficientIndex]);
				var outputIndex = decodedOffset + predictorOrder;
				decoded[outputIndex] = unchecked((int)(decoded[outputIndex] + (sum >> quantizationLevel)));
			}
		}

		private static void DecodeLpc32Avx2Order32(int[] decoded, int[] coefficients, int predictorOrder, int quantizationLevel, int length)
		{
			var coefficient0 = LoadVector(coefficients, 0);
			var coefficient1 = LoadVector(coefficients, 8);
			var coefficient2 = predictorOrder >= 24 ? LoadVector(coefficients, 16) : Vector256<int>.Zero;
			var coefficient3 = predictorOrder == 32 ? LoadVector(coefficients, 24) : Vector256<int>.Zero;
			for (var decodedOffset = 0; decodedOffset + predictorOrder < length; decodedOffset++)
			{
				AccumulateLpc32(Vector256<long>.Zero, Vector256<long>.Zero, coefficient0, LoadVector(decoded, decodedOffset), out var even, out var odd);
				AccumulateLpc32(even, odd, coefficient1, LoadVector(decoded, decodedOffset + 8), out even, out odd);
				var coefficientIndex = 16;
				if (predictorOrder >= 24)
				{
					AccumulateLpc32(even, odd, coefficient2, LoadVector(decoded, decodedOffset + 16), out even, out odd);
					coefficientIndex = 24;
				}
				if (predictorOrder == 32)
				{
					AccumulateLpc32(even, odd, coefficient3, LoadVector(decoded, decodedOffset + 24), out even, out odd);
					coefficientIndex = 32;
				}
				var sum = unchecked(Vector256.Sum(even) + Vector256.Sum(odd));
				for (; coefficientIndex < predictorOrder; coefficientIndex++)
					sum = unchecked(sum + (long)coefficients[coefficientIndex] * decoded[decodedOffset + coefficientIndex]);
				var outputIndex = decodedOffset + predictorOrder;
				decoded[outputIndex] = unchecked((int)(decoded[outputIndex] + (sum >> quantizationLevel)));
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void AccumulateLpc32(
			Vector256<long> even,
			Vector256<long> odd,
			Vector256<int> coefficients,
			Vector256<int> samples,
			out Vector256<long> accumulatedEven,
			out Vector256<long> accumulatedOdd)
		{
			accumulatedEven = Avx2.Add(even, Avx2.Multiply(coefficients, samples));
			var oddCoefficients = Avx2.ShiftRightLogical(coefficients.AsUInt64(), 32).AsInt32();
			var oddSamples = Avx2.ShiftRightLogical(samples.AsUInt64(), 32).AsInt32();
			accumulatedOdd = Avx2.Add(odd, Avx2.Multiply(oddCoefficients, oddSamples));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Vector256<int> LoadVector(int[] values, int offset)
		{
			// Kernel dispatch and coefficient bounds prove that all eight lanes are inside the source array.
			return Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(values), unchecked((nuint)offset));
		}

		public static void DecodeLpc33(long[] decoded, int[] residual, int[] coefficients, int predictorOrder, int quantizationLevel, int length)
		{
			var decodedOffset = 0;
			for (var index = predictorOrder; index < length; index++, decodedOffset++)
			{
				long sum = 0;
				var coefficientIndex = 0;
				for (; coefficientIndex < predictorOrder; coefficientIndex++)
					sum = unchecked(sum + coefficients[coefficientIndex] * (long)(ulong)decoded[decodedOffset + coefficientIndex]);
				decoded[decodedOffset + coefficientIndex] = unchecked((long)((ulong)(long)residual[index] + (ulong)(sum >> quantizationLevel)));
			}
		}

		public static void AnalyzeRemodulate(int[] decoded, int[] coefficients, int order, int quantizationLevel, int length, int bitsPerSample)
		{
			var effectiveBits = 1 << (bitsPerSample - 1);
			uint sigma = 0;
			for (var index = order; index < length; index++)
				sigma |= unchecked((uint)(decoded[index] + effectiveBits));
			if (sigma < 2U * effectiveBits)
				return;

			for (var index = length - 1; index >= order; index--)
			{
				long prediction = 0;
				for (var coefficient = 0; coefficient < order; coefficient++)
					prediction = unchecked(prediction + coefficients[coefficient] * (long)decoded[index - order + coefficient]);
				decoded[index] = unchecked((int)((uint)decoded[index] - (ulong)(prediction >> quantizationLevel)));
			}
			for (var index = order; index < length; index++)
			{
				var decodedOffset = index - order;
				var prediction = 0;
				var coefficient = 0;
				for (; coefficient < order; coefficient++)
					prediction = unchecked(prediction + coefficients[coefficient] * unchecked((int)(uint)decoded[decodedOffset + coefficient]));
				decoded[decodedOffset + coefficient] = unchecked((int)((uint)decoded[decodedOffset + coefficient] + (uint)(prediction >> quantizationLevel)));
			}
		}

		public static void RestoreWasted32(int[] decoded, int wastedBits, int length)
		{
			for (var index = 0; index < length; index++)
				decoded[index] = unchecked((int)((uint)decoded[index] << wastedBits));
		}

		public static void RestoreWasted33(long[] decoded, int[] residual, int wastedBits, int length)
		{
			for (var index = 0; index < length; index++)
				decoded[index] = unchecked((long)((ulong)(long)residual[index] << wastedBits));
		}
	}
}
