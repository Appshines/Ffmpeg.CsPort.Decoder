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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Flac
{
	/// <summary>
	/// Ports FLAC fixed and LPC reconstruction plus wasted-bit restoration from flacdec.c and flacdsp.c.
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
