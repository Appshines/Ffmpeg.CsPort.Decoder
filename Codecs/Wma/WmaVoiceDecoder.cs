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

namespace Ffmpeg.CsPort.Decoder.Codecs.Wma
{
	/// <summary>
	/// Ports FFmpeg's WMA Voice decoder, including packet spillover, LSP dequantization, ACELP synthesis,
	/// adaptive postfiltering, Wiener denoising, and packed mono float output.
	/// </summary>
	public sealed class WmaVoiceDecoder
	{
		private const int MaximumBlocks = 8;
		private const int MaximumLsps = 16;
		private const int MaximumFrames = 3;
		private const int MaximumFrameSize = 160;
		private const int MaximumSignalHistory = 416;
		private const int MaximumSuperframeSize = MaximumFrameSize * MaximumFrames;
		private const int SuperframeCacheMaximumSize = 256;
		private const int AdaptiveCodebookNone = 0;
		private const int AdaptiveCodebookAsymmetric = 1;
		private const int AdaptiveCodebookHamming = 2;
		private const int FixedCodebookSilence = 0;
		private const int FixedCodebookHardcoded = 1;
		private const int FixedCodebookAwPulses = 2;
		private const int FixedCodebookExcitationPulses = 3;
		private static readonly Vlc FrameTypeVlc = CreateFrameTypeVlc();
		private static readonly FrameDescriptor[] FrameDescriptors =
		{
			new FrameDescriptor(1, 0, AdaptiveCodebookNone, FixedCodebookSilence, 0),
			new FrameDescriptor(2, 1, AdaptiveCodebookNone, FixedCodebookHardcoded, 0),
			new FrameDescriptor(2, 1, AdaptiveCodebookAsymmetric, FixedCodebookAwPulses, 0),
			new FrameDescriptor(2, 1, AdaptiveCodebookAsymmetric, FixedCodebookExcitationPulses, 2),
			new FrameDescriptor(2, 1, AdaptiveCodebookAsymmetric, FixedCodebookExcitationPulses, 5),
			new FrameDescriptor(4, 2, AdaptiveCodebookAsymmetric, FixedCodebookExcitationPulses, 0),
			new FrameDescriptor(4, 2, AdaptiveCodebookAsymmetric, FixedCodebookExcitationPulses, 2),
			new FrameDescriptor(4, 2, AdaptiveCodebookAsymmetric, FixedCodebookExcitationPulses, 5),
			new FrameDescriptor(2, 1, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 0),
			new FrameDescriptor(2, 1, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 2),
			new FrameDescriptor(2, 1, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 5),
			new FrameDescriptor(4, 2, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 0),
			new FrameDescriptor(4, 2, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 2),
			new FrameDescriptor(4, 2, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 5),
			new FrameDescriptor(8, 3, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 0),
			new FrameDescriptor(8, 3, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 2),
			new FrameDescriptor(8, 3, AdaptiveCodebookHamming, FixedCodebookExcitationPulses, 5)
		};
		private static readonly short[] AwStartOffsets =
		{
			-11, -9, -7, -5, -3, -1, 1, 3, 5, 7, 9, 11, 13, 15, 18, 17, 19, 20, 21, 22, 23, 24,
			25, 26, 27, 28, 29, 30, 31, 32, 33, 35, 37, 39, 41, 43, 45, 47, 49, 51, 53, 55, 57, 59,
			61, 63, 65, 67, 69, 71, 73, 75, 77, 79, 81, 83, 85, 87, 89, 91, 93, 95, 97, 99, 101, 103,
			105, 107, 109, 111, 113, 115, 117, 119, 121, 123, 125, 127, 129, 131, 133, 135, 137, 139,
			141, 143, 145, 147, 149, 151, 153, 155, 157, 159
		};
		private static readonly uint[,] RandomDivisors =
		{
			{ 8332, unchecked(3U * 715827883U) }, { 4545, 0 }, { 3124, unchecked(11U * 268435456U) },
			{ 2380, unchecked(15U * 204522253U) }, { 1922, unchecked(23U * 165191050U) },
			{ 1612, unchecked(23U * 138547333U) }, { 1388, unchecked(27U * 119304648U) },
			{ 1219, unchecked(16U * 104755300U) }, { 1086, unchecked(39U * 93368855U) }
		};
		private static readonly float[] GainPredictionCoefficients = { 0.8169f, -0.06545f, 0.1726f, 0.0185f, -0.0359f, 0.0458f };
		private readonly BitReader packetReader = new BitReader();
		private readonly BitReader cacheReader = new BitReader();
		private readonly int sampleRate;
		private readonly int blockAlign;
		private readonly int spilloverBitSize;
		private readonly bool doAdaptivePostfilter;
		private readonly int denoiseStrength;
		private readonly bool denoiseTiltCorrection;
		private readonly int dcLevel;
		private readonly int lspCount;
		private readonly bool lspQuantizationMode;
		private readonly int lspDefinitionMode;
		private readonly sbyte[] vbmTree = new sbyte[25];
		private readonly int minimumPitchValue;
		private readonly int maximumPitchValue;
		private readonly int pitchBitCount;
		private readonly int historySampleCount;
		private readonly ushort[] blockConversionTable = new ushort[4];
		private readonly int blockDeltaPitchHalfRange;
		private readonly int blockDeltaPitchBitCount;
		private readonly int blockPitchRange;
		private readonly int blockPitchBitCount;
		private readonly byte[] superframeCache = new byte[SuperframeCacheMaximumSize + 64];
		private readonly double[] previousLsps = new double[MaximumLsps];
		private readonly float[] gainPredictionError = new float[6];
		private readonly float[] excitationHistory = new float[MaximumSignalHistory];
		private readonly float[] synthesisHistory = new float[MaximumLsps];
		private readonly float[] zeroExcitationPostfilter = new float[MaximumSignalHistory + MaximumSuperframeSize];
		private readonly float[] denoiseFilterCache = new float[MaximumFrameSize];
		private readonly float[] tiltedLpcsPostfilter = new float[130];
		private readonly float[] denoiseCoefficientsPostfilter = new float[130];
		private readonly float[] synthesisFilterOutput = new float[16 + 130];
		private readonly float[] dcfMemory = new float[2];
		private readonly float[] sine = new float[511];
		private readonly float[] cosine = new float[511];
		private readonly FfmpegFloatRealTransforms realTransforms;
		private readonly double[] frameLsps = new double[MaximumFrames * MaximumLsps];
		private readonly double[] residualPreviousLsps = new double[MaximumLsps];
		private readonly double[] residualA1 = new double[MaximumLsps * 2];
		private readonly double[] residualA2 = new double[MaximumLsps * 2];
		private readonly double[] interpolatedLsps = new double[MaximumLsps];
		private readonly float[] excitation = new float[MaximumSignalHistory + MaximumSuperframeSize + 12];
		private readonly float[] synthesis = new float[MaximumLsps + MaximumSuperframeSize];
		private readonly float[] superframeSamples = new float[MaximumSuperframeSize];
		private readonly float[] outputSamples = new float[MaximumSuperframeSize * 64];
		private readonly float[] lpcs = new float[MaximumLsps];
		private readonly double[] polynomialP = new double[MaximumLsps / 2 + 1];
		private readonly double[] polynomialQ = new double[MaximumLsps / 2 + 1];
		private readonly int[] pitches = new int[MaximumBlocks];
		private readonly float[] pulses = new float[MaximumFrameSize / 2];
		private readonly FixedVector fixedVector = new FixedVector();
		private readonly ushort[] useMask = new ushort[9];
		private readonly float[] interpolationBuffer = new float[MaximumFrameSize / 2];
		private readonly float[] responseCoefficients = new float[130];
		private readonly float[] responseLpcs = new float[130];
		private readonly float[] responseLpcsDct = new float[130];
		private readonly float[] frequencyCoefficients = new float[130];
		private readonly float[] frequencySynthesis = new float[130];
		private int spilloverBits;
		private bool hasResidualLsps;
		private int skipBitsNext;
		private int superframeCacheBits;
		private int numberOfSuperframes;
		private int lastPitchValue = 40;
		private int lastAdaptiveCodebookType;
		private int pitchDifferenceShift16;
		private float silenceGain;
		private bool awIndexIsExtended;
		private int awPulseRange;
		private readonly int[] awPulseCounts = new int[2];
		private readonly int[] awFirstPulseOffsets = new int[2];
		private int awNextPulseOffsetCache;
		private int frameCounter;
		private float postfilterGain;
		private int denoiseFilterCacheSize;
		private int decodedOutputSamples;
		private bool drained;

