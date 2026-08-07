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
using System.Runtime.InteropServices;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Transforms;

namespace Ffmpeg.CsPort.Decoder.Codecs.Dca
{
	/// <summary>
	/// Ports FFmpeg's DTS Express/LBR chunk parser, residual reconstruction, tonal synthesis, and filter bank.
	/// </summary>
	internal sealed class DcaLbrDecoder
	{
		private const int Channels = 6;
		private const int ChannelsTotal = 32;
		private const int Subbands = 32;
		private const int Tones = 512;
		private const int TimeSamples = 128;
		private const int TimeHistory = 8;
		private const int TimeBufferSamples = TimeSamples + TimeHistory * 2;
		private const int AmplitudeMaximum = 56;
		private const int Flag24Bit = 0x01;
		private const int FlagLfePresent = 0x02;
		private const int FlagBandLimitHalf = 0x08;
		private const int FlagBandLimitQuarter = 0x10;
		private const int FlagBandLimitNone = 0x14;
		private const int FlagBandLimitMask = 0x1c;
		private const int FlagDownmixStereo = 0x20;
		private const int FlagDownmixMultichannel = 0x40;
		private const int ChunkFrame = 0x04;
		private const int ChunkFrameNoChecksum = 0x06;
		private const int ChunkLfe = 0x0a;
		private const int ChunkScaleFactors = 0x0e;
		private const int ChunkTonal = 0x10;
		private const int ChunkTonalGroup1 = 0x11;
		private const int ChunkTonalGroup5 = 0x15;
		private const int ChunkTonalScaleFactors = 0x16;
		private const int ChunkTonalScaleFactorGroup1 = 0x17;
		private const int ChunkTonalScaleFactorGroup5 = 0x1b;
		private const int ChunkResidualGridLowResolution = 0x30;
		private const int ChunkResidualGridHighResolution = 0x40;
		private const int ChunkResidualTimeSamples1 = 0x50;
		private const int ChunkResidualTimeSamples2 = 0x60;
		private const uint SyncWord = 0x0a801921;
		private static readonly sbyte[] ChannelReorderWithoutLfe =
		{
			0, -1, -1, -1, -1, 0, 1, -1, -1, -1, 0, 1, 2, -1, -1, 0, 1, -1, -1, -1,
			1, 2, 0, -1, -1, 0, 1, 2, 3, -1, 0, 1, 3, 4, 2
		};
		private static readonly sbyte[] ChannelReorderWithLfe =
		{
			0, -1, -1, -1, -1, 0, 1, -1, -1, -1, 0, 1, 2, -1, -1, 1, 2, -1, -1, -1,
			2, 3, 0, -1, -1, 0, 1, 3, 4, -1, 0, 1, 4, 5, 2
		};
		private static readonly byte[] LfeIndex = { 1, 2, 3, 0, 1, 2, 3 };
		private static readonly byte[] LayoutChannels = { 1, 2, 3, 2, 3, 4, 5 };
		private static readonly float[] Cosine = CreateCosineTable();
		private static readonly float[] LpcTable =
		{
			-0.995734176295034521871191178905f, -0.961825643172819070408796290732f,
			-0.895163291355062322067016499754f, -0.798017227280239503332805112796f,
			-0.673695643646557211712691912426f, -0.526432162877355800244607799141f,
			-0.361241666187152948744714596184f, -0.183749517816570331574408839621f,
			0.0f, 0.207911690817759337101742284405f, 0.406736643075800207753985990341f,
			0.587785252292473129168705954639f, 0.743144825477394235014697048974f,
			0.866025403784438646763723170753f, 0.951056516295153572116439333379f,
			0.994521895368273336922691944981f
		};

		private readonly BitReader _Bits = new BitReader();
		private readonly byte[,] _QuantLevels = new byte[Channels / 2, Subbands];
		private readonly byte[] _SubbandIndices = new byte[Subbands];
		private readonly byte[,] _SecondaryChannelSumDifference = new byte[Channels / 2, Subbands];
		private readonly byte[,] _SecondaryChannelLeftRight = new byte[Channels / 2, Subbands];
		private readonly uint[] _ChannelPresence = new uint[Channels];
		private readonly byte[,,] _Grid1ScaleFactors = new byte[Channels, 12, 8];
		private readonly byte[,,] _Grid2ScaleFactors = new byte[Channels, 3, 64];
		private readonly sbyte[,] _Grid3Average = new sbyte[Channels, Subbands - 4];
		private readonly sbyte[,,] _Grid3ScaleFactors = new sbyte[Channels, Subbands - 4, 8];
		private readonly uint[] _Grid3Presence = new uint[Channels];
		private readonly byte[,,] _HighResolutionScaleFactors = new byte[Channels, Subbands, 8];
		private readonly byte[,,] _PartialStereo = new byte[Channels, Subbands / 4, 5];
		private readonly float[,,,,] _LpcCoefficients = new float[2, Channels, 3, 2, 8];
		private readonly float[] _SubbandScaleFactors = new float[Subbands];
		private readonly float[][][] _TimeSampleBuffers = CreateTimeSampleBuffers();
		private readonly float[,] _History = new float[Channels, Subbands * 4];
		private readonly float[] _Window = new float[Subbands * 4];
		private readonly float[] _LfeData = new float[64];
		private readonly float[,] _LfeHistory = new float[5, 2];
		private readonly byte[] _TonalScaleFactors = new byte[6];
		private readonly ushort[,,] _TonalBounds = new ushort[5, 32, 2];
		private readonly DcaLbrTone[] _Tones = CreateTones();
		private readonly uint[] _TonalAmplitudes = new uint[ChannelsTotal];
		private readonly uint[] _TonalPhases = new uint[ChannelsTotal];
		private readonly int[] _LpcCodes = new int[16];
		private readonly int[] _QuantLevelScratch = new int[Subbands];
		private readonly float[] _TransformValues = new float[Subbands * 4];
		private readonly float[] _TransformResult = new float[Subbands * 8];
		private readonly float[] _RandomAccumulation = new float[8];
		private readonly DcaLbrChunk[] _TonalGroupChunks = new DcaLbrChunk[5];
		private readonly DcaLbrChunk[] _Grid1Chunks = new DcaLbrChunk[Channels / 2];
		private readonly DcaLbrChunk[] _HighResolutionGridChunks = new DcaLbrChunk[Channels / 2];
		private readonly DcaLbrChunk[] _TimeSample1Chunks = new DcaLbrChunk[Channels / 2];
		private readonly DcaLbrChunk[] _TimeSample2Chunks = new DcaLbrChunk[Channels / 2];
		private DcaLbrChunk _LfeChunk;
		private DcaLbrChunk _TonalChunk;
		private byte[] _Data;
		private int _BytePosition;
		private int _ByteEnd;
		private int _SampleRate;
		private int _ChannelMask;
		private int _Flags;
		private int _OriginalBitRate;
		private int _ScaledBitRate;
		private int _NumberOfChannels;
		private int _TotalChannels;
		private int _FrequencyRange;
		private int _BandLimit;
		private int _LimitedRate;
		private int _LimitedRange;
		private int _ResidualProfile;
		private int _NumberOfSubbands;
		private int _Grid3AverageOnlyStartSubband;
		private int _MinimumMonoSubband;
		private int _MaximumMonoSubband;
		private int _FrameNumber;
		private int _LbrRandom = 1;
		private int _PartialStereoPresence;
		private int _NumberOfTones;
		private float _LfeScale;
		private FfmpegFloatMdct _Imdct;

		public int SampleRate => _SampleRate;
		public int NumberOfSamples => 1024 << _FrequencyRange;
		public int NumberOfOutputChannels => _SampleRate == 0 ? 0 : LayoutChannels[(_ChannelMask & 7) - 1] + ((_Flags & FlagLfePresent) != 0 ? 1 : 0);

