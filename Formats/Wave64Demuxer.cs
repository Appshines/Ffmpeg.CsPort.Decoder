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
using System.IO;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Demuxes Sony Wave64 GUID chunks and exposes WAV-compatible PCM, G.711, IMA/MS ADPCM, and GSM block timing with direct seeking.
	/// </summary>
	public sealed class Wave64Demuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer, IDecodedFrameCountAudioDemuxer
	{
		private static readonly byte[] s_RiffGuid = { 0x72,0x69,0x66,0x66,0x2e,0x91,0xcf,0x11,0xa5,0xd6,0x28,0xdb,0x04,0xc1,0x00,0x00 };
		private static readonly byte[] s_WaveGuid = { 0x77,0x61,0x76,0x65,0xf3,0xac,0xd3,0x11,0x8c,0xd1,0x00,0xc0,0x4f,0x8e,0xdb,0x8a };
		private static readonly byte[] s_FormatGuid = { 0x66,0x6d,0x74,0x20,0xf3,0xac,0xd3,0x11,0x8c,0xd1,0x00,0xc0,0x4f,0x8e,0xdb,0x8a };
		private static readonly byte[] s_FactGuid = { 0x66,0x61,0x63,0x74,0xf3,0xac,0xd3,0x11,0x8c,0xd1,0x00,0xc0,0x4f,0x8e,0xdb,0x8a };
		private static readonly byte[] s_DataGuid = { 0x64,0x61,0x74,0x61,0xf3,0xac,0xd3,0x11,0x8c,0xd1,0x00,0xc0,0x4f,0x8e,0xdb,0x8a };
		private readonly FormatReader _Reader;
		private readonly byte[] _Guid = new byte[16];
		private long _DataStart;
		private long _DataEnd;
		private long _FramesPerBlock = 1;
		private int _MaximumPacketSize;
		private long _CurrentTimestamp;

		public Wave64Demuxer(Stream a_Stream)
		{
			_Reader = new FormatReader(a_Stream);
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;
		public long DecodedFrameCount { get; private set; }

		/// <summary>Scans aligned Wave64 GUID chunks, reads the embedded WAVEFORMAT, and preserves fact/data duration semantics.</summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0) || !_Reader.ReadExactly(_Guid) || !_Guid.AsSpan().SequenceEqual(s_RiffGuid) ||
				!_Reader.ReadUInt64LittleEndian(out var l_RiffSize) || l_RiffSize < 72 || !_Reader.ReadExactly(_Guid) || !_Guid.AsSpan().SequenceEqual(s_WaveGuid))
				return FfmpegError.InvalidData;
			var l_GotFormat = false;
			while (_Reader.Position <= _Reader.Length - 24)
			{
				if (!_Reader.ReadExactly(_Guid) || !_Reader.ReadUInt64LittleEndian(out var l_ChunkSize) || l_ChunkSize <= 24 || l_ChunkSize > long.MaxValue)
					return _DataStart > 0 ? FinishHeader() : FfmpegError.InvalidData;
				var l_PayloadStart = _Reader.Position;
				var l_PayloadSize = (long)l_ChunkSize - 24;
				if (_Guid.AsSpan().SequenceEqual(s_FormatGuid))
				{
					var l_Result = ParseFormat(l_PayloadSize);
					if (l_Result < 0) return l_Result;
					l_GotFormat = true;
				} else if (_Guid.AsSpan().SequenceEqual(s_FactGuid) && l_PayloadSize >= 8)
				{
					if (!_Reader.ReadUInt64LittleEndian(out var l_Duration)) return FfmpegError.InvalidData;
					if (l_Duration > 0) StreamInfo.Duration = l_Duration > long.MaxValue ? long.MaxValue : (long)l_Duration;
				} else if (_Guid.AsSpan().SequenceEqual(s_DataGuid))
				{
					_DataStart = l_PayloadStart;
					_DataEnd = Math.Min(_Reader.Length, l_PayloadStart + l_PayloadSize);
				}
				var l_Next = Align8(l_PayloadStart + l_PayloadSize);
				if (l_Next < l_PayloadStart || l_Next > _Reader.Length || !_Reader.Seek(l_Next)) break;
			}
			if (!l_GotFormat || _DataStart <= 0) return FfmpegError.EndOfFile;
			return FinishHeader();
		}

		public int ReadPacket(Span<byte> a_Destination, out DemuxedAudioPacket a_Packet)
		{
			a_Packet = default;
			var l_Left = _DataEnd - _Reader.Position;
			if (l_Left <= 0) return FfmpegError.EndOfFile;
			var l_Size = _MaximumPacketSize;
			if (StreamInfo.BlockAlign > 1)
			{
				if (l_Size < StreamInfo.BlockAlign) l_Size = StreamInfo.BlockAlign;
				l_Size = l_Size / StreamInfo.BlockAlign * StreamInfo.BlockAlign;
			}
			l_Size = (int)Math.Min(l_Size, l_Left);
			if (a_Destination.Length < l_Size) return FfmpegError.InvalidArgument;
			var l_Position = _Reader.Position;
			var l_Read = _Reader.Read(a_Destination.Slice(0, l_Size));
			if (l_Read <= 0) return FfmpegError.EndOfFile;
			var l_Duration = StreamInfo.BlockAlign > 0 ? l_Read / StreamInfo.BlockAlign * _FramesPerBlock : 0;
			a_Packet = new DemuxedAudioPacket(l_Read, l_Position, _CurrentTimestamp, _CurrentTimestamp, l_Duration, 0, false);
			_CurrentTimestamp += l_Duration;
			return l_Read;
		}

		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp) => TrySeekToFrame(a_Timestamp, out a_ActualTimestamp);

		/// <summary>Seeks to the fixed encoded Wave64 block containing the requested decoded sample.</summary>
		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			a_ActualFrameIndex = 0;
			if (_DataStart <= 0 || StreamInfo.BlockAlign <= 0 || _FramesPerBlock <= 0) return false;
			var l_BlockIndex = Math.Max(0L, a_FrameIndex) / _FramesPerBlock;
			var l_Position = _DataStart + l_BlockIndex * StreamInfo.BlockAlign;
			if (l_Position >= _DataEnd) l_Position = Math.Max(_DataStart, _DataEnd - StreamInfo.BlockAlign);
			if (!_Reader.Seek(l_Position)) return false;
			_CurrentTimestamp = (l_Position - _DataStart) / StreamInfo.BlockAlign * _FramesPerBlock;
			a_ActualFrameIndex = _CurrentTimestamp;
			return true;
		}

		private int FinishHeader()
		{
			if (StreamInfo.CodecId == AudioCodecId.None || StreamInfo.SampleRate <= 0 || StreamInfo.Channels <= 0 ||
				StreamInfo.BlockAlign <= 0 || _DataEnd < _DataStart || !_Reader.Seek(_DataStart)) return FfmpegError.InvalidData;
			var l_BlockCount = (_DataEnd - _DataStart) / StreamInfo.BlockAlign;
			DecodedFrameCount = l_BlockCount * _FramesPerBlock;
			if (StreamInfo.Duration == 0) StreamInfo.Duration = DecodedFrameCount;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = StreamInfo.SampleRate;
			_MaximumPacketSize = PcmFormat.GetDefaultPacketSize(StreamInfo);
			if (_MaximumPacketSize < 0) return _MaximumPacketSize;
			if (StreamInfo.CodecId == AudioCodecId.GsmMicrosoft) _MaximumPacketSize = StreamInfo.BlockAlign;
			_CurrentTimestamp = 0;
			return 0;
		}

		/// <summary>Parses WAVEFORMATEX fields and codec extradata embedded in the Wave64 format GUID chunk.</summary>
		private int ParseFormat(long a_Size)
		{
			var l_Start = _Reader.Position;
			if (a_Size < 14 || a_Size > int.MaxValue || !_Reader.ReadUInt16LittleEndian(out var l_Id) ||
				!_Reader.ReadUInt16LittleEndian(out var l_Channels) || !_Reader.ReadUInt32LittleEndian(out var l_SampleRate) ||
				!_Reader.ReadUInt32LittleEndian(out var l_ByteRate) || !_Reader.ReadUInt16LittleEndian(out var l_BlockAlign)) return FfmpegError.InvalidData;
			ushort l_BitsPerSample = 8;
			if (a_Size != 14 && !_Reader.ReadUInt16LittleEndian(out l_BitsPerSample)) return FfmpegError.InvalidData;
			StreamInfo.CodecTag = l_Id == 0xfffe ? 0U : l_Id;
			StreamInfo.CodecId = l_Id == 0xfffe ? AudioCodecId.None : ResolveWaveCodec(l_Id, l_BitsPerSample);
			StreamInfo.Channels = l_Channels; StreamInfo.SampleRate = (int)l_SampleRate; StreamInfo.BitRate = l_ByteRate * 8L;
			StreamInfo.BlockAlign = l_BlockAlign; StreamInfo.BitsPerCodedSample = l_BitsPerSample;
			if (a_Size >= 18)
			{
				if (!_Reader.ReadUInt16LittleEndian(out var l_DeclaredExtraSize)) return FfmpegError.InvalidData;
				var l_ExtraSize = Math.Min((int)a_Size - 18, l_DeclaredExtraSize);
				var l_ExtraData = new byte[l_ExtraSize];
				if (!_Reader.ReadExactly(l_ExtraData)) return FfmpegError.InvalidData;
				StreamInfo.CodecExtraData = l_ExtraData;
				if (l_ExtraSize >= 22 && l_Id == 0xfffe)
				{
					var l_ValidBits = BinaryPrimitives.ReadUInt16LittleEndian(l_ExtraData);
					StreamInfo.ChannelMask = BinaryPrimitives.ReadUInt32LittleEndian(l_ExtraData.AsSpan(2, 4));
					var l_SubFormat = BinaryPrimitives.ReadUInt32LittleEndian(l_ExtraData.AsSpan(6, 4));
					if (l_ValidBits != 0) StreamInfo.BitsPerCodedSample = l_ValidBits;
					StreamInfo.CodecTag = l_SubFormat;
					StreamInfo.CodecId = ResolveWaveCodec(l_SubFormat, StreamInfo.BitsPerCodedSample);
				} else if (l_ExtraSize >= 2 && (StreamInfo.CodecId == AudioCodecId.AdpcmImaWave || StreamInfo.CodecId == AudioCodecId.AdpcmMicrosoft || StreamInfo.CodecId == AudioCodecId.GsmMicrosoft))
					_FramesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(l_ExtraData);
			}
			if (StreamInfo.SampleRate <= 0 || l_Start + a_Size < l_Start || !_Reader.Seek(l_Start + a_Size)) return FfmpegError.InvalidData;
			return 0;
		}

		private static AudioCodecId ResolveWaveCodec(uint a_Tag, int a_BitsPerSample)
		{
			switch (a_Tag)
			{
				case 1: return PcmFormat.GetCodecId(a_BitsPerSample, false, false, ~1);
				case 2: return AudioCodecId.AdpcmMicrosoft;
				case 3: return PcmFormat.GetCodecId(a_BitsPerSample, true, false, 0);
				case 6: return AudioCodecId.PcmALaw;
				case 7: return AudioCodecId.PcmMuLaw;
				case 0x11: return AudioCodecId.AdpcmImaWave;
				case 0x31: return AudioCodecId.GsmMicrosoft;
				default: return AudioCodecId.None;
			}
		}

		private static long Align8(long a_Value) => checked((a_Value + 7) & ~7L);
	}
}
