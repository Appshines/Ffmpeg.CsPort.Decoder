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

namespace Ffmpeg.CsPort.Decoder.Codecs.MpegAudio
{
	/// <summary>
	/// Ports FFmpeg's scalar floating-point MPEG synthesis DCT, window application, and Layer III IMDCT.
	/// </summary>
	internal static class MpegAudioDsp
	{
		private const int SubbandLimit = 32;

		private static readonly float[] Cos0 =
		{
			(float)(0.50060299823519630134 / 2), (float)(0.50547095989754365998 / 2),
			(float)(0.51544730992262454697 / 2), (float)(0.53104259108978417447 / 2),
			(float)(0.55310389603444452782 / 2), (float)(0.58293496820613387367 / 2),
			(float)(0.62250412303566481615 / 2), (float)(0.67480834145500574602 / 2),
			(float)(0.74453627100229844977 / 2), (float)(0.83934964541552703873 / 2),
			(float)(0.97256823786196069369 / 2), (float)(1.16943993343288495515 / 4),
			(float)(1.48416461631416627724 / 4), (float)(2.05778100995341155085 / 8),
			(float)(3.40760841846871878570 / 8), (float)(10.19000812354805681150 / 32)
		};

		private static readonly float[] Cos1 =
		{
			(float)(0.50241928618815570551 / 2), (float)(0.52249861493968888062 / 2),
			(float)(0.56694403481635770368 / 2), (float)(0.64682178335999012954 / 2),
			(float)(0.78815462345125022473 / 2), (float)(1.06067768599034747134 / 4),
			(float)(1.72244709823833392782 / 4), (float)(5.10114861868916385802 / 16)
		};

		private static readonly float[] Cos2 =
		{
			(float)(0.50979557910415916894 / 2), (float)(0.60134488693504528054 / 2),
			(float)(0.89997622313641570463 / 2), (float)(2.56291544774150617881 / 8)
		};

		private static readonly float[] Cos3 =
		{
			(float)(0.54119610014619698439 / 2), (float)(1.30656296487637652785 / 4)
		};

		private const float Cos4 = (float)(0.70710678118654752440 / 2);

		private static readonly float[] Icos36 =
		{
			0.50190991877167369479f, 0.51763809020504152469f, 0.55168895948124587824f,
			0.61038729438072803416f, 0.70710678118654752439f, 0.87172339781054900991f,
			1.18310079157624925896f, 1.93185165257813657349f, 5.73685662283492756461f
		};

		private static readonly float[] Icos36Half =
		{
			(float)(0.50190991877167369479 / 2), (float)(0.51763809020504152469 / 2),
			(float)(0.55168895948124587824 / 2), (float)(0.61038729438072803416 / 2),
			(float)(0.70710678118654752439 / 2), (float)(0.87172339781054900991 / 2),
			(float)(1.18310079157624925896 / 4), (float)(1.93185165257813657349 / 4), 0
		};

		private static readonly byte[] DctFirstOrder = { 0, 16, 8, 24, 4, 20, 12, 28, 2, 18, 10, 26, 6, 22, 14, 30 };