		/// <summary>
		/// Parses one LBR component and retains all persistent predictor, tone, and overlap state required by following frames.
		/// </summary>
		public int Parse(byte[] data, int extensionOffset, DcaExssAsset asset)
		{
			if (data == null || asset == null || asset.LbrOffset < 0 || asset.LbrSize < 5 ||
				extensionOffset < 0 || extensionOffset > data.Length - asset.LbrOffset ||
				extensionOffset + asset.LbrOffset > data.Length - asset.LbrSize)
				return FfmpegError.InvalidData;
			InitializeByteReader(data, extensionOffset + asset.LbrOffset, asset.LbrSize);
			if (ReadBigEndian32() != SyncWord) return FfmpegError.InvalidData;
			var headerType = ReadByte();
			if (headerType == 1)
			{
				if (_SampleRate == 0) return FfmpegError.InvalidData;
			} else if (headerType == 2)
			{
				var result = ParseDecoderInitialization();
				if (result < 0)
				{
					_SampleRate = 0;
					return result;
				}
			} else return FfmpegError.InvalidData;

			var chunkIdentifier = ReadByte();
			var chunkLength = (chunkIdentifier & 0x80) != 0 ? ReadBigEndian16() : ReadByte();
			if (chunkLength > BytesLeft) chunkLength = BytesLeft;
			var frameStart = _BytePosition;
			var frameEnd = frameStart + chunkLength;
			if ((chunkIdentifier & 0x7f) == ChunkFrame)
			{
				SkipBytes(2);
			} else if ((chunkIdentifier & 0x7f) != ChunkFrameNoChecksum) return FfmpegError.InvalidData;
			_ByteEnd = frameEnd;
			ResetFrameState();
			CollectChunks();

			var parseResult = ParseLfeChunk(_LfeChunk);
			parseResult |= ParseTonalChunk(_TonalChunk);
			for (var group = 0; group < 5; group++) parseResult |= ParseTonalGroup(_TonalGroupChunks[group]);
			for (var pair = 0; pair < (_NumberOfChannels + 1) / 2; pair++)
			{
				var firstChannel = pair * 2;
				var secondChannel = Math.Min(firstChannel + 1, _NumberOfChannels - 1);
				if (ParseGrid1Chunk(_Grid1Chunks[pair], firstChannel, secondChannel) < 0 ||
					ParseHighResolutionGrid(_HighResolutionGridChunks[pair], firstChannel, secondChannel) < 0)
				{
					parseResult = -1;
					continue;
				}
				if (_Grid1Chunks[pair].Length == 0 || _HighResolutionGridChunks[pair].Length == 0 || _TimeSample1Chunks[pair].Length == 0) continue;
				if (ParseTimeSample1Chunk(_TimeSample1Chunks[pair], firstChannel, secondChannel) < 0 ||
					ParseTimeSample2Chunk(_TimeSample2Chunks[pair], firstChannel, secondChannel) < 0) parseResult = -1;
			}
			_ = parseResult;
			return 0;
		}

		/// <summary>
		/// Reconstructs and writes one FFmpeg-compatible planar-float DTS Express frame without allocating in the filter loop.
		/// </summary>
		public int Filter(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (_SampleRate == 0 || _Imdct == null) return FfmpegError.InvalidData;
			var channelConfiguration = (_ChannelMask & 7) - 1;
			if ((uint)channelConfiguration >= LayoutChannels.Length) return FfmpegError.InvalidData;
			var baseChannels = LayoutChannels[channelConfiguration];
			var hasLfe = (_Flags & FlagLfePresent) != 0;
			var outputChannels = baseChannels + (hasLfe ? 1 : 0);
			var sampleCount = NumberOfSamples;
			var planeSize = sampleCount * sizeof(float);
			var requiredBytes = planeSize * outputChannels;
			if (output.Length < requiredBytes) return FfmpegError.InvalidArgument;
			var samples = MemoryMarshal.Cast<byte, float>(output.Slice(0, requiredBytes));
			var reorder = hasLfe ? ChannelReorderWithLfe : ChannelReorderWithoutLfe;

			for (var pair = 0; pair < (_NumberOfChannels + 1) / 2; pair++)
			{
				var firstChannel = pair * 2;
				var secondChannel = Math.Min(firstChannel + 1, _NumberOfChannels - 1);
				DecodeGrid(firstChannel, secondChannel);
				GenerateRandomTimeSamples(firstChannel, secondChannel);
				FilterTimeSamples(firstChannel, secondChannel);
				if (firstChannel != secondChannel && (_PartialStereoPresence & (1 << firstChannel)) != 0)
					DecodePartialStereo(firstChannel, secondChannel);
				if (firstChannel < baseChannels)
					TransformChannel(firstChannel, samples.Slice(reorder[channelConfiguration * 5 + firstChannel] * sampleCount, sampleCount));
				if (firstChannel != secondChannel && secondChannel < baseChannels)
					TransformChannel(secondChannel, samples.Slice(reorder[channelConfiguration * 5 + secondChannel] * sampleCount, sampleCount));
			}
			if (hasLfe) FilterLowFrequencyEffects(samples.Slice(LfeIndex[channelConfiguration] * sampleCount, sampleCount));
			frame = new AudioFrameInfo(sampleCount, outputChannels, AudioSampleFormat.FloatPlanar, outputChannels, planeSize, requiredBytes);
			return 0;
		}

		/// <summary>
		/// Parses the LBR decoder-initialization header and derives rates, channel layout, bands, and synthesis state.
		/// </summary>
		private int ParseDecoderInitialization()
		{
			var oldSampleRate = _SampleRate;
			var oldBandLimit = _BandLimit;
			var oldChannels = _NumberOfChannels;
			var sampleRateCode = ReadByte();
			if ((uint)sampleRateCode >= DcaTables.SamplingFrequencies.Length) return FfmpegError.InvalidData;
			_SampleRate = DcaTables.SamplingFrequencies[sampleRateCode];
			if (_SampleRate > 48000) return FfmpegError.PatchWelcome;
			_ChannelMask = ReadLittleEndian16();
			if ((_ChannelMask & 7) == 0) return FfmpegError.PatchWelcome;
			var version = ReadLittleEndian16();
			if ((version & 0xff00) != 0x0800) return FfmpegError.PatchWelcome;
			_Flags = ReadByte();
			if ((_Flags & FlagDownmixMultichannel) != 0) return FfmpegError.PatchWelcome;
			if ((_Flags & FlagLfePresent) != 0 && _SampleRate != 48000) _Flags &= ~FlagLfePresent;
			var highBitRate = ReadByte();
			_OriginalBitRate = ReadLittleEndian16() | (highBitRate & 0x0f) << 16;
			_ScaledBitRate = ReadLittleEndian16() | (highBitRate & 0xf0) << 12;
			_TotalChannels = BitOperations.PopCount((uint)((_ChannelMask & ~8 & 0xffff) | ((_ChannelMask & ~8 & 0xae66) << 16)));
			_NumberOfChannels = Math.Min(_TotalChannels, Channels);
			switch (_Flags & FlagBandLimitMask)
			{
				case FlagBandLimitNone: _BandLimit = 0; break;
				case FlagBandLimitHalf: _BandLimit = 1; break;
				case FlagBandLimitQuarter: _BandLimit = 2; break;
				default: return FfmpegError.PatchWelcome;
			}
			_FrequencyRange = DcaTables.FrequencyRanges[sampleRateCode];
			_ResidualProfile = _OriginalBitRate >= 44000 * (_TotalChannels + 2) ? 2 :
				_OriginalBitRate >= 25000 * (_TotalChannels + 2) ? 1 : 0;
			_LimitedRate = _SampleRate >> _BandLimit;
			_LimitedRange = _FrequencyRange - _BandLimit;
			if (_LimitedRange < 0 || _LimitedRate == 0 || _NumberOfChannels == 0) return FfmpegError.InvalidData;
			_NumberOfSubbands = 8 << _LimitedRange;
			_Grid3AverageOnlyStartSubband = Math.Min(_NumberOfSubbands,
				_NumberOfSubbands * DcaTables.AvgG3Freqs[_ResidualProfile] / (_LimitedRate / 2));
			_MinimumMonoSubband = Math.Min(_NumberOfSubbands, _NumberOfSubbands * 2000 / (_LimitedRate / 2));
			_MaximumMonoSubband = Math.Min(_NumberOfSubbands, _NumberOfSubbands * 14000 / (_LimitedRate / 2));
			if (oldSampleRate != _SampleRate || oldBandLimit != _BandLimit) InitializeSampleRate();
			if ((_Flags & FlagDownmixStereo) != 0)
			{
				if (_TotalChannels < 3 || _TotalChannels > ChannelsTotal - 2) return FfmpegError.InvalidData;
				_TotalChannels += 2;
				_NumberOfChannels = 2;
				_ChannelMask = 2;
				_Flags &= ~FlagLfePresent;
			}
			if (oldSampleRate != _SampleRate || oldBandLimit != _BandLimit || oldChannels != _NumberOfChannels) Flush();
			return 0;
		}

