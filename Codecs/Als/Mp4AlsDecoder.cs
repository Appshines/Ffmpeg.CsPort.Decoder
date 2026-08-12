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
 * PORT-NOTE: 1:1 translation. Performance-motivated, semantics-preserving transformations
 * applied (see repository history); bit-exactness remains verified by the conformance tests.
 */
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Als
{
	/// <summary>
	/// Ports FFmpeg's integer MPEG-4 ALS decoder, including Rice residuals, adaptive LPC,
	/// block switching, joint stereo, shifted LSBs, and long-term prediction.
	/// </summary>
	public sealed class Mp4AlsDecoder
	{
		private const uint AlsIdentifier = 0x414c5300;
		private const int MaximumChannels = 32;
		private const int SeekCheckpointInterval = 32;
		private readonly BitReader _Reader = new BitReader();
		private readonly int _SampleRate;
		private readonly int _Channels;
		private readonly uint _TotalSamples;
		private readonly int _BitsPerSample;
		private readonly int _Resolution;
		private readonly int _FrameLength;
		private readonly int _RandomAccessDistance;
		private readonly int _RandomAccessFlag;
		private readonly bool _AdaptiveOrder;
		private readonly int _CoefficientTable;
		private readonly bool _LongTermPrediction;
		private readonly int _MaximumOrder;
		private readonly int _BlockSwitching;
		private readonly bool _SubBlockPartitioning;
		private readonly bool _JointStereo;
		private readonly int _MaximumRiceParameter;
		private readonly int _LongTermLagLength;
		private readonly int[][] _RawSamples;
		private readonly int[] _ChannelPositions;
		private readonly int[] _BlockSizes = new int[32];
		private readonly BlockState _FirstBlock;
		private readonly BlockState _SecondBlock;
		private readonly List<SeekCheckpoint> _SeekCheckpoints = new List<SeekCheckpoint>();
		private long _FrameId;

		/// <summary>Creates the reusable integer ALS decoder state from the validated AudioSpecificConfig.</summary>
		private Mp4AlsDecoder(
			int sampleRate,
			int channels,
			uint totalSamples,
			int resolution,
			int frameLength,
			int randomAccessDistance,
			int randomAccessFlag,
			bool adaptiveOrder,
			int coefficientTable,
			bool longTermPrediction,
			int maximumOrder,
			int blockSwitching,
			bool subBlockPartitioning,
			bool jointStereo,
			int[] channelPositions)
		{
			_SampleRate = sampleRate;
			_Channels = channels;
			_TotalSamples = totalSamples;
			_Resolution = resolution;
			_BitsPerSample = (resolution + 1) * 8;
			_FrameLength = frameLength;
			_RandomAccessDistance = randomAccessDistance;
			_RandomAccessFlag = randomAccessFlag;
			_AdaptiveOrder = adaptiveOrder;
			_CoefficientTable = coefficientTable;
			_LongTermPrediction = longTermPrediction;
			_MaximumOrder = maximumOrder;
			_BlockSwitching = blockSwitching;
			_SubBlockPartitioning = subBlockPartitioning;
			_JointStereo = jointStereo;
			_MaximumRiceParameter = resolution > 1 ? 31 : 15;
			_LongTermLagLength = 8 + (sampleRate >= 96000 ? 1 : 0) + (sampleRate >= 192000 ? 1 : 0);
			_ChannelPositions = channelPositions;
			_RawSamples = new int[channels][];
			for (var channel = 0; channel < channels; channel++)
				_RawSamples[channel] = new int[checked(frameLength + maximumOrder)];
			_FirstBlock = new BlockState(maximumOrder);
			_SecondBlock = new BlockState(maximumOrder);
		}

		public int SampleRate => _SampleRate;
		public int Channels => _Channels;
		public int BitsPerSample => _BitsPerSample;
		public int MaximumSamplesPerFrame => _FrameLength;
		public long TotalSamples => _TotalSamples == uint.MaxValue ? -1 : _TotalSamples;

		/// <summary>
		/// Restores the closest cached predictor-history checkpoint and decodes at most one checkpoint interval to the target ALS frame.
		/// </summary>
		public bool TryPrepareSeek(long a_SampleIndex, byte[] a_Packet, int a_PacketLength, Span<byte> a_ScratchOutput,
			out int a_PacketOffset, out long a_ActualSampleIndex)
		{
			a_PacketOffset = 0;
			a_ActualSampleIndex = 0;
			if (a_SampleIndex < 0 || a_Packet == null || a_PacketLength < 0 || a_PacketLength > a_Packet.Length)
				return false;
			var l_TargetFrame = a_SampleIndex / _FrameLength;
			if (_TotalSamples != uint.MaxValue)
				l_TargetFrame = Math.Min(l_TargetFrame, Math.Max(0L, (_TotalSamples - 1L) / _FrameLength));
			var l_Checkpoint = FindCheckpoint(l_TargetFrame);
			if (l_Checkpoint == null)
				return false;
			RestoreCheckpoint(l_Checkpoint);
			a_PacketOffset = l_Checkpoint.PacketOffset;
			while (_FrameId < l_TargetFrame)
			{
				var l_Consumed = DecodeFrame(a_Packet, a_PacketOffset, a_PacketLength - a_PacketOffset, a_ScratchOutput, out var l_Frame);
				if (l_Consumed <= 0 || l_Frame.NumberOfSamples <= 0 || l_Consumed > a_PacketLength - a_PacketOffset)
					return false;
				a_PacketOffset += l_Consumed;
			}
			a_ActualSampleIndex = checked(l_TargetFrame * _FrameLength);
			return true;
		}

		/// <summary>Reads an MPEG-4 AudioSpecificConfig and creates the matching integer ALS decoder state.</summary>
		public static int Initialize(byte[] a_ExtraData, int a_FallbackSampleRate, int a_Channels, out Mp4AlsDecoder a_Decoder)
		{
			a_Decoder = null;
			if (a_ExtraData == null || a_Channels < 1 || a_Channels > MaximumChannels)
				return FfmpegError.InvalidArgument;
			var l_IdentifierOffset = FindIdentifier(a_ExtraData);
			if (l_IdentifierOffset < 0)
				return FfmpegError.InvalidData;
			var l_Reader = new BitReader();
			var l_ConfigOffset = l_IdentifierOffset + 4;
			if (l_Reader.Initialize(a_ExtraData, l_ConfigOffset, checked((a_ExtraData.Length - l_ConfigOffset) * 8)) < 0 || l_Reader.BitsLeft < 152)
				return FfmpegError.InvalidData;

			var l_ConfiguredSampleRate = checked((int)l_Reader.ReadBitsLong(32));
			var l_TotalSamples = l_Reader.ReadBitsLong(32);
			l_Reader.SkipBits(16 + 3);
			var l_Resolution = (int)l_Reader.ReadBits(3);
			var l_Floating = l_Reader.ReadBit() != 0;
			l_Reader.SkipBits(1);
			var l_FrameLength = checked((int)l_Reader.ReadBits(16) + 1);
			var l_RandomAccessDistance = (int)l_Reader.ReadBits(8);
			var l_RandomAccessFlag = (int)l_Reader.ReadBits(2);
			var l_AdaptiveOrder = l_Reader.ReadBit() != 0;
			var l_CoefficientTable = (int)l_Reader.ReadBits(2);
			var l_LongTermPrediction = l_Reader.ReadBit() != 0;
			var l_MaximumOrder = (int)l_Reader.ReadBits(10);
			var l_BlockSwitching = (int)l_Reader.ReadBits(2);
			var l_Bgmc = l_Reader.ReadBit() != 0;
			var l_SubBlockPartitioning = l_Reader.ReadBit() != 0;
			var l_JointStereo = l_Reader.ReadBit() != 0;
			var l_MultiChannelCoding = l_Reader.ReadBit() != 0;
			var l_HasChannelConfiguration = l_Reader.ReadBit() != 0;
			var l_HasChannelSorting = l_Reader.ReadBit() != 0;
			var l_CrcEnabled = l_Reader.ReadBit() != 0;
			var l_RlsLms = l_Reader.ReadBit() != 0;
			l_Reader.SkipBits(5 + 1);

			if (l_Floating || l_Bgmc || l_MultiChannelCoding || l_RlsLms)
				return FfmpegError.PatchWelcome;
			if (l_ConfiguredSampleRate <= 0)
				l_ConfiguredSampleRate = a_FallbackSampleRate;
			if (l_ConfiguredSampleRate <= 0 || l_FrameLength <= 0 || l_MaximumOrder > 1023 ||
				l_CoefficientTable > 3 || l_RandomAccessFlag > 2)
				return FfmpegError.InvalidData;

			if (l_HasChannelConfiguration)
			{
				if (l_Reader.BitsLeft < 16) return FfmpegError.InvalidData;
				l_Reader.SkipBits(16);
			}
			var l_ChannelPositions = new int[a_Channels];
			for (var channel = 0; channel < a_Channels; channel++) l_ChannelPositions[channel] = channel;
			if (l_HasChannelSorting && a_Channels > 1)
			{
				var l_PositionBits = CeilingLog2(a_Channels);
				if (l_Reader.BitsLeft < a_Channels * l_PositionBits) return FfmpegError.InvalidData;
				Array.Fill(l_ChannelPositions, -1);
				for (var channel = 0; channel < a_Channels; channel++)
				{
					var l_Position = (int)l_Reader.ReadBitsOrZero(l_PositionBits);
					if (l_Position >= a_Channels || l_ChannelPositions[l_Position] >= 0) return FfmpegError.InvalidData;
					l_ChannelPositions[l_Position] = channel;
				}
				l_Reader.Align();
			}
			if (l_Reader.BitsLeft < 64) return FfmpegError.InvalidData;
			var l_HeaderSize = l_Reader.ReadBitsLong(32);
			var l_TrailerSize = l_Reader.ReadBitsLong(32);
			if (l_HeaderSize == uint.MaxValue) l_HeaderSize = 0;
			if (l_TrailerSize == uint.MaxValue) l_TrailerSize = 0;
			var l_HeaderTrailerBits = ((ulong)l_HeaderSize + l_TrailerSize) * 8UL;
			if (l_HeaderTrailerBits > (ulong)Math.Max(0, l_Reader.BitsLeft) || l_HeaderTrailerBits > int.MaxValue)
				return FfmpegError.InvalidData;
			l_Reader.SkipBits((int)l_HeaderTrailerBits);
			if (l_CrcEnabled)
			{
				if (l_Reader.BitsLeft < 32) return FfmpegError.InvalidData;
				l_Reader.SkipBits(32);
			}

			a_Decoder = new Mp4AlsDecoder(l_ConfiguredSampleRate, a_Channels, l_TotalSamples, l_Resolution,
				l_FrameLength, l_RandomAccessDistance, l_RandomAccessFlag, l_AdaptiveOrder, l_CoefficientTable,
				l_LongTermPrediction, l_MaximumOrder, l_BlockSwitching, l_SubBlockPartitioning, l_JointStereo,
				l_ChannelPositions);
			return 0;
		}

		/// <summary>Decodes one ALS access unit from a possibly larger MP4 packet and reports the consumed byte count.</summary>
		public int DecodeFrame(byte[] a_Packet, int a_PacketOffset, int a_PacketLength, Span<byte> a_Output, out AudioFrameInfo a_Frame)
		{
			a_Frame = default;
			if (a_Packet == null || a_PacketOffset < 0 || a_PacketLength < 0 || a_PacketLength > a_Packet.Length - a_PacketOffset ||
				_Reader.Initialize(a_Packet, a_PacketOffset, checked(a_PacketLength * 8)) < 0)
				return FfmpegError.InvalidArgument;
			var l_RandomAccessFrame = _RandomAccessDistance != 0 && _FrameId % _RandomAccessDistance == 0;
			var l_FrameStart = _FrameId * (long)_FrameLength;
			var l_CurrentFrameLength = _TotalSamples == uint.MaxValue
				? _FrameLength
				: checked((int)Math.Min(Math.Max(0L, _TotalSamples - l_FrameStart), _FrameLength));
			if (l_CurrentFrameLength <= 0)
				return FfmpegError.EndOfFile;
			CaptureCheckpoint(a_PacketOffset);
			var l_Result = ReadFrameData(l_CurrentFrameLength, l_RandomAccessFrame);
			if (l_Result < 0 || _Reader.BitsLeft < 0)
				return l_Result < 0 ? l_Result : FfmpegError.InvalidData;
			_FrameId++;

			var l_BytesPerSample = _BitsPerSample <= 16 ? 2 : 4;
			var l_OutputSize = checked(l_CurrentFrameLength * _Channels * l_BytesPerSample);
			if (a_Output.Length < l_OutputSize)
				return FfmpegError.InvalidArgument;
			WriteOutput(a_Output.Slice(0, l_OutputSize), l_CurrentFrameLength, l_BytesPerSample);
			a_Frame = new AudioFrameInfo(l_CurrentFrameLength, _Channels,
				l_BytesPerSample == 2 ? AudioSampleFormat.Signed16 : AudioSampleFormat.Signed32,
				1, l_OutputSize, l_OutputSize);
			return (_Reader.Position + 7) >> 3;
		}

		/// <summary>Reads the per-channel block layout and reconstructs all integer samples for one ALS frame.</summary>
		private int ReadFrameData(int a_FrameLength, bool a_RandomAccessFrame)
		{
			if (_RandomAccessFlag == 1 && a_RandomAccessFrame)
			{
				if (_Reader.BitsLeft < 32) return FfmpegError.InvalidData;
				_Reader.SkipBits(32);
			}
			var l_BlockInfo = 0U;
			for (var channel = 0; channel < _Channels; channel++)
			{
				var l_BlockCount = GetBlockSizes(a_FrameLength, _BlockSizes, ref l_BlockInfo);
				if (l_BlockCount <= 0) return FfmpegError.InvalidData;
				var l_PairChannels = _JointStereo && channel + 1 < _Channels;
				var l_Result = l_PairChannels
					? DecodeChannelPair(channel, _BlockSizes, l_BlockCount, a_RandomAccessFrame)
					: DecodeIndependentChannel(channel, _BlockSizes, l_BlockCount, a_RandomAccessFrame);
				if (l_Result < 0) return l_Result;
				CopyFrameHistory(channel);
				if (l_PairChannels)
				{
					channel++;
					CopyFrameHistory(channel);
				}
			}
			return 0;
		}

		private int DecodeIndependentChannel(int a_Channel, int[] a_BlockSizes, int a_BlockCount, bool a_RandomAccessFrame)
		{
			var l_Offset = 0;
			for (var block = 0; block < a_BlockCount; block++)
			{
				var l_Result = ReadDecodeBlock(_FirstBlock, a_Channel, -1, l_Offset, a_BlockSizes[block], a_RandomAccessFrame && block == 0);
				if (l_Result < 0) return l_Result;
				l_Offset += a_BlockSizes[block];
			}
			return 0;
		}

		private int DecodeChannelPair(int a_Channel, int[] a_BlockSizes, int a_BlockCount, bool a_RandomAccessFrame)
		{
			var l_Offset = 0;
			for (var block = 0; block < a_BlockCount; block++)
			{
				var l_RandomAccessBlock = a_RandomAccessFrame && block == 0;
				var l_Result = ReadDecodeBlock(_FirstBlock, a_Channel, a_Channel + 1, l_Offset, a_BlockSizes[block], l_RandomAccessBlock);
				if (l_Result < 0) return l_Result;
				l_Result = ReadDecodeBlock(_SecondBlock, a_Channel + 1, a_Channel, l_Offset, a_BlockSizes[block], l_RandomAccessBlock);
				if (l_Result < 0) return l_Result;
				if (_FirstBlock.JointStereo)
				{
					for (var sample = 0; sample < a_BlockSizes[block]; sample++)
					{
						var l_Index = _MaximumOrder + l_Offset + sample;
						_RawSamples[a_Channel][l_Index] = unchecked(_RawSamples[a_Channel + 1][l_Index] - _RawSamples[a_Channel][l_Index]);
					}
				} else if (_SecondBlock.JointStereo)
				{
					for (var sample = 0; sample < a_BlockSizes[block]; sample++)
					{
						var l_Index = _MaximumOrder + l_Offset + sample;
						_RawSamples[a_Channel + 1][l_Index] = unchecked(_RawSamples[a_Channel + 1][l_Index] + _RawSamples[a_Channel][l_Index]);
					}
				}
				l_Offset += a_BlockSizes[block];
			}
			return 0;
		}

		private int ReadDecodeBlock(BlockState a_State, int a_Channel, int a_OtherChannel, int a_Offset, int a_BlockLength, bool a_RandomAccessBlock)
		{
			if (a_BlockLength <= 0 || _Reader.BitsLeft < 7) return FfmpegError.InvalidData;
			a_State.Reset(a_Channel, a_OtherChannel, a_Offset, a_BlockLength, a_RandomAccessBlock);
			var l_Result = _Reader.ReadBit() != 0 ? ReadVariableBlock(a_State) : ReadConstantBlock(a_State);
			_Reader.Align();
			if (l_Result < 0) return l_Result;
			return a_State.IsConstant ? DecodeConstantBlock(a_State) : DecodeVariableBlock(a_State);
		}

		private int ReadConstantBlock(BlockState a_State)
		{
			var l_HasValue = _Reader.ReadBit() != 0;
			a_State.JointStereo = _Reader.ReadBit() != 0;
			_Reader.SkipBits(5);
			a_State.ConstantValue = l_HasValue ? _Reader.ReadSignedBits(_BitsPerSample) : 0;
			a_State.IsConstant = true;
			return 0;
		}

		/// <summary>Reads Rice parameters, predictor coefficients, optional LTP data, and residuals for one variable block.</summary>
		private int ReadVariableBlock(BlockState a_State)
		{
			a_State.JointStereo = _Reader.ReadBit() != 0;
			var l_Log2SubBlocks = !_SubBlockPartitioning ? 0 : 2 * (int)_Reader.ReadBit();
			var l_SubBlockCount = 1 << l_Log2SubBlocks;
			if ((a_State.BlockLength & (l_SubBlockCount - 1)) != 0) return FfmpegError.InvalidData;
			var l_SubBlockLength = a_State.BlockLength >> l_Log2SubBlocks;
			Span<int> l_RiceParameters = stackalloc int[8];
			l_RiceParameters[0] = (int)_Reader.ReadBits(4 + (_Resolution > 1 ? 1 : 0));
			for (var subBlock = 1; subBlock < l_SubBlockCount; subBlock++)
			{
				if (!TryDecodeRice(0, out var l_Delta)) return FfmpegError.InvalidData;
				l_RiceParameters[subBlock] = l_RiceParameters[subBlock - 1] + l_Delta;
				if ((uint)l_RiceParameters[subBlock] > 32U) return FfmpegError.InvalidData;
			}
			if (_Reader.ReadBit() != 0) a_State.ShiftLeastSignificantBits = (int)_Reader.ReadBits(4) + 1;
			a_State.StorePreviousSamples = a_State.JointStereo && a_State.OtherChannel >= 0 || a_State.ShiftLeastSignificantBits != 0;

			if (_AdaptiveOrder && _MaximumOrder != 0)
			{
				var l_MaximumAdaptiveOrder = Math.Clamp((a_State.BlockLength >> 3) - 1, 2, _MaximumOrder + 1);
				a_State.OptimalOrder = (int)_Reader.ReadBitsOrZero(CeilingLog2(l_MaximumAdaptiveOrder));
				if (a_State.OptimalOrder > _MaximumOrder) return FfmpegError.InvalidData;
			} else a_State.OptimalOrder = _MaximumOrder;
			var l_Result = ReadPredictorCoefficients(a_State);
			if (l_Result < 0) return l_Result;
			l_Result = ReadLongTermPrediction(a_State);
			if (l_Result < 0) return l_Result;

			var l_Start = 0;
			var l_Data = _RawSamples[a_State.Channel];
			var l_Base = _MaximumOrder + a_State.Offset;
			if (a_State.RandomAccessBlock)
			{
				l_Start = Math.Min(a_State.OptimalOrder, 3);
				if (l_SubBlockLength <= l_Start) return FfmpegError.PatchWelcome;
				if (a_State.OptimalOrder > 0 && !TryDecodeRice(_BitsPerSample - 4, out l_Data[l_Base])) return FfmpegError.InvalidData;
				if (a_State.OptimalOrder > 1 && !TryDecodeRice(Math.Min(l_RiceParameters[0] + 3, _MaximumRiceParameter), out l_Data[l_Base + 1])) return FfmpegError.InvalidData;
				if (a_State.OptimalOrder > 2 && !TryDecodeRice(Math.Min(l_RiceParameters[0] + 1, _MaximumRiceParameter), out l_Data[l_Base + 2])) return FfmpegError.InvalidData;
			}
			var l_Current = l_Base + l_Start;
			for (var subBlock = 0; subBlock < l_SubBlockCount; subBlock++)
			{
				var l_SubBlockStart = subBlock == 0 ? l_Start : 0;
				for (var sample = l_SubBlockStart; sample < l_SubBlockLength; sample++)
				{
					if (!TryDecodeRice(l_RiceParameters[subBlock], out l_Data[l_Current++])) return FfmpegError.InvalidData;
				}
			}
			return 0;
		}

		/// <summary>Decodes and scales the PARCOR coefficients used to reconstruct an adaptive LPC block.</summary>
		private int ReadPredictorCoefficients(BlockState a_State)
		{
			var l_Order = a_State.OptimalOrder;
			if (l_Order == 0) return 0;
			var l_AddBase = 1;
			if (_CoefficientTable == 3)
			{
				l_AddBase = 0x7f;
				a_State.QuantizedCoefficients[0] = 32 * Mp4AlsTables.ParcorScaledValues[_Reader.ReadBits(7)];
				if (l_Order > 1) a_State.QuantizedCoefficients[1] = -32 * Mp4AlsTables.ParcorScaledValues[_Reader.ReadBits(7)];
				for (var coefficient = 2; coefficient < l_Order; coefficient++)
					a_State.QuantizedCoefficients[coefficient] = (int)_Reader.ReadBits(7);
			} else
			{
				var l_Coefficient = 0;
				var l_TableCount = Math.Min(l_Order, 20);
				for (; l_Coefficient < l_TableCount; l_Coefficient++)
				{
					var l_TableOffset = (_CoefficientTable * 20 + l_Coefficient) * 2;
					if (!TryDecodeRice(Mp4AlsTables.ParcorRice[l_TableOffset + 1], out var l_Value)) return FfmpegError.InvalidData;
					l_Value += Mp4AlsTables.ParcorRice[l_TableOffset];
					if (l_Value < -64 || l_Value > 63) return FfmpegError.InvalidData;
					a_State.QuantizedCoefficients[l_Coefficient] = l_Value;
				}
				var l_MiddleCount = Math.Min(l_Order, 127);
				for (; l_Coefficient < l_MiddleCount; l_Coefficient++)
				{
					if (!TryDecodeRice(2, out var l_Value)) return FfmpegError.InvalidData;
					a_State.QuantizedCoefficients[l_Coefficient] = l_Value + (l_Coefficient & 1);
				}
				for (; l_Coefficient < l_Order; l_Coefficient++)
				{
					if (!TryDecodeRice(1, out a_State.QuantizedCoefficients[l_Coefficient])) return FfmpegError.InvalidData;
				}
				a_State.QuantizedCoefficients[0] = 32 * Mp4AlsTables.ParcorScaledValues[a_State.QuantizedCoefficients[0] + 64];
				if (l_Order > 1) a_State.QuantizedCoefficients[1] = -32 * Mp4AlsTables.ParcorScaledValues[a_State.QuantizedCoefficients[1] + 64];
			}
			for (var coefficient = 2; coefficient < l_Order; coefficient++)
				a_State.QuantizedCoefficients[coefficient] = unchecked(a_State.QuantizedCoefficients[coefficient] * (1 << 14) + (l_AddBase << 13));
			return 0;
		}

		private int ReadLongTermPrediction(BlockState a_State)
		{
			if (!_LongTermPrediction || _Reader.ReadBit() == 0) return 0;
			a_State.UseLongTermPrediction = true;
			if (!TryDecodeRice(1, out var l_First) || !TryDecodeRice(2, out var l_Second)) return FfmpegError.InvalidData;
			a_State.LongTermGains[0] = l_First * 8;
			a_State.LongTermGains[1] = l_Second * 8;
			var l_Row = ReadUnary(0, 4);
			if (l_Row >= 4) return FfmpegError.InvalidData;
			var l_Column = (int)_Reader.ReadBits(2);
			a_State.LongTermGains[2] = Mp4AlsTables.LongTermPredictionGains[l_Row * 4 + l_Column];
			if (!TryDecodeRice(2, out var l_Fourth) || !TryDecodeRice(1, out var l_Fifth)) return FfmpegError.InvalidData;
			a_State.LongTermGains[3] = l_Fourth * 8;
			a_State.LongTermGains[4] = l_Fifth * 8;
			a_State.LongTermLag = (int)_Reader.ReadBits(_LongTermLagLength) + Math.Max(4, a_State.OptimalOrder + 1);
			return 0;
		}

		private int DecodeConstantBlock(BlockState a_State)
		{
			var l_Data = _RawSamples[a_State.Channel];
			var l_Base = _MaximumOrder + a_State.Offset;
			Array.Fill(l_Data, a_State.ConstantValue, l_Base, a_State.BlockLength);
			return 0;
		}

		/// <summary>Applies ALS long-term and LPC prediction while preserving the cross-channel history used by joint stereo.</summary>
		private int DecodeVariableBlock(BlockState a_State)
		{
			var l_Data = _RawSamples[a_State.Channel];
			var l_Base = _MaximumOrder + a_State.Offset;
			if (a_State.UseLongTermPrediction)
			{
				for (var sample = Math.Max(a_State.LongTermLag - 2, 0); sample < a_State.BlockLength; sample++)
				{
					var l_Center = sample - a_State.LongTermLag;
					var l_Begin = Math.Max(0, l_Center - 2);
					var l_End = l_Center + 3;
					var l_Gain = 5 - (l_End - l_Begin);
					long l_Prediction = 1 << 6;
					for (var source = l_Begin; source < l_End; source++, l_Gain++)
						l_Prediction += (long)a_State.LongTermGains[l_Gain] * l_Data[l_Base + source];
					l_Data[l_Base + sample] = unchecked(l_Data[l_Base + sample] + (int)(l_Prediction >> 7));
				}
			}

			var l_StartSample = 0;
			if (a_State.RandomAccessBlock)
			{
				for (; l_StartSample < Math.Min(a_State.OptimalOrder, a_State.BlockLength); l_StartSample++)
				{
					long l_Prediction = 1 << 19;
					for (var coefficient = 0; coefficient < l_StartSample; coefficient++)
						l_Prediction += (long)a_State.LinearCoefficients[coefficient] * l_Data[l_Base + l_StartSample - coefficient - 1];
					l_Data[l_Base + l_StartSample] = unchecked(l_Data[l_Base + l_StartSample] - (int)(l_Prediction >> 20));
					ParcorToLinear(l_StartSample, a_State.QuantizedCoefficients, a_State.LinearCoefficients);
				}
			} else
			{
				Array.Clear(a_State.LinearCoefficients, 0, a_State.OptimalOrder);
				for (var coefficient = 0; coefficient < a_State.OptimalOrder; coefficient++)
					ParcorToLinear(coefficient, a_State.QuantizedCoefficients, a_State.LinearCoefficients);
				if (a_State.StorePreviousSamples)
					Array.Copy(l_Data, l_Base - _MaximumOrder, a_State.PreviousSamples, 0, _MaximumOrder);
				if (a_State.JointStereo && a_State.OtherChannel >= 0)
				{
					var l_Other = _RawSamples[a_State.OtherChannel];
					for (var history = 1; history <= _MaximumOrder; history++)
						l_Data[l_Base - history] = unchecked(l_Data[l_Base - history] - l_Other[l_Base - history]);
					if (a_State.Channel < a_State.OtherChannel)
						for (var history = 1; history <= _MaximumOrder; history++)
							l_Data[l_Base - history] = unchecked(-l_Data[l_Base - history]);
				}
				if (a_State.ShiftLeastSignificantBits != 0)
					for (var history = 1; history <= _MaximumOrder; history++)
						l_Data[l_Base - history] >>= a_State.ShiftLeastSignificantBits;
			}

			for (var coefficient = 0; coefficient < a_State.OptimalOrder; coefficient++)
				a_State.ReversedCoefficients[coefficient] = a_State.LinearCoefficients[a_State.OptimalOrder - coefficient - 1];
			for (var sample = l_StartSample; sample < a_State.BlockLength; sample++)
			{
				long l_Prediction = 1 << 19;
				for (var coefficient = 0; coefficient < a_State.OptimalOrder; coefficient++)
					l_Prediction += (long)a_State.ReversedCoefficients[coefficient] * l_Data[l_Base + sample - a_State.OptimalOrder + coefficient];
				l_Data[l_Base + sample] = unchecked(l_Data[l_Base + sample] - (int)(l_Prediction >> 20));
			}
			if (a_State.StorePreviousSamples)
				Array.Copy(a_State.PreviousSamples, 0, l_Data, l_Base - _MaximumOrder, _MaximumOrder);
			if (a_State.ShiftLeastSignificantBits != 0)
				for (var sample = 0; sample < a_State.BlockLength; sample++)
					l_Data[l_Base + sample] = unchecked(l_Data[l_Base + sample] << a_State.ShiftLeastSignificantBits);
			return 0;
		}

		private int GetBlockSizes(int a_CurrentFrameLength, int[] a_BlockSizes, ref uint a_BlockInfo)
		{
			if (_BlockSwitching != 0)
			{
				var l_BitCount = 1 << (_BlockSwitching + 2);
				if (_Reader.BitsLeft < l_BitCount) return 0;
				a_BlockInfo = _Reader.ReadBitsLong(l_BitCount) << (32 - l_BitCount);
			}
			Span<int> l_Divisors = stackalloc int[32];
			var l_Count = 0;
			ParseBlockDivisors(a_BlockInfo, 0, 0, l_Divisors, ref l_Count);
			var l_Remaining = a_CurrentFrameLength;
			for (var block = 0; block < l_Count; block++)
			{
				var l_Size = _FrameLength >> l_Divisors[block];
				if (l_Remaining <= l_Size)
				{
					a_BlockSizes[block] = l_Remaining;
					return block + 1;
				}
				a_BlockSizes[block] = l_Size;
				l_Remaining -= l_Size;
			}
			return l_Count;
		}

		private static void ParseBlockDivisors(uint a_BlockInfo, int a_Node, int a_Divisor, Span<int> a_Divisors, ref int a_Count)
		{
			if (a_Node < 31 && ((a_BlockInfo << a_Node) & 0x40000000U) != 0)
			{
				a_Node *= 2;
				ParseBlockDivisors(a_BlockInfo, a_Node + 1, a_Divisor + 1, a_Divisors, ref a_Count);
				ParseBlockDivisors(a_BlockInfo, a_Node + 2, a_Divisor + 1, a_Divisors, ref a_Count);
			} else if (a_Count < a_Divisors.Length)
			{
				a_Divisors[a_Count++] = a_Divisor;
			}
		}

		private bool TryDecodeRice(int a_Parameter, out int a_Value)
		{
			a_Value = 0;
			if (a_Parameter < 0 || a_Parameter > 32 || _Reader.BitsLeft < a_Parameter) return false;
			var l_Quotient = (uint)ReadUnary(0, _Reader.BitsLeft - a_Parameter);
			var l_Positive = a_Parameter != 0 ? _Reader.ReadBit() != 0 : (l_Quotient & 1) == 0;
			if (a_Parameter > 1)
			{
				l_Quotient <<= a_Parameter - 1;
				l_Quotient += _Reader.ReadBitsLong(a_Parameter - 1);
			} else if (a_Parameter == 0) l_Quotient >>= 1;
			a_Value = l_Positive ? unchecked((int)l_Quotient) : unchecked((int)~l_Quotient);
			return true;
		}

		private int ReadUnary(int a_StopBit, int a_MaximumLength)
		{
			var l_Count = 0;
			while (l_Count < a_MaximumLength && _Reader.ReadBit() != (uint)a_StopBit) l_Count++;
			return l_Count;
		}

		private static void ParcorToLinear(int a_Order, int[] a_Parcor, int[] a_Linear)
		{
			var l_Left = 0;
			var l_Right = a_Order - 1;
			while (l_Left < l_Right)
			{
				var l_Temporary = (int)(((long)a_Parcor[a_Order] * a_Linear[l_Right] + (1 << 19)) >> 20);
				a_Linear[l_Right] = unchecked(a_Linear[l_Right] + (int)(((long)a_Parcor[a_Order] * a_Linear[l_Left] + (1 << 19)) >> 20));
				a_Linear[l_Left] = unchecked(a_Linear[l_Left] + l_Temporary);
				l_Left++;
				l_Right--;
			}
			if (l_Left == l_Right)
				a_Linear[l_Left] = unchecked(a_Linear[l_Left] + (int)(((long)a_Parcor[a_Order] * a_Linear[l_Right] + (1 << 19)) >> 20));
			a_Linear[a_Order] = a_Parcor[a_Order];
		}

		private void CopyFrameHistory(int a_Channel)
		{
			if (_MaximumOrder != 0)
				Array.Copy(_RawSamples[a_Channel], _FrameLength, _RawSamples[a_Channel], 0, _MaximumOrder);
		}

		private void WriteOutput(Span<byte> a_Output, int a_FrameLength, int a_BytesPerSample)
		{
			var l_Shift = a_BytesPerSample * 8 - _BitsPerSample;
			var l_OutputOffset = 0;
			for (var sample = 0; sample < a_FrameLength; sample++)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var l_SourceChannel = _ChannelPositions[channel];
					var l_Value = unchecked(_RawSamples[l_SourceChannel][_MaximumOrder + sample] << l_Shift);
					if (a_BytesPerSample == 2)
						BinaryPrimitives.WriteInt16LittleEndian(a_Output.Slice(l_OutputOffset, 2), unchecked((short)l_Value));
					else
						BinaryPrimitives.WriteInt32LittleEndian(a_Output.Slice(l_OutputOffset, 4), l_Value);
					l_OutputOffset += a_BytesPerSample;
				}
			}
		}

		private static int FindIdentifier(byte[] a_Data)
		{
			for (var offset = 0; offset <= a_Data.Length - 4; offset++)
				if (BinaryPrimitives.ReadUInt32BigEndian(a_Data.AsSpan(offset, 4)) == AlsIdentifier) return offset;
			return -1;
		}

		private void CaptureCheckpoint(int a_PacketOffset)
		{
			if (_FrameId % SeekCheckpointInterval != 0 || FindCheckpoint(_FrameId, true) != null) return;
			var l_History = new int[checked(_Channels * _MaximumOrder)];
			for (var channel = 0; channel < _Channels; channel++)
				Array.Copy(_RawSamples[channel], 0, l_History, channel * _MaximumOrder, _MaximumOrder);
			_SeekCheckpoints.Add(new SeekCheckpoint(_FrameId, a_PacketOffset, l_History));
		}

		private SeekCheckpoint FindCheckpoint(long a_TargetFrame, bool a_Exact = false)
		{
			SeekCheckpoint l_Result = null;
			for (var index = 0; index < _SeekCheckpoints.Count; index++)
			{
				var l_Candidate = _SeekCheckpoints[index];
				if (l_Candidate.FrameId == a_TargetFrame) return l_Candidate;
				if (!a_Exact && l_Candidate.FrameId < a_TargetFrame && (l_Result == null || l_Candidate.FrameId > l_Result.FrameId))
					l_Result = l_Candidate;
			}
			return l_Result;
		}

		private void RestoreCheckpoint(SeekCheckpoint a_Checkpoint)
		{
			_FrameId = a_Checkpoint.FrameId;
			for (var channel = 0; channel < _Channels; channel++)
				Array.Copy(a_Checkpoint.History, channel * _MaximumOrder, _RawSamples[channel], 0, _MaximumOrder);
		}

		private static int CeilingLog2(int a_Value)
		{
			var l_Result = 0;
			var l_Value = Math.Max(0, a_Value - 1);
			while (l_Value != 0) { l_Value >>= 1; l_Result++; }
			return l_Result;
		}

		/// <summary>Holds transient syntax and predictor state for one independently coded ALS block.</summary>
		private sealed class BlockState
		{
			public readonly int[] QuantizedCoefficients;
			public readonly int[] LinearCoefficients;
			public readonly int[] ReversedCoefficients;
			public readonly int[] PreviousSamples;
			public readonly int[] LongTermGains = new int[5];
			public int Channel;
			public int OtherChannel;
			public int Offset;
			public int BlockLength;
			public bool RandomAccessBlock;
			public bool IsConstant;
			public bool JointStereo;
			public int ConstantValue;
			public int ShiftLeastSignificantBits;
			public bool StorePreviousSamples;
			public int OptimalOrder;
			public bool UseLongTermPrediction;
			public int LongTermLag;

			public BlockState(int a_MaximumOrder)
			{
				QuantizedCoefficients = new int[a_MaximumOrder];
				LinearCoefficients = new int[a_MaximumOrder];
				ReversedCoefficients = new int[a_MaximumOrder];
				PreviousSamples = new int[a_MaximumOrder];
			}

			public void Reset(int a_Channel, int a_OtherChannel, int a_Offset, int a_BlockLength, bool a_RandomAccessBlock)
			{
				Channel = a_Channel;
				OtherChannel = a_OtherChannel;
				Offset = a_Offset;
				BlockLength = a_BlockLength;
				RandomAccessBlock = a_RandomAccessBlock;
				IsConstant = false;
				JointStereo = false;
				ConstantValue = 0;
				ShiftLeastSignificantBits = 0;
				StorePreviousSamples = false;
				OptimalOrder = 1;
				UseLongTermPrediction = false;
				LongTermLag = 0;
				Array.Clear(LongTermGains, 0, LongTermGains.Length);
			}
		}

		/// <summary>Captures the byte position and cross-frame LPC history at one bounded seek interval.</summary>
		private sealed class SeekCheckpoint
		{
			public long FrameId { get; }
			public int PacketOffset { get; }
			public int[] History { get; }

			public SeekCheckpoint(long a_FrameId, int a_PacketOffset, int[] a_History)
			{
				FrameId = a_FrameId;
				PacketOffset = a_PacketOffset;
				History = a_History;
			}
		}
	}
}
