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
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Mathematics;
using Ffmpeg.CsPort.Decoder.Transforms;
using Ffmpeg.CsPort.Decoder.Windows;

namespace Ffmpeg.CsPort.Decoder.Codecs.Wma
{
	/// <summary>
	/// Ports FFmpeg's scalar Windows Media Audio v1/v2 decoder, including superframe bit reservoirs,
	/// exponent/noise coding, variable block lengths, full IMDCT, and asymmetric overlap windows.
	/// </summary>
	public sealed class WmaV1V2Decoder
	{
		private const int BlockMinimumBits = 7;
		private const int BlockMaximumSize = 1 << 11;
		private const int MaximumBlockSizes = 5;
		private const int HighBandMaximumSize = 16;
		private const int NoiseTableSize = 8192;
		private const int MaximumCodedSuperframeSize = 32768;
		private const int MaximumFramesPerPacket = 15;
		private const int LspPowerBits = 7;
		private const double Log2Ten = 3.32192809488736234787;

		private readonly BitReader reader = new BitReader();
		private readonly int version;
		private readonly int sampleRate;
		private readonly int channels;
		private readonly long bitRate;
		private readonly int blockAlign;
		private readonly bool useBitReservoir;
		private readonly bool useExponentVlc;
		private readonly bool useVariableBlockLength;
		private readonly bool useNoiseCoding;
		private readonly int byteOffsetBits;
		private readonly int frameLengthBits;
		private readonly int frameLength;
		private readonly int numberOfBlockSizes;
		private readonly int coefficientStart;
		private readonly int[] exponentSizes = new int[MaximumBlockSizes];
		private readonly ushort[][] exponentBands = CreateUShortPlanes(MaximumBlockSizes, 25);
		private readonly int[] highBandStart = new int[MaximumBlockSizes];
		private readonly int[] coefficientEnd = new int[MaximumBlockSizes];
		private readonly int[] exponentHighSizes = new int[MaximumBlockSizes];
		private readonly int[][] exponentHighBands = CreateIntPlanes(MaximumBlockSizes, HighBandMaximumSize);
		private readonly int[][] highBandCoded = CreateIntPlanes(2, HighBandMaximumSize);
		private readonly int[][] highBandValues = CreateIntPlanes(2, HighBandMaximumSize);
		private readonly WmaCoefficientTable[] coefficientVlcs = new WmaCoefficientTable[2];
		private readonly int[] exponentBlockSize = new int[2];
		private readonly bool[] exponentsInitialized = new bool[2];
		private readonly bool[] channelCoded = new bool[2];
		private readonly float[][] exponents = CreateFloatPlanes(2, BlockMaximumSize);
		private readonly float[] maximumExponent = { 1.0f, 1.0f };
		private readonly float[][] quantizedCoefficients = CreateFloatPlanes(2, BlockMaximumSize);
		private readonly float[][] coefficients = CreateFloatPlanes(2, BlockMaximumSize);
		private readonly float[] transformOutput = new float[BlockMaximumSize * 2];
		private readonly float[][] frameOutput = CreateFloatPlanes(2, BlockMaximumSize * 2);
		private readonly float[][] decodedSamples;
		private readonly FfmpegFloatMdct[] transforms = new FfmpegFloatMdct[MaximumBlockSizes];
		private readonly float[][] windows = new float[MaximumBlockSizes][];
		private readonly float[] noiseTable = new float[NoiseTableSize];
		private readonly float noiseMultiplier;
		private readonly float[] lspCosTable = new float[BlockMaximumSize];
		private readonly float[] lspPowerExponentTable = new float[256];
		private readonly float[] lspPowerMantissaTable1 = new float[1 << LspPowerBits];
		private readonly float[] lspPowerMantissaTable2 = new float[1 << LspPowerBits];
		private readonly byte[] lastSuperframe = new byte[MaximumCodedSuperframeSize + 64];
		private int lastBitOffset;
		private int lastSuperframeLength;
		private int nextBlockLengthBits;
		private int previousBlockLengthBits;
		private int blockLengthBits;
		private int blockLength;
		private int blockNumber;
		private int blockPosition;
		private int noiseIndex;
		private bool resetBlockLengths = true;
		private bool midSideStereo;
		private bool endOfFileDone;
		private int samplesToSkip;

		public int Channels => channels;
		public int SampleRate => sampleRate;
		public int FrameLength => frameLength;
		public int InitialSkipSampleCount => frameLength * 2;
		public int RandomAccessOutputDelaySampleCount => frameLength * (useBitReservoir ? 3 : 2);
		public int MaximumOutputBytes => MaximumFramesPerPacket * frameLength * channels * sizeof(float);