		private void InitializeSampleRate()
		{
			var scale = (-1.0 / (1 << 17)) * Math.Sqrt(1 << (2 - _LimitedRange));
			_Imdct = new FfmpegFloatMdct(1 << (_FrequencyRange + 5), true, (float)scale, true);
			for (var index = 0; index < 32 << _FrequencyRange; index++) _Window[index] = DcaTables.LongWindow[index << (2 - _FrequencyRange)];
			var bitRatePerChannel = _ScaledBitRate / _TotalChannels;
			if (bitRatePerChannel < 14000) scale = 0.85;
			else if (bitRatePerChannel < 32000) scale = (bitRatePerChannel - 14000) * (1.0 / 120000) + 0.85;
			else scale = 1.0;
			scale *= 1.0 / int.MaxValue;
			for (var subband = 0; subband < _NumberOfSubbands; subband++)
			{
				if (subband < 2) _SubbandScaleFactors[subband] = 0;
				else if (subband < 5) _SubbandScaleFactors[subband] = (float)((subband - 1) * 0.25 * 0.785 * scale);
				else _SubbandScaleFactors[subband] = (float)(0.785 * scale);
			}
			_LfeScale = (float)((16 << _FrequencyRange) * 0.0000078265894);
		}

		private void Flush()
		{
			for (var channel = 0; channel < Channels; channel++)
				for (var band = 0; band < Subbands / 4; band++)
					for (var scaleFactor = 0; scaleFactor < 5; scaleFactor++) _PartialStereo[channel, band, scaleFactor] = 16;
			Array.Clear(_LpcCoefficients, 0, _LpcCoefficients.Length);
			Array.Clear(_History, 0, _History.Length);
			Array.Clear(_TonalBounds, 0, _TonalBounds.Length);
			Array.Clear(_LfeHistory, 0, _LfeHistory.Length);
			_FrameNumber = 0;
			_NumberOfTones = 0;
			for (var channel = 0; channel < _NumberOfChannels; channel++)
				for (var subband = 0; subband < _NumberOfSubbands; subband++)
					Array.Clear(_TimeSampleBuffers[channel][subband], 0, TimeHistory);
		}

		private void ResetFrameState()
		{
			Array.Clear(_QuantLevels, 0, _QuantLevels.Length);
			Array.Fill(_SubbandIndices, byte.MaxValue);
			Array.Clear(_SecondaryChannelSumDifference, 0, _SecondaryChannelSumDifference.Length);
			Array.Clear(_SecondaryChannelLeftRight, 0, _SecondaryChannelLeftRight.Length);
			Array.Clear(_ChannelPresence, 0, _ChannelPresence.Length);
			Array.Clear(_Grid1ScaleFactors, 0, _Grid1ScaleFactors.Length);
			Array.Clear(_Grid2ScaleFactors, 0, _Grid2ScaleFactors.Length);
			Array.Clear(_Grid3Average, 0, _Grid3Average.Length);
			Array.Clear(_Grid3ScaleFactors, 0, _Grid3ScaleFactors.Length);
			Array.Clear(_Grid3Presence, 0, _Grid3Presence.Length);
			Array.Clear(_TonalScaleFactors, 0, _TonalScaleFactors.Length);
			Array.Clear(_LfeData, 0, _LfeData.Length);
			_PartialStereoPresence = 0;
			_FrameNumber = (_FrameNumber + 1) & 31;
			for (var channel = 0; channel < _NumberOfChannels; channel++)
				for (var subband = 0; subband < _NumberOfSubbands / 4; subband++)
				{
					_PartialStereo[channel, subband, 0] = _PartialStereo[channel, subband, 4];
					_PartialStereo[channel, subband, 4] = 16;
				}
			for (var channel = 0; channel < Channels; channel++)
				for (var subband = 0; subband < 3; subband++)
					for (var phase = 0; phase < 2; phase++)
						for (var coefficient = 0; coefficient < 8; coefficient++) _LpcCoefficients[_FrameNumber & 1, channel, subband, phase, coefficient] = 0;
			for (var group = 0; group < 5; group++)
				for (var scaleFactor = 0; scaleFactor < 1 << group; scaleFactor++)
				{
					var index = ((_FrameNumber << group) + scaleFactor) & 31;
					_TonalBounds[group, index, 0] = _TonalBounds[group, index, 1] = (ushort)_NumberOfTones;
				}
			_LfeChunk = default;
			_TonalChunk = default;
			Array.Clear(_TonalGroupChunks, 0, _TonalGroupChunks.Length);
			Array.Clear(_Grid1Chunks, 0, _Grid1Chunks.Length);
			Array.Clear(_HighResolutionGridChunks, 0, _HighResolutionGridChunks.Length);
			Array.Clear(_TimeSample1Chunks, 0, _TimeSample1Chunks.Length);
			Array.Clear(_TimeSample2Chunks, 0, _TimeSample2Chunks.Length);
		}

		private void CollectChunks()
		{
			while (BytesLeft > 0)
			{
				var identifier = ReadByte();
				var length = (identifier & 0x80) != 0 ? ReadBigEndian16() : ReadByte();
				identifier &= 0x7f;
				if (length > BytesLeft) length = BytesLeft;
				var chunk = new DcaLbrChunk(identifier, _BytePosition, length);
				if (identifier == ChunkLfe) _LfeChunk = chunk;
				else if (identifier == ChunkScaleFactors || identifier == ChunkTonal || identifier == ChunkTonalScaleFactors) _TonalChunk = chunk;
				else if (identifier >= ChunkTonalGroup1 && identifier <= ChunkTonalGroup5)
				{
					var index = ChunkTonalGroup5 - identifier;
					chunk.Identifier = index;
					_TonalGroupChunks[index] = chunk;
				} else if (identifier >= ChunkTonalScaleFactorGroup1 && identifier <= ChunkTonalScaleFactorGroup5)
				{
					var index = ChunkTonalScaleFactorGroup5 - identifier;
					chunk.Identifier = index;
					_TonalGroupChunks[index] = chunk;
				} else if (identifier >= ChunkResidualGridLowResolution && identifier < ChunkResidualGridLowResolution + 3)
					_Grid1Chunks[identifier - ChunkResidualGridLowResolution] = chunk;
				else if (identifier >= ChunkResidualGridHighResolution && identifier < ChunkResidualGridHighResolution + 3)
					_HighResolutionGridChunks[identifier - ChunkResidualGridHighResolution] = chunk;
				else if (identifier >= ChunkResidualTimeSamples1 && identifier < ChunkResidualTimeSamples1 + 3)
					_TimeSample1Chunks[identifier - ChunkResidualTimeSamples1] = chunk;
				else if (identifier >= ChunkResidualTimeSamples2 && identifier < ChunkResidualTimeSamples2 + 3)
					_TimeSample2Chunks[identifier - ChunkResidualTimeSamples2] = chunk;
				SkipBytes(length);
			}
		}

		private int ParseLfeChunk(DcaLbrChunk chunk)
		{
			if ((_Flags & FlagLfePresent) == 0 || chunk.Length == 0) return 0;
			if (_Bits.Initialize(_Data, chunk.Offset, chunk.Length * 8, true) < 0) return FfmpegError.InvalidData;
			if (chunk.Length >= 52) return ParseLfe24();
			if (chunk.Length >= 35) return ParseLfe16();
			return FfmpegError.InvalidData;
		}

		private int ParseLfe24()
		{
			var packedSample = (int)_Bits.ReadBits(24);
			var sign = packedSample >> 23;
			var value = (((packedSample & 0x7fffff) ^ -sign) + sign) * (1.0f / 0x7fffff);
			var stepIndex = (int)_Bits.ReadBits(8);
			var maximumStep = DcaTables.LfeStepSize24.Length - 1;
			if (stepIndex > maximumStep) return FfmpegError.InvalidData;
			var step = DcaTables.LfeStepSize24[stepIndex];
			for (var index = 0; index < 64; index++)
			{
				var code = (int)_Bits.ReadBits(6);
				var delta = step * 0.03125f;
				if ((code & 16) != 0) delta += step;
				if ((code & 8) != 0) delta += step * 0.5f;
				if ((code & 4) != 0) delta += step * 0.25f;
				if ((code & 2) != 0) delta += step * 0.125f;
				if ((code & 1) != 0) delta += step * 0.0625f;
				value = (code & 32) != 0 ? Math.Max(value - delta, -3.0f) : Math.Min(value + delta, 3.0f);
				stepIndex = Math.Clamp(stepIndex + DcaTables.LfeDeltaIndex24[code & 31], 0, maximumStep);
				step = DcaTables.LfeStepSize24[stepIndex];
				_LfeData[index] = value * _LfeScale;
			}
			return 0;
		}

