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
using Ffmpeg.CsPort.Decoder.Transforms;
using Ffmpeg.CsPort.Decoder.Windows;

namespace Ffmpeg.CsPort.Decoder.Codecs.Ac3
{
	/// <summary>
	/// Ports FFmpeg's scalar floating-point AC-3 decoder from sync parsing through planar output synthesis.
	/// </summary>
	public sealed class Ac3Decoder
	{
		private const int CouplingChannel = 0;
		private const int MaximumChannels = 7;
		private const int ExponentReuse = 0;
		private const int DeltaReuse = 0;
		private const int DeltaNew = 1;
		private const int DeltaNone = 2;
		private const int DeltaReserved = 3;
		private const int ParseErrorSync = -0x1030c0a;
		private readonly Ac3DecoderState _State = new Ac3DecoderState();
		private readonly BitReader _Bits = new BitReader();
		private readonly BitReader _LookaheadBits = new BitReader();
		private readonly FfmpegFloatMdct _Mdct128 = new FfmpegFloatMdct(128, true, 1.0f);
		private readonly FfmpegFloatMdct _Mdct256 = new FfmpegFloatMdct(256, true, 1.0f);
		private static readonly float[] s_DynamicRange = CreateDynamicRangeTable();

		public Ac3Decoder()
		{
			CodecWindows.InitializeKaiserBesselWindow(_State.Window, 5.0f, 256);
			Ac3MantissaTables.InitializeDither(_State.DitherState);
			_State.DitherIndex = 0;
		}

		/// <summary>
		/// Finds and normalizes one sync frame, decodes all six AC-3 blocks, and writes FFmpeg-ordered planar floats.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength <= 0 || packetOffset > packet.Length - packetLength)
				return FfmpegError.InvalidArgument;
			var syncOffset = FindSyncWord(packet, packetOffset, packetLength);
			if (syncOffset < 0 || syncOffset > 10) return syncOffset;
			var sourceOffset = packetOffset + syncOffset;
			var available = Math.Min(packetLength - syncOffset, _State.InputBuffer.Length);
			if (available < Ac3HeaderParser.HeaderSize) return FfmpegError.InvalidData;
			if (packet[sourceOffset] == 0x77 && packet[sourceOffset + 1] == 0x0b)
			{
				var wordCount = available >> 1;
				for (var index = 0; index < wordCount; index++)
				{
					_State.InputBuffer[index * 2] = packet[sourceOffset + index * 2 + 1];
					_State.InputBuffer[index * 2 + 1] = packet[sourceOffset + index * 2];
				}
			} else
			{
				Array.Copy(packet, sourceOffset, _State.InputBuffer, 0, available);
			}
			if (_Bits.Initialize(_State.InputBuffer, available * 8) < 0) return FfmpegError.InvalidData;
			var headerResult = ParseFrameHeader();
			if (headerResult < 0) return headerResult;
			if (_State.BitRate == 0 || _State.NumberOfBlocks <= 0 || _State.Channels <= 0) return FfmpegError.InvalidData;
			var frameSize = GetCurrentFrameSize();
			var sampleCount = _State.NumberOfBlocks * Ac3Tables.BlockSize;
			if (frameSize > available)
			{
				var incompletePlaneSize = sampleCount * sizeof(float);
				var incompleteOutputSize = incompletePlaneSize * _State.Channels;
				if (output.Length < incompleteOutputSize) return FfmpegError.InvalidArgument;
				for (var channel = 0; channel < _State.OutputMap.Length; channel++) _State.OutputMap[channel] = channel;
				ConcealOutput(0, _State.Channels, sampleCount);
				WriteOutput(output, sampleCount, _State.Channels);
				frame = new AudioFrameInfo(sampleCount, _State.Channels, AudioSampleFormat.FloatPlanar,
					_State.Channels, incompletePlaneSize, incompleteOutputSize);
				return packetLength;
			}
			var independentChannels = _State.Channels;
			var independentChannelMode = _State.ChannelMode;
			var independentLowFrequencyEffects = _State.LowFrequencyEffects;
			var independentSampleRate = _State.SampleRate;
			var independentBlocks = _State.NumberOfBlocks;
			var independentLayout = GetChannelLayout(independentChannelMode, independentLowFrequencyEffects);
			var hasDependentFrame = false;
			var trailingHeaderResult = 0;
			var outputChannels = independentChannels;
			if (available - frameSize > 16)
			{
				if (_State.InputBuffer[frameSize] != 0x0b || _State.InputBuffer[frameSize + 1] != 0x77)
				{
					trailingHeaderResult = ParseErrorSync;
				} else if (_LookaheadBits.Initialize(_State.InputBuffer, frameSize, (available - frameSize) * 8) < 0)
				{
					trailingHeaderResult = FfmpegError.InvalidData;
				} else
				{
					trailingHeaderResult = Ac3HeaderParser.Parse(_LookaheadBits, out var dependentHeader);
					if (trailingHeaderResult == 0 && dependentHeader.FrameType == (int)Eac3FrameType.Dependent &&
						dependentHeader.NumberOfBlocks == independentBlocks && dependentHeader.SampleRate == independentSampleRate)
					{
						hasDependentFrame = true;
						outputChannels = CountBits(independentLayout | GetDependentChannelLayout(dependentHeader.ChannelMap));
					}
				}
			}
			var planeSize = sampleCount * sizeof(float);
			var outputSize = planeSize * outputChannels;
			if (output.Length < outputSize) return FfmpegError.InvalidArgument;
			for (var channel = 0; channel < _State.OutputMap.Length; channel++) _State.OutputMap[channel] = channel;

			var independentDecodeError = false;
			for (var block = 0; block < _State.NumberOfBlocks; block++)
			{
				if (!independentDecodeError)
					independentDecodeError = DecodeAudioBlock(block, 0) < 0;
				if (independentDecodeError) ConcealOutputBlock(0, independentChannels, block);
			}
			SavePreviousOutput(0, independentChannels, sampleCount);
			if (trailingHeaderResult < 0) return trailingHeaderResult;
			var consumed = frameSize;
			if (hasDependentFrame)
			{
				if (_Bits.Initialize(_State.InputBuffer, frameSize, (available - frameSize) * 8) < 0) return FfmpegError.InvalidData;
				var dependentResult = ParseFrameHeader();
				if (dependentResult < 0 || _State.FrameType != (int)Eac3FrameType.Dependent) return dependentResult < 0 ? dependentResult : FfmpegError.InvalidData;
				var dependentDecodeError = false;
				for (var block = 0; block < _State.NumberOfBlocks; block++)
				{
					if (!dependentDecodeError)
						dependentDecodeError = DecodeAudioBlock(block, MaximumChannels) < 0;
					if (dependentDecodeError) ConcealOutputBlock(MaximumChannels, _State.Channels, block);
				}
				SavePreviousOutput(MaximumChannels, _State.Channels, sampleCount);
				BuildDependentOutputMap(independentLayout, outputChannels);
				consumed += _State.FrameSize;
			}
			WriteOutput(output, sampleCount, outputChannels);
			frame = new AudioFrameInfo(sampleCount, outputChannels, AudioSampleFormat.FloatPlanar, outputChannels, planeSize, outputSize);
			return Math.Min(packetLength, consumed + syncOffset);
		}

		public void Flush()
		{
			for (var channel = 0; channel < _State.Delay.Length; channel++) Array.Clear(_State.Delay[channel], 0, _State.Delay[channel].Length);
			for (var channel = 0; channel < _State.Output.Length; channel++) Array.Clear(_State.Output[channel], 0, _State.Output[channel].Length);
			for (var channel = 0; channel < _State.PreviousOutput.Length; channel++) Array.Clear(_State.PreviousOutput[channel], 0, _State.PreviousOutput[channel].Length);
			Ac3MantissaTables.InitializeDither(_State.DitherState);
			_State.DitherIndex = 0;
			_State.Downmixed = 0;
		}