		private WmaVoiceDecoder(int sampleRate, int blockAlign, byte[] extraData)
		{
			this.sampleRate = sampleRate;
			this.blockAlign = blockAlign;
			var flags = BinaryPrimitives.ReadInt32LittleEndian(extraData.AsSpan(18));
			spilloverBitSize = 3 + FfmpegMath.CeilLog2(blockAlign);
			doAdaptivePostfilter = (flags & 1) != 0;
			denoiseStrength = flags >> 2 & 15;
			denoiseTiltCorrection = (flags & 0x40) != 0;
			dcLevel = flags >> 7 & 15;
			lspQuantizationMode = (flags & 0x2000) != 0;
			lspDefinitionMode = (flags & 0x4000) != 0 ? 1 : 0;
			lspCount = (flags & 0x1000) != 0 ? 16 : 10;
			for (var index = 0; index < lspCount; index++) previousLsps[index] = Math.PI * (index + 1.0) / (lspCount + 1.0);
			var extraReader = new BitReader();
			extraReader.Initialize(extraData, 22, (extraData.Length - 22) * 8);
			DecodeVbmTree(extraReader, vbmTree);
			minimumPitchValue = ((sampleRate << 8) / 400 + 50) >> 8;
			maximumPitchValue = ((sampleRate << 8) * 37 / 2000 + 50) >> 8;
			var pitchRange = maximumPitchValue - minimumPitchValue;
			pitchBitCount = FfmpegMath.CeilLog2(pitchRange);
			historySampleCount = maximumPitchValue + 8;
			blockConversionTable[0] = (ushort)minimumPitchValue;
			blockConversionTable[1] = (ushort)(pitchRange * 25 >> 6);
			blockConversionTable[2] = (ushort)(pitchRange * 44 >> 6);
			blockConversionTable[3] = (ushort)(maximumPitchValue - 1);
			blockDeltaPitchHalfRange = pitchRange >> 3 & ~15;
			blockDeltaPitchBitCount = 1 + FfmpegMath.CeilLog2(blockDeltaPitchHalfRange);
			blockPitchRange = blockConversionTable[2] + blockConversionTable[3] + 1 + 2 * (blockConversionTable[1] - 2 * minimumPitchValue);
			blockPitchBitCount = FfmpegMath.CeilLog2(blockPitchRange);
			if (doAdaptivePostfilter)
			{
				realTransforms = new FfmpegFloatRealTransforms();
				InitializeSineWindows();
			}
		}

		public int Channels => 1;
		public int SampleRate => sampleRate;
		public int MaximumOutputBytes => outputSamples.Length * sizeof(float);

		public static int Initialize(int sampleRate, int channels, long bitRate, int blockAlign, byte[] extraData, out WmaVoiceDecoder decoder)
		{
			decoder = null;
			if (extraData == null || extraData.Length != 46 || blockAlign <= 0 || blockAlign > 1 << 22 || sampleRate <= 0 ||
				channels <= 0 || bitRate <= 0) return FfmpegError.InvalidData;
			var flags = BinaryPrimitives.ReadInt32LittleEndian(extraData.AsSpan(18));
			if ((flags >> 2 & 15) >= 12 || sampleRate >= int.MaxValue / (256 * 37)) return FfmpegError.InvalidData;
			var minimumPitch = ((sampleRate << 8) / 400 + 50) >> 8;
			var maximumPitch = ((sampleRate << 8) * 37 / 2000 + 50) >> 8;
			var pitchRange = maximumPitch - minimumPitch;
			var deltaHalfRange = pitchRange >> 3 & ~15;
			if (pitchRange <= 0 || deltaHalfRange <= 0) return FfmpegError.InvalidData;
			if (minimumPitch < 1 || maximumPitch + 8 > MaximumSignalHistory) return FfmpegError.NotImplemented;
			var reader = new BitReader();
			reader.Initialize(extraData, 22, (extraData.Length - 22) * 8);
			var tree = new sbyte[25];
			if (DecodeVbmTree(reader, tree) < 0) return FfmpegError.InvalidData;
			decoder = new WmaVoiceDecoder(sampleRate, blockAlign, extraData);
			return 0;
		}

		/// <summary>Consumes one ASF media object and emits every WMA Voice superframe as packed mono floats.</summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packetLength == 0) return Drain(output, out frame);
			if (packet == null || packetOffset < 0 || packetLength < 0 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			drained = false;
			decodedOutputSamples = 0;
			var consumed = 0;
			while (consumed < packetLength)
			{
				var result = DecodePacketCall(packet, packetOffset + consumed, packetLength - consumed);
				if (result < 0) return result;
				if (result == 0) break;
				consumed += result;
			}
			var write = WriteOutput(output, out frame);
			return write < 0 ? write : packetLength;
		}

		public int Drain(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (drained) return 0;
			decodedOutputSamples = 0;
			DecodePacketCall(Array.Empty<byte>(), 0, 0);
			drained = true;
			return WriteOutput(output, out frame);
		}

		public void Flush()
		{
			postfilterGain = 0.0f;
			superframeCacheBits = 0;
			skipBitsNext = 0;
			for (var index = 0; index < lspCount; index++) previousLsps[index] = Math.PI * (index + 1.0) / (lspCount + 1.0);
			Array.Clear(excitationHistory, 0, excitationHistory.Length);
			Array.Clear(synthesisHistory, 0, synthesisHistory.Length);
			Array.Clear(gainPredictionError, 0, gainPredictionError.Length);
			if (doAdaptivePostfilter)
			{
				Array.Clear(synthesisFilterOutput, MaximumLsps - lspCount, lspCount);
				Array.Clear(dcfMemory, 0, dcfMemory.Length);
				Array.Clear(zeroExcitationPostfilter, 0, historySampleCount);
				Array.Clear(denoiseFilterCache, 0, denoiseFilterCache.Length);
			}
			drained = false;
		}

		/// <summary>Mirrors one FFmpeg decoder callback so byte-level partial packet consumption remains unchanged.</summary>
		private int DecodePacketCall(byte[] packet, int packetOffset, int packetLength)
		{
			var size = packetLength;
			while (size > blockAlign) size -= blockAlign;
			if (packetReader.Initialize(packet, packetOffset, size * 8) < 0) return FfmpegError.InvalidData;
			if (size % blockAlign == 0)
			{
				if (size == 0)
				{
					spilloverBits = 0;
					numberOfSuperframes = 0;
				} else
				{
					var header = ParsePacketHeader(packetReader);
					if (header < 0) return header;
					numberOfSuperframes = header;
				}
				if (superframeCacheBits > 0)
				{
					var count = packetReader.Position;
					if (count + spilloverBits > packetLength * 8) spilloverBits = packetLength * 8 - count;
					AppendCacheBits(packetReader, Math.Max(spilloverBits, 0));
					if (SynthesizeSuperframeFromCache() == 0)
					{
						count += spilloverBits;
						skipBitsNext = count & 7;
						return count >> 3;
					}
					packetReader.SkipBits(spilloverBits - count + packetReader.Position);
				} else if (spilloverBits != 0) packetReader.SkipBits(spilloverBits);
			} else if (skipBitsNext != 0) packetReader.SkipBits(skipBitsNext);
			superframeCacheBits = 0;
			skipBitsNext = 0;
			var bitsLeft = packetReader.BitsLeft;
			if (numberOfSuperframes-- == 0) return size;
			if (numberOfSuperframes > 0)
			{
				var result = SynthesizeSuperframe(packetReader);
				if (result < 0) return result;
				var count = packetReader.Position;
				skipBitsNext = count & 7;
				return count >> 3;
			}
			if (bitsLeft > 0)
			{
				superframeCacheBits = 0;
				AppendCacheBits(packetReader, bitsLeft);
			}
			return size;
		}

		private int ParsePacketHeader(BitReader reader)
		{
			reader.SkipBits(4);
			hasResidualLsps = reader.ReadBit() != 0;
			uint value;
			var superframes = 0;
			do
			{
				if (reader.BitsLeft < 6 + spilloverBitSize) return FfmpegError.InvalidData;
				value = reader.ReadBits(6);
				superframes += (int)value;
			} while (value == 63);
			spilloverBits = (int)reader.ReadBits(spilloverBitSize);
			return reader.BitsLeft >= 0 ? superframes : FfmpegError.InvalidData;
		}

		private void AppendCacheBits(BitReader source, int bitCount)
		{
			if (superframeCacheBits + bitCount > SuperframeCacheMaximumSize * 8) return;
			for (var index = 0; index < bitCount; index++)
			{
				var destination = superframeCacheBits + index;
				var mask = (byte)(1 << (7 - (destination & 7)));
				if (source.ReadBit() != 0) superframeCache[destination >> 3] |= mask;
				else superframeCache[destination >> 3] &= (byte)~mask;
			}
			superframeCacheBits += bitCount;
		}