		private int ParseLfe16()
		{
			var packedSample = (int)_Bits.ReadBits(16);
			var sign = packedSample >> 15;
			var value = (((packedSample & 0x7fff) ^ -sign) + sign) * (1.0f / 0x7fff);
			var stepIndex = (int)_Bits.ReadBits(8);
			var maximumStep = DcaTables.LfeStepSize16.Length - 1;
			if (stepIndex > maximumStep) return FfmpegError.InvalidData;
			var step = DcaTables.LfeStepSize16[stepIndex];
			for (var index = 0; index < 64; index++)
			{
				var code = (int)_Bits.ReadBits(4);
				var delta = step * 0.125f;
				if ((code & 4) != 0) delta += step;
				if ((code & 2) != 0) delta += step * 0.5f;
				if ((code & 1) != 0) delta += step * 0.25f;
				value = (code & 8) != 0 ? Math.Max(value - delta, -3.0f) : Math.Min(value + delta, 3.0f);
				stepIndex = Math.Clamp(stepIndex + DcaTables.LfeDeltaIndex16[code & 7], 0, maximumStep);
				step = DcaTables.LfeStepSize16[stepIndex];
				_LfeData[index] = value * _LfeScale;
			}
			return 0;
		}

		private int ParseTonalChunk(DcaLbrChunk chunk)
		{
			if (chunk.Length == 0) return 0;
			if (_Bits.Initialize(_Data, chunk.Offset, chunk.Length * 8, true) < 0) return FfmpegError.InvalidData;
			if (chunk.Identifier == ChunkScaleFactors || chunk.Identifier == ChunkTonalScaleFactors)
			{
				if (_Bits.BitsLeft < 36) return FfmpegError.InvalidData;
				for (var subband = 0; subband < 6; subband++) _TonalScaleFactors[subband] = (byte)_Bits.ReadBits(6);
			}
			if (chunk.Identifier == ChunkTonal || chunk.Identifier == ChunkTonalScaleFactors)
				for (var group = 0; group < 5; group++)
				{
					var result = ParseTonal(group);
					if (result < 0) return result;
				}
			return 0;
		}

		private int ParseTonalGroup(DcaLbrChunk chunk)
		{
			if (chunk.Length == 0) return 0;
			if (_Bits.Initialize(_Data, chunk.Offset, chunk.Length * 8, true) < 0) return FfmpegError.InvalidData;
			return ParseTonal(chunk.Identifier);
		}

		/// <summary>
		/// Decodes one LBR tonal group, including differential frequency, amplitude, phase, and rotation state.
		/// </summary>
		private int ParseTonal(int group)
		{
			var channelBitCount = CeilingLog2(_TotalChannels);
			uint difference = 0;
			for (var scaleFactor = 0; scaleFactor < 1 << group; scaleFactor += difference != 0 ? 8 : 1)
			{
				var scaleFactorIndex = ((_FrameNumber << group) + scaleFactor) & 31;
				_TonalBounds[group, scaleFactorIndex, 0] = (ushort)_NumberOfTones;
				for (var frequency = 1; ; frequency++)
				{
					if (_Bits.BitsLeft < 1) return FfmpegError.InvalidData;
					difference = (uint)ParseVlc(DcaTables.TonalGroupVlc[group], 2);
					if (difference >= DcaTables.FstAmp.Length) return FfmpegError.InvalidData;
					difference = _Bits.ReadBitsOrZero((int)difference >> 2) + DcaTables.FstAmp[difference];
					if (difference <= 1) break;
					frequency += (int)difference - 2;
					if (frequency >> (5 - group) > _NumberOfSubbands * 4 - 6) return FfmpegError.InvalidData;
					var mainChannel = channelBitCount != 0 ? (int)_Bits.ReadBits(channelBitCount) : 0;
					if ((uint)mainChannel >= (uint)_TotalChannels) return FfmpegError.InvalidData;
					var mainAmplitude = ParseVlc(DcaTables.TonalScaleFactorVlc, 2) +
						_TonalScaleFactors[DcaTables.FreqToSb[frequency >> (7 - group)]] + _LimitedRange - 2;
					_TonalAmplitudes[mainChannel] = mainAmplitude < AmplitudeMaximum ? (uint)mainAmplitude : 0;
					_TonalPhases[mainChannel] = _Bits.ReadBits(3);
					for (var channel = 0; channel < _TotalChannels; channel++)
					{
						if (channel == mainChannel) continue;
						if (_Bits.ReadBit() != 0)
						{
							_TonalAmplitudes[channel] = unchecked(_TonalAmplitudes[mainChannel] - (uint)ParseVlc(DcaTables.DampingVlc, 1));
							_TonalPhases[channel] = unchecked(_TonalPhases[mainChannel] - (uint)ParseVlc(DcaTables.PhaseDifferenceVlc, 1));
						} else _TonalAmplitudes[channel] = _TonalPhases[channel] = 0;
					}
					if (_TonalAmplitudes[mainChannel] != 0)
					{
						var tone = _Tones[_NumberOfTones];
						_NumberOfTones = (_NumberOfTones + 1) & (Tones - 1);
						tone.Frequency = (byte)(frequency >> (5 - group));
						tone.FrequencyDelta = (byte)((frequency & ((1 << (5 - group)) - 1)) << group);
						tone.PhaseRotation = unchecked((byte)(256 - (tone.Frequency & 1) * 128 - tone.FrequencyDelta * 4));
						var shift = DcaTables.Ph0Shift[(tone.Frequency & 3) * 2 + (frequency & 1)] -
							((tone.PhaseRotation << (5 - group)) - tone.PhaseRotation);
						for (var channel = 0; channel < _NumberOfChannels; channel++)
						{
							tone.Amplitude[channel] = _TonalAmplitudes[channel] < AmplitudeMaximum ? (byte)_TonalAmplitudes[channel] : (byte)0;
							tone.Phase[channel] = unchecked((byte)(128 - _TonalPhases[channel] * 32 + shift));
						}
					}
				}
				_TonalBounds[group, scaleFactorIndex, 1] = (ushort)_NumberOfTones;
			}
			return 0;
		}

		private int ParseScaleFactors(int channel, int band)
		{
			if (EnsureBits(20) != 0) return 0;
			var previous = ParseVlc(DcaTables.FirstResidualAmplitudeVlc, 2);
			var next = 0;
			var scaleFactor = 0;
			while (scaleFactor < 7)
			{
				_Grid1ScaleFactors[channel, band, scaleFactor] = (byte)previous;
				if (EnsureBits(20) != 0) return 0;
				var distance = ParseVlc(DcaTables.ResidualApproximationVlc, 1) + 1;
				if (distance > 7 - scaleFactor) return FfmpegError.InvalidData;
				if (EnsureBits(20) != 0) return 0;
				next = ParseVlc(DcaTables.ResidualAmplitudeVlc, 2);
				next = (next & 1) != 0 ? previous + ((next + 1) >> 1) : previous - (next >> 1);
				if (distance == 2)
					_Grid1ScaleFactors[channel, band, scaleFactor + 1] = (byte)(next > previous ? previous + ((next - previous) >> 1) : previous - ((previous - next) >> 1));
				else if (distance == 4)
				{
					if (next > previous)
					{
						_Grid1ScaleFactors[channel, band, scaleFactor + 1] = (byte)(previous + ((next - previous) >> 2));
						_Grid1ScaleFactors[channel, band, scaleFactor + 2] = (byte)(previous + ((next - previous) >> 1));
						_Grid1ScaleFactors[channel, band, scaleFactor + 3] = (byte)(previous + ((next - previous) * 3 >> 2));
					} else
					{
						_Grid1ScaleFactors[channel, band, scaleFactor + 1] = (byte)(previous - ((previous - next) >> 2));
						_Grid1ScaleFactors[channel, band, scaleFactor + 2] = (byte)(previous - ((previous - next) >> 1));
						_Grid1ScaleFactors[channel, band, scaleFactor + 3] = (byte)(previous - ((previous - next) * 3 >> 2));
					}
				} else
					for (var index = 1; index < distance; index++)
						_Grid1ScaleFactors[channel, band, scaleFactor + index] = (byte)(previous + (next - previous) * index / distance);
				previous = next;
				scaleFactor += distance;
			}
			_Grid1ScaleFactors[channel, band, scaleFactor] = (byte)next;
			return 0;
		}

