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
using System.Numerics;
using System.Runtime.InteropServices;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Dca
{
	/// <summary>
	/// Ports FFmpeg's DTS-HD Master Audio XLL parser, lossless predictors, band assembly, and planar integer output.
	/// </summary>
	internal sealed class DcaXllDecoder
	{
		private const int ChannelSetsMaximum = 3;
		private const int ChannelsMaximum = 8;
		private const int BandsMaximum = 2;
		private const int PredictionOrderMaximum = 16;
		private const int DecimationHistory = 8;
		private const int SpeakerCount = 32;
		private const int PbrBufferMaximum = 240 << 10;
		private readonly BitReader _Bits = new BitReader();
		private readonly DcaXllChannelSet[] _ChannelSets = CreateChannelSets();
		private readonly int[] _Navigation = new int[ChannelSetsMaximum * BandsMaximum * 1024];
		private readonly int[][] _OutputSamples = new int[SpeakerCount][];
		private readonly int[] _OutputSampleOffsets = new int[SpeakerCount];
		private readonly int[][] _TemporarySampleReferences = new int[ChannelsMaximum][];
		private readonly int[] _PredictionCoefficients = new int[PredictionOrderMaximum];
		private readonly int[] _ChannelRemap = new int[SpeakerCount];
		private readonly byte[] _PbrBuffer = new byte[PbrBufferMaximum + 64];
		private int _FrameSize;
		private int _NumberOfChannelSets;
		private int _NumberOfFrameSegments;
		private int _SegmentSamplesLog2;
		private int _SegmentSamples;
		private int _FrameSamplesLog2;
		private int _FrameSamples;
		private int _SegmentSizeBitCount;
		private int _BandCrcPresent;
		private int _ScalableLeastSignificantBits;
		private int _ChannelMaskBitCount;
		private int _FixedLeastSignificantBitWidth;
		private int _NumberOfFrequencyBands;
		private int _NumberOfChannels;
		private int _ResidualChannelSets;
		private int _ActiveChannelSets;
		private int _HighDefinitionStreamId;
		private int _PbrLength;
		private int _PbrDelay;
		private int _OutputMask;

		public int NumberOfSamples => _FrameSamples << (_NumberOfFrequencyBands - 1);
		public int SampleRate => _ChannelSets[0].Frequency << (_NumberOfFrequencyBands - 1);
		public int BaseFrequency => _ChannelSets[0].Frequency;
		public int NumberOfChannelSets => _NumberOfChannelSets;
		public int ResidualChannelSets => _ResidualChannelSets;

		/// <summary>
		/// Parses one XLL component, including partial-bitstream buffering when an EXSS asset delays or spans XLL frames.
		/// </summary>
		public int Parse(byte[] data, int exssOffset, DcaExssAsset asset)
		{
			if (_HighDefinitionStreamId != asset.HighDefinitionStreamId)
			{
				ClearPartialBitstream();
				_HighDefinitionStreamId = asset.HighDefinitionStreamId;
			}
			var componentOffset = exssOffset + asset.XllOffset;
			return _PbrLength != 0
				? ParsePartialBitstream(data, componentOffset, asset.XllSize, asset)
				: ParseWithoutPartialBitstream(data, componentOffset, asset.XllSize, asset);
		}

		private int ParseFrame(byte[] data, int offset, int size, DcaExssAsset asset)
		{
			var result = _Bits.Initialize(data, offset, size * 8);
			if (result < 0) return result;
			result = ParseCommonHeader();
			if (result < 0) return result;
			result = ParseSubHeaders(asset);
			if (result < 0) return result;
			result = ParseNavigationTable();
			if (result < 0) return result;
			result = ParseBandData();
			if (result < 0) return result;
			if (_FrameSize * 8 > (_Bits.Position + 31 & ~31))
			{
				_Bits.SkipBits(-_Bits.Position & 31);
				var extraSyncWord = _Bits.ShowBitsLong(32);
				_ = extraSyncWord == 0x02000850 || (extraSyncWord >> 1) == (0xf14000d0U >> 1);
			}
			return Seek(_FrameSize * 8);
		}

		private int ParseCommonHeader()
		{
			if (_Bits.ReadBitsLong(32) != 0x41a29547) return FfmpegError.TryAgain;
			var streamVersion = (int)_Bits.ReadBits(4) + 1;
			if (streamVersion > 1) return FfmpegError.PatchWelcome;
			var headerSize = (int)_Bits.ReadBits(8) + 1;
			var frameSizeBitCount = (int)_Bits.ReadBits(5) + 1;
			_FrameSize = (int)_Bits.ReadBitsLong(frameSizeBitCount);
			if (_FrameSize < 0 || _FrameSize >= PbrBufferMaximum) return FfmpegError.InvalidData;
			_FrameSize++;
			_NumberOfChannelSets = (int)_Bits.ReadBits(4) + 1;
			if (_NumberOfChannelSets > ChannelSetsMaximum) return FfmpegError.PatchWelcome;
			var frameSegmentsLog2 = (int)_Bits.ReadBits(4);
			_NumberOfFrameSegments = 1 << frameSegmentsLog2;
			if (_NumberOfFrameSegments > 1024) return FfmpegError.InvalidData;
			_SegmentSamplesLog2 = (int)_Bits.ReadBits(4);
			if (_SegmentSamplesLog2 == 0) return FfmpegError.InvalidData;
			_SegmentSamples = 1 << _SegmentSamplesLog2;
			if (_SegmentSamples > 512) return FfmpegError.InvalidData;
			_FrameSamplesLog2 = _SegmentSamplesLog2 + frameSegmentsLog2;
			_FrameSamples = 1 << _FrameSamplesLog2;
			if (_FrameSamples > 65536) return FfmpegError.InvalidData;
			_SegmentSizeBitCount = (int)_Bits.ReadBits(5) + 1;
			_BandCrcPresent = (int)_Bits.ReadBits(2);
			_ScalableLeastSignificantBits = (int)_Bits.ReadBit();
			_ChannelMaskBitCount = (int)_Bits.ReadBits(5) + 1;
			_FixedLeastSignificantBitWidth = _ScalableLeastSignificantBits != 0 ? (int)_Bits.ReadBits(4) : 0;
			return Seek(headerSize * 8);
		}

		/// <summary>
		/// Parses one XLL channel-set header with its channel map, decorrelation, predictors, and scalable-LSB description.
		/// </summary>
		private int ParseChannelSetHeader(DcaXllChannelSet channelSet, int channelSetIndex, DcaExssAsset asset)
		{
			var headerPosition = _Bits.Position;
			var headerSize = (int)_Bits.ReadBits(10) + 1;
			channelSet.NumberOfChannels = (int)_Bits.ReadBits(4) + 1;
			if (channelSet.NumberOfChannels > ChannelsMaximum) return FfmpegError.PatchWelcome;
			channelSet.ResidualEncode = (int)_Bits.ReadBits(channelSet.NumberOfChannels);
			channelSet.PcmBitResolution = (int)_Bits.ReadBits(5) + 1;
			channelSet.StorageBitResolution = (int)_Bits.ReadBits(5) + 1;
			if (channelSet.StorageBitResolution != 16 && channelSet.StorageBitResolution != 20 && channelSet.StorageBitResolution != 24) return FfmpegError.PatchWelcome;
			if (channelSet.PcmBitResolution > channelSet.StorageBitResolution) return FfmpegError.InvalidData;
			channelSet.Frequency = DcaTables.SamplingFrequencies[_Bits.ReadBits(4)];
			if (channelSet.Frequency > 192000) return FfmpegError.PatchWelcome;
			if (_Bits.ReadBits(2) != 0 || _Bits.ReadBits(2) != 0) return FfmpegError.PatchWelcome;
			if (asset.OneToOneChannelToSpeaker != 0)
			{
				channelSet.PrimaryChannelSet = (int)_Bits.ReadBit();
				if ((channelSet.PrimaryChannelSet != 0) != (channelSetIndex == 0)) return FfmpegError.InvalidData;
				channelSet.DownmixCoefficientsPresent = (int)_Bits.ReadBit();
				channelSet.DownmixEmbedded = channelSet.DownmixCoefficientsPresent != 0 ? (int)_Bits.ReadBit() : 0;
				if (channelSet.DownmixCoefficientsPresent != 0 && channelSet.PrimaryChannelSet != 0)
				{
					channelSet.DownmixType = (int)_Bits.ReadBits(3);
					if (channelSet.DownmixType >= 7) return FfmpegError.InvalidData;
				}
				channelSet.HierarchicalChannelSet = (int)_Bits.ReadBit();
				if (channelSet.HierarchicalChannelSet == 0 && _NumberOfChannelSets != 1) return FfmpegError.PatchWelcome;
				if (channelSet.DownmixCoefficientsPresent != 0)
				{
					var result = ParseDownmixCoefficients(channelSet);
					if (result < 0) return result;
				}
				if (_Bits.ReadBit() == 0) return FfmpegError.PatchWelcome;
				channelSet.ChannelMask = (int)_Bits.ReadBitsLong(_ChannelMaskBitCount);
				if (BitOperations.PopCount((uint)channelSet.ChannelMask) != channelSet.NumberOfChannels) return FfmpegError.InvalidData;
				var outputChannel = 0;
				for (var speaker = 0; speaker < _ChannelMaskBitCount; speaker++) if ((channelSet.ChannelMask & (1 << speaker)) != 0) channelSet.ChannelRemap[outputChannel++] = speaker;
			} else
			{
				if (channelSet.NumberOfChannels != 2 || _NumberOfChannelSets != 1 || _Bits.ReadBit() != 0) return FfmpegError.PatchWelcome;
				channelSet.PrimaryChannelSet = 1;
				channelSet.DownmixCoefficientsPresent = channelSet.DownmixEmbedded = channelSet.HierarchicalChannelSet = 0;
				channelSet.ChannelMask = 6;
				channelSet.ChannelRemap[0] = 1;
				channelSet.ChannelRemap[1] = 2;
			}

			if (channelSet.Frequency > 96000)
			{
				if (_Bits.ReadBit() != 0) return FfmpegError.PatchWelcome;
				channelSet.NumberOfFrequencyBands = 2;
			} else channelSet.NumberOfFrequencyBands = 1;
			channelSet.Frequency >>= channelSet.NumberOfFrequencyBands - 1;
			if (channelSetIndex != 0)
			{
				var primary = _ChannelSets[0];
				if (channelSet.NumberOfFrequencyBands != primary.NumberOfFrequencyBands || channelSet.Frequency != primary.Frequency ||
					channelSet.PcmBitResolution != primary.PcmBitResolution || channelSet.StorageBitResolution != primary.StorageBitResolution) return FfmpegError.PatchWelcome;
			}
			channelSet.AllocationBitCount = channelSet.StorageBitResolution > 16 ? 5 : channelSet.StorageBitResolution > 8 ? 4 : 3;
			if ((_NumberOfChannelSets > 1 || channelSet.NumberOfFrequencyBands > 1) && channelSet.AllocationBitCount < 5) channelSet.AllocationBitCount++;
			for (var bandIndex = 0; bandIndex < channelSet.NumberOfFrequencyBands; bandIndex++)
			{
				var band = channelSet.Bands[bandIndex];
				band.DecorrelationEnabled = (int)_Bits.ReadBit();
				if (band.DecorrelationEnabled != 0 && channelSet.NumberOfChannels > 1)
				{
					var channelBits = CeilingLog2(channelSet.NumberOfChannels);
					for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
					{
						band.OriginalOrder[channel] = (int)_Bits.ReadBits(channelBits);
						if (band.OriginalOrder[channel] >= channelSet.NumberOfChannels) return FfmpegError.InvalidData;
					}
					for (var channel = 0; channel < channelSet.NumberOfChannels / 2; channel++) band.DecorrelationCoefficient[channel] = _Bits.ReadBit() != 0 ? GetLinear(7) : 0;
				} else
				{
					for (var channel = 0; channel < channelSet.NumberOfChannels; channel++) band.OriginalOrder[channel] = channel;
					Array.Clear(band.DecorrelationCoefficient, 0, band.DecorrelationCoefficient.Length);
				}
				band.HighestPredictionOrder = 0;
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
				{
					band.AdaptivePredictionOrder[channel] = (int)_Bits.ReadBits(4);
					band.HighestPredictionOrder = Math.Max(band.HighestPredictionOrder, band.AdaptivePredictionOrder[channel]);
				}
				if (band.HighestPredictionOrder > _SegmentSamples) return FfmpegError.InvalidData;
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++) band.FixedPredictionOrder[channel] = band.AdaptivePredictionOrder[channel] != 0 ? 0 : (int)_Bits.ReadBits(2);
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
				{
					for (var order = 0; order < band.AdaptivePredictionOrder[channel]; order++)
					{
						var coefficient = GetLinear(8);
						if (coefficient == -128) return FfmpegError.InvalidData;
						band.AdaptiveReflectionCoefficient[channel, order] = coefficient < 0 ? -DcaTables.XllReflCoeff[-coefficient] : DcaTables.XllReflCoeff[coefficient];
					}
				}
				band.DownmixEmbedded = channelSet.DownmixEmbedded != 0 && (bandIndex == 0 || _Bits.ReadBit() != 0) ? 1 : 0;
				if ((bandIndex == 0 && _ScalableLeastSignificantBits != 0) || (bandIndex != 0 && _Bits.ReadBit() != 0))
				{
					band.LeastSignificantBitSectionSize = (int)_Bits.ReadBitsLong(_SegmentSizeBitCount);
					if (band.LeastSignificantBitSectionSize < 0 || band.LeastSignificantBitSectionSize > _FrameSize) return FfmpegError.InvalidData;
					if (band.LeastSignificantBitSectionSize != 0 && (_BandCrcPresent > 2 || (bandIndex == 0 && _BandCrcPresent > 1))) band.LeastSignificantBitSectionSize += 2;
					for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
					{
						band.ScalableLeastSignificantBits[channel] = (int)_Bits.ReadBits(4);
						if (band.ScalableLeastSignificantBits[channel] != 0 && band.LeastSignificantBitSectionSize == 0) return FfmpegError.InvalidData;
					}
				} else
				{
					band.LeastSignificantBitSectionSize = 0;
					Array.Clear(band.ScalableLeastSignificantBits, 0, band.ScalableLeastSignificantBits.Length);
				}
				if ((bandIndex == 0 && _ScalableLeastSignificantBits != 0) || (bandIndex != 0 && _Bits.ReadBit() != 0))
					for (var channel = 0; channel < channelSet.NumberOfChannels; channel++) band.BitWidthAdjustment[channel] = (int)_Bits.ReadBits(4);
				else Array.Clear(band.BitWidthAdjustment, 0, band.BitWidthAdjustment.Length);
			}
			return Seek(headerPosition + headerSize * 8);
		}

		private int ParseDownmixCoefficients(DcaXllChannelSet channelSet)
		{
			var outputChannels = channelSet.PrimaryChannelSet != 0 ? DcaTables.DmixPrimaryNch[channelSet.DownmixType] : channelSet.HierarchicalOffset;
			var coefficientPosition = 0;
			for (var output = 0; output < outputChannels; output++)
			{
				var inverseScale = 0;
				if (channelSet.PrimaryChannelSet == 0)
				{
					var code = (int)_Bits.ReadBits(9);
					var sign = (code >> 8) - 1;
					var tableIndex = (code & 0xff) - 41;
					if ((uint)tableIndex >= 201) return FfmpegError.InvalidData;
					var scale = DcaTables.Dmixtable[tableIndex + 41];
					inverseScale = unchecked((int)DcaTables.InvDmixtable[tableIndex]);
					channelSet.DownmixScale[output] = (scale ^ sign) - sign;
					channelSet.DownmixScaleInverse[output] = (inverseScale ^ sign) - sign;
				}
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
				{
					var code = (int)_Bits.ReadBits(9);
					var sign = (code >> 8) - 1;
					var tableIndex = code & 0xff;
					if (tableIndex >= DcaTables.Dmixtable.Length) return FfmpegError.InvalidData;
					var coefficient = (int)DcaTables.Dmixtable[tableIndex];
					if (channelSet.PrimaryChannelSet == 0) coefficient = DcaMath.Multiply(inverseScale, coefficient, 16);
					channelSet.DownmixCoefficient[coefficientPosition++] = (coefficient ^ sign) - sign;
				}
			}
			return 0;
		}

		private int ParseSubHeaders(DcaExssAsset asset)
		{
			_NumberOfFrequencyBands = _NumberOfChannels = _ResidualChannelSets = 0;
			for (var index = 0; index < _NumberOfChannelSets; index++)
			{
				var channelSet = _ChannelSets[index];
				channelSet.HierarchicalOffset = _NumberOfChannels;
				var result = ParseChannelSetHeader(channelSet, index, asset);
				if (result < 0) return result;
				_NumberOfFrequencyBands = Math.Max(_NumberOfFrequencyBands, channelSet.NumberOfFrequencyBands);
				if (channelSet.HierarchicalChannelSet != 0) _NumberOfChannels += channelSet.NumberOfChannels;
				if (channelSet.ResidualEncode != (1 << channelSet.NumberOfChannels) - 1) _ResidualChannelSets++;
			}
			for (var index = _NumberOfChannelSets - 1; index > 0; index--)
			{
				var channelSet = _ChannelSets[index];
				if (!IsHierarchicalDownmixChannelSet(channelSet)) continue;
				var output = FindNextHierarchicalDownmixChannelSet(index);
				if (output != null) PrescaleDownmix(channelSet, output);
			}
			_ActiveChannelSets = _NumberOfChannelSets;
			return 0;
		}

		private int ParseNavigationTable()
		{
			var count = _NumberOfFrequencyBands * _NumberOfFrameSegments * _NumberOfChannelSets;
			if (count > 1024) return FfmpegError.InvalidData;
			var position = 0;
			for (var band = 0; band < _NumberOfFrequencyBands; band++)
				for (var segment = 0; segment < _NumberOfFrameSegments; segment++)
					for (var channelSet = 0; channelSet < _NumberOfChannelSets; channelSet++)
					{
						var size = 0;
						if (_ChannelSets[channelSet].NumberOfFrequencyBands > band)
						{
							size = (int)_Bits.ReadBitsLong(_SegmentSizeBitCount);
							if (size < 0 || size >= _FrameSize) return FfmpegError.InvalidData;
							size++;
						}
						_Navigation[position++] = size;
					}
			_Bits.SkipBits(-_Bits.Position & 7);
			_Bits.SkipBits(16);
			return 0;
		}

		/// <summary>
		/// Parses one NAVI-delimited XLL band segment using linear, Rice, or hybrid residual coding without decode-loop allocation.
		/// </summary>
		private int ParseChannelSetBandData(DcaXllChannelSet channelSet, int bandIndex, int segment, int bandDataEnd)
		{
			var band = channelSet.Bands[bandIndex];
			if (segment == 0 || _Bits.ReadBit() == 0)
			{
				channelSet.SegmentCommon = (int)_Bits.ReadBit();
				var codingChannels = channelSet.SegmentCommon != 0 ? 1 : channelSet.NumberOfChannels;
				for (var index = 0; index < codingChannels; index++)
				{
					channelSet.RiceCodeFlag[index] = (int)_Bits.ReadBit();
					channelSet.HybridLinearAllocation[index] = channelSet.SegmentCommon == 0 && channelSet.RiceCodeFlag[index] != 0 && _Bits.ReadBit() != 0
						? (int)_Bits.ReadBits(channelSet.AllocationBitCount) + 1 : 0;
				}
				for (var index = 0; index < codingChannels; index++)
				{
					if (segment == 0)
					{
						channelSet.AllocationPartA[index] = (int)_Bits.ReadBits(channelSet.AllocationBitCount);
						if (channelSet.RiceCodeFlag[index] == 0 && channelSet.AllocationPartA[index] != 0) channelSet.AllocationPartA[index]++;
						channelSet.SamplesPartA[index] = channelSet.SegmentCommon == 0 ? band.AdaptivePredictionOrder[index] : band.HighestPredictionOrder;
					} else channelSet.AllocationPartA[index] = channelSet.SamplesPartA[index] = 0;
					channelSet.AllocationPartB[index] = (int)_Bits.ReadBits(channelSet.AllocationBitCount);
					if (channelSet.RiceCodeFlag[index] == 0 && channelSet.AllocationPartB[index] != 0) channelSet.AllocationPartB[index]++;
				}
			}
			for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
			{
				var codingIndex = channelSet.SegmentCommon != 0 ? 0 : channel;
				var partAOffset = DecimationHistory + segment * _SegmentSamples;
				var partBOffset = partAOffset + channelSet.SamplesPartA[codingIndex];
				var partBSamples = _SegmentSamples - channelSet.SamplesPartA[codingIndex];
				var samples = band.MostSignificantSamples[channel];
				if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
				if (channelSet.RiceCodeFlag[codingIndex] == 0)
				{
					ReadLinearArray(samples, partAOffset, channelSet.SamplesPartA[codingIndex], channelSet.AllocationPartA[codingIndex]);
					ReadLinearArray(samples, partBOffset, partBSamples, channelSet.AllocationPartB[codingIndex]);
				} else
				{
					var result = ReadRiceArray(samples, partAOffset, channelSet.SamplesPartA[codingIndex], channelSet.AllocationPartA[codingIndex]);
					if (result < 0) return result;
					if (channelSet.HybridLinearAllocation[codingIndex] != 0)
					{
						var isolatedSamples = (int)_Bits.ReadBits(_SegmentSamplesLog2);
						Array.Clear(samples, partBOffset, partBSamples);
						for (var index = 0; index < isolatedSamples; index++)
						{
							var location = (int)_Bits.ReadBits(_SegmentSamplesLog2);
							if (location >= partBSamples) return FfmpegError.InvalidData;
							samples[partBOffset + location] = -1;
						}
						for (var index = 0; index < partBSamples; index++) samples[partBOffset + index] = samples[partBOffset + index] != 0
							? GetLinear(channelSet.HybridLinearAllocation[codingIndex]) : GetRice(channelSet.AllocationPartB[codingIndex]);
					} else
					{
						result = ReadRiceArray(samples, partBOffset, partBSamples, channelSet.AllocationPartB[codingIndex]);
						if (result < 0) return result;
					}
				}
			}
			if (segment == 0 && bandIndex == 1)
			{
				var bitCount = (int)_Bits.ReadBits(5) + 1;
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
					for (var index = 1; index < DecimationHistory; index++) channelSet.DecimationHistory[channel, index] = _Bits.ReadSignedBits(bitCount);
			}
			if (band.LeastSignificantBitSectionSize != 0)
			{
				var result = Seek(bandDataEnd - band.LeastSignificantBitSectionSize * 8);
				if (result < 0) return result;
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
				{
					if (band.ScalableLeastSignificantBits[channel] == 0) continue;
					var samples = band.LeastSignificantSamples[channel];
					var sampleOffset = segment * _SegmentSamples;
					for (var index = 0; index < _SegmentSamples; index++) samples[sampleOffset + index] = (int)_Bits.ReadBits(band.ScalableLeastSignificantBits[channel]);
				}
			}
			return Seek(bandDataEnd);
		}

		private int ParseBandData()
		{
			var navigationPosition = 0;
			var bitPosition = _Bits.Position;
			for (var band = 0; band < _NumberOfFrequencyBands; band++)
				for (var segment = 0; segment < _NumberOfFrameSegments; segment++)
					for (var channelSet = 0; channelSet < _NumberOfChannelSets; channelSet++)
					{
						var current = _ChannelSets[channelSet];
						if (current.NumberOfFrequencyBands > band)
						{
							bitPosition += _Navigation[navigationPosition] * 8;
							if (bitPosition > _Bits.SizeInBits) return FfmpegError.InvalidData;
							if (channelSet < _ActiveChannelSets)
							{
								var result = ParseChannelSetBandData(current, band, segment, bitPosition);
								if (result < 0) ClearBandData(current, band, segment);
							}
							_Bits.SkipBits(bitPosition - _Bits.Position);
						}
						navigationPosition++;
					}
			return 0;
		}

		/// <summary>
		/// Reconstructs XLL predictors and scalable bits, assembles high-rate bands, and emits FFmpeg-planar S16 or S32 samples.
		/// </summary>
		public int Filter(Span<byte> output, DcaCoreDecoder core, bool recovery, out AudioFrameInfo frame)
		{
			frame = default;
			if (recovery)
			{
				for (var channelSetIndex = 0; channelSetIndex < _NumberOfChannelSets; channelSetIndex++)
				{
					var channelSet = _ChannelSets[channelSetIndex];
					if (channelSetIndex < _ActiveChannelSets) ForceLossyOutput(channelSet, core);
					if (channelSet.PrimaryChannelSet == 0) channelSet.DownmixEmbedded = 0;
				}
				_ScalableLeastSignificantBits = 0;
				_FixedLeastSignificantBitWidth = 0;
			}
			_OutputMask = 0;
			for (var channelSetIndex = 0; channelSetIndex < _ActiveChannelSets; channelSetIndex++)
			{
				var channelSet = _ChannelSets[channelSetIndex];
				FilterBandData(channelSet, 0);
				if (channelSet.ResidualEncode != (1 << channelSet.NumberOfChannels) - 1)
				{
					var result = CombineResidualFrame(channelSet, core);
					if (result < 0) return result;
				}
				if (_ScalableLeastSignificantBits != 0) AssembleMostAndLeastSignificantBits(channelSet, 0);
				if (channelSet.NumberOfFrequencyBands > 1)
				{
					FilterBandData(channelSet, 1);
					AssembleMostAndLeastSignificantBits(channelSet, 1);
				}
				_OutputMask |= channelSet.ChannelMask;
			}
			for (var index = 1; index < _NumberOfChannelSets; index++)
			{
				var channelSet = _ChannelSets[index];
				if (!IsHierarchicalDownmixChannelSet(channelSet)) continue;
				if (index >= _ActiveChannelSets)
				{
					for (var band = 0; band < channelSet.NumberOfFrequencyBands; band++) if (channelSet.Bands[band].DownmixEmbedded != 0) ScaleDownmix(channelSet, band);
					break;
				}
				for (var band = 0; band < channelSet.NumberOfFrequencyBands; band++) if (channelSet.Bands[band].DownmixEmbedded != 0) UndoDownmix(channelSet, band);
			}
			if (_NumberOfFrequencyBands > 1)
				for (var index = 0; index < _ActiveChannelSets; index++) AssembleFrequencyBands(_ChannelSets[index]);

			var channelCount = BuildChannelRemap(_OutputMask);
			if (channelCount == 0) return FfmpegError.InvalidArgument;
			var primary = _ChannelSets[0];
			var sampleCount = NumberOfSamples;
			var bytesPerSample = primary.StorageBitResolution == 16 ? 2 : 4;
			var requiredBytes = checked(sampleCount * channelCount * bytesPerSample);
			if (output.Length < requiredBytes) return FfmpegError.InvalidArgument;
			if (primary.StorageBitResolution == 16)
			{
				var shift = 16 - primary.PcmBitResolution;
				var destination = MemoryMarshal.Cast<byte, short>(output.Slice(0, requiredBytes));
				for (var channel = 0; channel < channelCount; channel++)
				{
					var samples = _OutputSamples[_ChannelRemap[channel]];
					var sampleOffset = _OutputSampleOffsets[_ChannelRemap[channel]];
					for (var index = 0; index < sampleCount; index++) destination[channel * sampleCount + index] = (short)Math.Clamp(unchecked(samples[sampleOffset + index] * (int)(1U << shift)), short.MinValue, short.MaxValue);
				}
				var planeSize = sampleCount * sizeof(short);
				frame = new AudioFrameInfo(sampleCount, channelCount, AudioSampleFormat.Signed16Planar, channelCount, planeSize, requiredBytes);
			} else if (primary.StorageBitResolution == 20 || primary.StorageBitResolution == 24)
			{
				var shift = 24 - primary.PcmBitResolution;
				var destination = MemoryMarshal.Cast<byte, int>(output.Slice(0, requiredBytes));
				for (var channel = 0; channel < channelCount; channel++)
				{
					var samples = _OutputSamples[_ChannelRemap[channel]];
					var sampleOffset = _OutputSampleOffsets[_ChannelRemap[channel]];
					for (var index = 0; index < sampleCount; index++) destination[channel * sampleCount + index] = DcaMath.Clip23(unchecked(samples[sampleOffset + index] * (int)(1U << shift))) * 256;
				}
				var planeSize = sampleCount * sizeof(int);
				frame = new AudioFrameInfo(sampleCount, channelCount, AudioSampleFormat.Signed32Planar, channelCount, planeSize, requiredBytes);
			} else return FfmpegError.InvalidArgument;
			return 0;
		}

		private void ForceLossyOutput(DcaXllChannelSet channelSet, DcaCoreDecoder core)
		{
			for (var band = 0; band < channelSet.NumberOfFrequencyBands; band++) ClearBandData(channelSet, band, -1);
			for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
			{
				if ((channelSet.ResidualEncode & (1 << channel)) == 0) continue;
				if (core.GetFixedSpeakerSamples(channelSet.ChannelRemap[channel]) == null) continue;
				channelSet.ResidualEncode &= ~(1 << channel);
			}
		}

		/// <summary>
		/// Applies adaptive/fixed prediction and pair decorrelation in the same nested loop order as FFmpeg.
		/// </summary>
		private void FilterBandData(DcaXllChannelSet channelSet, int bandIndex)
		{
			var band = channelSet.Bands[bandIndex];
			for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
			{
				var samples = band.MostSignificantSamples[channel];
				var order = band.AdaptivePredictionOrder[channel];
				if (order > 0)
				{
					var coefficients = _PredictionCoefficients;
					for (var index = 0; index < order; index++)
					{
						var reflection = band.AdaptiveReflectionCoefficient[channel, index];
						for (var pair = 0; pair < (index + 1) / 2; pair++)
						{
							var first = coefficients[pair];
							var second = coefficients[index - pair - 1];
							coefficients[pair] = first + DcaMath.Multiply(reflection, second, 16);
							coefficients[index - pair - 1] = second + DcaMath.Multiply(reflection, first, 16);
						}
						coefficients[index] = reflection;
					}
					for (var index = 0; index < _FrameSamples - order; index++)
					{
						long error = 0;
						for (var coefficient = 0; coefficient < order; coefficient++) error += (long)samples[DecimationHistory + index + coefficient] * coefficients[order - coefficient - 1];
						var position = DecimationHistory + index + order;
						samples[position] = unchecked(samples[position] - DcaMath.Clip23(DcaMath.Normalize(error, 16)));
					}
				} else
				{
					for (var pass = 0; pass < band.FixedPredictionOrder[channel]; pass++)
						for (var index = 1; index < _FrameSamples; index++) samples[DecimationHistory + index] = unchecked(samples[DecimationHistory + index] + samples[DecimationHistory + index - 1]);
				}
			}
			if (band.DecorrelationEnabled != 0)
			{
				for (var pair = 0; pair < channelSet.NumberOfChannels / 2; pair++)
				{
					var coefficient = band.DecorrelationCoefficient[pair];
					if (coefficient != 0) Decorrelate(band.MostSignificantSamples[pair * 2 + 1], band.MostSignificantSamples[pair * 2], coefficient, _FrameSamples);
				}
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++) _TemporarySampleReferences[channel] = band.MostSignificantSamples[channel];
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++) band.MostSignificantSamples[band.OriginalOrder[channel]] = _TemporarySampleReferences[channel];
			}
			if (channelSet.NumberOfFrequencyBands == 1)
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
				{
					_OutputSamples[channelSet.ChannelRemap[channel]] = band.MostSignificantSamples[channel];
					_OutputSampleOffsets[channelSet.ChannelRemap[channel]] = DecimationHistory;
				}
		}

		private int GetLeastSignificantBitWidth(DcaXllChannelSet channelSet, int bandIndex, int channel)
		{
			var band = channelSet.Bands[bandIndex];
			var adjustment = band.BitWidthAdjustment[channel];
			var shift = band.ScalableLeastSignificantBits[channel];
			if (_FixedLeastSignificantBitWidth != 0) shift = _FixedLeastSignificantBitWidth;
			else if (shift != 0 && adjustment != 0) shift += adjustment - 1;
			else shift += adjustment;
			return shift;
		}

		private void AssembleMostAndLeastSignificantBits(DcaXllChannelSet channelSet, int bandIndex)
		{
			var band = channelSet.Bands[bandIndex];
			for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
			{
				var shift = GetLeastSignificantBitWidth(channelSet, bandIndex, channel);
				if (shift == 0) continue;
				var mostSignificant = band.MostSignificantSamples[channel];
				if (band.ScalableLeastSignificantBits[channel] != 0)
				{
					var leastSignificant = band.LeastSignificantSamples[channel];
					var adjustment = band.BitWidthAdjustment[channel];
					for (var index = 0; index < _FrameSamples; index++) mostSignificant[DecimationHistory + index] = unchecked(mostSignificant[DecimationHistory + index] * (int)(1U << shift) + (leastSignificant[index] << adjustment));
				} else
					for (var index = 0; index < _FrameSamples; index++) mostSignificant[DecimationHistory + index] = unchecked(mostSignificant[DecimationHistory + index] * (int)(1U << shift));
			}
		}

		private void AssembleFrequencyBands(DcaXllChannelSet channelSet)
		{
			for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
			{
				var low = channelSet.Bands[0].MostSignificantSamples[channel];
				var high = channelSet.Bands[1].MostSignificantSamples[channel];
				for (var index = 0; index < DecimationHistory; index++) low[index] = channelSet.DecimationHistory[channel, index];
				Filter22(low, DecimationHistory, high, DecimationHistory, DcaTables.XllBandCoeff[0], _FrameSamples);
				Filter22(high, DecimationHistory, low, DecimationHistory, DcaTables.XllBandCoeff[1], _FrameSamples);
				Filter22(low, DecimationHistory, high, DecimationHistory, DcaTables.XllBandCoeff[2], _FrameSamples);
				Filter22(high, DecimationHistory, low, DecimationHistory, DcaTables.XllBandCoeff[3], _FrameSamples);
				var lowOffset = DecimationHistory;
				for (var index = 0; index < 8; index++, lowOffset--)
				{
					Filter23(low, lowOffset, high, DecimationHistory, DcaTables.XllBandCoeff[index + 4], _FrameSamples);
					Filter23(high, DecimationHistory, low, lowOffset, DcaTables.XllBandCoeff[index + 12], _FrameSamples);
					Filter23(low, lowOffset, high, DecimationHistory, DcaTables.XllBandCoeff[index + 4], _FrameSamples);
				}
				var assembled = channelSet.AssembledSamples[channel];
				for (var index = 0; index < _FrameSamples; index++)
				{
					assembled[index * 2] = high[DecimationHistory + index];
					assembled[index * 2 + 1] = low[lowOffset + 1 + index];
				}
				_OutputSamples[channelSet.ChannelRemap[channel]] = assembled;
				_OutputSampleOffsets[channelSet.ChannelRemap[channel]] = 0;
			}
		}

		private int CombineResidualFrame(DcaXllChannelSet channelSet, DcaCoreDecoder core)
		{
			if (core == null) return FfmpegError.InvalidArgument;
			if (channelSet.Frequency != core.FixedOutputRate || _FrameSamples != core.FixedNumberOfSamples) return FfmpegError.InvalidData;
			var output = FindNextHierarchicalDownmixChannelSet(Array.IndexOf(_ChannelSets, channelSet));
			for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
			{
				if ((channelSet.ResidualEncode & (1 << channel)) != 0) continue;
				var source = core.GetFixedSpeakerSamples(channelSet.ChannelRemap[channel]);
				if (source == null) return FfmpegError.InvalidData;
				var shift = 24 - channelSet.PcmBitResolution + GetLeastSignificantBitWidth(channelSet, 0, channel);
				if (shift > 24) return FfmpegError.InvalidData;
				var round = shift > 0 ? 1 << (shift - 1) : 0;
				var destination = channelSet.Bands[0].MostSignificantSamples[channel];
				if (output != null)
				{
					var inverseScale = output.DownmixScaleInverse[channelSet.HierarchicalOffset + channel];
					for (var index = 0; index < _FrameSamples; index++) destination[DecimationHistory + index] = unchecked(destination[DecimationHistory + index] +
						DcaMath.Clip23((DcaMath.Multiply(source[index], inverseScale, 16) + round) >> shift));
				} else
					for (var index = 0; index < _FrameSamples; index++) destination[DecimationHistory + index] = unchecked(destination[DecimationHistory + index] + ((source[index] + round) >> shift));
			}
			return 0;
		}

		private void UndoDownmix(DcaXllChannelSet output, int bandIndex)
		{
			var coefficientPosition = 0;
			var channels = 0;
			for (var index = 0; index < _ActiveChannelSets; index++)
			{
				var input = _ChannelSets[index];
				if (input.HierarchicalChannelSet == 0) continue;
				for (var channel = 0; channel < input.NumberOfChannels; channel++)
					for (var outputChannel = 0; outputChannel < output.NumberOfChannels; outputChannel++)
					{
						var coefficient = output.DownmixCoefficient[coefficientPosition++];
						if (coefficient != 0)
						{
							DownmixSubtract(input.Bands[bandIndex].MostSignificantSamples[channel], output.Bands[bandIndex].MostSignificantSamples[outputChannel], coefficient, _FrameSamples, DecimationHistory);
							if (bandIndex != 0)
								for (var history = 0; history < DecimationHistory; history++) input.DecimationHistory[channel, history] = unchecked(input.DecimationHistory[channel, history] - DcaMath.Multiply(output.DecimationHistory[outputChannel, history], coefficient, 15));
						}
					}
				channels += input.NumberOfChannels;
				if (channels >= output.HierarchicalOffset) break;
			}
		}

		private void ScaleDownmix(DcaXllChannelSet output, int bandIndex)
		{
			var channels = 0;
			for (var index = 0; index < _ActiveChannelSets; index++)
			{
				var input = _ChannelSets[index];
				if (input.HierarchicalChannelSet == 0) continue;
				for (var channel = 0; channel < input.NumberOfChannels; channel++)
				{
					var scale = output.DownmixScale[channels++];
					if (scale != 1 << 15)
					{
						ScaleSamples(input.Bands[bandIndex].MostSignificantSamples[channel], scale, _FrameSamples, DecimationHistory);
						if (bandIndex != 0)
							for (var history = 0; history < DecimationHistory; history++) input.DecimationHistory[channel, history] = DcaMath.Multiply(input.DecimationHistory[channel, history], scale, 15);
					}
				}
				if (channels >= output.HierarchicalOffset) break;
			}
		}

		private int ParseWithoutPartialBitstream(byte[] data, int offset, int size, DcaExssAsset asset)
		{
			var result = ParseFrame(data, offset, size, asset);
			if (result == FfmpegError.TryAgain && asset.XllSyncPresent != 0 && asset.XllSyncOffset < size)
			{
				offset += asset.XllSyncOffset;
				size -= asset.XllSyncOffset;
				if (asset.XllDelayFrames > 0)
				{
					result = CopyToPartialBitstream(data, offset, size, asset.XllDelayFrames);
					return result < 0 ? result : FfmpegError.TryAgain;
				}
				result = ParseFrame(data, offset, size, asset);
			}
			if (result < 0) return result;
			if (_FrameSize > size) return FfmpegError.InvalidArgument;
			if (_FrameSize < size) return CopyToPartialBitstream(data, offset + _FrameSize, size - _FrameSize, 0);
			return 0;
		}

		private int ParsePartialBitstream(byte[] data, int offset, int size, DcaExssAsset asset)
		{
			if (size > PbrBufferMaximum - _PbrLength) { ClearPartialBitstream(); return FfmpegError.NoSpace; }
			data.AsSpan(offset, size).CopyTo(_PbrBuffer.AsSpan(_PbrLength));
			_PbrLength += size;
			Array.Clear(_PbrBuffer, _PbrLength, 64);
			if (_PbrDelay > 0 && --_PbrDelay != 0) return FfmpegError.TryAgain;
			var result = ParseFrame(_PbrBuffer, 0, _PbrLength, asset);
			if (result < 0 || _FrameSize > _PbrLength) { ClearPartialBitstream(); return result < 0 ? result : FfmpegError.InvalidArgument; }
			if (_FrameSize == _PbrLength) ClearPartialBitstream();
			else { _PbrLength -= _FrameSize; _PbrBuffer.AsSpan(_FrameSize, _PbrLength).CopyTo(_PbrBuffer); }
			return 0;
		}

		private int CopyToPartialBitstream(byte[] data, int offset, int size, int delay)
		{
			if (size > PbrBufferMaximum) return FfmpegError.NoSpace;
			data.AsSpan(offset, size).CopyTo(_PbrBuffer);
			Array.Clear(_PbrBuffer, size, 64);
			_PbrLength = size;
			_PbrDelay = delay;
			return 0;
		}

		private void ClearPartialBitstream() { _PbrLength = _PbrDelay = 0; }

		private void ClearBandData(DcaXllChannelSet channelSet, int bandIndex, int segment)
		{
			var band = channelSet.Bands[bandIndex];
			var offset = segment < 0 ? 0 : segment * _SegmentSamples;
			var count = segment < 0 ? _FrameSamples : _SegmentSamples;
			for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
			{
				Array.Clear(band.MostSignificantSamples[channel], DecimationHistory + offset, count);
				if (band.LeastSignificantBitSectionSize != 0) Array.Clear(band.LeastSignificantSamples[channel], offset, count);
			}
			if (segment <= 0 && bandIndex != 0) Array.Clear(channelSet.DecimationHistory, 0, channelSet.DecimationHistory.Length);
			if (segment < 0) { Array.Clear(band.ScalableLeastSignificantBits, 0, band.ScalableLeastSignificantBits.Length); Array.Clear(band.BitWidthAdjustment, 0, band.BitWidthAdjustment.Length); }
		}

		private int GetLinear(int bitCount)
		{
			var value = _Bits.ReadBitsLong(bitCount);
			return unchecked((int)((value >> 1) ^ (uint)-(int)(value & 1)));
		}

		private int GetRice(int parameter)
		{
			var value = GetRiceUnsigned(parameter);
			return unchecked((int)((value >> 1) ^ (uint)-(int)(value & 1)));
		}

		private uint GetRiceUnsigned(int parameter)
		{
			var quotient = 0U;
			while (_Bits.BitsLeft > 0 && _Bits.ReadBit() != 1) quotient++;
			return quotient << parameter | _Bits.ReadBitsLong(parameter);
		}

		private void ReadLinearArray(int[] destination, int offset, int count, int bitCount)
		{
			if (bitCount == 0) Array.Clear(destination, offset, count);
			else for (var index = 0; index < count; index++) destination[offset + index] = GetLinear(bitCount);
		}

		private int ReadRiceArray(int[] destination, int offset, int count, int parameter)
		{
			var index = 0;
			for (; index < count && _Bits.BitsLeft > parameter; index++) destination[offset + index] = GetRice(parameter);
			return index < count ? FfmpegError.InvalidData : 0;
		}

		private int Seek(int position)
		{
			if (position < _Bits.Position || position > _Bits.SizeInBits) return FfmpegError.InvalidData;
			_Bits.SkipBits(position - _Bits.Position);
			return 0;
		}

		private static bool IsHierarchicalDownmixChannelSet(DcaXllChannelSet channelSet) => channelSet.PrimaryChannelSet == 0 && channelSet.DownmixEmbedded != 0 && channelSet.HierarchicalChannelSet != 0;

		private DcaXllChannelSet FindNextHierarchicalDownmixChannelSet(int index)
		{
			if (_ChannelSets[index].HierarchicalChannelSet != 0)
				for (index++; index < _NumberOfChannelSets; index++) if (IsHierarchicalDownmixChannelSet(_ChannelSets[index])) return _ChannelSets[index];
			return null;
		}

		private static void PrescaleDownmix(DcaXllChannelSet channelSet, DcaXllChannelSet output)
		{
			var coefficientPosition = 0;
			for (var index = 0; index < channelSet.HierarchicalOffset; index++)
			{
				var scale = output.DownmixScale[index];
				var inverseScale = output.DownmixScaleInverse[index];
				channelSet.DownmixScale[index] = DcaMath.Multiply(channelSet.DownmixScale[index], scale, 15);
				channelSet.DownmixScaleInverse[index] = DcaMath.Multiply(channelSet.DownmixScaleInverse[index], inverseScale, 16);
				for (var channel = 0; channel < channelSet.NumberOfChannels; channel++)
				{
					var coefficient = DcaMath.Multiply(channelSet.DownmixCoefficient[coefficientPosition], inverseScale, 16);
					channelSet.DownmixCoefficient[coefficientPosition++] = DcaMath.Multiply(coefficient, output.DownmixScale[channelSet.HierarchicalOffset + channel], 15);
				}
			}
		}

		private int BuildChannelRemap(int mask)
		{
			var map = mask == 0x6001f || mask == 0x6003f ? DcaTables.DcaToWaveWide : DcaTables.DcaToWaveNormal;
			Span<int> waveMap = stackalloc int[18];
			var waveMask = 0;
			for (var dcaChannel = 0; dcaChannel < 28; dcaChannel++)
			{
				if ((mask & (1 << dcaChannel)) == 0) continue;
				var waveChannel = map[dcaChannel];
				if ((waveMask & (1 << waveChannel)) != 0) continue;
				waveMap[waveChannel] = dcaChannel;
				waveMask |= 1 << waveChannel;
			}
			var count = 0;
			for (var waveChannel = 0; waveChannel < 18; waveChannel++) if ((waveMask & (1 << waveChannel)) != 0) _ChannelRemap[count++] = waveMap[waveChannel];
			return count;
		}

		private static void Decorrelate(int[] destination, int[] source, int coefficient, int length)
		{
			for (var index = 0; index < length; index++)
			{
				var product = unchecked((uint)source[DecimationHistory + index] * (uint)coefficient + 4U);
				var value = unchecked((int)product) >> 3;
				destination[DecimationHistory + index] = unchecked(destination[DecimationHistory + index] + value);
			}
		}

		private static void Filter22(int[] destination, int destinationOffset, int[] source, int sourceOffset, int coefficient, int length)
		{
			for (var index = 0; index < length; index++) destination[destinationOffset + index] = unchecked(destination[destinationOffset + index] - DcaMath.Multiply(source[sourceOffset + index], coefficient, 22));
		}

		private static void Filter23(int[] destination, int destinationOffset, int[] source, int sourceOffset, int coefficient, int length)
		{
			for (var index = 0; index < length; index++) destination[destinationOffset + index] = unchecked(destination[destinationOffset + index] - DcaMath.Multiply(source[sourceOffset + index], coefficient, 23));
		}

		private static void DownmixSubtract(int[] destination, int[] source, int coefficient, int length, int offset)
		{
			for (var index = 0; index < length; index++) destination[offset + index] = unchecked(destination[offset + index] - DcaMath.Multiply(source[offset + index], coefficient, 15));
		}

		private static void ScaleSamples(int[] samples, int scale, int length, int offset)
		{
			for (var index = 0; index < length; index++) samples[offset + index] = DcaMath.Multiply(samples[offset + index], scale, 15);
		}

		private static int CeilingLog2(int value) => value <= 1 ? 0 : BitOperations.Log2((uint)(value - 1)) + 1;

		private static DcaXllChannelSet[] CreateChannelSets()
		{
			var result = new DcaXllChannelSet[ChannelSetsMaximum];
			for (var index = 0; index < result.Length; index++) result[index] = new DcaXllChannelSet();
			return result;
		}
	}

	/// <summary>
	/// Mirrors FFmpeg's per-channel-set XLL coding, hierarchy, residual, and sample-buffer state.
	/// </summary>
	internal sealed class DcaXllChannelSet
	{
		public int NumberOfChannels, ResidualEncode, PcmBitResolution, StorageBitResolution, Frequency;
		public int PrimaryChannelSet, DownmixCoefficientsPresent, DownmixEmbedded, DownmixType, HierarchicalChannelSet, HierarchicalOffset, ChannelMask;
		public readonly int[] DownmixCoefficient = new int[128];
		public readonly int[] DownmixScale = new int[16];
		public readonly int[] DownmixScaleInverse = new int[16];
		public readonly int[] ChannelRemap = new int[8];
		public int NumberOfFrequencyBands, AllocationBitCount, SegmentCommon;
		public readonly int[] RiceCodeFlag = new int[8];
		public readonly int[] HybridLinearAllocation = new int[8];
		public readonly int[] AllocationPartA = new int[8];
		public readonly int[] AllocationPartB = new int[8];
		public readonly int[] SamplesPartA = new int[8];
		public readonly int[,] DecimationHistory = new int[8, 8];
		public readonly DcaXllBand[] Bands = { new DcaXllBand(), new DcaXllBand() };
		public readonly int[][] AssembledSamples = CreatePlanes(8, 131072);

		internal static int[][] CreatePlanes(int count, int length)
		{
			var result = new int[count][];
			for (var index = 0; index < count; index++) result[index] = new int[length];
			return result;
		}
	}

	/// <summary>
	/// Mirrors one FFmpeg XLL frequency band's decorrelation, prediction, scalable-LSB, and sample-buffer state.
	/// </summary>
	internal sealed class DcaXllBand
	{
		public int DecorrelationEnabled;
		public readonly int[] OriginalOrder = new int[8];
		public readonly int[] DecorrelationCoefficient = new int[4];
		public readonly int[] AdaptivePredictionOrder = new int[8];
		public int HighestPredictionOrder;
		public readonly int[] FixedPredictionOrder = new int[8];
		public readonly int[,] AdaptiveReflectionCoefficient = new int[8, 16];
		public int DownmixEmbedded, LeastSignificantBitSectionSize;
		public readonly int[] ScalableLeastSignificantBits = new int[8];
		public readonly int[] BitWidthAdjustment = new int[8];
		public readonly int[][] MostSignificantSamples = DcaXllChannelSet.CreatePlanes(8, 65544);
		public readonly int[][] LeastSignificantSamples = DcaXllChannelSet.CreatePlanes(8, 65536);
	}
}
