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
using Ffmpeg.CsPort.Decoder.Transforms;
using Ffmpeg.CsPort.Decoder.Windows;

namespace Ffmpeg.CsPort.Decoder.Codecs.Wma
{
	/// <summary>
	/// Ports FFmpeg's scalar WMA Pro decoder, including packet-spanning frames, tiled subframes,
	/// vector/run-level entropy coding, scale-factor reuse, channel decorrelation, IMDCT, and overlap.
	/// </summary>
	public sealed class WmaProDecoder
	{
		private const int MaximumChannels = 8;
		private const int MaximumSubframes = 32;
		private const int MaximumBands = 29;
		private const int MaximumFrameDataSize = 32768;
		private const int MinimumBlockBits = 6;
		private const int MaximumBlockBits = 13;
		private const int BlockSizes = MaximumBlockBits - MinimumBlockBits + 1;
		private const double Log2Ten = 3.32192809488736234787;

		private readonly BitReader packetReader = new BitReader();
		private readonly BitReader frameReader = new BitReader();
		private readonly byte[] frameData = new byte[MaximumFrameDataSize + 64];
		private readonly int sampleRate;
		private readonly int channels;
		private readonly int blockAlign;
		private readonly uint decodeFlags;
		private readonly int bitsPerSample;
		private readonly int samplesPerFrame;
		private readonly int log2FrameSize;
		private readonly bool lengthPrefix;
		private readonly bool dynamicRangeCompression;
		private readonly int maximumSubframes;
		private readonly int subframeLengthBits;
		private readonly bool maximumSubframeLengthBit;
		private readonly int minimumSamplesPerSubframe;
		private readonly int lfeChannel;
		private readonly int[] numberOfScaleFactorBands = new int[BlockSizes];
		private readonly int[][] scaleFactorBandOffsets = CreateIntPlanes(BlockSizes, MaximumBands + 1);
		private readonly int[][][] scaleFactorOffsets = CreateIntCube(BlockSizes, BlockSizes, MaximumBands);
		private readonly int[] subwooferCutoffs = new int[BlockSizes];
		private readonly FfmpegFloatMdct[] transforms = new FfmpegFloatMdct[BlockSizes];
		private readonly float[][] windows = new float[BlockSizes][];
		private readonly WmaProChannel[] channel = new WmaProChannel[MaximumChannels];
		private readonly WmaProChannelGroup[] channelGroups = new WmaProChannelGroup[MaximumChannels];
		private readonly int[] currentChannelIndexes = new int[MaximumChannels];
		private readonly ushort[] tileSamples = new ushort[MaximumChannels];
		private readonly bool[] containsSubframe = new bool[MaximumChannels];
		private readonly float[] transformInput = new float[1 << MaximumBlockBits];
		private readonly float[] matrixData = new float[MaximumChannels];
		private readonly float[][] decodedSamples;
		private int numberOfSavedBits;
		private int frameOffset;
		private int packetSequenceNumber;
		private bool packetLoss = true;
		private bool skipFrame = true;
		private bool endOfFileDone;
		private uint frameNumber;
		private int trimStart;
		private int trimEnd;
		private int currentSubframeLength;
		private int currentSubframeOffset;
		private int channelsForCurrentSubframe;
		private int currentTableIndex;
		private int numberOfBands;
		private int escapeLength;
		private bool transmitNumberOfVectorCoefficients;
		private int numberOfChannelGroups;
		private int outputSamples;

		/// <summary>
		/// Validates WMA Pro extradata and initializes block, channel, transform, and VLC state for the stream.
		/// </summary>
		private WmaProDecoder(int sampleRate, int channels, int blockAlign, byte[] extraData)
		{
			this.sampleRate = sampleRate;
			this.blockAlign = blockAlign;
			decodeFlags = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(14));
			var channelMask = BinaryPrimitives.ReadUInt32LittleEndian(extraData.AsSpan(2));
			bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(extraData);
			this.channels = channelMask != 0 ? BitOperations.PopCount(channelMask) : channels;
			log2FrameSize = FfmpegMath.Log2((uint)blockAlign) + 4;
			lengthPrefix = (decodeFlags & 0x40) != 0;
			dynamicRangeCompression = (decodeFlags & 0x80) != 0;
			var frameBits = GetFrameLengthBits(sampleRate, decodeFlags);
			samplesPerFrame = 1 << frameBits;
			var log2MaximumSubframes = (int)((decodeFlags & 0x38) >> 3);
			maximumSubframes = 1 << log2MaximumSubframes;
			maximumSubframeLengthBit = maximumSubframes == 16 || maximumSubframes == 4;
			subframeLengthBits = FfmpegMath.Log2((uint)log2MaximumSubframes) + 1;
			minimumSamplesPerSubframe = samplesPerFrame / maximumSubframes;

			for (var index = 0; index < MaximumChannels; index++)
			{
				channel[index] = new WmaProChannel();
				channelGroups[index] = new WmaProChannelGroup();
			}
			for (var index = 0; index < this.channels; index++) channel[index].PreviousBlockLength = samplesPerFrame;
			var lfe = -1;
			if ((channelMask & 8) != 0)
				for (uint mask = 1; mask < 16; mask <<= 1)
					if ((channelMask & mask) != 0) lfe++;
			lfeChannel = lfe;