		/// <summary>
		/// Transfers AC-3 or E-AC-3 header syntax into frame state and derives channel, block, and coupling limits.
		/// </summary>
		private int ParseFrameHeader()
		{
			var result = Ac3HeaderParser.Parse(_Bits, out var header);
			if (result < 0) return result;
			_State.BitAllocationParameters.SampleRateCode = header.SampleRateCode;
			_State.BitAllocationParameters.SampleRateShift = header.SampleRateShift;
			_State.BitstreamId = header.BitstreamId;
			_State.FrameType = header.FrameType;
			_State.ChannelMode = header.ChannelMode;
			_State.LowFrequencyEffects = header.LowFrequencyEffects;
			_State.SampleRate = header.SampleRate;
			_State.BitRate = header.BitRate;
			_State.FrameSize = header.FrameSize;
			_State.ChannelMap = header.ChannelMap;
			_State.Channels = header.Channels;
			_State.FullBandwidthChannels = header.Channels - header.LowFrequencyEffects;
			_State.LowFrequencyEffectsChannel = _State.FullBandwidthChannels + 1;
			_State.NumberOfBlocks = header.NumberOfBlocks;
			if (_State.LowFrequencyEffects != 0)
			{
				var channel = _State.LowFrequencyEffectsChannel;
				_State.StartFrequency[channel] = 0;
				_State.EndFrequency[channel] = 7;
				_State.NumberOfExponentGroups[channel] = 2;
				_State.ChannelInCoupling[channel] = 0;
			}
			if (header.BitstreamId <= 10)
			{
				_State.IsEnhanced = 0;
				_State.SignalToNoiseOffsetStrategy = 2;
				_State.BlockSwitchSyntax = 1;
				_State.DitherFlagSyntax = 1;
				_State.BitAllocationSyntax = 1;
				_State.FastGainSyntax = 0;
				_State.FirstCouplingLeak = 0;
				_State.DeltaBitAllocationSyntax = 1;
				_State.SkipSyntax = 1;
				Array.Clear(_State.ChannelUsesAdaptiveHybridTransform, 0, _State.ChannelUsesAdaptiveHybridTransform.Length);
				return 0;
			}
			_State.IsEnhanced = 1;
			return ParseEnhancedFrameHeader();
		}

		/// <summary>
		/// Ports E-AC-3's frame-level syntax switches, predeclared coupling state, and exponent strategy matrix.
		/// </summary>
		private int ParseEnhancedFrameHeader()
		{
			if (_State.BitAllocationParameters.SampleRateCode == 3) return FfmpegError.PatchWelcome;
			var exponentStrategyPerBlock = 1;
			var parseAdaptiveHybridTransform = 0;
			if (_State.NumberOfBlocks == 6)
			{
				exponentStrategyPerBlock = (int)_Bits.ReadBit();
				parseAdaptiveHybridTransform = (int)_Bits.ReadBit();
			}
			_State.SignalToNoiseOffsetStrategy = (int)_Bits.ReadBits(2);
			var parseTransientProcessing = (int)_Bits.ReadBit();
			_State.BlockSwitchSyntax = (int)_Bits.ReadBit();
			if (_State.BlockSwitchSyntax == 0) Array.Clear(_State.BlockSwitch, 0, _State.BlockSwitch.Length);
			_State.DitherFlagSyntax = (int)_Bits.ReadBit();
			if (_State.DitherFlagSyntax == 0)
				for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++) _State.DitherFlag[channel] = 1;
			_State.DitherFlag[CouplingChannel] = 0;
			_State.DitherFlag[_State.LowFrequencyEffectsChannel] = 0;
			_State.BitAllocationSyntax = (int)_Bits.ReadBit();
			if (_State.BitAllocationSyntax == 0)
			{
				_State.BitAllocationParameters.SlowDecay = Ac3Tables.SlowDecay[2];
				_State.BitAllocationParameters.FastDecay = Ac3Tables.FastDecay[1];
				_State.BitAllocationParameters.SlowGain = Ac3Tables.SlowGain[1];
				_State.BitAllocationParameters.DecibelsPerBit = Ac3Tables.DecibelsPerBit[2];
				_State.BitAllocationParameters.Floor = Ac3Tables.Floor[7];
			}
			_State.FastGainSyntax = (int)_Bits.ReadBit();
			_State.DeltaBitAllocationSyntax = (int)_Bits.ReadBit();
			_State.SkipSyntax = (int)_Bits.ReadBit();
			var parseSpectralExtensionAttenuation = (int)_Bits.ReadBit();

			var couplingBlockCount = 0;
			if (_State.ChannelMode > 1)
			{
				for (var block = 0; block < _State.NumberOfBlocks; block++)
				{
					_State.CouplingStrategyExists[block] = block == 0 || _Bits.ReadBit() != 0 ? 1 : 0;
					if (_State.CouplingStrategyExists[block] != 0) _State.CouplingInUse[block] = (int)_Bits.ReadBit();
					else _State.CouplingInUse[block] = _State.CouplingInUse[block - 1];
					couplingBlockCount += _State.CouplingInUse[block];
				}
			} else Array.Clear(_State.CouplingInUse, 0, _State.CouplingInUse.Length);

			if (exponentStrategyPerBlock != 0)
			{
				for (var block = 0; block < _State.NumberOfBlocks; block++)
					for (var channel = _State.CouplingInUse[block] == 0 ? 1 : 0; channel <= _State.FullBandwidthChannels; channel++)
						_State.ExponentStrategy[block][channel] = (int)_Bits.ReadBits(2);
			} else
			{
				var firstChannel = _State.ChannelMode > 1 && couplingBlockCount != 0 ? 0 : 1;
				for (var channel = firstChannel; channel <= _State.FullBandwidthChannels; channel++)
				{
					var strategy = (int)_Bits.ReadBits(5);
					for (var block = 0; block < 6; block++) _State.ExponentStrategy[block][channel] = Ac3Tables.EnhancedFrameExponentStrategy[strategy, block];
				}
			}
			if (_State.LowFrequencyEffects != 0)
				for (var block = 0; block < _State.NumberOfBlocks; block++) _State.ExponentStrategy[block][_State.LowFrequencyEffectsChannel] = (int)_Bits.ReadBit();
			if (_State.FrameType == (int)Eac3FrameType.Independent && (_State.NumberOfBlocks == 6 || _Bits.ReadBit() != 0))
				_Bits.SkipBits(5 * _State.FullBandwidthChannels);

			if (parseAdaptiveHybridTransform != 0)
			{
				_State.ChannelUsesAdaptiveHybridTransform[CouplingChannel] = 0;
				for (var channel = couplingBlockCount != 6 ? 1 : 0; channel <= _State.Channels; channel++)
				{
					var canUse = true;
					for (var block = 1; block < 6; block++)
						if (_State.ExponentStrategy[block][channel] != ExponentReuse || channel == 0 && _State.CouplingStrategyExists[block] != 0) { canUse = false; break; }
					_State.ChannelUsesAdaptiveHybridTransform[channel] = canUse && _Bits.ReadBit() != 0 ? 1 : 0;
				}
			} else Array.Clear(_State.ChannelUsesAdaptiveHybridTransform, 0, _State.ChannelUsesAdaptiveHybridTransform.Length);