		/// <summary>
		/// Executes the staged 32-point DCT in exactly the butterfly order of dct32_template.c.
		/// </summary>
		internal static void Dct32(float[] output, int outputOffset, float[] input, int inputOffset)
		{
			Span<float> values = stackalloc float[32];
			Bf0(values, 0, 31, Cos0[0], 2, input, inputOffset);
			Bf0(values, 15, 16, Cos0[15], 32, input, inputOffset);
			Bf(values, 0, 15, Cos1[0], 2); Bf(values, 16, 31, -Cos1[0], 2);
			Bf0(values, 7, 24, Cos0[7], 2, input, inputOffset);
			Bf0(values, 8, 23, Cos0[8], 2, input, inputOffset);
			Bf(values, 7, 8, Cos1[7], 16); Bf(values, 23, 24, -Cos1[7], 16);
			Bf(values, 0, 7, Cos2[0], 2); Bf(values, 8, 15, -Cos2[0], 2);
			Bf(values, 16, 23, Cos2[0], 2); Bf(values, 24, 31, -Cos2[0], 2);
			Bf0(values, 3, 28, Cos0[3], 2, input, inputOffset);
			Bf0(values, 12, 19, Cos0[12], 4, input, inputOffset);
			Bf(values, 3, 12, Cos1[3], 2); Bf(values, 19, 28, -Cos1[3], 2);
			Bf0(values, 4, 27, Cos0[4], 2, input, inputOffset);
			Bf0(values, 11, 20, Cos0[11], 4, input, inputOffset);
			Bf(values, 4, 11, Cos1[4], 2); Bf(values, 20, 27, -Cos1[4], 2);
			Bf(values, 3, 4, Cos2[3], 8); Bf(values, 11, 12, -Cos2[3], 8);
			Bf(values, 19, 20, Cos2[3], 8); Bf(values, 27, 28, -Cos2[3], 8);
			Bf(values, 0, 3, Cos3[0], 2); Bf(values, 4, 7, -Cos3[0], 2);
			Bf(values, 8, 11, Cos3[0], 2); Bf(values, 12, 15, -Cos3[0], 2);
			Bf(values, 16, 19, Cos3[0], 2); Bf(values, 20, 23, -Cos3[0], 2);
			Bf(values, 24, 27, Cos3[0], 2); Bf(values, 28, 31, -Cos3[0], 2);

			Bf0(values, 1, 30, Cos0[1], 2, input, inputOffset);
			Bf0(values, 14, 17, Cos0[14], 8, input, inputOffset);
			Bf(values, 1, 14, Cos1[1], 2); Bf(values, 17, 30, -Cos1[1], 2);
			Bf0(values, 6, 25, Cos0[6], 2, input, inputOffset);
			Bf0(values, 9, 22, Cos0[9], 2, input, inputOffset);
			Bf(values, 6, 9, Cos1[6], 4); Bf(values, 22, 25, -Cos1[6], 4);
			Bf(values, 1, 6, Cos2[1], 2); Bf(values, 9, 14, -Cos2[1], 2);
			Bf(values, 17, 22, Cos2[1], 2); Bf(values, 25, 30, -Cos2[1], 2);
			Bf0(values, 2, 29, Cos0[2], 2, input, inputOffset);
			Bf0(values, 13, 18, Cos0[13], 8, input, inputOffset);
			Bf(values, 2, 13, Cos1[2], 2); Bf(values, 18, 29, -Cos1[2], 2);
			Bf0(values, 5, 26, Cos0[5], 2, input, inputOffset);
			Bf0(values, 10, 21, Cos0[10], 2, input, inputOffset);
			Bf(values, 5, 10, Cos1[5], 4); Bf(values, 21, 26, -Cos1[5], 4);
			Bf(values, 2, 5, Cos2[2], 2); Bf(values, 10, 13, -Cos2[2], 2);
			Bf(values, 18, 21, Cos2[2], 2); Bf(values, 26, 29, -Cos2[2], 2);
			Bf(values, 1, 2, Cos3[1], 4); Bf(values, 5, 6, -Cos3[1], 4);
			Bf(values, 9, 10, Cos3[1], 4); Bf(values, 13, 14, -Cos3[1], 4);
			Bf(values, 17, 18, Cos3[1], 4); Bf(values, 21, 22, -Cos3[1], 4);
			Bf(values, 25, 26, Cos3[1], 4); Bf(values, 29, 30, -Cos3[1], 4);

			Bf1(values, 0, 1, 2, 3); Bf2(values, 4, 5, 6, 7);
			Bf1(values, 8, 9, 10, 11); Bf2(values, 12, 13, 14, 15);
			Bf1(values, 16, 17, 18, 19); Bf2(values, 20, 21, 22, 23);
			Bf1(values, 24, 25, 26, 27); Bf2(values, 28, 29, 30, 31);

			values[8] += values[12]; values[12] += values[10]; values[10] += values[14];
			values[14] += values[9]; values[9] += values[13]; values[13] += values[11]; values[11] += values[15];
			for (var index = 0; index < 16; index++) output[outputOffset + DctFirstOrder[index]] = values[index];

			values[24] += values[28]; values[28] += values[26]; values[26] += values[30];
			values[30] += values[25]; values[25] += values[29]; values[29] += values[27]; values[27] += values[31];
			output[outputOffset + 1] = values[16] + values[24]; output[outputOffset + 17] = values[17] + values[25];
			output[outputOffset + 9] = values[18] + values[26]; output[outputOffset + 25] = values[19] + values[27];
			output[outputOffset + 5] = values[20] + values[28]; output[outputOffset + 21] = values[21] + values[29];
			output[outputOffset + 13] = values[22] + values[30]; output[outputOffset + 29] = values[23] + values[31];
			output[outputOffset + 3] = values[24] + values[20]; output[outputOffset + 19] = values[25] + values[21];
			output[outputOffset + 11] = values[26] + values[22]; output[outputOffset + 27] = values[27] + values[23];
			output[outputOffset + 7] = values[28] + values[18]; output[outputOffset + 23] = values[29] + values[19];
			output[outputOffset + 15] = values[30] + values[17]; output[outputOffset + 31] = values[31];
		}

