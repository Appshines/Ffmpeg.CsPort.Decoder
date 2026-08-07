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
using System.Buffers.Binary;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Amr
{
	/// <summary>Decodes AMR-WB speech frames with the scalar FFmpeg 8.1.2 reference schedule.</summary>
	public sealed class AmrWideBandDecoder
	{
		private const int FilterOrder = 16;
		private const int HighBandFilterOrder = 20;
		private const int SubframeSize = 64;
		private const int OutputSubframeSize = 80;
		private const int OutputSamples = 320;
		private const int PitchDelayMinimum = 34;
		private const int PitchDelayMaximum = 231;
		private const int ExcitationOffset = PitchDelayMaximum + FilterOrder + 1;
		private const int UpsampleMemory = 24;
		private const int FirMemory = 30;

		private static readonly uint[] InitialRandomState =
		{
			0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u,
			0x9B52C78Cu, 0x769717A6u, 0x7210FE98u, 0x30E8499Cu, 0x405C216Au, 0xA361A851u, 0x48A945B2u, 0x9A7827B6u,
			0xFDD55C02u, 0x78E7339Du, 0x10216E36u, 0x481B4450u, 0x9A15AC5Bu, 0x4CC3CC3Eu, 0x21A67C60u, 0xB4761AECu,
			0x1963ED24u, 0x27308ADEu, 0x8E602C65u, 0xA21145B0u, 0xC1F43100u, 0xBDD0138Au, 0x9EBB9990u, 0x4FCC3B15u,
			0x76D67110u, 0x10CFED53u, 0xBF6C273Cu, 0x97AC729Au, 0x0E687B3Au, 0x7FC41F86u, 0x65B13DEEu, 0xA071E7AEu,
			0x9F4CA96Cu, 0x80321AFDu, 0x65774CEBu, 0x92C380E5u, 0xB2D27C43u, 0x215D9740u, 0x989F039Eu, 0x15FDF5CBu,
			0xC791C180u, 0x841B01F2u, 0xA8FD21EFu, 0xA1E4A8EBu, 0x64AB3959u, 0x9F9CDF19u, 0xE89A5244u, 0x428BA68Eu,
			0x40157736u, 0xAA10CFFEu, 0xD1EE016Fu, 0x745B571Bu, 0x594F9768u, 0xC7911A10u, 0xFB23F4CFu, 0x2DFA40ECu
		};

		private readonly ushort[] frameFields = new ushort[56];
		private readonly float[] currentIsf = new float[FilterOrder];
		private readonly float[] pastQuantizedIsf = new float[FilterOrder];
		private readonly float[] pastFinalIsf = new float[FilterOrder];
		private readonly double[] isp = new double[4 * FilterOrder];
		private readonly double[] pastSubframe4Isp = new double[FilterOrder];
		private readonly float[] lpc = new float[4 * FilterOrder];
		private readonly float[] excitationBuffer = new float[ExcitationOffset + SubframeSize + 1];
		private readonly float[] pitchVector = new float[SubframeSize];
		private readonly float[] fixedVector = new float[SubframeSize];
		private readonly float[] predictionError = new float[4];
		private readonly float[] pitchGain = new float[6];
		private readonly float[] fixedGain = new float[2];
		private readonly float[] samplesAz = new float[FilterOrder + SubframeSize];
		private readonly float[] samplesUp = new float[UpsampleMemory + SubframeSize];
		private readonly float[] samplesHighBand = new float[HighBandFilterOrder + OutputSubframeSize];
		private readonly float[] highPass31Memory = new float[2];
		private readonly float[] highPass400Memory = new float[2];
		private readonly float[] deemphasisMemory = new float[1];
		private readonly float[] bandPassMemory = new float[FirMemory];
		private readonly float[] lowPassMemory = new float[FirMemory];
		private readonly uint[] randomState = new uint[64];
		private readonly float[] outputSamples = new float[OutputSamples];
		private readonly float[] spareVector = new float[SubframeSize];
		private readonly float[] synthesisExcitation = new float[SubframeSize];
		private readonly float[] highBandExcitation = new float[OutputSubframeSize];
		private readonly float[] highBandSamples = new float[OutputSubframeSize];
		private readonly int[] pulsePositions = new int[24];
		private readonly float[] highBandLpc = new float[HighBandFilterOrder];
		private readonly float[] extrapolatedIsf = new float[HighBandFilterOrder];
		private readonly double[] extrapolatedIsp = new double[HighBandFilterOrder];
		private readonly float[] differenceIsf = new float[FilterOrder - 2];
		private readonly float[] correlationLag = new float[3];
		private readonly float[] firData = new float[OutputSubframeSize + FirMemory];
		private readonly double[] firstPolynomial = new double[FilterOrder / 2 + 1];
		private readonly double[] secondPolynomial = new double[FilterOrder / 2 + 1];
		private readonly double[] highBandFirstPolynomial = new double[HighBandFilterOrder / 2 + 1];
		private readonly double[] highBandSecondPolynomial = new double[HighBandFilterOrder / 2 + 1];

		private int currentMode;
		private int basePitchLag;
		private int pitchLagInteger;
		private float tiltCoefficient;
		private int previousImpulseFilter;
		private float previousTransitionGain;
		private int randomIndex;
		private bool firstFrame = true;

		public AmrWideBandDecoder()
		{
			Array.Copy(InitialRandomState, randomState, randomState.Length);
			for (var index = 0; index < FilterOrder; index++)
				pastFinalIsf[index] = AmrWideBandTables.IsfInit[index] * (1.0f / (1 << 15));
			for (var index = 0; index < predictionError.Length; index++) predictionError[index] = -14.0f;
		}

		public int Channels => 1;
		public int SampleRate => 16000;
		public int MaximumOutputBytes => OutputSamples * sizeof(float);

		/// <summary>Consumes one AMR-WB storage-format speech frame and writes 320 packed scalar float samples.</summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < 1 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			currentMode = packet[packetOffset] >> 3 & 15;
			var quality = (packet[packetOffset] & 4) == 4;
			var expectedSize = ((AmrWideBandTables.FrameBits[currentMode] + 7) >> 3) + 1;
			if (output.Length < MaximumOutputBytes) return FfmpegError.InvalidArgument;
			if (currentMode == 15 || !quality)
			{
				output.Slice(0, MaximumOutputBytes).Clear();
				frame = new AudioFrameInfo(OutputSamples, 1, AudioSampleFormat.FloatPlanar, 1, MaximumOutputBytes, MaximumOutputBytes);
				return expectedSize;
			}
			if (currentMode > 9 || packetLength < expectedSize) return FfmpegError.InvalidData;
			if (currentMode == 9) return FfmpegError.PatchWelcome;
			UnpackFrame(packet, packetOffset + 1, GetOrder(currentMode));
			if (currentMode == 0) DecodeIsf36Bit();
			else DecodeIsf46Bit();
			AddMeanAndPastIsf();
			AmrDsp.SetMinimumLsfDistance(currentIsf, 0, 128.0 / 32768.0, FilterOrder - 1);
			var stability = CalculateStability();
			currentIsf[FilterOrder - 1] = (float)(currentIsf[FilterOrder - 1] * 2.0);
			AmrDsp.LsfToLsp(isp, 3 * FilterOrder, currentIsf, 0, FilterOrder);
			if (firstFrame)
			{
				firstFrame = false;
				Array.Copy(isp, 3 * FilterOrder, pastSubframe4Isp, 0, FilterOrder);
			}
			InterpolateIsp();
			for (var subframe = 0; subframe < 4; subframe++)
				AmrDsp.WideBandLspToLpc(isp, subframe * FilterOrder, lpc, subframe * FilterOrder, firstPolynomial, secondPolynomial);
			for (var subframe = 0; subframe < 4; subframe++) DecodeSubframe(subframe, stability);
			Array.Copy(isp, 3 * FilterOrder, pastSubframe4Isp, 0, FilterOrder);
			Array.Copy(currentIsf, pastFinalIsf, FilterOrder);
			for (var index = 0; index < OutputSamples; index++)
				BinaryPrimitives.WriteInt32LittleEndian(output.Slice(index * sizeof(float), sizeof(float)), BitConverter.SingleToInt32Bits(outputSamples[index]));
			frame = new AudioFrameInfo(OutputSamples, 1, AudioSampleFormat.FloatPlanar, 1, MaximumOutputBytes, MaximumOutputBytes);
			return expectedSize;
		}

		private void UnpackFrame(byte[] packet, int packetOffset, ushort[] order)
		{
			Array.Clear(frameFields, 0, frameFields.Length);
			var orderIndex = 0;
			while (order[orderIndex] != 0)
			{
				var fieldSize = order[orderIndex++];
				var fieldOffset = order[orderIndex++];
				var field = 0;
				while (fieldSize-- != 0)
				{
					var bit = order[orderIndex++];
					field = field << 1 | packet[packetOffset + (bit >> 3)] >> (bit & 7) & 1;
				}
				frameFields[fieldOffset >> 1] = (ushort)field;
			}
		}

		private static ushort[] GetOrder(int mode)
		{
			return mode switch
			{
				0 => AmrWideBandTables.OrderMODE6k60,
				1 => AmrWideBandTables.OrderMODE8k85,
				2 => AmrWideBandTables.OrderMODE12k65,
				3 => AmrWideBandTables.OrderMODE14k25,
				4 => AmrWideBandTables.OrderMODE15k85,
				5 => AmrWideBandTables.OrderMODE18k25,
				6 => AmrWideBandTables.OrderMODE19k85,
				7 => AmrWideBandTables.OrderMODE23k05,
				_ => AmrWideBandTables.OrderMODE23k85
			};
		}

		private void DecodeIsf36Bit()
		{
			for (var index = 0; index < 9; index++) currentIsf[index] = AmrWideBandTables.Dico1Isf[frameFields[1] * 9 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 7; index++) currentIsf[index + 9] = AmrWideBandTables.Dico2Isf[frameFields[2] * 7 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 5; index++) currentIsf[index] += AmrWideBandTables.Dico21Isf36b[frameFields[3] * 5 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 4; index++) currentIsf[index + 5] += AmrWideBandTables.Dico22Isf36b[frameFields[4] * 4 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 7; index++) currentIsf[index + 9] += AmrWideBandTables.Dico23Isf36b[frameFields[5] * 7 + index] * (1.0f / (1 << 15));
		}

		private void DecodeIsf46Bit()
		{
			for (var index = 0; index < 9; index++) currentIsf[index] = AmrWideBandTables.Dico1Isf[frameFields[1] * 9 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 7; index++) currentIsf[index + 9] = AmrWideBandTables.Dico2Isf[frameFields[2] * 7 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 3; index++) currentIsf[index] += AmrWideBandTables.Dico21Isf[frameFields[3] * 3 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 3; index++) currentIsf[index + 3] += AmrWideBandTables.Dico22Isf[frameFields[4] * 3 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 3; index++) currentIsf[index + 6] += AmrWideBandTables.Dico23Isf[frameFields[5] * 3 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 3; index++) currentIsf[index + 9] += AmrWideBandTables.Dico24Isf[frameFields[6] * 3 + index] * (1.0f / (1 << 15));
			for (var index = 0; index < 4; index++) currentIsf[index + 12] += AmrWideBandTables.Dico25Isf[frameFields[7] * 4 + index] * (1.0f / (1 << 15));
		}

		private void AddMeanAndPastIsf()
		{
			for (var index = 0; index < FilterOrder; index++)
			{
				var temporary = currentIsf[index];
				currentIsf[index] += AmrWideBandTables.IsfMean[index] * (1.0f / (1 << 15));
				currentIsf[index] = (float)(currentIsf[index] + (1.0 / 3.0) * pastQuantizedIsf[index]);
				pastQuantizedIsf[index] = temporary;
			}
		}

		private float CalculateStability()
		{
			var accumulation = 0.0f;
			for (var index = 0; index < FilterOrder - 1; index++)
			{
				var difference = currentIsf[index] - pastFinalIsf[index];
				accumulation += difference * difference;
			}
			return (float)Math.Max(0.0, 1.25 - accumulation * 0.8 * 512);
		}

		private void InterpolateIsp()
		{
			for (var subframe = 0; subframe < 3; subframe++)
			{
				var coefficient = AmrWideBandTables.IsfpInter[subframe];
				for (var index = 0; index < FilterOrder; index++)
					isp[subframe * FilterOrder + index] = (1.0 - coefficient) * pastSubframe4Isp[index] +
						coefficient * isp[3 * FilterOrder + index];
			}
		}

		/// <summary>Runs the AMR-WB excitation, enhancement, upsampling, and high-band reconstruction stages.</summary>
		private void DecodeSubframe(int subframe, float stability)
		{
			var subframeOffset = 8 + 12 * subframe;
			DecodePitchVector(subframeOffset, subframe);
			DecodeFixedVector(subframeOffset);
			PitchSharpen();
			DecodeGains(subframeOffset, out var fixedGainFactor);
			fixedGain[0] = AmrDsp.SetAmrFixedGain(fixedGainFactor,
				AmrDsp.ScalarProduct(fixedVector, 0, fixedVector, 0, SubframeSize) / SubframeSize,
				predictionError, 30.0f, AmrWideBandTables.EnergyPredFac);
			var voiceFactor = CalculateVoiceFactor();
			tiltCoefficient = (float)(voiceFactor * 0.25 + 0.25);
			for (var index = 0; index < SubframeSize; index++)
			{
				excitationBuffer[ExcitationOffset + index] *= pitchGain[0];
				excitationBuffer[ExcitationOffset + index] += fixedGain[0] * fixedVector[index];
				excitationBuffer[ExcitationOffset + index] = MathF.Truncate(excitationBuffer[ExcitationOffset + index]);
			}
			var synthesisFixedGain = EnhanceNoise(fixedGain[0], voiceFactor, stability);
			var synthesisFixedVector = ApplyAntiSparseness();
			EnhancePitch(synthesisFixedVector, voiceFactor);
			Synthesize(subframe, synthesisFixedGain, synthesisFixedVector);
			Deemphasize();
			AmrDsp.ApplySecondOrderTransfer(samplesUp, UpsampleMemory, samplesUp, UpsampleMemory,
				AmrWideBandTables.HpfZeros, AmrWideBandTables.Hpf31Poles, 0.989501953f, highPass31Memory, SubframeSize);
			Upsample(outputSamples, subframe * OutputSubframeSize);
			AmrDsp.ApplySecondOrderTransfer(highBandSamples, 0, samplesUp, UpsampleMemory,
				AmrWideBandTables.HpfZeros, AmrWideBandTables.Hpf400Poles, 0.893554687f, highPass400Memory, SubframeSize);
			var highBandGain = FindHighBandGain(subframeOffset);
			ScaleHighBandExcitation(highBandGain);
			SynthesizeHighBand(subframe);
			HighBandFir(highBandSamples, AmrWideBandTables.Bpf67Coef, bandPassMemory, samplesHighBand, HighBandFilterOrder);
			if (currentMode == 8) HighBandFir(highBandSamples, AmrWideBandTables.Lpf7Coef, lowPassMemory, highBandSamples, 0);
			var outputOffset = subframe * OutputSubframeSize;
			for (var index = 0; index < OutputSubframeSize; index++)
				outputSamples[outputOffset + index] = (outputSamples[outputOffset + index] + highBandSamples[index]) * (1.0f / (1 << 15));
			UpdateSubframeState();
		}

		/// <summary>
		/// Interpolates the AMR-WB adaptive-codebook excitation and applies pitch sharpening for one subframe.
		/// </summary>
		private void DecodePitchVector(int subframeOffset, int subframe)
		{
			var pitchIndex = frameFields[subframeOffset];
			int lagFraction;
			if (currentMode <= 1)
			{
				if (subframe == 0 || subframe == 2 && currentMode != 0)
				{
					if (pitchIndex < 116)
					{
						pitchLagInteger = (pitchIndex + 69) >> 1;
						lagFraction = (pitchIndex - (pitchLagInteger << 1) + 68) * 2;
					} else { pitchLagInteger = pitchIndex - 24; lagFraction = 0; }
					basePitchLag = Math.Clamp(pitchLagInteger - 8 - (lagFraction < 0 ? 1 : 0), PitchDelayMinimum, PitchDelayMaximum - 15);
				} else
				{
					pitchLagInteger = (pitchIndex + 1) >> 1;
					lagFraction = (pitchIndex - (pitchLagInteger << 1)) * 2;
					pitchLagInteger += basePitchLag;
				}
			} else
			{
				if (subframe == 0 || subframe == 2)
				{
					if (pitchIndex < 376)
					{
						pitchLagInteger = (pitchIndex + 137) >> 2;
						lagFraction = pitchIndex - (pitchLagInteger << 2) + 136;
					} else if (pitchIndex < 440)
					{
						pitchLagInteger = (pitchIndex + 257 - 376) >> 1;
						lagFraction = (pitchIndex - (pitchLagInteger << 1) + 256 - 376) * 2;
					} else { pitchLagInteger = pitchIndex - 280; lagFraction = 0; }
					basePitchLag = Math.Clamp(pitchLagInteger - 8 - (lagFraction < 0 ? 1 : 0), PitchDelayMinimum, PitchDelayMaximum - 15);
				} else
				{
					pitchLagInteger = (pitchIndex + 1) >> 2;
					lagFraction = pitchIndex - (pitchLagInteger << 2);
					pitchLagInteger += basePitchLag;
				}
			}
			var interpolationLag = pitchLagInteger + (lagFraction > 0 ? 1 : 0);
			AmrDsp.Interpolate(excitationBuffer, ExcitationOffset, excitationBuffer, ExcitationOffset + 1 - interpolationLag,
				AmrWideBandTables.AcInter, 4, lagFraction + (lagFraction > 0 ? 0 : 4), FilterOrder, SubframeSize + 1);
			if (frameFields[subframeOffset + 1] != 0) Array.Copy(excitationBuffer, ExcitationOffset, pitchVector, 0, SubframeSize);
			else
			{
				for (var index = 0; index < SubframeSize; index++) pitchVector[index] = (float)(0.18 * excitationBuffer[ExcitationOffset + index - 1] +
					0.64 * excitationBuffer[ExcitationOffset + index] + 0.18 * excitationBuffer[ExcitationOffset + index + 1]);
				Array.Copy(pitchVector, 0, excitationBuffer, ExcitationOffset, SubframeSize);
			}
		}

		/// <summary>
		/// Places signed AMR-WB algebraic-codebook pulses and filters them through the fixed-codebook response.
		/// </summary>
		private void DecodeFixedVector(int subframeOffset)
		{
			var highOffset = subframeOffset + 4;
			var lowOffset = subframeOffset + 8;
			switch (currentMode)
			{
				case 0:
					for (var track = 0; track < 2; track++) DecodeOnePulse(track * 6, frameFields[lowOffset + track], 5, 1);
					break;
				case 1:
					for (var track = 0; track < 4; track++) DecodeOnePulse(track * 6, frameFields[lowOffset + track], 4, 1);
					break;
				case 2:
					for (var track = 0; track < 4; track++) DecodeTwoPulses(track * 6, frameFields[lowOffset + track], 4, 1);
					break;
				case 3:
					for (var track = 0; track < 2; track++) DecodeThreePulses(track * 6, frameFields[lowOffset + track], 4, 1);
					for (var track = 2; track < 4; track++) DecodeTwoPulses(track * 6, frameFields[lowOffset + track], 4, 1);
					break;
				case 4:
					for (var track = 0; track < 4; track++) DecodeThreePulses(track * 6, frameFields[lowOffset + track], 4, 1);
					break;
				case 5:
					for (var track = 0; track < 4; track++) DecodeFourPulses(track * 6, frameFields[lowOffset + track] + (frameFields[highOffset + track] << 14), 4, 1);
					break;
				case 6:
					for (var track = 0; track < 2; track++) DecodeFivePulses(track * 6, frameFields[lowOffset + track] + (frameFields[highOffset + track] << 10), 4, 1);
					for (var track = 2; track < 4; track++) DecodeFourPulses(track * 6, frameFields[lowOffset + track] + (frameFields[highOffset + track] << 14), 4, 1);
					break;
				default:
					for (var track = 0; track < 4; track++) DecodeSixPulses(track * 6, frameFields[lowOffset + track] + (frameFields[highOffset + track] << 11), 4, 1);
					break;
			}
			Array.Clear(fixedVector, 0, fixedVector.Length);
			var spacing = currentMode == 0 ? 2 : 4;
			for (var track = 0; track < 4; track++)
				for (var pulse = 0; pulse < AmrWideBandTables.PulsesPerModeTrack[currentMode * 4 + track]; pulse++)
				{
					var signedPosition = pulsePositions[track * 6 + pulse];
					var position = (Math.Abs(signedPosition) - 1) * spacing + track;
					fixedVector[position] += signedPosition < 0 ? -1.0f : 1.0f;
				}
		}

		private static int BitString(int value, int leastSignificantBit, int length) => value >> leastSignificantBit & ((1 << length) - 1);
		private static int BitPosition(int value, int position) => value >> position & 1;

		private void DecodeOnePulse(int output, int code, int bits, int offset)
		{
			var position = BitString(code, 0, bits) + offset;
			pulsePositions[output] = BitPosition(code, bits) != 0 ? -position : position;
		}

		private void DecodeTwoPulses(int output, int code, int bits, int offset)
		{
			var position0 = BitString(code, bits, bits) + offset;
			var position1 = BitString(code, 0, bits) + offset;
			pulsePositions[output] = BitPosition(code, 2 * bits) != 0 ? -position0 : position0;
			pulsePositions[output + 1] = BitPosition(code, 2 * bits) != 0 ? -position1 : position1;
			if (position0 > position1) pulsePositions[output + 1] = -pulsePositions[output + 1];
		}

		private void DecodeThreePulses(int output, int code, int bits, int offset)
		{
			var half = BitPosition(code, 2 * bits - 1) << (bits - 1);
			DecodeTwoPulses(output, BitString(code, 0, 2 * bits - 1), bits - 1, offset + half);
			DecodeOnePulse(output + 2, BitString(code, 2 * bits, bits + 1), bits, offset);
		}

		private void DecodeFourPulses(int output, int code, int bits, int offset)
		{
			var halfOffset = 1 << (bits - 1);
			switch (BitString(code, 4 * bits - 2, 2))
			{
				case 0:
					var half = BitPosition(code, 4 * bits - 3) << (bits - 1);
					var subhalf = BitPosition(code, 2 * bits - 3) << (bits - 2);
					DecodeTwoPulses(output, BitString(code, 0, 2 * bits - 3), bits - 2, offset + half + subhalf);
					DecodeTwoPulses(output + 2, BitString(code, 2 * bits - 2, 2 * bits - 1), bits - 1, offset + half);
					break;
				case 1:
					DecodeOnePulse(output, BitString(code, 3 * bits - 2, bits), bits - 1, offset);
					DecodeThreePulses(output + 1, BitString(code, 0, 3 * bits - 2), bits - 1, offset + halfOffset);
					break;
				case 2:
					DecodeTwoPulses(output, BitString(code, 2 * bits - 1, 2 * bits - 1), bits - 1, offset);
					DecodeTwoPulses(output + 2, BitString(code, 0, 2 * bits - 1), bits - 1, offset + halfOffset);
					break;
				default:
					DecodeThreePulses(output, BitString(code, bits, 3 * bits - 2), bits - 1, offset);
					DecodeOnePulse(output + 3, BitString(code, 0, bits), bits - 1, offset + halfOffset);
					break;
			}
		}

		private void DecodeFivePulses(int output, int code, int bits, int offset)
		{
			var half = BitPosition(code, 5 * bits - 1) << (bits - 1);
			DecodeThreePulses(output, BitString(code, 2 * bits + 1, 3 * bits - 2), bits - 1, offset + half);
			DecodeTwoPulses(output + 3, BitString(code, 0, 2 * bits + 1), bits, offset);
		}

		private void DecodeSixPulses(int output, int code, int bits, int offset)
		{
			var halfOffset = 1 << (bits - 1);
			var more = BitPosition(code, 6 * bits - 5) << (bits - 1);
			var other = halfOffset - more;
			switch (BitString(code, 6 * bits - 4, 2))
			{
				case 0:
					DecodeOnePulse(output, BitString(code, 0, bits), bits - 1, offset + more);
					DecodeFivePulses(output + 1, BitString(code, bits, 5 * bits - 5), bits - 1, offset + more);
					break;
				case 1:
					DecodeOnePulse(output, BitString(code, 0, bits), bits - 1, offset + other);
					DecodeFivePulses(output + 1, BitString(code, bits, 5 * bits - 5), bits - 1, offset + more);
					break;
				case 2:
					DecodeTwoPulses(output, BitString(code, 0, 2 * bits - 1), bits - 1, offset + other);
					DecodeFourPulses(output + 2, BitString(code, 2 * bits - 1, 4 * bits - 4), bits - 1, offset + more);
					break;
				default:
					DecodeThreePulses(output, BitString(code, 3 * bits - 2, 3 * bits - 2), bits - 1, offset);
					DecodeThreePulses(output + 3, BitString(code, 0, 3 * bits - 2), bits - 1, offset + halfOffset);
					break;
			}
		}

		private void PitchSharpen()
		{
			for (var index = SubframeSize - 1; index != 0; index--) fixedVector[index] -= fixedVector[index - 1] * tiltCoefficient;
			for (var index = pitchLagInteger; index < SubframeSize; index++)
				fixedVector[index] = (float)(fixedVector[index] + fixedVector[index - pitchLagInteger] * 0.85);
		}

		private void DecodeGains(int subframeOffset, out float fixedGainFactor)
		{
			var index = frameFields[subframeOffset + 2] * 2;
			var table = currentMode <= 1 ? AmrWideBandTables.QuaGain6b : AmrWideBandTables.QuaGain7b;
			pitchGain[0] = table[index] * (1.0f / (1 << 14));
			fixedGainFactor = table[index + 1] * (1.0f / (1 << 11));
		}

		private float CalculateVoiceFactor()
		{
			var pitchEnergy = (double)AmrDsp.ScalarProduct(pitchVector, 0, pitchVector, 0, SubframeSize) * pitchGain[0] * pitchGain[0];
			var fixedEnergy = (double)AmrDsp.ScalarProduct(fixedVector, 0, fixedVector, 0, SubframeSize) * fixedGain[0] * fixedGain[0];
			return (float)((pitchEnergy - fixedEnergy) / (pitchEnergy + fixedEnergy + 0.01));
		}

		private float EnhanceNoise(float currentFixedGain, float voiceFactor, float stability)
		{
			var smoothing = (float)(0.5 * (1 - voiceFactor) * stability);
			float gain;
			if (currentFixedGain < previousTransitionGain)
				gain = Math.Min(previousTransitionGain, currentFixedGain + currentFixedGain * (6226 * (1.0f / (1 << 15))));
			else gain = Math.Max(previousTransitionGain, currentFixedGain * (27536 * (1.0f / (1 << 15))));
			previousTransitionGain = gain;
			return (float)(smoothing * gain + (1 - smoothing) * currentFixedGain);
		}

		private float[] ApplyAntiSparseness()
		{
			if (currentMode > 1) return fixedVector;
			var impulseFilter = pitchGain[0] < 0.6f ? 0 : pitchGain[0] < 0.9f ? 1 : 2;
			if (fixedGain[0] > 3.0f * fixedGain[1])
			{
				if (impulseFilter < 2) impulseFilter++;
			} else
			{
				var count = 0;
				for (var index = 0; index < 6; index++) if (pitchGain[index] < 0.6f) count++;
				if (count > 2) impulseFilter = 0;
				if (impulseFilter > previousImpulseFilter + 1) impulseFilter--;
			}
			previousImpulseFilter = impulseFilter;
			impulseFilter += currentMode == 1 ? 1 : 0;
			if (impulseFilter >= 2) return fixedVector;
			var coefficients = impulseFilter == 0 ? AmrWideBandTables.IrFilterStr : AmrWideBandTables.IrFilterMid;
			Array.Clear(spareVector, 0, spareVector.Length);
			for (var index = 0; index < SubframeSize; index++)
				if (fixedVector[index] != 0.0f)
					AmrDsp.CircularAdd(spareVector, 0, spareVector, 0, coefficients, 0, index, fixedVector[index], SubframeSize);
			return spareVector;
		}

		private static void EnhancePitch(float[] vector, float voiceFactor)
		{
			var coefficient = (float)(0.125 * (1 + voiceFactor));
			var last = vector[0];
			vector[0] -= coefficient * vector[1];
			for (var index = 1; index < SubframeSize - 1; index++)
			{
				var current = vector[index];
				vector[index] -= coefficient * (last + vector[index + 1]);
				last = current;
			}
			vector[SubframeSize - 1] -= coefficient * last;
		}

		private void Synthesize(int subframe, float synthesisFixedGain, float[] synthesisFixedVector)
		{
			AmrDsp.WeightedVectorSum(synthesisExcitation, 0, pitchVector, 0, synthesisFixedVector, 0,
				pitchGain[0], synthesisFixedGain, SubframeSize);
			if (pitchGain[0] > 0.5f && currentMode <= 1)
			{
				var energy = AmrDsp.ScalarProduct(synthesisExcitation, 0, synthesisExcitation, 0, SubframeSize);
				var pitchFactor = (float)(0.25 * pitchGain[0] * pitchGain[0]);
				for (var index = 0; index < SubframeSize; index++) synthesisExcitation[index] += pitchFactor * pitchVector[index];
				AmrDsp.ScaleToEnergy(synthesisExcitation, 0, synthesisExcitation, 0, energy, SubframeSize);
			}
			AmrDsp.SynthesisFilter(samplesAz, FilterOrder, lpc, subframe * FilterOrder,
				synthesisExcitation, 0, SubframeSize, FilterOrder);
		}

		private void Deemphasize()
		{
			const float factor = 0.68f;
			samplesUp[UpsampleMemory] = samplesAz[FilterOrder] + factor * deemphasisMemory[0];
			for (var index = 1; index < SubframeSize; index++)
				samplesUp[UpsampleMemory + index] = samplesAz[FilterOrder + index] + samplesUp[UpsampleMemory + index - 1] * factor;
			deemphasisMemory[0] = samplesUp[UpsampleMemory + SubframeSize - 1];
		}

		private void Upsample(float[] output, int outputOffset)
		{
			var outputIndex = 0;
			var integerPart = 0;
			for (var group = 0; group < OutputSubframeSize / 5; group++)
			{
				output[outputOffset + outputIndex++] = samplesUp[12 + integerPart];
				var fractionalPart = 4;
				for (var phase = 1; phase < 5; phase++)
				{
					output[outputOffset + outputIndex++] = AmrDsp.ScalarProduct(samplesUp, 1 + integerPart,
						AmrWideBandTables.UpsampleFir, (4 - fractionalPart) * UpsampleMemory, UpsampleMemory);
					integerPart++;
					fractionalPart--;
				}
			}
		}

		private float FindHighBandGain(int subframeOffset)
		{
			if (currentMode == 8) return AmrWideBandTables.QuaHbGain[frameFields[subframeOffset + 3]] * (1.0f / (1 << 14));
			var temporary = AmrDsp.ScalarProduct(highBandSamples, 0, highBandSamples, 1, SubframeSize - 1);
			var tilt = temporary > 0.0f ? temporary / AmrDsp.ScalarProduct(highBandSamples, 0, highBandSamples, 0, SubframeSize) : 0.0f;
			return Math.Clamp((float)((1.0 - tilt) * (1.25 - 0.25 * (frameFields[0] > 0 ? 1 : 0))), 0.1f, 1.0f);
		}

		private void ScaleHighBandExcitation(float highBandGain)
		{
			var energy = AmrDsp.ScalarProduct(synthesisExcitation, 0, synthesisExcitation, 0, SubframeSize);
			for (var index = 0; index < OutputSubframeSize; index++)
				highBandExcitation[index] = (float)(32768.0 - (ushort)NextRandom());
			AmrDsp.ScaleToEnergy(highBandExcitation, 0, highBandExcitation, 0, energy * highBandGain * highBandGain, OutputSubframeSize);
		}

		private uint NextRandom()
		{
			var value = unchecked(randomState[(randomIndex - 24) & 63] + randomState[(randomIndex - 55) & 63]);
			randomState[randomIndex & 63] = value;
			randomIndex = unchecked(randomIndex + 1);
			return value;
		}

		private void SynthesizeHighBand(int subframe)
		{
			var coefficientCount = FilterOrder;
			if (currentMode == 0)
			{
				AmrDsp.WeightedVectorSum(extrapolatedIsf, 0, pastFinalIsf, 0, currentIsf, 0,
					AmrWideBandTables.IsfpInter[subframe], (float)(1.0 - AmrWideBandTables.IsfpInter[subframe]), FilterOrder);
				ExtrapolateIsf(extrapolatedIsf);
				extrapolatedIsf[HighBandFilterOrder - 1] = (float)(extrapolatedIsf[HighBandFilterOrder - 1] * 2.0);
				AmrDsp.LsfToLsp(extrapolatedIsp, 0, extrapolatedIsf, 0, HighBandFilterOrder);
				WideBand20LspToLpc(extrapolatedIsp, highBandLpc);
				WeightLpc(highBandLpc, highBandLpc, 0.9f, HighBandFilterOrder);
				coefficientCount = HighBandFilterOrder;
			} else WeightLpc(highBandLpc, lpc, 0.6f, FilterOrder, subframe * FilterOrder);
			AmrDsp.SynthesisFilter(samplesHighBand, HighBandFilterOrder, highBandLpc, 0,
				highBandExcitation, 0, OutputSubframeSize, coefficientCount);
		}

		private void ExtrapolateIsf(float[] values)
		{
			values[HighBandFilterOrder - 1] = values[FilterOrder - 1];
			for (var index = 0; index < FilterOrder - 2; index++) differenceIsf[index] = values[index + 1] - values[index];
			var mean = 0.0f;
			for (var index = 2; index < FilterOrder - 2; index++) mean += differenceIsf[index] * (1.0f / (FilterOrder - 4));
			var maximumCorrelation = 0;
			for (var index = 0; index < 3; index++)
			{
				correlationLag[index] = AutoCorrelation(mean, index + 2);
				if (correlationLag[index] > correlationLag[maximumCorrelation]) maximumCorrelation = index;
			}
			maximumCorrelation++;
			for (var index = FilterOrder - 1; index < HighBandFilterOrder - 1; index++)
				values[index] = values[index - 1] + values[index - 1 - maximumCorrelation] - values[index - 2 - maximumCorrelation];
			var estimate = (float)(7965 + (values[2] - values[3] - values[4]) / 6.0);
			var scale = (float)(0.5 * (Math.Min(estimate, 7600) - values[FilterOrder - 2]) /
				(values[HighBandFilterOrder - 2] - values[FilterOrder - 2]));
			for (var index = FilterOrder - 1; index < HighBandFilterOrder - 1; index++)
				differenceIsf[index - FilterOrder + 1] = scale * (values[index] - values[index - 1]);
			for (var index = 1; index < HighBandFilterOrder - FilterOrder; index++)
				if (differenceIsf[index] + differenceIsf[index - 1] < 5.0f)
				{
					if (differenceIsf[index] > differenceIsf[index - 1]) differenceIsf[index - 1] = 5.0f - differenceIsf[index];
					else differenceIsf[index] = 5.0f - differenceIsf[index - 1];
				}
			for (var index = FilterOrder - 1; index < HighBandFilterOrder - 1; index++)
				values[index] = values[index - 1] + differenceIsf[index - FilterOrder + 1] * (1.0f / (1 << 15));
			for (var index = 0; index < HighBandFilterOrder - 1; index++) values[index] = (float)(values[index] * 0.8);
		}

		private float AutoCorrelation(float mean, int lag)
		{
			var sum = 0.0f;
			for (var index = 7; index < FilterOrder - 2; index++)
			{
				var product = (differenceIsf[index] - mean) * (differenceIsf[index - lag] - mean);
				sum += product * product;
			}
			return sum;
		}

		private static void WeightLpc(float[] output, float[] input, float gamma, int count, int inputOffset = 0)
		{
			var factor = gamma;
			for (var index = 0; index < count; index++)
			{
				output[index] = input[inputOffset + index] * factor;
				factor *= gamma;
			}
		}

		private void WideBand20LspToLpc(double[] source, float[] output)
		{
			LspPolynomial(source, 0, highBandFirstPolynomial, 0, 10);
			highBandSecondPolynomial[0] = 0.0;
			LspPolynomial(source, 1, highBandSecondPolynomial, 1, 9);
			for (var index = 1; index < 10; index++)
			{
				var firstValue = highBandFirstPolynomial[index] * (1 + source[19]);
				var secondValue = (highBandSecondPolynomial[index + 1] - highBandSecondPolynomial[index - 1]) * (1 - source[19]);
				output[index - 1] = (float)((firstValue + secondValue) * 0.5);
				output[20 - index - 1] = (float)((firstValue - secondValue) * 0.5);
			}
			output[9] = (float)((1.0 + source[19]) * highBandFirstPolynomial[10] * 0.5);
			output[19] = (float)source[19];
		}

		private static void LspPolynomial(double[] source, int sourceOffset, double[] output, int outputOffset, int halfOrder)
		{
			output[outputOffset] = 1.0;
			output[outputOffset + 1] = -2 * source[sourceOffset];
			for (var index = 2; index <= halfOrder; index++)
			{
				var value = -2 * source[sourceOffset + 2 * (index - 1)];
				output[outputOffset + index] = value * output[outputOffset + index - 1] + 2 * output[outputOffset + index - 2];
				for (var inner = index - 1; inner > 1; inner--)
					output[outputOffset + inner] += output[outputOffset + inner - 1] * value + output[outputOffset + inner - 2];
				output[outputOffset + 1] += value;
			}
		}

		private void HighBandFir(float[] output, float[] coefficients, float[] memory, float[] input, int inputOffset)
		{
			Array.Copy(memory, firData, FirMemory);
			Array.Copy(input, inputOffset, firData, FirMemory, OutputSubframeSize);
			for (var sample = 0; sample < OutputSubframeSize; sample++)
			{
				output[sample] = 0.0f;
				for (var index = 0; index <= FirMemory; index++) output[sample] += firData[sample + index] * coefficients[index];
			}
			Array.Copy(firData, OutputSubframeSize, memory, 0, FirMemory);
		}

		private void UpdateSubframeState()
		{
			Array.Copy(excitationBuffer, SubframeSize, excitationBuffer, 0, ExcitationOffset);
			Array.Copy(pitchGain, 0, pitchGain, 1, 5);
			fixedGain[1] = fixedGain[0];
			Array.Copy(samplesAz, SubframeSize, samplesAz, 0, FilterOrder);
			Array.Copy(samplesUp, SubframeSize, samplesUp, 0, UpsampleMemory);
			Array.Copy(samplesHighBand, OutputSubframeSize, samplesHighBand, 0, HighBandFilterOrder);
		}
	}
}
