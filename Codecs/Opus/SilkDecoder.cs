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

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>Ports FFmpeg's scalar SILK frame entropy decode, excitation, LPC/LTP synthesis, and stereo unmixing.</summary>
	internal sealed class SilkDecoder
	{
		private const int History = 322;
		private const int MaximumLag = 290;
		private readonly int outputChannels;
		private readonly SilkFrame[] frames = { new SilkFrame(), new SilkFrame() };
		private readonly float[] previousStereoWeights = new float[2];
		private readonly float[] stereoWeights = new float[2];
		private readonly SilkSubframe[] subframe = { new SilkSubframe(), new SilkSubframe(), new SilkSubframe(), new SilkSubframe() };
		private readonly float[] lpcLeadin = new float[16];
		private readonly float[] lpcBody = new float[16];
		private readonly float[] residual = new float[MaximumLag + History];
		private readonly sbyte[] lsfIndex2 = new sbyte[16];
		private readonly short[] lsfResidual = new short[16];
		private readonly short[] normalizedLsf = new short[16];
		private readonly int[] lsp = new int[16];
		private readonly int[] polynomialP = new int[9];
		private readonly int[] polynomialQ = new int[9];
		private readonly int[] lpc32 = new int[32];
		private readonly int[] lpcStability32 = new int[32];
		private readonly short[] lpc16 = new short[16];
		private readonly byte[] pulseCount = new byte[20];
		private readonly byte[] lsbCount = new byte[20];
		private readonly int[] excitation = new int[320];
		private readonly int[] branch = new int[8];
		private int midOnly;
		private int subframes;
		private int subframeLength;
		private int frameLength;
		private int nlsfInterpolationFactor;
		private OpusBandwidth bandwidth;
		private bool wideband;
		private int previousCodedChannels;

		public SilkDecoder(int outputChannels)
		{
			if (outputChannels != 1 && outputChannels != 2) throw new ArgumentOutOfRangeException(nameof(outputChannels));
			this.outputChannels = outputChannels;
		}

		/// <summary>Decodes a complete 10–60 ms SILK superframe into its native-rate planar samples.</summary>
		public int DecodeSuperframe(OpusRangeDecoder range, float[][] output, int outputOffset, OpusBandwidth inputBandwidth, int codedChannels, int durationMilliseconds)
		{
			if (inputBandwidth > OpusBandwidth.Wideband || codedChannels < 1 || codedChannels > 2 || durationMilliseconds < 10 || durationMilliseconds > 60)
				return -22;
			var frameCount = 1 + (durationMilliseconds > 20 ? 1 : 0) + (durationMilliseconds > 40 ? 1 : 0);
			subframes = durationMilliseconds / frameCount / 5;
			subframeLength = 20 * ((int)inputBandwidth + 2);
			frameLength = subframeLength * subframes;
			bandwidth = inputBandwidth;
			wideband = inputBandwidth == OpusBandwidth.Wideband;
			if (codedChannels > previousCodedChannels) FlushFrame(frames[1]);
			previousCodedChannels = codedChannels;
			Span<int> active = stackalloc int[12];
			Span<int> redundancy = stackalloc int[2];
			for (var channel = 0; channel < codedChannels; channel++)
			{
				for (var frame = 0; frame < frameCount; frame++) active[channel * 6 + frame] = (int)range.DecodeLog(1);
				redundancy[channel] = (int)range.DecodeLog(1);
			}
			for (var channel = 0; channel < codedChannels; channel++) if (redundancy[channel] != 0 && durationMilliseconds > 20)
				redundancy[channel] = (int)range.DecodeCdf(durationMilliseconds == 40 ? OpusTables.SilkModelLbrrFlags40 : OpusTables.SilkModelLbrrFlags60);
			for (var frame = 0; frame < frameCount; frame++)
			{
				for (var channel = 0; channel < codedChannels; channel++) if ((redundancy[channel] & 1 << frame) != 0)
				{
					var activeSide = channel == 0 && (redundancy[1] & 1 << frame) == 0 ? 0 : 1;
					DecodeFrame(range, frame, channel, codedChannels, 1, activeSide, true);
				}
				midOnly = 0;
			}
			for (var frame = 0; frame < frameCount; frame++)
			{
				for (var channel = 0; channel < codedChannels && midOnly == 0; channel++)
					DecodeFrame(range, frame, channel, codedChannels, active[channel * 6 + frame], codedChannels > 1 ? active[6 + frame] : 0, false);
				if (midOnly != 0 && frames[1].Coded) FlushFrame(frames[1]);
				if (codedChannels == 1 || outputChannels == 1)
				{
					for (var channel = 0; channel < outputChannels; channel++) Array.Copy(frames[0].Output, History - frameLength - 2, output[channel], outputOffset + frame * frameLength, frameLength);
				} else UnmixStereo(output[0], output[1], outputOffset + frame * frameLength);
				midOnly = 0;
			}
			return frameCount * frameLength;
		}

		public void Flush()
		{
			FlushFrame(frames[0]); FlushFrame(frames[1]);
			Array.Clear(previousStereoWeights, 0, previousStereoWeights.Length);
		}

		/// <summary>
		/// Decodes one SILK channel frame from activity flags through gains, LPC/LTP state, excitation, and synthesis.
		/// </summary>
		private void DecodeFrame(OpusRangeDecoder range, int frameNumber, int channel, int codedChannels, int active, int activeSide, bool redundant)
		{
			var frame = frames[channel];
			if (codedChannels == 2 && channel == 0)
			{
				var n = (int)range.DecodeCdf(OpusTables.SilkModelStereoS1);
				var wi0 = (int)range.DecodeCdf(OpusTables.SilkModelStereoS2) + 3 * (n / 5); var ws0 = (int)range.DecodeCdf(OpusTables.SilkModelStereoS3);
				var wi1 = (int)range.DecodeCdf(OpusTables.SilkModelStereoS2) + 3 * (n % 5); var ws1 = (int)range.DecodeCdf(OpusTables.SilkModelStereoS3);
				var w0 = OpusTables.SilkStereoWeights[wi0] + (((OpusTables.SilkStereoWeights[wi0 + 1] - OpusTables.SilkStereoWeights[wi0]) * 6554) >> 16) * (ws0 * 2 + 1);
				var w1 = OpusTables.SilkStereoWeights[wi1] + (((OpusTables.SilkStereoWeights[wi1 + 1] - OpusTables.SilkStereoWeights[wi1]) * 6554) >> 16) * (ws1 * 2 + 1);
				stereoWeights[0] = (float)((w0 - w1) / 8192.0); stereoWeights[1] = (float)(w1 / 8192.0);
				midOnly = activeSide != 0 ? 0 : (int)range.DecodeCdf(OpusTables.SilkModelMidOnly);
			}
			int voiced; int highQuantizationOffset;
			if (active == 0) { highQuantizationOffset = (int)range.DecodeCdf(OpusTables.SilkModelFrameTypeInactive); voiced = 0; }
			else { var type = (int)range.DecodeCdf(OpusTables.SilkModelFrameTypeActive); highQuantizationOffset = type & 1; voiced = type >> 1; }
			for (var index = 0; index < subframes; index++)
			{
				int logGain;
				if (index == 0 && (frameNumber == 0 || !frame.Coded))
				{
					var high = (int)range.DecodeCdf(OpusTables.SilkModelGainHighbits, (active + voiced) * 9);
					logGain = high << 3 | (int)range.DecodeCdf(OpusTables.SilkModelGainLowbits);
					if (frame.Coded) logGain = Math.Max(logGain, frame.LogGain - 16);
				} else
				{
					var delta = (int)range.DecodeCdf(OpusTables.SilkModelGainDelta);
					logGain = ClipUnsigned(Math.Max((delta << 1) - 16, frame.LogGain + delta - 4), 6);
				}
				frame.LogGain = logGain;
				logGain = (int)(((long)logGain * 0x1D1C71 >> 16) + 2090);
				var integer = logGain >> 7; var fraction = logGain & 127;
				var linear = (1 << integer) + ((-174 * fraction * (128 - fraction) >> 16) + fraction) * ((1 << integer) >> 7);
				subframe[index].Gain = linear / 65536.0f;
			}
			DecodeLpc(frame, range, out var order, out var hasLpcLeadin, voiced);
			if (voiced != 0) DecodePitch(frame, range, frameNumber);
			var ltpScale = voiced != 0 && frameNumber == 0 ? OpusTables.SilkLtpScaleFactor[range.DecodeCdf(OpusTables.SilkModelLtpScaleIndex)] / 16384.0f : 15565.0f / 16384.0f;
			DecodeExcitation(range, highQuantizationOffset, active, voiced);
			if (outputChannels == channel || redundant) return;
			for (var index = 0; index < subframes; index++)
			{
				var coefficients = index < 2 && hasLpcLeadin ? lpcLeadin : lpcBody;
				var destination = History + index * subframeLength;
				var residualBase = MaximumLag + index * subframeLength;
				var lpcBase = History + index * subframeLength;
				if (voiced != 0)
				{
					int outputEnd; float scale;
					if (index < 2 || nlsfInterpolationFactor == 4) { outputEnd = -index * subframeLength; scale = ltpScale; }
					else { outputEnd = -(index - 2) * subframeLength; scale = 1.0f; }
					for (var sample = -subframe[index].PitchLag - 2; sample < outputEnd; sample++)
					{
						var sum = frame.Output[destination + sample];
						for (var coefficient = 0; coefficient < order; coefficient++) sum -= coefficients[coefficient] * frame.Output[destination + sample - coefficient - 1];
						residual[residualBase + sample] = Math.Clamp(sum, -1.0f, 1.0f) * scale / subframe[index].Gain;
					}
					if (outputEnd != 0) { var rescale = subframe[index - 1].Gain / subframe[index].Gain; for (var sample = outputEnd; sample < 0; sample++) residual[residualBase + sample] *= rescale; }
					for (var sample = 0; sample < subframeLength; sample++)
					{
						var sum = residual[residualBase + sample];
						for (var tap = 0; tap < 5; tap++) sum += subframe[index].LtpTaps[tap] * residual[residualBase + sample - subframe[index].PitchLag + 2 - tap];
						residual[residualBase + sample] = sum;
					}
				}
				for (var sample = 0; sample < subframeLength; sample++)
				{
					var sum = residual[residualBase + sample] * subframe[index].Gain;
					for (var coefficient = 1; coefficient <= order; coefficient++) sum += coefficients[coefficient - 1] * frame.LpcHistory[lpcBase + sample - coefficient];
					frame.LpcHistory[lpcBase + sample] = sum; frame.Output[destination + sample] = Math.Clamp(sum, -1.0f, 1.0f);
				}
			}
			frame.PreviousVoiced = voiced != 0;
			Array.Copy(frame.LpcHistory, frameLength, frame.LpcHistory, 0, History);
			Array.Copy(frame.Output, frameLength, frame.Output, 0, History);
			frame.Coded = true;
		}

		/// <summary>
		/// Decodes SILK normalized line spectral frequencies and converts both interpolation stages to LPC coefficients.
		/// </summary>
		private void DecodeLpc(SilkFrame frame, OpusRangeDecoder range, out int order, out bool hasLeadin, int voiced)
		{
			order = wideband ? 16 : 10;
			var firstIndex = (int)range.DecodeCdf(OpusTables.SilkModelLsfS1, ((wideband ? 1 : 0) * 2 + voiced) * 33);
			for (var index = 0; index < order; index++)
			{
				var model = wideband ? OpusTables.SilkLsfS2ModelSelWb[firstIndex * 16 + index] : OpusTables.SilkLsfS2ModelSelNbmb[firstIndex * 10 + index];
				lsfIndex2[index] = (sbyte)((int)range.DecodeCdf(OpusTables.SilkModelLsfS2, model * 10) - 4);
				if (lsfIndex2[index] == -4) lsfIndex2[index] -= (sbyte)range.DecodeCdf(OpusTables.SilkModelLsfS2Ext);
				else if (lsfIndex2[index] == 4) lsfIndex2[index] += (sbyte)range.DecodeCdf(OpusTables.SilkModelLsfS2Ext);
			}
			for (var index = order - 1; index >= 0; index--)
			{
				var step = wideband ? 9830 : 11796; var value = lsfIndex2[index] * 1024;
				if (lsfIndex2[index] < 0) value += 102; else if (lsfIndex2[index] > 0) value -= 102;
				value = value * step >> 16;
				if (index + 1 < order)
				{
					var selector = wideband ? OpusTables.SilkLsfWeightSelWb[firstIndex * 15 + index] : OpusTables.SilkLsfWeightSelNbmb[firstIndex * 9 + index];
					var weight = wideband ? OpusTables.SilkLsfPredWeightsWb[selector * 15 + index] : OpusTables.SilkLsfPredWeightsNbmb[selector * 9 + index];
					value += lsfResidual[index + 1] * weight >> 8;
				}
				lsfResidual[index] = (short)value;
			}
			for (var index = 0; index < order; index++)
			{
				var codebookOffset = firstIndex * order; var current = wideband ? OpusTables.SilkLsfCodebookWb[codebookOffset + index] : OpusTables.SilkLsfCodebookNbmb[codebookOffset + index];
				var previous = index != 0 ? (wideband ? OpusTables.SilkLsfCodebookWb[codebookOffset + index - 1] : OpusTables.SilkLsfCodebookNbmb[codebookOffset + index - 1]) : 0;
				var next = index + 1 < order ? (wideband ? OpusTables.SilkLsfCodebookWb[codebookOffset + index + 1] : OpusTables.SilkLsfCodebookNbmb[codebookOffset + index + 1]) : 256;
				var weightSquare = (1024 / (current - previous) + 1024 / (next - current)) << 16;
				var integer = IntegerLog(weightSquare); var fraction = weightSquare >> (integer - 8) & 127; var y = (integer & 1) != 0 ? 32768 : 46214; y >>= (32 - integer) >> 1;
				var weight = y + (213 * fraction * y >> 16);
				normalizedLsf[index] = (short)ClipUnsigned(current * 128 + lsfResidual[index] * 16384 / weight, 15);
			}
			StabilizeLsf(normalizedLsf, order, wideband ? OpusTables.SilkLsfMinSpacingWb : OpusTables.SilkLsfMinSpacingNbmb);
			hasLeadin = false;
			if (subframes == 4)
			{
				var interpolation = (int)range.DecodeCdf(OpusTables.SilkModelLsfInterpolationOffset);
				if (interpolation != 4 && frame.Coded)
				{
					hasLeadin = true;
					if (interpolation != 0) { for (var index = 0; index < order; index++) lpc16[index] = (short)(frame.NormalizedLsf[index] + ((normalizedLsf[index] - frame.NormalizedLsf[index]) * interpolation >> 2)); LsfToLpc(lpc16, lpcLeadin, order); }
					else Array.Copy(frame.Lpc, lpcLeadin, 16);
				} else interpolation = 4;
				nlsfInterpolationFactor = interpolation; LsfToLpc(normalizedLsf, lpcBody, order);
			} else { nlsfInterpolationFactor = 4; LsfToLpc(normalizedLsf, lpcBody, order); }
			Array.Copy(normalizedLsf, frame.NormalizedLsf, order); Array.Copy(lpcBody, frame.Lpc, order);
		}

		private void DecodePitch(SilkFrame frame, OpusRangeDecoder range, int frameNumber)
		{
			var absolute = frameNumber == 0 || !frame.PreviousVoiced; int primaryLag = 0;
			if (!absolute) { var delta = (int)range.DecodeCdf(OpusTables.SilkModelPitchDelta); if (delta != 0) primaryLag = frame.PrimaryLag + delta - 9; else absolute = true; }
			if (absolute)
			{
				var high = (int)range.DecodeCdf(OpusTables.SilkModelPitchHighbits); int low;
				if (bandwidth == OpusBandwidth.Narrowband) low = (int)range.DecodeCdf(OpusTables.SilkModelLcgSeed);
				else if (bandwidth == OpusBandwidth.Mediumband) low = (int)range.DecodeCdf(OpusTables.SilkModelPitchLowbitsMb);
				else low = (int)range.DecodeCdf(OpusTables.SilkModelGainLowbits);
				primaryLag = OpusTables.SilkPitchMinLag[(int)bandwidth] + high * OpusTables.SilkPitchScale[(int)bandwidth] + low;
			}
			frame.PrimaryLag = primaryLag;
			var contour = subframes == 2
				? (bandwidth == OpusBandwidth.Narrowband ? (int)range.DecodeCdf(OpusTables.SilkModelPitchContourNb10ms) : (int)range.DecodeCdf(OpusTables.SilkModelPitchContourMbwb10ms))
				: (bandwidth == OpusBandwidth.Narrowband ? (int)range.DecodeCdf(OpusTables.SilkModelPitchContourNb20ms) : (int)range.DecodeCdf(OpusTables.SilkModelPitchContourMbwb20ms));
			var contourWidth = subframes == 2 ? 2 : 4;
			var offsets = bandwidth == OpusBandwidth.Narrowband ? (subframes == 2 ? OpusTables.SilkPitchOffsetNb10ms : OpusTables.SilkPitchOffsetNb20ms) : (subframes == 2 ? OpusTables.SilkPitchOffsetMbwb10ms : OpusTables.SilkPitchOffsetMbwb20ms);
			for (var index = 0; index < subframes; index++) subframe[index].PitchLag = Math.Clamp(primaryLag + offsets[contour * contourWidth + index], OpusTables.SilkPitchMinLag[(int)bandwidth], OpusTables.SilkPitchMaxLag[(int)bandwidth]);
			var filter = (int)range.DecodeCdf(OpusTables.SilkModelLtpFilter); var selectors = filter == 0 ? OpusTables.SilkModelLtpFilter0Sel : filter == 1 ? OpusTables.SilkModelLtpFilter1Sel : OpusTables.SilkModelLtpFilter2Sel; var taps = filter == 0 ? OpusTables.SilkLtpFilter0Taps : filter == 1 ? OpusTables.SilkLtpFilter1Taps : OpusTables.SilkLtpFilter2Taps;
			for (var index = 0; index < subframes; index++) { var tapIndex = (int)range.DecodeCdf(selectors); for (var tap = 0; tap < 5; tap++) subframe[index].LtpTaps[tap] = taps[tapIndex * 5 + tap] / 128.0f; }
		}

		private void DecodeExcitation(OpusRangeDecoder range, int highQuantizationOffset, int active, int voiced)
		{
			var seed = range.DecodeCdf(OpusTables.SilkModelLcgSeed); var shellBlocks = OpusTables.SilkShellBlocks[(int)bandwidth * 2 + (subframes >> 2)]; var rate = (int)range.DecodeCdf(OpusTables.SilkModelExcRate, voiced * 10);
			Array.Clear(lsbCount, 0, lsbCount.Length);
			for (var block = 0; block < shellBlocks; block++)
			{
				pulseCount[block] = (byte)range.DecodeCdf(OpusTables.SilkModelPulseCount, rate * 19);
				if (pulseCount[block] == 17) { while (pulseCount[block] == 17 && ++lsbCount[block] != 10) pulseCount[block] = (byte)range.DecodeCdf(OpusTables.SilkModelPulseCount, 9 * 19); if (lsbCount[block] == 10) pulseCount[block] = (byte)range.DecodeCdf(OpusTables.SilkModelPulseCount, 10 * 19); }
			}
			for (var block = 0; block < shellBlocks; block++)
			{
				var location = block * 16;
				if (pulseCount[block] == 0) { Array.Clear(excitation, location, 16); continue; }
				branch[0] = pulseCount[block];
				CountChildren(range, 0, branch[0], branch, 2);
				for (var first = 0; first < 2; first++)
				{
					CountChildren(range, 1, branch[2 + first], branch, 4);
					for (var second = 0; second < 2; second++)
					{
						CountChildren(range, 2, branch[4 + second], branch, 6);
						for (var third = 0; third < 2; third++) { CountChildren(range, 3, branch[6 + third], excitation, location); location += 2; }
					}
				}
			}
			for (var index = 0; index < shellBlocks << 4; index++) for (var bit = 0; bit < lsbCount[index >> 4]; bit++) excitation[index] = excitation[index] << 1 | (int)range.DecodeCdf(OpusTables.SilkModelExcitationLsb);
			for (var index = 0; index < shellBlocks << 4; index++) if (excitation[index] != 0)
			{
				var modelOffset = (((active + voiced) * 2 + highQuantizationOffset) * 7 + Math.Min(pulseCount[index >> 4], (byte)6)) * 3;
				if (range.DecodeCdf(OpusTables.SilkModelExcitationSign, modelOffset) == 0) excitation[index] *= -1;
			}
			for (var index = 0; index < shellBlocks << 4; index++)
			{
				var value = excitation[index]; excitation[index] = value * 256 | OpusTables.SilkQuantOffset[voiced * 2 + highQuantizationOffset];
				if (value < 0) excitation[index] += 20; else if (value > 0) excitation[index] -= 20;
				seed = unchecked(196314165 * seed + 907633515); if ((seed & 0x80000000) != 0) excitation[index] *= -1; seed = unchecked(seed + (uint)value);
				residual[MaximumLag + index] = excitation[index] / 8388608.0f;
			}
		}

		private static void CountChildren(OpusRangeDecoder range, int model, int total, int[] destination, int offset)
		{
			if (total != 0) { destination[offset] = (int)range.DecodeCdf(OpusTables.SilkModelPulseLocation, model * 168 + ((total + 4) * (total - 1) >> 1)); destination[offset + 1] = total - destination[offset]; }
			else destination[offset] = destination[offset + 1] = 0;
		}

		private void UnmixStereo(float[] left, float[] right, int outputOffset)
		{
			var mid = frames[0].Output; var side = frames[1].Output; var source = History - frameLength; var previous0 = previousStereoWeights[0]; var previous1 = previousStereoWeights[1]; var weight0 = stereoWeights[0]; var weight1 = stereoWeights[1]; var interpolationLength = OpusTables.SilkStereoInterpLen[(int)bandwidth];
			var index = 0;
			for (; index < interpolationLength; index++)
			{
				var interpolation0 = previous0 + index * (weight0 - previous0) / interpolationLength; var interpolation1 = previous1 + index * (weight1 - previous1) / interpolationLength; var p0 = (float)(0.25 * (mid[source + index - 2] + 2 * mid[source + index - 1] + mid[source + index]));
				left[outputOffset + index] = Math.Clamp((1 + interpolation1) * mid[source + index - 1] + side[source + index - 1] + interpolation0 * p0, -1.0f, 1.0f); right[outputOffset + index] = Math.Clamp((1 - interpolation1) * mid[source + index - 1] - side[source + index - 1] - interpolation0 * p0, -1.0f, 1.0f);
			}
			for (; index < frameLength; index++)
			{
				var p0 = (float)(0.25 * (mid[source + index - 2] + 2 * mid[source + index - 1] + mid[source + index]));
				left[outputOffset + index] = Math.Clamp((1 + weight1) * mid[source + index - 1] + side[source + index - 1] + weight0 * p0, -1.0f, 1.0f); right[outputOffset + index] = Math.Clamp((1 - weight1) * mid[source + index - 1] - side[source + index - 1] - weight0 * p0, -1.0f, 1.0f);
			}
			Array.Copy(stereoWeights, previousStereoWeights, 2);
		}

		private void LsfToLpc(short[] nlsf, float[] output, int order)
		{
			for (var index = 0; index < order; index++)
			{
				var tableIndex = nlsf[index] >> 8; var offset = nlsf[index] & 255; var destination = order == 10 ? OpusTables.SilkLsfOrderingNbmb[index] : OpusTables.SilkLsfOrderingWb[index];
				lsp[destination] = OpusTables.SilkCosine[tableIndex] * 256 + (OpusTables.SilkCosine[tableIndex + 1] - OpusTables.SilkCosine[tableIndex]) * offset; lsp[destination] = (lsp[destination] + 4) >> 3;
			}
			LspToPolynomial(lsp, 0, polynomialP, order >> 1); LspToPolynomial(lsp, 1, polynomialQ, order >> 1);
			for (var index = 0; index < order >> 1; index++) { var p = polynomialP[index + 1] + polynomialP[index]; var q = polynomialQ[index + 1] - polynomialQ[index]; lpc32[index] = -q - p; lpc32[order - index - 1] = q - p; }
			var iteration = 0;
			for (; iteration < 10; iteration++)
			{
				uint maximum = 0; var maximumIndex = 0;
				for (var index = 0; index < order; index++) { var value = (uint)Math.Abs((long)lpc32[index]); if (value > maximum) { maximum = value; maximumIndex = index; } }
				maximum = (maximum + 16) >> 5;
				if (maximum <= 32767) break;
				maximum = Math.Min(maximum, 163838); var chirpBase = (uint)(65470 - ((maximum - 32767) << 14) / ((maximum * (uint)(maximumIndex + 1)) >> 2)); var chirp = chirpBase;
				for (var index = 0; index < order; index++) { lpc32[index] = (int)RoundMultiply(lpc32[index], chirp, 16); chirp = (chirpBase * chirp + 32768) >> 16; }
			}
			if (iteration == 10) for (var index = 0; index < order; index++) { var value = (lpc32[index] + 16) >> 5; lpc16[index] = (short)Math.Clamp(value, short.MinValue, short.MaxValue); lpc32[index] = lpc16[index] << 5; }
			else for (var index = 0; index < order; index++) lpc16[index] = (short)((lpc32[index] + 16) >> 5);
			for (var iteration2 = 1; iteration2 <= 16 && !IsLpcStable(lpc16, order); iteration2++)
			{
				var chirpBase = (uint)(65536 - (1 << iteration2)); var chirp = chirpBase;
				for (var index = 0; index < order; index++) { lpc32[index] = (int)RoundMultiply(lpc32[index], chirp, 16); lpc16[index] = (short)((lpc32[index] + 16) >> 5); chirp = (chirpBase * chirp + 32768) >> 16; }
			}
			for (var index = 0; index < order; index++) output[index] = lpc16[index] / 4096.0f;
		}

		private static void LspToPolynomial(int[] input, int inputOffset, int[] polynomial, int halfOrder)
		{
			polynomial[0] = 65536; polynomial[1] = -input[inputOffset];
			for (var index = 1; index < halfOrder; index++) { polynomial[index + 1] = polynomial[index - 1] * 2 - (int)RoundMultiply(input[inputOffset + 2 * index], polynomial[index], 16); for (var inner = index; inner > 1; inner--) polynomial[inner] += polynomial[inner - 2] - (int)RoundMultiply(input[inputOffset + 2 * index], polynomial[inner - 1], 16); polynomial[1] -= input[inputOffset + 2 * index]; }
		}

		private bool IsLpcStable(short[] coefficients, int order)
		{
			var dcResponse = 0; var totalInverseGain = 1 << 30; var rowOffset = 0;
			for (var index = 0; index < order; index++) { dcResponse += coefficients[index]; lpcStability32[index] = coefficients[index] * 4096; }
			if (dcResponse >= 4096) return false;
			for (var k = order - 1; ; k--)
			{
				if (Math.Abs((long)lpcStability32[rowOffset + k]) > 16773022) return false;
				var reflection = unchecked(-lpcStability32[rowOffset + k] * 128); var gainDivisor = (1 << 30) - MultiplyHigh(reflection, reflection); totalInverseGain = MultiplyHigh(totalInverseGain, gainDivisor) << 2;
				if (k == 0) return totalInverseGain >= 107374;
				var fractionalBits = IntegerLog(gainDivisor); var gain = ((1 << 29) - 1) / (gainDivisor >> (fractionalBits + 1 - 16)); var error = (1 << 29) - (int)(((long)(gainDivisor << (15 + 16 - fractionalBits)) * gain) >> 16); gain = (gain << 16) + (error * gain >> 13);
				var previousOffset = rowOffset; rowOffset = (k & 1) * 16;
				for (var index = 0; index < k; index++) { var difference = SaturatingSubtract(lpcStability32[previousOffset + index], (int)RoundMultiply(lpcStability32[previousOffset + k - index - 1], reflection, 31)); var value = RoundMultiply(difference, gain, fractionalBits); if (value < int.MinValue || value > int.MaxValue) return false; lpcStability32[rowOffset + index] = (int)value; }
			}
		}

		private static void StabilizeLsf(short[] nlsf, int order, ushort[] minimumDelta)
		{
			for (var pass = 0; pass < 20; pass++)
			{
				var minimumDifference = 0; var selected = 0;
				for (var index = 0; index < order + 1; index++) { var low = index != 0 ? nlsf[index - 1] : 0; var high = index != order ? nlsf[index] : 32768; var difference = high - low - minimumDelta[index]; if (difference < minimumDifference) { minimumDifference = difference; selected = index; } }
				if (minimumDifference == 0) return;
				if (selected == 0) nlsf[0] = (short)minimumDelta[0];
				else if (selected == order) nlsf[order - 1] = (short)(32768 - minimumDelta[order]);
				else { var minimumCenter = 0; var maximumCenter = 32768; for (var index = 0; index < selected; index++) minimumCenter += minimumDelta[index]; minimumCenter += minimumDelta[selected] >> 1; for (var index = order; index > selected; index--) maximumCenter -= minimumDelta[index]; maximumCenter -= minimumDelta[selected] >> 1; var center = nlsf[selected - 1] + nlsf[selected]; center = (center >> 1) + (center & 1); center = Math.Min(maximumCenter, Math.Max(minimumCenter, center)); nlsf[selected - 1] = (short)(center - (minimumDelta[selected] >> 1)); nlsf[selected] = (short)(nlsf[selected - 1] + minimumDelta[selected]); }
			}
			for (var index = 1; index < order; index++) { var value = nlsf[index]; var target = index - 1; while (target >= 0 && nlsf[target] > value) { nlsf[target + 1] = nlsf[target]; target--; } nlsf[target + 1] = value; }
			if (nlsf[0] < minimumDelta[0]) nlsf[0] = (short)minimumDelta[0];
			for (var index = 1; index < order; index++) nlsf[index] = (short)Math.Max(nlsf[index], Math.Min(nlsf[index - 1] + minimumDelta[index], 32767));
			if (nlsf[order - 1] > 32768 - minimumDelta[order]) nlsf[order - 1] = (short)(32768 - minimumDelta[order]);
			for (var index = order - 2; index >= 0; index--) if (nlsf[index] > nlsf[index + 1] - minimumDelta[index + 1]) nlsf[index] = (short)(nlsf[index + 1] - minimumDelta[index + 1]);
		}

		private static void FlushFrame(SilkFrame frame)
		{
			if (!frame.Coded) return;
			Array.Clear(frame.Output, 0, frame.Output.Length); Array.Clear(frame.LpcHistory, 0, frame.LpcHistory.Length); Array.Clear(frame.Lpc, 0, frame.Lpc.Length); Array.Clear(frame.NormalizedLsf, 0, frame.NormalizedLsf.Length); frame.LogGain = frame.PrimaryLag = 0; frame.PreviousVoiced = false; frame.Coded = false;
		}

		private static int ClipUnsigned(int value, int bits) { var maximum = (1 << bits) - 1; return value < 0 ? 0 : value > maximum ? maximum : value; }
		private static int IntegerLog(int value) { var result = 0; while (value != 0) { value >>= 1; result++; } return result; }
		private static long RoundMultiply(long first, long second, int shift) => (((first * second) >> (shift - 1)) + 1) >> 1;
		private static int MultiplyHigh(int first, int second) => (int)(((long)first * second) >> 32);
		private static int SaturatingSubtract(int first, int second) { var result = (long)first - second; return result < int.MinValue ? int.MinValue : result > int.MaxValue ? int.MaxValue : (int)result; }
	}
}