		private int SynthesizeSuperframeFromCache()
		{
			cacheReader.Initialize(superframeCache, superframeCacheBits);
			superframeCacheBits = 0;
			return SynthesizeSuperframe(cacheReader);
		}

		/// <summary>Decodes the three frames in one superframe and commits all predictor histories only after success.</summary>
		private int SynthesizeSuperframe(BitReader reader)
		{
			var sampleCount = MaximumSuperframeSize;
			Array.Copy(synthesisHistory, 0, synthesis, 0, lspCount);
			Array.Copy(excitationHistory, 0, excitation, 0, historySampleCount);
			if (reader.ReadBit() == 0) return FfmpegError.PatchWelcome;
			if (reader.ReadBit() != 0)
			{
				sampleCount = (int)reader.ReadBits(12);
				if (sampleCount > MaximumSuperframeSize) return FfmpegError.InvalidData;
			}
			var mean = lspCount == 16 ? WmaVoiceTables.MeanLsf16 : WmaVoiceTables.MeanLsf10;
			var meanOffset = lspDefinitionMode * lspCount;
			if (hasResidualLsps)
			{
				for (var index = 0; index < lspCount; index++) residualPreviousLsps[index] = previousLsps[index] - mean[meanOffset + index];
				if (lspCount == 10) DequantizeLsp10Residual(reader, 2 * MaximumLsps, residualPreviousLsps, residualA1, residualA2);
				else DequantizeLsp16Residual(reader, 2 * MaximumLsps, residualPreviousLsps, residualA1, residualA2);
				for (var index = 0; index < lspCount; index++)
				{
					frameLsps[index] = mean[meanOffset + index] + residualA1[index] - residualA2[index * 2];
					frameLsps[MaximumLsps + index] = mean[meanOffset + index] + residualA1[lspCount + index] - residualA2[index * 2 + 1];
					frameLsps[2 * MaximumLsps + index] += mean[meanOffset + index];
				}
				for (var frame = 0; frame < MaximumFrames; frame++) StabilizeLsps(frameLsps, frame * MaximumLsps, lspCount);
			}
			for (var frame = 0; frame < MaximumFrames; frame++)
			{
				var lspOffset = frame * MaximumLsps;
				if (!hasResidualLsps)
				{
					if (lspCount == 10) DequantizeLsp10Independent(reader, frameLsps, lspOffset);
					else DequantizeLsp16Independent(reader, frameLsps, lspOffset);
					for (var index = 0; index < lspCount; index++) frameLsps[lspOffset + index] += mean[meanOffset + index];
					StabilizeLsps(frameLsps, lspOffset, lspCount);
				}
				var previous = frame == 0 ? previousLsps : frameLsps;
				var previousOffset = frame == 0 ? 0 : (frame - 1) * MaximumLsps;
				var result = SynthesizeFrame(reader, frame, frameLsps, lspOffset, previous, previousOffset,
					historySampleCount + frame * MaximumFrameSize, lspCount + frame * MaximumFrameSize, frame * MaximumFrameSize);
				if (result < 0) return result;
			}
			if (reader.ReadBit() != 0)
			{
				var count = (int)reader.ReadBits(4);
				reader.SkipBits(10 * (count + 1));
			}
			if (reader.BitsLeft < 0)
			{
				Flush();
				return FfmpegError.InvalidData;
			}
			for (var index = 0; index < lspCount; index++) previousLsps[index] = frameLsps[2 * MaximumLsps + index];
			Array.Copy(synthesis, MaximumSuperframeSize, synthesisHistory, 0, lspCount);
			Array.Copy(excitation, MaximumSuperframeSize, excitationHistory, 0, historySampleCount);
			if (doAdaptivePostfilter) Array.Copy(zeroExcitationPostfilter, MaximumSuperframeSize, zeroExcitationPostfilter, 0, historySampleCount);
			if (decodedOutputSamples + sampleCount > outputSamples.Length) return FfmpegError.InvalidArgument;
			Array.Copy(superframeSamples, 0, outputSamples, decodedOutputSamples, sampleCount);
			decodedOutputSamples += sampleCount;
			return 0;
		}

		/// <summary>Decodes one 160-sample Voice frame block-by-block and applies the optional two-stage postfilter.</summary>
		private int SynthesizeFrame(BitReader reader, int frameIndex, double[] currentLsps, int currentLspOffset,
			double[] oldLsps, int oldLspOffset, int excitationOffset, int synthesisOffset, int sampleOffset)
		{
			var treeIndex = reader.ReadVlc(FrameTypeVlc.Table, FrameTypeVlc.RootBits, 3);
			var descriptorIndex = treeIndex >= 0 && treeIndex < vbmTree.Length ? vbmTree[treeIndex] : -1;
			if (descriptorIndex < 0) return FfmpegError.InvalidData;
			var descriptor = FrameDescriptors[descriptorIndex];
			var blockSamples = MaximumFrameSize / descriptor.BlockCount;
			pitches[0] = int.MaxValue;
			var currentPitch = 0;
			if (descriptor.AdaptiveCodebookType == AdaptiveCodebookAsymmetric)
			{
				var blocksTimesTwo = descriptor.BlockCount << 1;
				var logBlocksTimesTwo = descriptor.LogBlockCount + 1;
				currentPitch = minimumPitchValue + (int)reader.ReadBits(pitchBitCount);
				currentPitch = Math.Min(currentPitch, maximumPitchValue - 1);
				if (lastAdaptiveCodebookType == AdaptiveCodebookNone || 20 * Math.Abs(currentPitch - lastPitchValue) > currentPitch + lastPitchValue)
					lastPitchValue = currentPitch;
				for (var block = 0; block < descriptor.BlockCount; block++)
				{
					var factor = block * 2 + 1;
					pitches[block] = (factor * currentPitch + (blocksTimesTwo - factor) * lastPitchValue + descriptor.BlockCount) >> logBlocksTimesTwo;
				}
				pitchDifferenceShift16 = (currentPitch - lastPitchValue) * (1 << 16) / MaximumFrameSize;
			}
			if (descriptor.FixedCodebookType == FixedCodebookSilence) silenceGain = WmaVoiceTables.GainSilence[reader.ReadBits(8)];
			else if (descriptor.FixedCodebookType == FixedCodebookAwPulses) ParseAwCoordinates(reader, pitches);
			var lastBlockPitch = 0;
			for (var block = 0; block < descriptor.BlockCount; block++)
			{
				var blockPitchShift2 = 0;
				if (descriptor.AdaptiveCodebookType == AdaptiveCodebookHamming)
				{
					var firstRange = (blockConversionTable[1] - blockConversionTable[0]) << 2;
					var secondRange = (blockConversionTable[2] - blockConversionTable[1]) << 1;
					var thirdRange = blockConversionTable[3] - blockConversionTable[2] + 1;
					var blockPitch = block == 0 ? (int)reader.ReadBits(blockPitchBitCount) :
						lastBlockPitch - blockDeltaPitchHalfRange + (int)reader.ReadBits(blockDeltaPitchBitCount);
					lastBlockPitch = FfmpegMath.Clip(blockPitch, blockDeltaPitchHalfRange, blockPitchRange - blockDeltaPitchHalfRange);
					if (blockPitch < firstRange) blockPitchShift2 = (blockConversionTable[0] << 2) + blockPitch;
					else
					{
						blockPitch -= firstRange;
						if (blockPitch < secondRange) blockPitchShift2 = (blockConversionTable[1] << 2) + (blockPitch << 1);
						else
						{
							blockPitch -= secondRange;
							blockPitchShift2 = blockPitch < thirdRange ? (blockConversionTable[2] + blockPitch) << 2 : blockConversionTable[3] << 2;
						}
					}
					pitches[block] = blockPitchShift2 >> 2;
				} else if (descriptor.AdaptiveCodebookType == AdaptiveCodebookAsymmetric) blockPitchShift2 = pitches[block] << 2;
				SynthesizeBlock(reader, block, blockSamples, blockPitchShift2, currentLsps, currentLspOffset, oldLsps, oldLspOffset,
					descriptor, excitationOffset + block * blockSamples, synthesisOffset + block * blockSamples);
			}
			if (doAdaptivePostfilter)
			{
				if (descriptor.FixedCodebookType >= FixedCodebookAwPulses && pitches[0] == int.MaxValue) return FfmpegError.InvalidData;
				for (var index = 0; index < lspCount; index++) interpolatedLsps[index] = Math.Cos(0.5 * (oldLsps[oldLspOffset + index] + currentLsps[currentLspOffset + index]));
				LspToLpc(interpolatedLsps, lpcs, lspCount >> 1);
				Postfilter(synthesis, synthesisOffset, superframeSamples, sampleOffset, 80, lpcs,
					historySampleCount + MaximumFrameSize * frameIndex, descriptor.FixedCodebookType, pitches[0]);
				for (var index = 0; index < lspCount; index++) interpolatedLsps[index] = Math.Cos(currentLsps[currentLspOffset + index]);
				LspToLpc(interpolatedLsps, lpcs, lspCount >> 1);
				Postfilter(synthesis, synthesisOffset + 80, superframeSamples, sampleOffset + 80, 80, lpcs,
					historySampleCount + MaximumFrameSize * frameIndex + 80, descriptor.FixedCodebookType, pitches[0]);
			} else Array.Copy(synthesis, synthesisOffset, superframeSamples, sampleOffset, MaximumFrameSize);
			frameCounter++;
			if (frameCounter >= 0xffff) frameCounter -= 0xffff;
			lastAdaptiveCodebookType = descriptor.AdaptiveCodebookType;
			if (descriptor.AdaptiveCodebookType == AdaptiveCodebookNone) lastPitchValue = 0;
			else if (descriptor.AdaptiveCodebookType == AdaptiveCodebookAsymmetric) lastPitchValue = currentPitch;
			else lastPitchValue = pitches[descriptor.BlockCount - 1];
			return 0;
		}

