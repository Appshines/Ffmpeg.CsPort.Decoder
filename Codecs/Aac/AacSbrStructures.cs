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
using Ffmpeg.CsPort.Decoder.Transforms;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Stores the SBR frequency-grid parameters whose changes trigger table regeneration.</summary>
	internal sealed class AacSbrSpectrumParameters
	{
		public int StartFrequency = -1;
		public int StopFrequency = -1;
		public int CrossoverBand = -1;
		public int FrequencyScale = -1;
		public int AlterScale = -1;
		public int NoiseBands = -1;

		public bool EqualsValues(AacSbrSpectrumParameters other)
		{
			return StartFrequency == other.StartFrequency && StopFrequency == other.StopFrequency &&
				CrossoverBand == other.CrossoverBand && FrequencyScale == other.FrequencyScale &&
				AlterScale == other.AlterScale && NoiseBands == other.NoiseBands;
		}

		public void CopyFrom(AacSbrSpectrumParameters source)
		{
			StartFrequency = source.StartFrequency;
			StopFrequency = source.StopFrequency;
			CrossoverBand = source.CrossoverBand;
			FrequencyScale = source.FrequencyScale;
			AlterScale = source.AlterScale;
			NoiseBands = source.NoiseBands;
		}
	}

	/// <summary>Owns one SBR channel's persistent grid, envelope, filterbank, harmonic, and smoothing state.</summary>
	internal sealed class AacSbrData
	{
		public const int AnalysisBandStride = 2;
		public const int AnalysisSlotStride = 32 * AnalysisBandStride;
		public const int AnalysisPositionStride = 32 * AnalysisSlotStride;
		public const int AdjustedBandStride = 2;
		public const int AdjustedSlotStride = 64 * AdjustedBandStride;
		public const int AdjustedPositionStride = 38 * AdjustedSlotStride;

		public int FrameClass;
		public bool AddHarmonicFlag;
		public int NumberOfEnvelopes;
		public byte[] FrequencyResolution = new byte[9];
		public int NumberOfNoiseEnvelopes;
		public byte[] DeltaFrequencyEnvelope = new byte[9];
		public byte[] DeltaFrequencyNoise = new byte[2];
		public byte[,] InverseFilteringMode = new byte[2, 5];
		public byte[] AddHarmonic = new byte[48];
		public bool AmplitudeResolution;
		public float[] SynthesisFilterbankSamples = new float[2304];
		public float[] AnalysisFilterbankSamples = new float[1312];
		public int SynthesisFilterbankSamplesOffset = 1152;
		public int[] AttackEnvelope = { 0, -1 };
		public float[] Bandwidth = new float[5];
		public float[] Analysis = new float[2 * AnalysisPositionStride];
		public int AnalysisPosition;
		public float[] Adjusted = new float[2 * AdjustedPositionStride];
		public float[,] GainHistory = new float[42, 48];
		public float[,] NoiseHistory = new float[42, 48];
		public byte[,] SinusoidIndexMapped = new byte[9, 48];
		public byte[,] QuantizedEnvelope = new byte[9, 48];
		public float[,] Envelope = new float[9, 48];
		public byte[,] QuantizedNoise = new byte[3, 5];
		public float[,] Noise = new float[3, 5];
		public byte[] TimeEnvelope = new byte[9];
		public byte PreviousEnvelopeEnd;
		public byte[] TimeNoise = new byte[3];
		public int NoiseIndex;
		public int SineIndex;
	}

	/// <summary>Owns all scalar SBR parser, frequency-grid, QMF, high-frequency, and envelope workspaces for one AAC element.</summary>
	internal sealed class AacSpectralBandReplication
	{
		public const int LowTimeStride = 2;
		public const int LowBandStride = 40 * LowTimeStride;
		public const int HighTimeStride = 2;
		public const int HighBandStride = 40 * HighTimeStride;
		public const int OutputTimeStride = 64;
		public const int OutputComponentStride = 38 * OutputTimeStride;
		public const int OutputChannelStride = 2 * OutputComponentStride;
		public const int CorrelationSecondStride = 2;
		public const int CorrelationFirstStride = 2 * CorrelationSecondStride;

		public AacParametricStereo ParametricStereo = new AacParametricStereo();
		public int SampleRate;
		public bool Started;
		public bool ReadyForDequantization;
		public AacElementType ElementType;
		public bool Reset;
		public AacSbrSpectrumParameters Spectrum = new AacSbrSpectrumParameters();
		public bool HeaderAmplitudeResolution;
		public int LimiterBands;
		public int LimiterGains;
		public bool InterpolateFrequency;
		public bool SmoothingMode;
		public bool Coupling;
		public int[] K = new int[5];
		public int[] Crossover = new int[2];
		public int[] NumberOfSubbands = new int[2];
		public bool PreviousGridPushed;
		public int NumberOfMasterBands;
		public AacSbrData[] Data = { new AacSbrData(), new AacSbrData() };
		public int[] NumberOfBands = new int[2];
		public int NumberOfNoiseBands;
		public int NumberOfLimiterBands;
		public ushort[] MasterFrequencyTable = new ushort[49];
		public ushort[] LowFrequencyTable = new ushort[25];
		public ushort[] HighFrequencyTable = new ushort[49];
		public ushort[] NoiseFrequencyTable = new ushort[6];
		public ushort[] LimiterFrequencyTable = new ushort[30];
		public int NumberOfPatches;
		public byte[] PatchSubbandCount = new byte[6];
		public byte[] PatchStartSubband = new byte[6];
		public float[] Low = new float[32 * LowBandStride];
		public float[] High = new float[64 * HighBandStride];
		public float[] Output = new float[2 * OutputChannelStride];
		public float[,] Alpha0 = new float[64, 2];
		public float[,] Alpha1 = new float[64, 2];
		public float[,] OriginalEnvelopeMapped = new float[8, 48];
		public float[,] NoiseMapped = new float[8, 48];
		public byte[,] SinusoidMapped = new byte[8, 48];
		public float[,] CurrentEnvelope = new float[8, 48];
		public float[,] NoiseAmplitude = new float[8, 48];
		public float[,] SinusoidAmplitude = new float[8, 48];
		public float[,] Gain = new float[8, 48];
		public float[] QmfScratch = new float[320];
		public float[] MdctScratch = new float[128];
		public float[] MdctInput = new float[64];
		public float[] GainFilterScratch = new float[48];
		public float[] NoiseFilterScratch = new float[48];
		public float[] CorrelationScratch = new float[3 * CorrelationFirstStride];
		public short[] BandScratch0 = new short[49];
		public short[] BandScratch1 = new short[49];
		public readonly FfmpegFloatMdct SynthesisMdct = new FfmpegFloatMdct(64, true, 1.0f / (64 * 32768));
		public readonly FfmpegFloatMdct AnalysisMdct = new FfmpegFloatMdct(64, true, -2.0f * 32768);

		public AacSpectralBandReplication()
		{
			Crossover[1] = 32;
		}
	}
}