		private int ParseGrid1Chunk(DcaLbrChunk chunk, int firstChannel, int secondChannel)
		{
			if (chunk.Length == 0) return 0;
			if (_Bits.Initialize(_Data, chunk.Offset, chunk.Length * 8, true) < 0) return FfmpegError.InvalidData;
			var gridSubbands = DcaTables.ScfToGrid1[_NumberOfSubbands - 1] + 1;
			for (var band = 2; band < gridSubbands; band++)
			{
				var result = ParseScaleFactors(firstChannel, band);
				if (result < 0) return result;
				if (firstChannel != secondChannel && DcaTables.Grid1ToScf[band] < _MinimumMonoSubband)
				{
					result = ParseScaleFactors(secondChannel, band);
					if (result < 0) return result;
				}
			}
			if (_Bits.BitsLeft < 1) return 0;
			for (var band = 0; band < _NumberOfSubbands - 4; band++)
			{
				_Grid3Average[firstChannel, band] = (sbyte)(ParseVlc(DcaTables.AverageGroupThreeVlc, 2) - 16);
				if (firstChannel != secondChannel)
					_Grid3Average[secondChannel, band] = band + 4 < _MinimumMonoSubband
						? (sbyte)(ParseVlc(DcaTables.AverageGroupThreeVlc, 2) - 16) : _Grid3Average[firstChannel, band];
			}
			if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
			if (firstChannel != secondChannel)
			{
				if (EnsureBits(8) != 0) return 0;
				var firstMinimum = (int)_Bits.ReadBits(4);
				var secondMinimum = (int)_Bits.ReadBits(4);
				var partialBands = (_NumberOfSubbands - _MinimumMonoSubband + 3) / 4;
				for (var band = 0; band < partialBands; band++)
					for (var channel = firstChannel; channel <= secondChannel; channel++)
						for (var scaleFactor = 1; scaleFactor <= 4; scaleFactor++)
							_PartialStereo[channel, band, scaleFactor] = (byte)ParseStereoCode(channel == firstChannel ? firstMinimum : secondMinimum);
				if (_Bits.BitsLeft >= 0) _PartialStereoPresence |= 1 << firstChannel;
			}
			return 0;
		}

		private int ParseGrid1SecondaryChannel(int secondChannel)
		{
			var gridSubbands = DcaTables.ScfToGrid1[_NumberOfSubbands - 1] + 1;
			for (var band = 2; band < gridSubbands; band++)
				if (DcaTables.Grid1ToScf[band] >= _MinimumMonoSubband)
				{
					var result = ParseScaleFactors(secondChannel, band);
					if (result < 0) return result;
				}
			for (var band = 0; band < _NumberOfSubbands - 4; band++)
				if (band + 4 >= _MinimumMonoSubband)
				{
					if (EnsureBits(20) != 0) return 0;
					_Grid3Average[secondChannel, band] = (sbyte)(ParseVlc(DcaTables.AverageGroupThreeVlc, 2) - 16);
				}
			return 0;
		}

		private void ParseGrid3(int firstChannel, int secondChannel, int band, int flag)
		{
			for (var channel = firstChannel; channel <= secondChannel; channel++)
			{
				if (((channel != firstChannel && band + 4 >= _MinimumMonoSubband) ? 1 : 0) != flag) continue;
				if ((_Grid3Presence[channel] & 1U << band) != 0) continue;
				for (var index = 0; index < 8; index++)
				{
					if (EnsureBits(20) != 0) return;
					_Grid3ScaleFactors[channel, band, index] = (sbyte)(ParseVlc(DcaTables.GridThreeVlc, 2) - 16);
				}
				_Grid3Presence[channel] |= 1U << band;
			}
		}

		private int ParseHighResolutionGrid(DcaLbrChunk chunk, int firstChannel, int secondChannel)
		{
			if (chunk.Length == 0) return 0;
			if (_Bits.Initialize(_Data, chunk.Offset, chunk.Length * 8, true) < 0) return FfmpegError.InvalidData;
			var profile = (int)_Bits.ReadBits(8);
			var overlap = profile >> 3 & 7;
			var stereo = profile >> 6;
			var maximumSubbandProfile = profile & 7;
			for (var subband = 0; subband < _NumberOfSubbands; subband++)
			{
				var frequency = subband * _LimitedRate / _NumberOfSubbands;
				var amplitude = 18000 / (12 * frequency / 1000 + 100 + 40 * stereo) + 20 * overlap;
				_QuantLevelScratch[subband] = amplitude <= 95 ? 1 : amplitude <= 140 ? 2 : amplitude <= 180 ? 3 : amplitude <= 230 ? 4 : 5;
			}
			var index = 0;
			for (; index < 8; index++) _QuantLevels[firstChannel / 2, index] = (byte)_QuantLevelScratch[DcaTables.SbReorder[maximumSubbandProfile * 8 + index]];
			for (; index < _NumberOfSubbands; index++) _QuantLevels[firstChannel / 2, index] = (byte)_QuantLevelScratch[index];
			var result = ParseLinearPrediction(firstChannel, secondChannel, 0, 2);
			if (result < 0) return result;
			result = ParseTimeSamples(firstChannel, secondChannel, 0, 2, 0);
			if (result < 0) return result;
			for (var band = 0; band < 2; band++)
				for (var channel = firstChannel; channel <= secondChannel; channel++)
				{
					result = ParseScaleFactors(channel, band);
					if (result < 0) return result;
				}
			return 0;
		}

		private int ParseGrid2(int firstChannel, int secondChannel, int startBand, int endBand, int flag)
		{
			var gridSubbands = DcaTables.ScfToGrid2[_NumberOfSubbands - 1] + 1;
			endBand = Math.Min(endBand, gridSubbands);
			for (var band = startBand; band < endBand; band++)
				for (var channel = firstChannel; channel <= secondChannel; channel++)
				{
					if (((channel != firstChannel && DcaTables.Grid2ToScf[band] >= _MinimumMonoSubband) ? 1 : 0) != flag)
					{
						if (flag == 0)
							for (var index = 0; index < 64; index++) _Grid2ScaleFactors[channel, band, index] = _Grid2ScaleFactors[firstChannel, band, index];
						continue;
					}
					for (var group = 0; group < 8; group++)
					{
						if (_Bits.BitsLeft < 1)
						{
							for (var index = group * 8; index < 64; index++) _Grid2ScaleFactors[channel, band, index] = 0;
							break;
						}
						if (_Bits.ReadBit() != 0)
						{
							for (var index = 0; index < 8; index++)
							{
								if (EnsureBits(20) != 0) break;
								_Grid2ScaleFactors[channel, band, group * 8 + index] = (byte)ParseVlc(DcaTables.GridTwoVlc, 2);
							}
						} else
							for (var index = 0; index < 8; index++) _Grid2ScaleFactors[channel, band, group * 8 + index] = 0;
					}
				}
			return 0;
		}

		private int ParseTimeSample1Chunk(DcaLbrChunk chunk, int firstChannel, int secondChannel)
		{
			if (chunk.Length == 0) return 0;
			if (_Bits.Initialize(_Data, chunk.Offset, chunk.Length * 8, true) < 0) return FfmpegError.InvalidData;
			var result = ParseLinearPrediction(firstChannel, secondChannel, 2, 3);
			if (result < 0) return result;
			result = ParseTimeSamples(firstChannel, secondChannel, 2, 4, 0);
			if (result < 0) return result;
			result = ParseGrid2(firstChannel, secondChannel, 0, 1, 0);
			if (result < 0) return result;
			return ParseTimeSamples(firstChannel, secondChannel, 4, 6, 0);
		}

		private int ParseTimeSample2Chunk(DcaLbrChunk chunk, int firstChannel, int secondChannel)
		{
			if (chunk.Length == 0) return 0;
			if (_Bits.Initialize(_Data, chunk.Offset, chunk.Length * 8, true) < 0) return FfmpegError.InvalidData;
			var result = ParseGrid2(firstChannel, secondChannel, 1, 3, 0);
			if (result < 0) return result;
			result = ParseTimeSamples(firstChannel, secondChannel, 6, _MaximumMonoSubband, 0);
			if (result < 0) return result;
			if (firstChannel != secondChannel)
			{
				result = ParseGrid1SecondaryChannel(secondChannel);
				if (result < 0) return result;
				result = ParseGrid2(firstChannel, secondChannel, 0, 3, 1);
				if (result < 0) return result;
			}
			return ParseTimeSamples(firstChannel, secondChannel, _MinimumMonoSubband, _NumberOfSubbands, 1);
		}

