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
using System;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Ports FFmpeg's CPU-zero scalar SBR QMF and high-frequency DSP primitives without decode-time allocations.</summary>
	internal static class AacSbrDsp
	{
		private static float ToggleSign(float value)
		{
			return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value) ^ int.MinValue);
		}

		public static float SumSquare(float[] values, int band, int offset, int count)
		{
			var sum0 = 0.0f;
			var sum1 = 0.0f;
			var row = values.AsSpan(band * AacSpectralBandReplication.HighBandStride +
				offset * AacSpectralBandReplication.HighTimeStride, count * AacSpectralBandReplication.HighTimeStride);
			for (var index = 0; index < count; index += 2)
			{
				var sourceIndex = index * AacSpectralBandReplication.HighTimeStride;
				var real = row[sourceIndex];
				var imaginary = row[sourceIndex + 1];
				sum0 += real * real;
				sum1 += imaginary * imaginary;
				real = row[sourceIndex + AacSpectralBandReplication.HighTimeStride];
				imaginary = row[sourceIndex + AacSpectralBandReplication.HighTimeStride + 1];
				sum0 += real * real;
				sum1 += imaginary * imaginary;
			}
			return sum0 + sum1;
		}

		public static void Autocorrelate(float[] source, int band, float[] correlation)
		{
			var sourceRow = source.AsSpan(band * AacSpectralBandReplication.LowBandStride,
				AacSpectralBandReplication.LowBandStride);
			var realSum2 = sourceRow[0] * sourceRow[4] + sourceRow[1] * sourceRow[5];
			var imaginarySum2 = sourceRow[0] * sourceRow[5] - sourceRow[1] * sourceRow[4];
			var realSum1 = 0.0f;
			var imaginarySum1 = 0.0f;
			var realSum0 = 0.0f;
			for (var index = 1; index < 38; index++)
			{
				var sourceIndex = index * AacSpectralBandReplication.LowTimeStride;
				var nextIndex = sourceIndex + AacSpectralBandReplication.LowTimeStride;
				var secondIndex = nextIndex + AacSpectralBandReplication.LowTimeStride;
				realSum0 += sourceRow[sourceIndex] * sourceRow[sourceIndex] + sourceRow[sourceIndex + 1] * sourceRow[sourceIndex + 1];
				realSum1 += sourceRow[sourceIndex] * sourceRow[nextIndex] + sourceRow[sourceIndex + 1] * sourceRow[nextIndex + 1];
				imaginarySum1 += sourceRow[sourceIndex] * sourceRow[nextIndex + 1] - sourceRow[sourceIndex + 1] * sourceRow[nextIndex];
				realSum2 += sourceRow[sourceIndex] * sourceRow[secondIndex] + sourceRow[sourceIndex + 1] * sourceRow[secondIndex + 1];
				imaginarySum2 += sourceRow[sourceIndex] * sourceRow[secondIndex + 1] - sourceRow[sourceIndex + 1] * sourceRow[secondIndex];
			}
			correlation[AacSpectralBandReplication.CorrelationSecondStride] = realSum2;
			correlation[AacSpectralBandReplication.CorrelationSecondStride + 1] = imaginarySum2;
			correlation[2 * AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] =
				realSum0 + sourceRow[0] * sourceRow[0] + sourceRow[1] * sourceRow[1];
			correlation[AacSpectralBandReplication.CorrelationFirstStride] =
				realSum0 + sourceRow[76] * sourceRow[76] + sourceRow[77] * sourceRow[77];
			correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] =
				realSum1 + sourceRow[0] * sourceRow[2] + sourceRow[1] * sourceRow[3];
			correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride + 1] =
				imaginarySum1 + sourceRow[0] * sourceRow[3] - sourceRow[1] * sourceRow[2];
			correlation[0] = realSum1 + sourceRow[76] * sourceRow[78] + sourceRow[77] * sourceRow[79];
			correlation[1] = imaginarySum1 + sourceRow[76] * sourceRow[79] - sourceRow[77] * sourceRow[78];
		}

		/// <summary>Runs the 32-slot analysis QMF, including FFmpeg's reverse window product, five-way sum, shuffles, and scaled IMDCT.</summary>
		public static void Analyze(AacSpectralBandReplication sbr, AacSbrData data, float[] input)
		{
			var samples = data.AnalysisFilterbankSamples;
			Array.Copy(samples, 1024, samples, 0, 288);
			Array.Copy(input, 0, samples, 288, 1024);
			var z = sbr.QmfScratch;
			for (var slot = 0; slot < 32; slot++)
			{
				var sampleOffset = slot * 32;
				for (var index = 0; index < 320; index++)
					z[index] = AacSbrTables.QmfWindowDownsampled[index] * samples[sampleOffset + 319 - index];
				for (var index = 0; index < 64; index++)
					z[index] = z[index] + z[index + 64] + z[index + 128] + z[index + 192] + z[index + 256];
				PreShuffle(z);
				sbr.AnalysisMdct.Transform(z.AsSpan(64, 64), z.AsSpan(0, 64));
				PostShuffle(data.Analysis, data.AnalysisPosition, slot, z);
			}
		}

		private static void PreShuffle(float[] values)
		{
			values[64] = values[0];
			values[65] = values[1];
			for (var index = 1; index < 31; index += 2)
			{
				values[64 + 2 * index] = ToggleSign(values[64 - index]);
				values[64 + 2 * index + 1] = values[index + 1];
				values[64 + 2 * index + 2] = ToggleSign(values[63 - index]);
				values[64 + 2 * index + 3] = values[index + 2];
			}
			values[126] = ToggleSign(values[33]);
			values[127] = values[32];
		}

		private static void PostShuffle(float[] destination, int position, int slot, float[] values)
		{
			var row = destination.AsSpan(position * AacSbrData.AnalysisPositionStride +
				slot * AacSbrData.AnalysisSlotStride, AacSbrData.AnalysisSlotStride);
			for (var band = 0; band < 32; band += 2)
			{
				var destinationIndex = band * AacSbrData.AnalysisBandStride;
				row[destinationIndex] = ToggleSign(values[63 - band]);
				row[destinationIndex + 1] = values[band];
				row[destinationIndex + AacSbrData.AnalysisBandStride] = ToggleSign(values[62 - band]);
				row[destinationIndex + AacSbrData.AnalysisBandStride + 1] = values[band + 1];
			}
		}

		public static void GenerateHighFrequency(
			float[] high,
			int highBand,
			float[] low,
			int lowBand,
			float[,] alpha0,
			float[,] alpha1,
			float bandwidth,
			int start,
			int end)
		{
			var alpha10 = alpha1[lowBand, 0] * bandwidth * bandwidth;
			var alpha11 = alpha1[lowBand, 1] * bandwidth * bandwidth;
			var alpha00 = alpha0[lowBand, 0] * bandwidth;
			var alpha01 = alpha0[lowBand, 1] * bandwidth;
			var highRow = high.AsSpan(highBand * AacSpectralBandReplication.HighBandStride,
				AacSpectralBandReplication.HighBandStride);
			var lowRow = low.AsSpan(lowBand * AacSpectralBandReplication.LowBandStride,
				AacSpectralBandReplication.LowBandStride);
			for (var index = start; index < end; index++)
			{
				var destinationIndex = index * AacSpectralBandReplication.HighTimeStride;
				var sourceIndex = index * AacSpectralBandReplication.LowTimeStride;
				highRow[destinationIndex] =
					lowRow[sourceIndex - 2 * AacSpectralBandReplication.LowTimeStride] * alpha10 -
					lowRow[sourceIndex - 2 * AacSpectralBandReplication.LowTimeStride + 1] * alpha11 +
					lowRow[sourceIndex - AacSpectralBandReplication.LowTimeStride] * alpha00 -
					lowRow[sourceIndex - AacSpectralBandReplication.LowTimeStride + 1] * alpha01 +
					lowRow[sourceIndex];
				highRow[destinationIndex + 1] =
					lowRow[sourceIndex - 2 * AacSpectralBandReplication.LowTimeStride + 1] * alpha10 +
					lowRow[sourceIndex - 2 * AacSpectralBandReplication.LowTimeStride] * alpha11 +
					lowRow[sourceIndex - AacSpectralBandReplication.LowTimeStride + 1] * alpha00 +
					lowRow[sourceIndex - AacSpectralBandReplication.LowTimeStride] * alpha01 +
					lowRow[sourceIndex + 1];
			}
		}

		public static void FilterGain(float[] adjusted, int position, int slot, int crossover, float[] high, float[] gain, int count, int sourceTime)
		{
			var adjustedRow = adjusted.AsSpan(position * AacSbrData.AdjustedPositionStride + slot * AacSbrData.AdjustedSlotStride +
				crossover * AacSbrData.AdjustedBandStride, count * AacSbrData.AdjustedBandStride);
			for (var band = 0; band < count; band++)
			{
				var adjustedIndex = band * AacSbrData.AdjustedBandStride;
				var highIndex = (crossover + band) * AacSpectralBandReplication.HighBandStride +
					sourceTime * AacSpectralBandReplication.HighTimeStride;
				adjustedRow[adjustedIndex] = high[highIndex] * gain[band];
				adjustedRow[adjustedIndex + 1] = high[highIndex + 1] * gain[band];
			}
		}

		public static void ApplyNoise(
			float[] adjusted,
			int position,
			int slot,
			int crossover,
			float[,] sinusoidAmplitude,
			int envelope,
			float[] noiseAmplitude,
			int noiseIndex,
			int sineIndex,
			int count)
		{
			var sign0 = sineIndex == 0 ? 1.0f : sineIndex == 2 ? -1.0f : 0.0f;
			var paritySign = 1 - 2 * (crossover & 1);
			var sign1 = sineIndex == 1 ? paritySign : sineIndex == 3 ? -paritySign : 0.0f;
			var adjustedRow = adjusted.AsSpan(position * AacSbrData.AdjustedPositionStride + slot * AacSbrData.AdjustedSlotStride +
				crossover * AacSbrData.AdjustedBandStride, count * AacSbrData.AdjustedBandStride);
			for (var band = 0; band < count; band++)
			{
				var adjustedIndex = band * AacSbrData.AdjustedBandStride;
				var real = adjustedRow[adjustedIndex];
				var imaginary = adjustedRow[adjustedIndex + 1];
				noiseIndex = (noiseIndex + 1) & 0x1ff;
				var sinusoid = sinusoidAmplitude[envelope, band];
				if (sinusoid != 0.0f)
				{
					real += sinusoid * sign0;
					imaginary += sinusoid * sign1;
				} else
				{
					real += noiseAmplitude[band] * AacSbrTables.Noise[noiseIndex * 2];
					imaginary += noiseAmplitude[band] * AacSbrTables.Noise[noiseIndex * 2 + 1];
				}
				adjustedRow[adjustedIndex] = real;
				adjustedRow[adjustedIndex + 1] = imaginary;
				sign1 = -sign1;
			}
		}

		/// <summary>Runs the 32-slot synthesis QMF and ten scalar windowed accumulation branches into 2048 output samples.</summary>
		public static void Synthesize(AacSpectralBandReplication sbr, AacSbrData data, int channel, float[] output, bool downsampled)
		{
			var division = downsampled ? 1 : 0;
			var window = downsampled ? AacSbrTables.QmfWindowDownsampled : AacSbrTables.QmfWindowUpsampled;
			var step = 128 >> division;
			var outputStep = 64 >> division;
			var samples = data.SynthesisFilterbankSamples;
			var outputChannelOffset = channel * AacSpectralBandReplication.OutputChannelStride;
			for (var slot = 0; slot < 32; slot++)
			{
				var realOutput = sbr.Output.AsSpan(outputChannelOffset + slot * AacSpectralBandReplication.OutputTimeStride,
					AacSpectralBandReplication.OutputTimeStride);
				var imaginaryOutput = sbr.Output.AsSpan(outputChannelOffset + AacSpectralBandReplication.OutputComponentStride +
					slot * AacSpectralBandReplication.OutputTimeStride, AacSpectralBandReplication.OutputTimeStride);
				if (data.SynthesisFilterbankSamplesOffset < step)
				{
					var savedSamples = (1280 - 128) >> division;
					Array.Copy(samples, 0, samples, 2304 - savedSamples, savedSamples);
					data.SynthesisFilterbankSamplesOffset = 2304 - savedSamples - step;
				} else
				{
					data.SynthesisFilterbankSamplesOffset -= step;
				}
				var sampleOffset = data.SynthesisFilterbankSamplesOffset;
				if (downsampled)
				{
					for (var band = 0; band < 32; band++)
					{
						realOutput[band] = -realOutput[band];
						sbr.MdctInput[band] = realOutput[band];
						sbr.MdctInput[32 + band] = imaginaryOutput[31 - band];
					}
					sbr.SynthesisMdct.Transform(sbr.MdctInput, sbr.MdctScratch.AsSpan(0, 64));
					for (var index = 0; index < 32; index++)
					{
						samples[sampleOffset + index] = sbr.MdctScratch[63 - 2 * index];
						samples[sampleOffset + 63 - index] = ToggleSign(sbr.MdctScratch[63 - 2 * index - 1]);
					}
				} else
				{
					for (var band = 0; band < 64; band++)
					{
						var imaginary = imaginaryOutput[band];
						if ((band & 3) == 1)
							imaginary = ToggleSign(imaginary);
						else if ((band & 3) == 3)
							imaginary = ToggleSign(imaginary);
						imaginaryOutput[band] = imaginary;
						sbr.MdctInput[band] = realOutput[band];
					}
					sbr.SynthesisMdct.Transform(sbr.MdctInput, sbr.MdctScratch.AsSpan(0, 64));
					for (var band = 0; band < 64; band++)
						sbr.MdctInput[band] = imaginaryOutput[band];
					sbr.SynthesisMdct.Transform(sbr.MdctInput, sbr.MdctScratch.AsSpan(64, 64));
					for (var index = 0; index < 64; index++)
					{
						samples[sampleOffset + index] = sbr.MdctScratch[64 + index] - sbr.MdctScratch[63 - index];
						samples[sampleOffset + 127 - index] = sbr.MdctScratch[64 + index] + sbr.MdctScratch[63 - index];
					}
				}
				var destination = slot * outputStep;
				for (var index = 0; index < outputStep; index++)
				{
					var value = samples[sampleOffset + index] * window[index];
					value = samples[sampleOffset + (192 >> division) + index] * window[(64 >> division) + index] + value;
					value = samples[sampleOffset + (256 >> division) + index] * window[(128 >> division) + index] + value;
					value = samples[sampleOffset + (448 >> division) + index] * window[(192 >> division) + index] + value;
					value = samples[sampleOffset + (512 >> division) + index] * window[(256 >> division) + index] + value;
					value = samples[sampleOffset + (704 >> division) + index] * window[(320 >> division) + index] + value;
					value = samples[sampleOffset + (768 >> division) + index] * window[(384 >> division) + index] + value;
					value = samples[sampleOffset + (960 >> division) + index] * window[(448 >> division) + index] + value;
					value = samples[sampleOffset + (1024 >> division) + index] * window[(512 >> division) + index] + value;
					value = samples[sampleOffset + (1216 >> division) + index] * window[(576 >> division) + index] + value;
					output[destination + index] = value;
				}
			}
		}
	}
}