		private void SynthesizeBlock(BitReader reader, int blockIndex, int size, int blockPitchShift2,
			double[] currentLsps, int currentOffset, double[] oldLsps, int oldOffset, FrameDescriptor descriptor,
			int excitationOffset, int synthesisOffset)
		{
			if (descriptor.AdaptiveCodebookType == AdaptiveCodebookNone) SynthesizeHardcodedBlock(reader, blockIndex, size, descriptor, excitationOffset);
			else SynthesizeCodebookBlock(reader, blockIndex, size, blockPitchShift2, descriptor, excitationOffset);
			var factor = (float)((blockIndex + 0.5) / descriptor.BlockCount);
			for (var index = 0; index < lspCount; index++)
				interpolatedLsps[index] = Math.Cos(oldLsps[oldOffset + index] + factor * (currentLsps[currentOffset + index] - oldLsps[oldOffset + index]));
			LspToLpc(interpolatedLsps, lpcs, lspCount >> 1);
			SynthesisFilter(synthesis, synthesisOffset, lpcs, excitation, excitationOffset, size, lspCount);
		}

		private void SynthesizeHardcodedBlock(BitReader reader, int blockIndex, int size, FrameDescriptor descriptor, int outputOffset)
		{
			int randomIndex;
			float gain;
			if (descriptor.FixedCodebookType == FixedCodebookSilence)
			{
				randomIndex = PseudoRandom(frameCounter, blockIndex, size);
				gain = silenceGain;
			} else
			{
				randomIndex = (int)reader.ReadBits(8);
				gain = WmaVoiceTables.GainUniversal[reader.ReadBits(6)];
			}
			Array.Clear(gainPredictionError, 0, gainPredictionError.Length);
			for (var index = 0; index < size; index++) excitation[outputOffset + index] = WmaVoiceTables.StdCodebook[randomIndex + index] * gain;
		}

		/// <summary>Reconstructs the fixed and adaptive codebooks without changing FFmpeg's pulse and interpolation schedules.</summary>
		private void SynthesizeCodebookBlock(BitReader reader, int blockIndex, int size, int blockPitchShift2,
			FrameDescriptor descriptor, int excitationOffset)
		{
			Array.Clear(pulses, 0, size);
			fixedVector.PitchLag = blockPitchShift2 >> 2;
			fixedVector.PitchFactor = 1.0f;
			fixedVector.NoRepeatMask = 0;
			fixedVector.Count = 0;
			if (descriptor.FixedCodebookType == FixedCodebookAwPulses)
			{
				DecodeAwPulseSetOne(reader, blockIndex, fixedVector);
				if (DecodeAwPulseSetTwo(reader, blockIndex, fixedVector) != 0)
				{
					var randomIndex = PseudoRandom(frameCounter, blockIndex, size);
					for (var index = 0; index < size; index++) excitation[excitationOffset + index] = WmaVoiceTables.StdCodebook[randomIndex + index] * silenceGain;
					reader.SkipBits(8);
					return;
				}
			} else
			{
				var offsetBits = 5 - descriptor.LogBlockCount;
				fixedVector.NoRepeatMask = -1;
				for (var pulse = 0; pulse < 5; pulse++)
				{
					var sign = reader.ReadBit() != 0 ? 1.0f : -1.0f;
					var firstPosition = (int)reader.ReadBits(offsetBits);
					fixedVector.Positions[fixedVector.Count] = pulse + 5 * firstPosition;
					fixedVector.Values[fixedVector.Count++] = sign;
					if (pulse < descriptor.DoublePulses)
					{
						var secondPosition = (int)reader.ReadBits(offsetBits);
						fixedVector.Positions[fixedVector.Count] = pulse + 5 * secondPosition;
						fixedVector.Values[fixedVector.Count++] = firstPosition < secondPosition ? -sign : sign;
					}
				}
			}
			SetFixedVector(pulses, fixedVector, size);
			var gainIndex = (int)reader.ReadBits(7);
			var prediction = ScalarProduct(gainPredictionError, 0, GainPredictionCoefficients, 0, 6);
			var fixedGain = MathF.Exp((float)((double)prediction - 5.2409161640 + WmaVoiceTables.GainCodebookFcb[gainIndex]));
			var adaptiveGain = WmaVoiceTables.GainCodebookAcb[gainIndex];
			var predictionError = Math.Clamp(WmaVoiceTables.GainCodebookFcb[gainIndex], -2.9957322736f, 1.6094379124f);
			var gainWeight = 8 >> descriptor.LogBlockCount;
			Array.Copy(gainPredictionError, 0, gainPredictionError, gainWeight, 6 - gainWeight);
			for (var index = 0; index < gainWeight; index++) gainPredictionError[index] = predictionError;
			if (descriptor.AdaptiveCodebookType == AdaptiveCodebookAsymmetric)
			{
				for (var index = 0; index < size;)
				{
					var absoluteIndex = blockIndex * size + index;
					var pitchShift16 = (lastPitchValue << 16) + pitchDifferenceShift16 * absoluteIndex;
					var pitch = (pitchShift16 + 0x6fff) >> 16;
					var interpolationIndexShift16 = ((pitch << 16) - pitchShift16) * 8 + 0x58000;
					var interpolationIndex = interpolationIndexShift16 >> 16;
					int length;
					if (pitchDifferenceShift16 != 0)
					{
						var next = pitchDifferenceShift16 > 0 ? interpolationIndexShift16 & ~0xffff : interpolationIndexShift16 + 0x10000 & ~0xffff;
						length = FfmpegMath.Clip((interpolationIndexShift16 - next) / pitchDifferenceShift16 / 8, 1, size - index);
					} else length = size;
					Interpolate(excitation, excitationOffset + index, excitation, excitationOffset + index - pitch,
						WmaVoiceTables.Ipol1Coeffs, 17, interpolationIndex, 9, length);
					index += length;
				}
			} else
			{
				var blockPitch = blockPitchShift2 >> 2;
				var interpolationIndex = blockPitchShift2 & 3;
				if (interpolationIndex != 0) Interpolate(excitation, excitationOffset, excitation, excitationOffset - blockPitch,
					WmaVoiceTables.Ipol2Coeffs, 4, interpolationIndex, 8, size);
				else for (var index = 0; index < size; index++) excitation[excitationOffset + index] = excitation[excitationOffset + index - blockPitch];
			}
			for (var index = 0; index < size; index++)
				excitation[excitationOffset + index] = adaptiveGain * excitation[excitationOffset + index] + fixedGain * pulses[index];
		}

		private void ParseAwCoordinates(BitReader reader, int[] pitch)
		{
			var bits = (int)reader.ReadBits(6);
			awIndexIsExtended = false;
			if (bits >= 54)
			{
				awIndexIsExtended = true;
				bits += (bits - 54) * 3 + (int)reader.ReadBits(2);
			}
			awPulseRange = Math.Min(pitch[0], pitch[1]) > 32 ? 24 : 16;
			var offset = (int)AwStartOffsets[bits];
			while (offset < 0) offset += pitch[0];
			awPulseCounts[0] = (pitch[0] - 1 + MaximumFrameSize / 2 - offset) / pitch[0];
			awFirstPulseOffsets[0] = offset - awPulseRange / 2;
			offset += awPulseCounts[0] * pitch[0];
			awPulseCounts[1] = (pitch[1] - 1 + MaximumFrameSize - offset) / pitch[1];
			awFirstPulseOffsets[1] = offset - (MaximumFrameSize + awPulseRange) / 2;
			if (AwStartOffsets[bits] < MaximumFrameSize / 2)
			{
				while (awFirstPulseOffsets[1] - pitch[1] + awPulseRange > 0) awFirstPulseOffsets[1] -= pitch[1];
				if (AwStartOffsets[bits] < 0)
					while (awFirstPulseOffsets[0] - pitch[0] + awPulseRange > 0) awFirstPulseOffsets[0] -= pitch[0];
			}
		}

