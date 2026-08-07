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
using System.IO;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Demuxes Sun/NeXT AU audio with fixed PCM, G.711, and little-endian G.726 packet geometry and direct byte-rate seeking.
	/// </summary>
	public sealed class AuAudioDemuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer, IDecodedFrameCountAudioDemuxer
	{
		private const uint AuTag = 0x2e736e64;
		private const uint UnknownDataSize = uint.MaxValue;
		private readonly FormatReader _Reader;
		private long _DataStart;
		private long _DataEnd;
		private int _BitsPerSample;
		private int _MaximumPacketSize;
		private long _CurrentFrame;

		public AuAudioDemuxer(Stream a_Stream)
		{
			_Reader = new FormatReader(a_Stream);
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;
		public long DecodedFrameCount { get; private set; }

		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0) || !_Reader.ReadUInt32BigEndian(out var l_Tag) || l_Tag != AuTag ||
				!_Reader.ReadUInt32BigEndian(out var l_HeaderSize) || l_HeaderSize < 24 || l_HeaderSize > _Reader.Length ||
				!_Reader.ReadUInt32BigEndian(out var l_DataSize) || !_Reader.ReadUInt32BigEndian(out var l_CodecTag) ||
				!_Reader.ReadUInt32BigEndian(out var l_SampleRate) || !_Reader.ReadUInt32BigEndian(out var l_Channels) ||
				l_SampleRate == 0 || l_SampleRate > int.MaxValue || l_Channels == 0 || l_Channels > int.MaxValue)
				return FfmpegError.InvalidData;
			StreamInfo.CodecTag = l_CodecTag;
			StreamInfo.CodecId = ResolveCodec(l_CodecTag, out _BitsPerSample);
			if (StreamInfo.CodecId == AudioCodecId.None || _BitsPerSample <= 0) return FfmpegError.PatchWelcome;
			StreamInfo.SampleRate = (int)l_SampleRate;
			StreamInfo.Channels = (int)l_Channels;
			StreamInfo.BitsPerCodedSample = _BitsPerSample;
			StreamInfo.BitRate = (long)StreamInfo.Channels * StreamInfo.SampleRate * _BitsPerSample;
			StreamInfo.BlockAlign = StreamInfo.CodecId == AudioCodecId.AdpcmG726LittleEndian
				? _BitsPerSample : Math.Max(_BitsPerSample * StreamInfo.Channels / 8, 1);
			_DataStart = l_HeaderSize;
			var l_Available = Math.Max(0L, _Reader.Length - _DataStart);
			var l_BoundedSize = l_DataSize == UnknownDataSize ? l_Available : Math.Min(l_Available, l_DataSize);
			_DataEnd = _DataStart + l_BoundedSize;
			DecodedFrameCount = l_BoundedSize * 8 / (StreamInfo.Channels * (long)_BitsPerSample);
			StreamInfo.Duration = l_DataSize == UnknownDataSize
				? DecodedFrameCount : (long)l_DataSize * 8 / (StreamInfo.Channels * (long)_BitsPerSample);
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = StreamInfo.SampleRate;
			_MaximumPacketSize = PcmFormat.GetDefaultPacketSize(StreamInfo);
			if (_MaximumPacketSize < 0) return _MaximumPacketSize;
			_CurrentFrame = 0;
			return _Reader.Seek(_DataStart) ? 0 : FfmpegError.InvalidData;
		}

		public int ReadPacket(Span<byte> a_Destination, out DemuxedAudioPacket a_Packet)
		{
			a_Packet = default;
			var l_Left = _DataEnd - _Reader.Position;
			if (l_Left <= 0) return FfmpegError.EndOfFile;
			var l_Size = (int)Math.Min(_MaximumPacketSize, l_Left);
			if (a_Destination.Length < l_Size) return FfmpegError.InvalidArgument;
			var l_Position = _Reader.Position;
			var l_Read = _Reader.Read(a_Destination.Slice(0, l_Size));
			if (l_Read <= 0) return FfmpegError.EndOfFile;
			var l_Duration = l_Read * 8L / (StreamInfo.Channels * _BitsPerSample);
			a_Packet = new DemuxedAudioPacket(l_Read, l_Position, _CurrentFrame, _CurrentFrame, l_Duration, 0, false);
			_CurrentFrame += l_Duration;
			return l_Read;
		}

		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp) => TrySeekToFrame(a_Timestamp, out a_ActualTimestamp);

		/// <summary>Rounds a requested sample to the next codec block boundary and seeks directly from the AU data offset.</summary>
		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			a_ActualFrameIndex = 0;
			if (_DataStart < 24 || StreamInfo.BlockAlign <= 0 || _BitsPerSample <= 0) return false;
			var l_ByteRate = StreamInfo.BitRate / 8;
			if (l_ByteRate <= 0) return false;
			var l_ByteOffset = checked(Math.Max(0L, a_FrameIndex) * l_ByteRate / StreamInfo.SampleRate);
			l_ByteOffset = (l_ByteOffset + StreamInfo.BlockAlign - 1) / StreamInfo.BlockAlign * StreamInfo.BlockAlign;
			l_ByteOffset = Math.Min(l_ByteOffset, _DataEnd - _DataStart);
			if (!_Reader.Seek(_DataStart + l_ByteOffset)) return false;
			_CurrentFrame = l_ByteOffset * 8 / (StreamInfo.Channels * (long)_BitsPerSample);
			a_ActualFrameIndex = _CurrentFrame;
			return true;
		}

		private static AudioCodecId ResolveCodec(uint a_Tag, out int a_BitsPerSample)
		{
			a_BitsPerSample = 0;
			switch (a_Tag)
			{
				case 1: a_BitsPerSample = 8; return AudioCodecId.PcmMuLaw;
				case 2: a_BitsPerSample = 8; return AudioCodecId.PcmS8;
				case 3: a_BitsPerSample = 16; return AudioCodecId.PcmS16BigEndian;
				case 4: a_BitsPerSample = 24; return AudioCodecId.PcmS24BigEndian;
				case 5: a_BitsPerSample = 32; return AudioCodecId.PcmS32BigEndian;
				case 6: a_BitsPerSample = 32; return AudioCodecId.PcmF32BigEndian;
				case 7: a_BitsPerSample = 64; return AudioCodecId.PcmF64BigEndian;
				case 23: a_BitsPerSample = 4; return AudioCodecId.AdpcmG726LittleEndian;
				case 25: a_BitsPerSample = 3; return AudioCodecId.AdpcmG726LittleEndian;
				case 26: a_BitsPerSample = 5; return AudioCodecId.AdpcmG726LittleEndian;
				case 27: a_BitsPerSample = 8; return AudioCodecId.PcmALaw;
				case 0x37323632: a_BitsPerSample = 2; return AudioCodecId.AdpcmG726LittleEndian;
				default: return AudioCodecId.None;
			}
		}
	}
}