		internal static void Synthesize(float[] synthBuffer, ref int synthOffset, float[] subbandSamples, int subbandOffset, float[] output, int outputOffset)
		{
			Dct32(synthBuffer, synthOffset, subbandSamples, subbandOffset);
			ApplyWindow(synthBuffer, synthOffset, output, outputOffset);
			synthOffset = (synthOffset - 32) & 511;
		}

		/// <summary>
		/// Applies the 512-tap polyphase window with the same accumulation and reset points as FFmpeg's scalar macro expansion.
		/// </summary>
		private static void ApplyWindow(float[] synthBuffer, int synthOffset, float[] output, int outputOffset)
		{
			Array.Copy(synthBuffer, synthOffset, synthBuffer, synthOffset + 512, 32);
			var window = MpegAudioTables.SynthesisWindow;
			var window1 = 0;
			var window2 = 31;
			var sample = outputOffset;
			var sample2 = outputOffset + 31;
			var sum = 0.0f;
			var pointer = synthOffset + 16;
			for (var index = 0; index < 8; index++) sum += window[window1 + index * 64] * synthBuffer[pointer + index * 64];
			pointer = synthOffset + 48;
			for (var index = 0; index < 8; index++) sum -= window[window1 + 32 + index * 64] * synthBuffer[pointer + index * 64];
			output[sample++] = sum; sum = 0;
			window1++;

			for (var item = 1; item < 16; item++)
			{
				var sum2 = 0.0f;
				pointer = synthOffset + 16 + item;
				for (var index = 0; index < 8; index++)
				{
					var value = synthBuffer[pointer + index * 64];
					sum += window[window1 + index * 64] * value;
					sum2 -= window[window2 + index * 64] * value;
				}
				pointer = synthOffset + 48 - item;
				for (var index = 0; index < 8; index++)
				{
					var value = synthBuffer[pointer + index * 64];
					sum -= window[window1 + 32 + index * 64] * value;
					sum2 -= window[window2 + 32 + index * 64] * value;
				}
				output[sample++] = sum; sum = 0;
				sum += sum2;
				output[sample2--] = sum; sum = 0;
				window1++;
				window2--;
			}

			pointer = synthOffset + 32;
			for (var index = 0; index < 8; index++) sum -= window[window1 + 32 + index * 64] * synthBuffer[pointer + index * 64];
			output[sample] = sum;
		}

		internal static void Imdct36Blocks(float[] output, int outputOffset, float[] buffer, float[] input, int inputOffset, int count, bool switchPoint, int blockType)
		{
			var bufferOffset = 0;
			for (var block = 0; block < count; block++)
			{
				var windowIndex = switchPoint && block < 2 ? 0 : blockType;
				windowIndex += 4 & -(block & 1);
				Imdct36(output, outputOffset + block, buffer, bufferOffset, input, inputOffset + block * 18, windowIndex * MpegAudioTables.MdctBufferSize);
				bufferOffset += (block & 3) != 3 ? 1 : 69;
			}
		}