		/// <summary>Decodes the second algebraic-waveform pulse set using FFmpeg's padded 16-bit availability masks.</summary>
		private int DecodeAwPulseSetTwo(BitReader reader, int blockIndex, FixedVector vector)
		{
			var pulseOffset = awFirstPulseOffsets[blockIndex];
			if (awPulseCounts[blockIndex] > 0)
				while (pulseOffset + awPulseRange < 1) pulseOffset += vector.PitchLag;
			int range;
			if (awPulseCounts[0] > 0)
			{
				if (blockIndex == 0) range = 32;
				else
				{
					range = 8;
					if (awPulseCounts[blockIndex] > 0) pulseOffset = awNextPulseOffsetCache;
				}
			} else range = 16;
			var pulseStart = awPulseCounts[blockIndex] > 0 ? pulseOffset - range / 2 : 0;
			Array.Clear(useMask, 0, useMask.Length);
			for (var index = 0; index < 5; index++) useMask[index + 2] = ushort.MaxValue;
			if (awPulseCounts[blockIndex] > 0)
				for (var index = pulseOffset; index < MaximumFrameSize / 2; index += vector.PitchLag)
				{
					var exclusionRange = awPulseRange;
					var maskIndex = 2 + (index >> 4);
					var firstShift = 16 - (index & 15);
					useMask[maskIndex++] = (ushort)(useMask[maskIndex - 1] & 0xffffU << firstShift);
					exclusionRange -= firstShift;
					if (exclusionRange >= 16)
					{
						useMask[maskIndex++] = 0;
						useMask[maskIndex] = (ushort)(useMask[maskIndex] & 0xffff >> (exclusionRange - 16));
					} else useMask[maskIndex] = (ushort)(useMask[maskIndex] & 0xffff >> exclusionRange);
				}
			var allocationIndex = (int)reader.ReadBits(awPulseCounts[0] > 0 ? 5 - 2 * blockIndex : 4);
			var selected = 0;
			var startOffset = 0;
			while (selected <= allocationIndex)
			{
				var index = pulseStart;
				while (index < 0) index += vector.PitchLag;
				if (index >= MaximumFrameSize / 2)
				{
					if (useMask[2] != 0) index = 0x0f;
					else if (useMask[3] != 0) index = 0x1f;
					else if (useMask[4] != 0) index = 0x2f;
					else if (useMask[5] != 0) index = 0x3f;
					else if (useMask[6] != 0) index = 0x4f;
					else return -1;
					index -= FfmpegMath.Log2(useMask[2 + (index >> 4)]);
				}
				var mask = 0x8000 >> (index & 15);
				if ((useMask[2 + (index >> 4)] & mask) != 0)
				{
					useMask[2 + (index >> 4)] &= (ushort)~mask;
					selected++;
					startOffset = index;
				}
				pulseStart++;
			}
			vector.Positions[vector.Count] = startOffset;
			vector.Values[vector.Count++] = reader.ReadBit() != 0 ? -1.0f : 1.0f;
			var remainder = (MaximumFrameSize / 2 - startOffset) % vector.PitchLag;
			awNextPulseOffsetCache = remainder != 0 ? vector.PitchLag - remainder : 0;
			return 0;
		}

		private void DecodeAwPulseSetOne(BitReader reader, int blockIndex, FixedVector vector)
		{
			var value = (int)reader.ReadBits(12 - 2 * (awIndexIsExtended && blockIndex == 0 ? 1 : 0));
			if (awPulseCounts[blockIndex] > 0)
			{
				int pulseCount, valueMask, indexMask, shift;
				if (awPulseRange == 24) { pulseCount = 3; valueMask = 8; indexMask = 7; shift = 4; }
				else { pulseCount = 4; valueMask = 4; indexMask = 3; shift = 3; }
				for (var pulse = pulseCount - 1; pulse >= 0; pulse--, value >>= shift)
				{
					vector.Values[vector.Count] = (value & valueMask) != 0 ? -1.0f : 1.0f;
					vector.Positions[vector.Count] = (value & indexMask) * pulseCount + pulse + awFirstPulseOffsets[blockIndex];
					while (vector.Positions[vector.Count] < 0) vector.Positions[vector.Count] += vector.PitchLag;
					if (vector.Positions[vector.Count] < MaximumFrameSize / 2) vector.Count++;
				}
			} else
			{
				var number = (value & 0x1ff) >> 1;
				int delta, index;
				if (number < 79) { delta = 1; index = number + 1; }
				else if (number < 156) { delta = 3; index = number + 1 - 77; }
				else if (number < 231) { delta = 5; index = number + 1 - 152; }
				else { delta = 7; index = number + 1 - 225; }
				var sign = (value & 0x200) != 0 ? -1.0f : 1.0f;
				vector.NoRepeatMask |= 3 << vector.Count;
				vector.Positions[vector.Count] = index - delta;
				vector.Values[vector.Count] = sign;
				vector.Positions[vector.Count + 1] = index;
				vector.Values[vector.Count + 1] = (value & 1) != 0 ? -sign : sign;
				vector.Count += 2;
			}
		}

		/// <summary>Applies the zero/pole synthesis, pitch smoothing, Wiener filter, gain control, and optional DC filter.</summary>
		private void Postfilter(float[] source, int sourceOffset, float[] destination, int destinationOffset, int size,
			float[] coefficients, int zeroExcitationOffset, int fixedCodebookType, int pitch)
		{
			ZeroSynthesisFilter(zeroExcitationPostfilter, zeroExcitationOffset, coefficients, source, sourceOffset, size, lspCount);
			var synthesisInput = zeroExcitationPostfilter;
			var synthesisInputOffset = zeroExcitationOffset;
			if (fixedCodebookType >= FixedCodebookAwPulses && SmoothPitch(pitch, zeroExcitationPostfilter, zeroExcitationOffset, interpolationBuffer, size) == 0)
			{
				synthesisInput = interpolationBuffer;
				synthesisInputOffset = 0;
			}
			const int synthesisBase = MaximumLsps;
			SynthesisFilter(synthesisFilterOutput, synthesisBase, coefficients, synthesisInput, synthesisInputOffset, size, lspCount);
			Array.Copy(synthesisFilterOutput, synthesisBase + size - lspCount, synthesisFilterOutput, synthesisBase - lspCount, lspCount);
			WienerDenoise(fixedCodebookType, synthesisFilterOutput, synthesisBase, size, coefficients);
			AdaptiveGainControl(destination, destinationOffset, synthesisFilterOutput, synthesisBase, source, sourceOffset, size, 0.99f);
			if (dcLevel > 8) ApplySecondOrderTransfer(destination, destinationOffset, size);
		}

		private int SmoothPitch(int pitch, float[] input, int inputOffset, float[] output, int size)
		{
			var optimalGain = 0.0f;
			var bestOffset = 0;
			var pointer = inputOffset - Math.Max(minimumPitchValue, pitch - 3);
			var end = inputOffset - Math.Min(maximumPitchValue, pitch + 3);
			do
			{
				var dot = ScalarProduct(input, inputOffset, input, pointer, size);
				if (dot > optimalGain) { optimalGain = dot; bestOffset = pointer; }
				pointer--;
			} while (pointer >= end);
			if (optimalGain <= 0.0f) return -1;
			var historyPower = ScalarProduct(input, bestOffset, input, bestOffset, size);
			if (historyPower <= 0.0f) return -1;
			var gain = optimalGain <= historyPower ? (float)(historyPower / (historyPower + 0.6 * optimalGain)) : 0.625f;
			for (var index = 0; index < size; index++) output[index] = input[bestOffset + index] + gain * (input[inputOffset + index] - input[bestOffset + index]);
			return 0;
		}