		private int ParseTimeSamples(int firstChannel, int secondChannel, int startSubband, int endSubband, int flag)
		{
			for (var subband = startSubband; subband < endSubband; subband++)
			{
				int reorderedSubband;
				if (subband < 6) reorderedSubband = subband;
				else if (flag != 0 && subband < _MaximumMonoSubband) reorderedSubband = _SubbandIndices[subband];
				else
				{
					if (EnsureBits(28) != 0) break;
					reorderedSubband = (int)_Bits.ReadBits(_LimitedRange + 3);
					if (reorderedSubband < 6) reorderedSubband = 6;
					_SubbandIndices[subband] = (byte)reorderedSubband;
				}
				if (reorderedSubband >= _NumberOfSubbands) return FfmpegError.InvalidData;
				if (subband == 12)
					for (var gridBand = 0; gridBand < _Grid3AverageOnlyStartSubband - 4; gridBand++) ParseGrid3(firstChannel, secondChannel, gridBand, flag);
				else if (subband < 12 && reorderedSubband >= 4) ParseGrid3(firstChannel, secondChannel, reorderedSubband - 4, flag);
				if (firstChannel != secondChannel)
				{
					if (EnsureBits(20) != 0) break;
					if (flag == 0 || reorderedSubband >= _MaximumMonoSubband)
						_SecondaryChannelSumDifference[firstChannel / 2, reorderedSubband] = (byte)_Bits.ReadBits(8);
					if (flag != 0 && reorderedSubband >= _MinimumMonoSubband)
						_SecondaryChannelLeftRight[firstChannel / 2, reorderedSubband] = (byte)_Bits.ReadBits(8);
				}
				var quantizationLevel = _QuantLevels[firstChannel / 2, subband];
				if (quantizationLevel == 0) return FfmpegError.InvalidData;
				if (subband < _MaximumMonoSubband && reorderedSubband >= _MinimumMonoSubband)
				{
					if (flag == 0) ParseChannel(firstChannel, reorderedSubband, quantizationLevel, 0);
					else if (firstChannel != secondChannel) ParseChannel(secondChannel, reorderedSubband, quantizationLevel, 1);
				} else
				{
					ParseChannel(firstChannel, reorderedSubband, quantizationLevel, 0);
					if (firstChannel != secondChannel) ParseChannel(secondChannel, reorderedSubband, quantizationLevel, 0);
				}
			}
			return 0;
		}

		/// <summary>
		/// Decodes one LBR subband's residual samples using the exact quantizer-specific packing branch.
		/// </summary>
		private void ParseChannel(int channel, int subband, int quantizationLevel, int flag)
		{
			if (EnsureBits(20) != 0) return;
			var codingMethod = (int)_Bits.ReadBit();
			var samples = _TimeSampleBuffers[channel][subband];
			var sample = 0;
			if (quantizationLevel == 1)
			{
				var blocks = Math.Min(_Bits.BitsLeft / 8, TimeSamples / 8);
				for (var block = 0; block < blocks; block++)
				{
					var code = (int)_Bits.ReadBits(8);
					for (var index = 0; index < 8; index++) samples[TimeHistory + block * 8 + index] = DcaTables.RsdLevel2a[code >> index & 1];
				}
				sample = blocks * 8;
			} else if (quantizationLevel == 2)
			{
				if (codingMethod != 0)
				{
					for (sample = 0; sample < TimeSamples && _Bits.BitsLeft >= 2; sample++)
						samples[TimeHistory + sample] = _Bits.ReadBit() != 0 ? DcaTables.RsdLevel2b[_Bits.ReadBit()] : 0;
				} else
				{
					var blocks = Math.Min(_Bits.BitsLeft / 8, (TimeSamples + 4) / 5);
					for (var block = 0; block < blocks; block++)
					{
						var code = DcaTables.RsdPack5In8[_Bits.ReadBits(8)];
						for (var index = 0; index < 5; index++) samples[TimeHistory + block * 5 + index] = DcaTables.RsdLevel3[code >> (index * 2) & 3];
					}
					sample = blocks * 5;
				}
			} else if (quantizationLevel == 3)
			{
				var blocks = Math.Min(_Bits.BitsLeft / 7, (TimeSamples + 2) / 3);
				for (var block = 0; block < blocks; block++)
				{
					var code = (int)_Bits.ReadBits(7);
					for (var index = 0; index < 3; index++) samples[TimeHistory + block * 3 + index] = DcaTables.RsdLevel5[DcaTables.RsdPack3In7[code * 3 + index]];
				}
				sample = blocks * 3;
			} else if (quantizationLevel == 4)
			{
				for (sample = 0; sample < TimeSamples && _Bits.BitsLeft >= 6; sample++)
					samples[TimeHistory + sample] = DcaTables.RsdLevel8[_Bits.ReadVlc(DcaTables.ResidualVlc.Table, 6, 1)];
			} else if (quantizationLevel == 5)
			{
				var count = Math.Min(_Bits.BitsLeft / 4, TimeSamples);
				for (sample = 0; sample < count; sample++) samples[TimeHistory + sample] = DcaTables.RsdLevel16[_Bits.ReadBits(4)];
			} else return;
			if (flag != 0 && _Bits.BitsLeft < 20) return;
			for (; sample < TimeSamples; sample++) samples[TimeHistory + sample] = NextRandom(subband);
			_ChannelPresence[channel] |= 1U << subband;
		}

		private int ParseLinearPrediction(int firstChannel, int secondChannel, int startSubband, int endSubband)
		{
			var frame = _FrameNumber & 1;
			for (var subband = startSubband; subband < endSubband; subband++)
			{
				var codeCount = 8 * (1 + (subband < 2 ? 1 : 0));
				for (var channel = firstChannel; channel <= secondChannel; channel++)
				{
					if (EnsureBits(4 * codeCount) != 0) return 0;
					for (var index = 0; index < codeCount; index++) _LpcCodes[index] = (int)_Bits.ReadBits(4);
					for (var phase = 0; phase < codeCount / 8; phase++) ConvertLinearPrediction(frame, channel, subband, phase, phase * 8);
				}
			}
			return 0;
		}

		private void ConvertLinearPrediction(int frame, int channel, int subband, int phase, int codeOffset)
		{
			for (var index = 0; index < 8; index++)
			{
				var reflection = LpcTable[_LpcCodes[codeOffset + index]];
				for (var coefficient = 0; coefficient < (index + 1) / 2; coefficient++)
				{
					var first = _LpcCoefficients[frame, channel, subband, phase, coefficient];
					var second = _LpcCoefficients[frame, channel, subband, phase, index - coefficient - 1];
					_LpcCoefficients[frame, channel, subband, phase, coefficient] = first + reflection * second;
					_LpcCoefficients[frame, channel, subband, phase, index - coefficient - 1] = second + reflection * first;
				}
				_LpcCoefficients[frame, channel, subband, phase, index] = reflection;
			}
		}

		private void DecodeGrid(int firstChannel, int secondChannel)
		{
			for (var channel = firstChannel; channel <= secondChannel; channel++)
				for (var subband = 0; subband < _NumberOfSubbands; subband++)
				{
					var grid1Band = DcaTables.ScfToGrid1[subband];
					var firstWeight = DcaTables.Grid1Weights[grid1Band * 32 + subband];
					var secondWeight = DcaTables.Grid1Weights[(grid1Band + 1) * 32 + subband];
					for (var index = 0; index < 8; index++)
					{
						var scaleFactor = firstWeight * _Grid1ScaleFactors[channel, grid1Band, index] +
							secondWeight * _Grid1ScaleFactors[channel, grid1Band + 1, index];
						if (subband < 4) _HighResolutionScaleFactors[channel, subband, index] = (byte)(scaleFactor >> 7);
						else _HighResolutionScaleFactors[channel, subband, index] = unchecked((byte)((scaleFactor >> 7) -
							_Grid3Average[channel, subband - 4] - _Grid3ScaleFactors[channel, subband - 4, index]));
					}
				}
		}

		private void GenerateRandomTimeSamples(int firstChannel, int secondChannel)
		{
			for (var channel = firstChannel; channel <= secondChannel; channel++)
				for (var subband = 0; subband < _NumberOfSubbands; subband++)
				{
					if ((_ChannelPresence[channel] & 1U << subband) != 0) continue;
					var samples = _TimeSampleBuffers[channel][subband];
					if (subband < 2) Array.Clear(samples, TimeHistory, TimeSamples);
					else if (subband < 10)
						for (var index = 0; index < TimeSamples; index++) samples[TimeHistory + index] = NextRandom(subband);
					else
						for (var block = 0; block < TimeSamples / 8; block++)
						{
							Array.Clear(_RandomAccumulation, 0, _RandomAccumulation.Length);
							for (var otherBand = 2; otherBand < 6; otherBand++)
							{
								var other = _TimeSampleBuffers[channel][otherBand];
								for (var index = 0; index < 8; index++) _RandomAccumulation[index] += MathF.Abs(other[TimeHistory + block * 8 + index]);
							}
							for (var index = 0; index < 8; index++) samples[TimeHistory + block * 8 + index] =
								(_RandomAccumulation[index] * 0.25f + 0.5f) * NextRandom(subband);
						}
				}
		}

