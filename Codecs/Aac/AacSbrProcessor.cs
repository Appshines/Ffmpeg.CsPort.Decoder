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
	/// <summary>Ports FFmpeg's scalar SBR dequantization, high-frequency reconstruction, envelope adjustment, and QMF application.</summary>
	internal sealed class AacSbrProcessor
	{
		private static readonly float[] BandwidthTable = { 0.0f, 0.75f, 0.9f, 0.98f };
		private static readonly float[] LimiterGain = { 0.70795f, 1.0f, 1.41254f, 10000000000.0f };
		private static readonly float[] Smooth =
		{
			0.33333333333333f, 0.30150283239582f, 0.21816949906249f, 0.11516383427084f, 0.03183050093751f
		};

		private static float Exp2Integer(int exponent)
		{
			if (exponent >= -126 && exponent <= 128)
				return BitConverter.Int32BitsToSingle((exponent + 127) << 23);
			if (exponent > 128)
				return float.PositiveInfinity;
			if (exponent > -150)
				return BitConverter.Int32BitsToSingle(1 << (exponent + 149));
			return 0.0f;
		}

		/// <summary>Expands quantized SBR envelope and noise factors, including the coupled channel-pair pan law.</summary>
		private static void Dequantize(AacSpectralBandReplication sbr, AacElementType elementType)
		{
			if (elementType == AacElementType.ChannelPair && sbr.Coupling)
			{
				var panOffset = sbr.Data[0].AmplitudeResolution ? 12 : 24;
				for (var envelope = 1; envelope <= sbr.Data[0].NumberOfEnvelopes; envelope++)
				{
					for (var band = 0; band < sbr.NumberOfBands[sbr.Data[0].FrequencyResolution[envelope]]; band++)
					{
						float first;
						float second;
						if (sbr.Data[0].AmplitudeResolution)
						{
							first = Exp2Integer(sbr.Data[0].QuantizedEnvelope[envelope, band] + 7);
							second = Exp2Integer(panOffset - sbr.Data[1].QuantizedEnvelope[envelope, band]);
						} else
						{
							var firstValue = sbr.Data[0].QuantizedEnvelope[envelope, band];
							var secondValue = panOffset - sbr.Data[1].QuantizedEnvelope[envelope, band];
							first = (float)(Exp2Integer((firstValue >> 1) + 7) * (firstValue % 2 == 0 ? 1.0 : 1.4142135623730951));
							second = (float)(Exp2Integer(secondValue >> 1) * ((secondValue & 1) == 0 ? 1.0 : 1.4142135623730951));
						}
						if (first > 1.0e20f)
							first = 1.0f;
						var factor = first / (1.0f + second);
						sbr.Data[0].Envelope[envelope, band] = factor;
						sbr.Data[1].Envelope[envelope, band] = factor * second;
					}
				}
				for (var envelope = 1; envelope <= sbr.Data[0].NumberOfNoiseEnvelopes; envelope++)
				{
					for (var band = 0; band < sbr.NumberOfNoiseBands; band++)
					{
						var first = Exp2Integer(7 - sbr.Data[0].QuantizedNoise[envelope, band]);
						var second = Exp2Integer(12 - sbr.Data[1].QuantizedNoise[envelope, band]);
						var factor = first / (1.0f + second);
						sbr.Data[0].Noise[envelope, band] = factor;
						sbr.Data[1].Noise[envelope, band] = factor * second;
					}
				}
				return;
			}
			var channelCount = elementType == AacElementType.ChannelPair ? 2 : 1;
			for (var channel = 0; channel < channelCount; channel++)
			{
				var data = sbr.Data[channel];
				for (var envelope = 1; envelope <= data.NumberOfEnvelopes; envelope++)
				{
					for (var band = 0; band < sbr.NumberOfBands[data.FrequencyResolution[envelope]]; band++)
					{
						if (data.AmplitudeResolution)
						{
							data.Envelope[envelope, band] = Exp2Integer(data.QuantizedEnvelope[envelope, band] + 6);
						} else
						{
							var value = data.QuantizedEnvelope[envelope, band];
							data.Envelope[envelope, band] = (float)(Exp2Integer((value >> 1) + 6) *
								((value & 1) == 0 ? 1.0 : 1.4142135623730951));
						}
						if (data.Envelope[envelope, band] > 1.0e20f)
							data.Envelope[envelope, band] = 1.0f;
					}
				}
				for (var envelope = 1; envelope <= data.NumberOfNoiseEnvelopes; envelope++)
				{
					for (var band = 0; band < sbr.NumberOfNoiseBands; band++)
						data.Noise[envelope, band] = Exp2Integer(6 - data.QuantizedNoise[envelope, band]);
				}
			}
		}

		private static void GenerateLowFrequency(AacSpectralBandReplication sbr, AacSbrData data)
		{
			Array.Clear(sbr.Low, 0, sbr.Low.Length);
			var currentAnalysisOffset = data.AnalysisPosition * AacSbrData.AnalysisPositionStride;
			for (var band = 0; band < sbr.Crossover[1]; band++)
			{
				var lowRow = sbr.Low.AsSpan(band * AacSpectralBandReplication.LowBandStride,
					AacSpectralBandReplication.LowBandStride);
				var analysisBandOffset = currentAnalysisOffset + band * AacSbrData.AnalysisBandStride;
				for (var time = 8; time < 40; time++)
				{
					var lowIndex = time * AacSpectralBandReplication.LowTimeStride;
					var analysisIndex = analysisBandOffset + (time - 8) * AacSbrData.AnalysisSlotStride;
					lowRow[lowIndex] = data.Analysis[analysisIndex];
					lowRow[lowIndex + 1] = data.Analysis[analysisIndex + 1];
				}
			}
			var previousPosition = 1 - data.AnalysisPosition;
			var previousAnalysisOffset = previousPosition * AacSbrData.AnalysisPositionStride;
			for (var band = 0; band < sbr.Crossover[0]; band++)
			{
				var lowRow = sbr.Low.AsSpan(band * AacSpectralBandReplication.LowBandStride,
					AacSpectralBandReplication.LowBandStride);
				var analysisBandOffset = previousAnalysisOffset + band * AacSbrData.AnalysisBandStride;
				for (var time = 0; time < 8; time++)
				{
					var lowIndex = time * AacSpectralBandReplication.LowTimeStride;
					var analysisIndex = analysisBandOffset + (time + 24) * AacSbrData.AnalysisSlotStride;
					lowRow[lowIndex] = data.Analysis[analysisIndex];
					lowRow[lowIndex + 1] = data.Analysis[analysisIndex + 1];
				}
			}
		}

		/// <summary>
		/// Estimates SBR autocorrelation and derives the two inverse-filter coefficients for every low subband.
		/// </summary>
		private static void InverseFilter(AacSpectralBandReplication sbr)
		{
			var correlation = sbr.CorrelationScratch;
			for (var band = 0; band < sbr.K[0]; band++)
			{
				AacSbrDsp.Autocorrelate(sbr.Low, band, correlation);
				var denominator = correlation[2 * AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] *
					correlation[AacSpectralBandReplication.CorrelationFirstStride] -
					(correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] *
					correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] +
					correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride + 1] *
					correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride + 1]) / 1.000001f;
				if (denominator == 0.0f)
				{
					sbr.Alpha1[band, 0] = 0.0f;
					sbr.Alpha1[band, 1] = 0.0f;
				} else
				{
					var real = correlation[0] * correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] -
						correlation[1] * correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride + 1] -
						correlation[AacSpectralBandReplication.CorrelationSecondStride] * correlation[AacSpectralBandReplication.CorrelationFirstStride];
					var imaginary = correlation[0] * correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride + 1] +
						correlation[1] * correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] -
						correlation[AacSpectralBandReplication.CorrelationSecondStride + 1] * correlation[AacSpectralBandReplication.CorrelationFirstStride];
					sbr.Alpha1[band, 0] = real / denominator;
					sbr.Alpha1[band, 1] = imaginary / denominator;
				}
				if (correlation[AacSpectralBandReplication.CorrelationFirstStride] == 0.0f)
				{
					sbr.Alpha0[band, 0] = 0.0f;
					sbr.Alpha0[band, 1] = 0.0f;
				} else
				{
					var real = correlation[0] + sbr.Alpha1[band, 0] *
						correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] +
						sbr.Alpha1[band, 1] * correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride + 1];
					var imaginary = correlation[1] + sbr.Alpha1[band, 1] *
						correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride] -
						sbr.Alpha1[band, 0] * correlation[AacSpectralBandReplication.CorrelationFirstStride + AacSpectralBandReplication.CorrelationSecondStride + 1];
					sbr.Alpha0[band, 0] = -real / correlation[AacSpectralBandReplication.CorrelationFirstStride];
					sbr.Alpha0[band, 1] = -imaginary / correlation[AacSpectralBandReplication.CorrelationFirstStride];
				}
				if (sbr.Alpha1[band, 0] * sbr.Alpha1[band, 0] + sbr.Alpha1[band, 1] * sbr.Alpha1[band, 1] >= 16.0f ||
					sbr.Alpha0[band, 0] * sbr.Alpha0[band, 0] + sbr.Alpha0[band, 1] * sbr.Alpha0[band, 1] >= 16.0f)
				{
					sbr.Alpha1[band, 0] = 0.0f;
					sbr.Alpha1[band, 1] = 0.0f;
					sbr.Alpha0[band, 0] = 0.0f;
					sbr.Alpha0[band, 1] = 0.0f;
				}
			}
		}

		private static void UpdateChirp(AacSpectralBandReplication sbr, AacSbrData data)
		{
			for (var band = 0; band < sbr.NumberOfNoiseBands; band++)
			{
				var bandwidth = data.InverseFilteringMode[0, band] + data.InverseFilteringMode[1, band] == 1
					? 0.6f : BandwidthTable[data.InverseFilteringMode[0, band]];
				if (bandwidth < data.Bandwidth[band])
					bandwidth = 0.75f * bandwidth + 0.25f * data.Bandwidth[band];
				else
					bandwidth = 0.90625f * bandwidth + 0.09375f * data.Bandwidth[band];
				data.Bandwidth[band] = bandwidth < 0.015625f ? 0.0f : bandwidth;
			}
		}

		private static int GenerateHighFrequency(AacSpectralBandReplication sbr, AacSbrData data)
		{
			var noiseBand = 0;
			var destinationBand = sbr.Crossover[1];
			for (var patch = 0; patch < sbr.NumberOfPatches; patch++)
			{
				for (var offset = 0; offset < sbr.PatchSubbandCount[patch]; offset++, destinationBand++)
				{
					var sourceBand = sbr.PatchStartSubband[patch] + offset;
					while (noiseBand <= sbr.NumberOfNoiseBands && destinationBand >= sbr.NoiseFrequencyTable[noiseBand])
						noiseBand++;
					noiseBand--;
					if (noiseBand < 0)
						return -1;
					AacSbrDsp.GenerateHighFrequency(sbr.High, destinationBand, sbr.Low, sourceBand, sbr.Alpha0, sbr.Alpha1,
						data.Bandwidth[noiseBand], 2 * data.TimeEnvelope[0] + 2, 2 * data.TimeEnvelope[data.NumberOfEnvelopes] + 2);
				}
			}
			for (; destinationBand < sbr.Crossover[1] + sbr.NumberOfSubbands[1]; destinationBand++)
			{
				var highRow = sbr.High.AsSpan(destinationBand * AacSpectralBandReplication.HighBandStride,
					AacSpectralBandReplication.HighBandStride);
				for (var time = 0; time < 40; time++)
				{
					var highIndex = time * AacSpectralBandReplication.HighTimeStride;
					highRow[highIndex] = 0.0f;
					highRow[highIndex + 1] = 0.0f;
				}
			}
			return 0;
		}

		/// <summary>Maps envelope, noise, and sinusoid controls from SBR bands onto individual QMF subbands.</summary>
		private static int Map(AacSpectralBandReplication sbr, AacSbrData data)
		{
			for (var envelope = 1; envelope < 8; envelope++)
			{
				for (var band = 0; band < 48; band++)
					data.SinusoidIndexMapped[envelope, band] = 0;
			}
			for (var envelope = 0; envelope < data.NumberOfEnvelopes; envelope++)
			{
				var highResolution = data.FrequencyResolution[envelope + 1] != 0;
				var table = highResolution ? sbr.HighFrequencyTable : sbr.LowFrequencyTable;
				var bandCount = sbr.NumberOfBands[highResolution ? 1 : 0];
				if (sbr.Crossover[1] != table[0])
					return -1;
				for (var band = 0; band < bandCount; band++)
				{
					for (var qmf = table[band]; qmf < table[band + 1]; qmf++)
						sbr.OriginalEnvelopeMapped[envelope, qmf - sbr.Crossover[1]] = data.Envelope[envelope + 1, band];
				}
				var noiseEnvelope = data.NumberOfNoiseEnvelopes > 1 && data.TimeEnvelope[envelope] >= data.TimeNoise[1] ? 1 : 0;
				for (var band = 0; band < sbr.NumberOfNoiseBands; band++)
				{
					for (var qmf = sbr.NoiseFrequencyTable[band]; qmf < sbr.NoiseFrequencyTable[band + 1]; qmf++)
						sbr.NoiseMapped[envelope, qmf - sbr.Crossover[1]] = data.Noise[noiseEnvelope + 1, band];
				}
				for (var band = 0; band < sbr.NumberOfBands[1]; band++)
				{
					if (data.AddHarmonicFlag)
					{
						var midpoint = (sbr.HighFrequencyTable[band] + sbr.HighFrequencyTable[band + 1]) >> 1;
						data.SinusoidIndexMapped[envelope + 1, midpoint - sbr.Crossover[1]] = (byte)(data.AddHarmonic[band] *
							(envelope >= data.AttackEnvelope[1] || data.SinusoidIndexMapped[0, midpoint - sbr.Crossover[1]] == 1 ? 1 : 0));
					}
				}
				for (var band = 0; band < bandCount; band++)
				{
					var present = false;
					for (var qmf = table[band]; qmf < table[band + 1]; qmf++)
					{
						if (data.SinusoidIndexMapped[envelope + 1, qmf - sbr.Crossover[1]] != 0)
						{
							present = true;
							break;
						}
					}
					for (var qmf = table[band]; qmf < table[band + 1]; qmf++)
						sbr.SinusoidMapped[envelope, qmf - sbr.Crossover[1]] = (byte)(present ? 1 : 0);
				}
			}
			for (var band = 0; band < 48; band++)
				data.SinusoidIndexMapped[0, band] = data.SinusoidIndexMapped[data.NumberOfEnvelopes, band];
			return 0;
		}

		private static void EstimateEnvelope(AacSpectralBandReplication sbr, AacSbrData data)
		{
			if (sbr.InterpolateFrequency)
			{
				for (var envelope = 0; envelope < data.NumberOfEnvelopes; envelope++)
				{
					var reciprocalSize = 0.5f / (data.TimeEnvelope[envelope + 1] - data.TimeEnvelope[envelope]);
					var lower = data.TimeEnvelope[envelope] * 2 + 2;
					var upper = data.TimeEnvelope[envelope + 1] * 2 + 2;
					if (lower >= 40)
						return;
					for (var band = 0; band < sbr.NumberOfSubbands[1]; band++)
						sbr.CurrentEnvelope[envelope, band] = AacSbrDsp.SumSquare(sbr.High, band + sbr.Crossover[1], lower, upper - lower) * reciprocalSize;
				}
				return;
			}
			for (var envelope = 0; envelope < data.NumberOfEnvelopes; envelope++)
			{
				var envelopeSize = 2 * (data.TimeEnvelope[envelope + 1] - data.TimeEnvelope[envelope]);
				var lower = data.TimeEnvelope[envelope] * 2 + 2;
				var upper = data.TimeEnvelope[envelope + 1] * 2 + 2;
				var highResolution = data.FrequencyResolution[envelope + 1] != 0;
				var table = highResolution ? sbr.HighFrequencyTable : sbr.LowFrequencyTable;
				if (lower >= 40)
					return;
				for (var band = 0; band < sbr.NumberOfBands[highResolution ? 1 : 0]; band++)
				{
					var sum = 0.0f;
					var denominator = envelopeSize * (table[band + 1] - table[band]);
					for (var qmf = table[band]; qmf < table[band + 1]; qmf++)
						sum += AacSbrDsp.SumSquare(sbr.High, qmf, lower, upper - lower);
					sum /= denominator;
					for (var qmf = table[band]; qmf < table[band + 1]; qmf++)
						sbr.CurrentEnvelope[envelope, qmf - sbr.Crossover[1]] = sum;
				}
			}
		}

		/// <summary>Calculates limiter-constrained gain, noise, and sinusoid amplitudes in FFmpeg's scalar operation order.</summary>
		private static void CalculateGain(AacSpectralBandReplication sbr, AacSbrData data)
		{
			const float floatMinimum = 1.17549435e-38f;
			const float floatEpsilon = 1.19209290e-7f;
			for (var envelope = 0; envelope < data.NumberOfEnvelopes; envelope++)
			{
				var delta = envelope != data.AttackEnvelope[1] && envelope != data.AttackEnvelope[0];
				for (var limiter = 0; limiter < sbr.NumberOfLimiterBands; limiter++)
				{
					var start = sbr.LimiterFrequencyTable[limiter] - sbr.Crossover[1];
					var end = sbr.LimiterFrequencyTable[limiter + 1] - sbr.Crossover[1];
					for (var band = start; band < end; band++)
					{
						var value = sbr.OriginalEnvelopeMapped[envelope, band] / (1.0f + sbr.NoiseMapped[envelope, band]);
						sbr.NoiseAmplitude[envelope, band] = MathF.Sqrt(value * sbr.NoiseMapped[envelope, band]);
						sbr.SinusoidAmplitude[envelope, band] = MathF.Sqrt(value * data.SinusoidIndexMapped[envelope + 1, band]);
						if (sbr.SinusoidMapped[envelope, band] == 0)
						{
							sbr.Gain[envelope, band] = MathF.Sqrt(sbr.OriginalEnvelopeMapped[envelope, band] /
								((1.0f + sbr.CurrentEnvelope[envelope, band]) * (1.0f + sbr.NoiseMapped[envelope, band] * (delta ? 1 : 0))));
						} else
						{
							sbr.Gain[envelope, band] = MathF.Sqrt(sbr.OriginalEnvelopeMapped[envelope, band] * sbr.NoiseMapped[envelope, band] /
								((1.0f + sbr.CurrentEnvelope[envelope, band]) * (1.0f + sbr.NoiseMapped[envelope, band])));
						}
						sbr.Gain[envelope, band] += floatMinimum;
					}
					var originalSum = 0.0f;
					var currentSum = 0.0f;
					for (var band = start; band < end; band++)
					{
						originalSum += sbr.OriginalEnvelopeMapped[envelope, band];
						currentSum += sbr.CurrentEnvelope[envelope, band];
					}
					var maximumGain = LimiterGain[sbr.LimiterGains] * MathF.Sqrt((floatEpsilon + originalSum) / (floatEpsilon + currentSum));
					maximumGain = Math.Min(100000.0f, maximumGain);
					for (var band = start; band < end; band++)
					{
						var maximumNoise = sbr.NoiseAmplitude[envelope, band] * maximumGain / sbr.Gain[envelope, band];
						sbr.NoiseAmplitude[envelope, band] = Math.Min(sbr.NoiseAmplitude[envelope, band], maximumNoise);
						sbr.Gain[envelope, band] = Math.Min(sbr.Gain[envelope, band], maximumGain);
					}
					originalSum = 0.0f;
					currentSum = 0.0f;
					for (var band = start; band < end; band++)
					{
						originalSum += sbr.OriginalEnvelopeMapped[envelope, band];
						currentSum += sbr.CurrentEnvelope[envelope, band] * sbr.Gain[envelope, band] * sbr.Gain[envelope, band] +
							sbr.SinusoidAmplitude[envelope, band] * sbr.SinusoidAmplitude[envelope, band] +
							(delta && sbr.SinusoidAmplitude[envelope, band] == 0.0f ? 1.0f : 0.0f) *
							sbr.NoiseAmplitude[envelope, band] * sbr.NoiseAmplitude[envelope, band];
					}
					var boost = MathF.Sqrt((floatEpsilon + originalSum) / (floatEpsilon + currentSum));
					boost = Math.Min(1.584893192f, boost);
					for (var band = start; band < end; band++)
					{
						sbr.Gain[envelope, band] *= boost;
						sbr.NoiseAmplitude[envelope, band] *= boost;
						sbr.SinusoidAmplitude[envelope, band] *= boost;
					}
				}
			}
		}

		/// <summary>Applies time-smoothed gains, deterministic noise, and attack-envelope sinusoids to reconstructed high bands.</summary>
		private static void Assemble(AacSpectralBandReplication sbr, AacSbrData data)
		{
			var smoothingLength = sbr.SmoothingMode ? 0 : 4;
			var crossover = sbr.Crossover[1];
			var subbandCount = sbr.NumberOfSubbands[1];
			var noiseIndex = data.NoiseIndex;
			var sineIndex = data.SineIndex;
			if (sbr.Reset)
			{
				for (var time = 0; time < smoothingLength; time++)
				{
					for (var band = 0; band < subbandCount; band++)
					{
						data.GainHistory[time + 2 * data.TimeEnvelope[0], band] = sbr.Gain[0, band];
						data.NoiseHistory[time + 2 * data.TimeEnvelope[0], band] = sbr.NoiseAmplitude[0, band];
					}
				}
			} else if (smoothingLength != 0)
			{
				for (var time = 0; time < 4; time++)
				{
					for (var band = 0; band < 48; band++)
					{
						data.GainHistory[time + 2 * data.TimeEnvelope[0], band] = data.GainHistory[time + 2 * data.PreviousEnvelopeEnd, band];
						data.NoiseHistory[time + 2 * data.TimeEnvelope[0], band] = data.NoiseHistory[time + 2 * data.PreviousEnvelopeEnd, band];
					}
				}
			}
			for (var envelope = 0; envelope < data.NumberOfEnvelopes; envelope++)
			{
				for (var time = 2 * data.TimeEnvelope[envelope]; time < 2 * data.TimeEnvelope[envelope + 1]; time++)
				{
					for (var band = 0; band < subbandCount; band++)
					{
						data.GainHistory[smoothingLength + time, band] = sbr.Gain[envelope, band];
						data.NoiseHistory[smoothingLength + time, band] = sbr.NoiseAmplitude[envelope, band];
					}
				}
			}
			for (var envelope = 0; envelope < data.NumberOfEnvelopes; envelope++)
			{
				for (var time = 2 * data.TimeEnvelope[envelope]; time < 2 * data.TimeEnvelope[envelope + 1]; time++)
				{
					var gainFilter = sbr.GainFilterScratch;
					var noiseFilter = sbr.NoiseFilterScratch;
					if (smoothingLength != 0 && envelope != data.AttackEnvelope[0] && envelope != data.AttackEnvelope[1])
					{
						for (var band = 0; band < subbandCount; band++)
						{
							gainFilter[band] = 0.0f;
							noiseFilter[band] = 0.0f;
							var historyIndex = time + smoothingLength;
							for (var tap = 0; tap <= smoothingLength; tap++)
							{
								gainFilter[band] += data.GainHistory[historyIndex - tap, band] * Smooth[tap];
								noiseFilter[band] += data.NoiseHistory[historyIndex - tap, band] * Smooth[tap];
							}
						}
					} else
					{
						for (var band = 0; band < subbandCount; band++)
						{
							gainFilter[band] = data.GainHistory[time + smoothingLength, band];
							noiseFilter[band] = data.NoiseHistory[time, band];
						}
					}
					AacSbrDsp.FilterGain(data.Adjusted, data.AnalysisPosition, time, crossover, sbr.High, gainFilter, subbandCount, time + 2);
					if (envelope != data.AttackEnvelope[0] && envelope != data.AttackEnvelope[1])
					{
						AacSbrDsp.ApplyNoise(data.Adjusted, data.AnalysisPosition, time, crossover, sbr.SinusoidAmplitude,
							envelope, noiseFilter, noiseIndex, sineIndex, subbandCount);
					} else
					{
						var component = sineIndex & 1;
						var firstSign = 1 - ((sineIndex + (crossover & 1)) & 2);
						var secondSign = (firstSign ^ -component) + component;
						var adjustedRow = data.Adjusted.AsSpan(data.AnalysisPosition * AacSbrData.AdjustedPositionStride +
							time * AacSbrData.AdjustedSlotStride + crossover * AacSbrData.AdjustedBandStride,
							subbandCount * AacSbrData.AdjustedBandStride);
						for (var band = 0; band + 1 < subbandCount; band += 2)
						{
							adjustedRow[band * AacSbrData.AdjustedBandStride + component] += sbr.SinusoidAmplitude[envelope, band] * firstSign;
							adjustedRow[(band + 1) * AacSbrData.AdjustedBandStride + component] += sbr.SinusoidAmplitude[envelope, band + 1] * secondSign;
						}
						if ((subbandCount & 1) != 0)
							adjustedRow[(subbandCount - 1) * AacSbrData.AdjustedBandStride + component] +=
								sbr.SinusoidAmplitude[envelope, subbandCount - 1] * firstSign;
					}
					noiseIndex = (noiseIndex + subbandCount) & 0x1ff;
					sineIndex = (sineIndex + 1) & 3;
				}
			}
			data.NoiseIndex = noiseIndex;
			data.SineIndex = sineIndex;
		}

		/// <summary>
		/// Applies SBR gains, noise, and sinusoidal components to generated high-frequency subbands in slot order.
		/// </summary>
		private static void GenerateOutputSubbands(AacSpectralBandReplication sbr, AacSbrData data, int channel)
		{
			var temporaryTime = Math.Max(2 * data.PreviousEnvelopeEnd - 32, 0);
			var outputChannelOffset = channel * AacSpectralBandReplication.OutputChannelStride;
			sbr.Output.AsSpan(outputChannelOffset, AacSpectralBandReplication.OutputChannelStride).Clear();
			var bandIndex = 0;
			for (; bandIndex < sbr.Crossover[0]; bandIndex++)
			{
				var lowRow = sbr.Low.AsSpan(bandIndex * AacSpectralBandReplication.LowBandStride,
					AacSpectralBandReplication.LowBandStride);
				for (var time = 0; time < temporaryTime; time++)
				{
					var outputIndex = outputChannelOffset + time * AacSpectralBandReplication.OutputTimeStride + bandIndex;
					var lowIndex = (time + 2) * AacSpectralBandReplication.LowTimeStride;
					sbr.Output[outputIndex] = lowRow[lowIndex];
					sbr.Output[outputIndex + AacSpectralBandReplication.OutputComponentStride] = lowRow[lowIndex + 1];
				}
			}
			var previousAdjustedOffset = (1 - data.AnalysisPosition) * AacSbrData.AdjustedPositionStride;
			for (; bandIndex < sbr.Crossover[0] + sbr.NumberOfSubbands[0]; bandIndex++)
			{
				for (var time = 0; time < temporaryTime; time++)
				{
					var outputIndex = outputChannelOffset + time * AacSpectralBandReplication.OutputTimeStride + bandIndex;
					var adjustedIndex = previousAdjustedOffset + (time + 32) * AacSbrData.AdjustedSlotStride +
						bandIndex * AacSbrData.AdjustedBandStride;
					sbr.Output[outputIndex] = data.Adjusted[adjustedIndex];
					sbr.Output[outputIndex + AacSpectralBandReplication.OutputComponentStride] = data.Adjusted[adjustedIndex + 1];
				}
			}
			bandIndex = 0;
			for (; bandIndex < sbr.Crossover[1]; bandIndex++)
			{
				var lowRow = sbr.Low.AsSpan(bandIndex * AacSpectralBandReplication.LowBandStride,
					AacSpectralBandReplication.LowBandStride);
				for (var time = temporaryTime; time < 38; time++)
				{
					var outputIndex = outputChannelOffset + time * AacSpectralBandReplication.OutputTimeStride + bandIndex;
					var lowIndex = (time + 2) * AacSpectralBandReplication.LowTimeStride;
					sbr.Output[outputIndex] = lowRow[lowIndex];
					sbr.Output[outputIndex + AacSpectralBandReplication.OutputComponentStride] = lowRow[lowIndex + 1];
				}
			}
			var currentAdjustedOffset = data.AnalysisPosition * AacSbrData.AdjustedPositionStride;
			for (; bandIndex < sbr.Crossover[1] + sbr.NumberOfSubbands[1]; bandIndex++)
			{
				for (var time = temporaryTime; time < 32; time++)
				{
					var outputIndex = outputChannelOffset + time * AacSpectralBandReplication.OutputTimeStride + bandIndex;
					var adjustedIndex = currentAdjustedOffset + time * AacSbrData.AdjustedSlotStride +
						bandIndex * AacSbrData.AdjustedBandStride;
					sbr.Output[outputIndex] = data.Adjusted[adjustedIndex];
					sbr.Output[outputIndex + AacSpectralBandReplication.OutputComponentStride] = data.Adjusted[adjustedIndex + 1];
				}
			}
		}

		/// <summary>Applies SBR or pure QMF upsampling to one decoded AAC element and replaces its core PCM with final-rate samples.</summary>
		public void Apply(AacChannelElement element, AacElementType elementType, int outputSampleRate, bool parametricStereo)
		{
			var sbr = element.Sbr;
			var channelCount = elementType == AacElementType.ChannelPair ? 2 : 1;
			if (elementType != sbr.ElementType)
			{
				sbr.Started = false;
				sbr.ReadyForDequantization = false;
			}
			if (sbr.Started && !sbr.ReadyForDequantization)
			{
				sbr.Started = false;
				sbr.Crossover[1] = 32;
				sbr.NumberOfSubbands[1] = 0;
			}
			if (!sbr.PreviousGridPushed)
			{
				sbr.Crossover[0] = sbr.Crossover[1];
				sbr.NumberOfSubbands[0] = sbr.NumberOfSubbands[1];
			} else
			{
				sbr.PreviousGridPushed = false;
			}
			if (sbr.Started)
			{
				Dequantize(sbr, elementType);
				sbr.ReadyForDequantization = false;
			}
			for (var channel = 0; channel < channelCount; channel++)
			{
				var data = sbr.Data[channel];
				AacSbrDsp.Analyze(sbr, data, element.Channels[channel].Output);
				GenerateLowFrequency(sbr, data);
				data.AnalysisPosition ^= 1;
				if (sbr.Started)
				{
					InverseFilter(sbr);
					UpdateChirp(sbr, data);
					GenerateHighFrequency(sbr, data);
					if (Map(sbr, data) == 0)
					{
						EstimateEnvelope(sbr, data);
						CalculateGain(sbr, data);
						Assemble(sbr, data);
					}
				}
				GenerateOutputSubbands(sbr, data, channel);
			}
			var applyParametricStereo = parametricStereo && elementType == AacElementType.SingleChannel;
			if (applyParametricStereo)
			{
				if (sbr.ParametricStereo.Common.Started)
				{
					AacPsProcessor.Apply(sbr.ParametricStereo, sbr.Output, sbr.Crossover[1] + sbr.NumberOfSubbands[1]);
				} else
				{
					sbr.Output.AsSpan(0, AacSpectralBandReplication.OutputChannelStride).CopyTo(
						sbr.Output.AsSpan(AacSpectralBandReplication.OutputChannelStride,
							AacSpectralBandReplication.OutputChannelStride));
				}
			}
			var downsampled = outputSampleRate < sbr.SampleRate;
			AacSbrDsp.Synthesize(sbr, sbr.Data[0], 0, element.Channels[0].Output, downsampled);
			if (channelCount == 2 || applyParametricStereo)
				AacSbrDsp.Synthesize(sbr, sbr.Data[1], 1, element.Channels[1].Output, downsampled);
		}
	}
}