		/// <summary>Builds and applies FFmpeg's frequency-domain denoising response while retaining its overlap cache.</summary>
		private void WienerDenoise(int fixedCodebookType, float[] samples, int sampleOffset, int size, float[] coefficients)
		{
			var remainder = 0;
			if (fixedCodebookType != FixedCodebookSilence)
			{
				tiltedLpcsPostfilter[0] = 1.0f;
				Array.Copy(coefficients, 0, tiltedLpcsPostfilter, 1, lspCount);
				Array.Clear(tiltedLpcsPostfilter, lspCount + 1, 128 - lspCount - 1);
				var tiltMemory = 0.0f;
				TiltCompensation(ref tiltMemory, (float)(0.7 * TiltFactor(coefficients, lspCount)), tiltedLpcsPostfilter, 0, lspCount + 2);
				remainder = Math.Min(127 - size, size - 1);
				CalculateInputResponse(tiltedLpcsPostfilter, fixedCodebookType, denoiseCoefficientsPostfilter, remainder);
				Array.Clear(samples, sampleOffset + size, 128 - size);
				realTransforms.Forward128(samples.AsSpan(sampleOffset, 128), frequencySynthesis);
				realTransforms.Forward128(denoiseCoefficientsPostfilter, frequencyCoefficients);
				frequencySynthesis[0] *= frequencyCoefficients[0];
				frequencySynthesis[1] *= frequencyCoefficients[1];
				for (var index = 1; index <= 64; index++)
				{
					var first = frequencySynthesis[index * 2];
					var second = frequencySynthesis[index * 2 + 1];
					frequencySynthesis[index * 2] = first * frequencyCoefficients[index * 2] - second * frequencyCoefficients[index * 2 + 1];
					frequencySynthesis[index * 2 + 1] = second * frequencyCoefficients[index * 2] + first * frequencyCoefficients[index * 2 + 1];
				}
				realTransforms.Inverse128(frequencySynthesis, samples.AsSpan(sampleOffset, 128));
			}
			if (denoiseFilterCacheSize != 0)
			{
				var limit = Math.Min(denoiseFilterCacheSize, size);
				for (var index = 0; index < limit; index++) samples[sampleOffset + index] += denoiseFilterCache[index];
				denoiseFilterCacheSize -= limit;
				Array.Copy(denoiseFilterCache, size, denoiseFilterCache, 0, denoiseFilterCacheSize);
			}
			if (fixedCodebookType != FixedCodebookSilence)
			{
				var limit = Math.Min(remainder, denoiseFilterCacheSize);
				for (var index = 0; index < limit; index++) denoiseFilterCache[index] += samples[sampleOffset + size + index];
				if (limit < remainder)
				{
					Array.Copy(samples, sampleOffset + size + limit, denoiseFilterCache, limit, remainder - limit);
					denoiseFilterCacheSize = remainder;
				}
			}
		}

		/// <summary>Derives the normalized impulse response using FFmpeg's RDFT, DCT-I, and DST-I quantization path.</summary>
		private void CalculateInputResponse(float[] sourceLpcs, int fixedCodebookType, float[] destination, int remainder)
		{
			var minimum = 15.0f;
			var maximum = -15.0f;
			Array.Copy(destination, responseCoefficients, 130);
			realTransforms.Forward128(sourceLpcs, responseLpcs);
			var lastCoefficient = MathF.Log10(responseLpcs[64] * responseLpcs[64]);
			maximum = Math.Max(maximum, lastCoefficient); minimum = Math.Min(minimum, lastCoefficient);
			for (var index = 1; index < 64; index++)
			{
				var value = MathF.Log10(responseLpcs[index * 2] * responseLpcs[index * 2] + responseLpcs[index * 2 + 1] * responseLpcs[index * 2 + 1]);
				responseLpcs[index] = value; maximum = Math.Max(maximum, value); minimum = Math.Min(minimum, value);
			}
			var firstValue = MathF.Log10(responseLpcs[0] * responseLpcs[0]);
			responseLpcs[0] = firstValue; maximum = Math.Max(maximum, firstValue); minimum = Math.Min(minimum, firstValue);
			var range = maximum - minimum;
			responseLpcs[64] = lastCoefficient;
			var inverseRange = (float)(64.0 / range);
			var gainMultiplier = (float)(range * (fixedCodebookType == FixedCodebookHardcoded ? (5.0 / 13.0) : (5.0 / 14.7)));
			var angleMultiplier = (float)(gainMultiplier * (8.0 * Math.Log(10.0) / Math.PI));
			for (var index = 0; index <= 64; index++)
			{
				var tableIndex = (int)Math.Round((maximum - responseLpcs[index]) * inverseRange - 1.0f, MidpointRounding.ToEven);
				tableIndex = Math.Max(0, tableIndex);
				var power = WmaVoiceTables.DenoisePowerTable[denoiseStrength * 64 + tableIndex];
				responseLpcs[index] = angleMultiplier * power;
				tableIndex = (int)Math.Clamp((power * gainMultiplier - 0.0295) * 70.570526123, 0.0, int.MaxValue / 2.0);
				responseCoefficients[index] = tableIndex > 127 ? WmaVoiceTables.EnergyTable[127] * MathF.Pow(1.0331663f, tableIndex - 127) :
					WmaVoiceTables.EnergyTable[Math.Max(0, tableIndex)];
			}
			realTransforms.DctI64(responseLpcs, responseLpcsDct);
			realTransforms.DstI64(responseLpcsDct, responseLpcs);
			var coefficientIndex = 255 + FfmpegMath.Clip((int)responseLpcs[64], -255, 255);
			responseCoefficients[0] *= cosine[coefficientIndex];
			coefficientIndex = 255 + FfmpegMath.Clip((int)(responseLpcs[64] - 2 * responseLpcs[63]), -255, 255);
			lastCoefficient = responseCoefficients[64] * cosine[coefficientIndex];
			for (var index = 63; ; index--)
			{
				coefficientIndex = 255 + FfmpegMath.Clip((int)(-responseLpcs[64] - 2 * responseLpcs[index - 1]), -255, 255);
				responseCoefficients[index * 2 + 1] = responseCoefficients[index] * sine[coefficientIndex];
				responseCoefficients[index * 2] = responseCoefficients[index] * cosine[coefficientIndex];
				index--;
				if (index == 0) break;
				coefficientIndex = 255 + FfmpegMath.Clip((int)(responseLpcs[64] - 2 * responseLpcs[index - 1]), -255, 255);
				responseCoefficients[index * 2 + 1] = responseCoefficients[index] * sine[coefficientIndex];
				responseCoefficients[index * 2] = responseCoefficients[index] * cosine[coefficientIndex];
			}
			responseCoefficients[64] = lastCoefficient;
			realTransforms.Inverse128(responseCoefficients, destination);
			Array.Clear(destination, remainder, 128 - remainder);
			if (denoiseTiltCorrection)
			{
				var tiltMemory = 0.0f;
				destination[remainder - 1] = 0.0f;
				TiltCompensation(ref tiltMemory, (float)(-1.8 * TiltFactor(destination, remainder - 1)), destination, 0, remainder);
			}
			var scale = (float)((1.0 / 64.0) * MathF.Sqrt(1.0f / ScalarProduct(destination, 0, destination, 0, remainder)));
			for (var index = 0; index < remainder; index++) destination[index] *= scale;
		}

		private void AdaptiveGainControl(float[] output, int outputOffset, float[] input, int inputOffset,
			float[] speech, int speechOffset, int size, float alpha)
		{
			var speechEnergy = 0.0f;
			var filterEnergy = 0.0f;
			for (var index = 0; index < size; index++)
			{
				speechEnergy += MathF.Abs(speech[speechOffset + index]);
				filterEnergy += MathF.Abs(input[inputOffset + index]);
			}
			var gainScale = filterEnergy == 0.0f ? 0.0f : (float)((1.0 - alpha) * speechEnergy / filterEnergy);
			var memory = postfilterGain;
			for (var index = 0; index < size; index++)
			{
				memory = alpha * memory + gainScale;
				output[outputOffset + index] = input[inputOffset + index] * memory;
			}
			postfilterGain = memory;
		}

		private void ApplySecondOrderTransfer(float[] samples, int offset, int count)
		{
			for (var index = 0; index < count; index++)
			{
				var temporary = 0.93980580475f * samples[offset + index] - -1.9330735188f * dcfMemory[0] - 0.93589198496f * dcfMemory[1];
				samples[offset + index] = temporary + -1.99997f * dcfMemory[0] + dcfMemory[1];
				dcfMemory[1] = dcfMemory[0];
				dcfMemory[0] = temporary;
			}
		}

		private static float TiltFactor(float[] coefficients, int count)
		{
			var first = 1.0f + ScalarProduct(coefficients, 0, coefficients, 0, count);
			var second = coefficients[0] + ScalarProduct(coefficients, 0, coefficients, 1, count - 1);
			return second / first;
		}

		private static void TiltCompensation(ref float memory, float tilt, float[] samples, int offset, int size)
		{
			var newMemory = samples[offset + size - 1];
			for (var index = size - 1; index > 0; index--) samples[offset + index] -= tilt * samples[offset + index - 1];
			samples[offset] -= tilt * memory;
			memory = newMemory;
		}