			var possibleBlockSizes = log2MaximumSubframes + 1;
			for (var table = 0; table < possibleBlockSizes; table++)
			{
				var subframeLength = samplesPerFrame >> table;
				var band = 1;
				scaleFactorBandOffsets[table][0] = 0;
				for (var index = 0; index < MaximumBands - 1 && scaleFactorBandOffsets[table][band - 1] < subframeLength; index++)
				{
					var offset = subframeLength * 2 * WmaProTables.CriticalFrequencies[index] / sampleRate + 2;
					offset &= ~3;
					if (offset > scaleFactorBandOffsets[table][band - 1]) scaleFactorBandOffsets[table][band++] = offset;
					if (offset >= subframeLength) break;
				}
				scaleFactorBandOffsets[table][band - 1] = subframeLength;
				numberOfScaleFactorBands[table] = band - 1;
			}
			for (var table = 0; table < possibleBlockSizes; table++)
				for (var band = 0; band < numberOfScaleFactorBands[table]; band++)
				{
					var offset = ((scaleFactorBandOffsets[table][band] + scaleFactorBandOffsets[table][band + 1] - 1) << table) >> 1;
					for (var otherTable = 0; otherTable < possibleBlockSizes; otherTable++)
					{
						var mappedBand = 0;
						while (scaleFactorBandOffsets[otherTable][mappedBand + 1] << otherTable < offset) mappedBand++;
						scaleFactorOffsets[table][otherTable][band] = mappedBand;
					}
				}

			for (var index = 0; index < BlockSizes; index++)
			{
				var blockBits = MinimumBlockBits + index;
				var scale = (float)(1.0 / (1 << (blockBits - 1)) / (1L << (bitsPerSample - 1)));
				transforms[index] = new FfmpegFloatMdct(1 << blockBits, true, scale);
				windows[index] = CodecWindows.GetSineWindow(blockBits);
			}
			for (var table = 0; table < possibleBlockSizes; table++)
			{
				var blockSize = samplesPerFrame >> table;
				var cutoff = (int)((440L * blockSize + 3L * (sampleRate >> 1) - 1) / sampleRate);
				subwooferCutoffs[table] = FfmpegMath.Clip(cutoff, 4, blockSize);
			}
			decodedSamples = CreateFloatPlanes(this.channels, samplesPerFrame * MaximumSubframes);
		}

		public int Channels => channels;
		public int SampleRate => sampleRate;
		public int FrameLength => samplesPerFrame;
		public int MaximumOutputBytes => samplesPerFrame * MaximumSubframes * channels * sizeof(float);