		/// <summary>Reads the WMA superframe header to count the decoded samples assigned to one sequential ASF packet.</summary>
		public int GetPacketDecodedSampleCount(ReadOnlySpan<byte> a_Packet, bool a_PreviousPacketAvailable)
		{
			if (a_Packet.Length < blockAlign)
				return 0;
			if (!useBitReservoir)
				return frameLength;
			var l_FrameCount = (a_Packet[0] & 0x0f) - (a_PreviousPacketAvailable ? 0 : 1);
			return l_FrameCount > 0 && l_FrameCount <= MaximumFramesPerPacket ? l_FrameCount * frameLength : 0;
		}

		/// <summary>
		/// Derives WMA v1/v2 frame, block, coefficient-band, noise-coding, and transform configuration from stream data.
		/// </summary>
		private WmaV1V2Decoder(AudioCodecId codecId, int sampleRate, int channels, long bitRate, int blockAlign, byte[] extraData)
		{
			version = codecId == AudioCodecId.WmaV1 ? 1 : 2;
			this.sampleRate = sampleRate;
			this.channels = channels;
			this.bitRate = bitRate;
			this.blockAlign = blockAlign;
			var flags = 0;
			if (version == 1 && extraData != null && extraData.Length >= 4)
				flags = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(2));
			else if (version == 2 && extraData != null && extraData.Length >= 6)
				flags = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(4));
			useExponentVlc = (flags & 1) != 0;
			useBitReservoir = (flags & 2) != 0;
			var variableBlockLength = (flags & 4) != 0;
			if (version == 2 && extraData != null && extraData.Length >= 8 &&
				BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(4)) == 0x0d && variableBlockLength)
				variableBlockLength = false;
			useVariableBlockLength = variableBlockLength;

			frameLengthBits = GetFrameLengthBits(sampleRate, version);
			nextBlockLengthBits = frameLengthBits;
			previousBlockLengthBits = frameLengthBits;
			blockLengthBits = frameLengthBits;
			frameLength = 1 << frameLengthBits;
			samplesToSkip = frameLength * 2;
			if (useVariableBlockLength)
			{
				var count = ((flags >> 3) & 3) + 1;
				if (bitRate / channels >= 32000) count += 2;
				var maximum = frameLengthBits - BlockMinimumBits;
				if (count > maximum) count = maximum;
				numberOfBlockSizes = count + 1;
			} else
			{
				numberOfBlockSizes = 1;
			}

			var highFrequency = sampleRate * 0.5f;
			var normalizedSampleRate = sampleRate;
			if (version == 2)
			{
				if (normalizedSampleRate >= 44100) normalizedSampleRate = 44100;
				else if (normalizedSampleRate >= 22050) normalizedSampleRate = 22050;
				else if (normalizedSampleRate >= 16000) normalizedSampleRate = 16000;
				else if (normalizedSampleRate >= 11025) normalizedSampleRate = 11025;
				else if (normalizedSampleRate >= 8000) normalizedSampleRate = 8000;
			}
			var bitsPerSample = (float)bitRate / (float)(channels * sampleRate);
			byteOffsetBits = FfmpegMath.Log2((uint)(int)(bitsPerSample * frameLength / 8.0 + 0.5)) + 2;
			var selectionBitsPerSample = channels == 2 ? bitsPerSample * 1.6f : bitsPerSample;
			var noiseCoding = true;
			if (normalizedSampleRate == 44100)
			{
				if (selectionBitsPerSample >= 0.61f) noiseCoding = false;
				else highFrequency *= 0.4f;
			} else if (normalizedSampleRate == 22050)
			{
				if (selectionBitsPerSample >= 1.16f) noiseCoding = false;
				else if (selectionBitsPerSample >= 0.72f) highFrequency *= 0.7f;
				else highFrequency *= 0.6f;
			} else if (normalizedSampleRate == 16000)
			{
				if (bitsPerSample > 0.5f) highFrequency *= 0.5f;
				else highFrequency *= 0.3f;
			} else if (normalizedSampleRate == 11025)
			{
				highFrequency *= 0.7f;
			} else if (normalizedSampleRate == 8000)
			{
				if (bitsPerSample <= 0.625f) highFrequency *= 0.5f;
				else if (bitsPerSample > 0.75f) noiseCoding = false;
				else highFrequency *= 0.65f;
			} else
			{
				if (bitsPerSample >= 0.8f) highFrequency *= 0.75f;
				else if (bitsPerSample >= 0.6f) highFrequency *= 0.6f;
				else highFrequency *= 0.5f;
			}
			useNoiseCoding = noiseCoding;
			coefficientStart = version == 1 ? 3 : 0;
			InitializeBands(highFrequency);
			for (var size = 0; size < numberOfBlockSizes; size++)
			{
				windows[size] = CodecWindows.GetSineWindow(frameLengthBits - size);
				transforms[size] = new FfmpegFloatMdct(1 << (frameLengthBits - size), true, 1.0f / 32768.0f, true);
			}

			if (useNoiseCoding)
			{
				noiseMultiplier = useExponentVlc ? 0.02f : 0.04f;
				var seed = 1U;
				var normalization = (float)((1.0 / (float)(1L << 31)) * Math.Sqrt(3.0) * noiseMultiplier);
				for (var index = 0; index < noiseTable.Length; index++)
				{
					seed = unchecked(seed * 314159U + 1U);
					noiseTable[index] = unchecked((int)seed) * normalization;
				}
			}
			if (!useExponentVlc) InitializeLspTables();
			var coefficientTable = 2;
			if (sampleRate >= 32000)
			{
				if (selectionBitsPerSample < 0.72f) coefficientTable = 0;
				else if (selectionBitsPerSample < 1.16f) coefficientTable = 1;
			}
			coefficientVlcs[0] = WmaTables.CoefficientVlcs[coefficientTable * 2];
			coefficientVlcs[1] = WmaTables.CoefficientVlcs[coefficientTable * 2 + 1];
			decodedSamples = CreateFloatPlanes(channels, MaximumFramesPerPacket * frameLength);
		}

		public static int Initialize(AudioCodecId codecId, int sampleRate, int channels, long bitRate,
			int blockAlign, byte[] extraData, out WmaV1V2Decoder decoder)
		{
			decoder = null;
			if ((codecId != AudioCodecId.WmaV1 && codecId != AudioCodecId.WmaV2) || sampleRate <= 0 || sampleRate > 50000 ||
				channels <= 0 || channels > 2 || bitRate <= 0 || blockAlign <= 0)
				return FfmpegError.InvalidArgument;
			var candidate = new WmaV1V2Decoder(codecId, sampleRate, channels, bitRate, blockAlign, extraData);
			if (candidate.byteOffsetBits + 3 > 25)
				return FfmpegError.NotImplemented;
			decoder = candidate;
			return 0;
		}

		/// <summary>Decodes one ASF-reassembled WMA superframe and writes every produced frame as planar float data.</summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packetLength == 0)
				return Drain(output, out frame);
			if (packet == null || packetOffset < 0 || packetLength < blockAlign || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			var bufferSize = blockAlign;
			if (reader.Initialize(packet, packetOffset, bufferSize * 8) < 0)
				return FfmpegError.InvalidData;
			var numberOfFrames = 1;
			if (useBitReservoir)
			{
				reader.SkipBits(4);
				numberOfFrames = (int)reader.ReadBits(4) - (lastSuperframeLength <= 0 ? 1 : 0);
				if (numberOfFrames <= 0)
				{
					var error = numberOfFrames < 0 || reader.BitsLeft <= 8;
					if (error) return FfmpegError.InvalidData;
					if (lastSuperframeLength + bufferSize - 1 > MaximumCodedSuperframeSize)
						return Fail(FfmpegError.InvalidData);
					var destination = lastSuperframeLength;
					var length = bufferSize - 1;
					while (length-- > 0) lastSuperframe[destination++] = (byte)reader.ReadBits(8);
					lastSuperframeLength += 8 * bufferSize - 8;
					return bufferSize;
				}
			}
			if (numberOfFrames > MaximumFramesPerPacket)
				return Fail(FfmpegError.InvalidData);
			var samplesOffset = 0;
			if (useBitReservoir)
			{
				var bitOffset = (int)reader.ReadBits(byteOffsetBits + 3);
				if (bitOffset > reader.BitsLeft)
					return Fail(FfmpegError.InvalidData);
				if (lastSuperframeLength > 0)
				{
					if (lastSuperframeLength + ((bitOffset + 7) >> 3) > MaximumCodedSuperframeSize)
						return Fail(FfmpegError.InvalidData);
					var destination = lastSuperframeLength;
					var appendedBits = bitOffset;
					while (appendedBits > 7)
					{
						lastSuperframe[destination++] = (byte)reader.ReadBits(8);
						appendedBits -= 8;
					}
					if (appendedBits > 0) lastSuperframe[destination] = (byte)(reader.ReadBits(appendedBits) << (8 - appendedBits));
					reader.Initialize(lastSuperframe, lastSuperframeLength * 8 + bitOffset);
					if (lastBitOffset > 0) reader.SkipBits(lastBitOffset);
					var result = DecodeOneFrame(samplesOffset);
					if (result < 0) return Fail(result);
					samplesOffset += frameLength;
					numberOfFrames--;
				}
				var headerBits = 4 + 4 + byteOffsetBits + 3;
				var position = bitOffset + headerBits;
				if (position >= MaximumCodedSuperframeSize * 8 || position > bufferSize * 8)
					return Fail(FfmpegError.InvalidData);
				reader.Initialize(packet, packetOffset + (position >> 3), (bufferSize - (position >> 3)) * 8);
				var remainder = position & 7;
				if (remainder > 0) reader.SkipBits(remainder);
				resetBlockLengths = true;
				for (var frameIndex = 0; frameIndex < numberOfFrames; frameIndex++)
				{
					var result = DecodeOneFrame(samplesOffset);
					if (result < 0) return Fail(result);
					samplesOffset += frameLength;
				}
				position = reader.Position + ((bitOffset + headerBits) & ~7);
				lastBitOffset = position & 7;
				position >>= 3;
				var length = bufferSize - position;
				if (length > MaximumCodedSuperframeSize || length < 0)
					return Fail(FfmpegError.InvalidData);
				lastSuperframeLength = length;
				Array.Copy(packet, packetOffset + position, lastSuperframe, 0, length);
			} else
			{
				var result = DecodeOneFrame(samplesOffset);
				if (result < 0) return Fail(result);
				samplesOffset += frameLength;
			}
			samplesOffset = SkipInitialSamples(samplesOffset);
			var writeResult = WriteOutput(output, samplesOffset, out frame);
			return writeResult < 0 ? writeResult : bufferSize;
		}

		public int Drain(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (endOfFileDone) return 0;
			for (var channel = 0; channel < channels; channel++)
				Array.Copy(frameOutput[channel], 0, decodedSamples[channel], 0, frameLength);
			lastSuperframeLength = 0;
			endOfFileDone = true;
			var samples = SkipInitialSamples(frameLength);
			return WriteOutput(output, samples, out frame);
		}

		public void Flush()
		{
			lastBitOffset = 0;
			lastSuperframeLength = 0;
			endOfFileDone = false;
			samplesToSkip = frameLength * 2;
		}

		private int SkipInitialSamples(int samples)
		{
			var skipped = Math.Min(samplesToSkip, samples);
			if (skipped != 0)
			{
				for (var channel = 0; channel < channels; channel++)
					Array.Copy(decodedSamples[channel], skipped, decodedSamples[channel], 0, samples - skipped);
				samplesToSkip -= skipped;
			}
			return samples - skipped;
		}

		/// <summary>Builds FFmpeg's version/rate-dependent exponent and high-band partitions without changing its integer rounding.</summary>
		private void InitializeBands(float highFrequency)
		{
			for (var size = 0; size < numberOfBlockSizes; size++)
			{
				var currentBlockLength = frameLength >> size;
				if (version == 1)
				{
					var lastPosition = 0;
					var band = 0;
					for (; band < 25; band++)
					{
						var position = (currentBlockLength * 2 * WmaTables.CriticalFrequencies[band] + (sampleRate >> 1)) / sampleRate;
						if (position > currentBlockLength) position = currentBlockLength;
						exponentBands[0][band] = (ushort)(position - lastPosition);
						if (position >= currentBlockLength) { band++; break; }
						lastPosition = position;
					}
					exponentSizes[0] = band;
				} else
				{
					byte[][] selected = null;
					var row = frameLengthBits - BlockMinimumBits - size;
					if (row >= 0 && row < 3)
					{
						if (sampleRate >= 44100) selected = WmaTables.ExponentBands44100;
						else if (sampleRate >= 32000) selected = WmaTables.ExponentBands32000;
						else if (sampleRate >= 22050) selected = WmaTables.ExponentBands22050;
					}
					if (selected != null)
					{
						var count = selected[row][0];
						for (var band = 0; band < count; band++) exponentBands[size][band] = selected[row][band + 1];
						exponentSizes[size] = count;
					} else
					{
						var count = 0;
						var lastPosition = 0;
						for (var band = 0; band < 25; band++)
						{
							var position = ((currentBlockLength * 2 * WmaTables.CriticalFrequencies[band]) + (sampleRate << 1)) / (4 * sampleRate);
							position <<= 2;
							if (position > currentBlockLength) position = currentBlockLength;
							if (position > lastPosition) exponentBands[size][count++] = (ushort)(position - lastPosition);
							if (position >= currentBlockLength) break;
							lastPosition = position;
						}
						exponentSizes[size] = count;
					}
				}
				coefficientEnd[size] = (frameLength - frameLength * 9 / 100) >> size;
				var frequencyPosition = (currentBlockLength * 2) * highFrequency / sampleRate;
				highBandStart[size] = (int)((double)frequencyPosition + 0.5);
				var highBandCount = 0;
				var currentPosition = 0;
				for (var band = 0; band < exponentSizes[size]; band++)
				{
					var start = currentPosition;
					currentPosition += exponentBands[size][band];
					var end = currentPosition;
					if (start < highBandStart[size]) start = highBandStart[size];
					if (end > coefficientEnd[size]) end = coefficientEnd[size];
					if (end > start) exponentHighBands[size][highBandCount++] = end - start;
				}
				exponentHighSizes[size] = highBandCount;
			}
		}

		private void InitializeLspTables()
		{
			var windowDelta = (float)(Math.PI / frameLength);
			for (var index = 0; index < frameLength; index++)
				lspCosTable[index] = (float)(2.0f * Math.Cos(windowDelta * index));
			for (var index = 0; index < lspPowerExponentTable.Length; index++)
				lspPowerExponentTable[index] = FfmpegMath.Exp2Float((index - 126) * -0.25f);
			var previous = 1.0f;
			for (var index = (1 << LspPowerBits) - 1; index >= 0; index--)
			{
				var mantissa = (1 << LspPowerBits) + index;
				var value = (float)mantissa * (0.5f / (1 << LspPowerBits));
				value = (float)(1.0 / Math.Sqrt(Math.Sqrt(value)));
				lspPowerMantissaTable1[index] = 2 * value - previous;
				lspPowerMantissaTable2[index] = previous - value;
				previous = value;
			}
		}

		private int DecodeOneFrame(int samplesOffset)
		{
			blockNumber = 0;
			blockPosition = 0;
			for (;;)
			{
				var result = DecodeBlock();
				if (result < 0) return result;
				if (result != 0) break;
			}
			for (var channel = 0; channel < channels; channel++)
			{
				Array.Copy(frameOutput[channel], 0, decodedSamples[channel], samplesOffset, frameLength);
				Array.Copy(frameOutput[channel], frameLength, frameOutput[channel], 0, frameLength);
			}
			return 0;
		}

		/// <summary>Replays one WMA transform block from its block-length header through inverse transform and overlap.</summary>
		private int DecodeBlock()
		{
			if (useVariableBlockLength)
			{
				var bits = FfmpegMath.Log2((uint)(numberOfBlockSizes - 1)) + 1;
				if (resetBlockLengths)
				{
					resetBlockLengths = false;
					var value = (int)reader.ReadBits(bits);
					if (value >= numberOfBlockSizes) return FfmpegError.InvalidData;
					previousBlockLengthBits = frameLengthBits - value;
					value = (int)reader.ReadBits(bits);
					if (value >= numberOfBlockSizes) return FfmpegError.InvalidData;
					blockLengthBits = frameLengthBits - value;
				} else
				{
					previousBlockLengthBits = blockLengthBits;
					blockLengthBits = nextBlockLengthBits;
				}
				var nextValue = (int)reader.ReadBits(bits);
				if (nextValue >= numberOfBlockSizes) return FfmpegError.InvalidData;
				nextBlockLengthBits = frameLengthBits - nextValue;
			} else
			{
				nextBlockLengthBits = frameLengthBits;
				previousBlockLengthBits = frameLengthBits;
				blockLengthBits = frameLengthBits;
			}
			var blockSizeIndex = frameLengthBits - blockLengthBits;
			if (blockSizeIndex >= numberOfBlockSizes) return FfmpegError.InvalidData;
			blockLength = 1 << blockLengthBits;
			if (blockPosition + blockLength > frameLength) return FfmpegError.InvalidData;
			if (channels == 2) midSideStereo = reader.ReadBit() != 0;
			var anyChannel = false;
			for (var channel = 0; channel < channels; channel++)
			{
				channelCoded[channel] = reader.ReadBit() != 0;
				anyChannel |= channelCoded[channel];
			}
			if (anyChannel)
			{
				var result = DecodeCodedBlock(blockSizeIndex);
				if (result < 0) return result;
			}
			var transform = transforms[blockSizeIndex];
			for (var channel = 0; channel < channels; channel++)
			{
				var halfBlock = blockLength / 2;
				if (channelCoded[channel]) transform.Transform(coefficients[channel], transformOutput);
				else if (!(midSideStereo && channel == 1)) Array.Clear(transformOutput, 0, transformOutput.Length);
				var outputIndex = frameLength / 2 + blockPosition - halfBlock;
				ApplyWindow(frameOutput[channel], outputIndex);
			}
			blockNumber++;
			blockPosition += blockLength;
			return blockPosition >= frameLength ? 1 : 0;
		}

		/// <summary>Decodes gains, exponent reuse, spectral RLE, noise substitution, and mid/side reconstruction for a coded block.</summary>
		private int DecodeCodedBlock(int blockSizeIndex)
		{
			var totalGain = 1;
			for (;;)
			{
				if (reader.BitsLeft < 7) return FfmpegError.InvalidData;
				var gain = (int)reader.ReadBits(7);
				totalGain += gain;
				if (gain != 127) break;
			}
			var coefficientBits = TotalGainToBits(totalGain);
			var coefficientCount = coefficientEnd[blockSizeIndex] - coefficientStart;
			Span<int> numberOfCoefficients = stackalloc int[2];
			for (var channel = 0; channel < channels; channel++) numberOfCoefficients[channel] = coefficientCount;
			if (useNoiseCoding)
			{
				for (var channel = 0; channel < channels; channel++)
					if (channelCoded[channel])
						for (var band = 0; band < exponentHighSizes[blockSizeIndex]; band++)
						{
							highBandCoded[channel][band] = (int)reader.ReadBit();
							if (highBandCoded[channel][band] != 0)
								numberOfCoefficients[channel] -= exponentHighBands[blockSizeIndex][band];
						}
				for (var channel = 0; channel < channels; channel++)
				{
					if (!channelCoded[channel]) continue;
					var value = int.MinValue;
					for (var band = 0; band < exponentHighSizes[blockSizeIndex]; band++)
						if (highBandCoded[channel][band] != 0)
						{
							if (value == int.MinValue) value = (int)reader.ReadBits(7) - 19;
							else value += reader.ReadVlc(WmaTables.HighGainVlc.Table, 9, 2);
							highBandValues[channel][band] = value;
						}
				}
			}
			if (blockLengthBits == frameLengthBits || reader.ReadBit() != 0)
			{
				for (var channel = 0; channel < channels; channel++)
				{
					if (!channelCoded[channel]) continue;
					var result = useExponentVlc ? DecodeExponentVlc(channel, blockSizeIndex) : DecodeExponentLsp(channel);
					if (result < 0) return result;
					exponentBlockSize[channel] = blockSizeIndex;
					exponentsInitialized[channel] = true;
				}
			}
			for (var channel = 0; channel < channels; channel++)
				if (channelCoded[channel] && !exponentsInitialized[channel]) return FfmpegError.InvalidData;
			for (var channel = 0; channel < channels; channel++)
			{
				if (channelCoded[channel])
				{
					Array.Clear(quantizedCoefficients[channel], 0, blockLength);
					var tableIndex = channel == 1 && midSideStereo ? 1 : 0;
					var result = DecodeRunLevel(coefficientVlcs[tableIndex], quantizedCoefficients[channel],
						numberOfCoefficients[channel], coefficientBits);
					if (result < 0) return result;
				}
				if (version == 1 && channels >= 2) reader.Align();
			}
			var quarterBlock = blockLength / 2;
			var transformNormalization = (float)(1.0 / (float)quarterBlock);
			if (version == 1) transformNormalization = (float)(transformNormalization * Math.Sqrt(quarterBlock));
			for (var channel = 0; channel < channels; channel++)
				if (channelCoded[channel]) ReconstructCoefficients(channel, blockSizeIndex, numberOfCoefficients[channel], totalGain, transformNormalization);
			if (midSideStereo && channelCoded[1])
			{
				if (!channelCoded[0])
				{
					Array.Clear(coefficients[0], 0, blockLength);
					channelCoded[0] = true;
				}
				for (var index = 0; index < blockLength; index++)
				{
					var difference = coefficients[0][index] - coefficients[1][index];
					coefficients[0][index] += coefficients[1][index];
					coefficients[1][index] = difference;
				}
			}
			return 0;
		}

		/// <summary>Expands FFmpeg's quantized spectrum and high-band synthetic noise in its original pointer order.</summary>
		private void ReconstructCoefficients(int channel, int blockSizeIndex, int numberOfCoefficients,
			int totalGain, float transformNormalization)
		{
			var source = quantizedCoefficients[channel];
			var exponent = exponents[channel];
			var exponentSize = exponentBlockSize[channel];
			var multiplier = (float)(double.Exp2(Log2Ten * (totalGain * 0.05)) / maximumExponent[channel]);
			multiplier *= transformNormalization;
			var destination = coefficients[channel];
			var destinationIndex = 0;
			var sourceIndex = 0;
			if (useNoiseCoding)
			{
				var secondaryMultiplier = multiplier;
				for (var index = 0; index < coefficientStart; index++)
				{
					destination[destinationIndex++] = noiseTable[noiseIndex] * exponent[index << blockSizeIndex >> exponentSize] * secondaryMultiplier;
					noiseIndex = noiseIndex + 1 & NoiseTableSize - 1;
				}
				var highBandCount = exponentHighSizes[blockSizeIndex];
				Span<float> exponentPower = stackalloc float[HighBandMaximumSize];
				var exponentIndex = highBandStart[blockSizeIndex] << blockSizeIndex >> exponentSize;
				var lastHighBand = 0;
				for (var band = 0; band < highBandCount; band++)
				{
					var count = exponentHighBands[blockSizeIndex][band];
					if (highBandCoded[channel][band] != 0)
					{
						var power = 0.0f;
						for (var index = 0; index < count; index++)
						{
							var value = exponent[exponentIndex + (index << blockSizeIndex >> exponentSize)];
							power += value * value;
						}
						exponentPower[band] = power / count;
						lastHighBand = band;
					}
					exponentIndex += count << blockSizeIndex >> exponentSize;
				}
				exponentIndex = coefficientStart << blockSizeIndex >> exponentSize;
				for (var band = -1; band < highBandCount; band++)
				{
					var count = band < 0 ? highBandStart[blockSizeIndex] - coefficientStart : exponentHighBands[blockSizeIndex][band];
					if (band >= 0 && highBandCoded[channel][band] != 0)
					{
						secondaryMultiplier = (float)Math.Sqrt(exponentPower[band] / exponentPower[lastHighBand]);
						secondaryMultiplier = (float)(secondaryMultiplier * double.Exp2(Log2Ten * (highBandValues[channel][band] * 0.05)));
						secondaryMultiplier /= maximumExponent[channel] * noiseMultiplier;
						secondaryMultiplier *= transformNormalization;
						for (var index = 0; index < count; index++)
						{
							destination[destinationIndex++] = noiseTable[noiseIndex] * exponent[exponentIndex + (index << blockSizeIndex >> exponentSize)] * secondaryMultiplier;
							noiseIndex = noiseIndex + 1 & NoiseTableSize - 1;
						}
						exponentIndex += count << blockSizeIndex >> exponentSize;
					} else
					{
						for (var index = 0; index < count; index++)
						{
							var noise = noiseTable[noiseIndex];
							noiseIndex = noiseIndex + 1 & NoiseTableSize - 1;
							destination[destinationIndex++] = (source[sourceIndex++] + noise) *
								exponent[exponentIndex + (index << blockSizeIndex >> exponentSize)] * multiplier;
						}
						exponentIndex += count << blockSizeIndex >> exponentSize;
					}
				}
				var tail = blockLength - coefficientEnd[blockSizeIndex];
				secondaryMultiplier = multiplier * exponent[exponentIndex - (1 << blockSizeIndex >> exponentSize)];
				for (var index = 0; index < tail; index++)
				{
					destination[destinationIndex++] = noiseTable[noiseIndex] * secondaryMultiplier;
					noiseIndex = noiseIndex + 1 & NoiseTableSize - 1;
				}
			} else
			{
				for (var index = 0; index < coefficientStart; index++) destination[destinationIndex++] = 0.0f;
				for (var index = 0; index < numberOfCoefficients; index++)
					destination[destinationIndex++] = source[index] * exponent[index << blockSizeIndex >> exponentSize] * multiplier;
				var tail = blockLength - coefficientEnd[blockSizeIndex];
				for (var index = 0; index < tail; index++) destination[destinationIndex++] = 0.0f;
			}
		}

		private int DecodeExponentVlc(int channel, int blockSizeIndex)
		{
			var destinationIndex = 0;
			var maximum = 0.0f;
			var bandIndex = 0;
			var lastExponent = 36;
			if (version == 1)
			{
				lastExponent = (int)reader.ReadBits(5) + 10;
				var value = WmaTables.Powers[60 + lastExponent];
				maximum = value;
				var count = exponentBands[blockSizeIndex][bandIndex++];
				for (var index = 0; index < count; index++) exponents[channel][destinationIndex++] = value;
			}
			while (destinationIndex < blockLength)
			{
				var code = reader.ReadVlc(WmaTables.ExponentVlc.Table, 8, 3);
				lastExponent += code - 60;
				if (lastExponent < -60 || lastExponent + 60 >= WmaTables.Powers.Length) return FfmpegError.InvalidData;
				var value = WmaTables.Powers[60 + lastExponent];
				if (value > maximum) maximum = value;
				var count = exponentBands[blockSizeIndex][bandIndex++];
				for (var index = 0; index < count; index++) exponents[channel][destinationIndex++] = value;
			}
			maximumExponent[channel] = maximum;
			return 0;
		}

		private int DecodeExponentLsp(int channel)
		{
			Span<float> lsp = stackalloc float[10];
			for (var index = 0; index < lsp.Length; index++)
			{
				var value = index == 0 || index >= 8 ? (int)reader.ReadBits(3) : (int)reader.ReadBits(4);
				lsp[index] = WmaTables.LspCodebook[index][value];
			}
			var maximum = 0.0f;
			for (var index = 0; index < blockLength; index++)
			{
				var p = 0.5f;
				var q = 0.5f;
				var window = lspCosTable[index];
				for (var coefficient = 1; coefficient < lsp.Length; coefficient += 2)
				{
					q *= window - lsp[coefficient - 1];
					p *= window - lsp[coefficient];
				}
				p *= p * (2.0f - window);
				q *= q * (2.0f + window);
				var value = PowMinusOneQuarter(p + q);
				if (value > maximum) maximum = value;
				exponents[channel][index] = value;
			}
			maximumExponent[channel] = maximum;
			return 0;
		}

		private float PowMinusOneQuarter(float value)
		{
			var bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
			var exponent = bits >> 23;
			var mantissa = bits >> (23 - LspPowerBits) & (1U << LspPowerBits) - 1;
			var normalizedBits = bits << LspPowerBits & (1U << 23) - 1 | 127U << 23;
			var normalized = BitConverter.Int32BitsToSingle(unchecked((int)normalizedBits));
			var first = lspPowerMantissaTable1[mantissa];
			var second = lspPowerMantissaTable2[mantissa];
			return lspPowerExponentTable[exponent] * (first + second * normalized);
		}

		private int DecodeRunLevel(WmaCoefficientTable table, float[] destination, int numberOfCoefficients, int coefficientBits)
		{
			var offset = 0;
			var mask = blockLength - 1;
			for (; offset < numberOfCoefficients; offset++)
			{
				var code = reader.ReadVlc(table.Vlc.Table, 9, 3);
				if (code > 1)
				{
					offset += table.Run[code];
					var sign = (int)reader.ReadBit() - 1;
					var bits = unchecked((uint)BitConverter.SingleToInt32Bits(table.Level[code])) ^ unchecked((uint)(sign & int.MinValue));
					destination[offset & mask] = BitConverter.Int32BitsToSingle(unchecked((int)bits));
				} else if (code == 1)
				{
					break;
				} else
				{
					var level = (int)reader.ReadBits(coefficientBits);
					offset += (int)reader.ReadBits(frameLengthBits);
					var sign = (int)reader.ReadBit() - 1;
					destination[offset & mask] = (level ^ sign) - sign;
				}
			}
			return offset > numberOfCoefficients ? FfmpegError.InvalidData : 0;
		}

		private void ApplyWindow(float[] output, int outputOffset)
		{
			if (blockLengthBits <= previousBlockLengthBits)
			{
				var window = windows[frameLengthBits - blockLengthBits];
				for (var index = 0; index < blockLength; index++)
					output[outputOffset + index] = transformOutput[index] * window[index] + output[outputOffset + index];
			} else
			{
				var previousLength = 1 << previousBlockLengthBits;
				var offset = (blockLength - previousLength) / 2;
				var window = windows[frameLengthBits - previousBlockLengthBits];
				for (var index = 0; index < previousLength; index++)
					output[outputOffset + offset + index] = transformOutput[offset + index] * window[index] + output[outputOffset + offset + index];
				Array.Copy(transformOutput, offset + previousLength, output, outputOffset + offset + previousLength, offset);
			}
			outputOffset += blockLength;
			if (blockLengthBits <= nextBlockLengthBits)
			{
				var window = windows[frameLengthBits - blockLengthBits];
				for (var index = 0; index < blockLength; index++)
					output[outputOffset + index] = transformOutput[blockLength + index] * window[blockLength - 1 - index];
			} else
			{
				var nextLength = 1 << nextBlockLengthBits;
				var offset = (blockLength - nextLength) / 2;
				Array.Copy(transformOutput, blockLength, output, outputOffset, offset);
				var window = windows[frameLengthBits - nextBlockLengthBits];
				for (var index = 0; index < nextLength; index++)
					output[outputOffset + offset + index] = transformOutput[blockLength + offset + index] * window[nextLength - 1 - index];
				Array.Clear(output, outputOffset + offset + nextLength, offset);
			}
		}

		private int WriteOutput(Span<byte> output, int samples, out AudioFrameInfo frame)
		{
			frame = default;
			var planeSize = samples * sizeof(float);
			var dataSize = planeSize * channels;
			if (output.Length < dataSize) return FfmpegError.InvalidArgument;
			for (var channel = 0; channel < channels; channel++)
				for (var sample = 0; sample < samples; sample++)
					BinaryPrimitives.WriteInt32LittleEndian(output.Slice(channel * planeSize + sample * 4, 4),
						BitConverter.SingleToInt32Bits(decodedSamples[channel][sample]));
			frame = new AudioFrameInfo(samples, channels, AudioSampleFormat.FloatPlanar, channels, planeSize, dataSize);
			return 0;
		}

		private int Fail(int error)
		{
			lastSuperframeLength = 0;
			return error;
		}

		private static int TotalGainToBits(int totalGain)
		{
			if (totalGain < 15) return 13;
			if (totalGain < 32) return 12;
			if (totalGain < 40) return 11;
			if (totalGain < 45) return 10;
			return 9;
		}

		private static int GetFrameLengthBits(int rate, int codecVersion)
		{
			if (rate <= 16000) return 9;
			if (rate <= 22050 || rate <= 32000 && codecVersion == 1) return 10;
			return 11;
		}

		private static float[][] CreateFloatPlanes(int count, int length)
		{
			var result = new float[count][];
			for (var index = 0; index < count; index++) result[index] = new float[length];
			return result;
		}

		private static int[][] CreateIntPlanes(int count, int length)
		{
			var result = new int[count][];
			for (var index = 0; index < count; index++) result[index] = new int[length];
			return result;
		}

		private static ushort[][] CreateUShortPlanes(int count, int length)
		{
			var result = new ushort[count][];
			for (var index = 0; index < count; index++) result[index] = new ushort[length];
			return result;
		}
	}
}