		/// <summary>Executes FFmpeg's four-sample scalar CELP synthesis kernel with identical dependency order.</summary>
		private static void SynthesisFilter(float[] output, int outputOffset, float[] coefficients, float[] input,
			int inputOffset, int bufferLength, int filterLength)
		{
			var a = coefficients[0];
			var b = coefficients[1];
			var c = coefficients[2];
			b -= coefficients[0] * coefficients[0];
			c -= coefficients[1] * coefficients[0];
			c -= coefficients[0] * b;
			var old0 = output[outputOffset - 4];
			var old1 = output[outputOffset - 3];
			var old2 = output[outputOffset - 2];
			var old3 = output[outputOffset - 1];
			var sample = 0;
			for (; sample <= bufferLength - 4; sample += 4)
			{
				var current = outputOffset + sample;
				var inputCurrent = inputOffset + sample;
				var out0 = input[inputCurrent];
				var out1 = input[inputCurrent + 1];
				var out2 = input[inputCurrent + 2];
				var out3 = input[inputCurrent + 3];
				out0 -= coefficients[2] * old1;
				out1 -= coefficients[2] * old2;
				out2 -= coefficients[2] * old3;
				out0 -= coefficients[1] * old2;
				out1 -= coefficients[1] * old3;
				out0 -= coefficients[0] * old3;
				var value = coefficients[3];
				out0 -= value * old0; out1 -= value * old1; out2 -= value * old2; out3 -= value * old3;
				for (var index = 5; index < filterLength; index += 2)
				{
					old3 = output[current - index]; value = coefficients[index - 1];
					out0 -= value * old3; out1 -= value * old0; out2 -= value * old1; out3 -= value * old2;
					old2 = output[current - index - 1]; value = coefficients[index];
					out0 -= value * old2; out1 -= value * old3; out2 -= value * old0; out3 -= value * old1;
					(old0, old2) = (old2, old0); old1 = old3;
				}
				var temporary0 = out0; var temporary1 = out1; var temporary2 = out2;
				out3 -= a * temporary2; out2 -= a * temporary1; out1 -= a * temporary0;
				out3 -= b * temporary1; out2 -= b * temporary0; out3 -= c * temporary0;
				output[current] = out0; output[current + 1] = out1; output[current + 2] = out2; output[current + 3] = out3;
				old0 = out0; old1 = out1; old2 = out2; old3 = out3;
			}
			for (; sample < bufferLength; sample++)
			{
				output[outputOffset + sample] = input[inputOffset + sample];
				for (var index = 1; index <= filterLength; index++) output[outputOffset + sample] -= coefficients[index - 1] * output[outputOffset + sample - index];
			}
		}

		private static void ZeroSynthesisFilter(float[] output, int outputOffset, float[] coefficients, float[] input,
			int inputOffset, int bufferLength, int filterLength)
		{
			for (var sample = 0; sample < bufferLength; sample++)
			{
				output[outputOffset + sample] = input[inputOffset + sample];
				for (var index = 1; index <= filterLength; index++) output[outputOffset + sample] += coefficients[index - 1] * input[inputOffset + sample - index];
			}
		}

		private void LspToLpc(double[] lsp, float[] output, int halfOrder)
		{
			LspToPolynomial(lsp, 0, polynomialP, halfOrder);
			LspToPolynomial(lsp, 1, polynomialQ, halfOrder);
			var outputEnd = (halfOrder << 1) - 1;
			while (halfOrder-- != 0)
			{
				var first = polynomialP[halfOrder + 1] + polynomialP[halfOrder];
				var second = polynomialQ[halfOrder + 1] - polynomialQ[halfOrder];
				output[halfOrder] = (float)(0.5 * (first + second));
				output[outputEnd - halfOrder] = (float)(0.5 * (first - second));
			}
		}

		private static void LspToPolynomial(double[] lsp, int offset, double[] polynomial, int halfOrder)
		{
			polynomial[0] = 1.0;
			polynomial[1] = -2 * lsp[offset];
			for (var index = 2; index <= halfOrder; index++)
			{
				var value = -2 * lsp[offset + 2 * (index - 1)];
				polynomial[index] = value * polynomial[index - 1] + 2 * polynomial[index - 2];
				for (var inner = index - 1; inner > 1; inner--) polynomial[inner] += polynomial[inner - 1] * value + polynomial[inner - 2];
				polynomial[1] += value;
			}
		}

		private static void Interpolate(float[] output, int outputOffset, float[] input, int inputOffset,
			float[] coefficients, int precision, int fractionalPosition, int filterLength, int length)
		{
			for (var sample = 0; sample < length; sample++)
			{
				var coefficientIndex = 0;
				var value = 0.0f;
				for (var index = 0; index < filterLength;)
				{
					value += input[inputOffset + sample + index] * coefficients[coefficientIndex + fractionalPosition];
					coefficientIndex += precision; index++;
					value += input[inputOffset + sample - index] * coefficients[coefficientIndex - fractionalPosition];
				}
				output[outputOffset + sample] = value;
			}
		}

		private static void SetFixedVector(float[] output, FixedVector vector, int size)
		{
			for (var index = 0; index < vector.Count; index++)
			{
				var position = vector.Positions[index];
				var repeats = (vector.NoRepeatMask >> index & 1) == 0;
				var value = vector.Values[index];
				if (vector.PitchLag > 0)
					do
					{
						output[position] += value;
						value *= vector.PitchFactor;
						position += vector.PitchLag;
					} while (position < size && repeats);
			}
		}

		private static float ScalarProduct(float[] first, int firstOffset, float[] second, int secondOffset, int count)
		{
			var product = 0.0f;
			for (var index = 0; index < count; index++) product += first[firstOffset + index] * second[secondOffset + index];
			return product;
		}

		private static int PseudoRandom(int frameCounter, int blockNumber, int blockSize)
		{
			var value = (uint)(blockNumber * 1877 + frameCounter);
			if (value >= 0xffff) value -= 0xffff;
			var divisorIndex = value - 9U * (uint)(((long)477218589 * value) >> 32);
			var result = unchecked(value * RandomDivisors[divisorIndex, 0] + (uint)(((ulong)value * RandomDivisors[divisorIndex, 1]) >> 32));
			return (ushort)result % (1000 - blockSize);
		}

		private static void DequantizeLsps(double[] output, int outputOffset, int count, ushort[] values, int valuesOffset,
			ushort[] sizes, int sizesOffset, int stages, byte[] table, double[] multipliers, int multiplierOffset,
			double[] bases, int baseOffset)
		{
			Array.Clear(output, outputOffset, count);
			var tableOffset = 0;
			for (var stage = 0; stage < stages; stage++)
			{
				var vectorOffset = tableOffset + values[valuesOffset + stage] * count;
				var baseValue = bases[baseOffset + stage];
				var multiplier = multipliers[multiplierOffset + stage];
				for (var index = 0; index < count; index++) output[outputOffset + index] += baseValue + multiplier * table[vectorOffset + index];
				tableOffset += sizes[sizesOffset + stage] * count;
			}
		}

		private static readonly ushort[] Lsp10IndependentSizes = { 256, 64, 32, 32 };
		private static readonly double[] Lsp10IndependentMultipliers = { 5.2187144800e-3, 1.4626986422e-3, 9.6179549166e-4, 1.1325736225e-3 };
		private static readonly double[] Lsp10IndependentBases = { Math.PI * -2.15522e-1, Math.PI * -6.1646e-2, Math.PI * -3.3486e-2, Math.PI * -5.7408e-2 };
		private static readonly ushort[] Lsp10ResidualSizes = { 128, 64, 64 };
		private static readonly double[] Lsp10ResidualMultipliers = { 2.5807601174e-3, 1.2354460219e-3, 1.1763821673e-3 };
		private static readonly double[] Lsp10ResidualBases = { Math.PI * -1.07448e-1, Math.PI * -5.2706e-2, Math.PI * -5.1634e-2 };
		private static readonly ushort[] Lsp16IndependentSizes = { 256, 64, 128, 64, 128 };
		private static readonly double[] Lsp16IndependentMultipliers = { 3.3439586280e-3, 6.9908173703e-4, 3.3216608306e-3, 1.0334960326e-3, 3.1899104283e-3 };
		private static readonly double[] Lsp16IndependentBases = { Math.PI * -1.27576e-1, Math.PI * -2.4292e-2, Math.PI * -1.28094e-1, Math.PI * -3.2128e-2, Math.PI * -1.29816e-1 };
		private static readonly ushort[] Lsp16ResidualSizes = { 128, 128, 128 };
		private static readonly double[] Lsp16ResidualMultipliers = { 1.2232979501e-3, 1.4062241527e-3, 1.6114744851e-3 };
		private static readonly double[] Lsp16ResidualBases = { Math.PI * -5.5830e-2, Math.PI * -5.2908e-2, Math.PI * -5.4776e-2 };
		private readonly ushort[] dequantizationValues = new ushort[5];