		private void FilterTimeSamples(int firstChannel, int secondChannel)
		{
			for (var subband = 0; subband < _NumberOfSubbands; subband++)
			{
				for (var channel = firstChannel; channel <= secondChannel; channel++)
				{
					var samples = _TimeSampleBuffers[channel][subband];
					if (subband < 4)
					{
						for (var block = 0; block < TimeSamples / 16; block++)
						{
							var scaleFactor = Math.Min((int)_HighResolutionScaleFactors[channel, subband, block], AmplitudeMaximum);
							for (var index = 0; index < 16; index++) samples[TimeHistory + block * 16 + index] *= DcaTables.QuantAmp[scaleFactor];
						}
					} else
					{
						var grid2Band = DcaTables.ScfToGrid2[subband];
						for (var pair = 0; pair < TimeSamples / 2; pair++)
						{
							var difference = _HighResolutionScaleFactors[channel, subband, pair / 8] - _Grid2ScaleFactors[channel, grid2Band, pair];
							var scaleFactor = difference < 0 || difference > AmplitudeMaximum ? AmplitudeMaximum : difference;
							samples[TimeHistory + pair * 2] *= DcaTables.QuantAmp[scaleFactor];
							samples[TimeHistory + pair * 2 + 1] *= DcaTables.QuantAmp[scaleFactor];
						}
					}
				}
				if (firstChannel != secondChannel) DecodeStereoTimeSamples(firstChannel, secondChannel, subband);
				if (subband < 3) SynthesizeLinearPrediction(firstChannel, secondChannel, subband);
			}
		}

		private void DecodeStereoTimeSamples(int firstChannel, int secondChannel, int subband)
		{
			var left = _TimeSampleBuffers[firstChannel][subband];
			var right = _TimeSampleBuffers[secondChannel][subband];
			var secondPresent = (_ChannelPresence[secondChannel] & 1U << subband) != 0;
			for (var block = 0; block < TimeSamples / 16; block++)
			{
				var sumDifference = (_SecondaryChannelSumDifference[firstChannel / 2, subband] >> block) & 1;
				var leftRight = (_SecondaryChannelLeftRight[firstChannel / 2, subband] >> block) & 1;
				var offset = TimeHistory + block * 16;
				if (subband >= _MinimumMonoSubband)
				{
					if (leftRight != 0 && secondPresent)
						for (var index = 0; index < 16; index++)
						{
							var temporary = left[offset + index];
							left[offset + index] = right[offset + index];
							right[offset + index] = sumDifference != 0 ? -temporary : temporary;
						} else if (!secondPresent)
						for (var index = 0; index < 16; index++) right[offset + index] =
							sumDifference != 0 && (_PartialStereoPresence & 1 << firstChannel) != 0 ? -left[offset + index] : left[offset + index];
				} else if (sumDifference != 0 && secondPresent)
					for (var index = 0; index < 16; index++)
					{
						var temporary = left[offset + index];
						left[offset + index] = (temporary + right[offset + index]) * 0.5f;
						right[offset + index] = (temporary - right[offset + index]) * 0.5f;
					}
			}
		}

		private void SynthesizeLinearPrediction(int firstChannel, int secondChannel, int subband)
		{
			var frame = _FrameNumber & 1;
			for (var channel = firstChannel; channel <= secondChannel; channel++)
			{
				if ((_ChannelPresence[channel] & 1U << subband) == 0) continue;
				if (subband < 2)
				{
					Predict(channel, subband, 0, frame ^ 1, 1, 16);
					Predict(channel, subband, 16, frame, 0, 64);
					Predict(channel, subband, 80, frame, 1, 48);
				} else
				{
					Predict(channel, subband, 0, frame ^ 1, 0, 16);
					Predict(channel, subband, 16, frame, 0, 112);
				}
			}
		}

		private void Predict(int channel, int subband, int start, int frame, int phase, int count)
		{
			var samples = _TimeSampleBuffers[channel][subband];
			var offset = TimeHistory + start;
			for (var sample = 0; sample < count; sample++)
			{
				var residual = 0.0f;
				for (var coefficient = 0; coefficient < 8; coefficient++) residual +=
					_LpcCoefficients[frame, channel, subband, phase, coefficient] * samples[offset + sample - coefficient - 1];
				samples[offset + sample] -= residual;
			}
		}

		private void DecodePartialStereo(int firstChannel, int secondChannel)
		{
			for (var channel = firstChannel; channel <= secondChannel; channel++)
				for (var subband = _MinimumMonoSubband; subband < _NumberOfSubbands; subband++)
				{
					if ((_ChannelPresence[secondChannel] & 1U << subband) != 0) continue;
					var partialBand = (subband - _MinimumMonoSubband) / 4;
					var samples = _TimeSampleBuffers[channel][subband];
					for (var scaleFactor = 1; scaleFactor <= 4; scaleFactor++)
					{
						var previous = DcaTables.StCoeff[_PartialStereo[channel, partialBand, scaleFactor - 1]];
						var next = DcaTables.StCoeff[_PartialStereo[channel, partialBand, scaleFactor]];
						for (var index = 0; index < 32; index++) samples[TimeHistory + (scaleFactor - 1) * 32 + index] *=
							(32 - index) * previous + index * next;
					}
				}
		}

		private void TransformChannel(int channel, Span<float> output)
		{
			var outputSubbands = 8 << _FrequencyRange;
			var transformLength = outputSubbands * 4;
			if (_NumberOfSubbands < outputSubbands) Array.Clear(_TransformValues, _NumberOfSubbands * 4, (outputSubbands - _NumberOfSubbands) * 4);
			for (var scaleFactor = 0; scaleFactor < TimeSamples / 4; scaleFactor++)
			{
				ApplyLbrBank(channel, scaleFactor * 4, _NumberOfSubbands);
				SynthesizeBaseFunction(channel, scaleFactor);
				_Imdct.Transform(_TransformValues.AsSpan(0, transformLength), _TransformResult.AsSpan(0, transformLength * 2));
				var outputOffset = scaleFactor * transformLength;
				for (var index = 0; index < transformLength; index++)
				{
					output[outputOffset + index] = _TransformResult[index] * _Window[index] + _History[channel, index];
					_History[channel, index] = _TransformResult[transformLength + index] * _Window[transformLength - index - 1];
				}
			}
			for (var subband = 0; subband < _NumberOfSubbands; subband++)
				Array.Copy(_TimeSampleBuffers[channel][subband], TimeSamples, _TimeSampleBuffers[channel][subband], 0, TimeHistory);
		}

		private void ApplyLbrBank(int channel, int sampleOffset, int length)
		{
			var switch0 = DcaTables.BankCoeff[0];
			var switch1 = DcaTables.BankCoeff[1];
			var switch2 = DcaTables.BankCoeff[2];
			var switch3 = DcaTables.BankCoeff[3];
			var coefficient1 = DcaTables.BankCoeff[4];
			var coefficient2 = DcaTables.BankCoeff[5];
			var coefficient3 = DcaTables.BankCoeff[6];
			var coefficient4 = DcaTables.BankCoeff[7];
			for (var subband = 0; subband < length; subband++)
			{
				var source = _TimeSampleBuffers[channel][subband];
				var offset = TimeHistory + sampleOffset;
				var a = source[offset - 4] * switch0 - source[offset - 1] * switch3;
				var b = source[offset - 3] * switch1 - source[offset - 2] * switch2;
				var c = source[offset + 2] * switch1 + source[offset + 1] * switch2;
				var d = source[offset + 3] * switch0 + source[offset] * switch3;
				var target = subband * 4;
				_TransformValues[target] = coefficient1 * b - coefficient2 * c + coefficient4 * a - coefficient3 * d;
				_TransformValues[target + 1] = coefficient1 * d - coefficient2 * a - coefficient4 * b - coefficient3 * c;
				_TransformValues[target + 2] = coefficient3 * b + coefficient2 * d - coefficient4 * c + coefficient1 * a;
				_TransformValues[target + 3] = coefficient3 * a - coefficient2 * b + coefficient4 * d - coefficient1 * c;
			}
			var allPass1 = DcaTables.BankCoeff[8];
			var allPass2 = DcaTables.BankCoeff[9];
			for (var subband = 12; subband < length - 1; subband++)
			{
				var current = subband * 4;
				var next = current + 4;
				var a = _TransformValues[current + 3] * allPass1;
				var b = _TransformValues[next] * allPass1;
				_TransformValues[current + 3] += b - a;
				_TransformValues[next] -= b + a;
				a = _TransformValues[current + 2] * allPass2;
				b = _TransformValues[next + 1] * allPass2;
				_TransformValues[current + 2] += b - a;
				_TransformValues[next + 1] -= b + a;
			}
		}

