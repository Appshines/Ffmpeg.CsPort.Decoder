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
	/// Ports the scalar 128-point RDFT and the 64-point type-I cosine/sine transforms used by WMA Voice.
	/// </summary>
	internal sealed class FfmpegFloatRealTransforms
	{
		private readonly FfmpegFloatFft fft64Forward = new FfmpegFloatFft(64, false);
		private readonly FfmpegFloatFft fft64Inverse = new FfmpegFloatFft(64, true);
		private readonly FfmpegFloatFft fft63Forward = new FfmpegFloatFft(63, false);
		private readonly FfmpegFloatFft fft65Forward = new FfmpegFloatFft(65, false);
		private readonly FfmpegComplexFloat[] input65 = new FfmpegComplexFloat[65];
		private readonly FfmpegComplexFloat[] output65 = new FfmpegComplexFloat[65];
		private readonly float[] dctInput = new float[130];
		private readonly float[] dstInput = new float[130];

		public void Forward128(ReadOnlySpan<float> input, Span<float> output)
		{
			for (var index = 0; index < 64; index++)
			{
				input65[index].Real = input[index * 2];
				input65[index].Imaginary = input[index * 2 + 1];
			}
			fft64Forward.Transform(input65.AsSpan(0, 64), output65.AsSpan(0, 64));
			PostprocessFull(output65, false, 1.0f);
			for (var index = 0; index <= 64; index++)
			{
				output[index * 2] = output65[index].Real;
				output[index * 2 + 1] = output65[index].Imaginary;
			}
		}

		public void Inverse128(ReadOnlySpan<float> input, Span<float> output)
		{
			for (var index = 0; index <= 64; index++)
			{
				output65[index].Real = input[index * 2];
				output65[index].Imaginary = input[index * 2 + 1];
			}
			output65[0].Imaginary = output65[64].Real;
			PostprocessFull(output65, true, 1.0f);
			fft64Inverse.Transform(output65.AsSpan(0, 64), input65.AsSpan(0, 64));
			for (var index = 0; index < 64; index++)
			{
				output[index * 2] = input65[index].Real;
				output[index * 2 + 1] = input65[index].Imaginary;
			}
		}

		public void DctI64(ReadOnlySpan<float> input, Span<float> output)
		{
			for (var index = 0; index < 63; index++) dctInput[index] = dctInput[126 - index] = input[index];
			dctInput[63] = input[63];
			ForwardHalf(dctInput, output, 126, true, 1.0f / 64.0f);
		}

		public void DstI64(ReadOnlySpan<float> input, Span<float> output)
		{
			dstInput[0] = 0.0f;
			for (var index = 1; index < 65; index++)
			{
				var value = input[index - 1];
				dstInput[index] = -value;
				dstInput[130 - index] = value;
			}
			dstInput[65] = 0.0f;
			ForwardHalf(dstInput, output, 130, false, 1.0f / 64.0f);
		}

		/// <summary>Applies FFmpeg's RDFT conjugate-pair postprocessing in its original operation order.</summary>
		private static void PostprocessFull(FfmpegComplexFloat[] data, bool inverse, float scale)
		{
			const int length = 128;
			const int half = length >> 1;
			const int quarter = length >> 2;
			var multiplier = (inverse ? 2.0f : 1.0f) * scale;
			var fact0 = (inverse ? 0.5f : 1.0f) * multiplier;
			var fact1 = (inverse ? 0.5f : 1.0f) * multiplier;
			var fact2 = multiplier;
			var fact3 = -multiplier;
			var fact4 = 0.5f * multiplier;
			var fact5 = -0.5f * multiplier;
			var fact6 = (0.5f - (inverse ? 1.0f : 0.0f)) * multiplier;
			var fact7 = -fact6;
			var first = data[0].Real;
			data[0].Real = first + data[0].Imaginary;
			data[0].Imaginary = first - data[0].Imaginary;
			data[0].Real = fact0 * data[0].Real;
			data[0].Imaginary = fact1 * data[0].Imaginary;
			data[quarter].Real = fact2 * data[quarter].Real;
			data[quarter].Imaginary = fact3 * data[quarter].Imaginary;
			var frequency = 2.0 * Math.PI / length;
			for (var index = 1; index < quarter; index++)
			{
				var cosine = (float)Math.Cos(index * frequency);
				var sine = (float)Math.Cos(((length - index * 4) / 4.0) * frequency) * (inverse ? 1.0f : -1.0f);
				var current = data[index];
				var opposite = data[half - index];
				var t0Real = fact4 * (current.Real + opposite.Real);
				var t0Imaginary = fact5 * (current.Imaginary - opposite.Imaginary);
				var t1Real = fact6 * (current.Imaginary + opposite.Imaginary);
				var t1Imaginary = fact7 * (current.Real - opposite.Real);
				var t2Real = t1Real * cosine - t1Imaginary * sine;
				var t2Imaginary = t1Real * sine + t1Imaginary * cosine;
				data[index].Real = t0Real + t2Real;
				data[index].Imaginary = t2Imaginary - t0Imaginary;
				data[half - index].Real = t0Real - t2Real;
				data[half - index].Imaginary = t2Imaginary + t0Imaginary;
			}
			if (!inverse)
			{
				data[half].Real = data[0].Imaginary;
				data[0].Imaginary = 0.0f;
				data[half].Imaginary = 0.0f;
			}
		}

		/// <summary>Runs the real-only RDFT variant used internally by FFmpeg's type-I DCT and DST wrappers.</summary>
		private void ForwardHalf(float[] input, Span<float> output, int length, bool realOutput, float scale)
		{
			var half = length >> 1;
			var quarter = length >> 2;
			for (var index = 0; index < half; index++)
			{
				input65[index].Real = input[index * 2];
				input65[index].Imaginary = input[index * 2 + 1];
			}
			if (half == 63) fft63Forward.Transform(input65.AsSpan(0, half), output65.AsSpan(0, half));
			else fft65Forward.Transform(input65.AsSpan(0, half), output65.AsSpan(0, half));
			for (var index = 0; index < half; index++)
			{
				output[index * 2] = output65[index].Real;
				output[index * 2 + 1] = output65[index].Imaginary;
			}
			var multiplier = scale;
			var fact0 = multiplier;
			var fact1 = multiplier;
			var fact2 = multiplier;
			var fact3 = -multiplier;
			var fact4 = 0.5f * multiplier;
			var fact5 = realOutput ? 1.0f / scale : -0.5f * multiplier;
			var fact6 = 0.5f * multiplier;
			var fact7 = -0.5f * multiplier;
			var moduloTwo = (length & 3) != 0;
			var middle = 0.0f;
			var direct = output65[0].Real;
			output65[0].Real = direct + output65[0].Imaginary;
			var dc = direct - output65[0].Imaginary;
			output65[0].Real = fact0 * output65[0].Real;
			dc = fact1 * dc;
			output65[quarter].Real = fact2 * output65[quarter].Real;
			if (!moduloTwo) output65[quarter].Imaginary = fact3 * output65[quarter].Imaginary;
			else
			{
				var firstMiddle = output65[quarter];
				var lastMiddle = output65[quarter + 1];
				var middle0 = realOutput ? fact4 * (firstMiddle.Real + lastMiddle.Real) : fact5 * (firstMiddle.Imaginary - lastMiddle.Imaginary);
				var middle1 = fact6 * (firstMiddle.Imaginary + lastMiddle.Imaginary);
				var middle2 = fact7 * (firstMiddle.Real - lastMiddle.Real);
				var middleCosine = (float)Math.Cos(quarter * (2.0 * Math.PI / length));
				var middleSine = -(float)Math.Cos(((length - quarter * 4) / 4.0) * (2.0 * Math.PI / length));
				if (realOutput)
				{
					var middle3 = middle1 * middleCosine - middle2 * middleSine;
					middle = middle0 - middle3;
				} else
				{
					var middle3 = middle1 * middleSine + middle2 * middleCosine;
					middle = middle0 + middle3;
				}
			}
			if (realOutput) output[0] = output65[0].Real;
			var frequency = 2.0 * Math.PI / length;
			for (var index = 1; index <= quarter; index++)
			{
				var first = output65[index];
				var last = output65[half - index];
				var t0 = realOutput ? fact4 * (first.Real + last.Real) : fact5 * (first.Imaginary - last.Imaginary);
				var t1 = fact6 * (first.Imaginary + last.Imaginary);
				var t2 = fact7 * (first.Real - last.Real);
				var cosine = (float)Math.Cos(index * frequency);
				var sine = -(float)Math.Cos(((length - index * 4) / 4.0) * frequency);
				if (realOutput)
				{
					var t3 = t1 * cosine - t2 * sine;
					output[index] = t0 + t3;
					output[length - index] = t0 - t3;
				} else
				{
					var t3 = t1 * sine + t2 * cosine;
					output[index - 1] = t3 - t0;
					output[length - index - 1] = t0 + t3;
				}
			}
			for (var index = 1; index < quarter + (realOutput ? 0 : 1); index++) output[half - index] = output[length - index];
			if (realOutput)
			{
				output[half] = dc;
				if (moduloTwo) output[quarter + 1] = middle * fact5;
			} else if (moduloTwo) output[quarter] = middle;
		}
	}
}
