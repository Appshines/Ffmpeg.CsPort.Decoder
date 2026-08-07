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
using System.Numerics;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Codecs.Wma
{
	/// <summary>
	/// Ports FFmpeg's WMA Lossless decoder, including packet reservoirs, tiled frames, Golomb-style
	/// residues, CDLMS/MCLMS prediction, inter-channel decorrelation, and 16/24-bit planar output.
	/// </summary>
	public sealed class WmaLosslessDecoder
	{
		private const int MaximumChannels = 8;
		private const int MaximumSubframes = 32;
		private const int MaximumFrameDataSize = 32768;
		private const int MaximumOrder = 256;
		private const int MaximumBlockSize = 1 << 14;
		private readonly BitReader packetReader = new BitReader();
		private readonly BitReader frameReader = new BitReader();
		private readonly int sampleRate;
		private readonly int channels;
		private readonly int blockAlign;
		private readonly int bitsPerSample;
		private readonly uint decodeFlags;
		private readonly bool lengthPrefix;
		private readonly bool dynamicRangeCompression;
		private readonly bool versionThreeRealtime;
		private readonly int samplesPerFrame;
		private readonly int log2FrameSize;
		private readonly int maximumSubframes;
		private readonly int minimumSamplesPerSubframe;
		private readonly byte[] frameData;
		private readonly WmaLosslessChannel[] channel = new WmaLosslessChannel[MaximumChannels];
		private readonly CdlmsState[][] cdlms = CreateCdlmsStates();
		private readonly int[] cdlmsTotal = new int[MaximumChannels];
		private readonly int[] currentChannelIndexes = new int[MaximumChannels];
		private readonly ushort[] tileSamples = new ushort[MaximumChannels];
		private readonly bool[] containsSubframe = new bool[MaximumChannels];
		private readonly int[][] residues = CreateIntPlanes(MaximumChannels, MaximumBlockSize);
		private readonly short[] acFilterCoefficients = new short[16];
		private readonly int[][] acFilterPreviousValues = CreateIntPlanes(MaximumChannels, 16);
		private readonly short[] mclmsCoefficients = new short[MaximumChannels * MaximumChannels * 32];
		private readonly short[] mclmsCurrentCoefficients = new short[MaximumChannels * MaximumChannels];
		private readonly int[] mclmsPreviousValues = new int[MaximumChannels * 2 * 32];
		private readonly int[] mclmsUpdates = new int[MaximumChannels * 2 * 32];
		private readonly int[] mclmsPrediction = new int[MaximumChannels];
		private readonly bool[] channelCoded = new bool[MaximumChannels];
		private readonly int[] updateSpeed = new int[MaximumChannels];
		private readonly bool[] transient = new bool[MaximumChannels];
		private readonly int[] transientPosition = new int[MaximumChannels];
		private readonly uint[] averageSum = new uint[MaximumChannels];
		private readonly int[][] lpcCoefficients = CreateIntPlanes(MaximumChannels, 40);
		private readonly int[][] frameSamples;
		private readonly int[][] decodedSamples;
		private readonly int[] frameWriteOffsets = new int[MaximumChannels];
		private int numberOfSavedBits;
		private int frameOffset;
		private int packetSequenceNumber;
		private bool packetLoss = true;
		private bool endOfFileDone;
		private int outputSamples;
		private uint frameNumber;
		private bool arithmeticCoding;
		private bool useAcFilter;
		private bool useInterChannelDecorrelation;
		private bool useMclms;
		private bool useLpc;
		private int acFilterOrder;
		private int acFilterScaling;
		private int mclmsOrder;
		private int mclmsScaling;
		private int mclmsRecent;
		private int movingAverageScaling;
		private int quantizationStepSize;
		private bool seekableTile;
		private int channelsForCurrentSubframe;

		private WmaLosslessDecoder(int sampleRate, int channels, int blockAlign, byte[] extraData)
		{
			this.sampleRate = sampleRate;
			this.blockAlign = blockAlign;
			bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(extraData);
			decodeFlags = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(14));
			var channelMask = BinaryPrimitives.ReadUInt32LittleEndian(extraData.AsSpan(2));
			this.channels = channelMask != 0 ? BitOperations.PopCount(channelMask) : channels;
			frameData = new byte[MaximumFrameDataSize * this.channels + 64];
			log2FrameSize = FfmpegMath.Log2((uint)blockAlign) + 4;
			lengthPrefix = (decodeFlags & 0x40) != 0;
			dynamicRangeCompression = (decodeFlags & 0x80) != 0;
			versionThreeRealtime = (decodeFlags & 0x100) != 0;
			samplesPerFrame = 1 << GetFrameLengthBits(sampleRate, decodeFlags);
			maximumSubframes = 1 << (int)((decodeFlags & 0x38) >> 3);
			minimumSamplesPerSubframe = samplesPerFrame / maximumSubframes;
			for (var index = 0; index < MaximumChannels; index++) channel[index] = new WmaLosslessChannel { PreviousBlockLength = samplesPerFrame };
			frameSamples = CreateIntPlanes(this.channels, samplesPerFrame);
			decodedSamples = CreateIntPlanes(this.channels, samplesPerFrame * MaximumSubframes);
		}

		public int Channels => channels;
		public int SampleRate => sampleRate;
		public int FrameLength => samplesPerFrame;
		public int MaximumOutputBytes => samplesPerFrame * MaximumSubframes * channels * (bitsPerSample == 16 ? 2 : 4);

		public static int Initialize(int sampleRate, int channels, long bitRate, int blockAlign, byte[] extraData, out WmaLosslessDecoder decoder)
		{
			decoder = null;
			if (sampleRate <= 0 || channels <= 0 || channels > MaximumChannels || bitRate <= 0 || blockAlign <= 0 || blockAlign > 1 << 21 ||
				extraData == null || extraData.Length < 18)
				return FfmpegError.InvalidArgument;
			var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(extraData);
			var mask = BinaryPrimitives.ReadUInt32LittleEndian(extraData.AsSpan(2));
			var codedChannels = mask != 0 ? BitOperations.PopCount(mask) : channels;
			var flags = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(14));
			var frameBits = GetFrameLengthBits(sampleRate, flags);
			var maximumSubframes = 1 << (int)((flags & 0x38) >> 3);
			if ((bitsPerSample != 16 && bitsPerSample != 24) || codedChannels <= 0 || codedChannels > MaximumChannels || codedChannels > channels ||
				frameBits > 14 || maximumSubframes > MaximumSubframes || (1 << frameBits) / maximumSubframes <= 0)
				return FfmpegError.InvalidData;
			decoder = new WmaLosslessDecoder(sampleRate, channels, blockAlign, extraData);
			return 0;
		}

		/// <summary>Consumes one ASF media object and emits every complete WMA Lossless frame in planar integer form.</summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packetLength == 0) return Drain(output, out frame);
			if (packet == null || packetOffset < 0 || packetLength <= 0 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			endOfFileDone = false;
			outputSamples = 0;
			var packetSize = Math.Min(blockAlign, packetLength);
			if (packetReader.Initialize(packet, packetOffset, packetSize * 8) < 0) return FfmpegError.InvalidData;
			var sequence = (int)packetReader.ReadBits(4);
			packetReader.SkipBits(1);
			packetReader.ReadBit();
			var previousFrameBits = (int)packetReader.ReadBits(log2FrameSize);
			if (!packetLoss && ((packetSequenceNumber + 1) & 15) != sequence) packetLoss = true;
			packetSequenceNumber = sequence;
			if (previousFrameBits > 0)
			{
				var remaining = packetReader.BitsLeft;
				if (previousFrameBits >= remaining) previousFrameBits = remaining;
				AppendSavedBits(packetReader, previousFrameBits);
				if (previousFrameBits < remaining && !packetLoss) DecodeSavedFrames();
			} else if (numberOfSavedBits - frameOffset != 0)
			{
				numberOfSavedBits = 0;
			}
			if (packetLoss)
			{
				numberOfSavedBits = 0;
				packetLoss = false;
			}

			if (lengthPrefix)
			{
				var moreFrames = true;
				while (moreFrames && packetReader.BitsLeft > log2FrameSize)
				{
					var frameSize = (int)packetReader.ShowBits(log2FrameSize);
					if (frameSize <= 0 || frameSize > packetReader.BitsLeft) break;
					SaveNewBits(packetReader, frameSize);
					moreFrames = DecodeOneFrame() > 0;
					if (packetLoss) break;
				}
			}
			if (!packetLoss && packetReader.BitsLeft > 0) SaveNewBits(packetReader, packetReader.BitsLeft);
			var write = WriteOutput(output, out frame);
			return write < 0 ? write : packetSize;
		}

		public int Drain(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (endOfFileDone) return 0;
			outputSamples = 0;
			if (numberOfSavedBits > frameReader.Position) DecodeSavedFrames();
			numberOfSavedBits = 0;
			endOfFileDone = true;
			return WriteOutput(output, out frame);
		}

		public void Flush()
		{
			packetLoss = true;
			numberOfSavedBits = 0;
			frameOffset = 0;
			cdlms[0][0].Order = 0;
			endOfFileDone = false;
		}

		private void DecodeSavedFrames()
		{
			var moreFrames = true;
			while (moreFrames && frameReader.Position < numberOfSavedBits && !packetLoss)
				moreFrames = DecodeOneFrame() > 0;
		}

		/// <summary>Decodes one complete lossless frame while retaining the source's partial-frame error behavior.</summary>
		private int DecodeOneFrame()
		{
			var frameSampleCount = samplesPerFrame;
			Array.Clear(frameWriteOffsets, 0, channels);
			var length = lengthPrefix ? (int)frameReader.ReadBits(log2FrameSize) : 0;
			var tileResult = DecodeTileHeader();
			if (tileResult < 0)
			{
				packetLoss = true;
				return tileResult;
			}
			if (dynamicRangeCompression) frameReader.SkipBits(8);
			if (frameReader.ReadBit() != 0)
			{
				if (frameReader.ReadBit() != 0) frameReader.SkipBits(FfmpegMath.Log2((uint)(samplesPerFrame * 2)));
				if (frameReader.ReadBit() != 0)
				{
					frameSampleCount -= (int)frameReader.ReadBits(FfmpegMath.Log2((uint)(samplesPerFrame * 2)));
					if (frameSampleCount <= 0) { packetLoss = true; return FfmpegError.InvalidData; }
				}
			}
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				channel[currentChannel].DecodedSamples = 0;
				channel[currentChannel].CurrentSubframe = 0;
			}
			var parsedAllSubframes = false;
			while (!parsedAllSubframes)
			{
				var decodedBeforeError = channel[0].DecodedSamples;
				var result = DecodeSubframe(out parsedAllSubframes);
				if (result < 0)
				{
					packetLoss = true;
					if (decodedBeforeError > 0) AppendFrame(decodedBeforeError);
					return 0;
				}
			}
			AppendFrame(frameSampleCount);
			if (lengthPrefix)
			{
				var consumed = frameReader.Position - frameOffset;
				if (length != consumed + 2) { packetLoss = true; return 0; }
				frameReader.SkipBits(length - consumed - 1);
			}
			var moreFrames = frameReader.ReadBit() != 0;
			frameNumber++;
			return moreFrames ? 1 : 0;
		}

		/// <summary>
		/// Decodes WMA Lossless tiling flags and builds each channel's ordered, non-overlapping subframe schedule.
		/// </summary>
		private int DecodeTileHeader()
		{
			Array.Clear(tileSamples, 0, channels);
			var channelsForSubframe = channels;
			var minimumChannelLength = 0;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++) channel[currentChannel].NumberOfSubframes = 0;
			var tileAligned = frameReader.ReadBit() != 0;
			var fixedLayout = maximumSubframes == 1 || tileAligned;
			do
			{
				var inUse = false;
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				{
					if (tileSamples[currentChannel] == minimumChannelLength)
						containsSubframe[currentChannel] = fixedLayout || channelsForSubframe == 1 ||
							minimumChannelLength == samplesPerFrame - minimumSamplesPerSubframe || frameReader.ReadBit() != 0;
					else containsSubframe[currentChannel] = false;
					inUse |= containsSubframe[currentChannel];
				}
				if (!inUse) return FfmpegError.InvalidData;
				var subframeLength = DecodeSubframeLength(minimumChannelLength);
				if (subframeLength <= 0) return FfmpegError.InvalidData;
				minimumChannelLength += subframeLength;
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				{
					var current = channel[currentChannel];
					if (containsSubframe[currentChannel])
					{
						if (current.NumberOfSubframes >= MaximumSubframes) return FfmpegError.InvalidData;
						current.SubframeLengths[current.NumberOfSubframes++] = (ushort)subframeLength;
						tileSamples[currentChannel] += (ushort)subframeLength;
						if (tileSamples[currentChannel] > samplesPerFrame) return FfmpegError.InvalidData;
					} else if (tileSamples[currentChannel] <= minimumChannelLength)
					{
						if (tileSamples[currentChannel] < minimumChannelLength)
						{
							channelsForSubframe = 0;
							minimumChannelLength = tileSamples[currentChannel];
						}
						channelsForSubframe++;
					}
				}
			} while (minimumChannelLength < samplesPerFrame);
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				var offset = 0;
				for (var index = 0; index < channel[currentChannel].NumberOfSubframes; index++)
				{
					channel[currentChannel].SubframeOffsets[index] = (ushort)offset;
					offset += channel[currentChannel].SubframeLengths[index];
				}
			}
			return 0;
		}

		private int DecodeSubframeLength(int offset)
		{
			if (offset == samplesPerFrame - minimumSamplesPerSubframe) return minimumSamplesPerSubframe;
			var bits = FfmpegMath.Log2((uint)(maximumSubframes - 1)) + 1;
			var ratio = (int)frameReader.ReadBits(bits);
			var length = minimumSamplesPerSubframe * (ratio + 1);
			return length < minimumSamplesPerSubframe || length > samplesPerFrame ? FfmpegError.InvalidData : length;
		}

		/// <summary>Decodes one lossless tile and applies the predictor stages in FFmpeg's original order.</summary>
		private int DecodeSubframe(out bool parsedAllSubframes)
		{
			parsedAllSubframes = false;
			var offset = samplesPerFrame;
			var subframeLength = samplesPerFrame;
			var totalSamples = samplesPerFrame * channels;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				if (offset > channel[currentChannel].DecodedSamples)
				{
					offset = channel[currentChannel].DecodedSamples;
					if (channel[currentChannel].CurrentSubframe >= channel[currentChannel].NumberOfSubframes) return FfmpegError.InvalidData;
					subframeLength = channel[currentChannel].SubframeLengths[channel[currentChannel].CurrentSubframe];
				}
			channelsForCurrentSubframe = 0;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				var current = channel[currentChannel];
				totalSamples -= current.DecodedSamples;
				if (offset == current.DecodedSamples && subframeLength == current.SubframeLengths[current.CurrentSubframe])
				{
					totalSamples -= current.SubframeLengths[current.CurrentSubframe];
					current.DecodedSamples += current.SubframeLengths[current.CurrentSubframe];
					currentChannelIndexes[channelsForCurrentSubframe++] = currentChannel;
				}
			}
			if (totalSamples == 0) parsedAllSubframes = true;

			seekableTile = frameReader.ReadBit() != 0;
			if (seekableTile)
			{
				ClearCodecBuffers();
				arithmeticCoding = frameReader.ReadBit() != 0;
				if (arithmeticCoding) return FfmpegError.PatchWelcome;
				useAcFilter = frameReader.ReadBit() != 0;
				useInterChannelDecorrelation = frameReader.ReadBit() != 0;
				useMclms = frameReader.ReadBit() != 0;
				if (useAcFilter) DecodeAcFilter();
				if (useMclms) DecodeMclms();
				var cdlmsResult = DecodeCdlms();
				if (cdlmsResult < 0) return cdlmsResult;
				movingAverageScaling = (int)frameReader.ReadBits(3);
				quantizationStepSize = (int)frameReader.ReadBits(8) + 1;
				ResetCodec();
			}
			var rawPcmTile = frameReader.ReadBit() != 0;
			if (!rawPcmTile && cdlms[0][0].Order == 0) return FfmpegError.InvalidData;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++) channelCoded[currentChannel] = true;
			if (!rawPcmTile)
			{
				for (var currentChannel = 0; currentChannel < channels; currentChannel++) channelCoded[currentChannel] = frameReader.ReadBit() != 0;
				if (versionThreeRealtime)
				{
					useLpc = frameReader.ReadBit() != 0;
					if (useLpc) DecodeLpc();
				} else useLpc = false;
			}
			if (frameReader.BitsLeft < 1) return FfmpegError.InvalidData;
			var paddingZeroes = frameReader.ReadBit() != 0 ? (int)frameReader.ReadBits(5) : 0;
			if (rawPcmTile)
			{
				var bits = bitsPerSample - paddingZeroes;
				if (bits <= 0) return FfmpegError.InvalidData;
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
					for (var sample = 0; sample < subframeLength; sample++) residues[currentChannel][sample] = (int)frameReader.ReadSignedBits64(bits);
			} else
			{
				if (bitsPerSample < paddingZeroes) return FfmpegError.InvalidData;
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				{
					if (channelCoded[currentChannel])
					{
						DecodeChannelResidues(currentChannel, subframeLength);
						if (seekableTile) UseHighUpdateSpeed(currentChannel); else UseNormalUpdateSpeed(currentChannel);
						RevertCdlms(currentChannel, 0, subframeLength, bitsPerSample > 16);
					} else Array.Clear(residues[currentChannel], 0, subframeLength);
				}
				if (useMclms) RevertMclms(subframeLength);
				if (useInterChannelDecorrelation) RevertInterChannelDecorrelation(subframeLength);
				if (useAcFilter) RevertAcFilter(subframeLength);
				if (quantizationStepSize != 1)
					for (var currentChannel = 0; currentChannel < channels; currentChannel++)
						for (var sample = 0; sample < subframeLength; sample++) residues[currentChannel][sample] = unchecked(residues[currentChannel][sample] * quantizationStepSize);
			}
			for (var index = 0; index < channelsForCurrentSubframe; index++)
			{
				var currentChannel = currentChannelIndexes[index];
				var length = channel[currentChannel].SubframeLengths[channel[currentChannel].CurrentSubframe];
				for (var sample = 0; sample < length; sample++)
				{
					if (bitsPerSample == 16)
						frameSamples[currentChannel][frameWriteOffsets[currentChannel]++] = unchecked((short)(unchecked((short)residues[currentChannel][sample]) * (1 << paddingZeroes)));
					else frameSamples[currentChannel][frameWriteOffsets[currentChannel]++] = unchecked(residues[currentChannel][sample] * (int)(256U << paddingZeroes));
				}
			}
			for (var index = 0; index < channelsForCurrentSubframe; index++)
			{
				var current = channel[currentChannelIndexes[index]];
				if (current.CurrentSubframe >= current.NumberOfSubframes) return FfmpegError.InvalidData;
				current.CurrentSubframe++;
			}
			return 0;
		}

		private void DecodeAcFilter()
		{
			acFilterOrder = (int)frameReader.ReadBits(4) + 1;
			acFilterScaling = (int)frameReader.ReadBits(4);
			for (var index = 0; index < acFilterOrder; index++) acFilterCoefficients[index] = (short)(frameReader.ReadBitsOrZero(acFilterScaling) + 1);
		}

		private void DecodeMclms()
		{
			mclmsOrder = ((int)frameReader.ReadBits(4) + 1) * 2;
			mclmsScaling = (int)frameReader.ReadBits(4);
			if (frameReader.ReadBit() == 0) return;
			var coefficientBits = FfmpegMath.Log2((uint)(mclmsScaling + 1));
			if (1 << coefficientBits < mclmsScaling + 1) coefficientBits++;
			var sentBits = (int)frameReader.ReadBitsOrZero(coefficientBits) + 2;
			for (var index = 0; index < mclmsOrder * channels * channels; index++) mclmsCoefficients[index] = (short)frameReader.ReadBits(sentBits);
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				for (var previousChannel = 0; previousChannel < currentChannel; previousChannel++)
					mclmsCurrentCoefficients[currentChannel * channels + previousChannel] = (short)frameReader.ReadBits(sentBits);
		}

		private int DecodeCdlms()
		{
			var sendCoefficients = frameReader.ReadBit() != 0;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				cdlmsTotal[currentChannel] = (int)frameReader.ReadBits(3) + 1;
				for (var stage = 0; stage < cdlmsTotal[currentChannel]; stage++)
				{
					cdlms[currentChannel][stage].Order = ((int)frameReader.ReadBits(7) + 1) * 8;
					if (cdlms[currentChannel][stage].Order > MaximumOrder) { cdlms[0][0].Order = 0; return FfmpegError.InvalidData; }
				}
				for (var stage = 0; stage < cdlmsTotal[currentChannel]; stage++) cdlms[currentChannel][stage].Scaling = (int)frameReader.ReadBits(4);
				if (sendCoefficients)
					for (var stage = 0; stage < cdlmsTotal[currentChannel]; stage++)
					{
						var state = cdlms[currentChannel][stage];
						var coefficientBits = FfmpegMath.Log2((uint)state.Order);
						if (1 << coefficientBits < state.Order) coefficientBits++;
						state.CoefficientsSent = (int)frameReader.ReadBits(coefficientBits) + 1;
						coefficientBits = FfmpegMath.Log2((uint)(state.Scaling + 1));
						if (1 << coefficientBits < state.Scaling + 1) coefficientBits++;
						state.BitsSent = (int)frameReader.ReadBitsOrZero(coefficientBits) + 2;
						var shiftLeft = 32 - state.BitsSent;
						var shiftRight = 32 - state.Scaling - 2;
						for (var index = 0; index < state.CoefficientsSent; index++)
							state.Coefficients[index] = unchecked((short)((frameReader.ReadBitsLong(state.BitsSent) << shiftLeft) >> shiftRight));
					}
				for (var stage = 0; stage < cdlmsTotal[currentChannel]; stage++)
					Array.Clear(cdlms[currentChannel][stage].Coefficients, cdlms[currentChannel][stage].Order, 8);
			}
			return 0;
		}

		/// <summary>
		/// Decodes WMA Lossless signed Rice residues while updating the adaptive mean and escape parameter.
		/// </summary>
		private int DecodeChannelResidues(int currentChannel, int tileSize)
		{
			var sample = 0;
			transient[currentChannel] = frameReader.ReadBit() != 0;
			if (transient[currentChannel])
			{
				transientPosition[currentChannel] = (int)frameReader.ReadBits(FfmpegMath.Log2((uint)tileSize));
				if (transientPosition[currentChannel] != 0) transient[currentChannel] = false;
				channel[currentChannel].TransientCounter = Math.Max(channel[currentChannel].TransientCounter, samplesPerFrame / 2);
			} else if (channel[currentChannel].TransientCounter != 0) transient[currentChannel] = true;
			if (seekableTile)
			{
				var averageMean = frameReader.ReadBits(bitsPerSample);
				averageSum[currentChannel] = averageMean << (movingAverageScaling + 1);
				residues[currentChannel][0] = useInterChannelDecorrelation ? frameReader.ReadSignedBits(bitsPerSample + 1) : frameReader.ReadSignedBits(bitsPerSample);
				sample++;
			}
			for (; sample < tileSize; sample++)
			{
				uint quotient = 0;
				while (frameReader.ReadBit() != 0)
				{
					quotient++;
					if (frameReader.BitsLeft <= 0) return -1;
				}
				if (quotient >= 32) quotient = unchecked(quotient + frameReader.ReadBitsLong((int)frameReader.ReadBits(5) + 1));
				var averageMean = (averageSum[currentChannel] + (1U << movingAverageScaling)) >> (movingAverageScaling + 1);
				uint residue;
				if (averageMean <= 1) residue = quotient;
				else
				{
					var remainderBits = FfmpegMath.CeilLog2((int)averageMean);
					var remainder = frameReader.ReadBitsLong(remainderBits);
					residue = unchecked((quotient << remainderBits) + remainder);
				}
				averageSum[currentChannel] = unchecked(residue + averageSum[currentChannel] - (averageSum[currentChannel] >> movingAverageScaling));
				residue = (residue >> 1) ^ unchecked(0U - (residue & 1));
				residues[currentChannel][sample] = unchecked((int)residue);
			}
			return 0;
		}

		private void DecodeLpc()
		{
			var order = (int)frameReader.ReadBits(5) + 1;
			var scaling = (int)frameReader.ReadBits(4);
			var integerBits = (int)frameReader.ReadBits(3) + 1;
			var coefficientBits = scaling + integerBits;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				for (var index = 0; index < order; index++) lpcCoefficients[currentChannel][index] = frameReader.ReadSignedBits(coefficientBits);
		}

		private void ClearCodecBuffers()
		{
			Array.Clear(acFilterCoefficients, 0, acFilterCoefficients.Length);
			for (var currentChannel = 0; currentChannel < channels; currentChannel++) Array.Clear(acFilterPreviousValues[currentChannel], 0, 16);
			for (var currentChannel = 0; currentChannel < channels; currentChannel++) Array.Clear(lpcCoefficients[currentChannel], 0, 40);
			Array.Clear(mclmsCoefficients, 0, mclmsCoefficients.Length);
			Array.Clear(mclmsCurrentCoefficients, 0, mclmsCurrentCoefficients.Length);
			Array.Clear(mclmsPreviousValues, 0, mclmsPreviousValues.Length);
			Array.Clear(mclmsUpdates, 0, mclmsUpdates.Length);
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				for (var stage = 0; stage < cdlmsTotal[currentChannel]; stage++)
				{
					var state = cdlms[currentChannel][stage];
					Array.Clear(state.Coefficients, 0, state.Coefficients.Length);
					Array.Clear(state.Previous16, 0, state.Previous16.Length);
					Array.Clear(state.Previous32, 0, state.Previous32.Length);
					Array.Clear(state.Updates, 0, state.Updates.Length);
				}
				averageSum[currentChannel] = 0;
			}
		}

		private void ResetCodec()
		{
			mclmsRecent = mclmsOrder * channels;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				for (var stage = 0; stage < cdlmsTotal[currentChannel]; stage++) cdlms[currentChannel][stage].Recent = cdlms[currentChannel][stage].Order;
				channel[currentChannel].TransientCounter = samplesPerFrame;
				transient[currentChannel] = true;
				transientPosition[currentChannel] = 0;
			}
		}

		private void RevertMclms(int tileSize)
		{
			for (var sample = 0; sample < tileSize; sample++)
			{
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				{
					mclmsPrediction[currentChannel] = 0;
					if (!channelCoded[currentChannel]) continue;
					for (var index = 0; index < mclmsOrder * channels; index++)
						mclmsPrediction[currentChannel] = unchecked(mclmsPrediction[currentChannel] +
							mclmsPreviousValues[index + mclmsRecent] * mclmsCoefficients[index + mclmsOrder * channels * currentChannel]);
					for (var previousChannel = 0; previousChannel < currentChannel; previousChannel++)
						mclmsPrediction[currentChannel] = unchecked(mclmsPrediction[currentChannel] +
							residues[previousChannel][sample] * mclmsCurrentCoefficients[previousChannel + channels * currentChannel]);
					mclmsPrediction[currentChannel] = unchecked(mclmsPrediction[currentChannel] + (int)((1U << mclmsScaling) >> 1));
					mclmsPrediction[currentChannel] >>= mclmsScaling;
					residues[currentChannel][sample] = unchecked(residues[currentChannel][sample] + mclmsPrediction[currentChannel]);
				}
				MclmsUpdate(sample);
			}
		}

		/// <summary>
		/// Updates the multichannel LMS prediction history and coefficients after one reconstructed sample.
		/// </summary>
		private void MclmsUpdate(int sample)
		{
			var range = 1 << (bitsPerSample - 1);
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				var predictionError = unchecked(residues[currentChannel][sample] - mclmsPrediction[currentChannel]);
				if (predictionError > 0)
				{
					for (var index = 0; index < mclmsOrder * channels; index++)
					{
						var coefficient = index + currentChannel * mclmsOrder * channels;
						mclmsCoefficients[coefficient] = unchecked((short)(mclmsCoefficients[coefficient] + mclmsUpdates[mclmsRecent + index]));
					}
					for (var previousChannel = 0; previousChannel < currentChannel; previousChannel++)
					{
						var coefficient = currentChannel * channels + previousChannel;
						mclmsCurrentCoefficients[coefficient] = unchecked((short)(mclmsCurrentCoefficients[coefficient] + Sign(residues[previousChannel][sample])));
					}
				} else if (predictionError < 0)
				{
					for (var index = 0; index < mclmsOrder * channels; index++)
					{
						var coefficient = index + currentChannel * mclmsOrder * channels;
						mclmsCoefficients[coefficient] = unchecked((short)(mclmsCoefficients[coefficient] - mclmsUpdates[mclmsRecent + index]));
					}
					for (var previousChannel = 0; previousChannel < currentChannel; previousChannel++)
					{
						var coefficient = currentChannel * channels + previousChannel;
						mclmsCurrentCoefficients[coefficient] = unchecked((short)(mclmsCurrentCoefficients[coefficient] - Sign(residues[previousChannel][sample])));
					}
				}
			}
			for (var currentChannel = channels - 1; currentChannel >= 0; currentChannel--)
			{
				mclmsRecent--;
				mclmsPreviousValues[mclmsRecent] = FfmpegMath.Clip(residues[currentChannel][sample], -range, range - 1);
				mclmsUpdates[mclmsRecent] = Sign(residues[currentChannel][sample]);
			}
			if (mclmsRecent == 0)
			{
				Array.Copy(mclmsPreviousValues, 0, mclmsPreviousValues, mclmsOrder * channels, mclmsOrder * channels);
				Array.Copy(mclmsUpdates, 0, mclmsUpdates, mclmsOrder * channels, mclmsOrder * channels);
				mclmsRecent = channels * mclmsOrder;
			}
		}

		private void UseHighUpdateSpeed(int currentChannel)
		{
			for (var stage = cdlmsTotal[currentChannel] - 1; stage >= 0; stage--)
			{
				var state = cdlms[currentChannel][stage];
				if (updateSpeed[currentChannel] == 16) continue;
				if (versionThreeRealtime)
					for (var index = 0; index < state.Order; index++) state.Updates[index + state.Recent] = unchecked((short)(state.Updates[index + state.Recent] * 2));
				else for (var index = 0; index < state.Order; index++) state.Updates[index] = unchecked((short)(state.Updates[index] * 2));
			}
			updateSpeed[currentChannel] = 16;
		}

		private void UseNormalUpdateSpeed(int currentChannel)
		{
			for (var stage = cdlmsTotal[currentChannel] - 1; stage >= 0; stage--)
			{
				var state = cdlms[currentChannel][stage];
				if (updateSpeed[currentChannel] == 8) continue;
				if (versionThreeRealtime)
					for (var index = 0; index < state.Order; index++) state.Updates[index + state.Recent] /= 2;
				else for (var index = 0; index < state.Order; index++) state.Updates[index] /= 2;
			}
			updateSpeed[currentChannel] = 8;
		}

		/// <summary>Runs the scalar CDLMS coefficient dot product and in-place adaptation with wrapping integer arithmetic.</summary>
		private void RevertCdlms(int currentChannel, int coefficientBegin, int coefficientEnd, bool use32BitHistory)
		{
			for (var stage = cdlmsTotal[currentChannel] - 1; stage >= 0; stage--)
			{
				var state = cdlms[currentChannel][stage];
				for (var sample = coefficientBegin; sample < coefficientEnd; sample++)
				{
					uint prediction = (1U << state.Scaling) >> 1;
					var residue = residues[currentChannel][sample];
					var multiplier = Sign(residue);
					var scalar = use32BitHistory ? ScalarProductAndAdd32(state, Align(state.Order, 8), multiplier) :
						ScalarProductAndAdd16(state, Align(state.Order, 16), multiplier);
					prediction = unchecked(prediction + (uint)scalar);
					var input = unchecked(residue + ((int)prediction >> state.Scaling));
					LmsUpdate(state, input, use32BitHistory, currentChannel);
					residues[currentChannel][sample] = input;
				}
			}
		}

		private int ScalarProductAndAdd16(CdlmsState state, int order, int multiplier)
		{
			uint result = 0;
			for (var index = 0; index < order; index += 2)
			{
				result = unchecked(result + (uint)(state.Coefficients[index] * state.Previous16[state.Recent + index]));
				state.Coefficients[index] = unchecked((short)(state.Coefficients[index] + multiplier * state.Updates[state.Recent + index]));
				result = unchecked(result + (uint)(state.Coefficients[index + 1] * state.Previous16[state.Recent + index + 1]));
				state.Coefficients[index + 1] = unchecked((short)(state.Coefficients[index + 1] + multiplier * state.Updates[state.Recent + index + 1]));
			}
			return unchecked((int)result);
		}

		private int ScalarProductAndAdd32(CdlmsState state, int order, int multiplier)
		{
			var result = 0;
			for (var index = 0; index < order; index += 2)
			{
				result = unchecked(result + state.Coefficients[index] * state.Previous32[state.Recent + index]);
				state.Coefficients[index] = unchecked((short)(state.Coefficients[index] + multiplier * state.Updates[state.Recent + index]));
				result = unchecked(result + state.Coefficients[index + 1] * state.Previous32[state.Recent + index + 1]);
				state.Coefficients[index + 1] = unchecked((short)(state.Coefficients[index + 1] + multiplier * state.Updates[state.Recent + index + 1]));
			}
			return result;
		}

		private void LmsUpdate(CdlmsState state, int input, bool use32BitHistory, int currentChannel)
		{
			var recent = state.Recent;
			if (recent != 0) recent--;
			else
			{
				if (use32BitHistory) Array.Copy(state.Previous32, 0, state.Previous32, state.Order, state.Order);
				else Array.Copy(state.Previous16, 0, state.Previous16, state.Order, state.Order);
				Array.Copy(state.Updates, 0, state.Updates, state.Order, state.Order);
				recent = state.Order - 1;
			}
			var range = 1 << (bitsPerSample - 1);
			var clipped = FfmpegMath.Clip(input, -range, range - 1);
			if (use32BitHistory) state.Previous32[recent] = clipped; else state.Previous16[recent] = (short)clipped;
			state.Updates[recent] = (short)(Sign(input) * updateSpeed[currentChannel]);
			state.Updates[recent + (state.Order >> 4)] >>= 2;
			state.Updates[recent + (state.Order >> 3)] >>= 1;
			state.Recent = recent;
			Array.Clear(state.Updates, recent + state.Order, state.Updates.Length - recent - state.Order);
		}

		private void RevertInterChannelDecorrelation(int tileSize)
		{
			if (channels != 2 || !channelCoded[0] && !channelCoded[1]) return;
			for (var sample = 0; sample < tileSize; sample++)
			{
				residues[0][sample] = unchecked(residues[0][sample] - (residues[1][sample] >> 1));
				residues[1][sample] = unchecked(residues[1][sample] + residues[0][sample]);
			}
		}

		private void RevertAcFilter(int tileSize)
		{
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				var previous = acFilterPreviousValues[currentChannel];
				for (var sample = 0; sample < acFilterOrder; sample++)
				{
					var prediction = 0;
					for (var index = 0; index < acFilterOrder; index++)
						prediction = unchecked(prediction + (sample <= index ? acFilterCoefficients[index] * previous[index - sample] :
							residues[currentChannel][sample - index - 1] * acFilterCoefficients[index]));
					prediction >>= acFilterScaling;
					residues[currentChannel][sample] = unchecked(residues[currentChannel][sample] + prediction);
				}
				for (var sample = acFilterOrder; sample < tileSize; sample++)
				{
					var prediction = 0;
					for (var index = 0; index < acFilterOrder; index++) prediction = unchecked(prediction + residues[currentChannel][sample - index - 1] * acFilterCoefficients[index]);
					prediction >>= acFilterScaling;
					residues[currentChannel][sample] = unchecked(residues[currentChannel][sample] + prediction);
				}
				for (var index = acFilterOrder - 1; index >= 0; index--)
					previous[index] = tileSize <= index ? previous[index - tileSize] : residues[currentChannel][tileSize - index - 1];
			}
		}

		private void AppendFrame(int samples)
		{
			if (samples <= 0) return;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				Array.Copy(frameSamples[currentChannel], 0, decodedSamples[currentChannel], outputSamples, samples);
			outputSamples += samples;
		}

		private void SaveNewBits(BitReader source, int length)
		{
			var position = source.Position;
			frameOffset = position & 7;
			var total = frameOffset + length;
			if ((total + 7) >> 3 > frameData.Length) { packetLoss = true; return; }
			Array.Clear(frameData, 0, (total + 7) >> 3);
			source.Seek(position - frameOffset);
			for (var index = 0; index < total; index++) SetSavedBit(index, source.ReadBit());
			source.Seek(position + length);
			numberOfSavedBits = total;
			frameReader.Initialize(frameData, numberOfSavedBits);
			frameReader.SkipBits(frameOffset);
		}

		private void AppendSavedBits(BitReader source, int length)
		{
			if (numberOfSavedBits + length > frameData.Length * 8) { packetLoss = true; numberOfSavedBits = 0; return; }
			for (var index = 0; index < length; index++) SetSavedBit(numberOfSavedBits + index, source.ReadBit());
			numberOfSavedBits += length;
			frameReader.Initialize(frameData, numberOfSavedBits);
			frameReader.SkipBits(frameOffset);
		}

		private void SetSavedBit(int position, uint value)
		{
			var mask = (byte)(1 << (7 - (position & 7)));
			if (value != 0) frameData[position >> 3] |= mask; else frameData[position >> 3] &= (byte)~mask;
		}

		private int WriteOutput(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			var bytesPerSample = bitsPerSample == 16 ? 2 : 4;
			var planeSize = outputSamples * bytesPerSample;
			var dataSize = planeSize * channels;
			if (output.Length < dataSize) return FfmpegError.InvalidArgument;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				for (var sample = 0; sample < outputSamples; sample++)
					if (bytesPerSample == 2)
						BinaryPrimitives.WriteInt16LittleEndian(output.Slice(currentChannel * planeSize + sample * 2, 2), (short)decodedSamples[currentChannel][sample]);
					else BinaryPrimitives.WriteInt32LittleEndian(output.Slice(currentChannel * planeSize + sample * 4, 4), decodedSamples[currentChannel][sample]);
			var format = bytesPerSample == 2 ? AudioSampleFormat.Signed16Planar : AudioSampleFormat.Signed32Planar;
			frame = new AudioFrameInfo(outputSamples, channels, format, channels, planeSize, dataSize);
			return 0;
		}

		private static int GetFrameLengthBits(int sampleRate, uint flags)
		{
			var bits = sampleRate <= 16000 ? 9 : sampleRate <= 22050 ? 10 : sampleRate <= 48000 ? 11 : sampleRate <= 96000 ? 12 : 13;
			var adjustment = flags & 6;
			if (adjustment == 2) bits++; else if (adjustment == 4) bits--; else if (adjustment == 6) bits -= 2;
			return bits;
		}

		private static int Sign(int value) => (value > 0 ? 1 : 0) - (value < 0 ? 1 : 0);
		private static int Align(int value, int alignment) => (value + alignment - 1) & -alignment;

		private static int[][] CreateIntPlanes(int count, int length)
		{
			var result = new int[count][];
			for (var index = 0; index < count; index++) result[index] = new int[length];
			return result;
		}

		private static CdlmsState[][] CreateCdlmsStates()
		{
			var result = new CdlmsState[MaximumChannels][];
			for (var currentChannel = 0; currentChannel < MaximumChannels; currentChannel++)
			{
				result[currentChannel] = new CdlmsState[9];
				for (var stage = 0; stage < 9; stage++) result[currentChannel][stage] = new CdlmsState();
			}
			return result;
		}

		/// <summary>
		/// Holds one WMA Lossless channel's subframe schedule and transient state.
		/// </summary>
		private sealed class WmaLosslessChannel
		{
			public int PreviousBlockLength;
			public int NumberOfSubframes;
			public readonly ushort[] SubframeLengths = new ushort[MaximumSubframes];
			public readonly ushort[] SubframeOffsets = new ushort[MaximumSubframes];
			public int CurrentSubframe;
			public int DecodedSamples;
			public int TransientCounter;
		}

		/// <summary>
		/// Stores one cascaded dynamic LMS stage's coefficients, history, and adaptation parameters.
		/// </summary>
		private sealed class CdlmsState
		{
			public int Order;
			public int Scaling;
			public int CoefficientsSent;
			public int BitsSent;
			public readonly short[] Coefficients = new short[MaximumOrder + 8];
			public readonly short[] Previous16 = new short[MaximumOrder * 2 + 16];
			public readonly int[] Previous32 = new int[MaximumOrder * 2 + 8];
			public readonly short[] Updates = new short[MaximumOrder * 2 + 8];
			public int Recent;
		}
	}
}
