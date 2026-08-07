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
	/// Ports FFmpeg's scalar DTS core parser, subband reconstruction, embedded XCH handling, and floating QMF synthesis.
	/// </summary>
	internal sealed class DcaCoreDecoder
	{
		private const int ChannelsMaximum = 7;
		private const int Subbands = 32;
		private const int SubbandsX96 = 64;
		private const int Subframes = 16;
		private const int SubbandSamples = 8;
		private const int PcmBlockSamples = 32;
		private const int AdpcmCoefficients = 4;
		private const int LfeHistory = 8;
		private const int AllocationBitsMaximum = 26;
		private const int SpeakerCount = 32;
		private const int SpeakerCenter = 0;
		private const int SpeakerLeft = 1;
		private const int SpeakerRight = 2;
		private const int SpeakerLeftSurround = 3;
		private const int SpeakerRightSurround = 4;
		private const int SpeakerLfe = 5;
		private const int SpeakerCenterSurround = 6;
		private const int SpeakerLeftSideSurround = 9;
		private const int SpeakerRightSideSurround = 10;
		private const int SpeakerMaskLfe = 1 << SpeakerLfe;
		private const int SpeakerMaskCenterSurround = 1 << SpeakerCenterSurround;
		private const uint XchSyncWord = 0x5a5a5a5a;
		private const uint X96SyncWord = 0x1d95f262;
		private const uint XxchSyncWord = 0x47004a03;
		private const int ExtensionCoreXch = 0x008;
		private const int ExtensionCoreXxch = 0x002;
		private const int ExtensionCoreX96 = 0x004;
		private const int ExtensionExssXbr = 0x020;
		private const int ExtensionExssXxch = 0x040;
		private const int ExtensionExssX96 = 0x080;

		private readonly BitReader _Bits = new BitReader();
		private readonly FfmpegFloatMdct _Mdct32 = new FfmpegFloatMdct(32, true, 1.0f);
		private readonly FfmpegFloatMdct _Mdct64 = new FfmpegFloatMdct(64, true, 1.0f);
		private readonly DcaFixedDct _FixedDct = new DcaFixedDct();
		private readonly byte[] _NumberOfSubbands = new byte[ChannelsMaximum];
		private readonly byte[] _SubbandVectorQuantizationStart = new byte[ChannelsMaximum];
		private readonly byte[] _JointIntensityIndex = new byte[ChannelsMaximum];
		private readonly byte[] _TransitionModeSelection = new byte[ChannelsMaximum];
		private readonly byte[] _ScaleFactorSelection = new byte[ChannelsMaximum];
		private readonly byte[] _BitAllocationSelection = new byte[ChannelsMaximum];
		private readonly byte[,] _QuantIndexSelection = new byte[ChannelsMaximum, 10];
		private readonly int[,] _ScaleFactorAdjustment = new int[ChannelsMaximum, 10];
		private readonly byte[] _NumberOfSubSubframes = new byte[Subframes];
		private readonly byte[,] _PredictionMode = new byte[ChannelsMaximum, SubbandsX96];
		private readonly short[,] _PredictionVectorQuantizationIndex = new short[ChannelsMaximum, SubbandsX96];
		private readonly byte[,] _BitAllocation = new byte[ChannelsMaximum, SubbandsX96];
		private readonly byte[,,] _TransitionMode = new byte[Subframes, ChannelsMaximum, Subbands];
		private readonly int[,,] _ScaleFactors = new int[ChannelsMaximum, Subbands, 2];
		private readonly byte[] _JointScaleSelection = new byte[ChannelsMaximum];
		private readonly int[,] _JointScaleFactors = new int[ChannelsMaximum, SubbandsX96];
		private readonly int[][][] _Subband = CreateSampleCube(ChannelsMaximum, Subbands, 132);
		private readonly int[][][] _X96Subband = CreateSampleCube(ChannelsMaximum, SubbandsX96, 132);
		private readonly int[] _LfeSamples = new int[LfeHistory + 64];
		private readonly float[][] _SynthesisHistory1 = CreateFloatPlanes(ChannelsMaximum, 1024);
		private readonly float[][] _SynthesisHistory2 = CreateFloatPlanes(ChannelsMaximum, 64);
		private readonly int[] _SynthesisOffset = new int[ChannelsMaximum];
		private readonly float[][] _SpeakerOutput = CreateFloatPlanes(SpeakerCount, 8192);
		private readonly int[] _ChannelRemap = new int[SpeakerCount];
		private readonly float[] _SynthesisInput = new float[64];
		private readonly int[][] _FixedSynthesisHistory1 = CreateIntPlanes(ChannelsMaximum, 1024);
		private readonly int[][] _FixedSynthesisHistory2 = CreateIntPlanes(ChannelsMaximum, 64);
		private readonly int[] _FixedSynthesisOffset = new int[ChannelsMaximum];
		private readonly int[][] _FixedSpeakerOutput = CreateIntPlanes(SpeakerCount, 8192);
		private readonly int[] _FixedSynthesisInput = new int[64];
		private byte[] _Input;
		private int _InputOffset;
		private int _InputSize;
		private int _CrcPresent;
		private int _PcmBlocks;
		private int _FrameSize;
		private int _AudioMode;
		private int _SampleRate;
		private int _BitRate;
		private int _DynamicRangePresent;
		private int _TimestampPresent;
		private int _AuxiliaryPresent;
		private int _ExtensionAudioType;
		private int _ExtensionAudioPresent;
		private int _SyncSubSubframes;
		private int _LowFrequencyEffects;
		private int _PredictorHistory;
		private int _FilterPerfect;
		private int _SourcePcmResolution;
		private int _EsFormat;
		private int _SumDifferenceFront;
		private int _SumDifferenceSurround;
		private int _NumberOfSubframes;
		private int _NumberOfChannels;
		private int _ChannelMask;
		private int _ExtensionAudioMask;
		private int _XchPosition;
		private int _XxchPosition;
		private int _X96Position;
		private int _XxchCrcPresent;
		private int _XxchMaskBitCount;
		private int _XxchCoreMask;
		private int _XxchSpeakerMask;
		private int _XxchDownmixEmbedded;
		private int _XxchDownmixScaleInverse;
		private readonly int[] _XxchDownmixMask = new int[2];
		private readonly int[] _XxchDownmixCoefficient = new int[64];
		private int _X96Revision;
		private int _X96Channels;
		private int _X96SubbandStart;
		private int _X96HighResolution;
		private uint _X96Random = 1;
		private float _OutputHistoryLfeFloat;
		private int _OutputHistoryLfeFixed;
		private readonly int[] _XbrFrameSize = new int[4];
		private readonly int[] _XbrChannelCount = new int[4];
		private readonly int[] _XbrSubbandCount = new int[ChannelsMaximum * 4];
		private readonly int[] _XbrAllocationBitCount = new int[ChannelsMaximum];
		private readonly int[,] _XbrBitAllocation = new int[ChannelsMaximum, Subbands];
		private readonly int[] _XbrScaleBitCount = new int[ChannelsMaximum];
		private readonly int[,,] _XbrScaleFactors = new int[ChannelsMaximum, Subbands, 2];
		private readonly int[] _X96FrameSize = new int[4];
		private readonly int[] _X96ChannelCount = new int[4];

		public int FrameSize => _FrameSize;
		public int SampleRate => _SampleRate;
		public int NumberOfChannels => BitOperations.PopCount((uint)_ChannelMask);
		public int NumberOfSamples => _PcmBlocks * PcmBlockSamples;
		public int BitRate => _BitRate > 3 && (_ExtensionAudioMask & 0xff0) == 0 ? _BitRate : 0;
		public int FixedOutputRate { get; private set; }
		public int FixedNumberOfSamples { get; private set; }

		/// <summary>
		/// Parses one normalized big-endian DTS core frame and then applies any embedded XCH channel extension.
		/// </summary>
		public int Parse(byte[] data, int offset, int size)
		{
			if (data == null || offset < 0 || size < 16 || offset > data.Length - size) return FfmpegError.InvalidData;
			_Input = data;
			_InputOffset = offset;
			_InputSize = size;
			_ExtensionAudioMask = 0;
			_XchPosition = _XxchPosition = _X96Position = 0;
			var result = _Bits.Initialize(data, offset, size * 8);
			if (result < 0) return result;
			result = ParseFrameHeader();
			if (result < 0) return result;
			if (_PcmBlocks > 128) return FfmpegError.PatchWelcome;
			PrepareSampleBuffers();
			result = ParseFrameData(HeaderType.Core, 0);
			if (result < 0) return result;
			ParseOptionalInformation();
			if (_FrameSize > size) _FrameSize = size;
			result = SeekBits(_FrameSize * 8);
			if (result < 0) return FfmpegError.InvalidData;
			return ParseEmbeddedChannelExtension();
		}

		/// <summary>
		/// Applies core-related components carried by one parsed EXSS asset after the compatible core frame.
		/// </summary>
		public int ParseExtensionSubstream(byte[] data, int exssOffset, DcaExssAsset asset, bool xllParsed)
		{
			if (asset != null && (asset.ExtensionMask & ExtensionExssXxch) != 0)
			{
				var result = _Bits.Initialize(data, exssOffset + asset.XxchOffset, asset.XxchSize * 8);
				if (result < 0) return result;
				result = ParseXxchFrame();
				if (result < 0) return result;
				_ExtensionAudioMask |= ExtensionExssXxch;
			}
			if (asset != null && (asset.ExtensionMask & ExtensionExssXbr) != 0)
			{
				var result = _Bits.Initialize(data, exssOffset + asset.XbrOffset, asset.XbrSize * 8);
				if (result < 0) return result;
				result = ParseXbrFrame();
				if (result >= 0) _ExtensionAudioMask |= ExtensionExssXbr;
			}
			if (!xllParsed)
			{
				if (asset != null && (asset.ExtensionMask & ExtensionExssX96) != 0)
				{
					var result = _Bits.Initialize(data, exssOffset + asset.X96Offset, asset.X96Size * 8);
					if (result < 0) return result;
					result = ParseX96FrameExtensionSubstream();
					if (result >= 0) _ExtensionAudioMask |= ExtensionExssX96;
				} else if (_X96Position != 0)
				{
					var result = _Bits.Initialize(_Input, _InputOffset, _InputSize * 8);
					if (result < 0) return result;
					_Bits.Seek(_X96Position);
					result = ParseX96Frame();
					if (result >= 0) _ExtensionAudioMask |= ExtensionCoreX96;
				}
			}
			return 0;
		}

		/// <summary>
		/// Synthesizes all decoded subbands into FFmpeg-ordered planar floats while preserving per-channel overlap history.
		/// </summary>
		public int Filter(Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			var x96Synthesis = (_ExtensionAudioMask & (ExtensionCoreX96 | ExtensionExssX96)) != 0;
			var sampleCount = NumberOfSamples << (x96Synthesis ? 1 : 0);
			var channelCount = BuildChannelRemap(_ChannelMask);
			var requiredBytes = checked(sampleCount * channelCount * sizeof(float));
			if (output.Length < requiredBytes || sampleCount > _SpeakerOutput[0].Length) return FfmpegError.InvalidArgument;

			var filter = x96Synthesis ? DcaTables.Fir64bands : (_FilterPerfect != 0 ? DcaTables.Fir32bandsPerfect : DcaTables.Fir32bandsNonperfect);
			for (var channel = 0; channel < _NumberOfChannels; channel++)
			{
				var speaker = MapPrimaryChannelToSpeaker(channel);
				if (speaker < 0) return FfmpegError.InvalidArgument;
				if (x96Synthesis) Synthesize64(channel, _SpeakerOutput[speaker], filter, channel < _X96Channels);
				else Synthesize32(channel, _SpeakerOutput[speaker], filter);
			}
			if (_LowFrequencyEffects != 0)
			{
				if (x96Synthesis)
				{
					SynthesizeLowFrequencyEffects(_SpeakerOutput[SpeakerLfe], sampleCount / 2);
					UpsampleLowFrequencyEffectsX96(_SpeakerOutput[SpeakerLfe], sampleCount / 2);
				} else SynthesizeLowFrequencyEffects(_SpeakerOutput[SpeakerLfe], 0);
			}
			if (_EsFormat != 0 && (_ExtensionAudioMask & ExtensionCoreXch) != 0 && _AudioMode >= 8)
			{
				for (var index = 0; index < sampleCount; index++)
				{
					_SpeakerOutput[SpeakerLeftSurround][index] += _SpeakerOutput[SpeakerCenterSurround][index] * -0.70710677f;
					_SpeakerOutput[SpeakerRightSurround][index] += _SpeakerOutput[SpeakerCenterSurround][index] * -0.70710677f;
				}
			}
			if ((_ExtensionAudioMask & (ExtensionCoreXxch | ExtensionExssXxch)) != 0 && _XxchDownmixEmbedded != 0)
			{
				var coefficientPosition = 0;
				var coreChannels = DcaTables.Channels[_AudioMode];
				for (var channel = coreChannels; channel < _NumberOfChannels; channel++)
				{
					var sourceSpeaker = MapPrimaryChannelToSpeaker(channel);
					if (sourceSpeaker < 0) return FfmpegError.InvalidArgument;
					for (var speaker = 0; speaker < _XxchMaskBitCount; speaker++)
						if ((_XxchDownmixMask[channel - coreChannels] & (1 << speaker)) != 0)
						{
							var coefficient = _XxchDownmixCoefficient[coefficientPosition++];
							if (coefficient != 0)
								for (var index = 0; index < sampleCount; index++) _SpeakerOutput[speaker][index] += _SpeakerOutput[sourceSpeaker][index] * (coefficient * (-1.0f / (1 << 15)));
						}
				}
				var scale = _XxchDownmixScaleInverse * (1.0f / (1 << 16));
				for (var speaker = 0; speaker < _XxchMaskBitCount; speaker++)
					if ((_XxchCoreMask & (1 << speaker)) != 0)
						for (var index = 0; index < sampleCount; index++) _SpeakerOutput[speaker][index] *= scale;
			}
			if ((_ExtensionAudioMask & (ExtensionCoreXxch | ExtensionExssXxch | ExtensionCoreXch)) == 0)
			{
				if ((_SumDifferenceFront != 0 && _AudioMode > 0) || _AudioMode == 3) ApplyButterfly(_SpeakerOutput[SpeakerLeft], _SpeakerOutput[SpeakerRight], sampleCount);
				if (_SumDifferenceSurround != 0 && _AudioMode >= 8) ApplyButterfly(_SpeakerOutput[SpeakerLeftSurround], _SpeakerOutput[SpeakerRightSurround], sampleCount);
			}

			var samples = MemoryMarshal.Cast<byte, float>(output.Slice(0, requiredBytes));
			for (var channel = 0; channel < channelCount; channel++)
				_SpeakerOutput[_ChannelRemap[channel]].AsSpan(0, sampleCount).CopyTo(samples.Slice(channel * sampleCount, sampleCount));
			var planeSize = sampleCount * sizeof(float);
			frame = new AudioFrameInfo(sampleCount, channelCount, AudioSampleFormat.FloatPlanar, channelCount, planeSize, requiredBytes);
			return 0;
		}

		/// <summary>
		/// Runs FFmpeg's integer core synthesis required when XLL residual channels reference the lossy core signal.
		/// </summary>
		public int FilterFixed(int x96Synthesis)
		{
			var x96Channels = 0;
			if (x96Synthesis == 0 && (_ExtensionAudioMask & (ExtensionCoreX96 | ExtensionExssX96)) != 0)
			{
				x96Channels = _X96Channels;
				x96Synthesis = 1;
			}
			if (x96Synthesis < 0) x96Synthesis = 0;
			FixedOutputRate = _SampleRate << x96Synthesis;
			FixedNumberOfSamples = NumberOfSamples << x96Synthesis;
			var filter = x96Synthesis != 0 ? DcaTables.Fir64bandsFixed : (_FilterPerfect != 0 ? DcaTables.Fir32bandsPerfectFixed : DcaTables.Fir32bandsNonperfectFixed);
			for (var channel = 0; channel < _NumberOfChannels; channel++)
			{
				var speaker = MapPrimaryChannelToSpeaker(channel);
				if (speaker < 0) return FfmpegError.InvalidArgument;
				if (x96Synthesis != 0) SynthesizeFixed64(channel, _FixedSpeakerOutput[speaker], filter, channel < x96Channels);
				else SynthesizeFixed32(channel, _FixedSpeakerOutput[speaker], filter);
			}
			if (_LowFrequencyEffects != 0)
			{
				if (_LowFrequencyEffects == 1) return FfmpegError.InvalidArgument;
				if (x96Synthesis != 0)
				{
					SynthesizeLowFrequencyEffectsFixed(_FixedSpeakerOutput[SpeakerLfe], FixedNumberOfSamples / 2);
					UpsampleLowFrequencyEffectsFixedX96(_FixedSpeakerOutput[SpeakerLfe], FixedNumberOfSamples / 2);
				} else SynthesizeLowFrequencyEffectsFixed(_FixedSpeakerOutput[SpeakerLfe], 0);
			}
			return 0;
		}

		public int[] GetFixedSpeakerSamples(int speaker)
		{
			if ((_ChannelMask & (1 << speaker)) != 0) return _FixedSpeakerOutput[speaker];
			if (speaker == SpeakerLeftSideSurround && (_ChannelMask & (1 << SpeakerLeftSurround)) != 0) return _FixedSpeakerOutput[SpeakerLeftSurround];
			if (speaker == SpeakerRightSideSurround && (_ChannelMask & (1 << SpeakerRightSurround)) != 0) return _FixedSpeakerOutput[SpeakerRightSurround];
			return null;
		}

		/// <summary>
		/// Parses the DTS core frame header and validates framing, rate, mode, resolution, and extension flags.
		/// </summary>
		private int ParseFrameHeader()
		{
			if (_Bits.ReadBitsLong(32) != DcaBitstream.CoreBigEndianSyncWord) return FfmpegError.InvalidData;
			var normalFrame = (int)_Bits.ReadBit();
			var deficitSamples = (int)_Bits.ReadBits(5) + 1;
			if (deficitSamples != 32) return normalFrame != 0 ? FfmpegError.InvalidData : FfmpegError.PatchWelcome;
			_CrcPresent = (int)_Bits.ReadBit();
			_PcmBlocks = (int)_Bits.ReadBits(7) + 1;
			if ((_PcmBlocks & 7) != 0) return _PcmBlocks < 6 || normalFrame != 0 ? FfmpegError.InvalidData : FfmpegError.PatchWelcome;
			_FrameSize = (int)_Bits.ReadBits(14) + 1;
			if (_FrameSize < 96) return FfmpegError.InvalidData;
			_AudioMode = (int)_Bits.ReadBits(6);
			if (_AudioMode >= 10) return FfmpegError.PatchWelcome;
			var sampleRateCode = (int)_Bits.ReadBits(4);
			_SampleRate = DcaTables.SampleRates[sampleRateCode];
			if (_SampleRate == 0) return FfmpegError.InvalidData;
			var bitRateCode = (int)_Bits.ReadBits(5);
			if (_Bits.ReadBit() != 0) return FfmpegError.InvalidData;
			_BitRate = unchecked((int)DcaTables.BitRates[bitRateCode]);
			_DynamicRangePresent = (int)_Bits.ReadBit();
			_TimestampPresent = (int)_Bits.ReadBit();
			_AuxiliaryPresent = (int)_Bits.ReadBit();
			_BitsSkip(1);
			_ExtensionAudioType = (int)_Bits.ReadBits(3);
			_ExtensionAudioPresent = (int)_Bits.ReadBit();
			_SyncSubSubframes = (int)_Bits.ReadBit();
			_LowFrequencyEffects = (int)_Bits.ReadBits(2);
			if (_LowFrequencyEffects == 3) return FfmpegError.InvalidData;
			_PredictorHistory = (int)_Bits.ReadBit();
			if (_CrcPresent != 0) _BitsSkip(16);
			_FilterPerfect = (int)_Bits.ReadBit();
			_BitsSkip(6);
			var resolutionCode = (int)_Bits.ReadBits(3);
			_SourcePcmResolution = DcaTables.BitsPerSample[resolutionCode];
			if (_SourcePcmResolution == 0) return FfmpegError.InvalidData;
			_EsFormat = resolutionCode & 1;
			_SumDifferenceFront = (int)_Bits.ReadBit();
			_SumDifferenceSurround = (int)_Bits.ReadBit();
			_BitsSkip(4);
			return 0;
		}

		/// <summary>
		/// Parses the core or XCH coding header in FFmpeg field order, retaining codebook choices for all following subframes.
		/// </summary>
		private int ParseCodingHeader(HeaderType header, int channelBase)
		{
			if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
			var headerPosition = _Bits.Position;
			var headerSize = 0;
			if (header == HeaderType.Core)
			{
				_NumberOfSubframes = (int)_Bits.ReadBits(4) + 1;
				_NumberOfChannels = (int)_Bits.ReadBits(3) + 1;
				if (_NumberOfChannels != DcaTables.Channels[_AudioMode]) return FfmpegError.InvalidData;
				_ChannelMask = DcaTables.AudioModeChannelMasks[_AudioMode];
				if (_LowFrequencyEffects != 0) _ChannelMask |= SpeakerMaskLfe;
			} else if (header == HeaderType.Xch)
			{
				_NumberOfChannels = DcaTables.Channels[_AudioMode] + 1;
				_ChannelMask |= SpeakerMaskCenterSurround;
			} else if (header == HeaderType.Xxch)
			{
				headerSize = (int)_Bits.ReadBits(7) + 1;
				var extensionChannels = (int)_Bits.ReadBits(3) + 1;
				if (extensionChannels > 2) return FfmpegError.PatchWelcome;
				_NumberOfChannels = DcaTables.Channels[_AudioMode] + extensionChannels;
				var mask = (int)_Bits.ReadBitsLong(_XxchMaskBitCount - SpeakerCenterSurround);
				_XxchSpeakerMask = mask << SpeakerCenterSurround;
				if (BitOperations.PopCount((uint)_XxchSpeakerMask) != extensionChannels || (_XxchCoreMask & _XxchSpeakerMask) != 0) return FfmpegError.InvalidData;
				_ChannelMask = _XxchCoreMask | _XxchSpeakerMask;
				if (_Bits.ReadBit() != 0)
				{
					_XxchDownmixEmbedded = (int)_Bits.ReadBit();
					var inverseIndex = (int)_Bits.ReadBits(6) * 4 - 44;
					if ((uint)inverseIndex >= (uint)DcaTables.InvDmixtable.Length) return FfmpegError.InvalidData;
					_XxchDownmixScaleInverse = unchecked((int)DcaTables.InvDmixtable[inverseIndex]);
					for (var channel = 0; channel < extensionChannels; channel++)
					{
						mask = (int)_Bits.ReadBitsLong(_XxchMaskBitCount);
						if ((mask & _XxchCoreMask) != mask) return FfmpegError.InvalidData;
						_XxchDownmixMask[channel] = mask;
					}
					var coefficientPosition = 0;
					for (var channel = 0; channel < extensionChannels; channel++)
						for (var speaker = 0; speaker < _XxchMaskBitCount; speaker++)
							if ((_XxchDownmixMask[channel] & (1 << speaker)) != 0)
							{
								var code = (int)_Bits.ReadBits(7);
								var sign = (code >> 6) - 1;
								code &= 63;
								var coefficientIndex = code != 0 ? code * 4 - 3 : 0;
								if ((uint)coefficientIndex >= (uint)DcaTables.Dmixtable.Length) return FfmpegError.InvalidData;
								var coefficient = code != 0 ? DcaTables.Dmixtable[coefficientIndex] : 0;
								_XxchDownmixCoefficient[coefficientPosition++] = (coefficient ^ sign) - sign;
							}
				} else _XxchDownmixEmbedded = 0;
			} else return FfmpegError.PatchWelcome;

			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				_NumberOfSubbands[channel] = (byte)(_Bits.ReadBits(5) + 2);
				if (_NumberOfSubbands[channel] > Subbands) return FfmpegError.InvalidData;
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++) _SubbandVectorQuantizationStart[channel] = (byte)(_Bits.ReadBits(5) + 1);
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				var value = (int)_Bits.ReadBits(3);
				if (value != 0 && header == HeaderType.Xxch) value += channelBase - 1;
				if (value > _NumberOfChannels) return FfmpegError.InvalidData;
				_JointIntensityIndex[channel] = (byte)value;
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++) _TransitionModeSelection[channel] = (byte)_Bits.ReadBits(2);
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				_ScaleFactorSelection[channel] = (byte)_Bits.ReadBits(3);
				if (_ScaleFactorSelection[channel] == 7) return FfmpegError.InvalidData;
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				_BitAllocationSelection[channel] = (byte)_Bits.ReadBits(3);
				if (_BitAllocationSelection[channel] == 7) return FfmpegError.InvalidData;
			}
			for (var codebook = 0; codebook < 10; codebook++)
				for (var channel = channelBase; channel < _NumberOfChannels; channel++)
					_QuantIndexSelection[channel, codebook] = (byte)_Bits.ReadBits(DcaTables.QuantIndexSelNbits[codebook]);
			for (var codebook = 0; codebook < 10; codebook++)
				for (var channel = channelBase; channel < _NumberOfChannels; channel++)
					if (_QuantIndexSelection[channel, codebook] < DcaTables.QuantIndexGroupSize[codebook])
						_ScaleFactorAdjustment[channel, codebook] = unchecked((int)DcaTables.ScaleFactorAdj[_Bits.ReadBits(2)]);
			if (header == HeaderType.Xxch) return SeekBits(headerPosition + headerSize * 8);
			if (_CrcPresent != 0) _BitsSkip(16);
			return 0;
		}

		private int ParseScale(ref int scaleIndex, int selection)
		{
			var table = selection > 5 ? DcaTables.ScaleFactorQuant7 : DcaTables.ScaleFactorQuant6;
			if (selection < 5) scaleIndex += ReadVlc(DcaTables.ScaleFactorVlc[selection], 2);
			else scaleIndex = (int)_Bits.ReadBits(selection + 1);
			if ((uint)scaleIndex >= (uint)table.Length) return FfmpegError.InvalidData;
			return unchecked((int)table[scaleIndex]);
		}

		private int ParseJointScale(int selection)
		{
			var scaleIndex = selection < 5 ? ReadVlc(DcaTables.ScaleFactorVlc[selection], 2) : (int)_Bits.ReadBits(selection + 1);
			scaleIndex += 64;
			return (uint)scaleIndex < (uint)DcaTables.JointScaleFactors.Length ? unchecked((int)DcaTables.JointScaleFactors[scaleIndex]) : FfmpegError.InvalidData;
		}

		/// <summary>
		/// Decodes one subframe header, including prediction flags, allocations, transition modes, scales, and joint-intensity scales.
		/// </summary>
		private int ParseSubframeHeader(int subframe, HeaderType header, int channelBase)
		{
			if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
			if (header == HeaderType.Core)
			{
				_NumberOfSubSubframes[subframe] = (byte)(_Bits.ReadBits(2) + 1);
				_BitsSkip(3);
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
				for (var band = 0; band < _NumberOfSubbands[channel]; band++) _PredictionMode[channel, band] = (byte)_Bits.ReadBit();
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
				for (var band = 0; band < _NumberOfSubbands[channel]; band++)
					if (_PredictionMode[channel, band] != 0) _PredictionVectorQuantizationIndex[channel, band] = (short)_Bits.ReadBits(12);
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				var selection = _BitAllocationSelection[channel];
				for (var band = 0; band < _SubbandVectorQuantizationStart[channel]; band++)
				{
					var allocation = selection < 5 ? ReadVlc(DcaTables.BitAllocationVlc[selection], 2) : (int)_Bits.ReadBits(selection - 1);
					if (allocation > AllocationBitsMaximum) return FfmpegError.InvalidData;
					_BitAllocation[channel, band] = (byte)allocation;
				}
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				for (var band = 0; band < Subbands; band++) _TransitionMode[subframe, channel, band] = 0;
				if (_NumberOfSubSubframes[subframe] > 1)
				{
					var selection = _TransitionModeSelection[channel];
					for (var band = 0; band < _SubbandVectorQuantizationStart[channel]; band++)
						if (_BitAllocation[channel, band] != 0) _TransitionMode[subframe, channel, band] = (byte)ReadVlc(DcaTables.TransitionModeVlc[selection], 1);
				}
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				var selection = _ScaleFactorSelection[channel];
				var scaleIndex = 0;
				for (var band = 0; band < _SubbandVectorQuantizationStart[channel]; band++)
				{
					if (_BitAllocation[channel, band] != 0)
					{
						var value = ParseScale(ref scaleIndex, selection);
						if (value < 0) return value;
						_ScaleFactors[channel, band, 0] = value;
						if (_TransitionMode[subframe, channel, band] != 0)
						{
							value = ParseScale(ref scaleIndex, selection);
							if (value < 0) return value;
							_ScaleFactors[channel, band, 1] = value;
						}
					} else _ScaleFactors[channel, band, 0] = 0;
				}
				for (var band = _SubbandVectorQuantizationStart[channel]; band < _NumberOfSubbands[channel]; band++)
				{
					var value = ParseScale(ref scaleIndex, selection);
					if (value < 0) return value;
					_ScaleFactors[channel, band, 0] = value;
				}
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				if (_JointIntensityIndex[channel] == 0) continue;
				_JointScaleSelection[channel] = (byte)_Bits.ReadBits(3);
				if (_JointScaleSelection[channel] == 7) return FfmpegError.InvalidData;
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				var sourceChannel = _JointIntensityIndex[channel] - 1;
				if (sourceChannel < 0) continue;
				for (var band = _NumberOfSubbands[channel]; band < _NumberOfSubbands[sourceChannel]; band++)
				{
					var value = ParseJointScale(_JointScaleSelection[channel]);
					if (value < 0) return value;
					_JointScaleFactors[channel, band] = value;
				}
			}
			if (_DynamicRangePresent != 0 && header == HeaderType.Core) _BitsSkip(8);
			if (_CrcPresent != 0) _BitsSkip(16);
			return 0;
		}

		private int ParseBlockCodes(Span<int> audio, int allocation)
		{
			var code1 = (int)_Bits.ReadBits(DcaTables.BlockCodeBits[allocation - 1]);
			var code2 = (int)_Bits.ReadBits(DcaTables.BlockCodeBits[allocation - 1]);
			var levels = unchecked((int)DcaTables.QuantLevels[allocation]);
			var offset = (levels - 1) / 2;
			var index = 0;
			for (; index < SubbandSamples / 2; index++)
			{
				var quotient = code1 / levels;
				audio[index] = code1 - quotient * levels - offset;
				code1 = quotient;
			}
			for (; index < SubbandSamples; index++)
			{
				var quotient = code2 / levels;
				audio[index] = code2 - quotient * levels - offset;
				code2 = quotient;
			}
			return (code1 | code2) != 0 ? FfmpegError.InvalidData : 0;
		}

		private int ExtractAudio(Span<int> audio, int allocation, int channel)
		{
			if (allocation == 0)
			{
				audio.Slice(0, SubbandSamples).Clear();
				return 0;
			}
			if (allocation <= 10)
			{
				var selection = _QuantIndexSelection[channel, allocation - 1];
				if (selection < DcaTables.QuantIndexGroupSize[allocation - 1])
				{
					for (var index = 0; index < SubbandSamples; index++) audio[index] = ReadVlc(DcaTables.QuantIndexVlc[allocation - 1][selection], 2);
					return 1;
				}
				if (allocation <= 7) return ParseBlockCodes(audio, allocation);
			}
			for (var index = 0; index < SubbandSamples; index++) audio[index] = _Bits.ReadSignedBits(allocation - 3);
			return 0;
		}

		/// <summary>
		/// Parses all coded subband and LFE samples for one subframe and applies inverse ADPCM and joint-intensity prediction.
		/// </summary>
		private int ParseSubframeAudio(int subframe, HeaderType header, int channelBase, ref int subbandPosition, ref int lfePosition)
		{
			Span<int> audio = stackalloc int[16];
			Span<int> vectorIndices = stackalloc int[Subbands];
			var numberOfSamples = _NumberOfSubSubframes[subframe] * SubbandSamples;
			if (subbandPosition + numberOfSamples > _PcmBlocks || _Bits.BitsLeft < 0) return FfmpegError.InvalidData;
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				for (var band = _SubbandVectorQuantizationStart[channel]; band < _NumberOfSubbands[channel]; band++) vectorIndices[band] = (int)_Bits.ReadBits(10);
				for (var band = _SubbandVectorQuantizationStart[channel]; band < _NumberOfSubbands[channel]; band++)
				{
					var coefficientOffset = vectorIndices[band] * Subbands;
					var scale = _ScaleFactors[channel, band, 0];
					var samples = _Subband[channel][band];
					for (var index = 0; index < numberOfSamples; index++)
						samples[AdpcmCoefficients + subbandPosition + index] = DcaMath.Clip23((DcaTables.HighFreqVq[coefficientOffset + index] * scale + 8) >> 4);
				}
			}
			if (_LowFrequencyEffects != 0 && header == HeaderType.Core)
			{
				var numberOfLfeSamples = 2 * _LowFrequencyEffects * _NumberOfSubSubframes[subframe];
				for (var index = 0; index < numberOfLfeSamples; index++) audio[index] = _Bits.ReadSignedBits(8);
				var scaleIndex = (int)_Bits.ReadBits(8);
				if (scaleIndex >= DcaTables.ScaleFactorQuant7.Length) return FfmpegError.InvalidData;
				var scale = DcaMath.Multiply(4697620, unchecked((int)DcaTables.ScaleFactorQuant7[scaleIndex]), 23);
				for (var index = 0; index < numberOfLfeSamples; index++) _LfeSamples[lfePosition++] = DcaMath.Clip23(unchecked(audio[index] * scale) >> 4);
			}

			var outputOffset = subbandPosition;
			for (var subSubframe = 0; subSubframe < _NumberOfSubSubframes[subframe]; subSubframe++)
			{
				for (var channel = channelBase; channel < _NumberOfChannels; channel++)
				{
					if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
					for (var band = 0; band < _SubbandVectorQuantizationStart[channel]; band++)
					{
						var allocation = _BitAllocation[channel, band];
						var result = ExtractAudio(audio, allocation, channel);
						if (result < 0) return result;
						var stepSize = unchecked((int)(_BitRate == 3 ? DcaTables.LosslessQuant[allocation] : DcaTables.LossyQuant[allocation]));
						var transition = _TransitionMode[subframe, channel, band];
						var scale = transition == 0 || subSubframe < transition ? _ScaleFactors[channel, band, 0] : _ScaleFactors[channel, band, 1];
						if (result > 0) scale = DcaMath.Clip23(unchecked((int)((long)_ScaleFactorAdjustment[channel, allocation - 1] * scale >> 22)));
						Dequantize(_Subband[channel][band], AdpcmCoefficients + outputOffset, audio, stepSize, scale, false);
					}
				}
				if ((subSubframe == _NumberOfSubSubframes[subframe] - 1 || _SyncSubSubframes != 0) && _Bits.ReadBits(16) != 0xffff) return FfmpegError.InvalidData;
				outputOffset += SubbandSamples;
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
				InverseAdpcm(_Subband[channel], channel, 0, _NumberOfSubbands[channel], subbandPosition, numberOfSamples);
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				var sourceChannel = _JointIntensityIndex[channel] - 1;
				if (sourceChannel < 0) continue;
				for (var band = _NumberOfSubbands[channel]; band < _NumberOfSubbands[sourceChannel]; band++)
				{
					var scale = _JointScaleFactors[channel, band];
					for (var index = 0; index < numberOfSamples; index++)
						_Subband[channel][band][AdpcmCoefficients + subbandPosition + index] = DcaMath.Clip23(DcaMath.Multiply(_Subband[sourceChannel][band][AdpcmCoefficients + subbandPosition + index], scale, 17));
				}
			}
			subbandPosition = outputOffset;
			return 0;
		}

		private int ParseFrameData(HeaderType header, int channelBase)
		{
			var result = ParseCodingHeader(header, channelBase);
			if (result < 0) return result;
			var subbandPosition = 0;
			var lfePosition = LfeHistory;
			for (var subframe = 0; subframe < _NumberOfSubframes; subframe++)
			{
				result = ParseSubframeHeader(subframe, header, channelBase);
				if (result < 0) return result;
				result = ParseSubframeAudio(subframe, header, channelBase, ref subbandPosition, ref lfePosition);
				if (result < 0) return result;
			}
			for (var channel = channelBase; channel < _NumberOfChannels; channel++)
			{
				var numberOfSubbands = _NumberOfSubbands[channel];
				if (_JointIntensityIndex[channel] != 0) numberOfSubbands = Math.Max(numberOfSubbands, _NumberOfSubbands[_JointIntensityIndex[channel] - 1]);
				for (var band = 0; band < numberOfSubbands; band++)
				{
					var samples = _Subband[channel][band];
					for (var index = 0; index < AdpcmCoefficients; index++) samples[index] = samples[_PcmBlocks + index];
				}
				for (var band = numberOfSubbands; band < Subbands; band++) Array.Clear(_Subband[channel][band], 0, AdpcmCoefficients + _PcmBlocks);
			}
			return 0;
		}

		private void ParseOptionalInformation()
		{
			if (_TimestampPresent != 0) _BitsSkip(32);
			if (_AuxiliaryPresent != 0) { }
			if (_ExtensionAudioPresent == 0) return;
			var syncPosition = Math.Min(_FrameSize / 4, _InputSize / 4) - 1;
			var lastPosition = _Bits.Position / 32;
			uint nextWord = 0;
			for (; syncPosition >= lastPosition; syncPosition--)
			{
				var word = ReadWord(syncPosition * 4);
				if (_ExtensionAudioType == 0 && word == XchSyncWord)
				{
					var size = (int)(nextWord >> 22) + 1;
					var distance = _FrameSize - syncPosition * 4;
					if (size >= 96 && (size == distance || size - 1 == distance) && ((nextWord >> 15) & 0x7f) == 8) { _XchPosition = syncPosition * 32 + 49; break; }
				} else if (_ExtensionAudioType == 2 && word == X96SyncWord)
				{
					var size = (int)(nextWord >> 20) + 1;
					if (size >= 96 && size == _FrameSize - syncPosition * 4) { _X96Position = syncPosition * 32 + 44; break; }
				} else if (_ExtensionAudioType == 6 && word == XxchSyncWord) { _XxchPosition = syncPosition * 32; break; }
				nextWord = word;
			}
		}

		private int ParseEmbeddedChannelExtension()
		{
			var result = 0;
			var extension = 0;
			if (_XxchPosition != 0)
			{
				_Bits.Seek(_XxchPosition);
				result = ParseXxchFrame();
				extension = ExtensionCoreXxch;
			} else if (_XchPosition != 0)
			{
				if ((_ChannelMask & SpeakerMaskCenterSurround) != 0) return FfmpegError.InvalidData;
				_Bits.Seek(_XchPosition);
				result = ParseFrameData(HeaderType.Xch, _NumberOfChannels);
				extension = ExtensionCoreXch;
			}
			if (result < 0)
			{
				_NumberOfChannels = DcaTables.Channels[_AudioMode];
				_ChannelMask = DcaTables.AudioModeChannelMasks[_AudioMode] | (_LowFrequencyEffects != 0 ? SpeakerMaskLfe : 0);
				return 0;
			}
			_ExtensionAudioMask |= extension;
			return 0;
		}

		private int ParseXxchFrame()
		{
			var headerPosition = _Bits.Position;
			if (_Bits.ReadBitsLong(32) != XxchSyncWord) return FfmpegError.InvalidData;
			var headerSize = (int)_Bits.ReadBits(6) + 1;
			_XxchCrcPresent = (int)_Bits.ReadBit();
			_XxchMaskBitCount = (int)_Bits.ReadBits(5) + 1;
			if (_XxchMaskBitCount <= SpeakerCenterSurround) return FfmpegError.InvalidData;
			var channelSets = (int)_Bits.ReadBits(2) + 1;
			if (channelSets > 1) return FfmpegError.PatchWelcome;
			var frameSize = (int)_Bits.ReadBits(14) + 1;
			_XxchCoreMask = (int)_Bits.ReadBitsLong(_XxchMaskBitCount);
			var mask = _ChannelMask;
			if ((mask & (1 << SpeakerLeftSurround)) != 0 && (_XxchCoreMask & (1 << SpeakerLeftSideSurround)) != 0) mask = mask & ~(1 << SpeakerLeftSurround) | 1 << SpeakerLeftSideSurround;
			if ((mask & (1 << SpeakerRightSurround)) != 0 && (_XxchCoreMask & (1 << SpeakerRightSideSurround)) != 0) mask = mask & ~(1 << SpeakerRightSurround) | 1 << SpeakerRightSideSurround;
			if (mask != _XxchCoreMask) return FfmpegError.InvalidData;
			var result = SeekBits(headerPosition + headerSize * 8);
			if (result < 0) return result;
			result = ParseFrameData(HeaderType.Xxch, _NumberOfChannels);
			if (result < 0) return result;
			return SeekBits(headerPosition + headerSize * 8 + frameSize * 8);
		}

		private int ParseX96Frame()
		{
			_X96Revision = (int)_Bits.ReadBits(4);
			if (_X96Revision < 1 || _X96Revision > 8) return FfmpegError.InvalidData;
			_X96Channels = _NumberOfChannels;
			var result = ParseX96FrameData(false, 0);
			if (result < 0) return result;
			return SeekBits(_FrameSize * 8);
		}

		private int ParseX96FrameExtensionSubstream()
		{
			var headerPosition = _Bits.Position;
			if (_Bits.ReadBitsLong(32) != X96SyncWord) return FfmpegError.InvalidData;
			var headerSize = (int)_Bits.ReadBits(6) + 1;
			_X96Revision = (int)_Bits.ReadBits(4);
			if (_X96Revision < 1 || _X96Revision > 8) return FfmpegError.InvalidData;
			_Bits.ReadBit();
			var channelSets = (int)_Bits.ReadBits(2) + 1;
			for (var channelSet = 0; channelSet < channelSets; channelSet++) _X96FrameSize[channelSet] = (int)_Bits.ReadBits(12) + 1;
			for (var channelSet = 0; channelSet < channelSets; channelSet++) _X96ChannelCount[channelSet] = (int)_Bits.ReadBits(3) + 1;
			var result = SeekBits(headerPosition + headerSize * 8);
			if (result < 0) return result;
			_X96Channels = 0;
			var channelBase = 0;
			for (var channelSet = 0; channelSet < channelSets; channelSet++)
			{
				headerPosition = _Bits.Position;
				if (channelBase + _X96ChannelCount[channelSet] <= _NumberOfChannels)
				{
					_X96Channels = channelBase + _X96ChannelCount[channelSet];
					result = ParseX96FrameData(true, channelBase);
					if (result < 0) return result;
				}
				channelBase += _X96ChannelCount[channelSet];
				result = SeekBits(headerPosition + _X96FrameSize[channelSet] * 8);
				if (result < 0) return result;
			}
			return 0;
		}

		private int ParseXbrFrame()
		{
			var headerPosition = _Bits.Position;
			if (_Bits.ReadBitsLong(32) != 0x655e315e) return FfmpegError.InvalidData;
			var headerSize = (int)_Bits.ReadBits(6) + 1;
			var channelSets = (int)_Bits.ReadBits(2) + 1;
			for (var channelSet = 0; channelSet < channelSets; channelSet++) _XbrFrameSize[channelSet] = (int)_Bits.ReadBits(14) + 1;
			var transitionMode = (int)_Bits.ReadBit();
			var subbandPosition = 0;
			for (var channelSet = 0; channelSet < channelSets; channelSet++)
			{
				_XbrChannelCount[channelSet] = (int)_Bits.ReadBits(3) + 1;
				var bandBitCount = (int)_Bits.ReadBits(2) + 5;
				for (var channel = 0; channel < _XbrChannelCount[channelSet]; channel++)
				{
					_XbrSubbandCount[subbandPosition] = (int)_Bits.ReadBits(bandBitCount) + 1;
					if (_XbrSubbandCount[subbandPosition++] > Subbands) return FfmpegError.InvalidData;
				}
			}
			var result = SeekBits(headerPosition + headerSize * 8);
			if (result < 0) return result;
			var channelBase = 0;
			for (var channelSet = 0; channelSet < channelSets; channelSet++)
			{
				headerPosition = _Bits.Position;
				if (channelBase + _XbrChannelCount[channelSet] <= _NumberOfChannels)
				{
					var samplePosition = 0;
					for (var subframe = 0; subframe < _NumberOfSubframes; subframe++)
					{
						result = ParseXbrSubframe(channelBase, channelBase + _XbrChannelCount[channelSet], transitionMode, subframe, ref samplePosition);
						if (result < 0) return result;
					}
				}
				channelBase += _XbrChannelCount[channelSet];
				result = SeekBits(headerPosition + _XbrFrameSize[channelSet] * 8);
				if (result < 0) return result;
			}
			return 0;
		}

		/// <summary>
		/// Adds one XBR residual subframe to the already reconstructed core subbands in FFmpeg field and loop order.
		/// </summary>
		private int ParseXbrSubframe(int channelBase, int channelEnd, int transitionMode, int subframe, ref int subbandPosition)
		{
			Span<int> audio = stackalloc int[SubbandSamples];
			if (subbandPosition + _NumberOfSubSubframes[subframe] * SubbandSamples > _PcmBlocks || _Bits.BitsLeft < 0) return FfmpegError.InvalidData;
			for (var channel = channelBase; channel < channelEnd; channel++) _XbrAllocationBitCount[channel] = (int)_Bits.ReadBits(2) + 2;
			for (var channel = channelBase; channel < channelEnd; channel++)
				for (var band = 0; band < _XbrSubbandCount[channel]; band++)
				{
					_XbrBitAllocation[channel, band] = (int)_Bits.ReadBits(_XbrAllocationBitCount[channel]);
					if (_XbrBitAllocation[channel, band] > AllocationBitsMaximum) return FfmpegError.InvalidData;
				}
			for (var channel = channelBase; channel < channelEnd; channel++)
			{
				_XbrScaleBitCount[channel] = (int)_Bits.ReadBits(3);
				if (_XbrScaleBitCount[channel] == 0) return FfmpegError.InvalidData;
			}
			for (var channel = channelBase; channel < channelEnd; channel++)
			{
				var table = _ScaleFactorSelection[channel] > 5 ? DcaTables.ScaleFactorQuant7 : DcaTables.ScaleFactorQuant6;
				for (var band = 0; band < _XbrSubbandCount[channel]; band++)
					if (_XbrBitAllocation[channel, band] != 0)
					{
						var scaleIndex = (int)_Bits.ReadBits(_XbrScaleBitCount[channel]);
						if ((uint)scaleIndex >= (uint)table.Length) return FfmpegError.InvalidData;
						_XbrScaleFactors[channel, band, 0] = unchecked((int)table[scaleIndex]);
						if (transitionMode != 0 && _TransitionMode[subframe, channel, band] != 0)
						{
							scaleIndex = (int)_Bits.ReadBits(_XbrScaleBitCount[channel]);
							if ((uint)scaleIndex >= (uint)table.Length) return FfmpegError.InvalidData;
							_XbrScaleFactors[channel, band, 1] = unchecked((int)table[scaleIndex]);
						}
					}
			}
			var outputOffset = subbandPosition;
			for (var subSubframe = 0; subSubframe < _NumberOfSubSubframes[subframe]; subSubframe++)
			{
				for (var channel = channelBase; channel < channelEnd; channel++)
				{
					if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
					for (var band = 0; band < _XbrSubbandCount[channel]; band++)
					{
						var allocation = _XbrBitAllocation[channel, band];
						if (allocation == 0) continue;
						if (allocation > 7)
							for (var index = 0; index < SubbandSamples; index++) audio[index] = _Bits.ReadSignedBits(allocation - 3);
						else
						{
							var result = ParseBlockCodes(audio, allocation);
							if (result < 0) return result;
						}
						var transition = transitionMode != 0 ? _TransitionMode[subframe, channel, band] : 0;
						var scale = transition == 0 || subSubframe < transition ? _XbrScaleFactors[channel, band, 0] : _XbrScaleFactors[channel, band, 1];
						Dequantize(_Subband[channel][band], AdpcmCoefficients + outputOffset, audio, unchecked((int)DcaTables.LosslessQuant[allocation]), scale, true);
					}
				}
				if ((subSubframe == _NumberOfSubSubframes[subframe] - 1 || _SyncSubSubframes != 0) && _Bits.ReadBits(16) != 0xffff) return FfmpegError.InvalidData;
				outputOffset += SubbandSamples;
			}
			subbandPosition = outputOffset;
			return 0;
		}

		private int ParseX96FrameData(bool extensionSubstream, int channelBase)
		{
			var result = ParseX96CodingHeader(extensionSubstream, channelBase);
			if (result < 0) return result;
			var subbandPosition = 0;
			for (var subframe = 0; subframe < _NumberOfSubframes; subframe++)
			{
				result = ParseX96SubframeHeader(channelBase);
				if (result < 0) return result;
				result = ParseX96SubframeAudio(subframe, channelBase, ref subbandPosition);
				if (result < 0) return result;
			}
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				var numberOfSubbands = _NumberOfSubbands[channel];
				if (_JointIntensityIndex[channel] != 0) numberOfSubbands = Math.Max(numberOfSubbands, _NumberOfSubbands[_JointIntensityIndex[channel] - 1]);
				for (var band = 0; band < SubbandsX96; band++)
				{
					var samples = _X96Subband[channel][band];
					if (band >= _X96SubbandStart && band < numberOfSubbands)
						for (var index = 0; index < AdpcmCoefficients; index++) samples[index] = samples[_PcmBlocks + index];
					else Array.Clear(samples, 0, AdpcmCoefficients + _PcmBlocks);
				}
			}
			return 0;
		}

		private int ParseX96CodingHeader(bool extensionSubstream, int channelBase)
		{
			var headerPosition = _Bits.Position;
			var headerSize = extensionSubstream ? (int)_Bits.ReadBits(7) + 1 : 0;
			_X96HighResolution = (int)_Bits.ReadBit();
			if (_X96Revision < 8)
			{
				_X96SubbandStart = (int)_Bits.ReadBits(5);
				if (_X96SubbandStart > 27) return FfmpegError.InvalidData;
			} else _X96SubbandStart = Subbands;
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				_NumberOfSubbands[channel] = (byte)(_Bits.ReadBits(6) + 1);
				if (_NumberOfSubbands[channel] < Subbands) return FfmpegError.InvalidData;
			}
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				var value = (int)_Bits.ReadBits(3);
				if (value != 0 && channelBase != 0) value += channelBase - 1;
				if (value > _X96Channels) return FfmpegError.InvalidData;
				_JointIntensityIndex[channel] = (byte)value;
			}
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				_ScaleFactorSelection[channel] = (byte)_Bits.ReadBits(3);
				if (_ScaleFactorSelection[channel] >= 6) return FfmpegError.InvalidData;
			}
			for (var channel = channelBase; channel < _X96Channels; channel++) _BitAllocationSelection[channel] = (byte)_Bits.ReadBits(3);
			for (var codebook = 0; codebook < 6 + 4 * _X96HighResolution; codebook++)
				for (var channel = channelBase; channel < _X96Channels; channel++) _QuantIndexSelection[channel, codebook] = (byte)_Bits.ReadBits(DcaTables.QuantIndexSelNbits[codebook]);
			if (extensionSubstream) return SeekBits(headerPosition + headerSize * 8);
			if (_CrcPresent != 0) _BitsSkip(16);
			return 0;
		}

		/// <summary>
		/// Parses X96 subband counts, prediction flags, allocation books, scales, and joint-intensity state.
		/// </summary>
		private int ParseX96SubframeHeader(int channelBase)
		{
			if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
			for (var channel = channelBase; channel < _X96Channels; channel++)
				for (var band = _X96SubbandStart; band < _NumberOfSubbands[channel]; band++) _PredictionMode[channel, band] = (byte)_Bits.ReadBit();
			for (var channel = channelBase; channel < _X96Channels; channel++)
				for (var band = _X96SubbandStart; band < _NumberOfSubbands[channel]; band++)
					if (_PredictionMode[channel, band] != 0) _PredictionVectorQuantizationIndex[channel, band] = (short)_Bits.ReadBits(12);
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				var selection = _BitAllocationSelection[channel];
				var allocation = 0;
				for (var band = _X96SubbandStart; band < _NumberOfSubbands[channel]; band++)
				{
					if (selection < 7) allocation += ReadVlc(DcaTables.QuantIndexVlc[5 + 2 * _X96HighResolution][selection], 2);
					else allocation = (int)_Bits.ReadBits(3 + _X96HighResolution);
					if (allocation < 0 || allocation > 7 + 8 * _X96HighResolution) return FfmpegError.InvalidData;
					_BitAllocation[channel, band] = (byte)allocation;
				}
			}
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				var scaleIndex = 0;
				for (var band = _X96SubbandStart; band < _NumberOfSubbands[channel]; band++)
				{
					var scale = ParseScale(ref scaleIndex, _ScaleFactorSelection[channel]);
					if (scale < 0) return scale;
					_ScaleFactors[channel, band >> 1, band & 1] = scale;
				}
			}
			for (var channel = channelBase; channel < _X96Channels; channel++)
				if (_JointIntensityIndex[channel] != 0)
				{
					_JointScaleSelection[channel] = (byte)_Bits.ReadBits(3);
					if (_JointScaleSelection[channel] == 7) return FfmpegError.InvalidData;
				}
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				var sourceChannel = _JointIntensityIndex[channel] - 1;
				if (sourceChannel < 0) continue;
				for (var band = _NumberOfSubbands[channel]; band < _NumberOfSubbands[sourceChannel]; band++)
				{
					var scale = ParseJointScale(_JointScaleSelection[channel]);
					if (scale < 0) return scale;
					_JointScaleFactors[channel, band] = scale;
				}
			}
			if (_CrcPresent != 0) _BitsSkip(16);
			return 0;
		}

		/// <summary>
		/// Decodes X96 high-frequency noise, vector, and scalar subbands while retaining FFmpeg's sub-subframe order.
		/// </summary>
		private int ParseX96SubframeAudio(int subframe, int channelBase, ref int subbandPosition)
		{
			Span<int> audio = stackalloc int[SubbandSamples];
			var numberOfSamples = _NumberOfSubSubframes[subframe] * SubbandSamples;
			if (subbandPosition + numberOfSamples > _PcmBlocks || _Bits.BitsLeft < 0) return FfmpegError.InvalidData;
			for (var channel = channelBase; channel < _X96Channels; channel++)
				for (var band = _X96SubbandStart; band < _NumberOfSubbands[channel]; band++)
				{
					var samples = _X96Subband[channel][band];
					var scale = _ScaleFactors[channel, band >> 1, band & 1];
					if (_BitAllocation[channel, band] == 0)
					{
						if (scale <= 1) Array.Clear(samples, AdpcmCoefficients + subbandPosition, numberOfSamples);
						else for (var index = 0; index < numberOfSamples; index++) samples[AdpcmCoefficients + subbandPosition + index] = DcaMath.Multiply(RandomX96(), scale, 31);
					} else if (_BitAllocation[channel, band] == 1)
					{
						var destination = AdpcmCoefficients + subbandPosition;
						for (var vector = 0; vector < (_NumberOfSubSubframes[subframe] + 1) / 2; vector++)
						{
							var tableOffset = (int)_Bits.ReadBits(10) * Subbands;
							var count = Math.Min(numberOfSamples - vector * 16, 16);
							for (var index = 0; index < count; index++) samples[destination++] = DcaMath.Clip23((DcaTables.HighFreqVq[tableOffset + index] * scale + (1 << 3)) >> 4);
						}
					}
				}
			var outputOffset = subbandPosition;
			for (var subSubframe = 0; subSubframe < _NumberOfSubSubframes[subframe]; subSubframe++)
			{
				for (var channel = channelBase; channel < _X96Channels; channel++)
				{
					if (_Bits.BitsLeft < 0) return FfmpegError.InvalidData;
					for (var band = _X96SubbandStart; band < _NumberOfSubbands[channel]; band++)
					{
						var allocation = _BitAllocation[channel, band] - 1;
						if (allocation < 1) continue;
						var result = ExtractAudio(audio, allocation, channel);
						if (result < 0) return result;
						var stepSize = unchecked((int)(_BitRate == 3 ? DcaTables.LosslessQuant[allocation] : DcaTables.LossyQuant[allocation]));
						var scale = _ScaleFactors[channel, band >> 1, band & 1];
						Dequantize(_X96Subband[channel][band], AdpcmCoefficients + outputOffset, audio, stepSize, scale, false);
					}
				}
				if ((subSubframe == _NumberOfSubSubframes[subframe] - 1 || _SyncSubSubframes != 0) && _Bits.ReadBits(16) != 0xffff) return FfmpegError.InvalidData;
				outputOffset += SubbandSamples;
			}
			for (var channel = channelBase; channel < _X96Channels; channel++) InverseAdpcm(_X96Subband[channel], channel, _X96SubbandStart, _NumberOfSubbands[channel], subbandPosition, numberOfSamples);
			for (var channel = channelBase; channel < _X96Channels; channel++)
			{
				var sourceChannel = _JointIntensityIndex[channel] - 1;
				if (sourceChannel < 0) continue;
				for (var band = _NumberOfSubbands[channel]; band < _NumberOfSubbands[sourceChannel]; band++)
					for (var index = 0; index < numberOfSamples; index++) _X96Subband[channel][band][AdpcmCoefficients + subbandPosition + index] = DcaMath.Clip23(DcaMath.Multiply(_X96Subband[sourceChannel][band][AdpcmCoefficients + subbandPosition + index], _JointScaleFactors[channel, band], 17));
			}
			subbandPosition = outputOffset;
			return 0;
		}

		private int RandomX96()
		{
			_X96Random = unchecked(1103515245U * _X96Random + 12345U);
			return unchecked((int)((_X96Random & 0x7fffffff) - 0x40000000));
		}

		private void PrepareSampleBuffers()
		{
			if (_PredictorHistory == 0)
			{
				for (var channel = 0; channel < ChannelsMaximum; channel++)
					for (var band = 0; band < Subbands; band++) Array.Clear(_Subband[channel][band], 0, AdpcmCoefficients);
			}
		}

		private static void Dequantize(int[] output, int outputOffset, ReadOnlySpan<int> input, int stepSize, int scale, bool residual)
		{
			long stepScale = (long)stepSize * scale;
			var shift = 0;
			if (stepScale > 1 << 23)
			{
				shift = BitOperations.Log2((ulong)(stepScale >> 23)) + 1;
				stepScale >>= shift;
			}
			for (var index = 0; index < SubbandSamples; index++)
			{
				var value = DcaMath.Clip23(DcaMath.Normalize(input[index] * stepScale, 22 - shift));
				if (residual) output[outputOffset + index] += value;
				else output[outputOffset + index] = value;
			}
		}

		private void InverseAdpcm(int[][] samples, int channel, int startBand, int endBand, int offset, int length)
		{
			for (var band = startBand; band < endBand; band++)
			{
				if (_PredictionMode[channel, band] == 0) continue;
				var predictor = _PredictionVectorQuantizationIndex[channel, band];
				var coefficientOffset = predictor * AdpcmCoefficients;
				var values = samples[band];
				for (var index = 0; index < length; index++)
				{
					long prediction = 0;
					var position = AdpcmCoefficients + offset + index;
					for (var coefficient = 0; coefficient < AdpcmCoefficients; coefficient++) prediction += (long)values[position - 1 - coefficient] * DcaTables.AdpcmVb[coefficientOffset + coefficient];
					values[position] = DcaMath.Clip23(values[position] + DcaMath.Clip23(DcaMath.Normalize(prediction, 13)));
				}
			}
		}

		/// <summary>
		/// Runs FFmpeg's 32-band half-IMDCT and polyphase window accumulation for every PCM block of one channel.
		/// </summary>
		private void Synthesize32(int channel, float[] output, float[] window)
		{
			var history1 = _SynthesisHistory1[channel];
			var history2 = _SynthesisHistory2[channel];
			var historyOffset = _SynthesisOffset[channel];
			for (var block = 0; block < _PcmBlocks; block++)
			{
				for (var band = 0; band < Subbands; band++)
				{
					var value = (float)_Subband[channel][band][AdpcmCoefficients + block];
					_SynthesisInput[band] = ((band - 1) & 2) != 0 ? -value : value;
				}
				_Mdct32.Transform(_SynthesisInput, history1.AsSpan(historyOffset, 32));
				for (var index = 0; index < 16; index++)
				{
					var a = history2[index];
					var b = history2[index + 16];
					var c = 0.0f;
					var d = 0.0f;
					var windowIndex = 0;
					for (; windowIndex < 512 - historyOffset; windowIndex += 64)
					{
						a += window[index + windowIndex] * -history1[historyOffset + 15 - index + windowIndex];
						b += window[index + windowIndex + 16] * history1[historyOffset + index + windowIndex];
						c += window[index + windowIndex + 32] * history1[historyOffset + 16 + index + windowIndex];
						d += window[index + windowIndex + 48] * history1[historyOffset + 31 - index + windowIndex];
					}
					for (; windowIndex < 512; windowIndex += 64)
					{
						a += window[index + windowIndex] * -history1[historyOffset + 15 - index + windowIndex - 512];
						b += window[index + windowIndex + 16] * history1[historyOffset + index + windowIndex - 512];
						c += window[index + windowIndex + 32] * history1[historyOffset + 16 + index + windowIndex - 512];
						d += window[index + windowIndex + 48] * history1[historyOffset + 31 - index + windowIndex - 512];
					}
					output[block * 32 + index] = a * (1.0f / (1 << 17));
					output[block * 32 + index + 16] = b * (1.0f / (1 << 17));
					history2[index] = c;
					history2[index + 16] = d;
				}
				historyOffset = (historyOffset - 32) & 511;
			}
			_SynthesisOffset[channel] = historyOffset;
		}

		private void SynthesizeLowFrequencyEffects(float[] output, int outputOffset)
		{
			var decimationSelection = _LowFrequencyEffects == 1 ? 1 : 0;
			var factor = 64 << decimationSelection;
			var numberOfCoefficients = 8 >> decimationSelection;
			var numberOfLfeSamples = _PcmBlocks >> (decimationSelection + 1);
			var filter = decimationSelection != 0 ? DcaTables.LfeFir128 : DcaTables.LfeFir64;
			for (var sample = 0; sample < numberOfLfeSamples; sample++)
			{
				for (var index = 0; index < factor / 2; index++)
				{
					var first = 0.0f;
					var second = 0.0f;
					for (var coefficient = 0; coefficient < numberOfCoefficients; coefficient++)
					{
						first += filter[index * numberOfCoefficients + coefficient] * _LfeSamples[LfeHistory + sample - coefficient];
						second += filter[255 - index * numberOfCoefficients - coefficient] * _LfeSamples[LfeHistory + sample - coefficient];
					}
					output[outputOffset + sample * factor + index] = first;
					output[outputOffset + sample * factor + factor / 2 + index] = second;
				}
			}
			for (var index = LfeHistory - 1; index >= 0; index--) _LfeSamples[index] = _LfeSamples[numberOfLfeSamples + index];
		}

		/// <summary>
		/// Runs FFmpeg's 64-band X96 QMF input permutation, half-IMDCT, and polyphase accumulation.
		/// </summary>
		private void Synthesize64(int channel, float[] output, float[] window, bool hasHighSubbands)
		{
			var history1 = _SynthesisHistory1[channel];
			var history2 = _SynthesisHistory2[channel];
			var historyOffset = _SynthesisOffset[channel];
			if (!hasHighSubbands) Array.Clear(_SynthesisInput, 32, 32);
			for (var block = 0; block < _PcmBlocks; block++)
			{
				if (hasHighSubbands)
				{
					for (var band = 0; band < 32; band++)
					{
						var value = (float)(_Subband[channel][band][AdpcmCoefficients + block] + _X96Subband[channel][band][AdpcmCoefficients + block]);
						_SynthesisInput[band] = ((band - 1) & 2) != 0 ? -value : value;
					}
					for (var band = 32; band < 64; band++)
					{
						var value = (float)_X96Subband[channel][band][AdpcmCoefficients + block];
						_SynthesisInput[band] = ((band - 1) & 2) != 0 ? -value : value;
					}
				} else
					for (var band = 0; band < 32; band++)
					{
						var value = (float)_Subband[channel][band][AdpcmCoefficients + block];
						_SynthesisInput[band] = ((band - 1) & 2) != 0 ? -value : value;
					}
				_Mdct64.Transform(_SynthesisInput, history1.AsSpan(historyOffset, 64));
				for (var index = 0; index < 32; index++)
				{
					var first = history2[index];
					var second = history2[index + 32];
					var third = 0.0f;
					var fourth = 0.0f;
					var windowIndex = 0;
					for (; windowIndex < 1024 - historyOffset; windowIndex += 128)
					{
						first += window[index + windowIndex] * -history1[historyOffset + 31 - index + windowIndex];
						second += window[index + windowIndex + 32] * history1[historyOffset + index + windowIndex];
						third += window[index + windowIndex + 64] * history1[historyOffset + 32 + index + windowIndex];
						fourth += window[index + windowIndex + 96] * history1[historyOffset + 63 - index + windowIndex];
					}
					for (; windowIndex < 1024; windowIndex += 128)
					{
						first += window[index + windowIndex] * -history1[historyOffset + 31 - index + windowIndex - 1024];
						second += window[index + windowIndex + 32] * history1[historyOffset + index + windowIndex - 1024];
						third += window[index + windowIndex + 64] * history1[historyOffset + 32 + index + windowIndex - 1024];
						fourth += window[index + windowIndex + 96] * history1[historyOffset + 63 - index + windowIndex - 1024];
					}
					output[block * 64 + index] = first * (1.0f / (1 << 16));
					output[block * 64 + index + 32] = second * (1.0f / (1 << 16));
					history2[index] = third;
					history2[index + 32] = fourth;
				}
				historyOffset = (historyOffset - 64) & 1023;
			}
			_SynthesisOffset[channel] = historyOffset;
		}

		private void UpsampleLowFrequencyEffectsX96(float[] output, int sourceOffset)
		{
			var previous = _OutputHistoryLfeFloat;
			for (var index = 0; index < sourceOffset; index++)
			{
				var value = output[sourceOffset + index];
				output[index * 2] = 0.25f * value + 0.75f * previous;
				output[index * 2 + 1] = 0.75f * value + 0.25f * previous;
				previous = value;
			}
			_OutputHistoryLfeFloat = previous;
		}

		/// <summary>
		/// Applies FFmpeg's fixed 32-band half-IMDCT and integer polyphase accumulation without changing its normalization points.
		/// </summary>
		private void SynthesizeFixed32(int channel, int[] output, int[] window)
		{
			var history1 = _FixedSynthesisHistory1[channel];
			var history2 = _FixedSynthesisHistory2[channel];
			var historyOffset = _FixedSynthesisOffset[channel];
			for (var block = 0; block < _PcmBlocks; block++)
			{
				for (var band = 0; band < Subbands; band++) _FixedSynthesisInput[band] = _Subband[channel][band][AdpcmCoefficients + block];
				_FixedDct.Transform32(history1, historyOffset, _FixedSynthesisInput);
				for (var index = 0; index < 16; index++)
				{
					long first = (long)history2[index] << 21;
					long second = (long)history2[index + 16] << 21;
					long third = 0;
					long fourth = 0;
					var windowIndex = 0;
					for (; windowIndex < 512 - historyOffset; windowIndex += 64)
					{
						first += (long)window[index + windowIndex] * history1[historyOffset + index + windowIndex];
						second += (long)window[index + windowIndex + 16] * history1[historyOffset + 15 - index + windowIndex];
						third += (long)window[index + windowIndex + 32] * history1[historyOffset + 16 + index + windowIndex];
						fourth += (long)window[index + windowIndex + 48] * history1[historyOffset + 31 - index + windowIndex];
					}
					for (; windowIndex < 512; windowIndex += 64)
					{
						first += (long)window[index + windowIndex] * history1[historyOffset + index + windowIndex - 512];
						second += (long)window[index + windowIndex + 16] * history1[historyOffset + 15 - index + windowIndex - 512];
						third += (long)window[index + windowIndex + 32] * history1[historyOffset + 16 + index + windowIndex - 512];
						fourth += (long)window[index + windowIndex + 48] * history1[historyOffset + 31 - index + windowIndex - 512];
					}
					output[block * 32 + index] = DcaMath.Clip23(DcaMath.Normalize(first, 21));
					output[block * 32 + index + 16] = DcaMath.Clip23(DcaMath.Normalize(second, 21));
					history2[index] = DcaMath.Normalize(third, 21);
					history2[index + 16] = DcaMath.Normalize(fourth, 21);
				}
				historyOffset = (historyOffset - 32) & 511;
			}
			_FixedSynthesisOffset[channel] = historyOffset;
		}

		/// <summary>
		/// Runs FFmpeg's fixed 64-band X96 synthesis, including its distinct integer input permutation and norm20 stages.
		/// </summary>
		private void SynthesizeFixed64(int channel, int[] output, int[] window, bool hasHighSubbands)
		{
			var history1 = _FixedSynthesisHistory1[channel];
			var history2 = _FixedSynthesisHistory2[channel];
			var historyOffset = _FixedSynthesisOffset[channel];
			if (!hasHighSubbands) Array.Clear(_FixedSynthesisInput, 32, 32);
			for (var block = 0; block < _PcmBlocks; block++)
			{
				if (hasHighSubbands)
				{
					for (var band = 0; band < 32; band++) _FixedSynthesisInput[band] = unchecked(_Subband[channel][band][AdpcmCoefficients + block] + _X96Subband[channel][band][AdpcmCoefficients + block]);
					for (var band = 32; band < 64; band++) _FixedSynthesisInput[band] = _X96Subband[channel][band][AdpcmCoefficients + block];
				} else for (var band = 0; band < 32; band++) _FixedSynthesisInput[band] = _Subband[channel][band][AdpcmCoefficients + block];
				_FixedDct.Transform64(history1, historyOffset, _FixedSynthesisInput);
				for (var index = 0; index < 32; index++)
				{
					long first = (long)history2[index] << 20;
					long second = (long)history2[index + 32] << 20;
					long third = 0;
					long fourth = 0;
					var windowIndex = 0;
					for (; windowIndex < 1024 - historyOffset; windowIndex += 128)
					{
						first += (long)window[index + windowIndex] * history1[historyOffset + index + windowIndex];
						second += (long)window[index + windowIndex + 32] * history1[historyOffset + 31 - index + windowIndex];
						third += (long)window[index + windowIndex + 64] * history1[historyOffset + 32 + index + windowIndex];
						fourth += (long)window[index + windowIndex + 96] * history1[historyOffset + 63 - index + windowIndex];
					}
					for (; windowIndex < 1024; windowIndex += 128)
					{
						first += (long)window[index + windowIndex] * history1[historyOffset + index + windowIndex - 1024];
						second += (long)window[index + windowIndex + 32] * history1[historyOffset + 31 - index + windowIndex - 1024];
						third += (long)window[index + windowIndex + 64] * history1[historyOffset + 32 + index + windowIndex - 1024];
						fourth += (long)window[index + windowIndex + 96] * history1[historyOffset + 63 - index + windowIndex - 1024];
					}
					output[block * 64 + index] = DcaMath.Clip23(DcaMath.Normalize(first, 20));
					output[block * 64 + index + 32] = DcaMath.Clip23(DcaMath.Normalize(second, 20));
					history2[index] = DcaMath.Normalize(third, 20);
					history2[index + 32] = DcaMath.Normalize(fourth, 20);
				}
				historyOffset = (historyOffset - 64) & 1023;
			}
			_FixedSynthesisOffset[channel] = historyOffset;
		}

		private void SynthesizeLowFrequencyEffectsFixed(int[] output, int outputOffset)
		{
			var numberOfLfeSamples = _PcmBlocks >> 1;
			for (var sample = 0; sample < numberOfLfeSamples; sample++)
			{
				for (var index = 0; index < 32; index++)
				{
					long first = 0;
					long second = 0;
					for (var coefficient = 0; coefficient < 8; coefficient++)
					{
						first += (long)DcaTables.LfeFir64Fixed[index * 8 + coefficient] * _LfeSamples[LfeHistory + sample - coefficient];
						second += (long)DcaTables.LfeFir64Fixed[255 - index * 8 - coefficient] * _LfeSamples[LfeHistory + sample - coefficient];
					}
					output[outputOffset + sample * 64 + index] = DcaMath.Clip23(DcaMath.Normalize(first, 23));
					output[outputOffset + sample * 64 + 32 + index] = DcaMath.Clip23(DcaMath.Normalize(second, 23));
				}
			}
			for (var index = LfeHistory - 1; index >= 0; index--) _LfeSamples[index] = _LfeSamples[numberOfLfeSamples + index];
		}

		private void UpsampleLowFrequencyEffectsFixedX96(int[] output, int sourceOffset)
		{
			var previous = _OutputHistoryLfeFixed;
			for (var index = 0; index < sourceOffset; index++)
			{
				var value = output[sourceOffset + index];
				output[index * 2] = DcaMath.Clip23(DcaMath.Normalize(2097471L * value + 6291137L * previous, 23));
				output[index * 2 + 1] = DcaMath.Clip23(DcaMath.Normalize(6291137L * value + 2097471L * previous, 23));
				previous = value;
			}
			_OutputHistoryLfeFixed = previous;
		}

		private int MapPrimaryChannelToSpeaker(int channel)
		{
			var coreChannels = DcaTables.Channels[_AudioMode];
			if (channel < coreChannels)
			{
				var speaker = DcaTables.PrimaryChannelToSpeaker[_AudioMode * 5 + channel];
				if ((_ExtensionAudioMask & (ExtensionCoreXxch | ExtensionExssXxch)) != 0)
				{
					if ((_XxchCoreMask & (1 << speaker)) != 0) return speaker;
					if (speaker == SpeakerLeftSurround && (_XxchCoreMask & (1 << SpeakerLeftSideSurround)) != 0) return SpeakerLeftSideSurround;
					if (speaker == SpeakerRightSurround && (_XxchCoreMask & (1 << SpeakerRightSideSurround)) != 0) return SpeakerRightSideSurround;
					return -1;
				}
				return speaker;
			}
			if ((_ExtensionAudioMask & ExtensionCoreXch) != 0 && channel == coreChannels) return SpeakerCenterSurround;
			if ((_ExtensionAudioMask & (ExtensionCoreXxch | ExtensionExssXxch)) != 0)
			{
				var position = coreChannels;
				for (var speaker = SpeakerCenterSurround; speaker < _XxchMaskBitCount; speaker++)
					if ((_XxchSpeakerMask & (1 << speaker)) != 0 && position++ == channel) return speaker;
			}
			return -1;
		}

		private int BuildChannelRemap(int mask)
		{
			var wide = mask == 0x6001f || mask == 0x6003f;
			var map = wide ? DcaTables.DcaToWaveWide : DcaTables.DcaToWaveNormal;
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

		private static void ApplyButterfly(float[] first, float[] second, int length)
		{
			for (var index = 0; index < length; index++)
			{
				var difference = first[index] - second[index];
				first[index] += second[index];
				second[index] = difference;
			}
		}

		private int ReadVlc(Vlc vlc, int maximumDepth)
		{
			return _Bits.ReadVlc(vlc.Table, vlc.RootBits, maximumDepth);
		}

		private int SeekBits(int position)
		{
			if (position < _Bits.Position || position > _Bits.SizeInBits) return FfmpegError.InvalidData;
			_Bits.SkipBits(position - _Bits.Position);
			return 0;
		}

		private void _BitsSkip(int count)
		{
			if (count <= 25) _Bits.SkipBits(count);
			else while (count > 0) { var step = Math.Min(count, 25); _Bits.SkipBits(step); count -= step; }
		}

		private uint ReadWord(int relativeOffset)
		{
			return relativeOffset >= 0 && relativeOffset <= _InputSize - 4
				? BinaryPrimitives.ReadUInt32BigEndian(_Input.AsSpan(_InputOffset + relativeOffset, 4)) : 0;
		}

		private static int[][][] CreateSampleCube(int planes, int bands, int samples)
		{
			var result = new int[planes][][];
			for (var plane = 0; plane < planes; plane++)
			{
				result[plane] = new int[bands][];
				for (var band = 0; band < bands; band++) result[plane][band] = new int[samples];
			}
			return result;
		}

		private static float[][] CreateFloatPlanes(int planes, int samples)
		{
			var result = new float[planes][];
			for (var plane = 0; plane < planes; plane++) result[plane] = new float[samples];
			return result;
		}

		private static int[][] CreateIntPlanes(int planes, int samples)
		{
			var result = new int[planes][];
			for (var plane = 0; plane < planes; plane++) result[plane] = new int[samples];
			return result;
		}

		private enum HeaderType
		{
			Core,
			Xch,
			Xxch
		}
	}
}