		private void DequantizeLsp10Independent(BitReader reader, double[] output, int offset)
		{
			dequantizationValues[0] = (ushort)reader.ReadBits(8); dequantizationValues[1] = (ushort)reader.ReadBits(6);
			dequantizationValues[2] = (ushort)reader.ReadBits(5); dequantizationValues[3] = (ushort)reader.ReadBits(5);
			DequantizeLsps(output, offset, 10, dequantizationValues, 0, Lsp10IndependentSizes, 0, 4,
				WmaVoiceTables.DqLsp10i, Lsp10IndependentMultipliers, 0, Lsp10IndependentBases, 0);
		}

		private void DequantizeLsp10Residual(BitReader reader, int independentOffset, double[] old, double[] first, double[] second)
		{
			DequantizeLsp10Independent(reader, frameLsps, independentOffset);
			var interpolation = (int)reader.ReadBits(5);
			dequantizationValues[0] = (ushort)reader.ReadBits(7); dequantizationValues[1] = (ushort)reader.ReadBits(6); dequantizationValues[2] = (ushort)reader.ReadBits(6);
			var interpolationTable = lspQuantizationMode ? WmaVoiceTables.Lsp10IntercoeffB : WmaVoiceTables.Lsp10IntercoeffA;
			for (var index = 0; index < 10; index++)
			{
				var delta = old[index] - frameLsps[independentOffset + index];
				first[index] = interpolationTable[(interpolation * 2) * 10 + index] * delta + frameLsps[independentOffset + index];
				first[10 + index] = interpolationTable[(interpolation * 2 + 1) * 10 + index] * delta + frameLsps[independentOffset + index];
			}
			DequantizeLsps(second, 0, 20, dequantizationValues, 0, Lsp10ResidualSizes, 0, 3,
				WmaVoiceTables.DqLsp10r, Lsp10ResidualMultipliers, 0, Lsp10ResidualBases, 0);
		}

		private void DequantizeLsp16Independent(BitReader reader, double[] output, int offset)
		{
			dequantizationValues[0] = (ushort)reader.ReadBits(8); dequantizationValues[1] = (ushort)reader.ReadBits(6);
			dequantizationValues[2] = (ushort)reader.ReadBits(7); dequantizationValues[3] = (ushort)reader.ReadBits(6);
			dequantizationValues[4] = (ushort)reader.ReadBits(7);
			DequantizeLsps(output, offset, 5, dequantizationValues, 0, Lsp16IndependentSizes, 0, 2,
				WmaVoiceTables.DqLsp16i1, Lsp16IndependentMultipliers, 0, Lsp16IndependentBases, 0);
			DequantizeLsps(output, offset + 5, 5, dequantizationValues, 2, Lsp16IndependentSizes, 2, 2,
				WmaVoiceTables.DqLsp16i2, Lsp16IndependentMultipliers, 2, Lsp16IndependentBases, 2);
			DequantizeLsps(output, offset + 10, 6, dequantizationValues, 4, Lsp16IndependentSizes, 4, 1,
				WmaVoiceTables.DqLsp16i3, Lsp16IndependentMultipliers, 4, Lsp16IndependentBases, 4);
		}

		private void DequantizeLsp16Residual(BitReader reader, int independentOffset, double[] old, double[] first, double[] second)
		{
			DequantizeLsp16Independent(reader, frameLsps, independentOffset);
			var interpolation = (int)reader.ReadBits(5);
			dequantizationValues[0] = (ushort)reader.ReadBits(7); dequantizationValues[1] = (ushort)reader.ReadBits(7); dequantizationValues[2] = (ushort)reader.ReadBits(7);
			var interpolationTable = lspQuantizationMode ? WmaVoiceTables.Lsp16IntercoeffB : WmaVoiceTables.Lsp16IntercoeffA;
			for (var index = 0; index < 16; index++)
			{
				var delta = old[index] - frameLsps[independentOffset + index];
				first[index] = interpolationTable[(interpolation * 2) * 16 + index] * delta + frameLsps[independentOffset + index];
				first[16 + index] = interpolationTable[(interpolation * 2 + 1) * 16 + index] * delta + frameLsps[independentOffset + index];
			}
			DequantizeLsps(second, 0, 10, dequantizationValues, 0, Lsp16ResidualSizes, 0, 1,
				WmaVoiceTables.DqLsp16r1, Lsp16ResidualMultipliers, 0, Lsp16ResidualBases, 0);
			DequantizeLsps(second, 10, 10, dequantizationValues, 1, Lsp16ResidualSizes, 1, 1,
				WmaVoiceTables.DqLsp16r2, Lsp16ResidualMultipliers, 1, Lsp16ResidualBases, 1);
			DequantizeLsps(second, 20, 12, dequantizationValues, 2, Lsp16ResidualSizes, 2, 1,
				WmaVoiceTables.DqLsp16r3, Lsp16ResidualMultipliers, 2, Lsp16ResidualBases, 2);
		}

		private static void StabilizeLsps(double[] values, int offset, int count)
		{
			values[offset] = Math.Max(values[offset], 0.0015 * Math.PI);
			for (var index = 1; index < count; index++) values[offset + index] = Math.Max(values[offset + index], values[offset + index - 1] + 0.0125 * Math.PI);
			values[offset + count - 1] = Math.Min(values[offset + count - 1], 0.9985 * Math.PI);
			for (var index = 1; index < count; index++)
				if (values[offset + index] < values[offset + index - 1])
				{
					for (var current = 1; current < count; current++)
					{
						var value = values[offset + current];
						var insertion = current - 1;
						for (; insertion >= 0; insertion--)
						{
							if (values[offset + insertion] <= value) break;
							values[offset + insertion + 1] = values[offset + insertion];
						}
						values[offset + insertion + 1] = value;
					}
					break;
				}
		}

		private int WriteOutput(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			var size = decodedOutputSamples * sizeof(float);
			if (output.Length < size) return FfmpegError.InvalidArgument;
			for (var index = 0; index < decodedOutputSamples; index++) BinaryPrimitives.WriteInt32LittleEndian(output.Slice(index * 4, 4), BitConverter.SingleToInt32Bits(outputSamples[index]));
			frame = new AudioFrameInfo(decodedOutputSamples, 1, AudioSampleFormat.Float, 1, size, size);
			return 0;
		}

		private void InitializeSineWindows()
		{
			for (var index = 0; index < 256; index++) cosine[index] = (float)Math.Sin((index + 0.5) * (Math.PI / (2.0 * 256.0)));
			Array.Copy(cosine, 0, sine, 255, 256);
			for (var index = 0; index < 255; index++)
			{
				sine[index] = -sine[510 - index];
				cosine[510 - index] = cosine[index];
			}
		}

		private static int DecodeVbmTree(BitReader reader, sbyte[] tree)
		{
			var counts = new int[8];
			for (var index = 0; index < tree.Length; index++) tree[index] = -1;
			for (var index = 0; index < 17; index++)
			{
				var value = (int)reader.ReadBits(3);
				if (counts[value] > 3) return -1;
				tree[value * 3 + counts[value]++] = (sbyte)index;
			}
			return 0;
		}

		private static Vlc CreateFrameTypeVlc()
		{
			var lengths = new sbyte[] { 2, 2, 2, 4, 4, 4, 6, 6, 6, 8, 8, 8, 10, 10, 10, 12, 12, 12, 14, 14, 14, 14 };
			var result = new Vlc();
			if (result.InitializeFromLengths(6, lengths) < 0) throw new InvalidOperationException("Invalid WMA Voice frame VLC table.");
			return result;
		}

		private readonly struct FrameDescriptor
		{
			public readonly int BlockCount;
			public readonly int LogBlockCount;
			public readonly int AdaptiveCodebookType;
			public readonly int FixedCodebookType;
			public readonly int DoublePulses;
			public FrameDescriptor(int blockCount, int logBlockCount, int adaptiveCodebookType, int fixedCodebookType, int doublePulses)
			{
				BlockCount = blockCount; LogBlockCount = logBlockCount; AdaptiveCodebookType = adaptiveCodebookType;
				FixedCodebookType = fixedCodebookType; DoublePulses = doublePulses;
			}
		}

		/// <summary>
		/// Stores one sparse WMA Voice fixed-codebook excitation vector without frame-time allocation.
		/// </summary>
		private sealed class FixedVector
		{
			public readonly int[] Positions = new int[16];
			public readonly float[] Values = new float[16];
			public int PitchLag;
			public float PitchFactor;
			public int NoRepeatMask;
			public int Count;
		}
	}
}
