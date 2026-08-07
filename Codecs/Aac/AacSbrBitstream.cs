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
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Ports FFmpeg's scalar SBR header, grid, envelope, noise, and derived-frequency-table parser.</summary>
	internal sealed class AacSbrBitstream
	{
		private static readonly sbyte[] CeilingLog2 = { 0, 1, 2, 2, 3, 3 };
		private readonly AacSbrSpectrumParameters previousSpectrum = new AacSbrSpectrumParameters();

		/// <summary>Consumes one complete SBR fill payload while preserving the host AAC reader's declared byte boundary.</summary>
		public int DecodeExtension(
			AacSpectralBandReplication sbr,
			BitReader reader,
			bool crc,
			int count,
			AacElementType elementType,
			int coreSampleRate,
			bool allowParametricStereo)
		{
			var startPosition = reader.Position;
			var payloadBits = count * 8 - 4;
			if (payloadBits < 0 || reader.BitsLeft < payloadBits)
				return FfmpegError.InvalidData;
			var endPosition = startPosition + payloadBits;
			sbr.Reset = false;
			if (sbr.SampleRate == 0)
				sbr.SampleRate = 2 * coreSampleRate;
			var usedBits = 0;
			if (crc)
			{
				reader.SkipBits(10);
				usedBits += 10;
			}
			sbr.Crossover[0] = sbr.Crossover[1];
			sbr.NumberOfSubbands[0] = sbr.NumberOfSubbands[1];
			sbr.PreviousGridPushed = true;
			usedBits++;
			if (reader.ReadBit() != 0)
				usedBits += ReadHeader(sbr, reader);
			if (sbr.Reset)
				Reset(sbr);
			if (sbr.Started)
				usedBits += ReadData(sbr, reader, elementType, allowParametricStereo);
			var alignmentBits = (payloadBits - usedBits) & 7;
			var bytesRead = (usedBits + alignmentBits + 4) >> 3;
			if (bytesRead > count)
				TurnOff(sbr);
			reader.Seek(endPosition);
			return 0;
		}

		private static void TurnOff(AacSpectralBandReplication sbr)
		{
			sbr.Started = false;
			sbr.ReadyForDequantization = false;
			sbr.Crossover[1] = 32;
			sbr.NumberOfSubbands[1] = 0;
			sbr.Data[0].AttackEnvelope[1] = -1;
			sbr.Data[1].AttackEnvelope[1] = -1;
			sbr.Spectrum.StartFrequency = -1;
			sbr.Spectrum.StopFrequency = -1;
			sbr.Spectrum.CrossoverBand = -1;
			sbr.Spectrum.FrequencyScale = -1;
			sbr.Spectrum.AlterScale = -1;
			sbr.Spectrum.NoiseBands = -1;
		}

		/// <summary>
		/// Parses an SBR header and updates frequency-table state only when the transmitted configuration changes.
		/// </summary>
		private int ReadHeader(AacSpectralBandReplication sbr, BitReader reader)
		{
			var start = reader.Position;
			var oldLimiterBands = sbr.LimiterBands;
			previousSpectrum.CopyFrom(sbr.Spectrum);
			sbr.Started = true;
			sbr.ReadyForDequantization = false;
			sbr.HeaderAmplitudeResolution = reader.ReadBit() != 0;
			sbr.Spectrum.StartFrequency = (int)reader.ReadBits(4);
			sbr.Spectrum.StopFrequency = (int)reader.ReadBits(4);
			sbr.Spectrum.CrossoverBand = (int)reader.ReadBits(3);
			reader.SkipBits(2);
			var extra1 = reader.ReadBit() != 0;
			var extra2 = reader.ReadBit() != 0;
			if (extra1)
			{
				sbr.Spectrum.FrequencyScale = (int)reader.ReadBits(2);
				sbr.Spectrum.AlterScale = (int)reader.ReadBit();
				sbr.Spectrum.NoiseBands = (int)reader.ReadBits(2);
			} else
			{
				sbr.Spectrum.FrequencyScale = 2;
				sbr.Spectrum.AlterScale = 1;
				sbr.Spectrum.NoiseBands = 2;
			}
			if (!sbr.Spectrum.EqualsValues(previousSpectrum))
				sbr.Reset = true;
			if (extra2)
			{
				sbr.LimiterBands = (int)reader.ReadBits(2);
				sbr.LimiterGains = (int)reader.ReadBits(2);
				sbr.InterpolateFrequency = reader.ReadBit() != 0;
				sbr.SmoothingMode = reader.ReadBit() != 0;
			} else
			{
				sbr.LimiterBands = 2;
				sbr.LimiterGains = 2;
				sbr.InterpolateFrequency = true;
				sbr.SmoothingMode = true;
			}
			if (sbr.LimiterBands != oldLimiterBands && !sbr.Reset)
				MakeLimiterFrequencyTable(sbr);
			return reader.Position - start;
		}

		private static void MakeBands(short[] bands, int offset, int start, int stop, int numberOfBands)
		{
			var basis = MathF.Pow((float)stop / start, 1.0f / numberOfBands);
			var product = (float)start;
			var previous = start;
			for (var band = 0; band < numberOfBands - 1; band++)
			{
				product *= basis;
				var present = (int)MathF.Round(product);
				bands[offset + band] = (short)(present - previous);
				previous = present;
			}
			bands[offset + numberOfBands - 1] = (short)(stop - previous);
		}

		private static void Sort(short[] values, int offset, int count)
		{
			for (var index = offset + 1; index < offset + count; index++)
			{
				var value = values[index];
				var destination = index;
				while (destination > offset && values[destination - 1] > value)
				{
					values[destination] = values[destination - 1];
					destination--;
				}
				values[destination] = value;
			}
		}

		/// <summary>Reconstructs FFmpeg's master SBR frequency table for the parsed output sample rate and header controls.</summary>
		private static int MakeMasterFrequencyTable(AacSpectralBandReplication sbr)
		{
			var offsetRow = -1;
			switch (sbr.SampleRate)
			{
				case 16000: offsetRow = 0; break;
				case 22050: offsetRow = 1; break;
				case 24000: offsetRow = 2; break;
				case 32000: offsetRow = 3; break;
				case 44100:
				case 48000:
				case 64000: offsetRow = 4; break;
				case 88200:
				case 96000:
				case 128000:
				case 176400:
				case 192000: offsetRow = 5; break;
			}
			if (offsetRow < 0)
				return FfmpegError.InvalidData;
			var minimumFrequency = sbr.SampleRate < 32000 ? 3000 : sbr.SampleRate < 64000 ? 4000 : 5000;
			var startMinimum = ((minimumFrequency << 7) + (sbr.SampleRate >> 1)) / sbr.SampleRate;
			var stopMinimum = ((minimumFrequency << 8) + (sbr.SampleRate >> 1)) / sbr.SampleRate;
			sbr.K[0] = startMinimum + AacSbrTables.FrequencyOffsets[offsetRow * 16 + sbr.Spectrum.StartFrequency];
			if (sbr.Spectrum.StopFrequency < 14)
			{
				sbr.K[2] = stopMinimum;
				MakeBands(sbr.BandScratch0, 0, stopMinimum, 64, 13);
				Sort(sbr.BandScratch0, 0, 13);
				for (var index = 0; index < sbr.Spectrum.StopFrequency; index++)
					sbr.K[2] += sbr.BandScratch0[index];
			} else if (sbr.Spectrum.StopFrequency == 14)
			{
				sbr.K[2] = 2 * sbr.K[0];
			} else if (sbr.Spectrum.StopFrequency == 15)
			{
				sbr.K[2] = 3 * sbr.K[0];
			} else
			{
				return FfmpegError.InvalidData;
			}
			sbr.K[2] = Math.Min(64, sbr.K[2]);
			var maximumSubbands = sbr.SampleRate <= 32000 ? 48 : sbr.SampleRate == 44100 ? 35 : 32;
			if (sbr.K[2] - sbr.K[0] > maximumSubbands)
				return FfmpegError.InvalidData;
			if (sbr.Spectrum.FrequencyScale == 0)
			{
				var delta = sbr.Spectrum.AlterScale + 1;
				sbr.NumberOfMasterBands = ((sbr.K[2] - sbr.K[0] + (delta & 2)) >> delta) << 1;
				if (sbr.NumberOfMasterBands <= 0 || sbr.Spectrum.CrossoverBand >= sbr.NumberOfMasterBands)
					return FfmpegError.InvalidData;
				for (var index = 1; index <= sbr.NumberOfMasterBands; index++)
					sbr.MasterFrequencyTable[index] = (ushort)delta;
				var difference = sbr.K[2] - sbr.K[0] - sbr.NumberOfMasterBands * delta;
				if (difference < 0)
				{
					sbr.MasterFrequencyTable[1]--;
					sbr.MasterFrequencyTable[2] -= (ushort)(difference < -1 ? 1 : 0);
				} else if (difference != 0)
				{
					sbr.MasterFrequencyTable[sbr.NumberOfMasterBands]++;
				}
				sbr.MasterFrequencyTable[0] = (ushort)sbr.K[0];
				for (var index = 1; index <= sbr.NumberOfMasterBands; index++)
					sbr.MasterFrequencyTable[index] += sbr.MasterFrequencyTable[index - 1];
				return 0;
			}

			var halfBands = 7 - sbr.Spectrum.FrequencyScale;
			var twoRegions = 49 * sbr.K[2] > 110 * sbr.K[0];
			sbr.K[1] = twoRegions ? 2 * sbr.K[0] : sbr.K[2];
			var numberOfBands0 = (int)MathF.Round(halfBands * MathF.Log2(sbr.K[1] / (float)sbr.K[0])) * 2;
			if (numberOfBands0 <= 0)
				return FfmpegError.InvalidData;
			var first = sbr.BandScratch0;
			first[0] = 0;
			MakeBands(first, 1, sbr.K[0], sbr.K[1], numberOfBands0);
			Sort(first, 1, numberOfBands0);
			var maximumDelta0 = first[numberOfBands0];
			first[0] = (short)sbr.K[0];
			for (var index = 1; index <= numberOfBands0; index++)
			{
				if (first[index] <= 0)
					return FfmpegError.InvalidData;
				first[index] += first[index - 1];
			}
			if (twoRegions)
			{
				var second = sbr.BandScratch1;
				var inverseWarp = sbr.Spectrum.AlterScale != 0 ? 0.76923076923076923077f : 1.0f;
				var numberOfBands1 = (int)MathF.Round(halfBands * inverseWarp * MathF.Log2(sbr.K[2] / (float)sbr.K[1])) * 2;
				MakeBands(second, 1, sbr.K[1], sbr.K[2], numberOfBands1);
				var minimumDelta1 = second[1];
				for (var index = 2; index <= numberOfBands1; index++)
					minimumDelta1 = Math.Min(minimumDelta1, second[index]);
				if (minimumDelta1 < maximumDelta0)
				{
					Sort(second, 1, numberOfBands1);
					var change = Math.Min(maximumDelta0 - second[1], (second[numberOfBands1] - second[1]) >> 1);
					second[1] += (short)change;
					second[numberOfBands1] -= (short)change;
				}
				Sort(second, 1, numberOfBands1);
				second[0] = (short)sbr.K[1];
				for (var index = 1; index <= numberOfBands1; index++)
				{
					if (second[index] <= 0)
						return FfmpegError.InvalidData;
					second[index] += second[index - 1];
				}
				sbr.NumberOfMasterBands = numberOfBands0 + numberOfBands1;
				if (sbr.Spectrum.CrossoverBand >= sbr.NumberOfMasterBands)
					return FfmpegError.InvalidData;
				for (var index = 0; index <= numberOfBands0; index++)
					sbr.MasterFrequencyTable[index] = (ushort)first[index];
				for (var index = 1; index <= numberOfBands1; index++)
					sbr.MasterFrequencyTable[numberOfBands0 + index] = (ushort)second[index];
			} else
			{
				sbr.NumberOfMasterBands = numberOfBands0;
				if (sbr.Spectrum.CrossoverBand >= sbr.NumberOfMasterBands)
					return FfmpegError.InvalidData;
				for (var index = 0; index <= numberOfBands0; index++)
					sbr.MasterFrequencyTable[index] = (ushort)first[index];
			}
			return 0;
		}

		/// <summary>
		/// Derives SBR high-frequency patch starts and widths in FFmpeg's source-band search order.
		/// </summary>
		private static int CalculatePatches(AacSpectralBandReplication sbr)
		{
			var lastK = -1;
			var lastMasterSubband = -1;
			var subband = 0;
			var masterSubband = sbr.K[0];
			var upperSubband = sbr.Crossover[1];
			var goalSubband = ((1000 << 11) + (sbr.SampleRate >> 1)) / sbr.SampleRate;
			sbr.NumberOfPatches = 0;
			var k = sbr.NumberOfMasterBands;
			if (goalSubband < sbr.Crossover[1] + sbr.NumberOfSubbands[1])
			{
				k = 0;
				while (sbr.MasterFrequencyTable[k] < goalSubband)
					k++;
			}
			do
			{
				var odd = 0;
				if (k == lastK && masterSubband == lastMasterSubband)
					return FfmpegError.InvalidData;
				lastK = k;
				lastMasterSubband = masterSubband;
				for (var index = k; index == k || subband > sbr.K[0] - 1 + masterSubband - odd; index--)
				{
					subband = sbr.MasterFrequencyTable[index];
					odd = (subband + sbr.K[0]) & 1;
				}
				if (sbr.NumberOfPatches > 5)
					return FfmpegError.InvalidData;
				sbr.PatchSubbandCount[sbr.NumberOfPatches] = (byte)Math.Max(subband - upperSubband, 0);
				sbr.PatchStartSubband[sbr.NumberOfPatches] = (byte)(sbr.K[0] - odd - sbr.PatchSubbandCount[sbr.NumberOfPatches]);
				if (sbr.PatchSubbandCount[sbr.NumberOfPatches] > 0)
				{
					upperSubband = subband;
					masterSubband = subband;
					sbr.NumberOfPatches++;
				} else
				{
					masterSubband = sbr.Crossover[1];
				}
				if (sbr.MasterFrequencyTable[k] - subband < 3)
					k = sbr.NumberOfMasterBands;
			} while (subband != sbr.Crossover[1] + sbr.NumberOfSubbands[1]);
			if (sbr.NumberOfPatches > 1 && sbr.PatchSubbandCount[sbr.NumberOfPatches - 1] < 3)
				sbr.NumberOfPatches--;
			return 0;
		}

		/// <summary>Derives high/low/noise/limiter borders and patch layout from the regenerated master table.</summary>
		private static int MakeDerivedFrequencyTables(AacSpectralBandReplication sbr)
		{
			sbr.NumberOfBands[1] = sbr.NumberOfMasterBands - sbr.Spectrum.CrossoverBand;
			sbr.NumberOfBands[0] = (sbr.NumberOfBands[1] + 1) >> 1;
			for (var index = 0; index <= sbr.NumberOfBands[1]; index++)
				sbr.HighFrequencyTable[index] = sbr.MasterFrequencyTable[sbr.Spectrum.CrossoverBand + index];
			sbr.NumberOfSubbands[1] = sbr.HighFrequencyTable[sbr.NumberOfBands[1]] - sbr.HighFrequencyTable[0];
			sbr.Crossover[1] = sbr.HighFrequencyTable[0];
			if (sbr.Crossover[1] + sbr.NumberOfSubbands[1] > 64 || sbr.Crossover[1] > 32)
				return FfmpegError.InvalidData;
			sbr.LowFrequencyTable[0] = sbr.HighFrequencyTable[0];
			var odd = sbr.NumberOfBands[1] & 1;
			for (var index = 1; index <= sbr.NumberOfBands[0]; index++)
				sbr.LowFrequencyTable[index] = sbr.HighFrequencyTable[2 * index - odd];
			sbr.NumberOfNoiseBands = Math.Max(1, (int)MathF.Round(sbr.Spectrum.NoiseBands * MathF.Log2(sbr.K[2] / (float)sbr.Crossover[1])));
			if (sbr.NumberOfNoiseBands > 5)
			{
				sbr.NumberOfNoiseBands = 1;
				return FfmpegError.InvalidData;
			}
			sbr.NoiseFrequencyTable[0] = sbr.LowFrequencyTable[0];
			var position = 0;
			for (var index = 1; index <= sbr.NumberOfNoiseBands; index++)
			{
				position += (sbr.NumberOfBands[0] - position) / (sbr.NumberOfNoiseBands + 1 - index);
				sbr.NoiseFrequencyTable[index] = sbr.LowFrequencyTable[position];
			}
			if (CalculatePatches(sbr) < 0)
				return FfmpegError.InvalidData;
			MakeLimiterFrequencyTable(sbr);
			sbr.Data[0].NoiseIndex = 0;
			sbr.Data[1].NoiseIndex = 0;
			return 0;
		}

		private static bool Contains(short[] values, int count, short value)
		{
			for (var index = 0; index <= count; index++)
			{
				if (values[index] == value)
					return true;
			}
			return false;
		}

		/// <summary>
		/// Merges low-resolution and patch borders into each SBR limiter table and removes overly close entries.
		/// </summary>
		private static void MakeLimiterFrequencyTable(AacSpectralBandReplication sbr)
		{
			if (sbr.LimiterBands == 0)
			{
				sbr.LimiterFrequencyTable[0] = sbr.LowFrequencyTable[0];
				sbr.LimiterFrequencyTable[1] = sbr.LowFrequencyTable[sbr.NumberOfBands[0]];
				sbr.NumberOfLimiterBands = 1;
				return;
			}
			var warped = sbr.LimiterBands == 1 ? 1.32715174233856803909f : sbr.LimiterBands == 2 ? 1.18509277094158210129f : 1.11987160404675912501f;
			var patchBorders = sbr.BandScratch0;
			patchBorders[0] = (short)sbr.Crossover[1];
			for (var index = 1; index <= sbr.NumberOfPatches; index++)
				patchBorders[index] = (short)(patchBorders[index - 1] + sbr.PatchSubbandCount[index - 1]);
			for (var index = 0; index <= sbr.NumberOfBands[0]; index++)
				sbr.LimiterFrequencyTable[index] = sbr.LowFrequencyTable[index];
			if (sbr.NumberOfPatches > 1)
			{
				for (var index = 1; index < sbr.NumberOfPatches; index++)
					sbr.LimiterFrequencyTable[sbr.NumberOfBands[0] + index] = (ushort)patchBorders[index];
			}
			var count = sbr.NumberOfPatches + sbr.NumberOfBands[0];
			for (var index = 1; index < count; index++)
			{
				var value = sbr.LimiterFrequencyTable[index];
				var destination = index;
				while (destination > 0 && sbr.LimiterFrequencyTable[destination - 1] > value)
				{
					sbr.LimiterFrequencyTable[destination] = sbr.LimiterFrequencyTable[destination - 1];
					destination--;
				}
				sbr.LimiterFrequencyTable[destination] = value;
			}
			sbr.NumberOfLimiterBands = sbr.NumberOfBands[0] + sbr.NumberOfPatches - 1;
			var input = 1;
			var output = 0;
			while (output < sbr.NumberOfLimiterBands)
			{
				if (sbr.LimiterFrequencyTable[input] >= sbr.LimiterFrequencyTable[output] * warped)
				{
					output++;
					sbr.LimiterFrequencyTable[output] = sbr.LimiterFrequencyTable[input++];
				} else if (sbr.LimiterFrequencyTable[input] == sbr.LimiterFrequencyTable[output] ||
					!Contains(patchBorders, sbr.NumberOfPatches, (short)sbr.LimiterFrequencyTable[input]))
				{
					input++;
					sbr.NumberOfLimiterBands--;
				} else if (!Contains(patchBorders, sbr.NumberOfPatches, (short)sbr.LimiterFrequencyTable[output]))
				{
					sbr.LimiterFrequencyTable[output] = sbr.LimiterFrequencyTable[input++];
					sbr.NumberOfLimiterBands--;
				} else
				{
					output++;
					sbr.LimiterFrequencyTable[output] = sbr.LimiterFrequencyTable[input++];
				}
			}
		}

		private static void Reset(AacSpectralBandReplication sbr)
		{
			if (MakeMasterFrequencyTable(sbr) < 0 || MakeDerivedFrequencyTables(sbr) < 0)
				TurnOff(sbr);
		}

		/// <summary>Decodes the four SBR frame-grid classes and derives noise and attack-envelope time borders.</summary>
		private static int ReadGrid(AacSpectralBandReplication sbr, BitReader reader, AacSbrData data)
		{
			var pointer = 0;
			var trailingBorder = 16;
			var oldEnvelopeCount = data.NumberOfEnvelopes;
			data.FrequencyResolution[0] = data.FrequencyResolution[data.NumberOfEnvelopes];
			data.AmplitudeResolution = sbr.HeaderAmplitudeResolution;
			data.PreviousEnvelopeEnd = data.TimeEnvelope[oldEnvelopeCount];
			var frameClass = (int)reader.ReadBits(2);
			var numberOfEnvelopes = 0;
			switch (frameClass)
			{
				case 0:
					numberOfEnvelopes = 1 << (int)reader.ReadBits(2);
					if (numberOfEnvelopes > 5)
						return FfmpegError.InvalidData;
					data.NumberOfEnvelopes = numberOfEnvelopes;
					if (numberOfEnvelopes == 1)
						data.AmplitudeResolution = false;
					data.TimeEnvelope[0] = 0;
					data.TimeEnvelope[numberOfEnvelopes] = (byte)trailingBorder;
					var step = (trailingBorder + (numberOfEnvelopes >> 1)) / numberOfEnvelopes;
					for (var index = 0; index < numberOfEnvelopes - 1; index++)
						data.TimeEnvelope[index + 1] = (byte)(data.TimeEnvelope[index] + step);
					data.FrequencyResolution[1] = (byte)reader.ReadBit();
					for (var index = 1; index < numberOfEnvelopes; index++)
						data.FrequencyResolution[index + 1] = data.FrequencyResolution[1];
					break;
				case 1:
					trailingBorder += (int)reader.ReadBits(2);
					var relativeTrailing = (int)reader.ReadBits(2);
					data.NumberOfEnvelopes = relativeTrailing + 1;
					data.TimeEnvelope[0] = 0;
					data.TimeEnvelope[data.NumberOfEnvelopes] = (byte)trailingBorder;
					for (var index = 0; index < relativeTrailing; index++)
						data.TimeEnvelope[data.NumberOfEnvelopes - 1 - index] = (byte)(data.TimeEnvelope[data.NumberOfEnvelopes - index] - 2 * (int)reader.ReadBits(2) - 2);
					pointer = (int)reader.ReadBitsOrZero(CeilingLog2[data.NumberOfEnvelopes]);
					for (var index = 0; index < data.NumberOfEnvelopes; index++)
						data.FrequencyResolution[data.NumberOfEnvelopes - index] = (byte)reader.ReadBit();
					break;
				case 2:
					data.TimeEnvelope[0] = (byte)reader.ReadBits(2);
					var relativeLeading = (int)reader.ReadBits(2);
					data.NumberOfEnvelopes = relativeLeading + 1;
					data.TimeEnvelope[data.NumberOfEnvelopes] = (byte)trailingBorder;
					for (var index = 0; index < relativeLeading; index++)
						data.TimeEnvelope[index + 1] = (byte)(data.TimeEnvelope[index] + 2 * (int)reader.ReadBits(2) + 2);
					pointer = (int)reader.ReadBitsOrZero(CeilingLog2[data.NumberOfEnvelopes]);
					ReadBitsVector(reader, data.FrequencyResolution, 1, data.NumberOfEnvelopes);
					break;
				default:
					data.TimeEnvelope[0] = (byte)reader.ReadBits(2);
					trailingBorder += (int)reader.ReadBits(2);
					var lead = (int)reader.ReadBits(2);
					var trail = (int)reader.ReadBits(2);
					numberOfEnvelopes = lead + trail + 1;
					if (numberOfEnvelopes > 5)
						return FfmpegError.InvalidData;
					data.NumberOfEnvelopes = numberOfEnvelopes;
					data.TimeEnvelope[numberOfEnvelopes] = (byte)trailingBorder;
					for (var index = 0; index < lead; index++)
						data.TimeEnvelope[index + 1] = (byte)(data.TimeEnvelope[index] + 2 * (int)reader.ReadBits(2) + 2);
					for (var index = 0; index < trail; index++)
						data.TimeEnvelope[numberOfEnvelopes - 1 - index] = (byte)(data.TimeEnvelope[numberOfEnvelopes - index] - 2 * (int)reader.ReadBits(2) - 2);
					pointer = (int)reader.ReadBitsOrZero(CeilingLog2[numberOfEnvelopes]);
					ReadBitsVector(reader, data.FrequencyResolution, 1, numberOfEnvelopes);
					break;
			}
			data.FrameClass = frameClass;
			if (pointer > data.NumberOfEnvelopes + 1)
				return FfmpegError.InvalidData;
			for (var index = 1; index <= data.NumberOfEnvelopes; index++)
			{
				if (data.TimeEnvelope[index - 1] >= data.TimeEnvelope[index])
					return FfmpegError.InvalidData;
			}
			data.NumberOfNoiseEnvelopes = data.NumberOfEnvelopes > 1 ? 2 : 1;
			data.TimeNoise[0] = data.TimeEnvelope[0];
			data.TimeNoise[data.NumberOfNoiseEnvelopes] = data.TimeEnvelope[data.NumberOfEnvelopes];
			if (data.NumberOfNoiseEnvelopes > 1)
			{
				int index;
				if (frameClass == 0)
					index = data.NumberOfEnvelopes >> 1;
				else if ((frameClass & 1) != 0)
					index = data.NumberOfEnvelopes - Math.Max(pointer - 1, 1);
				else if (pointer == 0)
					index = 1;
				else if (pointer == 1)
					index = data.NumberOfEnvelopes - 1;
				else
					index = pointer - 1;
				data.TimeNoise[1] = data.TimeEnvelope[index];
			}
			data.AttackEnvelope[0] = -(data.AttackEnvelope[1] != oldEnvelopeCount ? 1 : 0);
			data.AttackEnvelope[1] = -1;
			if ((frameClass & 1) != 0 && pointer != 0)
				data.AttackEnvelope[1] = data.NumberOfEnvelopes + 1 - pointer;
			else if (frameClass == 2 && pointer > 1)
				data.AttackEnvelope[1] = pointer - 1;
			return 0;
		}

		private static void ReadBitsVector(BitReader reader, byte[] values, int offset, int count)
		{
			for (var index = 0; index < count; index++)
				values[offset + index] = (byte)reader.ReadBit();
		}

		private static void CopyGrid(AacSbrData destination, AacSbrData source)
		{
			destination.FrequencyResolution[0] = destination.FrequencyResolution[destination.NumberOfEnvelopes];
			destination.PreviousEnvelopeEnd = destination.TimeEnvelope[destination.NumberOfEnvelopes];
			destination.AttackEnvelope[0] = -(destination.AttackEnvelope[1] != destination.NumberOfEnvelopes ? 1 : 0);
			for (var index = 1; index < destination.FrequencyResolution.Length; index++)
				destination.FrequencyResolution[index] = source.FrequencyResolution[index];
			Array.Copy(source.TimeEnvelope, destination.TimeEnvelope, destination.TimeEnvelope.Length);
			Array.Copy(source.TimeNoise, destination.TimeNoise, destination.TimeNoise.Length);
			destination.NumberOfEnvelopes = source.NumberOfEnvelopes;
			destination.AmplitudeResolution = source.AmplitudeResolution;
			destination.NumberOfNoiseEnvelopes = source.NumberOfNoiseEnvelopes;
			destination.FrameClass = source.FrameClass;
			destination.AttackEnvelope[1] = source.AttackEnvelope[1];
		}

		private static void ReadDeltaFlags(BitReader reader, AacSbrData data)
		{
			ReadBitsVector(reader, data.DeltaFrequencyEnvelope, 0, data.NumberOfEnvelopes);
			ReadBitsVector(reader, data.DeltaFrequencyNoise, 0, data.NumberOfNoiseEnvelopes);
		}

		private static void ReadInverseFiltering(AacSpectralBandReplication sbr, BitReader reader, AacSbrData data)
		{
			for (var index = 0; index < 5; index++)
				data.InverseFilteringMode[1, index] = data.InverseFilteringMode[0, index];
			for (var index = 0; index < sbr.NumberOfNoiseBands; index++)
				data.InverseFilteringMode[0, index] = (byte)reader.ReadBits(2);
		}

		/// <summary>
		/// Decodes differential SBR envelope values across time or frequency using the selected Huffman books.
		/// </summary>
		private static int ReadEnvelope(AacSpectralBandReplication sbr, BitReader reader, AacSbrData data, int channel)
		{
			var delta = channel == 1 && sbr.Coupling ? 2 : 1;
			var timeHuffman = 0;
			var frequencyHuffman = 0;
			int bits;
			if (sbr.Coupling && channel != 0)
			{
				if (data.AmplitudeResolution)
				{
					bits = 5; timeHuffman = 6; frequencyHuffman = 7;
				} else
				{
					bits = 6; timeHuffman = 2; frequencyHuffman = 3;
				}
			} else if (data.AmplitudeResolution)
			{
				bits = 6; timeHuffman = 4; frequencyHuffman = 5;
			} else
			{
				bits = 7; timeHuffman = 0; frequencyHuffman = 1;
			}
			var odd = sbr.NumberOfBands[1] & 1;
			for (var envelope = 0; envelope < data.NumberOfEnvelopes; envelope++)
			{
				var resolution = data.FrequencyResolution[envelope + 1];
				if (data.DeltaFrequencyEnvelope[envelope] != 0)
				{
					for (var band = 0; band < sbr.NumberOfBands[resolution]; band++)
					{
						int previousBand;
						if (resolution == data.FrequencyResolution[envelope])
							previousBand = band;
						else if (resolution != 0)
							previousBand = (band + odd) >> 1;
						else
							previousBand = band != 0 ? 2 * band - odd : 0;
						var value = data.QuantizedEnvelope[envelope, previousBand] + delta * reader.ReadVlc(AacSbrTables.HuffmanVlcs[timeHuffman].Table, 9, 3);
						data.QuantizedEnvelope[envelope + 1, band] = unchecked((byte)value);
						if (data.QuantizedEnvelope[envelope + 1, band] > 127)
							return FfmpegError.InvalidData;
					}
				} else
				{
					data.QuantizedEnvelope[envelope + 1, 0] = (byte)(delta * (int)reader.ReadBits(bits));
					for (var band = 1; band < sbr.NumberOfBands[resolution]; band++)
					{
						var value = data.QuantizedEnvelope[envelope + 1, band - 1] + delta * reader.ReadVlc(AacSbrTables.HuffmanVlcs[frequencyHuffman].Table, 9, 3);
						data.QuantizedEnvelope[envelope + 1, band] = unchecked((byte)value);
						if (data.QuantizedEnvelope[envelope + 1, band] > 127)
							return FfmpegError.InvalidData;
					}
				}
			}
			for (var band = 0; band < 48; band++)
				data.QuantizedEnvelope[0, band] = data.QuantizedEnvelope[data.NumberOfEnvelopes, band];
			return 0;
		}

		private static int ReadNoise(AacSpectralBandReplication sbr, BitReader reader, AacSbrData data, int channel)
		{
			var delta = channel == 1 && sbr.Coupling ? 2 : 1;
			var timeHuffman = sbr.Coupling && channel != 0 ? 9 : 8;
			var frequencyHuffman = sbr.Coupling && channel != 0 ? 7 : 5;
			for (var envelope = 0; envelope < data.NumberOfNoiseEnvelopes; envelope++)
			{
				if (data.DeltaFrequencyNoise[envelope] != 0)
				{
					for (var band = 0; band < sbr.NumberOfNoiseBands; band++)
					{
						var value = data.QuantizedNoise[envelope, band] + delta * reader.ReadVlc(AacSbrTables.HuffmanVlcs[timeHuffman].Table, 9, 2);
						data.QuantizedNoise[envelope + 1, band] = unchecked((byte)value);
						if (data.QuantizedNoise[envelope + 1, band] > 30)
							return FfmpegError.InvalidData;
					}
				} else
				{
					data.QuantizedNoise[envelope + 1, 0] = (byte)(delta * (int)reader.ReadBits(5));
					for (var band = 1; band < sbr.NumberOfNoiseBands; band++)
					{
						var value = data.QuantizedNoise[envelope + 1, band - 1] + delta * reader.ReadVlc(AacSbrTables.HuffmanVlcs[frequencyHuffman].Table, 9, 3);
						data.QuantizedNoise[envelope + 1, band] = unchecked((byte)value);
						if (data.QuantizedNoise[envelope + 1, band] > 30)
							return FfmpegError.InvalidData;
					}
				}
			}
			for (var band = 0; band < 5; band++)
				data.QuantizedNoise[0, band] = data.QuantizedNoise[data.NumberOfNoiseEnvelopes, band];
			return 0;
		}

		private static int ReadSingleChannel(AacSpectralBandReplication sbr, BitReader reader)
		{
			var data = sbr.Data[0];
			if (reader.ReadBit() != 0)
				reader.SkipBits(4);
			if (ReadGrid(sbr, reader, data) < 0)
				return FfmpegError.InvalidData;
			ReadDeltaFlags(reader, data);
			ReadInverseFiltering(sbr, reader, data);
			var result = ReadEnvelope(sbr, reader, data, 0);
			if (result < 0)
				return result;
			result = ReadNoise(sbr, reader, data, 0);
			if (result < 0)
				return result;
			data.AddHarmonicFlag = reader.ReadBit() != 0;
			if (data.AddHarmonicFlag)
				ReadBitsVector(reader, data.AddHarmonic, 0, sbr.NumberOfBands[1]);
			return 0;
		}

		/// <summary>Decodes coupled or independent SBR grids and scale factors for a channel-pair element.</summary>
		private static int ReadChannelPair(AacSpectralBandReplication sbr, BitReader reader)
		{
			var first = sbr.Data[0];
			var second = sbr.Data[1];
			if (reader.ReadBit() != 0)
				reader.SkipBits(8);
			sbr.Coupling = reader.ReadBit() != 0;
			if (sbr.Coupling)
			{
				if (ReadGrid(sbr, reader, first) < 0)
					return FfmpegError.InvalidData;
				CopyGrid(second, first);
				ReadDeltaFlags(reader, first);
				ReadDeltaFlags(reader, second);
				ReadInverseFiltering(sbr, reader, first);
				for (var band = 0; band < 5; band++)
				{
					second.InverseFilteringMode[1, band] = second.InverseFilteringMode[0, band];
					second.InverseFilteringMode[0, band] = first.InverseFilteringMode[0, band];
				}
				var result = ReadEnvelope(sbr, reader, first, 0);
				if (result < 0) return result;
				result = ReadNoise(sbr, reader, first, 0);
				if (result < 0) return result;
				result = ReadEnvelope(sbr, reader, second, 1);
				if (result < 0) return result;
				result = ReadNoise(sbr, reader, second, 1);
				if (result < 0) return result;
			} else
			{
				if (ReadGrid(sbr, reader, first) < 0 || ReadGrid(sbr, reader, second) < 0)
					return FfmpegError.InvalidData;
				ReadDeltaFlags(reader, first);
				ReadDeltaFlags(reader, second);
				ReadInverseFiltering(sbr, reader, first);
				ReadInverseFiltering(sbr, reader, second);
				var result = ReadEnvelope(sbr, reader, first, 0);
				if (result < 0) return result;
				result = ReadEnvelope(sbr, reader, second, 1);
				if (result < 0) return result;
				result = ReadNoise(sbr, reader, first, 0);
				if (result < 0) return result;
				result = ReadNoise(sbr, reader, second, 1);
				if (result < 0) return result;
			}
			first.AddHarmonicFlag = reader.ReadBit() != 0;
			if (first.AddHarmonicFlag)
				ReadBitsVector(reader, first.AddHarmonic, 0, sbr.NumberOfBands[1]);
			second.AddHarmonicFlag = reader.ReadBit() != 0;
			if (second.AddHarmonicFlag)
				ReadBitsVector(reader, second.AddHarmonic, 0, sbr.NumberOfBands[1]);
			return 0;
		}

		private static int ReadData(AacSpectralBandReplication sbr, BitReader reader, AacElementType elementType,
			bool allowParametricStereo)
		{
			var start = reader.Position;
			sbr.ElementType = elementType;
			sbr.ReadyForDequantization = true;
			var result = elementType == AacElementType.ChannelPair
				? ReadChannelPair(sbr, reader)
				: elementType == AacElementType.SingleChannel ? ReadSingleChannel(sbr, reader) : FfmpegError.InvalidData;
			if (result < 0)
			{
				TurnOff(sbr);
				return reader.Position - start;
			}
			if (reader.ReadBit() != 0)
			{
				var bitsLeft = (int)reader.ReadBits(4);
				if (bitsLeft == 15)
					bitsLeft += (int)reader.ReadBits(8);
				bitsLeft <<= 3;
				while (bitsLeft > 7)
				{
					bitsLeft -= 2;
					var extensionId = (int)reader.ReadBits(2);
					if (extensionId == 2 && allowParametricStereo)
					{
						bitsLeft -= AacPsBitstream.ReadData(sbr.ParametricStereo, reader, bitsLeft);
					} else
					{
						reader.SkipBits(bitsLeft);
						bitsLeft = 0;
					}
				}
				if (bitsLeft > 0)
					reader.SkipBits(bitsLeft);
			}
			return reader.Position - start;
		}
	}
}