		private void SynthesizeBaseFunction(int channel, int scaleFactor)
		{
			for (var group = 0; group < 5; group++)
			{
				var groupScaleFactor = (_FrameNumber << group) + ((scaleFactor - 22) >> (5 - group));
				var synthesisIndex = ((((scaleFactor - 22) & 31) << group) & 31) + (1 << group) - 1;
				SynthesizeTones(channel, group, (groupScaleFactor - 1) & 31, 30 - synthesisIndex);
				SynthesizeTones(channel, group, groupScaleFactor & 31, synthesisIndex);
			}
		}

		/// <summary>
		/// Accumulates active LBR tones into four synthesis slots with FFmpeg's phase and amplitude interpolation.
		/// </summary>
		private void SynthesizeTones(int channel, int group, int groupScaleFactor, int synthesisIndex)
		{
			if (synthesisIndex < 0) return;
			var start = _TonalBounds[group, groupScaleFactor, 0];
			var count = (_TonalBounds[group, groupScaleFactor, 1] - start) & (Tones - 1);
			for (var index = 0; index < count; index++)
			{
				var tone = _Tones[(start + index) & (Tones - 1)];
				if (tone.Amplitude[channel] != 0)
				{
					var amplitude = DcaTables.SynthEnv[synthesisIndex] * DcaTables.QuantAmp[tone.Amplitude[channel]];
					var cosine = amplitude * Cosine[tone.Phase[channel]];
					var sine = amplitude * Cosine[(tone.Phase[channel] + 64) & 255];
					var coefficient = tone.FrequencyDelta * 11;
					var frequency = tone.Frequency;
					switch (frequency)
					{
						case 0:
							goto P0;
						case 1:
							_TransformValues[3] += DcaTables.CorrCf[coefficient] * -sine;
							_TransformValues[2] += DcaTables.CorrCf[coefficient + 1] * cosine;
							_TransformValues[1] += DcaTables.CorrCf[coefficient + 2] * sine;
							_TransformValues[0] += DcaTables.CorrCf[coefficient + 3] * -cosine;
							goto P1;
						case 2:
							_TransformValues[2] += DcaTables.CorrCf[coefficient] * -sine;
							_TransformValues[1] += DcaTables.CorrCf[coefficient + 1] * cosine;
							_TransformValues[0] += DcaTables.CorrCf[coefficient + 2] * sine;
							goto P2;
						case 3:
							_TransformValues[1] += DcaTables.CorrCf[coefficient] * -sine;
							_TransformValues[0] += DcaTables.CorrCf[coefficient + 1] * cosine;
							goto P3;
						case 4:
							_TransformValues[0] += DcaTables.CorrCf[coefficient] * -sine;
							goto P4;
					}
					_TransformValues[frequency - 5] += DcaTables.CorrCf[coefficient] * -sine;
				P4:
					_TransformValues[frequency - 4] += DcaTables.CorrCf[coefficient + 1] * cosine;
				P3:
					_TransformValues[frequency - 3] += DcaTables.CorrCf[coefficient + 2] * sine;
				P2:
					_TransformValues[frequency - 2] += DcaTables.CorrCf[coefficient + 3] * -cosine;
				P1:
					_TransformValues[frequency - 1] += DcaTables.CorrCf[coefficient + 4] * -sine;
				P0:
					_TransformValues[frequency] += DcaTables.CorrCf[coefficient + 5] * cosine;
					_TransformValues[frequency + 1] += DcaTables.CorrCf[coefficient + 6] * sine;
					_TransformValues[frequency + 2] += DcaTables.CorrCf[coefficient + 7] * -cosine;
					_TransformValues[frequency + 3] += DcaTables.CorrCf[coefficient + 8] * -sine;
					_TransformValues[frequency + 4] += DcaTables.CorrCf[coefficient + 9] * cosine;
					_TransformValues[frequency + 5] += DcaTables.CorrCf[coefficient + 10] * sine;
				}
				tone.Phase[channel] = unchecked((byte)(tone.Phase[channel] + tone.PhaseRotation));
			}
		}

		private void FilterLowFrequencyEffects(Span<float> output)
		{
			var factor = 16 << _FrequencyRange;
			var outputOffset = 0;
			for (var sample = 0; sample < 64; sample++)
			{
				var result = _LfeData[sample];
				for (var interpolation = 0; interpolation < factor; interpolation++)
				{
					for (var filter = 0; filter < 5; filter++)
					{
						var coefficient = filter * 4;
						var temporary = _LfeHistory[filter, 0] * DcaTables.LfeIir[coefficient] +
							_LfeHistory[filter, 1] * DcaTables.LfeIir[coefficient + 1] + result;
						result = _LfeHistory[filter, 0] * DcaTables.LfeIir[coefficient + 2] +
							_LfeHistory[filter, 1] * DcaTables.LfeIir[coefficient + 3] + temporary;
						_LfeHistory[filter, 0] = _LfeHistory[filter, 1];
						_LfeHistory[filter, 1] = temporary;
					}
					output[outputOffset++] = result;
					result = 0;
				}
			}
		}

		private float NextRandom(int subband)
		{
			_LbrRandom = unchecked((int)(1103515245U * (uint)_LbrRandom + 12345U));
			return _LbrRandom * _SubbandScaleFactors[subband];
		}

		private int ParseStereoCode(int minimum)
		{
			var value = (uint)(ParseVlc(DcaTables.StereoGridVlc, 2) + minimum);
			value = (value & 1) != 0 ? 16 + (value >> 1) : 16 - (value >> 1);
			return value >= DcaTables.StCoeff.Length ? 16 : (int)value;
		}

		private int ParseVlc(Vlc vlc, int maximumDepth)
		{
			var value = _Bits.ReadVlc(vlc.Table, vlc.RootBits, maximumDepth);
			return value >= 0 ? value : (int)_Bits.ReadBits((int)_Bits.ReadBits(3) + 1);
		}

		private int EnsureBits(int count)
		{
			var left = _Bits.BitsLeft;
			if (left < 0) return FfmpegError.InvalidData;
			if (left < count)
			{
				_Bits.SkipBits(left);
				return 1;
			}
			return 0;
		}

		private void InitializeByteReader(byte[] data, int offset, int length)
		{
			_Data = data;
			_BytePosition = offset;
			_ByteEnd = offset + length;
		}

		private int BytesLeft => _ByteEnd - _BytePosition;

		private int ReadByte()
		{
			if (_BytePosition >= _ByteEnd) return 0;
			return _Data[_BytePosition++];
		}

		private int ReadBigEndian16()
		{
			var result = ReadByte() << 8;
			return result | ReadByte();
		}

		private int ReadLittleEndian16()
		{
			var result = ReadByte();
			return result | ReadByte() << 8;
		}

		private uint ReadBigEndian32()
		{
			if (BytesLeft >= 4)
			{
				var result = BinaryPrimitives.ReadUInt32BigEndian(_Data.AsSpan(_BytePosition, 4));
				_BytePosition += 4;
				return result;
			}
			return (uint)(ReadByte() << 24 | ReadByte() << 16 | ReadByte() << 8 | ReadByte());
		}

		private void SkipBytes(int count)
		{
			_BytePosition = Math.Min(_BytePosition + count, _ByteEnd);
		}

		private static int CeilingLog2(int value)
		{
			return value <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(value - 1));
		}

		private static float[] CreateCosineTable()
		{
			var result = new float[256];
			for (var index = 0; index < result.Length; index++)
				result[index] = (float)Math.Cos(Math.PI * index / 128);
			return result;
		}

		private static float[][][] CreateTimeSampleBuffers()
		{
			var result = new float[Channels][][];
			for (var channel = 0; channel < Channels; channel++)
			{
				result[channel] = new float[Subbands][];
				for (var subband = 0; subband < Subbands; subband++) result[channel][subband] = new float[TimeBufferSamples];
			}
			return result;
		}

		private static DcaLbrTone[] CreateTones()
		{
			var result = new DcaLbrTone[Tones];
			for (var index = 0; index < result.Length; index++) result[index] = new DcaLbrTone();
			return result;
		}

		private struct DcaLbrChunk
		{
			public int Identifier;
			public readonly int Offset;
			public readonly int Length;

			public DcaLbrChunk(int identifier, int offset, int length)
			{
				Identifier = identifier;
				Offset = offset;
				Length = length;
			}
		}

		/// <summary>
		/// Stores one active LBR tonal component and its per-channel amplitude and phase state.
		/// </summary>
		private sealed class DcaLbrTone
		{
			public byte Frequency;
			public byte FrequencyDelta;
			public byte PhaseRotation;
			public readonly byte[] Amplitude = new byte[Channels];
			public readonly byte[] Phase = new byte[Channels];
		}
	}
}
