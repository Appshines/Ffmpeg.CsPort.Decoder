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
	/// Demuxes fixed-packet Apple CAF PCM and G.711 streams, including 64-bit chunks, packet-table duration fields, and direct seeking.
	/// </summary>
	public sealed class CafAudioDemuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer, IDecodedFrameCountAudioDemuxer
	{
		private const uint CafTag = 0x63616666;
		private const uint DescriptionTag = 0x64657363;
		private const uint DataTag = 0x64617461;
		private const uint PacketTableTag = 0x70616b74;
		private const uint LinearPcmTag = 0x6c70636d;
		private const uint ALawTag = 0x616c6177;
		private const uint MuLawTag = 0x756c6177;
		private const int MaximumPacketSize = 4096;

		private readonly FormatReader _Reader;
		private int _BytesPerPacket;
		private int _FramesPerPacket;
		private long _DataStart;
		private long _DataSize = -1;
		private long _CurrentFrame;
		private long _PacketTableDuration = -1;

		public CafAudioDemuxer(Stream a_Stream)
		{
			_Reader = new FormatReader(a_Stream);
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;
		public long DecodedFrameCount { get; private set; }

		/// <summary>Validates the mandatory desc-first layout, scans 64-bit chunks, and resolves FFmpeg-compatible data and duration bounds.</summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0) || !_Reader.ReadUInt32BigEndian(out var l_Tag) || l_Tag != CafTag ||
				!_Reader.ReadUInt16BigEndian(out var l_Version) || l_Version != 1 || !_Reader.ReadUInt16BigEndian(out _) ||
				!_Reader.ReadUInt32BigEndian(out l_Tag) || l_Tag != DescriptionTag || !ReadChunkSize(out var l_DescriptionSize) ||
				l_DescriptionSize != 32)
				return FfmpegError.InvalidData;
			var l_Result = ReadDescription();
			if (l_Result < 0) return l_Result;
			while (_Reader.Position <= _Reader.Length - 12)
			{
				if (!_Reader.ReadUInt32BigEndian(out l_Tag) || !ReadChunkSize(out var l_Size))
					return FfmpegError.InvalidData;
				var l_PayloadPosition = _Reader.Position;
				if (l_Tag == DataTag)
				{
					if (!_Reader.ReadUInt32BigEndian(out _)) return FfmpegError.InvalidData;
					_DataStart = _Reader.Position;
					_DataSize = l_Size < 0 ? -1 : l_Size - 4;
					if (_DataSize < -1 || _DataSize > long.MaxValue - _DataStart) return FfmpegError.InvalidData;
				} else if (l_Tag == PacketTableTag)
				{
					l_Result = ReadPacketTable(l_Size);
					if (l_Result < 0) return l_Result;
				}
				if (l_Size < 0)
				{
					if (l_Tag == DataTag) break;
					return FfmpegError.InvalidData;
				}
				if (l_PayloadPosition > long.MaxValue - l_Size || !_Reader.Seek(l_PayloadPosition + l_Size)) break;
			}
			if (_DataStart <= 0 || _BytesPerPacket <= 0 || _FramesPerPacket <= 0)
				return FfmpegError.InvalidData;
			var l_AvailableDataSize = _DataSize >= 0 ? _DataSize : Math.Max(0L, _Reader.Length - _DataStart);
			DecodedFrameCount = l_AvailableDataSize / _BytesPerPacket * _FramesPerPacket;
			var l_DeclaredFrameCount = _DataSize >= 0 ? (_DataSize + 4) / _BytesPerPacket * _FramesPerPacket : DecodedFrameCount;
			StreamInfo.Duration = _PacketTableDuration >= 0 ? _PacketTableDuration : l_DeclaredFrameCount;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = StreamInfo.SampleRate;
			_CurrentFrame = 0;
			return _Reader.Seek(_DataStart) ? 0 : FfmpegError.InvalidData;
		}

		public int ReadPacket(Span<byte> a_Destination, out DemuxedAudioPacket a_Packet)
		{
			a_Packet = default;
			var l_Left = _DataSize >= 0 ? _DataStart + _DataSize - _Reader.Position : _Reader.Length - _Reader.Position;
			if (l_Left == 0) return FfmpegError.EndOfFile;
			if (l_Left < 0) return FfmpegError.InvalidData;
			var l_Size = MaximumPacketSize / _BytesPerPacket * _BytesPerPacket;
			l_Size = (int)Math.Min(l_Size, l_Left);
			if (l_Size <= 0) return FfmpegError.InvalidData;
			if (a_Destination.Length < l_Size) return FfmpegError.InvalidArgument;
			var l_Position = _Reader.Position;
			var l_Read = _Reader.Read(a_Destination.Slice(0, l_Size));
			if (l_Read <= 0) return FfmpegError.EndOfFile;
			var l_Duration = l_Read / _BytesPerPacket * (long)_FramesPerPacket;
			a_Packet = new DemuxedAudioPacket(l_Read, l_Position, _CurrentFrame, _CurrentFrame, l_Duration, 0, false);
			_CurrentFrame += l_Duration;
			return l_Read;
		}

		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			return TrySeekToFrame(a_Timestamp, out a_ActualTimestamp);
		}

		/// <summary>Maps a sample timestamp directly to the corresponding fixed CAF packet without scanning earlier audio.</summary>
		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			a_ActualFrameIndex = 0;
			if (_DataStart <= 0 || _BytesPerPacket <= 0 || _FramesPerPacket <= 0) return false;
			var l_PacketIndex = Math.Max(0L, a_FrameIndex) / _FramesPerPacket;
			var l_ByteOffset = l_PacketIndex * _BytesPerPacket;
			if (_DataSize >= 0) l_ByteOffset = Math.Min(l_ByteOffset, _DataSize);
			if (!_Reader.Seek(_DataStart + l_ByteOffset)) return false;
			_CurrentFrame = l_ByteOffset / _BytesPerPacket * _FramesPerPacket;
			a_ActualFrameIndex = _CurrentFrame;
			return true;
		}

		private int ReadDescription()
		{
			if (!_Reader.ReadUInt64BigEndian(out var l_SampleRateBits) || !_Reader.ReadUInt32LittleEndian(out var l_CodecTag) ||
				!_Reader.ReadUInt32BigEndian(out var l_Flags) || !_Reader.ReadUInt32BigEndian(out var l_BytesPerPacket) ||
				!_Reader.ReadUInt32BigEndian(out var l_FramesPerPacket) || !_Reader.ReadUInt32BigEndian(out var l_Channels) ||
				!_Reader.ReadUInt32BigEndian(out var l_BitsPerSample) || l_BytesPerPacket > int.MaxValue ||
				l_FramesPerPacket > int.MaxValue || l_Channels == 0 || l_Channels > int.MaxValue || l_BitsPerSample > int.MaxValue)
				return FfmpegError.InvalidData;
			var l_SampleRateValue = BitConverter.Int64BitsToDouble(unchecked((long)l_SampleRateBits));
			if (!double.IsFinite(l_SampleRateValue) || l_SampleRateValue <= 0 || l_SampleRateValue > int.MaxValue)
				return FfmpegError.InvalidData;
			StreamInfo.CodecTag = l_CodecTag;
			StreamInfo.SampleRate = Math.Clamp((int)l_SampleRateValue, 0, int.MaxValue);
			StreamInfo.Channels = (int)l_Channels;
			StreamInfo.BitsPerCodedSample = (int)l_BitsPerSample;
			_BytesPerPacket = (int)l_BytesPerPacket;
			_FramesPerPacket = (int)l_FramesPerPacket;
			StreamInfo.BlockAlign = _BytesPerPacket;
			if (l_CodecTag == BinaryPrimitives.ReverseEndianness(LinearPcmTag))
				StreamInfo.CodecId = PcmFormat.GetCodecId((int)l_BitsPerSample, (l_Flags & 1) != 0, (l_Flags & 2) == 0, -1);
			else if (l_CodecTag == BinaryPrimitives.ReverseEndianness(ALawTag)) StreamInfo.CodecId = AudioCodecId.PcmALaw;
			else if (l_CodecTag == BinaryPrimitives.ReverseEndianness(MuLawTag)) StreamInfo.CodecId = AudioCodecId.PcmMuLaw;
			if (StreamInfo.CodecId == AudioCodecId.None || _BytesPerPacket <= 0 || _FramesPerPacket <= 0)
				return FfmpegError.InvalidData;
			StreamInfo.BitRate = (long)StreamInfo.SampleRate * _BytesPerPacket * 8 / _FramesPerPacket;
			return 0;
		}

		private int ReadPacketTable(long a_Size)
		{
			var l_Start = _Reader.Position;
			if (a_Size < 24 || !_Reader.ReadUInt64BigEndian(out var l_Packets) || !_Reader.ReadUInt64BigEndian(out var l_ValidFrames) ||
				!_Reader.ReadUInt32BigEndian(out var l_Priming) || !_Reader.ReadUInt32BigEndian(out var l_Remainder) ||
				l_Packets > long.MaxValue || l_ValidFrames > long.MaxValue)
				return FfmpegError.InvalidData;
			StreamInfo.StartSkipSamples = unchecked((int)Math.Min(l_Priming, int.MaxValue));
			StreamInfo.EndPaddingSamples = unchecked((int)Math.Min(l_Remainder, int.MaxValue));
			if (_BytesPerPacket > 0 && _FramesPerPacket > 0 && l_Packets <= (ulong)(long.MaxValue / _FramesPerPacket))
			{
				_PacketTableDuration = (long)l_Packets * _FramesPerPacket - l_Priming - l_Remainder;
				if (_PacketTableDuration < 0) return FfmpegError.InvalidData;
			}
			return _Reader.Position - l_Start <= a_Size ? 0 : FfmpegError.InvalidData;
		}

		private bool ReadChunkSize(out long a_Size)
		{
			a_Size = 0;
			if (!_Reader.ReadUInt64BigEndian(out var l_Size)) return false;
			a_Size = unchecked((long)l_Size);
			return true;
		}
	}
}