		/// <summary>
		/// Executes the in-place 36-point IMDCT and overlap-add in the scalar FFmpeg statement order.
		/// </summary>
		private static void Imdct36(float[] output, int outputOffset, float[] buffer, int bufferOffset, float[] input, int inputOffset, int windowOffset)
		{
			Span<float> temporary = stackalloc float[18];
			for (var index = 17; index >= 1; index--) input[inputOffset + index] += input[inputOffset + index - 1];
			for (var index = 17; index >= 3; index -= 2) input[inputOffset + index] += input[inputOffset + index - 2];
			var c1 = (float)(0.98480775301220805936 / 2); var c2 = (float)(0.93969262078590838405 / 2);
			var c3 = (float)(0.86602540378443864676 / 2); var c4 = (float)(0.76604444311897803520 / 2);
			var c5 = (float)(0.64278760968653932632 / 2); var c7 = (float)(0.34202014332566873304 / 2);
			var c8 = (float)(0.17364817766693034885 / 2);
			for (var parity = 0; parity < 2; parity++)
			{
				var t2 = input[inputOffset + parity + 8] + input[inputOffset + parity + 16] - input[inputOffset + parity + 4];
				var t3 = input[inputOffset + parity] + input[inputOffset + parity + 12] * 0.5f;
				var t1 = input[inputOffset + parity] - input[inputOffset + parity + 12];
				temporary[parity + 6] = t1 - t2 * 0.5f; temporary[parity + 16] = t1 + t2;
				var t0 = 2 * c2 * (input[inputOffset + parity + 4] + input[inputOffset + parity + 8]);
				t1 = -2 * c8 * (input[inputOffset + parity + 8] - input[inputOffset + parity + 16]);
				t2 = -2 * c4 * (input[inputOffset + parity + 4] + input[inputOffset + parity + 16]);
				temporary[parity + 10] = t3 - t0 - t2; temporary[parity + 2] = t3 + t0 + t1; temporary[parity + 14] = t3 + t2 - t1;
				temporary[parity + 4] = -2 * c3 * (input[inputOffset + parity + 10] + input[inputOffset + parity + 14] - input[inputOffset + parity + 2]);
				t2 = 2 * c1 * (input[inputOffset + parity + 2] + input[inputOffset + parity + 10]);
				t3 = -2 * c7 * (input[inputOffset + parity + 10] - input[inputOffset + parity + 14]);
				t0 = 2 * c3 * input[inputOffset + parity + 6];
				t1 = -2 * c5 * (input[inputOffset + parity + 2] + input[inputOffset + parity + 14]);
				temporary[parity] = t2 + t3 + t0; temporary[parity + 12] = t2 + t1 - t0; temporary[parity + 8] = t3 - t1 - t0;
			}

			var source = 0;
			for (var index = 0; index < 4; index++)
			{
				var t0 = temporary[source]; var t1 = temporary[source + 2]; var s0 = t1 + t0; var s2 = t1 - t0;
				var t2 = temporary[source + 1]; var t3 = temporary[source + 3];
				var s1 = 2 * Icos36Half[index] * (t3 + t2); var s3 = Icos36[8 - index] * (t3 - t2);
				t0 = s0 + s1; t1 = s0 - s1;
				StoreImdctPair(output, outputOffset, buffer, bufferOffset, windowOffset, 9 + index, 8 - index, t0, t1);
				t0 = s2 + s3; t1 = s2 - s3;
				StoreImdctPair(output, outputOffset, buffer, bufferOffset, windowOffset, 17 - index, index, t0, t1);
				source += 4;
			}
			var finalS0 = temporary[16]; var finalS1 = 2 * Icos36Half[4] * temporary[17];
			StoreImdctPair(output, outputOffset, buffer, bufferOffset, windowOffset, 13, 4, finalS0 + finalS1, finalS0 - finalS1);
		}

		private static void StoreImdctPair(float[] output, int outputOffset, float[] buffer, int bufferOffset, int windowOffset, int high, int low, float future, float current)
		{
			var window = MpegAudioTables.MdctWindows;
			output[outputOffset + high * SubbandLimit] = current * window[windowOffset + high] + buffer[bufferOffset + 4 * high];
			output[outputOffset + low * SubbandLimit] = current * window[windowOffset + low] + buffer[bufferOffset + 4 * low];
			buffer[bufferOffset + 4 * high] = future * window[windowOffset + MpegAudioTables.MdctBufferSize / 2 + high];
			buffer[bufferOffset + 4 * low] = future * window[windowOffset + MpegAudioTables.MdctBufferSize / 2 + low];
		}

		private static void Bf0(Span<float> values, int first, int second, float coefficient, int scale, float[] input, int inputOffset)
		{
			var temporary0 = input[inputOffset + first] + input[inputOffset + second];
			var temporary1 = input[inputOffset + first] - input[inputOffset + second];
			values[first] = temporary0; values[second] = scale * coefficient * temporary1;
		}

		private static void Bf(Span<float> values, int first, int second, float coefficient, int scale)
		{
			var temporary0 = values[first] + values[second]; var temporary1 = values[first] - values[second];
			values[first] = temporary0; values[second] = scale * coefficient * temporary1;
		}

		private static void Bf1(Span<float> values, int first, int second, int third, int fourth)
		{
			Bf(values, first, second, Cos4, 2); Bf(values, third, fourth, -Cos4, 2); values[third] += values[fourth];
		}

		private static void Bf2(Span<float> values, int first, int second, int third, int fourth)
		{
			Bf1(values, first, second, third, fourth); values[first] += values[third]; values[third] += values[second]; values[second] += values[fourth];
		}
	}
}