			if (_State.SignalToNoiseOffsetStrategy == 0)
			{
				var coarse = ((int)_Bits.ReadBits(6) - 15) << 4;
				var offset = (coarse + (int)_Bits.ReadBits(4)) << 2;
				for (var channel = 0; channel <= _State.Channels; channel++) _State.SignalToNoiseOffset[channel] = offset;
			}
			if (parseTransientProcessing != 0)
				for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++) if (_Bits.ReadBit() != 0) _Bits.SkipBits(18);
			for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
				_State.SpectralExtensionAttenuationCode[channel] = parseSpectralExtensionAttenuation != 0 && _Bits.ReadBit() != 0 ? (sbyte)_Bits.ReadBits(5) : (sbyte)-1;
			if (_State.NumberOfBlocks > 1 && _Bits.ReadBit() != 0)
				_Bits.SkipBits((_State.NumberOfBlocks - 1) * (4 + IntegerLog2(_State.FrameSize - 2)));
			for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
			{
				_State.FirstSpectralExtensionCoordinates[channel] = 1;
				_State.FirstCouplingCoordinates[channel] = 1;
			}
			_State.FirstCouplingLeak = 1;
			return 0;
		}

		/// <summary>
		/// Preserves FFmpeg's audio-block syntax order from switch flags through bit allocation, mantissas, rematrixing, and IMDCT.
		/// </summary>
		private int DecodeAudioBlock(int block, int synthesisOffset)
		{
			Span<byte> bitAllocationStages = stackalloc byte[MaximumChannels];
			var differentTransforms = false;
			if (_State.BlockSwitchSyntax != 0)
			{
				for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
				{
					_State.BlockSwitch[channel] = (int)_Bits.ReadBit();
					if (channel > 1 && _State.BlockSwitch[channel] != _State.BlockSwitch[1]) differentTransforms = true;
				}
			}
			if (_State.DitherFlagSyntax != 0)
				for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++) _State.DitherFlag[channel] = (int)_Bits.ReadBit();

			var dynamicChannel = _State.ChannelMode == 0 ? 1 : 0;
			do
			{
				if (_Bits.ReadBit() != 0)
				{
					var rangeBits = (int)_Bits.ReadBits(8);
					_State.DynamicRange[dynamicChannel] = MathF.Pow(s_DynamicRange[rangeBits], 1.0f);
				} else if (block == 0) _State.DynamicRange[dynamicChannel] = 1.0f;
			} while (dynamicChannel-- != 0);

			if (_State.IsEnhanced != 0 && (block == 0 || _Bits.ReadBit() != 0))
			{
				_State.SpectralExtensionInUse = (int)_Bits.ReadBit();
				if (_State.SpectralExtensionInUse != 0)
				{
					var result = DecodeSpectralExtensionStrategy(block);
					if (result < 0) return result;
				}
			}
			if (_State.IsEnhanced == 0 || _State.SpectralExtensionInUse == 0)
			{
				_State.SpectralExtensionInUse = 0;
				for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
				{
					_State.ChannelUsesSpectralExtension[channel] = 0;
					_State.FirstSpectralExtensionCoordinates[channel] = 1;
				}
			}
			if (_State.SpectralExtensionInUse != 0) DecodeSpectralExtensionCoordinates();

			var couplingStrategyExists = _State.IsEnhanced != 0 ? _State.CouplingStrategyExists[block] != 0 : _Bits.ReadBit() != 0;
			if (couplingStrategyExists)
			{
				var result = DecodeCouplingStrategy(block, bitAllocationStages);
				if (result < 0) return result;
			} else if (_State.IsEnhanced == 0 && block == 0)
			{
				return FfmpegError.InvalidData;
			} else if (_State.IsEnhanced == 0)
			{
				_State.CouplingInUse[block] = _State.CouplingInUse[block - 1];
			}
			var couplingInUse = _State.CouplingInUse[block] != 0;
			if (couplingInUse)
			{
				var result = DecodeCouplingCoordinates(block);
				if (result < 0) return result;
			}

			if (_State.ChannelMode == 2)
			{
				if (_State.IsEnhanced != 0 && block == 0 || _Bits.ReadBit() != 0)
				{
					_State.NumberOfRematrixingBands = 4;
					if (couplingInUse && _State.StartFrequency[CouplingChannel] <= 61)
						_State.NumberOfRematrixingBands -= 1 + (_State.StartFrequency[CouplingChannel] == 37 ? 1 : 0);
					for (var band = 0; band < _State.NumberOfRematrixingBands; band++) _State.RematrixingFlags[band] = (int)_Bits.ReadBit();
				} else if (block == 0) _State.NumberOfRematrixingBands = 0;
			}

			for (var channel = couplingInUse ? 0 : 1; channel <= _State.Channels; channel++)
			{
				if (_State.IsEnhanced == 0)
					_State.ExponentStrategy[block][channel] = (int)_Bits.ReadBits(2 - (channel == _State.LowFrequencyEffectsChannel ? 1 : 0));
				if (_State.ExponentStrategy[block][channel] != ExponentReuse) bitAllocationStages[channel] = 3;
			}
			for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
			{
				_State.StartFrequency[channel] = 0;
				if (_State.ExponentStrategy[block][channel] == ExponentReuse) continue;
				var previousEnd = _State.EndFrequency[channel];
				if (_State.ChannelInCoupling[channel] != 0) _State.EndFrequency[channel] = _State.StartFrequency[CouplingChannel];
				else if (_State.ChannelUsesSpectralExtension[channel] != 0) _State.EndFrequency[channel] = _State.SpectralExtensionSourceStartFrequency;
				else
				{
					var bandwidthCode = (int)_Bits.ReadBits(6);
					if (bandwidthCode > 60) return FfmpegError.InvalidData;
					_State.EndFrequency[channel] = bandwidthCode * 3 + 73;
				}
				var groupSize = 3 << (_State.ExponentStrategy[block][channel] - 1);
				_State.NumberOfExponentGroups[channel] = (_State.EndFrequency[channel] + groupSize - 4) / groupSize;
				if (block > 0 && _State.EndFrequency[channel] != previousEnd) bitAllocationStages.Fill(3);
			}
			if (couplingInUse && _State.ExponentStrategy[block][CouplingChannel] != ExponentReuse)
				_State.NumberOfExponentGroups[CouplingChannel] = (_State.EndFrequency[CouplingChannel] - _State.StartFrequency[CouplingChannel]) /
					(3 << (_State.ExponentStrategy[block][CouplingChannel] - 1));

			for (var channel = couplingInUse ? 0 : 1; channel <= _State.Channels; channel++)
			{
				if (_State.ExponentStrategy[block][channel] == ExponentReuse) continue;
				_State.Exponents[channel][0] = (sbyte)((int)_Bits.ReadBits(4) << (channel == 0 ? 1 : 0));
				var result = DecodeExponents(_State.ExponentStrategy[block][channel], _State.NumberOfExponentGroups[channel],
					_State.Exponents[channel][0], _State.Exponents[channel], _State.StartFrequency[channel] + (channel != 0 ? 1 : 0));
				if (result < 0) return result;
				if (channel != CouplingChannel && channel != _State.LowFrequencyEffectsChannel) _Bits.SkipBits(2);
			}

			if (_State.BitAllocationSyntax != 0 && _Bits.ReadBit() != 0)
			{
				ref var parameters = ref _State.BitAllocationParameters;
				parameters.SlowDecay = Ac3Tables.SlowDecay[_Bits.ReadBits(2)] >> parameters.SampleRateShift;
				parameters.FastDecay = Ac3Tables.FastDecay[_Bits.ReadBits(2)] >> parameters.SampleRateShift;
				parameters.SlowGain = Ac3Tables.SlowGain[_Bits.ReadBits(2)];
				parameters.DecibelsPerBit = Ac3Tables.DecibelsPerBit[_Bits.ReadBits(2)];
				parameters.Floor = Ac3Tables.Floor[_Bits.ReadBits(3)];
				for (var channel = couplingInUse ? 0 : 1; channel <= _State.Channels; channel++) bitAllocationStages[channel] = Math.Max(bitAllocationStages[channel], (byte)2);
			} else if (_State.BitAllocationSyntax != 0 && block == 0) return FfmpegError.InvalidData;

			if ((_State.IsEnhanced == 0 || block == 0) && _State.SignalToNoiseOffsetStrategy != 0 && _Bits.ReadBit() != 0)
			{
				var signalToNoise = 0;
				var coarse = ((int)_Bits.ReadBits(6) - 15) << 4;
				var firstChannel = couplingInUse ? 0 : 1;
				for (var channel = firstChannel; channel <= _State.Channels; channel++)
				{
					if (channel == firstChannel || _State.SignalToNoiseOffsetStrategy == 2) signalToNoise = (coarse + (int)_Bits.ReadBits(4)) << 2;
					if (block != 0 && _State.SignalToNoiseOffset[channel] != signalToNoise) bitAllocationStages[channel] = Math.Max(bitAllocationStages[channel], (byte)1);
					_State.SignalToNoiseOffset[channel] = signalToNoise;
					if (_State.IsEnhanced == 0)
					{
						var previousFastGain = _State.FastGain[channel];
						_State.FastGain[channel] = Ac3Tables.FastGain[_Bits.ReadBits(3)];
						if (block != 0 && previousFastGain != _State.FastGain[channel]) bitAllocationStages[channel] = Math.Max(bitAllocationStages[channel], (byte)2);
					}
				}
			} else if (_State.IsEnhanced == 0 && block == 0) return FfmpegError.InvalidData;

			if (_State.FastGainSyntax != 0 && _Bits.ReadBit() != 0)
			{
				for (var channel = couplingInUse ? 0 : 1; channel <= _State.Channels; channel++)
				{
					var previousFastGain = _State.FastGain[channel];
					_State.FastGain[channel] = Ac3Tables.FastGain[_Bits.ReadBits(3)];
					if (block != 0 && previousFastGain != _State.FastGain[channel]) bitAllocationStages[channel] = Math.Max(bitAllocationStages[channel], (byte)2);
				}
			} else if (_State.IsEnhanced != 0 && block == 0)
			{
				for (var channel = couplingInUse ? 0 : 1; channel <= _State.Channels; channel++) _State.FastGain[channel] = Ac3Tables.FastGain[4];
			}

			if (_State.FrameType == (int)Eac3FrameType.Independent && _Bits.ReadBit() != 0) _Bits.SkipBits(10);

			if (couplingInUse)
			{
				if (_State.FirstCouplingLeak != 0 || _Bits.ReadBit() != 0)
				{
					var fastLeak = (int)_Bits.ReadBits(3);
					var slowLeak = (int)_Bits.ReadBits(3);
					if (block != 0 && (fastLeak != _State.BitAllocationParameters.CouplingFastLeak || slowLeak != _State.BitAllocationParameters.CouplingSlowLeak))
						bitAllocationStages[CouplingChannel] = Math.Max(bitAllocationStages[CouplingChannel], (byte)2);
					_State.BitAllocationParameters.CouplingFastLeak = fastLeak;
					_State.BitAllocationParameters.CouplingSlowLeak = slowLeak;
				} else if (block == 0) return FfmpegError.InvalidData;
				_State.FirstCouplingLeak = 0;
			}

			if (_State.DeltaBitAllocationSyntax != 0 && _Bits.ReadBit() != 0)
			{
				for (var channel = couplingInUse ? 0 : 1; channel <= _State.FullBandwidthChannels; channel++)
				{
					_State.DeltaMode[channel] = (int)_Bits.ReadBits(2);
					if (_State.DeltaMode[channel] == DeltaReserved) return FfmpegError.InvalidData;
					bitAllocationStages[channel] = Math.Max(bitAllocationStages[channel], (byte)2);
				}
				for (var channel = couplingInUse ? 0 : 1; channel <= _State.FullBandwidthChannels; channel++)
				{
					if (_State.DeltaMode[channel] != DeltaNew) continue;
					_State.DeltaSegmentCount[channel] = (int)_Bits.ReadBits(3) + 1;
					for (var segment = 0; segment < _State.DeltaSegmentCount[channel]; segment++)
					{
						_State.DeltaOffsets[channel][segment] = (byte)_Bits.ReadBits(5);
						_State.DeltaLengths[channel][segment] = (byte)_Bits.ReadBits(4);
						_State.DeltaValues[channel][segment] = (byte)_Bits.ReadBits(3);
					}
				}
			} else if (block == 0)
			{
				for (var channel = 0; channel <= _State.Channels; channel++) _State.DeltaMode[channel] = DeltaNone;
			}

			var allocationResult = CalculateBitAllocation(bitAllocationStages, couplingInUse);
			if (allocationResult < 0) return allocationResult;
			if (_State.SkipSyntax != 0 && _Bits.ReadBit() != 0)
			{
				var skipLength = (int)_Bits.ReadBits(9);
				_Bits.SkipBits(8 * skipLength);
			}

			DecodeTransformCoefficients(couplingInUse, block);
			if (_State.ChannelMode == 2) ApplyRematrixing();
			for (var channel = 1; channel <= _State.Channels; channel++)
			{
				var audioChannel = _State.ChannelMode == 0 && channel <= 2 ? 2 - channel : 0;
				var gain = _State.DynamicRange[audioChannel];
				gain *= 1.0f / 4194304.0f;
				for (var coefficient = 0; coefficient < 256; coefficient++)
					_State.TransformCoefficients[channel][coefficient] = _State.FixedCoefficients[channel][coefficient] * gain;
			}
			if (_State.SpectralExtensionInUse != 0) ApplySpectralExtension();
			if (differentTransforms && _State.Downmixed != 0)
			{
				_State.Downmixed = 0;
				UpmixDelay();
			}
			ApplyImdct(block, synthesisOffset);
			return 0;
		}

		private int DecodeSpectralExtensionStrategy(int block)
		{
			if (_State.ChannelMode == 1) _State.ChannelUsesSpectralExtension[1] = 1;
			else
			{
				var uses = _Bits.ReadBits(_State.FullBandwidthChannels);
				for (var channel = _State.FullBandwidthChannels; channel >= 1; channel--)
				{
					_State.ChannelUsesSpectralExtension[channel] = (int)(uses & 1);
					uses >>= 1;
				}
			}
			var destinationStart = (int)_Bits.ReadBits(2);
			var startSubband = (int)_Bits.ReadBits(3) + 2;
			if (startSubband > 7) startSubband += startSubband - 7;
			var endSubband = (int)_Bits.ReadBits(3) + 5;
			if (endSubband > 7) endSubband += endSubband - 7;
			var destinationStartFrequency = destinationStart * 12 + 25;
			var sourceStartFrequency = startSubband * 12 + 25;
			var destinationEndFrequency = endSubband * 12 + 25;
			if (startSubband >= endSubband || destinationStartFrequency >= sourceStartFrequency) return FfmpegError.InvalidData;
			_State.SpectralExtensionDestinationStartFrequency = destinationStartFrequency;
			_State.SpectralExtensionSourceStartFrequency = sourceStartFrequency;
			_State.SpectralExtensionDestinationEndFrequency = destinationEndFrequency;
			DecodeSpectralExtensionBandStructure(block, startSubband, endSubband);
			return 0;
		}

		private void DecodeSpectralExtensionBandStructure(int block, int startSubband, int endSubband)
		{
			var subbandCount = endSubband - startSubband;
			if (block == 0) Array.Copy(Ac3Tables.DefaultSpectralExtensionBandStructure, _State.SpectralExtensionBandStructure, Ac3Tables.DefaultSpectralExtensionBandStructure.Length);
			var structureOffset = startSubband + 1;
			if (_Bits.ReadBit() != 0)
				for (var subband = 0; subband < subbandCount - 1; subband++) _State.SpectralExtensionBandStructure[structureOffset + subband] = (byte)_Bits.ReadBit();
			var bandCount = subbandCount;
			_State.SpectralExtensionBandSizes[0] = 12;
			var band = 0;
			for (var subband = 1; subband < subbandCount; subband++)
			{
				if (_State.SpectralExtensionBandStructure[structureOffset + subband - 1] != 0)
				{
					bandCount--;
					_State.SpectralExtensionBandSizes[band] += 12;
				} else _State.SpectralExtensionBandSizes[++band] = 12;
			}
			_State.NumberOfSpectralExtensionBands = bandCount;
		}

		private void DecodeSpectralExtensionCoordinates()
		{
			for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
			{
				if (_State.ChannelUsesSpectralExtension[channel] != 0)
				{
					if (_State.FirstSpectralExtensionCoordinates[channel] != 0 || _Bits.ReadBit() != 0)
					{
						_State.FirstSpectralExtensionCoordinates[channel] = 0;
						var blend = (int)_Bits.ReadBits(5) * (1.0f / 32);
						var masterCoordinate = (int)_Bits.ReadBits(2) * 3;
						var bin = _State.SpectralExtensionSourceStartFrequency;
						for (var band = 0; band < _State.NumberOfSpectralExtensionBands; band++)
						{
							var bandSize = _State.SpectralExtensionBandSizes[band];
							var ratio = (float)(bin + (bandSize >> 1)) / _State.SpectralExtensionDestinationEndFrequency - blend;
							if (ratio < 0.0f) ratio = 0.0f; else if (ratio > 1.0f) ratio = 1.0f;
							var noiseBlend = MathF.Sqrt(3.0f * ratio);
							var signalBlend = MathF.Sqrt(1.0f - ratio);
							bin += bandSize;
							var exponent = (int)_Bits.ReadBits(4);
							var mantissa = (int)_Bits.ReadBits(2);
							if (exponent == 15) mantissa <<= 1; else mantissa += 4;
							mantissa <<= 25 - exponent - masterCoordinate;
							var coordinate = mantissa * (1.0f / (1 << 23));
							_State.SpectralExtensionNoiseBlend[channel][band] = noiseBlend * coordinate;
							_State.SpectralExtensionSignalBlend[channel][band] = signalBlend * coordinate;
						}
					}
				} else _State.FirstSpectralExtensionCoordinates[channel] = 1;
			}
		}

		/// <summary>
		/// Reproduces E-AC-3 spectral-extension copy sections, band RMS scaling, boundary attenuation, and dither blending.
		/// </summary>
		private void ApplySpectralExtension()
		{
			Span<byte> wrapFlags = stackalloc byte[17];
			Span<byte> copySizes = stackalloc byte[18];
			Span<float> rootMeanSquareEnergy = stackalloc float[17];
			wrapFlags[0] = 1;
			var bin = _State.SpectralExtensionDestinationStartFrequency;
			var copySectionCount = 0;
			for (var band = 0; band < _State.NumberOfSpectralExtensionBands; band++)
			{
				var bandSize = _State.SpectralExtensionBandSizes[band];
				if (bin + bandSize > _State.SpectralExtensionSourceStartFrequency)
				{
					copySizes[copySectionCount++] = (byte)(bin - _State.SpectralExtensionDestinationStartFrequency);
					bin = _State.SpectralExtensionDestinationStartFrequency;
					wrapFlags[band] = 1;
				}
				for (var index = 0; index < bandSize;)
				{
					if (bin == _State.SpectralExtensionSourceStartFrequency)
					{
						copySizes[copySectionCount++] = (byte)(bin - _State.SpectralExtensionDestinationStartFrequency);
						bin = _State.SpectralExtensionDestinationStartFrequency;
					}
					var copySize = Math.Min(bandSize - index, _State.SpectralExtensionSourceStartFrequency - bin);
					bin += copySize;
					index += copySize;
				}
			}
			copySizes[copySectionCount++] = (byte)(bin - _State.SpectralExtensionDestinationStartFrequency);

			for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
			{
				if (_State.ChannelUsesSpectralExtension[channel] == 0) continue;
				var coefficients = _State.TransformCoefficients[channel];
				bin = _State.SpectralExtensionSourceStartFrequency;
				for (var section = 0; section < copySectionCount; section++)
				{
					Array.Copy(coefficients, _State.SpectralExtensionDestinationStartFrequency, coefficients, bin, copySizes[section]);
					bin += copySizes[section];
				}
				bin = _State.SpectralExtensionSourceStartFrequency;
				for (var band = 0; band < _State.NumberOfSpectralExtensionBands; band++)
				{
					var accumulator = 0.0f;
					var bandSize = _State.SpectralExtensionBandSizes[band];
					for (var index = 0; index < bandSize; index++) { var coefficient = coefficients[bin++]; accumulator += coefficient * coefficient; }
					rootMeanSquareEnergy[band] = MathF.Sqrt(accumulator / bandSize);
				}
				var attenuationCode = _State.SpectralExtensionAttenuationCode[channel];
				if (attenuationCode >= 0)
				{
					bin = _State.SpectralExtensionSourceStartFrequency - 2;
					for (var band = 0; band < _State.NumberOfSpectralExtensionBands; band++)
					{
						if (wrapFlags[band] != 0)
						{
							coefficients[bin] *= Ac3Tables.SpectralExtensionAttenuation[attenuationCode, 0];
							coefficients[bin + 1] *= Ac3Tables.SpectralExtensionAttenuation[attenuationCode, 1];
							coefficients[bin + 2] *= Ac3Tables.SpectralExtensionAttenuation[attenuationCode, 2];
							coefficients[bin + 3] *= Ac3Tables.SpectralExtensionAttenuation[attenuationCode, 1];
							coefficients[bin + 4] *= Ac3Tables.SpectralExtensionAttenuation[attenuationCode, 0];
						}
						bin += _State.SpectralExtensionBandSizes[band];
					}
				}
				bin = _State.SpectralExtensionSourceStartFrequency;
				for (var band = 0; band < _State.NumberOfSpectralExtensionBands; band++)
				{
					var noiseScale = _State.SpectralExtensionNoiseBlend[channel][band] * rootMeanSquareEnergy[band] * (1.0f / int.MinValue);
					var signalScale = _State.SpectralExtensionSignalBlend[channel][band];
					for (var index = 0; index < _State.SpectralExtensionBandSizes[band]; index++)
					{
						var noise = noiseScale * unchecked((int)NextDitherValue());
						coefficients[bin] *= signalScale;
						coefficients[bin++] += noise;
					}
				}
			}
		}

		private int DecodeCouplingStrategy(int block, Span<byte> bitAllocationStages)
		{
			bitAllocationStages.Fill(3);
			if (_State.IsEnhanced == 0) _State.CouplingInUse[block] = (int)_Bits.ReadBit();
			if (_State.CouplingInUse[block] != 0)
			{
				if (_State.ChannelMode < 2) return FfmpegError.InvalidData;
				if (_State.IsEnhanced != 0 && _Bits.ReadBit() != 0) return FfmpegError.PatchWelcome;
				if (_State.IsEnhanced != 0 && _State.ChannelMode == 2)
				{
					_State.ChannelInCoupling[1] = 1;
					_State.ChannelInCoupling[2] = 1;
				} else
					for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++) _State.ChannelInCoupling[channel] = (int)_Bits.ReadBit();
				_State.PhaseFlagsInUse = _State.ChannelMode == 2 ? (int)_Bits.ReadBit() : 0;
				var startSubband = (int)_Bits.ReadBits(4);
				var endSubband = (int)_Bits.ReadBits(4) + 3;
				if (startSubband >= endSubband) return FfmpegError.InvalidData;
				_State.StartFrequency[CouplingChannel] = startSubband * 12 + 37;
				_State.EndFrequency[CouplingChannel] = endSubband * 12 + 37;
				DecodeBandStructure(block, startSubband, endSubband);
			} else
			{
				for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
				{
					_State.ChannelInCoupling[channel] = 0;
					_State.FirstCouplingCoordinates[channel] = 1;
				}
				_State.FirstCouplingLeak = _State.IsEnhanced;
				_State.PhaseFlagsInUse = 0;
			}
			return 0;
		}

		private void DecodeBandStructure(int block, int startSubband, int endSubband)
		{
			var subbandCount = endSubband - startSubband;
			if (block == 0) Array.Copy(Ac3Tables.DefaultCouplingBandStructure, _State.CouplingBandStructure, Ac3Tables.DefaultCouplingBandStructure.Length);
			var structureOffset = startSubband + 1;
			if (_State.IsEnhanced == 0 || _Bits.ReadBit() != 0)
				for (var subband = 0; subband < subbandCount - 1; subband++) _State.CouplingBandStructure[structureOffset + subband] = (byte)_Bits.ReadBit();
			var bandCount = subbandCount;
			_State.CouplingBandSizes[0] = 12;
			var band = 0;
			for (var subband = 1; subband < subbandCount; subband++)
			{
				if (_State.CouplingBandStructure[structureOffset + subband - 1] != 0)
				{
					bandCount--;
					_State.CouplingBandSizes[band] += 12;
				} else _State.CouplingBandSizes[++band] = 12;
			}
			_State.NumberOfCouplingBands = bandCount;
		}

		private int DecodeCouplingCoordinates(int block)
		{
			var coordinatesExist = false;
			for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
			{
				if (_State.ChannelInCoupling[channel] != 0)
				{
					if (_State.IsEnhanced != 0 && _State.FirstCouplingCoordinates[channel] != 0 || _Bits.ReadBit() != 0)
					{
						_State.FirstCouplingCoordinates[channel] = 0;
						coordinatesExist = true;
						var masterCoordinate = 3 * (int)_Bits.ReadBits(2);
						for (var band = 0; band < _State.NumberOfCouplingBands; band++)
						{
							var exponent = (int)_Bits.ReadBits(4);
							var mantissa = (int)_Bits.ReadBits(4);
							_State.CouplingCoordinates[channel][band] = exponent == 15 ? mantissa << 22 : (mantissa + 16) << 21;
							_State.CouplingCoordinates[channel][band] >>= exponent + masterCoordinate;
						}
					} else if (block == 0) return FfmpegError.InvalidData;
				} else _State.FirstCouplingCoordinates[channel] = 1;
			}
			if (_State.ChannelMode == 2 && coordinatesExist)
				for (var band = 0; band < _State.NumberOfCouplingBands; band++) _State.PhaseFlags[band] = _State.PhaseFlagsInUse != 0 ? (int)_Bits.ReadBit() : 0;
			return 0;
		}

		private int DecodeExponents(int strategy, int groupCount, int absoluteExponent, sbyte[] exponents, int destinationOffset)
		{
			Span<int> grouped = stackalloc int[256];
			var groupedCount = 0;
			for (var group = 0; group < groupCount; group++)
			{
				var value = (int)_Bits.ReadBits(7);
				if (value >= 125) return FfmpegError.InvalidData;
				grouped[groupedCount++] = value / 25;
				grouped[groupedCount++] = value % 25 / 5;
				grouped[groupedCount++] = value % 25 % 5;
			}
			var previous = absoluteExponent;
			var groupSize = strategy + (strategy == 3 ? 1 : 0);
			var destination = destinationOffset;
			for (var index = 0; index < groupedCount; index++)
			{
				previous += grouped[index] - 2;
				if (previous < 0 || previous > 24) return FfmpegError.InvalidData;
				for (var repeat = 0; repeat < groupSize; repeat++) exponents[destination++] = (sbyte)previous;
			}
			return 0;
		}

		private int CalculateBitAllocation(Span<byte> stages, bool couplingInUse)
		{
			for (var channel = couplingInUse ? 0 : 1; channel <= _State.Channels; channel++)
			{
				if (stages[channel] > 2)
					Ac3BitAllocation.CalculatePowerSpectralDensity(_State.Exponents[channel], _State.StartFrequency[channel], _State.EndFrequency[channel],
						_State.PowerSpectralDensity[channel], _State.BandPowerSpectralDensity[channel]);
				if (stages[channel] > 1)
				{
					var result = Ac3BitAllocation.CalculateMask(ref _State.BitAllocationParameters, _State.BandPowerSpectralDensity[channel],
						_State.StartFrequency[channel], _State.EndFrequency[channel], _State.FastGain[channel], channel == _State.LowFrequencyEffectsChannel,
						_State.DeltaMode[channel], _State.DeltaSegmentCount[channel], _State.DeltaOffsets[channel], _State.DeltaLengths[channel],
						_State.DeltaValues[channel], _State.Mask[channel]);
					if (result < 0) return FfmpegError.InvalidData;
				}
				if (stages[channel] > 0)
					Ac3BitAllocation.CalculatePointers(_State.Mask[channel], _State.PowerSpectralDensity[channel], _State.StartFrequency[channel],
						_State.EndFrequency[channel], _State.SignalToNoiseOffset[channel], _State.BitAllocationParameters.Floor,
						_State.ChannelUsesAdaptiveHybridTransform[channel] != 0 ? Ac3Tables.EnhancedBitAllocationPointers : Ac3Tables.BitAllocationPointers,
						_State.BitAllocationPointers[channel]);
			}
			return 0;
		}

		private void DecodeTransformCoefficients(bool couplingInUse, int block)
		{
			var groups = default(MantissaGroups);
			var couplingDecoded = false;
			for (var channel = 1; channel <= _State.Channels; channel++)
			{
				DecodeTransformCoefficientsForChannel(channel, block, ref groups);
				var end = _State.EndFrequency[channel];
				if (_State.ChannelInCoupling[channel] != 0)
				{
					if (!couplingDecoded)
					{
						DecodeTransformCoefficientsForChannel(CouplingChannel, block, ref groups);
						CalculateCouplingCoefficients();
						couplingDecoded = true;
					}
					end = _State.EndFrequency[CouplingChannel];
				}
				for (var coefficient = end; coefficient < 256; coefficient++) _State.FixedCoefficients[channel][coefficient] = 0;
			}
			for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
				if (_State.DitherFlag[channel] == 0 && _State.ChannelInCoupling[channel] != 0)
					for (var coefficient = _State.StartFrequency[CouplingChannel]; coefficient < _State.EndFrequency[CouplingChannel]; coefficient++)
						if (_State.BitAllocationPointers[CouplingChannel][coefficient] == 0) _State.FixedCoefficients[channel][coefficient] = 0;
		}

		/// <summary>
		/// Decodes grouped mantissas and exponent-scaled transform coefficients for one AC-3 channel in bin order.
		/// </summary>
		private void DecodeTransformCoefficientsForChannel(int channel, int block, ref MantissaGroups groups)
		{
			if (_State.ChannelUsesAdaptiveHybridTransform[channel] != 0)
			{
				if (block == 0) DecodeAdaptiveHybridTransformChannel(channel);
				for (var frequency = _State.StartFrequency[channel]; frequency < _State.EndFrequency[channel]; frequency++)
					_State.FixedCoefficients[channel][frequency] = _State.PreMantissa[channel][frequency * 6 + block] >> _State.Exponents[channel][frequency];
				return;
			}
			var dither = channel == CouplingChannel || _State.DitherFlag[channel] != 0;
			for (var frequency = _State.StartFrequency[channel]; frequency < _State.EndFrequency[channel]; frequency++)
			{
				var pointer = _State.BitAllocationPointers[channel][frequency];
				int mantissa;
				switch (pointer)
				{
					case 0:
						mantissa = dither ? NextDitherMantissa() : 0;
						break;
					case 1:
						if (groups.Pointer1Count != 0)
						{
							groups.Pointer1Count--;
							mantissa = groups.Pointer1Count == 0 ? groups.Pointer1First : groups.Pointer1Second;
						}
						else
						{
							var bits = (int)_Bits.ReadBits(5);
							mantissa = Ac3MantissaTables.BitAllocationPointer1[bits, 0];
							groups.Pointer1Second = Ac3MantissaTables.BitAllocationPointer1[bits, 1];
							groups.Pointer1First = Ac3MantissaTables.BitAllocationPointer1[bits, 2];
							groups.Pointer1Count = 2;
						}
						break;
					case 2:
						if (groups.Pointer2Count != 0)
						{
							groups.Pointer2Count--;
							mantissa = groups.Pointer2Count == 0 ? groups.Pointer2First : groups.Pointer2Second;
						}
						else
						{
							var bits = (int)_Bits.ReadBits(7);
							mantissa = Ac3MantissaTables.BitAllocationPointer2[bits, 0];
							groups.Pointer2Second = Ac3MantissaTables.BitAllocationPointer2[bits, 1];
							groups.Pointer2First = Ac3MantissaTables.BitAllocationPointer2[bits, 2];
							groups.Pointer2Count = 2;
						}
						break;
					case 3:
						mantissa = Ac3MantissaTables.BitAllocationPointer3[_Bits.ReadBits(3)];
						break;
					case 4:
						if (groups.Pointer4Count != 0)
						{
							groups.Pointer4Count = 0;
							mantissa = groups.Pointer4;
						} else
						{
							var bits = (int)_Bits.ReadBits(7);
							mantissa = Ac3MantissaTables.BitAllocationPointer4[bits, 0];
							groups.Pointer4 = Ac3MantissaTables.BitAllocationPointer4[bits, 1];
							groups.Pointer4Count = 1;
						}
						break;
					case 5:
						mantissa = Ac3MantissaTables.BitAllocationPointer5[_Bits.ReadBits(4)];
						break;
					default:
						if (pointer > 15) pointer = 15;
						var bitCount = Ac3Tables.QuantizationBits[pointer];
						mantissa = unchecked((int)((uint)_Bits.ReadSignedBits(bitCount) << (24 - bitCount)));
						break;
				}
				_State.FixedCoefficients[channel][frequency] = mantissa >> _State.Exponents[channel][frequency];
			}
		}

		private void UpmixDelay()
		{
			switch (_State.ChannelMode)
			{
				case 0:
				case 2:
					Array.Copy(_State.Delay[0], _State.Delay[1], _State.Delay[0].Length);
					break;
				case 4:
					Array.Clear(_State.Delay[2], 0, _State.Delay[2].Length);
					break;
				case 6:
					Array.Clear(_State.Delay[3], 0, _State.Delay[3].Length);
					Array.Clear(_State.Delay[2], 0, _State.Delay[2].Length);
					break;
				case 3:
					Array.Copy(_State.Delay[1], _State.Delay[2], _State.Delay[1].Length);
					Array.Clear(_State.Delay[1], 0, _State.Delay[1].Length);
					break;
				case 5:
					Array.Clear(_State.Delay[3], 0, _State.Delay[3].Length);
					Array.Copy(_State.Delay[1], _State.Delay[2], _State.Delay[1].Length);
					Array.Clear(_State.Delay[1], 0, _State.Delay[1].Length);
					break;
				case 7:
					Array.Clear(_State.Delay[4], 0, _State.Delay[4].Length);
					Array.Clear(_State.Delay[3], 0, _State.Delay[3].Length);
					Array.Copy(_State.Delay[1], _State.Delay[2], _State.Delay[1].Length);
					Array.Clear(_State.Delay[1], 0, _State.Delay[1].Length);
					break;
			}
		}

		private void ConcealOutput(int synthesisOffset, int channelCount, int sampleCount)
		{
			for (var channel = 0; channel < channelCount; channel++)
			{
				var source = _State.PreviousOutput[synthesisOffset + channel];
				var destination = _State.Output[synthesisOffset + channel];
				for (var block = 0; block < sampleCount; block += Ac3Tables.BlockSize)
					Array.Copy(source, 0, destination, block, Ac3Tables.BlockSize);
			}
		}

		private void ConcealOutputBlock(int synthesisOffset, int channelCount, int block)
		{
			var destinationOffset = block * Ac3Tables.BlockSize;
			for (var channel = 0; channel < channelCount; channel++)
			{
				if (block == 0)
					Array.Copy(_State.PreviousOutput[synthesisOffset + channel], 0,
						_State.Output[synthesisOffset + channel], 0, Ac3Tables.BlockSize);
				else
					Array.Copy(_State.Output[synthesisOffset + channel], destinationOffset - Ac3Tables.BlockSize,
						_State.Output[synthesisOffset + channel], destinationOffset, Ac3Tables.BlockSize);
			}
		}

		private void SavePreviousOutput(int synthesisOffset, int channelCount, int sampleCount)
		{
			var sourceOffset = sampleCount - Ac3Tables.BlockSize;
			for (var channel = 0; channel < channelCount; channel++)
				Array.Copy(_State.Output[synthesisOffset + channel], sourceOffset,
					_State.PreviousOutput[synthesisOffset + channel], 0, Ac3Tables.BlockSize);
		}

		/// <summary>
		/// Decodes all six E-AC-3 AHT coefficient blocks together, including GAQ grouping, escape remapping, and the six-point IDCT.
		/// </summary>
		private void DecodeAdaptiveHybridTransformChannel(int channel)
		{
			var gainAdaptiveQuantizationMode = (int)_Bits.ReadBits(2);
			var endingPointer = gainAdaptiveQuantizationMode < 2 ? 12 : 17;
			var gainCount = 0;
			if (gainAdaptiveQuantizationMode == 1 || gainAdaptiveQuantizationMode == 2)
			{
				for (var bin = _State.StartFrequency[channel]; bin < _State.EndFrequency[channel]; bin++)
					if (_State.BitAllocationPointers[channel][bin] > 7 && _State.BitAllocationPointers[channel][bin] < endingPointer)
						_State.GainAdaptiveQuantizationGain[gainCount++] = (int)_Bits.ReadBit() << (gainAdaptiveQuantizationMode - 1);
			} else if (gainAdaptiveQuantizationMode == 3)
			{
				var groupCount = 2;
				for (var bin = _State.StartFrequency[channel]; bin < _State.EndFrequency[channel]; bin++)
				{
					if (_State.BitAllocationPointers[channel][bin] <= 7 || _State.BitAllocationPointers[channel][bin] >= 17) continue;
					if (groupCount++ == 2)
					{
						var groupCode = (int)_Bits.ReadBits(5);
						if (groupCode > 26) groupCode = 26;
						_State.GainAdaptiveQuantizationGain[gainCount++] = Ac3Tables.UngroupThreeInFiveBits[groupCode, 0];
						_State.GainAdaptiveQuantizationGain[gainCount++] = Ac3Tables.UngroupThreeInFiveBits[groupCode, 1];
						_State.GainAdaptiveQuantizationGain[gainCount++] = Ac3Tables.UngroupThreeInFiveBits[groupCode, 2];
						groupCount = 0;
					}
				}
			}

			gainCount = 0;
			for (var bin = _State.StartFrequency[channel]; bin < _State.EndFrequency[channel]; bin++)
			{
				var pointer = _State.BitAllocationPointers[channel][bin];
				var bitCount = Ac3Tables.BitsPerEnhancedBitAllocationPointer[pointer];
				var preMantissaOffset = bin * 6;
				if (pointer == 0)
				{
					for (var block = 0; block < 6; block++) _State.PreMantissa[channel][preMantissaOffset + block] = (int)(NextDitherValue() & 0x7fffff) - 0x400000;
				} else if (pointer < 8)
				{
					var vector = (int)_Bits.ReadBits(bitCount);
					for (var block = 0; block < 6; block++) _State.PreMantissa[channel][preMantissaOffset + block] = ReadVectorQuantization(pointer, vector, block) * (1 << 8);
				} else
				{
					var logarithmicGain = gainAdaptiveQuantizationMode != 0 && pointer < endingPointer ? _State.GainAdaptiveQuantizationGain[gainCount++] : 0;
					var gainBits = bitCount - logarithmicGain;
					for (var block = 0; block < 6; block++)
					{
						var mantissa = _Bits.ReadSignedBits(gainBits);
						if (logarithmicGain != 0 && mantissa == -(1 << (gainBits - 1)))
						{
							var mantissaBits = bitCount - (2 - logarithmicGain);
							mantissa = _Bits.ReadSignedBits(mantissaBits);
							mantissa = unchecked((int)((uint)mantissa << (23 - (mantissaBits - 1))));
							var bias = mantissa >= 0 ? 1 << (23 - logarithmicGain) : Ac3Tables.GainAdaptiveQuantizationRemap24B[pointer - 8, logarithmicGain - 1] * (1 << 8);
							mantissa = unchecked(mantissa + (int)((Ac3Tables.GainAdaptiveQuantizationRemap24A[pointer - 8, logarithmicGain - 1] * (long)mantissa) >> 15) + bias);
						} else
						{
							mantissa = unchecked(mantissa * (1 << (24 - bitCount)));
							if (logarithmicGain == 0)
								mantissa = unchecked(mantissa + (int)((Ac3Tables.GainAdaptiveQuantizationRemap1[pointer - 8] * (long)mantissa) >> 15));
						}
						_State.PreMantissa[channel][preMantissaOffset + block] = mantissa;
					}
				}
				ApplySixPointInverseTransform(_State.PreMantissa[channel], preMantissaOffset);
			}
		}

		private static int ReadVectorQuantization(int pointer, int vector, int block)
		{
			switch (pointer)
			{
				case 1: return Ac3Tables.VectorQuantization1[vector, block];
				case 2: return Ac3Tables.VectorQuantization2[vector, block];
				case 3: return Ac3Tables.VectorQuantization3[vector, block];
				case 4: return Ac3Tables.VectorQuantization4[vector, block];
				case 5: return Ac3Tables.VectorQuantization5[vector, block];
				case 6: return Ac3Tables.VectorQuantization6[vector, block];
				default: return Ac3Tables.VectorQuantization7[vector, block];
			}
		}

		private static void ApplySixPointInverseTransform(int[] values, int offset)
		{
			var odd1 = values[offset + 1] - values[offset + 3] - values[offset + 5];
			var even2 = (int)((values[offset + 2] * 10273905L) >> 23);
			var temporary = (int)((values[offset + 4] * 11863283L) >> 23);
			var odd0 = (int)(((values[offset + 1] + values[offset + 5]) * 3070444L) >> 23);
			var even0 = values[offset] + (temporary >> 1);
			var even1 = values[offset] - temporary;
			temporary = even0;
			even0 = temporary + even2;
			even2 = temporary - even2;
			temporary = odd0;
			odd0 = temporary + values[offset + 1] + values[offset + 3];
			var odd2 = temporary + values[offset + 5] - values[offset + 3];
			values[offset] = even0 + odd0;
			values[offset + 1] = even1 + odd1;
			values[offset + 2] = even2 + odd2;
			values[offset + 3] = even2 - odd2;
			values[offset + 4] = even1 - odd1;
			values[offset + 5] = even0 - odd0;
		}

		private void CalculateCouplingCoefficients()
		{
			var bin = _State.StartFrequency[CouplingChannel];
			for (var band = 0; band < _State.NumberOfCouplingBands; band++)
			{
				var start = bin;
				var end = bin + _State.CouplingBandSizes[band];
				for (var channel = 1; channel <= _State.FullBandwidthChannels; channel++)
				{
					if (_State.ChannelInCoupling[channel] == 0) continue;
					var coordinate = _State.CouplingCoordinates[channel][band] << 5;
					for (bin = start; bin < end; bin++)
					{
						var value = unchecked(_State.FixedCoefficients[CouplingChannel][bin] * 16);
						_State.FixedCoefficients[channel][bin] = (int)(((long)value * coordinate) >> 32);
					}
					if (channel == 2 && _State.PhaseFlags[band] != 0)
						for (bin = start; bin < end; bin++) _State.FixedCoefficients[2][bin] = -_State.FixedCoefficients[2][bin];
				}
				bin = end;
			}
		}

		private void ApplyRematrixing()
		{
			var end = Math.Min(_State.EndFrequency[1], _State.EndFrequency[2]);
			for (var band = 0; band < _State.NumberOfRematrixingBands; band++)
			{
				if (_State.RematrixingFlags[band] == 0) continue;
				var bandEnd = Math.Min(end, Ac3Tables.RematrixBands[band + 1]);
				for (var coefficient = Ac3Tables.RematrixBands[band]; coefficient < bandEnd; coefficient++)
				{
					var first = _State.FixedCoefficients[1][coefficient];
					_State.FixedCoefficients[1][coefficient] += _State.FixedCoefficients[2][coefficient];
					_State.FixedCoefficients[2][coefficient] = first - _State.FixedCoefficients[2][coefficient];
				}
			}
		}

		private void ApplyImdct(int block, int synthesisOffset)
		{
			for (var channel = 1; channel <= _State.Channels; channel++)
			{
				var outputChannel = GetOutputChannel(channel - 1);
				var destination = _State.Output[synthesisOffset + outputChannel];
				var destinationOffset = block * Ac3Tables.BlockSize;
				var delay = _State.Delay[synthesisOffset + channel - 1];
				if (_State.BlockSwitch[channel] != 0)
				{
					for (var index = 0; index < 128; index++) _State.ShortTransformInput[index] = _State.TransformCoefficients[channel][2 * index];
					_Mdct128.Transform(_State.ShortTransformInput, _State.ShortTransformOutput);
					VectorMultiplyWindow(destination, destinationOffset, delay, _State.ShortTransformOutput, _State.Window, 128);
					for (var index = 0; index < 128; index++) _State.ShortTransformInput[index] = _State.TransformCoefficients[channel][2 * index + 1];
					_Mdct128.Transform(_State.ShortTransformInput, delay.AsSpan(0, 128));
				} else
				{
					_Mdct256.Transform(_State.TransformCoefficients[channel], _State.TemporaryOutput);
					VectorMultiplyWindow(destination, destinationOffset, delay, _State.TemporaryOutput, _State.Window, 128);
					Array.Copy(_State.TemporaryOutput, 128, delay, 0, 128);
				}
			}
		}

		private int GetOutputChannel(int internalChannel)
		{
			var row = _State.ChannelMode * 2 + _State.LowFrequencyEffects;
			for (var codedChannel = 0; codedChannel < _State.Channels; codedChannel++)
				if (Ac3Tables.ChannelMap[row, codedChannel] == internalChannel) return codedChannel;
			return internalChannel;
		}

		private void BuildDependentOutputMap(ulong independentLayout, int outputChannels)
		{
			ReadOnlySpan<byte> directlyMapped = stackalloc byte[] { 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
			ReadOnlySpan<ulong> locations = stackalloc ulong[]
			{
				1UL << 0, 1UL << 2, 1UL << 1, 1UL << 9, 1UL << 10,
				(1UL << 6) | (1UL << 7), (1UL << 4) | (1UL << 5), 1UL << 8, 1UL << 11,
				(1UL << 33) | (1UL << 34), (1UL << 31) | (1UL << 32), (1UL << 12) | (1UL << 14),
				1UL << 13, (1UL << 15) | (1UL << 17), 1UL << 35, 1UL << 3
			};
			var combinedLayout = independentLayout | GetDependentChannelLayout(_State.ChannelMap);
			var row = _State.ChannelMode * 2 + _State.LowFrequencyEffects;
			var extend = 0;
			for (var location = 0; location < 16; location++)
			{
				if ((_State.ChannelMap & (1 << (15 - location))) == 0) continue;
				if (directlyMapped[location] != 0)
				{
					var channelBit = TrailingZeroCount(locations[location]);
					var index = ChannelIndex(combinedLayout, channelBit);
					if (index >= 0 && index < outputChannels && extend < _State.Channels)
						_State.OutputMap[index] = MaximumChannels + Ac3Tables.ChannelMap[row, extend++];
				} else
				{
					for (var channelBit = 0; channelBit < 64; channelBit++)
					{
						if ((locations[location] & (1UL << channelBit)) == 0) continue;
						var index = ChannelIndex(combinedLayout, channelBit);
						if (index >= 0 && index < outputChannels && extend < _State.Channels)
							_State.OutputMap[index] = MaximumChannels + Ac3Tables.ChannelMap[row, extend++];
					}
				}
			}
		}

		private static ulong GetChannelLayout(int channelMode, int lowFrequencyEffects)
		{
			var layout = channelMode switch
			{
				0 => (1UL << 0) | (1UL << 1), 1 => 1UL << 2, 2 => (1UL << 0) | (1UL << 1),
				3 => (1UL << 0) | (1UL << 1) | (1UL << 2), 4 => (1UL << 0) | (1UL << 1) | (1UL << 8),
				5 => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 8),
				6 => (1UL << 0) | (1UL << 1) | (1UL << 9) | (1UL << 10),
				_ => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 9) | (1UL << 10)
			};
			return lowFrequencyEffects != 0 ? layout | (1UL << 3) : layout;
		}

		private static ulong GetDependentChannelLayout(int channelMap)
		{
			ReadOnlySpan<ulong> locations = stackalloc ulong[]
			{
				1UL << 0, 1UL << 2, 1UL << 1, 1UL << 9, 1UL << 10, (1UL << 6) | (1UL << 7),
				(1UL << 4) | (1UL << 5), 1UL << 8, 1UL << 11, (1UL << 33) | (1UL << 34),
				(1UL << 31) | (1UL << 32), (1UL << 12) | (1UL << 14), 1UL << 13,
				(1UL << 15) | (1UL << 17), 1UL << 35, 1UL << 3
			};
			var result = 0UL;
			for (var index = 0; index < 16; index++) if ((channelMap & (1 << (15 - index))) != 0) result |= locations[index];
			return result;
		}

		private static int ChannelIndex(ulong layout, int channelBit)
		{
			if ((layout & (1UL << channelBit)) == 0) return -1;
			return CountBits(layout & ((1UL << channelBit) - 1));
		}

		private static int CountBits(ulong value)
		{
			var result = 0;
			while (value != 0) { value &= value - 1; result++; }
			return result;
		}

		private static int TrailingZeroCount(ulong value)
		{
			var result = 0;
			while ((value & 1) == 0) { value >>= 1; result++; }
			return result;
		}

		private int NextDitherMantissa()
		{
			var value = NextDitherValue();
			return unchecked((int)((((value >> 8) * 181) >> 8) - 5931008));
		}

		private uint NextDitherValue()
		{
			var index = _State.DitherIndex;
			var value = _State.DitherState[index & 63] = unchecked(_State.DitherState[(index - 24) & 63] + _State.DitherState[(index - 55) & 63]);
			_State.DitherIndex = unchecked(index + 1);
			return value;
		}

		private void WriteOutput(Span<byte> output, int sampleCount, int channelCount)
		{
			var position = 0;
			for (var channel = 0; channel < channelCount; channel++)
				for (var sample = 0; sample < sampleCount; sample++)
				{
					BinaryPrimitives.WriteInt32LittleEndian(output.Slice(position, sizeof(float)), BitConverter.SingleToInt32Bits(_State.Output[_State.OutputMap[channel]][sample]));
					position += sizeof(float);
				}
		}

		private int GetCurrentFrameSize()
		{
			return _State.FrameSize;
		}

		private static int FindSyncWord(byte[] data, int offset, int length)
		{
			for (var index = 1; index < length - 1; index += 2)
			{
				var value = data[offset + index];
				if (value != 0x77 && value != 0x0b) continue;
				if ((value ^ data[offset + index - 1]) == (0x77 ^ 0x0b)) return index - 1;
				if ((value ^ data[offset + index + 1]) == (0x77 ^ 0x0b)) return index;
			}
			return FfmpegError.InvalidData;
		}

		private static void VectorMultiplyWindow(float[] destination, int destinationOffset, float[] source0, float[] source1, float[] window, int length)
		{
			for (int left = -length, right = length - 1; left < 0; left++, right--)
			{
				var first = source0[length + left];
				var second = source1[right];
				var windowLeft = window[length + left];
				var windowRight = window[length + right];
				destination[destinationOffset + length + left] = first * windowRight - second * windowLeft;
				destination[destinationOffset + length + right] = first * windowLeft + second * windowRight;
			}
		}

		private static float[] CreateDynamicRangeTable()
		{
			var table = new float[256];
			for (var index = 0; index < table.Length; index++)
			{
				var exponent = (index >> 5) - ((index >> 7) << 3) - 5;
				table[index] = MathF.ScaleB((index & 0x1f) | 0x20, exponent);
			}
			return table;
		}

		private static int IntegerLog2(int value)
		{
			var result = 0;
			while ((value >>= 1) != 0) result++;
			return result;
		}

		private struct MantissaGroups
		{
			public int Pointer1First;
			public int Pointer1Second;
			public int Pointer2First;
			public int Pointer2Second;
			public int Pointer4;
			public int Pointer1Count;
			public int Pointer2Count;
			public int Pointer4Count;
		}
	}
}
