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
	/// Contains direct scalar translations of FFmpeg's float split-radix and fixed-factor FFT kernels.
	/// </summary>
	internal static class FfmpegFloatTransformKernel
	{
		private static readonly float[][] CosineTables = CreateCosineTables();
		private static readonly float[] Table53 = CreateTable53();
		private static readonly float[] Table7 = CreateTable7();
		private static readonly float[] Table9 = CreateTable9();

		internal static void InitializeTables()
		{
			_ = CosineTables[0];
		}

		/// <summary>
		/// Executes a preshuffled in-place split-radix FFT, preserving the native codelet recursion and operation order.
		/// </summary>
		internal static void PowerOfTwoFft(FfmpegComplexFloat[] values, int offset, int length)
		{
			switch (length)
			{
				case 1:
					return;
				case 2:
					Fft2(values, offset);
					return;
				case 4:
					Fft4(values, offset);
					return;
				case 8:
					Fft8(values, offset);
					return;
				case 16:
					Fft16(values, offset);
					return;
			}

			var quarter = length >> 2;
			PowerOfTwoFft(values, offset, length >> 1);
			PowerOfTwoFft(values, offset + quarter * 2, quarter);
			PowerOfTwoFft(values, offset + quarter * 3, quarter);
			SplitRadixCombine(values, offset, length);
		}

		internal static void FactorFft(
			Span<FfmpegComplexFloat> output,
			int outputOffset,
			ReadOnlySpan<FfmpegComplexFloat> input,
			int inputOffset,
			int outputStride,
			int factor)
		{
			switch (factor)
			{
				case 3:
					Fft3(output, outputOffset, input, inputOffset, outputStride);
					break;
				case 5:
					Fft5(output, outputOffset, input, inputOffset, outputStride, 0, 1, 2, 3, 4);
					break;
				case 7:
					Fft7(output, outputOffset, input, inputOffset, outputStride);
					break;
				case 9:
					Fft9(output, outputOffset, input, inputOffset, outputStride);
					break;
				case 15:
					Fft15(output, outputOffset, input, inputOffset, outputStride);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(factor));
			}
		}

		private static void Fft2(FfmpegComplexFloat[] values, int offset)
		{
			var first = values[offset];
			var second = values[offset + 1];
			values[offset + 1].Real = first.Real - second.Real;
			values[offset].Real = first.Real + second.Real;
			values[offset + 1].Imaginary = first.Imaginary - second.Imaginary;
			values[offset].Imaginary = first.Imaginary + second.Imaginary;
		}

		private static void Fft4(FfmpegComplexFloat[] values, int offset)
		{
			var s0 = values[offset];
			var s1 = values[offset + 1];
			var s2 = values[offset + 2];
			var s3 = values[offset + 3];
			var t3 = s0.Real - s1.Real;
			var t1 = s0.Real + s1.Real;
			var t8 = s3.Real - s2.Real;
			var t6 = s3.Real + s2.Real;
			values[offset + 2].Real = t1 - t6;
			values[offset].Real = t1 + t6;
			var t4 = s0.Imaginary - s1.Imaginary;
			var t2 = s0.Imaginary + s1.Imaginary;
			var t7 = s2.Imaginary - s3.Imaginary;
			var t5 = s2.Imaginary + s3.Imaginary;
			values[offset + 3].Imaginary = t4 - t8;
			values[offset + 1].Imaginary = t4 + t8;
			values[offset + 3].Real = t3 - t7;
			values[offset + 1].Real = t3 + t7;
			values[offset + 2].Imaginary = t2 - t5;
			values[offset].Imaginary = t2 + t5;
		}

		private static void Fft8(FfmpegComplexFloat[] values, int offset)
		{
			Fft4(values, offset);
			var s4 = values[offset + 4];
			var s5 = values[offset + 5];
			var s6 = values[offset + 6];
			var s7 = values[offset + 7];
			var t1 = s4.Real + s5.Real;
			values[offset + 5].Real = s4.Real - s5.Real;
			var t2 = s4.Imaginary + s5.Imaginary;
			values[offset + 5].Imaginary = s4.Imaginary - s5.Imaginary;
			var t5 = s6.Real + s7.Real;
			values[offset + 7].Real = s6.Real - s7.Real;
			var t6 = s6.Imaginary + s7.Imaginary;
			values[offset + 7].Imaginary = s6.Imaginary - s7.Imaginary;
			Butterflies(values, offset, offset + 2, offset + 4, offset + 6, t1, t2, t5, t6);
			var cosine = GetCosineTable(8)[1];
			Transform(values, offset + 1, offset + 3, offset + 5, offset + 7, cosine, cosine);
		}

		private static void Fft16(FfmpegComplexFloat[] values, int offset)
		{
			Fft8(values, offset);
			Fft4(values, offset + 8);
			Fft4(values, offset + 12);
			var t1 = values[offset + 8].Real;
			var t2 = values[offset + 8].Imaginary;
			var t5 = values[offset + 12].Real;
			var t6 = values[offset + 12].Imaginary;
			Butterflies(values, offset, offset + 4, offset + 8, offset + 12, t1, t2, t5, t6);
			var cosine = GetCosineTable(16);
			Transform(values, offset + 2, offset + 6, offset + 10, offset + 14, cosine[2], cosine[2]);
			Transform(values, offset + 1, offset + 5, offset + 9, offset + 13, cosine[1], cosine[3]);
			Transform(values, offset + 3, offset + 7, offset + 11, offset + 15, cosine[3], cosine[1]);
		}

		/// <summary>
		/// Combines the three split-radix branches using the same CMUL/BF statement boundaries as the C macros.
		/// </summary>
		private static void SplitRadixCombine(FfmpegComplexFloat[] values, int offset, int length)
		{
			var cosine = GetCosineTable(length);
			var quarter = length >> 2;
			for (var index = 0; index < quarter; index += 8)
			{
				TransformAtSplitIndex(values, offset, quarter, cosine, index);
				TransformAtSplitIndex(values, offset, quarter, cosine, index + 2);
				TransformAtSplitIndex(values, offset, quarter, cosine, index + 4);
				TransformAtSplitIndex(values, offset, quarter, cosine, index + 6);
				TransformAtSplitIndex(values, offset, quarter, cosine, index + 1);
				TransformAtSplitIndex(values, offset, quarter, cosine, index + 3);
				TransformAtSplitIndex(values, offset, quarter, cosine, index + 5);
				TransformAtSplitIndex(values, offset, quarter, cosine, index + 7);
			}
		}

		private static void TransformAtSplitIndex(
			FfmpegComplexFloat[] values,
			int offset,
			int quarter,
			float[] cosine,
			int index)
		{
			Transform(
				values,
				offset + index,
				offset + quarter + index,
				offset + quarter * 2 + index,
				offset + quarter * 3 + index,
				cosine[index],
				cosine[quarter - index]);
		}

		private static void Transform(
			FfmpegComplexFloat[] values,
			int a0Index,
			int a1Index,
			int a2Index,
			int a3Index,
			float wReal,
			float wImaginary)
		{
			var a0 = values[a0Index];
			var a1 = values[a1Index];
			var a2 = values[a2Index];
			var a3 = values[a3Index];
			var t1 = a2.Real * wReal - a2.Imaginary * -wImaginary;
			var t2 = a2.Real * -wImaginary + a2.Imaginary * wReal;
			var t5 = a3.Real * wReal - a3.Imaginary * wImaginary;
			var t6 = a3.Real * wImaginary + a3.Imaginary * wReal;
			var r0 = a0.Real;
			var i0 = a0.Imaginary;
			var r1 = a1.Real;
			var i1 = a1.Imaginary;
			var t3 = t5 - t1;
			t5 += t1;
			a2.Real = r0 - t5;
			a0.Real = r0 + t5;
			a3.Imaginary = i1 - t3;
			a1.Imaginary = i1 + t3;
			var t4 = t2 - t6;
			t6 = t2 + t6;
			a3.Real = r1 - t4;
			a1.Real = r1 + t4;
			a2.Imaginary = i0 - t6;
			a0.Imaginary = i0 + t6;
			values[a0Index] = a0;
			values[a1Index] = a1;
			values[a2Index] = a2;
			values[a3Index] = a3;
		}

		private static void Butterflies(
			FfmpegComplexFloat[] values,
			int a0Index,
			int a1Index,
			int a2Index,
			int a3Index,
			float t1,
			float t2,
			float t5,
			float t6)
		{
			var a0 = values[a0Index];
			var a1 = values[a1Index];
			var a2 = values[a2Index];
			var a3 = values[a3Index];
			var r0 = a0.Real;
			var i0 = a0.Imaginary;
			var r1 = a1.Real;
			var i1 = a1.Imaginary;
			var t3 = t5 - t1;
			t5 += t1;
			a2.Real = r0 - t5;
			a0.Real = r0 + t5;
			a3.Imaginary = i1 - t3;
			a1.Imaginary = i1 + t3;
			var t4 = t2 - t6;
			t6 = t2 + t6;
			a3.Real = r1 - t4;
			a1.Real = r1 + t4;
			a2.Imaginary = i0 - t6;
			a0.Imaginary = i0 + t6;
			values[a0Index] = a0;
			values[a1Index] = a1;
			values[a2Index] = a2;
			values[a3Index] = a3;
		}

		private static float[] CreateCosineTable(int length)
		{
			var result = new float[length / 4 + 1];
			var frequency = 2.0 * Math.PI / length;
			for (var index = 0; index < length / 4; index++)
			{
				result[index] = (float)Math.Cos(index * frequency);
			}
			result[length / 4] = 0.0f;
			return result;
		}

		private static float[] GetCosineTable(int length)
		{
			var index = 0;
			for (var value = length; value > 8; value >>= 1)
			{
				index++;
			}
			return CosineTables[index];
		}

		private static float[][] CreateCosineTables()
		{
			var tables = new float[15][];
			var length = 8;
			for (var index = 0; index < tables.Length; index++)
			{
				tables[index] = CreateCosineTable(length);
				length <<= 1;
			}
			return tables;
		}

		private static float[] CreateTable53()
		{
			return new[]
			{
				(float)Math.Cos(2 * Math.PI / 5),
				(float)Math.Cos(2 * Math.PI / 5),
				(float)Math.Cos(2 * Math.PI / 10),
				(float)Math.Cos(2 * Math.PI / 10),
				(float)Math.Sin(2 * Math.PI / 5),
				(float)Math.Sin(2 * Math.PI / 5),
				(float)Math.Sin(2 * Math.PI / 10),
				(float)Math.Sin(2 * Math.PI / 10),
				(float)Math.Cos(2 * Math.PI / 12),
				(float)Math.Cos(2 * Math.PI / 12),
				(float)Math.Cos(2 * Math.PI / 6),
				(float)Math.Cos(8 * Math.PI / 6)
			};
		}

		private static float[] CreateTable7()
		{
			return new[]
			{
				(float)Math.Cos(2 * Math.PI / 7),
				(float)Math.Sin(2 * Math.PI / 7),
				(float)Math.Sin(2 * Math.PI / 28),
				(float)Math.Cos(2 * Math.PI / 28),
				(float)Math.Cos(2 * Math.PI / 14),
				(float)Math.Sin(2 * Math.PI / 14)
			};
		}

		private static float[] CreateTable9()
		{
			var result = new float[8];
			result[0] = (float)Math.Cos(2 * Math.PI / 3);
			result[1] = (float)Math.Sin(2 * Math.PI / 3);
			result[2] = (float)Math.Cos(2 * Math.PI / 9);
			result[3] = (float)Math.Sin(2 * Math.PI / 9);
			result[4] = (float)Math.Cos(2 * Math.PI / 36);
			result[5] = (float)Math.Sin(2 * Math.PI / 36);
			result[6] = result[2] + result[5];
			result[7] = result[3] - result[4];
			return result;
		}

		private static void Fft3(Span<FfmpegComplexFloat> output, int outputOffset, ReadOnlySpan<FfmpegComplexFloat> input, int inputOffset, int stride)
		{
			var t0 = input[inputOffset];
			var in1 = input[inputOffset + 1];
			var in2 = input[inputOffset + 2];
			var t1Real = in1.Imaginary - in2.Imaginary;
			var t2Imaginary = in1.Imaginary + in2.Imaginary;
			var t1Imaginary = in1.Real - in2.Real;
			var t2Real = in1.Real + in2.Real;
			output[outputOffset] = new FfmpegComplexFloat(t0.Real + t2Real, t0.Imaginary + t2Imaginary);
			t1Real = Table53[8] * t1Real;
			t1Imaginary = Table53[9] * t1Imaginary;
			t2Real = Table53[10] * t2Real;
			t2Imaginary = Table53[10] * t2Imaginary;
			output[outputOffset + stride] = new FfmpegComplexFloat(
				t0.Real - t2Real + t1Real,
				t0.Imaginary - t2Imaginary - t1Imaginary);
			output[outputOffset + 2 * stride] = new FfmpegComplexFloat(
				t0.Real - t2Real - t1Real,
				t0.Imaginary - t2Imaginary + t1Imaginary);
		}

		/// <summary>
		/// Executes the shared five-point kernel; destination indices encode FFmpeg's three 15-point column layouts.
		/// </summary>
		private static void Fft5(
			Span<FfmpegComplexFloat> output,
			int outputOffset,
			ReadOnlySpan<FfmpegComplexFloat> input,
			int inputOffset,
			int stride,
			int d0,
			int d1,
			int d2,
			int d3,
			int d4)
		{
			var dc = input[inputOffset];
			var in1 = input[inputOffset + 1];
			var in2 = input[inputOffset + 2];
			var in3 = input[inputOffset + 3];
			var in4 = input[inputOffset + 4];
			var t1Imaginary = in1.Real - in4.Real;
			var t0Real = in1.Real + in4.Real;
			var t1Real = in1.Imaginary - in4.Imaginary;
			var t0Imaginary = in1.Imaginary + in4.Imaginary;
			var t3Imaginary = in2.Real - in3.Real;
			var t2Real = in2.Real + in3.Real;
			var t3Real = in2.Imaginary - in3.Imaginary;
			var t2Imaginary = in2.Imaginary + in3.Imaginary;
			output[outputOffset + d0 * stride] = new FfmpegComplexFloat(
				dc.Real + t0Real + t2Real,
				dc.Imaginary + t0Imaginary + t2Imaginary);

			var t4Real = Table53[0] * t2Real - Table53[2] * t0Real;
			t0Real = t0Real * Table53[0] - t2Real * Table53[2];
			var t4Imaginary = Table53[0] * t2Imaginary - Table53[2] * t0Imaginary;
			t0Imaginary = t0Imaginary * Table53[0] - t2Imaginary * Table53[2];
			var t5Real = t3Real * Table53[4] - t1Real * Table53[6];
			t1Real = t3Real * Table53[6] + t1Real * Table53[4];
			var t5Imaginary = t3Imaginary * Table53[4] - t1Imaginary * Table53[6];
			t1Imaginary = t3Imaginary * Table53[6] + t1Imaginary * Table53[4];

			var z0Real = t0Real - t1Real;
			var z3Real = t0Real + t1Real;
			var z0Imaginary = t0Imaginary - t1Imaginary;
			var z3Imaginary = t0Imaginary + t1Imaginary;
			var z2Real = t4Real - t5Real;
			var z1Real = t4Real + t5Real;
			var z2Imaginary = t4Imaginary - t5Imaginary;
			var z1Imaginary = t4Imaginary + t5Imaginary;
			output[outputOffset + d1 * stride] = new FfmpegComplexFloat(dc.Real + z3Real, dc.Imaginary + z0Imaginary);
			output[outputOffset + d2 * stride] = new FfmpegComplexFloat(dc.Real + z2Real, dc.Imaginary + z1Imaginary);
			output[outputOffset + d3 * stride] = new FfmpegComplexFloat(dc.Real + z1Real, dc.Imaginary + z2Imaginary);
			output[outputOffset + d4 * stride] = new FfmpegComplexFloat(dc.Real + z0Real, dc.Imaginary + z3Imaginary);
		}

		/// <summary>
		/// Executes the seven-point Winograd-style scalar kernel with FFmpeg's explicit rounding boundaries.
		/// </summary>
		private static void Fft7(Span<FfmpegComplexFloat> output, int outputOffset, ReadOnlySpan<FfmpegComplexFloat> input, int inputOffset, int stride)
		{
			var dc = input[inputOffset];
			Span<FfmpegComplexFloat> t = stackalloc FfmpegComplexFloat[6];
			Span<FfmpegComplexFloat> z = stackalloc FfmpegComplexFloat[3];
			Butterfly(out t[1].Real, out t[0].Real, input[inputOffset + 1].Real, input[inputOffset + 6].Real);
			Butterfly(out t[1].Imaginary, out t[0].Imaginary, input[inputOffset + 1].Imaginary, input[inputOffset + 6].Imaginary);
			Butterfly(out t[3].Real, out t[2].Real, input[inputOffset + 2].Real, input[inputOffset + 5].Real);
			Butterfly(out t[3].Imaginary, out t[2].Imaginary, input[inputOffset + 2].Imaginary, input[inputOffset + 5].Imaginary);
			Butterfly(out t[5].Real, out t[4].Real, input[inputOffset + 3].Real, input[inputOffset + 4].Real);
			Butterfly(out t[5].Imaginary, out t[4].Imaginary, input[inputOffset + 3].Imaginary, input[inputOffset + 4].Imaginary);
			output[outputOffset] = new FfmpegComplexFloat(
				dc.Real + t[0].Real + t[2].Real + t[4].Real,
				dc.Imaginary + t[0].Imaginary + t[2].Imaginary + t[4].Imaginary);

			z[0].Real = Table7[0] * t[0].Real - Table7[4] * t[4].Real - Table7[2] * t[2].Real;
			z[1].Real = Table7[0] * t[4].Real - Table7[2] * t[0].Real - Table7[4] * t[2].Real;
			z[2].Real = Table7[0] * t[2].Real - Table7[4] * t[0].Real - Table7[2] * t[4].Real;
			z[0].Imaginary = Table7[0] * t[0].Imaginary - Table7[2] * t[2].Imaginary - Table7[4] * t[4].Imaginary;
			z[1].Imaginary = Table7[0] * t[4].Imaginary - Table7[2] * t[0].Imaginary - Table7[4] * t[2].Imaginary;
			z[2].Imaginary = Table7[0] * t[2].Imaginary - Table7[4] * t[0].Imaginary - Table7[2] * t[4].Imaginary;
			t[0].Real = Table7[5] * t[1].Imaginary + Table7[3] * t[5].Imaginary - Table7[1] * t[3].Imaginary;
			t[2].Real = Table7[1] * t[5].Imaginary + Table7[5] * t[3].Imaginary - Table7[3] * t[1].Imaginary;
			t[4].Real = Table7[5] * t[5].Imaginary + Table7[3] * t[3].Imaginary + Table7[1] * t[1].Imaginary;
			t[0].Imaginary = Table7[1] * t[1].Real + Table7[3] * t[3].Real + Table7[5] * t[5].Real;
			t[2].Imaginary = Table7[5] * t[3].Real + Table7[1] * t[5].Real - Table7[3] * t[1].Real;
			t[4].Imaginary = Table7[5] * t[1].Real + Table7[3] * t[5].Real - Table7[1] * t[3].Real;

			Butterfly(out t[1].Real, out z[0].Real, z[0].Real, t[4].Real);
			Butterfly(out t[3].Real, out z[1].Real, z[1].Real, t[2].Real);
			Butterfly(out t[5].Real, out z[2].Real, z[2].Real, t[0].Real);
			Butterfly(out t[1].Imaginary, out z[0].Imaginary, z[0].Imaginary, t[0].Imaginary);
			Butterfly(out t[3].Imaginary, out z[1].Imaginary, z[1].Imaginary, t[2].Imaginary);
			Butterfly(out t[5].Imaginary, out z[2].Imaginary, z[2].Imaginary, t[4].Imaginary);
			output[outputOffset + stride] = new FfmpegComplexFloat(dc.Real + z[0].Real, dc.Imaginary + t[1].Imaginary);
			output[outputOffset + 2 * stride] = new FfmpegComplexFloat(dc.Real + t[3].Real, dc.Imaginary + z[1].Imaginary);
			output[outputOffset + 3 * stride] = new FfmpegComplexFloat(dc.Real + z[2].Real, dc.Imaginary + t[5].Imaginary);
			output[outputOffset + 4 * stride] = new FfmpegComplexFloat(dc.Real + t[5].Real, dc.Imaginary + z[2].Imaginary);
			output[outputOffset + 5 * stride] = new FfmpegComplexFloat(dc.Real + z[1].Real, dc.Imaginary + t[3].Imaginary);
			output[outputOffset + 6 * stride] = new FfmpegComplexFloat(dc.Real + t[1].Real, dc.Imaginary + z[0].Imaginary);
		}

		/// <summary>
		/// Executes FFmpeg's scalar nine-point FFT decomposition with its fixed three-point butterfly schedule.
		/// </summary>
		private static void Fft9(Span<FfmpegComplexFloat> output, int outputOffset, ReadOnlySpan<FfmpegComplexFloat> input, int inputOffset, int stride)
		{
			var dc = input[inputOffset];
			Span<FfmpegComplexFloat> t = stackalloc FfmpegComplexFloat[8];
			Span<FfmpegComplexFloat> w = stackalloc FfmpegComplexFloat[4];
			Span<FfmpegComplexFloat> x = stackalloc FfmpegComplexFloat[5];
			Span<FfmpegComplexFloat> y = stackalloc FfmpegComplexFloat[5];
			Span<FfmpegComplexFloat> z = stackalloc FfmpegComplexFloat[2];
			Butterfly(out t[1].Real, out t[0].Real, input[inputOffset + 1].Real, input[inputOffset + 8].Real);
			Butterfly(out t[1].Imaginary, out t[0].Imaginary, input[inputOffset + 1].Imaginary, input[inputOffset + 8].Imaginary);
			Butterfly(out t[3].Real, out t[2].Real, input[inputOffset + 2].Real, input[inputOffset + 7].Real);
			Butterfly(out t[3].Imaginary, out t[2].Imaginary, input[inputOffset + 2].Imaginary, input[inputOffset + 7].Imaginary);
			Butterfly(out t[5].Real, out t[4].Real, input[inputOffset + 3].Real, input[inputOffset + 6].Real);
			Butterfly(out t[5].Imaginary, out t[4].Imaginary, input[inputOffset + 3].Imaginary, input[inputOffset + 6].Imaginary);
			Butterfly(out t[7].Real, out t[6].Real, input[inputOffset + 4].Real, input[inputOffset + 5].Real);
			Butterfly(out t[7].Imaginary, out t[6].Imaginary, input[inputOffset + 4].Imaginary, input[inputOffset + 5].Imaginary);

			w[0] = new FfmpegComplexFloat(t[0].Real - t[6].Real, t[0].Imaginary - t[6].Imaginary);
			w[1] = new FfmpegComplexFloat(t[2].Real - t[6].Real, t[2].Imaginary - t[6].Imaginary);
			w[2] = new FfmpegComplexFloat(t[1].Real - t[7].Real, t[1].Imaginary - t[7].Imaginary);
			w[3] = new FfmpegComplexFloat(t[3].Real + t[7].Real, t[3].Imaginary + t[7].Imaginary);
			z[0] = new FfmpegComplexFloat(dc.Real + t[4].Real, dc.Imaginary + t[4].Imaginary);
			z[1] = new FfmpegComplexFloat(t[0].Real + t[2].Real + t[6].Real, t[0].Imaginary + t[2].Imaginary + t[6].Imaginary);
			output[outputOffset] = new FfmpegComplexFloat(z[0].Real + z[1].Real, z[0].Imaginary + z[1].Imaginary);

			y[3] = new FfmpegComplexFloat(
				Table9[1] * (t[1].Real - t[3].Real + t[7].Real),
				Table9[1] * (t[1].Imaginary - t[3].Imaginary + t[7].Imaginary));
			x[3] = new FfmpegComplexFloat(z[0].Real + Table9[0] * z[1].Real, z[0].Imaginary + Table9[0] * z[1].Imaginary);
			z[0] = new FfmpegComplexFloat(dc.Real + Table9[0] * t[4].Real, dc.Imaginary + Table9[0] * t[4].Imaginary);
			x[1] = new FfmpegComplexFloat(Table9[2] * w[0].Real + Table9[5] * w[1].Real, Table9[2] * w[0].Imaginary + Table9[5] * w[1].Imaginary);
			x[2] = new FfmpegComplexFloat(Table9[5] * w[0].Real - Table9[6] * w[1].Real, Table9[5] * w[0].Imaginary - Table9[6] * w[1].Imaginary);
			y[1] = new FfmpegComplexFloat(Table9[3] * w[2].Real + Table9[4] * w[3].Real, Table9[3] * w[2].Imaginary + Table9[4] * w[3].Imaginary);
			y[2] = new FfmpegComplexFloat(Table9[4] * w[2].Real - Table9[7] * w[3].Real, Table9[4] * w[2].Imaginary - Table9[7] * w[3].Imaginary);
			y[0] = new FfmpegComplexFloat(Table9[1] * t[5].Real, Table9[1] * t[5].Imaginary);

			x[4] = new FfmpegComplexFloat(x[1].Real + x[2].Real, x[1].Imaginary + x[2].Imaginary);
			y[4] = new FfmpegComplexFloat(y[1].Real - y[2].Real, y[1].Imaginary - y[2].Imaginary);
			x[1] = new FfmpegComplexFloat(z[0].Real + x[1].Real, z[0].Imaginary + x[1].Imaginary);
			y[1] = new FfmpegComplexFloat(y[0].Real + y[1].Real, y[0].Imaginary + y[1].Imaginary);
			x[2] = new FfmpegComplexFloat(z[0].Real + x[2].Real, z[0].Imaginary + x[2].Imaginary);
			y[2] = new FfmpegComplexFloat(y[2].Real - y[0].Real, y[2].Imaginary - y[0].Imaginary);
			x[4] = new FfmpegComplexFloat(z[0].Real - x[4].Real, z[0].Imaginary - x[4].Imaginary);
			y[4] = new FfmpegComplexFloat(y[0].Real - y[4].Real, y[0].Imaginary - y[4].Imaginary);
			output[outputOffset + stride] = new FfmpegComplexFloat(x[1].Real + y[1].Imaginary, x[1].Imaginary - y[1].Real);
			output[outputOffset + 2 * stride] = new FfmpegComplexFloat(x[2].Real + y[2].Imaginary, x[2].Imaginary - y[2].Real);
			output[outputOffset + 3 * stride] = new FfmpegComplexFloat(x[3].Real + y[3].Imaginary, x[3].Imaginary - y[3].Real);
			output[outputOffset + 4 * stride] = new FfmpegComplexFloat(x[4].Real + y[4].Imaginary, x[4].Imaginary - y[4].Real);
			output[outputOffset + 5 * stride] = new FfmpegComplexFloat(x[4].Real - y[4].Imaginary, x[4].Imaginary + y[4].Real);
			output[outputOffset + 6 * stride] = new FfmpegComplexFloat(x[3].Real - y[3].Imaginary, x[3].Imaginary + y[3].Real);
			output[outputOffset + 7 * stride] = new FfmpegComplexFloat(x[2].Real - y[2].Imaginary, x[2].Imaginary + y[2].Real);
			output[outputOffset + 8 * stride] = new FfmpegComplexFloat(x[1].Real - y[1].Imaginary, x[1].Imaginary + y[1].Real);
		}

		private static void Fft15(Span<FfmpegComplexFloat> output, int outputOffset, ReadOnlySpan<FfmpegComplexFloat> input, int inputOffset, int stride)
		{
			Span<FfmpegComplexFloat> temporary = stackalloc FfmpegComplexFloat[15];
			for (var index = 0; index < 5; index++)
			{
				Fft3(temporary, index, input, inputOffset + index * 3, 5);
			}

			Fft5(output, outputOffset, temporary, 0, stride, 0, 6, 12, 3, 9);
			Fft5(output, outputOffset, temporary, 5, stride, 10, 1, 7, 13, 4);
			Fft5(output, outputOffset, temporary, 10, stride, 5, 11, 2, 8, 14);
		}

		private static void Butterfly(out float difference, out float sum, float first, float second)
		{
			difference = first - second;
			sum = first + second;
		}
	}
}
