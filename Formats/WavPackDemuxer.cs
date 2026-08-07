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
using System.Collections.Generic;
using System.IO;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Demuxes WavPack 4 block sequences, preserves FFmpeg packet boundaries, and builds a decoded-frame seek index.
	/// </summary>
	public sealed class WavPackDemuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer, IDecodedFrameCountAudioDemuxer
	{
		private const uint c_Signature = 0x6b707677;
		private const uint c_Mono = 0x00000004;
		private const uint c_InitialBlock = 0x00000800;
		private const uint c_FinalBlock = 0x00001000;
		private static readonly int[] s_SampleRates = { 6000,8000,9600,11025,12000,16000,22050,24000,32000,44100,48000,64000,88200,96000,192000,0 };
		private readonly FormatReader _Reader;
		private readonly byte[] _Header = new byte[32];
		private readonly List<PacketEntry> _Packets = new List<PacketEntry>();
		private int _PacketIndex;

		public WavPackDemuxer(Stream a_Stream)
		{
			_Reader = new FormatReader(a_Stream);
			StreamInfo = new AudioStreamInfo { CodecId = AudioCodecId.WavPack };
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => _Packets.Count == 0 ? 0 : _Packets[0].Timestamp;
		public long DecodedFrameCount { get; private set; }

		/// <summary>Validates all physical blocks once and groups multichannel block sequences into FFmpeg-compatible packets.</summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0)) return FfmpegError.InvalidArgument;
			PacketEntry l_Packet = default;
			var l_InPacket = false;
			var l_FirstAudioHeader = true;
			while (_Reader.Position <= _Reader.Length - 32)
			{
				var l_Position = _Reader.Position;
				if (!_Reader.ReadExactly(_Header)) return FfmpegError.EndOfFile;
				if (!TryParseHeader(_Header, out var l_Header)) break;
				var l_TotalSize = (long)l_Header.StoredSize + 8;
				if (l_TotalSize < 32 || l_Position > _Reader.Length - l_TotalSize) return FfmpegError.InvalidData;
				if (l_Header.SampleCount == 0)
				{
					if (!_Reader.Seek(l_Position + l_TotalSize)) return FfmpegError.InvalidData;
					continue;
				}
				if (l_FirstAudioHeader)
				{
					var l_Result = InitializeStream(l_Header, l_Position, l_TotalSize);
					if (l_Result < 0) return l_Result;
					l_FirstAudioHeader = false;
				}
				if (!l_InPacket || (l_Header.Flags & c_InitialBlock) != 0)
				{
					if (l_InPacket) return FfmpegError.InvalidData;
					l_Packet = new PacketEntry(l_Position, 0, l_Header.BlockIndex, l_Header.SampleCount);
					l_InPacket = true;
				}
				l_Packet.Size = checked(l_Packet.Size + (int)l_TotalSize);
				l_Packet.Timestamp = l_Header.BlockIndex;
				l_Packet.Duration = l_Header.SampleCount;
				if ((l_Header.Flags & c_FinalBlock) != 0)
				{
					_Packets.Add(l_Packet);
					l_InPacket = false;
				}
				if (!_Reader.Seek(l_Position + l_TotalSize)) return FfmpegError.InvalidData;
			}
			if (l_InPacket || _Packets.Count == 0 || StreamInfo.SampleRate <= 0 || StreamInfo.Channels <= 0) return FfmpegError.InvalidData;
			DecodedFrameCount = StreamInfo.Duration > 0 ? StreamInfo.Duration : _Packets[_Packets.Count - 1].Timestamp + _Packets[_Packets.Count - 1].Duration;
			StreamInfo.Duration = DecodedFrameCount;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = StreamInfo.SampleRate;
			_PacketIndex = 0;
			return _Reader.Seek(_Packets[0].Position) ? 0 : FfmpegError.InvalidData;
		}

		public int ReadPacket(Span<byte> a_Destination, out DemuxedAudioPacket a_Packet)
		{
			a_Packet = default;
			if (_PacketIndex >= _Packets.Count) return FfmpegError.EndOfFile;
			var l_Entry = _Packets[_PacketIndex++];
			if (a_Destination.Length < l_Entry.Size) return FfmpegError.InvalidArgument;
			if (!_Reader.Seek(l_Entry.Position) || _Reader.Read(a_Destination.Slice(0, l_Entry.Size)) != l_Entry.Size) return FfmpegError.EndOfFile;
			a_Packet = new DemuxedAudioPacket(l_Entry.Size, l_Entry.Position, l_Entry.Timestamp, l_Entry.Timestamp, l_Entry.Duration, 0, false);
			return l_Entry.Size;
		}

		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp) => TrySeekToFrame(a_Timestamp, out a_ActualTimestamp);

		/// <summary>Uses the packet block-index table to seek to the access unit at or immediately before a decoded frame.</summary>
		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			a_ActualFrameIndex = 0;
			if (_Packets.Count == 0) return false;
			var l_Target = Math.Max(0L, a_FrameIndex);
			var l_Low = 0; var l_High = _Packets.Count - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + (l_High - l_Low + 1) / 2;
				if (_Packets[l_Middle].Timestamp <= l_Target) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			_PacketIndex = l_Low;
			a_ActualFrameIndex = _Packets[l_Low].Timestamp;
			return _Reader.Seek(_Packets[l_Low].Position);
		}

		private int InitializeStream(BlockHeader a_Header, long a_Position, long a_TotalSize)
		{
			if (a_Header.Version < 0x402 || a_Header.Version > 0x410) return FfmpegError.PatchWelcome;
			StreamInfo.BitsPerCodedSample = (((int)a_Header.Flags & 3) + 1) << 3;
			StreamInfo.Channels = (a_Header.Flags & c_Mono) != 0 ? 1 : 2;
			StreamInfo.ChannelMask = StreamInfo.Channels == 1 ? 4UL : 3UL;
			StreamInfo.SampleRate = s_SampleRates[(a_Header.Flags >> 23) & 15];
			StreamInfo.Duration = a_Header.TotalSamples == uint.MaxValue ? 0 : a_Header.TotalSamples;
			StreamInfo.CodecExtraData = new[] { (byte)a_Header.Version, (byte)(a_Header.Version >> 8) };
			if ((a_Header.Flags & 0x80000000) != 0) return FfmpegError.PatchWelcome;
			if (StreamInfo.SampleRate == 0 || (a_Header.Flags & c_InitialBlock) == 0 || (a_Header.Flags & c_FinalBlock) == 0)
			{
				var l_Result = ReadAdditionalParameters(a_Position + 32, a_Position + a_TotalSize);
				if (l_Result < 0) return l_Result;
			}
			return StreamInfo.SampleRate > 0 && StreamInfo.Channels > 0 ? 0 : FfmpegError.InvalidData;
		}

		/// <summary>Reads optional custom-rate and channel-layout metadata needed when the fixed header cannot describe them.</summary>
		private int ReadAdditionalParameters(long a_Start, long a_End)
		{
			if (!_Reader.Seek(a_Start)) return FfmpegError.InvalidData;
			while (_Reader.Position + 2 <= a_End)
			{
				if (!_Reader.ReadByte(out var l_Id) || !_Reader.ReadByte(out var l_SizeByte)) return FfmpegError.InvalidData;
				var l_Words = (int)l_SizeByte;
				if ((l_Id & 0x80) != 0)
				{
					if (!_Reader.ReadByte(out var l_High1) || !_Reader.ReadByte(out var l_High2)) return FfmpegError.InvalidData;
					l_Words |= l_High1 << 8 | l_High2 << 16;
				}
				var l_StoredSize = checked(l_Words * 2L);
				var l_Size = l_StoredSize - (((l_Id & 0x40) != 0) ? 1 : 0);
				var l_DataStart = _Reader.Position;
				if (l_Size < 0 || l_DataStart > a_End - l_StoredSize) return FfmpegError.InvalidData;
				if ((l_Id & 0x3f) == 0x27 && l_Size == 3)
				{
					var l_Rate = new byte[3]; if (!_Reader.ReadExactly(l_Rate)) return FfmpegError.InvalidData;
					StreamInfo.SampleRate = l_Rate[0] | l_Rate[1] << 8 | l_Rate[2] << 16;
				} else if ((l_Id & 0x3f) == 0x0d && l_Size >= 2)
				{
					var l_Data = new byte[(int)l_Size]; if (!_Reader.ReadExactly(l_Data)) return FfmpegError.InvalidData;
					var l_MaskOffset = 1;
					if (l_Data.Length >= 6)
					{
						StreamInfo.Channels = (l_Data[0] | (l_Data[2] & 0x0f) << 8) + 1;
						l_MaskOffset = 3;
					} else StreamInfo.Channels = l_Data[0];
					ulong l_Mask = 0; for (var l_Index = l_MaskOffset; l_Index < l_Data.Length && l_Index - l_MaskOffset < 8; l_Index++) l_Mask |= (ulong)l_Data[l_Index] << ((l_Index - l_MaskOffset) * 8);
					StreamInfo.ChannelMask = l_Mask;
				}
				if (!_Reader.Seek(l_DataStart + l_StoredSize)) return FfmpegError.InvalidData;
			}
			return 0;
		}

		private static bool TryParseHeader(ReadOnlySpan<byte> a_Data, out BlockHeader a_Header)
		{
			a_Header = default;
			if (a_Data.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(a_Data) != c_Signature) return false;
			var l_Size = BinaryPrimitives.ReadUInt32LittleEndian(a_Data.Slice(4, 4));
			if (l_Size < 24 || l_Size > 16 * 1024 * 1024) return false;
			a_Header = new BlockHeader(l_Size, BinaryPrimitives.ReadUInt16LittleEndian(a_Data.Slice(8, 2)),
				BinaryPrimitives.ReadUInt32LittleEndian(a_Data.Slice(12, 4)), BinaryPrimitives.ReadUInt32LittleEndian(a_Data.Slice(16, 4)),
				BinaryPrimitives.ReadUInt32LittleEndian(a_Data.Slice(20, 4)), BinaryPrimitives.ReadUInt32LittleEndian(a_Data.Slice(24, 4)));
			return true;
		}

		private struct PacketEntry
		{
			public PacketEntry(long a_Position, int a_Size, long a_Timestamp, long a_Duration) { Position = a_Position; Size = a_Size; Timestamp = a_Timestamp; Duration = a_Duration; }
			public long Position; public int Size; public long Timestamp; public long Duration;
		}

		private readonly struct BlockHeader
		{
			public BlockHeader(uint a_StoredSize, ushort a_Version, uint a_TotalSamples, uint a_BlockIndex, uint a_SampleCount, uint a_Flags)
			{ StoredSize = a_StoredSize; Version = a_Version; TotalSamples = a_TotalSamples; BlockIndex = a_BlockIndex; SampleCount = a_SampleCount; Flags = a_Flags; }
			public uint StoredSize { get; } public ushort Version { get; } public uint TotalSamples { get; } public uint BlockIndex { get; } public uint SampleCount { get; } public uint Flags { get; }
		}
	}
}
