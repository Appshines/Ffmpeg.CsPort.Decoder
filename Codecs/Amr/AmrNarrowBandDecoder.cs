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
	/// <summary>Decodes AMR-NB speech frames with the scalar FFmpeg 8.1.2 reference schedule.</summary>
	public sealed class AmrNarrowBandDecoder
	{
		private const int BlockSize = 160;
		private const int SubframeSize = 40;
		private const int FilterOrder = 10;
		private const int PitchDelayMinimum = 20;
		private const int PitchDelayMaximum = 143;
		private const int ExcitationOffset = PitchDelayMaximum + FilterOrder + 1;
		private const float SharpMaximum = 0.79449462890625f;

		private readonly ushort[] frameFields = new ushort[57];
		private readonly short[] previousLsfResidual = new short[FilterOrder];
		private readonly double[] lsp = new double[4 * FilterOrder];
		private readonly double[] previousLspSubframe4 = new double[FilterOrder];
		private readonly float[] lsfQuantized = new float[4 * FilterOrder];
		private readonly float[] lsfAverage = new float[FilterOrder];
		private readonly float[] lpc = new float[4 * FilterOrder];
		private readonly float[] excitationBuffer = new float[ExcitationOffset + SubframeSize];
		private readonly float[] pitchVector = new float[SubframeSize];
		private readonly float[] fixedVector = new float[SubframeSize];
		private readonly float[] predictionError = new float[4];
		private readonly float[] pitchGain = new float[5];
		private readonly float[] fixedGain = new float[5];
		private readonly float[] postfilterMemory = new float[FilterOrder];
		private readonly float[] highPassMemory = new float[2];
		private readonly float[] samplesInput = new float[FilterOrder + SubframeSize];
		private readonly float[] outputSamples = new float[BlockSize];
		private readonly float[] lsfWithoutResidual = new float[FilterOrder];
		private readonly float[] currentLsf = new float[FilterOrder];
		private readonly short[] lsfResidual = new short[FilterOrder];
		private readonly float[] excitation = new float[SubframeSize];
		private readonly float[] sparseVector = new float[SubframeSize];
		private readonly float[] filter1 = new float[SubframeSize];
		private readonly float[] filter2 = new float[SubframeSize];
		private readonly float[] impulseBuffer = new float[FilterOrder + 22];
		private readonly float[] poleOutput = new float[SubframeSize + FilterOrder];
		private readonly float[] lpcNumerator = new float[FilterOrder];
		private readonly float[] lpcDenominator = new float[FilterOrder];
		private readonly double[] firstPolynomial = new double[FilterOrder / 2 + 1];
		private readonly double[] secondPolynomial = new double[FilterOrder / 2 + 1];
		private readonly AmrFixedVector fixedSparse = new AmrFixedVector();

		private int currentMode;
		private byte pitchLagInteger;
		private float beta;
		private byte differenceCount;
		private byte hangCount;
		private float previousSparseFixedGain;
		private byte previousImpulseFilter;
		private byte impulseFilterOnset;
		private float tiltMemory;
		private float postfilterGain;

		public AmrNarrowBandDecoder()
		{
			for (var index = 0; index < FilterOrder; index++)
			{
				previousLspSubframe4[index] = AmrNarrowBandTables.LspSub4Init[index] * 1000 / (float)(1 << 15);
				lsfAverage[index] = lsfQuantized[3 * FilterOrder + index] = AmrNarrowBandTables.LspAvgInit[index] / (float)(1 << 15);
			}
			for (var index = 0; index < predictionError.Length; index++) predictionError[index] = -14.0f;
		}

		public int Channels => 1;
		public int SampleRate => 8000;
		public int MaximumOutputBytes => BlockSize * sizeof(float);

		/// <summary>Consumes one AMR storage-format speech frame and writes 160 packed scalar float samples.</summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < 1 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			var mode = packet[packetOffset] >> 3 & 15;
			if (mode >= 9 || packetLength < AmrNarrowBandTables.FrameSizesNb[mode] + 1) return FfmpegError.InvalidData;
			if (mode == 8) return FfmpegError.PatchWelcome;
			if (output.Length < MaximumOutputBytes) return FfmpegError.InvalidArgument;
			currentMode = mode;
			UnpackFrame(packet, packetOffset + 1, GetOrder(mode));
			if (mode == 7) DecodeFiveLsfVectors();
			else DecodeThreeLsfVectors();
			for (var subframe = 0; subframe < 4; subframe++)
				AmrDsp.LspToLpc(lsp, subframe * FilterOrder, lpc, subframe * FilterOrder, 5, firstPolynomial, secondPolynomial);
			for (var subframe = 0; subframe < 4; subframe++)
			{
				DecodePitchVector(subframe);
				DecodeFixedSparse(subframe);
				DecodeGains(subframe, out var fixedGainFactor);
				PitchSharpening(subframe);
				if (fixedSparse.PitchLag == 0) return FfmpegError.InvalidData;
				AmrDsp.SetFixedVector(fixedVector, 0, fixedSparse, 1.0f, SubframeSize);
				fixedGain[4] = AmrDsp.SetAmrFixedGain(fixedGainFactor,
					AmrDsp.ScalarProduct(fixedVector, 0, fixedVector, 0, SubframeSize) / SubframeSize,
					predictionError, AmrNarrowBandTables.EnergyMean[currentMode], AmrNarrowBandTables.EnergyPredFac);
				for (var index = 0; index < SubframeSize; index++) excitationBuffer[ExcitationOffset + index] *= pitchGain[4];
				AmrDsp.SetFixedVector(excitationBuffer, ExcitationOffset, fixedSparse, fixedGain[4], SubframeSize);
				for (var index = 0; index < SubframeSize; index++)
					excitationBuffer[ExcitationOffset + index] = MathF.Truncate(excitationBuffer[ExcitationOffset + index]);
				var synthesisFixedGain = SmoothFixedGain(subframe);
				var synthesisFixedVector = ApplyAntiSparseness(synthesisFixedGain);
				if (Synthesize(subframe, synthesisFixedGain, synthesisFixedVector, false))
					Synthesize(subframe, synthesisFixedGain, synthesisFixedVector, true);
				Postfilter(subframe, outputSamples, subframe * SubframeSize);
				AmrDsp.ClearFixedVector(fixedVector, 0, fixedSparse, SubframeSize);
				UpdateState();
			}
			AmrDsp.ApplySecondOrderTransfer(outputSamples, 0, outputSamples, 0, AmrNarrowBandTables.HighpassZeros,
				AmrNarrowBandTables.HighpassPoles, (float)((double)0.939819335f * (2.0 / 32768.0)), highPassMemory, BlockSize);
			AmrDsp.WeightedVectorSum(lsfAverage, 0, lsfAverage, 0, lsfQuantized, 3 * FilterOrder, 0.84f, 0.16f, FilterOrder);
			for (var index = 0; index < BlockSize; index++)
				BinaryPrimitives.WriteInt32LittleEndian(output.Slice(index * sizeof(float), sizeof(float)), BitConverter.SingleToInt32Bits(outputSamples[index]));
			frame = new AudioFrameInfo(BlockSize, 1, AudioSampleFormat.FloatPlanar, 1, MaximumOutputBytes, MaximumOutputBytes);
			return AmrNarrowBandTables.FrameSizesNb[mode] + 1;
		}

		private void UnpackFrame(byte[] packet, int packetOffset, byte[] order)
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

		private static byte[] GetOrder(int mode)
		{
			return mode switch
			{
				0 => AmrNarrowBandTables.OrderMODE4k75,
				1 => AmrNarrowBandTables.OrderMODE5k15,
				2 => AmrNarrowBandTables.OrderMODE5k9,
				3 => AmrNarrowBandTables.OrderMODE6k7,
				4 => AmrNarrowBandTables.OrderMODE7k4,
				5 => AmrNarrowBandTables.OrderMODE7k95,
				6 => AmrNarrowBandTables.OrderMODE10k2,
				_ => AmrNarrowBandTables.OrderMODE12k2
			};
		}

		private void InterpolateLsf(float[] newLsf)
		{
			for (var subframe = 0; subframe < 4; subframe++)
				AmrDsp.WeightedVectorSum(lsfQuantized, subframe * FilterOrder, lsfQuantized, 3 * FilterOrder, newLsf, 0,
					(float)(0.25 * (3 - subframe)), (float)(0.25 * (subframe + 1)), FilterOrder);
		}

		private void DecodeFiveLsfVectors()
		{
			var parameter2 = frameFields[2];
			for (var index = 0; index < FilterOrder; index++)
				lsfWithoutResidual[index] = (float)(previousLsfResidual[index] * (8000.0 / 32768.0) * 0.65 + AmrNarrowBandTables.Lsf5Mean[index]);
			DecodeFiveLsfVector(lsp, FilterOrder, lsfWithoutResidual, 0, parameter2, false);
			DecodeFiveLsfVector(lsp, 3 * FilterOrder, lsfWithoutResidual, 2, parameter2, true);
			for (var index = 0; index < FilterOrder; index++)
			{
				lsp[index] = 0.5 * previousLspSubframe4[index] + 0.5 * lsp[FilterOrder + index];
				lsp[2 * FilterOrder + index] = 0.5 * lsp[FilterOrder + index] + 0.5 * lsp[3 * FilterOrder + index];
			}
		}

		private void DecodeFiveLsfVector(double[] destination, int destinationOffset, float[] unquantized,
			int quantizerOffset, int parameter2, bool update)
		{
			CopyPair(AmrNarrowBandTables.Lsf51, frameFields[0] * 4 + quantizerOffset, 0);
			CopyPair(AmrNarrowBandTables.Lsf52, frameFields[1] * 4 + quantizerOffset, 2);
			CopyPair(AmrNarrowBandTables.Lsf53, (parameter2 >> 1) * 4 + quantizerOffset, 4);
			CopyPair(AmrNarrowBandTables.Lsf54, frameFields[3] * 4 + quantizerOffset, 6);
			CopyPair(AmrNarrowBandTables.Lsf55, frameFields[4] * 4 + quantizerOffset, 8);
			if ((parameter2 & 1) != 0)
			{
				lsfResidual[4] *= -1;
				lsfResidual[5] *= -1;
			}
			if (update) Array.Copy(lsfResidual, previousLsfResidual, FilterOrder);
			for (var index = 0; index < FilterOrder; index++)
				currentLsf[index] = (float)(lsfResidual[index] * ((8000.0 / 32768.0) / 8000.0) + unquantized[index] * (1.0 / 8000.0));
			AmrDsp.SetMinimumLsfDistance(currentLsf, 0, 50.0488 / 8000.0, FilterOrder);
			if (update) InterpolateLsf(currentLsf);
			AmrDsp.LsfToLsp(destination, destinationOffset, currentLsf, 0, FilterOrder);
		}

		private void CopyPair(short[] table, int tableOffset, int outputOffset)
		{
			lsfResidual[outputOffset] = table[tableOffset];
			lsfResidual[outputOffset + 1] = table[tableOffset + 1];
		}

		private void DecodeThreeLsfVectors()
		{
			var table1 = currentMode == 5 ? AmrNarrowBandTables.Lsf31MODE7k95 : AmrNarrowBandTables.Lsf31;
			var firstOffset = frameFields[0] * 3;
			for (var index = 0; index < 3; index++) lsfResidual[index] = table1[firstOffset + index];
			var secondOffset = (frameFields[1] << (currentMode <= 1 ? 1 : 0)) * 3;
			for (var index = 0; index < 3; index++) lsfResidual[3 + index] = AmrNarrowBandTables.Lsf32[secondOffset + index];
			var table3 = currentMode <= 1 ? AmrNarrowBandTables.Lsf33MODE5k15 : AmrNarrowBandTables.Lsf33;
			var thirdOffset = frameFields[2] * 4;
			for (var index = 0; index < 4; index++) lsfResidual[6 + index] = table3[thirdOffset + index];
			for (var index = 0; index < FilterOrder; index++)
				currentLsf[index] = (float)((lsfResidual[index] + previousLsfResidual[index] * AmrNarrowBandTables.PredFac[index]) *
					((8000.0 / 32768.0) / 8000.0) + AmrNarrowBandTables.Lsf3Mean[index] * (1.0 / 8000.0));
			AmrDsp.SetMinimumLsfDistance(currentLsf, 0, 50.0488 / 8000.0, FilterOrder);
			InterpolateLsf(currentLsf);
			Array.Copy(lsfResidual, previousLsfResidual, FilterOrder);
			AmrDsp.LsfToLsp(lsp, 3 * FilterOrder, currentLsf, 0, FilterOrder);
			for (var subframe = 1; subframe <= 3; subframe++)
				for (var index = 0; index < FilterOrder; index++)
					lsp[(subframe - 1) * FilterOrder + index] = previousLspSubframe4[index] +
						(lsp[3 * FilterOrder + index] - previousLspSubframe4[index]) * 0.25 * subframe;
		}

		private void DecodePitchVector(int subframe)
		{
			var baseOffset = 5 + 13 * subframe;
			int lagInteger;
			int lagFraction;
			if (currentMode == 7)
			{
				var pitchIndex = frameFields[baseOffset];
				if (subframe == 0 || subframe == 2)
				{
					if (pitchIndex < 463)
					{
						lagInteger = (pitchIndex + 107) * 10923 >> 16;
						lagFraction = pitchIndex - lagInteger * 6 + 105;
					} else
					{
						lagInteger = pitchIndex - 368;
						lagFraction = 0;
					}
				} else
				{
					lagInteger = ((pitchIndex + 5) * 10923 >> 16) - 1;
					lagFraction = pitchIndex - lagInteger * 6 - 3;
					lagInteger += Math.Clamp(pitchLagInteger - 5, 18, PitchDelayMaximum - 9);
				}
			} else
			{
				AmrDsp.DecodePitchLag(out lagInteger, out lagFraction, frameFields[baseOffset], pitchLagInteger, subframe,
					currentMode != 0 && currentMode != 1, currentMode <= 3 ? 4 : currentMode == 5 ? 5 : 6,
					PitchDelayMinimum, PitchDelayMaximum);
				lagFraction *= 2;
			}
			pitchLagInteger = (byte)lagInteger;
			lagInteger += lagFraction > 0 ? 1 : 0;
			AmrDsp.Interpolate(excitationBuffer, ExcitationOffset, excitationBuffer, ExcitationOffset + 1 - lagInteger,
				AmrDsp.B60Sinc, 6, lagFraction + 6 - 6 * (lagFraction > 0 ? 1 : 0), 10, SubframeSize);
			Array.Copy(excitationBuffer, ExcitationOffset, pitchVector, 0, SubframeSize);
		}

		/// <summary>
		/// Reconstructs the AMR-NB algebraic fixed-codebook pulses for one subframe in mode-specific track order.
		/// </summary>
		private void DecodeFixedSparse(int subframe)
		{
			var pulsesOffset = 5 + 13 * subframe + 3;
			fixedSparse.NoRepeatMask = 0;
			if (currentMode == 7)
			{
				AmrDsp.DecodeTenPulses35Bits(frameFields, pulsesOffset, fixedSparse, AmrNarrowBandTables.GrayDecode, 5, 3);
				return;
			}
			if (currentMode == 6)
			{
				DecodeEightPulses31Bits(pulsesOffset);
				return;
			}
			var fixedIndex = frameFields[pulsesOffset];
			int pulseSubset;
			if (currentMode <= 1)
			{
				pulseSubset = (fixedIndex >> 3 & 8) + (subframe << 1);
				fixedSparse.Positions[0] = (fixedIndex & 7) * 5 + AmrNarrowBandTables.TrackPosition[pulseSubset];
				fixedSparse.Positions[1] = (fixedIndex >> 3 & 7) * 5 + AmrNarrowBandTables.TrackPosition[pulseSubset + 1];
				fixedSparse.Count = 2;
			} else if (currentMode == 2)
			{
				pulseSubset = (fixedIndex & 1) << 1 | 1;
				fixedSparse.Positions[0] = (fixedIndex >> 1 & 7) * 5 + pulseSubset;
				pulseSubset = fixedIndex >> 4 & 3;
				fixedSparse.Positions[1] = (fixedIndex >> 6 & 7) * 5 + pulseSubset + (pulseSubset == 3 ? 1 : 0);
				fixedSparse.Count = fixedSparse.Positions[0] == fixedSparse.Positions[1] ? 1 : 2;
			} else if (currentMode == 3)
			{
				fixedSparse.Positions[0] = (fixedIndex & 7) * 5;
				pulseSubset = fixedIndex >> 2 & 2;
				fixedSparse.Positions[1] = (fixedIndex >> 4 & 7) * 5 + pulseSubset + 1;
				pulseSubset = fixedIndex >> 6 & 2;
				fixedSparse.Positions[2] = (fixedIndex >> 8 & 7) * 5 + pulseSubset + 2;
				fixedSparse.Count = 3;
			} else
			{
				fixedSparse.Positions[0] = AmrNarrowBandTables.GrayDecode[fixedIndex & 7];
				fixedSparse.Positions[1] = AmrNarrowBandTables.GrayDecode[fixedIndex >> 3 & 7] + 1;
				fixedSparse.Positions[2] = AmrNarrowBandTables.GrayDecode[fixedIndex >> 6 & 7] + 2;
				pulseSubset = fixedIndex >> 9 & 1;
				fixedSparse.Positions[3] = AmrNarrowBandTables.GrayDecode[fixedIndex >> 10 & 7] + pulseSubset + 3;
				fixedSparse.Count = 4;
			}
			for (var index = 0; index < fixedSparse.Count; index++)
				fixedSparse.Values[index] = (frameFields[pulsesOffset + 1] >> index & 1) != 0 ? 1.0f : -1.0f;
		}

		private void DecodeEightPulses31Bits(int pulsesOffset)
		{
			DecodeTenBitPulse(frameFields[pulsesOffset + 4], 0, 4, 1);
			DecodeTenBitPulse(frameFields[pulsesOffset + 5], 2, 6, 5);
			var temporary = ((frameFields[pulsesOffset + 6] >> 2) * 25 + 12) >> 5;
			fixedSparse.Positions[3] = temporary % 5;
			fixedSparse.Positions[7] = temporary / 5;
			if ((fixedSparse.Positions[7] & 1) != 0) fixedSparse.Positions[3] = 4 - fixedSparse.Positions[3];
			fixedSparse.Positions[3] = fixedSparse.Positions[3] << 1 | frameFields[pulsesOffset + 6] & 1;
			fixedSparse.Positions[7] = fixedSparse.Positions[7] << 1 | frameFields[pulsesOffset + 6] >> 1 & 1;
			fixedSparse.Count = 8;
			for (var index = 0; index < 4; index++)
			{
				var position1 = (fixedSparse.Positions[index] << 2) + index;
				var position2 = (fixedSparse.Positions[index + 4] << 2) + index;
				var sign = frameFields[pulsesOffset + index] != 0 ? -1.0f : 1.0f;
				fixedSparse.Positions[index] = position1;
				fixedSparse.Positions[index + 4] = position2;
				fixedSparse.Values[index] = sign;
				fixedSparse.Values[index + 4] = position2 < position1 ? -sign : sign;
			}
		}

		private void DecodeTenBitPulse(int code, int first, int second, int third)
		{
			var tableOffset = (code >> 3) * 3;
			fixedSparse.Positions[first] = (AmrNarrowBandTables.BaseFiveTable[tableOffset + 2] << 1) + (code & 1);
			fixedSparse.Positions[second] = (AmrNarrowBandTables.BaseFiveTable[tableOffset + 1] << 1) + (code >> 1 & 1);
			fixedSparse.Positions[third] = (AmrNarrowBandTables.BaseFiveTable[tableOffset] << 1) + (code >> 2 & 1);
		}

		private void DecodeGains(int subframe, out float fixedGainFactor)
		{
			var baseOffset = 5 + 13 * subframe;
			if (currentMode == 7 || currentMode == 5)
			{
				pitchGain[4] = AmrNarrowBandTables.QuaGainPit[frameFields[baseOffset + 1]] * (1.0f / 16384.0f);
				fixedGainFactor = AmrNarrowBandTables.QuaGainCode[frameFields[baseOffset + 2]] * (1.0f / 2048.0f);
				return;
			}
			ushort[] table;
			int tableOffset;
			if (currentMode >= 3)
			{
				table = AmrNarrowBandTables.GainsHigh;
				tableOffset = frameFields[baseOffset + 1] * 2;
			} else if (currentMode >= 1)
			{
				table = AmrNarrowBandTables.GainsLow;
				tableOffset = frameFields[baseOffset + 1] * 2;
			} else
			{
				table = AmrNarrowBandTables.GainsMODE4k75;
				var pairedBase = 5 + 13 * (subframe & 2);
				tableOffset = (frameFields[pairedBase + 1] * 2 + (subframe & 1)) * 2;
			}
			pitchGain[4] = table[tableOffset] * (1.0f / 16384.0f);
			fixedGainFactor = table[tableOffset + 1] * (1.0f / 4096.0f);
		}

		private void PitchSharpening(int subframe)
		{
			if (currentMode == 7) beta = Math.Min(pitchGain[4], 1.0f);
			fixedSparse.PitchLag = pitchLagInteger;
			fixedSparse.PitchFactor = beta;
			if (currentMode != 0 || (subframe & 1) != 0) beta = Math.Clamp(pitchGain[4], 0.0f, SharpMaximum);
		}

		private float SmoothFixedGain(int subframe)
		{
			var difference = 0.0f;
			for (var index = 0; index < FilterOrder; index++)
				difference = (float)(difference + (double)MathF.Abs(lsfAverage[index] - lsfQuantized[subframe * FilterOrder + index]) /
					lsfAverage[index]);
			differenceCount++;
			if (difference <= 0.65f) differenceCount = 0;
			if (differenceCount > 10)
			{
				hangCount = 0;
				differenceCount--;
			}
			if (hangCount < 40) hangCount++;
			else if (currentMode < 4 || currentMode == 6)
			{
				var smoothing = Math.Clamp((float)(4.0 * difference - 1.6), 0.0f, 1.0f);
				var mean = (float)((fixedGain[0] + fixedGain[1] + fixedGain[2] + fixedGain[3] + fixedGain[4]) * 0.2);
				return (float)(smoothing * fixedGain[4] + (1.0 - smoothing) * mean);
			}
			return fixedGain[4];
		}

		private float[] ApplyAntiSparseness(float synthesisFixedGain)
		{
			var impulseFilter = pitchGain[4] < 0.6f ? 0 : pitchGain[4] < 0.9f ? 1 : 2;
			if (synthesisFixedGain > 2.0f * previousSparseFixedGain) impulseFilterOnset = 2;
			else if (impulseFilterOnset != 0) impulseFilterOnset--;
			if (impulseFilterOnset == 0)
			{
				var count = 0;
				for (var index = 0; index < 5; index++) if (pitchGain[index] < 0.6f) count++;
				if (count > 2) impulseFilter = 0;
				if (impulseFilter > previousImpulseFilter + 1) impulseFilter--;
			} else if (impulseFilter < 2) impulseFilter++;
			if (synthesisFixedGain < 5.0f) impulseFilter = 2;
			var result = fixedVector;
			if (currentMode != 4 && currentMode < 6 && impulseFilter < 2)
			{
				var filter = impulseFilter == 0
					? currentMode == 5 ? AmrNarrowBandTables.IrFilterStrongMODE7k95 : AmrNarrowBandTables.IrFilterStrong
					: AmrNarrowBandTables.IrFilterMedium;
				ApplyImpulseFilter(sparseVector, filter);
				result = sparseVector;
			}
			previousImpulseFilter = (byte)impulseFilter;
			previousSparseFixedGain = synthesisFixedGain;
			return result;
		}

		private void ApplyImpulseFilter(float[] output, float[] filter)
		{
			var lag = fixedSparse.PitchLag;
			var factor = fixedSparse.PitchFactor;
			if (lag < SubframeSize)
			{
				AmrDsp.CircularAdd(filter1, 0, filter, 0, filter, 0, lag, factor, SubframeSize);
				if (lag < SubframeSize >> 1) AmrDsp.CircularAdd(filter2, 0, filter, 0, filter1, 0, lag, factor, SubframeSize);
			}
			Array.Clear(output, 0, SubframeSize);
			for (var index = 0; index < fixedSparse.Count; index++)
			{
				var position = fixedSparse.Positions[index];
				var selected = position >= SubframeSize - lag ? filter : position >= SubframeSize - (lag << 1) ? filter1 : filter2;
				AmrDsp.CircularAdd(output, 0, output, 0, selected, 0, position, fixedSparse.Values[index], SubframeSize);
			}
		}

		private bool Synthesize(int subframe, float synthesisFixedGain, float[] synthesisFixedVector, bool overflow)
		{
			if (overflow)
				for (var index = 0; index < SubframeSize; index++) pitchVector[index] *= 0.25f;
			AmrDsp.WeightedVectorSum(excitation, 0, pitchVector, 0, synthesisFixedVector, 0,
				pitchGain[4], synthesisFixedGain, SubframeSize);
			if (pitchGain[4] > 0.5f && !overflow)
			{
				var energy = AmrDsp.ScalarProduct(excitation, 0, excitation, 0, SubframeSize);
				var pitchFactor = pitchGain[4] * (currentMode == 7
					? 0.25f * Math.Min(pitchGain[4], 1.0f)
					: 0.5f * Math.Min(pitchGain[4], SharpMaximum));
				for (var index = 0; index < SubframeSize; index++) excitation[index] += pitchFactor * pitchVector[index];
				AmrDsp.ScaleToEnergy(excitation, 0, excitation, 0, energy, SubframeSize);
			}
			AmrDsp.SynthesisFilter(samplesInput, FilterOrder, lpc, subframe * FilterOrder, excitation, 0, SubframeSize, FilterOrder);
			for (var index = 0; index < SubframeSize; index++) if (MathF.Abs(samplesInput[FilterOrder + index]) > 32768.0f) return true;
			return false;
		}

		private void Postfilter(int subframe, float[] output, int outputOffset)
		{
			var speechEnergy = AmrDsp.ScalarProduct(samplesInput, FilterOrder, samplesInput, FilterOrder, SubframeSize);
			var numeratorPowers = currentMode == 7 || currentMode == 6 ? AmrDsp.Pow07 : AmrDsp.Pow055;
			var denominatorPowers = currentMode == 7 || currentMode == 6 ? AmrDsp.Pow075 : AmrDsp.Pow07;
			for (var index = 0; index < FilterOrder; index++)
			{
				lpcNumerator[index] = lpc[subframe * FilterOrder + index] * numeratorPowers[index];
				lpcDenominator[index] = lpc[subframe * FilterOrder + index] * denominatorPowers[index];
			}
			Array.Copy(postfilterMemory, poleOutput, FilterOrder);
			AmrDsp.SynthesisFilter(poleOutput, FilterOrder, lpcDenominator, 0, samplesInput, FilterOrder, SubframeSize, FilterOrder);
			Array.Copy(poleOutput, SubframeSize, postfilterMemory, 0, FilterOrder);
			AmrDsp.ZeroSynthesisFilter(output, outputOffset, lpcNumerator, 0, poleOutput, FilterOrder, SubframeSize, FilterOrder);
			AmrDsp.TiltCompensation(ref tiltMemory, CalculateTiltFactor(), output, outputOffset, SubframeSize);
			AmrDsp.AdaptiveGainControl(output, outputOffset, output, outputOffset, speechEnergy, SubframeSize, 0.9f, ref postfilterGain);
		}

		private float CalculateTiltFactor()
		{
			Array.Clear(impulseBuffer, 0, impulseBuffer.Length);
			var impulseOffset = FilterOrder;
			impulseBuffer[impulseOffset] = 1.0f;
			Array.Copy(lpcNumerator, 0, impulseBuffer, impulseOffset + 1, FilterOrder);
			AmrDsp.SynthesisFilter(impulseBuffer, impulseOffset, lpcDenominator, 0, impulseBuffer, impulseOffset, 22, FilterOrder);
			var first = AmrDsp.ScalarProduct(impulseBuffer, impulseOffset, impulseBuffer, impulseOffset, 22);
			var second = AmrDsp.ScalarProduct(impulseBuffer, impulseOffset, impulseBuffer, impulseOffset + 1, 21);
			return second >= 0.0f ? (float)(second / first * 0.8) : 0.0f;
		}

		private void UpdateState()
		{
			Array.Copy(lsp, 3 * FilterOrder, previousLspSubframe4, 0, FilterOrder);
			Array.Copy(excitationBuffer, SubframeSize, excitationBuffer, 0, ExcitationOffset);
			Array.Copy(pitchGain, 1, pitchGain, 0, 4);
			Array.Copy(fixedGain, 1, fixedGain, 0, 4);
			Array.Copy(samplesInput, SubframeSize, samplesInput, 0, FilterOrder);
		}
	}
}