		public static int Initialize(int sampleRate, int channels, long bitRate, int blockAlign, byte[] extraData, out WmaProDecoder decoder)
		{
			decoder = null;
			if (sampleRate <= 0 || channels <= 0 || channels > MaximumChannels || bitRate <= 0 || blockAlign <= 0 ||
				extraData == null || extraData.Length < 18)
				return FfmpegError.InvalidArgument;
			var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(extraData);
			var mask = BinaryPrimitives.ReadUInt32LittleEndian(extraData.AsSpan(2));
			var codedChannels = mask != 0 ? BitOperations.PopCount(mask) : channels;
			var flags = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(14));
			var frameBits = GetFrameLengthBits(sampleRate, flags);
			var maximumSubframes = 1 << ((flags & 0x38) >> 3);
			if (bitsPerSample < 1 || bitsPerSample > 32 || codedChannels <= 0 || codedChannels > MaximumChannels ||
				codedChannels > channels || FfmpegMath.Log2((uint)blockAlign) + 4 > 25 || frameBits > MaximumBlockBits ||
				maximumSubframes > MaximumSubframes || (1 << frameBits) / maximumSubframes < (1 << MinimumBlockBits))
				return FfmpegError.InvalidData;
			decoder = new WmaProDecoder(sampleRate, channels, blockAlign, extraData);
			return 0;
		}

		/// <summary>Consumes one ASF media object, reconstructs all complete WMA Pro frames, and writes planar float output.</summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packetLength == 0) return Drain(output, out frame);
			if (packet == null || packetOffset < 0 || packetLength < blockAlign || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			endOfFileDone = false;
			outputSamples = 0;
			var initialize = packetReader.Initialize(packet, packetOffset, blockAlign * 8);
			if (initialize < 0) return initialize;
			var sequence = (int)packetReader.ReadBits(4);
			packetReader.SkipBits(2);
			var previousFrameBits = (int)packetReader.ReadBits(log2FrameSize);
			if (!packetLoss && ((packetSequenceNumber + 1) & 15) != sequence) packetLoss = true;
			packetSequenceNumber = sequence;

			var moreFrames = false;
			if (previousFrameBits > 0)
			{
				if (previousFrameBits >= packetReader.BitsLeft) previousFrameBits = packetReader.BitsLeft;
				AppendSavedBits(packetReader, previousFrameBits);
				if (!packetLoss) moreFrames = DecodeAssembledFrame();
			} else if (numberOfSavedBits - frameOffset != 0)
			{
				numberOfSavedBits = 0;
			}
			if (packetLoss)
			{
				numberOfSavedBits = 0;
				packetLoss = false;
			}
			moreFrames = !packetLoss;

			while (moreFrames && packetReader.BitsLeft > log2FrameSize)
			{
				var frameSize = lengthPrefix ? (int)packetReader.ShowBits(log2FrameSize) : packetReader.BitsLeft;
				if (frameSize <= 0 || frameSize > packetReader.BitsLeft) break;
				SaveNewBits(packetReader, frameSize);
				moreFrames = DecodeAssembledFrame();
				if (packetLoss) break;
			}
			if (!packetLoss && packetReader.BitsLeft > 0) SaveNewBits(packetReader, packetReader.BitsLeft);
			var write = WriteOutput(output, out frame);
			return write < 0 ? write : blockAlign;
		}

		public int Drain(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (endOfFileDone) return 0;
			endOfFileDone = true;
			return 0;
		}

		public void Flush()
		{
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				Array.Clear(channel[currentChannel].Output, 0, samplesPerFrame);
			packetLoss = true;
			endOfFileDone = false;
			skipFrame = true;
		}

		/// <summary>Decodes one complete length-delimited frame from the saved bit reservoir.</summary>
		private bool DecodeAssembledFrame()
		{
			var length = 0;
			if (lengthPrefix) length = (int)frameReader.ReadBits(log2FrameSize);
			if (DecodeTileHeader() < 0)
			{
				packetLoss = true;
				return false;
			}
			if (channels > 1 && frameReader.ReadBit() != 0 && frameReader.ReadBit() != 0)
				for (var index = 0; index < channels * channels; index++) frameReader.SkipBits(4);
			if (dynamicRangeCompression) frameReader.SkipBits(8);
			if (frameReader.ReadBit() != 0)
			{
				if (frameReader.ReadBit() != 0) trimStart = (int)frameReader.ReadBits(FfmpegMath.Log2((uint)(samplesPerFrame * 2)));
				if (frameReader.ReadBit() != 0) trimEnd = (int)frameReader.ReadBits(FfmpegMath.Log2((uint)(samplesPerFrame * 2)));
			} else
			{
				trimStart = 0;
				trimEnd = 0;
			}
			var frameHeaderOffset = frameReader.Position;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				channel[currentChannel].DecodedSamples = 0;
				channel[currentChannel].CurrentSubframe = 0;
				channel[currentChannel].ReuseScaleFactors = false;
			}
			var parsedAllSubframes = false;
			while (!parsedAllSubframes)
			{
				var result = DecodeSubframe(out parsedAllSubframes);
				if (result < 0)
				{
					packetLoss = true;
					return false;
				}
			}

			EmitFrame();
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				Array.Copy(channel[currentChannel].Output, samplesPerFrame, channel[currentChannel].Output, 0, samplesPerFrame >> 1);
			if (skipFrame) skipFrame = false;
			if (lengthPrefix)
			{
				var consumed = frameReader.Position - frameOffset;
				if (length != consumed + 2)
				{
					packetLoss = true;
					return false;
				}
				frameReader.SkipBits(length - consumed - 1);
			} else
			{
				while (frameReader.Position < numberOfSavedBits && frameReader.ReadBit() == 0) { }
			}
			var moreFrames = frameReader.ReadBit() != 0;
			frameNumber++;
			_ = frameHeaderOffset;
			return moreFrames;
		}

		/// <summary>Reconstructs FFmpeg's per-channel tiled subframe layout for the current frame.</summary>
		private int DecodeTileHeader()
		{
			Array.Clear(tileSamples, 0, channels);
			var channelsForSubframe = channels;
			var minimumChannelLength = 0;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++) channel[currentChannel].NumberOfSubframes = 0;
			var fixedChannelLayout = maximumSubframes == 1 || frameReader.ReadBit() != 0;
			do
			{
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				{
					if (tileSamples[currentChannel] == minimumChannelLength)
						containsSubframe[currentChannel] = fixedChannelLayout || channelsForSubframe == 1 ||
							minimumChannelLength == samplesPerFrame - minimumSamplesPerSubframe || frameReader.ReadBit() != 0;
					else containsSubframe[currentChannel] = false;
				}
				var subframeLength = DecodeSubframeLength(minimumChannelLength);
				if (subframeLength <= 0) return FfmpegError.InvalidData;
				minimumChannelLength += subframeLength;
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				{
					var current = channel[currentChannel];
					if (containsSubframe[currentChannel])
					{
						if (current.NumberOfSubframes >= MaximumSubframes) return FfmpegError.InvalidData;
						current.SubframeLengths[current.NumberOfSubframes] = (ushort)subframeLength;
						tileSamples[currentChannel] += (ushort)subframeLength;
						current.NumberOfSubframes++;
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
			if (frameReader.BitsLeft < 1) return FfmpegError.InvalidData;
			int shift;
			if (maximumSubframeLengthBit)
				shift = frameReader.ReadBit() != 0 ? 1 + (int)frameReader.ReadBitsOrZero(subframeLengthBits - 1) : 0;
			else shift = (int)frameReader.ReadBits(subframeLengthBits);
			var length = samplesPerFrame >> shift;
			return length < minimumSamplesPerSubframe || length > samplesPerFrame ? FfmpegError.InvalidData : length;
		}

		/// <summary>Decodes one synchronized subframe group without changing the source's coefficient or transform schedule.</summary>
		private int DecodeSubframe(out bool parsedAllSubframes)
		{
			parsedAllSubframes = false;
			var offset = samplesPerFrame;
			var subframeLength = samplesPerFrame;
			var totalSamples = samplesPerFrame * channels;
			var transmitCoefficients = false;
			currentSubframeOffset = frameReader.Position;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
			{
				channel[currentChannel].Grouped = false;
				if (offset > channel[currentChannel].DecodedSamples)
				{
					offset = channel[currentChannel].DecodedSamples;
					if (channel[currentChannel].CurrentSubframe >= channel[currentChannel].NumberOfSubframes) return FfmpegError.InvalidData;
					subframeLength = channel[currentChannel].SubframeLengths[channel[currentChannel].CurrentSubframe];
				}
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
			currentTableIndex = FfmpegMath.Log2((uint)(samplesPerFrame / subframeLength));
			numberOfBands = numberOfScaleFactorBands[currentTableIndex];
			var currentBandOffsets = scaleFactorBandOffsets[currentTableIndex];
			var subwooferCutoff = subwooferCutoffs[currentTableIndex];
			offset += samplesPerFrame >> 1;
			for (var index = 0; index < channelsForCurrentSubframe; index++) channel[currentChannelIndexes[index]].CoefficientOffset = offset;
			currentSubframeLength = subframeLength;
			escapeLength = FfmpegMath.Log2((uint)(subframeLength - 1)) + 1;

			if (frameReader.ReadBit() != 0)
			{
				var fillBits = (int)frameReader.ReadBits(2);
				if (fillBits == 0)
				{
					var length = (int)frameReader.ReadBits(4);
					fillBits = (int)frameReader.ReadBitsOrZero(length) + 1;
				}
				if (frameReader.Position + fillBits > numberOfSavedBits) return FfmpegError.InvalidData;
				frameReader.SkipBits(fillBits);
			}
			if (frameReader.ReadBit() != 0) return FfmpegError.PatchWelcome;
			var transformResult = DecodeChannelTransform(currentBandOffsets);
			if (transformResult < 0) return transformResult;
			for (var index = 0; index < channelsForCurrentSubframe; index++)
			{
				var current = channel[currentChannelIndexes[index]];
				current.TransmitCoefficients = frameReader.ReadBit() != 0;
				if (current.TransmitCoefficients) transmitCoefficients = true;
			}
			if (transmitCoefficients)
			{
				var quantizationStep = 90 * bitsPerSample >> 4;
				transmitNumberOfVectorCoefficients = frameReader.ReadBit() != 0;
				if (transmitNumberOfVectorCoefficients)
				{
					var numberOfBits = FfmpegMath.Log2((uint)((subframeLength + 3) / 4)) + 1;
					for (var index = 0; index < channelsForCurrentSubframe; index++)
					{
						var current = channel[currentChannelIndexes[index]];
						current.NumberOfVectorCoefficients = (int)frameReader.ReadBits(numberOfBits) << 2;
						if (current.NumberOfVectorCoefficients > subframeLength) return FfmpegError.InvalidData;
					}
				} else
				{
					for (var index = 0; index < channelsForCurrentSubframe; index++)
						channel[currentChannelIndexes[index]].NumberOfVectorCoefficients = subframeLength;
				}
				var step = frameReader.ReadSignedBits(6);
				quantizationStep += step;
				if (step == -32 || step == 31)
				{
					var sign = (step == 31 ? 1 : 0) - 1;
					var quantization = 0;
					while (frameReader.Position + 5 < numberOfSavedBits && (step = (int)frameReader.ReadBits(5)) == 31) quantization += 31;
					quantizationStep += ((quantization + step) ^ sign) - sign;
				}
				if (channelsForCurrentSubframe == 1)
					channel[currentChannelIndexes[0]].QuantizationStep = quantizationStep;
				else
				{
					var modifierLength = (int)frameReader.ReadBits(3);
					for (var index = 0; index < channelsForCurrentSubframe; index++)
					{
						var current = channel[currentChannelIndexes[index]];
						current.QuantizationStep = quantizationStep;
						if (frameReader.ReadBit() != 0) current.QuantizationStep += modifierLength != 0 ? (int)frameReader.ReadBits(modifierLength) + 1 : 1;
					}
				}
				if (DecodeScaleFactors() < 0) return FfmpegError.InvalidData;
			}

			for (var index = 0; index < channelsForCurrentSubframe; index++)
			{
				var currentChannel = currentChannelIndexes[index];
				var current = channel[currentChannel];
				if (current.TransmitCoefficients && frameReader.Position < numberOfSavedBits) DecodeCoefficients(currentChannel);
				else Array.Clear(current.Output, current.CoefficientOffset, subframeLength);
			}
			if (transmitCoefficients)
			{
				InverseChannelTransform(currentBandOffsets);
				for (var index = 0; index < channelsForCurrentSubframe; index++)
				{
					var currentChannel = currentChannelIndexes[index];
					var current = channel[currentChannel];
					if (currentChannel == lfeChannel) Array.Clear(transformInput, subwooferCutoff, subframeLength - subwooferCutoff);
					for (var band = 0; band < numberOfBands; band++)
					{
						var end = Math.Min(currentBandOffsets[band + 1], subframeLength);
						var exponent = current.QuantizationStep - (current.MaximumScaleFactor - current.ScaleFactors[band]) * current.ScaleFactorStep;
						var quantization = (float)Math.Pow(2.0, Log2Ten * (exponent / 20.0));
						var start = currentBandOffsets[band];
						for (var coefficient = start; coefficient < end; coefficient++)
							transformInput[coefficient] = current.Output[current.CoefficientOffset + coefficient] * quantization;
					}
					transforms[FfmpegMath.Log2((uint)subframeLength) - MinimumBlockBits].Transform(
						transformInput.AsSpan(0, subframeLength), current.Output.AsSpan(current.CoefficientOffset, subframeLength));
				}
			}
			ApplyWindow();
			for (var index = 0; index < channelsForCurrentSubframe; index++)
			{
				var current = channel[currentChannelIndexes[index]];
				if (current.CurrentSubframe >= current.NumberOfSubframes) return FfmpegError.InvalidData;
				current.CurrentSubframe++;
			}
			return 0;
		}

		/// <summary>Builds channel groups and their optional per-band inverse decorrelation matrices.</summary>
		private int DecodeChannelTransform(int[] currentBandOffsets)
		{
			numberOfChannelGroups = 0;
			if (channels <= 1) return 0;
			var remainingChannels = channelsForCurrentSubframe;
			if (frameReader.ReadBit() != 0) return FfmpegError.PatchWelcome;
			while (remainingChannels != 0 && numberOfChannelGroups < channelsForCurrentSubframe)
			{
				var group = channelGroups[numberOfChannelGroups++];
				group.NumberOfChannels = 0;
				group.Transform = false;
				if (remainingChannels > 2)
				{
					for (var index = 0; index < channelsForCurrentSubframe; index++)
					{
						var currentChannel = currentChannelIndexes[index];
						if (!channel[currentChannel].Grouped && frameReader.ReadBit() != 0)
						{
							group.ChannelIndexes[group.NumberOfChannels++] = currentChannel;
							channel[currentChannel].Grouped = true;
						}
					}
				} else
				{
					group.NumberOfChannels = remainingChannels;
					var groupIndex = 0;
					for (var index = 0; index < channelsForCurrentSubframe; index++)
					{
						var currentChannel = currentChannelIndexes[index];
						if (!channel[currentChannel].Grouped) group.ChannelIndexes[groupIndex++] = currentChannel;
						channel[currentChannel].Grouped = true;
					}
				}
				if (group.NumberOfChannels == 2)
				{
					if (frameReader.ReadBit() != 0)
					{
						if (frameReader.ReadBit() != 0) return FfmpegError.PatchWelcome;
					} else
					{
						group.Transform = true;
						var value = channels == 2 ? 1.0f : 0.70703125f;
						group.Matrix[0] = value; group.Matrix[1] = -value;
						group.Matrix[2] = value; group.Matrix[3] = value;
					}
				} else if (group.NumberOfChannels > 2 && frameReader.ReadBit() != 0)
				{
					group.Transform = true;
					if (frameReader.ReadBit() != 0) DecodeDecorrelationMatrix(group);
					else
					{
						if (group.NumberOfChannels > 6) return FfmpegError.PatchWelcome;
						var matrixOffset = group.NumberOfChannels switch { 1 => 0, 2 => 1, 3 => 5, 4 => 14, 5 => 30, _ => 55 };
						Array.Copy(WmaProTables.DefaultDecorrelationMatrices, matrixOffset, group.Matrix, 0,
							group.NumberOfChannels * group.NumberOfChannels);
					}
				}
				if (group.Transform)
				{
					if (frameReader.ReadBit() == 0)
						for (var band = 0; band < numberOfBands; band++) group.TransformBands[band] = frameReader.ReadBit() != 0;
					else Array.Fill(group.TransformBands, true, 0, numberOfBands);
				}
				remainingChannels -= group.NumberOfChannels;
			}
			_ = currentBandOffsets;
			return 0;
		}

		private void DecodeDecorrelationMatrix(WmaProChannelGroup group)
		{
			Span<sbyte> rotations = stackalloc sbyte[MaximumChannels * MaximumChannels];
			Array.Clear(group.Matrix, 0, channels * channels);
			var rotationCount = group.NumberOfChannels * (group.NumberOfChannels - 1) >> 1;
			for (var index = 0; index < rotationCount; index++) rotations[index] = (sbyte)frameReader.ReadBits(6);
			for (var index = 0; index < group.NumberOfChannels; index++)
				group.Matrix[group.NumberOfChannels * index + index] = frameReader.ReadBit() != 0 ? 1.0f : -1.0f;
			var offset = 0;
			for (var row = 1; row < group.NumberOfChannels; row++)
			{
				for (var previousRow = 0; previousRow < row; previousRow++)
					for (var column = 0; column < row + 1; column++)
					{
						var first = group.Matrix[previousRow * group.NumberOfChannels + column];
						var second = group.Matrix[row * group.NumberOfChannels + column];
						var rotation = rotations[offset + previousRow];
						float sine;
						float cosine;
						if (rotation < 32)
						{
							sine = WmaProTables.Sine64[rotation];
							cosine = WmaProTables.Sine64[32 - rotation];
						} else
						{
							sine = WmaProTables.Sine64[64 - rotation];
							cosine = -WmaProTables.Sine64[rotation - 32];
						}
						group.Matrix[column + previousRow * group.NumberOfChannels] = first * sine - second * cosine;
						group.Matrix[column + row * group.NumberOfChannels] = first * cosine + second * sine;
					}
				offset += row;
			}
		}

		/// <summary>
		/// Decodes WMA Pro scale-factor reuse or differential updates and expands them to coefficient bands.
		/// </summary>
		private int DecodeScaleFactors()
		{
			for (var index = 0; index < channelsForCurrentSubframe; index++)
			{
				var current = channel[currentChannelIndexes[index]];
				var destinationIndex = 1 - current.ScaleFactorIndex;
				current.ScaleFactors = current.SavedScaleFactors[destinationIndex];
				if (current.ReuseScaleFactors)
				{
					var offsets = scaleFactorOffsets[currentTableIndex][current.ScaleFactorTableIndex];
					for (var band = 0; band < numberOfBands; band++)
						current.ScaleFactors[band] = current.SavedScaleFactors[current.ScaleFactorIndex][offsets[band]];
				}
				if (current.CurrentSubframe == 0 || frameReader.ReadBit() != 0)
				{
					if (!current.ReuseScaleFactors)
					{
						current.ScaleFactorStep = (int)frameReader.ReadBits(2) + 1;
						var value = 45 / current.ScaleFactorStep;
						for (var band = 0; band < numberOfBands; band++)
						{
							value += frameReader.ReadVlc(WmaProTables.ScaleVlc.Table, 8, 3);
							current.ScaleFactors[band] = value;
						}
					} else
					{
						for (var band = 0; band < numberOfBands; band++)
						{
							var valueIndex = frameReader.ReadVlc(WmaProTables.ScaleRunLevelVlc.Table, 9, 3);
							int skip;
							int value;
							int sign;
							if (valueIndex == 0)
							{
								var code = (int)frameReader.ReadBits(14);
								value = code >> 6;
								sign = (code & 1) - 1;
								skip = (code & 0x3f) >> 1;
							} else if (valueIndex == 1) break;
							else
							{
								skip = WmaProTables.ScaleRun[valueIndex];
								value = WmaProTables.ScaleLevel[valueIndex];
								sign = (int)frameReader.ReadBit() - 1;
							}
							band += skip;
							if (band >= numberOfBands) return FfmpegError.InvalidData;
							current.ScaleFactors[band] += (value ^ sign) - sign;
						}
					}
					current.ScaleFactorIndex = destinationIndex;
					current.ScaleFactorTableIndex = currentTableIndex;
					current.ReuseScaleFactors = true;
				}
				current.MaximumScaleFactor = current.ScaleFactors[0];
				for (var band = 1; band < numberOfBands; band++) current.MaximumScaleFactor = Math.Max(current.MaximumScaleFactor, current.ScaleFactors[band]);
			}
			return 0;
		}

		/// <summary>Decodes WMA Pro vector tuples followed by the source run/level escape syntax.</summary>
		private int DecodeCoefficients(int currentChannel)
		{
			var current = channel[currentChannel];
			var tableIndex = (int)frameReader.ReadBit();
			var run = tableIndex != 0 ? WmaProTables.Coefficient1Run : WmaProTables.Coefficient0Run;
			var level = tableIndex != 0 ? WmaProTables.Coefficient1Level : WmaProTables.Coefficient0Level;
			var coefficient = 0;
			var zeroCount = 0;
			var runLevelMode = false;
			Span<uint> values = stackalloc uint[4];
			while ((transmitNumberOfVectorCoefficients || !runLevelMode) && coefficient + 3 < current.NumberOfVectorCoefficients)
			{
				var vector = frameReader.ReadVlc(WmaProTables.Vector4Vlc.Table, 9, 2);
				if (vector < 0)
				{
					for (var index = 0; index < 4; index += 2)
					{
						vector = frameReader.ReadVlc(WmaProTables.Vector2Vlc.Table, 9, 2);
						if (vector < 0)
						{
							var first = frameReader.ReadVlc(WmaProTables.Vector1Vlc.Table, 9, 2);
							if (first == WmaProTables.Vector1Lengths.Length - 1) first += (int)ReadLargeValue();
							var second = frameReader.ReadVlc(WmaProTables.Vector1Vlc.Table, 9, 2);
							if (second == WmaProTables.Vector1Lengths.Length - 1) second += (int)ReadLargeValue();
							values[index] = unchecked((uint)BitConverter.SingleToInt32Bits(first));
							values[index + 1] = unchecked((uint)BitConverter.SingleToInt32Bits(second));
						} else
						{
							values[index] = FloatIntegerBits(vector >> 4);
							values[index + 1] = FloatIntegerBits(vector & 15);
						}
					}
				} else
				{
					values[0] = FloatIntegerBits(vector >> 12);
					values[1] = FloatIntegerBits((vector >> 8) & 15);
					values[2] = FloatIntegerBits((vector >> 4) & 15);
					values[3] = FloatIntegerBits(vector & 15);
				}
				for (var index = 0; index < 4; index++)
				{
					if (values[index] != 0)
					{
						var sign = (uint)((int)frameReader.ReadBit() - 1);
						current.Output[current.CoefficientOffset + coefficient] = BitConverter.Int32BitsToSingle(unchecked((int)(values[index] ^ (sign << 31))));
						zeroCount = 0;
					} else
					{
						current.Output[current.CoefficientOffset + coefficient] = 0;
						zeroCount++;
						runLevelMode |= zeroCount > currentSubframeLength >> 8;
					}
					coefficient++;
				}
			}
			if (coefficient < currentSubframeLength)
			{
				Array.Clear(current.Output, current.CoefficientOffset + coefficient, currentSubframeLength - coefficient);
				var mask = currentSubframeLength - 1;
				var vlc = WmaProTables.CoefficientVlcs[tableIndex];
				for (; coefficient < currentSubframeLength; coefficient++)
				{
					var code = frameReader.ReadVlc(vlc.Table, 9, 3);
					if (code > 1)
					{
						coefficient += run[code];
						var sign = (int)frameReader.ReadBit() - 1;
						var bits = BitConverter.SingleToInt32Bits(level[code]) ^ (sign & unchecked((int)0x80000000));
						current.Output[current.CoefficientOffset + (coefficient & mask)] = BitConverter.Int32BitsToSingle(bits);
					} else if (code == 1) break;
					else
					{
						var magnitude = (int)ReadLargeValue();
						if (frameReader.ReadBit() != 0)
						{
							if (frameReader.ReadBit() != 0)
							{
								if (frameReader.ReadBit() != 0) return FfmpegError.InvalidData;
								coefficient += (int)frameReader.ReadBits(escapeLength) + 4;
							} else coefficient += (int)frameReader.ReadBits(2) + 1;
						}
						var sign = (int)frameReader.ReadBit() - 1;
						current.Output[current.CoefficientOffset + (coefficient & mask)] = (magnitude ^ sign) - sign;
					}
				}
				if (coefficient > currentSubframeLength) return FfmpegError.InvalidData;
			}
			return 0;
		}

		private uint ReadLargeValue()
		{
			var bits = 8;
			if (frameReader.ReadBit() != 0)
			{
				bits += 8;
				if (frameReader.ReadBit() != 0)
				{
					bits += 8;
					if (frameReader.ReadBit() != 0) bits += 7;
				}
			}
			return frameReader.ReadBitsLong(bits);
		}

		private void InverseChannelTransform(int[] currentBandOffsets)
		{
			for (var groupIndex = 0; groupIndex < numberOfChannelGroups; groupIndex++)
			{
				var group = channelGroups[groupIndex];
				if (!group.Transform) continue;
				for (var band = 0; band < numberOfBands; band++)
				{
					var start = currentBandOffsets[band];
					var end = Math.Min(currentBandOffsets[band + 1], currentSubframeLength);
					if (group.TransformBands[band])
					{
						for (var coefficient = start; coefficient < end; coefficient++)
						{
							for (var currentChannel = 0; currentChannel < group.NumberOfChannels; currentChannel++)
							{
								var current = channel[group.ChannelIndexes[currentChannel]];
								matrixData[currentChannel] = current.Output[current.CoefficientOffset + coefficient];
							}
							var matrixOffset = 0;
							for (var currentChannel = 0; currentChannel < group.NumberOfChannels; currentChannel++)
							{
								var sum = 0.0f;
								for (var input = 0; input < group.NumberOfChannels; input++) sum += matrixData[input] * group.Matrix[matrixOffset++];
								var current = channel[group.ChannelIndexes[currentChannel]];
								current.Output[current.CoefficientOffset + coefficient] = sum;
							}
						}
					} else if (channels == 2)
					{
						for (var currentChannel = 0; currentChannel < 2; currentChannel++)
						{
							var current = channel[group.ChannelIndexes[currentChannel]];
							for (var coefficient = start; coefficient < end; coefficient++)
								current.Output[current.CoefficientOffset + coefficient] *= 181.0f / 128;
						}
					}
				}
			}
		}

		private void ApplyWindow()
		{
			for (var index = 0; index < channelsForCurrentSubframe; index++)
			{
				var current = channel[currentChannelIndexes[index]];
				var windowLength = current.PreviousBlockLength;
				var start = current.CoefficientOffset - (windowLength >> 1);
				if (currentSubframeLength < windowLength)
				{
					start += (windowLength - currentSubframeLength) >> 1;
					windowLength = currentSubframeLength;
				}
				var window = windows[FfmpegMath.Log2((uint)windowLength) - MinimumBlockBits];
				var half = windowLength >> 1;
				for (int left = -half, right = half - 1; left < 0; left++, right--)
				{
					var first = current.Output[start + half + left];
					var second = current.Output[start + half + right];
					var windowLeft = window[half + left];
					var windowRight = window[half + right];
					current.Output[start + half + left] = first * windowRight - second * windowLeft;
					current.Output[start + half + right] = first * windowLeft + second * windowRight;
				}
				current.PreviousBlockLength = currentSubframeLength;
			}
		}

		private void EmitFrame()
		{
			var start = 0;
			var count = samplesPerFrame;
			if (trimStart != 0)
			{
				if (trimStart < count) { start = trimStart; count -= trimStart; } else count = 0;
			}
			if (trimEnd != 0)
			{
				if (trimEnd < count) count -= trimEnd; else count = 0;
			}
			if (!skipFrame && count > 0)
			{
				for (var currentChannel = 0; currentChannel < channels; currentChannel++)
					Array.Copy(channel[currentChannel].Output, start, decodedSamples[currentChannel], outputSamples, count);
				outputSamples += count;
			}
			trimStart = 0;
			trimEnd = 0;
		}

		private void SaveNewBits(BitReader source, int length)
		{
			var position = source.Position;
			frameOffset = position & 7;
			var total = frameOffset + length;
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
			if (length <= 0) return;
			if (numberOfSavedBits + length > MaximumFrameDataSize * 8)
			{
				packetLoss = true;
				return;
			}
			for (var index = 0; index < length; index++) SetSavedBit(numberOfSavedBits + index, source.ReadBit());
			numberOfSavedBits += length;
			frameReader.Initialize(frameData, numberOfSavedBits);
			frameReader.SkipBits(frameOffset);
		}

		private void SetSavedBit(int position, uint value)
		{
			var mask = (byte)(1 << (7 - (position & 7)));
			if (value != 0) frameData[position >> 3] |= mask;
			else frameData[position >> 3] &= (byte)~mask;
		}

		private int WriteOutput(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			var planeSize = outputSamples * sizeof(float);
			var dataSize = planeSize * channels;
			if (output.Length < dataSize) return FfmpegError.InvalidArgument;
			for (var currentChannel = 0; currentChannel < channels; currentChannel++)
				for (var sample = 0; sample < outputSamples; sample++)
					BinaryPrimitives.WriteInt32LittleEndian(output.Slice(currentChannel * planeSize + sample * 4, 4),
						BitConverter.SingleToInt32Bits(decodedSamples[currentChannel][sample]));
			frame = new AudioFrameInfo(outputSamples, channels, AudioSampleFormat.FloatPlanar, channels, planeSize, dataSize);
			return 0;
		}

		private static uint FloatIntegerBits(int value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

		private static int GetFrameLengthBits(int sampleRate, uint flags)
		{
			var bits = sampleRate <= 16000 ? 9 : sampleRate <= 22050 ? 10 : sampleRate <= 48000 ? 11 : sampleRate <= 96000 ? 12 : 13;
			var adjustment = flags & 6;
			if (adjustment == 2) bits++;
			else if (adjustment == 4) bits--;
			else if (adjustment == 6) bits -= 2;
			return bits;
		}

		private static int[][] CreateIntPlanes(int count, int length)
		{
			var result = new int[count][];
			for (var index = 0; index < count; index++) result[index] = new int[length];
			return result;
		}

		private static int[][][] CreateIntCube(int first, int second, int length)
		{
			var result = new int[first][][];
			for (var index = 0; index < first; index++) result[index] = CreateIntPlanes(second, length);
			return result;
		}

		private static float[][] CreateFloatPlanes(int count, int length)
		{
			var result = new float[count][];
			for (var index = 0; index < count; index++) result[index] = new float[length];
			return result;
		}

		/// <summary>
		/// Holds one WMA Pro channel's transform, scale-factor, coefficient, and overlap state.
		/// </summary>
		private sealed class WmaProChannel
		{
			public int PreviousBlockLength;
			public bool TransmitCoefficients;
			public int NumberOfSubframes;
			public readonly ushort[] SubframeLengths = new ushort[MaximumSubframes];
			public readonly ushort[] SubframeOffsets = new ushort[MaximumSubframes];
			public int CurrentSubframe;
			public int DecodedSamples;
			public bool Grouped;
			public int QuantizationStep;
			public bool ReuseScaleFactors;
			public int ScaleFactorStep;
			public int MaximumScaleFactor;
			public readonly int[][] SavedScaleFactors = CreateIntPlanes(2, MaximumBands);
			public int ScaleFactorIndex;
			public int[] ScaleFactors;
			public int ScaleFactorTableIndex;
			public int CoefficientOffset;
			public int NumberOfVectorCoefficients;
			public readonly float[] Output = new float[(1 << MaximumBlockBits) + (1 << MaximumBlockBits) / 2];
		}

		/// <summary>
		/// Stores channel membership and transform-coding flags for one WMA Pro channel group.
		/// </summary>
		private sealed class WmaProChannelGroup
		{
			public int NumberOfChannels;
			public bool Transform;
			public readonly bool[] TransformBands = new bool[MaximumBands];
			public readonly float[] Matrix = new float[MaximumChannels * MaximumChannels];
			public readonly int[] ChannelIndexes = new int[MaximumChannels];
		}
	}
}
