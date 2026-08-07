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
using Ffmpeg.CsPort.Decoder.Codecs.MpegAudio;
using Ffmpeg.CsPort.Decoder.Codecs.Opus;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Demuxes one supported audio PID from 188-byte MPEG transport streams, including PSI, PES, timestamps, and Opus access units.
	/// </summary>
	public sealed class MpegTsAudioDemuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer, IDecodedFrameCountAudioDemuxer
	{
		private const int TransportPacketSize = 188;
		private const int TransportTimeBase = 90000;
		private const int NoPid = -1;
		private readonly FormatReader _Reader;
		private readonly byte[] _TransportPacket = new byte[TransportPacketSize];
		private readonly List<TsAudioPacket> _Packets = new List<TsAudioPacket>();
		private readonly List<PesSegment> _Segments = new List<PesSegment>();
		private readonly MemoryStream _ElementaryStream = new MemoryStream();
		private readonly MemoryStream _CurrentPes = new MemoryStream();
		private int _ProgramMapPid = NoPid;
		private int _AudioPid = NoPid;
		private AudioCodecId _CodecId;
		private int _OpusChannelConfiguration;
		private long _CurrentPesPosition;
		private bool _CurrentPesCorrupt;
		private int _LastContinuityCounter = -1;
		private int _CurrentPacket;
		private long _LastPesTimestamp = DemuxedAudioPacket.NoTimestamp;

		public MpegTsAudioDemuxer(Stream a_Stream)
		{
			_Reader = new FormatReader(a_Stream);
			StreamInfo = new AudioStreamInfo { TimeBaseNumerator = 1, TimeBaseDenominator = TransportTimeBase };
		}

		public AudioStreamInfo StreamInfo { get; }
		public int AudioPid => _AudioPid;
		public long FirstTimestamp => _Packets.Count == 0 ? 0 : _Packets[0].PresentationTimestamp;
		public long DecodedFrameCount { get; private set; }

		/// <summary>
		/// Scans PSI and the selected PID once, reassembles PES payloads, then builds an in-memory audio packet index for direct seeking.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0) || _Reader.Length < TransportPacketSize * 3)
				return FfmpegError.InvalidArgument;
			for (var l_Position = 0L; l_Position <= _Reader.Length - TransportPacketSize; l_Position += TransportPacketSize)
			{
				if (!_Reader.Seek(l_Position) || !_Reader.ReadExactly(_TransportPacket) || _TransportPacket[0] != 0x47)
					return FfmpegError.InvalidData;
				ProcessTransportPacket(l_Position);
			}
			FlushPes();
			if (_AudioPid == NoPid || _CodecId == AudioCodecId.None)
				return FfmpegError.InvalidData;
			var l_Result = _CodecId == AudioCodecId.Opus ? FinishOpusPackets() : BuildRawCodecPackets();
			if (l_Result < 0 || _Packets.Count == 0)
				return l_Result < 0 ? l_Result : FfmpegError.InvalidData;
			ResolveMissingTimestamps();
			_CurrentPacket = 0;
			StreamInfo.CodecId = _CodecId;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = TransportTimeBase;
			var l_LastPesDuration = _CodecId == AudioCodecId.Opus ? 0 : _Packets[_Packets.Count - 1].Duration;
			StreamInfo.Duration = _LastPesTimestamp == DemuxedAudioPacket.NoTimestamp
				? Math.Max(0L, _Packets[_Packets.Count - 1].PresentationTimestamp - _Packets[0].PresentationTimestamp)
				: Math.Max(0L, _LastPesTimestamp + l_LastPesDuration - _Packets[0].PresentationTimestamp);
			return 0;
		}

		public int ReadPacket(Span<byte> a_Destination, out DemuxedAudioPacket a_Packet)
		{
			a_Packet = default;
			if (_CurrentPacket >= _Packets.Count)
				return FfmpegError.EndOfFile;
			var l_Source = _Packets[_CurrentPacket++];
			if (a_Destination.Length < l_Source.Data.Length)
				return FfmpegError.InvalidArgument;
			l_Source.Data.AsSpan().CopyTo(a_Destination);
			a_Packet = new DemuxedAudioPacket(l_Source.Data.Length, l_Source.Position, l_Source.PresentationTimestamp,
				l_Source.DecodeTimestamp, l_Source.Duration, 0, l_Source.Corrupt);
			return l_Source.Data.Length;
		}

		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			var l_Index = FindPacketByTimestamp(a_Timestamp);
			if (l_Index < 0) { a_ActualTimestamp = 0; return false; }
			_CurrentPacket = l_Index;
			a_ActualTimestamp = _Packets[l_Index].PresentationTimestamp;
			return true;
		}

		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			if (_Packets.Count == 0) { a_ActualFrameIndex = 0; return false; }
			var l_TimelineOrigin = RescaleNearest(_Packets[0].PresentationTimestamp, StreamInfo.SampleRate, TransportTimeBase);
			var l_RelativeFrameIndex = Math.Max(0L, a_FrameIndex - l_TimelineOrigin);
			var l_Low = 0;
			var l_High = _Packets.Count - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Packets[l_Middle].DecodedFramePosition <= l_RelativeFrameIndex) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			_CurrentPacket = l_Low;
			a_ActualFrameIndex = l_TimelineOrigin + _Packets[l_Low].DecodedFramePosition;
			return true;
		}

		/// <summary>Extracts one TS payload, handles adaptation discontinuities, and routes PSI or the selected PES PID.</summary>
		private void ProcessTransportPacket(long a_Position)
		{
			if ((_TransportPacket[1] & 0x80) != 0)
				return;
			var l_PayloadUnitStart = (_TransportPacket[1] & 0x40) != 0;
			var l_Pid = ((_TransportPacket[1] & 0x1f) << 8) | _TransportPacket[2];
			var l_AdaptationControl = _TransportPacket[3] >> 4 & 3;
			var l_ContinuityCounter = _TransportPacket[3] & 15;
			var l_PayloadOffset = 4;
			var l_Discontinuity = false;
			if ((l_AdaptationControl & 2) != 0)
			{
				var l_AdaptationLength = _TransportPacket[l_PayloadOffset];
				if (l_AdaptationLength > 0 && l_PayloadOffset + 1 < TransportPacketSize)
					l_Discontinuity = (_TransportPacket[l_PayloadOffset + 1] & 0x80) != 0;
				l_PayloadOffset += l_AdaptationLength + 1;
			}
			if ((l_AdaptationControl & 1) == 0 || l_PayloadOffset >= TransportPacketSize)
				return;
			var l_Payload = _TransportPacket.AsSpan(l_PayloadOffset);
			if (l_Pid == 0 && l_PayloadUnitStart)
			{
				ParseProgramAssociation(l_Payload);
				return;
			}
			if (l_Pid == _ProgramMapPid && l_PayloadUnitStart)
			{
				ParseProgramMap(l_Payload);
				return;
			}
			if (l_Pid != _AudioPid)
				return;
			if (_LastContinuityCounter >= 0 && !l_Discontinuity && l_ContinuityCounter != ((_LastContinuityCounter + 1) & 15))
				_CurrentPesCorrupt = true;
			_LastContinuityCounter = l_ContinuityCounter;
			if (l_PayloadUnitStart)
			{
				FlushPes();
				_CurrentPesPosition = a_Position;
				_CurrentPesCorrupt = false;
			}
			_CurrentPes.Write(l_Payload);
		}

		private void ParseProgramAssociation(ReadOnlySpan<byte> a_Payload)
		{
			var l_Section = GetPsiSection(a_Payload, 0x00);
			if (l_Section.IsEmpty)
				return;
			var l_End = l_Section.Length - 4;
			for (var l_Offset = 8; l_Offset <= l_End - 4; l_Offset += 4)
			{
				var l_Program = BinaryPrimitives.ReadUInt16BigEndian(l_Section.Slice(l_Offset, 2));
				if (l_Program == 0) continue;
				_ProgramMapPid = BinaryPrimitives.ReadUInt16BigEndian(l_Section.Slice(l_Offset + 2, 2)) & 0x1fff;
				return;
			}
		}

		/// <summary>Selects the first supported audio elementary stream and reads private registration/channel descriptors.</summary>
		private void ParseProgramMap(ReadOnlySpan<byte> a_Payload)
		{
			if (_AudioPid != NoPid)
				return;
			var l_Section = GetPsiSection(a_Payload, 0x02);
			if (l_Section.IsEmpty || l_Section.Length < 16)
				return;
			var l_ProgramInfoLength = BinaryPrimitives.ReadUInt16BigEndian(l_Section.Slice(10, 2)) & 0x0fff;
			var l_Offset = 12 + l_ProgramInfoLength;
			var l_End = l_Section.Length - 4;
			while (l_Offset <= l_End - 5)
			{
				var l_StreamType = l_Section[l_Offset];
				var l_Pid = BinaryPrimitives.ReadUInt16BigEndian(l_Section.Slice(l_Offset + 1, 2)) & 0x1fff;
				var l_InfoLength = BinaryPrimitives.ReadUInt16BigEndian(l_Section.Slice(l_Offset + 3, 2)) & 0x0fff;
				if (l_Offset + 5 + l_InfoLength > l_End)
					return;
				var l_Descriptors = l_Section.Slice(l_Offset + 5, l_InfoLength);
				var l_Codec = ResolveCodec(l_StreamType, l_Descriptors, out var l_OpusChannels);
				if (l_Codec != AudioCodecId.None)
				{
					_AudioPid = l_Pid;
					_CodecId = l_Codec;
					_OpusChannelConfiguration = l_OpusChannels;
					return;
				}
				l_Offset += 5 + l_InfoLength;
			}
		}

		private static ReadOnlySpan<byte> GetPsiSection(ReadOnlySpan<byte> a_Payload, byte a_TableId)
		{
			if (a_Payload.Length < 4)
				return default;
			var l_Offset = 1 + a_Payload[0];
			if (l_Offset > a_Payload.Length - 3 || a_Payload[l_Offset] != a_TableId)
				return default;
			var l_Length = 3 + (BinaryPrimitives.ReadUInt16BigEndian(a_Payload.Slice(l_Offset + 1, 2)) & 0x0fff);
			return l_Length <= a_Payload.Length - l_Offset ? a_Payload.Slice(l_Offset, l_Length) : default;
		}

		private static AudioCodecId ResolveCodec(int a_StreamType, ReadOnlySpan<byte> a_Descriptors, out int a_OpusChannels)
		{
			a_OpusChannels = 0;
			switch (a_StreamType)
			{
				case 0x03: return AudioCodecId.Mp2;
				case 0x04: return AudioCodecId.Mp2;
				case 0x0f: return AudioCodecId.Aac;
				case 0x11: return AudioCodecId.AacLatm;
				case 0x81: return AudioCodecId.Ac3;
				case 0x82: return AudioCodecId.Dts;
				case 0x87: return AudioCodecId.Eac3;
				case 0x06: break;
				default: return AudioCodecId.None;
			}
			var l_Registration = 0U;
			for (var l_Offset = 0; l_Offset <= a_Descriptors.Length - 2;)
			{
				var l_Tag = a_Descriptors[l_Offset];
				var l_Length = a_Descriptors[l_Offset + 1];
				if (l_Offset + 2 + l_Length > a_Descriptors.Length) break;
				var l_Data = a_Descriptors.Slice(l_Offset + 2, l_Length);
				if (l_Tag == 0x05 && l_Length >= 4) l_Registration = BinaryPrimitives.ReadUInt32BigEndian(l_Data);
				if (l_Tag == 0x7f && l_Length >= 2 && l_Data[0] == 0x80) a_OpusChannels = l_Data[1];
				l_Offset += 2 + l_Length;
			}
			if (l_Registration == 0x4f707573U) return AudioCodecId.Opus;
			if (l_Registration == 0x41432d33U) return AudioCodecId.Ac3;
			if (l_Registration == 0x45414333U) return AudioCodecId.Eac3;
			if (l_Registration == 0x44545331U || l_Registration == 0x44545332U || l_Registration == 0x44545333U) return AudioCodecId.Dts;
			return AudioCodecId.None;
		}

		private void FlushPes()
		{
			if (_CurrentPes.Length == 0)
				return;
			var l_Data = _CurrentPes.ToArray();
			_CurrentPes.SetLength(0);
			if (_CurrentPesCorrupt || l_Data.Length < 9 || l_Data[0] != 0 || l_Data[1] != 0 || l_Data[2] != 1)
				return;
			var l_TotalLength = BinaryPrimitives.ReadUInt16BigEndian(l_Data.AsSpan(4, 2));
			if (l_TotalLength != 0 && l_TotalLength + 6 < l_Data.Length)
				Array.Resize(ref l_Data, l_TotalLength + 6);
			var l_HeaderLength = l_Data[8];
			var l_PayloadOffset = 9 + l_HeaderLength;
			if (l_PayloadOffset > l_Data.Length)
				return;
			var l_PresentationTimestamp = (l_Data[7] & 0x80) != 0 && l_HeaderLength >= 5 ? ReadPesTimestamp(l_Data.AsSpan(9, 5)) : DemuxedAudioPacket.NoTimestamp;
			var l_DecodeTimestamp = (l_Data[7] & 0x40) != 0 && l_HeaderLength >= 10 ? ReadPesTimestamp(l_Data.AsSpan(14, 5)) : l_PresentationTimestamp;
			if (l_PresentationTimestamp != DemuxedAudioPacket.NoTimestamp)
				_LastPesTimestamp = l_PresentationTimestamp;
			var l_Payload = l_Data.AsSpan(l_PayloadOffset);
			if (_CodecId == AudioCodecId.Opus)
				AppendOpusAccessUnits(l_Payload, l_PresentationTimestamp, l_DecodeTimestamp, _CurrentPesPosition);
			else
			{
				var l_Start = _ElementaryStream.Length;
				_ElementaryStream.Write(l_Payload);
				_Segments.Add(new PesSegment(l_Start, l_Payload.Length, l_PresentationTimestamp, l_DecodeTimestamp, _CurrentPesPosition));
			}
		}

		/// <summary>Removes MPEG-TS Opus access-unit headers and records each self-contained multistream Opus packet.</summary>
		private void AppendOpusAccessUnits(ReadOnlySpan<byte> a_Payload, long a_Pts, long a_Dts, long a_Position)
		{
			var l_Offset = 0;
			var l_PacketInPes = 0;
			var l_SampleOffset = 0L;
			while (l_Offset <= a_Payload.Length - 3 && a_Payload[l_Offset] == 0x7f)
			{
				l_Offset += 2;
				var l_Size = 0;
				byte l_Lace;
				do
				{
					if (l_Offset >= a_Payload.Length) return;
					l_Lace = a_Payload[l_Offset++];
					l_Size += l_Lace;
				} while (l_Lace == 255);
				if (l_Size <= 0 || l_Size > a_Payload.Length - l_Offset) return;
				var l_Data = a_Payload.Slice(l_Offset, l_Size).ToArray();
				l_Offset += l_Size;
				var l_Parsed = new OpusPacket();
				if (OpusPacketParser.Parse(l_Parsed, l_Data, 0, l_Data.Length, false) < 0) return;
				var l_Samples = l_Parsed.FrameDuration * l_Parsed.FrameCount;
				var l_Duration = l_Samples * (long)TransportTimeBase / 48000;
				var l_PacketPts = a_Pts == DemuxedAudioPacket.NoTimestamp ? a_Pts : a_Pts + l_SampleOffset * TransportTimeBase / 48000;
				var l_PacketDts = a_Dts == DemuxedAudioPacket.NoTimestamp ? a_Dts : a_Dts + l_SampleOffset * TransportTimeBase / 48000;
				_Packets.Add(new TsAudioPacket(l_Data, l_PacketInPes == 0 ? a_Position : DemuxedAudioPacket.NoTimestamp,
					l_PacketPts, l_PacketDts, l_Duration, l_Samples, false));
				l_SampleOffset += l_Samples;
				l_PacketInPes++;
			}
		}

		private int FinishOpusPackets()
		{
			var l_ExtraData = BuildOpusHead(_OpusChannelConfiguration);
			if (l_ExtraData == null)
				return FfmpegError.InvalidData;
			StreamInfo.CodecExtraData = l_ExtraData;
			StreamInfo.SampleRate = 48000;
			StreamInfo.Channels = _OpusChannelConfiguration;
			return BuildDecodedFramePositions();
		}

		/// <summary>Uses the existing raw demuxers to preserve codec frame boundaries after PES payload concatenation.</summary>
		private int BuildRawCodecPackets()
		{
			var l_Data = _ElementaryStream.ToArray();
			using (var l_Stream = new MemoryStream(l_Data, false))
			{
				var l_Demuxer = CreateRawDemuxer(l_Stream);
				if (l_Demuxer == null || ReadRawHeader(l_Demuxer) < 0)
					return FfmpegError.InvalidData;
				CopyRawStreamInfo(GetRawStreamInfo(l_Demuxer));
				var l_Buffer = new byte[4 * 1024 * 1024];
				var l_SegmentIndex = 0;
				var l_SegmentSampleOffset = 0L;
				var l_LastSegmentIndex = -1;
				while (true)
				{
					var l_Result = ReadRawPacket(l_Demuxer, l_Buffer, out var l_RawPacket);
					if (l_Result == FfmpegError.EndOfFile) break;
					if (l_Result < 0) return l_Result;
					while (l_SegmentIndex + 1 < _Segments.Count && l_RawPacket.Position >= _Segments[l_SegmentIndex].End)
						l_SegmentIndex++;
					if (l_SegmentIndex >= _Segments.Count) return FfmpegError.InvalidData;
					var l_Segment = _Segments[l_SegmentIndex];
					if (l_LastSegmentIndex != l_SegmentIndex) { l_SegmentSampleOffset = 0; l_LastSegmentIndex = l_SegmentIndex; }
					var l_Samples = GetPacketSampleCount(l_Buffer, l_Result, l_RawPacket, StreamInfo.SampleRate);
					if (l_Samples <= 0) return FfmpegError.InvalidData;
					var l_Pts = l_Segment.PresentationTimestamp == DemuxedAudioPacket.NoTimestamp ? DemuxedAudioPacket.NoTimestamp :
						l_Segment.PresentationTimestamp + RescaleNearest(l_SegmentSampleOffset, TransportTimeBase, StreamInfo.SampleRate);
					var l_Dts = l_Segment.DecodeTimestamp == DemuxedAudioPacket.NoTimestamp ? l_Pts :
						l_Segment.DecodeTimestamp + RescaleNearest(l_SegmentSampleOffset, TransportTimeBase, StreamInfo.SampleRate);
					var l_Duration = l_Samples * (long)TransportTimeBase / StreamInfo.SampleRate;
					var l_Position = l_SegmentSampleOffset == 0 ? l_Segment.Position : DemuxedAudioPacket.NoTimestamp;
					_Packets.Add(new TsAudioPacket(l_Buffer.AsSpan(0, l_Result).ToArray(), l_Position, l_Pts, l_Dts,
						l_Duration, l_Samples, l_RawPacket.IsCorrupt));
					l_SegmentSampleOffset += l_Samples;
				}
			}
			return BuildDecodedFramePositions();
		}

		private object CreateRawDemuxer(Stream a_Stream)
		{
			switch (_CodecId)
			{
				case AudioCodecId.Mp1:
				case AudioCodecId.Mp2:
				case AudioCodecId.Mp3: return new MpegAudioDemuxer(a_Stream);
				case AudioCodecId.Aac: return new RawAacDemuxer(a_Stream, false);
				case AudioCodecId.AacLatm: return new RawAacDemuxer(a_Stream, true);
				case AudioCodecId.Ac3:
				case AudioCodecId.Eac3: return new Ac3RawDemuxer(a_Stream);
				case AudioCodecId.Dts: return new DtsRawDemuxer(a_Stream);
				default: return null;
			}
		}

		private static int ReadRawHeader(object a_Demuxer)
		{
			if (a_Demuxer is MpegAudioDemuxer l_Mpeg) return l_Mpeg.ReadHeader();
			if (a_Demuxer is RawAacDemuxer l_Aac) return l_Aac.ReadHeader();
			if (a_Demuxer is Ac3RawDemuxer l_Ac3) return l_Ac3.ReadHeader();
			if (a_Demuxer is DtsRawDemuxer l_Dts) return l_Dts.ReadHeader();
			return FfmpegError.InvalidArgument;
		}

		private static AudioStreamInfo GetRawStreamInfo(object a_Demuxer)
		{
			if (a_Demuxer is MpegAudioDemuxer l_Mpeg) return l_Mpeg.StreamInfo;
			if (a_Demuxer is RawAacDemuxer l_Aac) return l_Aac.StreamInfo;
			if (a_Demuxer is Ac3RawDemuxer l_Ac3) return l_Ac3.StreamInfo;
			if (a_Demuxer is DtsRawDemuxer l_Dts) return l_Dts.StreamInfo;
			return null;
		}

		private static int ReadRawPacket(object a_Demuxer, Span<byte> a_Buffer, out DemuxedAudioPacket a_Packet)
		{
			if (a_Demuxer is MpegAudioDemuxer l_Mpeg) return l_Mpeg.ReadPacket(a_Buffer, out a_Packet);
			if (a_Demuxer is RawAacDemuxer l_Aac) return l_Aac.ReadPacket(a_Buffer, out a_Packet);
			if (a_Demuxer is Ac3RawDemuxer l_Ac3) return l_Ac3.ReadPacket(a_Buffer, out a_Packet);
			if (a_Demuxer is DtsRawDemuxer l_Dts) return l_Dts.ReadPacket(a_Buffer, out a_Packet);
			a_Packet = default;
			return FfmpegError.InvalidArgument;
		}

		private void CopyRawStreamInfo(AudioStreamInfo a_Source)
		{
			StreamInfo.CodecId = a_Source.CodecId;
			_CodecId = a_Source.CodecId;
			StreamInfo.CodecTag = a_Source.CodecTag;
			StreamInfo.SampleRate = a_Source.SampleRate;
			StreamInfo.Channels = a_Source.Channels;
			StreamInfo.ChannelMask = a_Source.ChannelMask;
			StreamInfo.BitsPerCodedSample = a_Source.BitsPerCodedSample;
			StreamInfo.BlockAlign = a_Source.BlockAlign;
			StreamInfo.BitRate = a_Source.BitRate;
			StreamInfo.CodecExtraData = a_Source.CodecExtraData;
		}

		private int GetPacketSampleCount(byte[] a_Data, int a_Size, DemuxedAudioPacket a_Packet, int a_SampleRate)
		{
			if (_CodecId == AudioCodecId.Mp1 || _CodecId == AudioCodecId.Mp2 || _CodecId == AudioCodecId.Mp3)
			{
				if (a_Size < 4) return 0;
				var l_Header = new MpegAudioHeader();
				return l_Header.Decode(BinaryPrimitives.ReadUInt32BigEndian(a_Data.AsSpan(0, 4))) == 0 ? l_Header.SamplesPerFrame : 0;
			}
			if (_CodecId == AudioCodecId.Aac && a_Size >= 7)
				return 1024 * ((a_Data[6] & 3) + 1);
			if (_CodecId == AudioCodecId.AacLatm)
				return 1024;
			return checked((int)RescaleNearest(a_Packet.Duration, a_SampleRate, TransportTimeBase));
		}

		private int BuildDecodedFramePositions()
		{
			var l_Position = 0L;
			for (var l_Index = 0; l_Index < _Packets.Count; l_Index++)
			{
				_Packets[l_Index].DecodedFramePosition = l_Position;
				l_Position += _Packets[l_Index].SampleCount;
			}
			DecodedFrameCount = l_Position;
			return 0;
		}

		private void ResolveMissingTimestamps()
		{
			for (var l_Index = 0; l_Index < _Packets.Count; l_Index++)
			{
				if (_Packets[l_Index].PresentationTimestamp != DemuxedAudioPacket.NoTimestamp) continue;
				var l_Next = l_Index + 1;
				while (l_Next < _Packets.Count && _Packets[l_Next].PresentationTimestamp == DemuxedAudioPacket.NoTimestamp) l_Next++;
				if (l_Next < _Packets.Count)
				{
					var l_Timestamp = _Packets[l_Next].PresentationTimestamp;
					for (var l_Back = l_Next - 1; l_Back >= l_Index; l_Back--)
					{
						l_Timestamp -= _Packets[l_Back].Duration;
						_Packets[l_Back].PresentationTimestamp = l_Timestamp;
						_Packets[l_Back].DecodeTimestamp = l_Timestamp;
					}
				} else if (l_Index > 0)
				{
					_Packets[l_Index].PresentationTimestamp = _Packets[l_Index - 1].PresentationTimestamp + _Packets[l_Index - 1].Duration;
					_Packets[l_Index].DecodeTimestamp = _Packets[l_Index].PresentationTimestamp;
				}
			}
		}

		private int FindPacketByTimestamp(long a_Timestamp)
		{
			if (_Packets.Count == 0) return -1;
			var l_Low = 0; var l_High = _Packets.Count - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Packets[l_Middle].PresentationTimestamp <= a_Timestamp) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			return l_Low;
		}

		private static long ReadPesTimestamp(ReadOnlySpan<byte> a_Data)
		{
			return ((long)(a_Data[0] >> 1 & 7) << 30) | ((long)a_Data[1] << 22) |
				((long)(a_Data[2] >> 1) << 15) | ((long)a_Data[3] << 7) | (uint)(a_Data[4] >> 1);
		}

		private static long RescaleNearest(long a_Value, long a_Numerator, long a_Denominator)
		{
			return checked((a_Value * a_Numerator + a_Denominator / 2) / a_Denominator);
		}

		private static byte[] BuildOpusHead(int a_Channels)
		{
			if (a_Channels < 1 || a_Channels > 8) return null;
			var l_MappingFamily = a_Channels <= 2 ? 0 : 1;
			var l_Data = new byte[l_MappingFamily == 0 ? 19 : 21 + a_Channels];
			"OpusHead"u8.CopyTo(l_Data);
			l_Data[8] = 1;
			l_Data[9] = (byte)a_Channels;
			BinaryPrimitives.WriteInt32LittleEndian(l_Data.AsSpan(12, 4), 48000);
			l_Data[18] = (byte)l_MappingFamily;
			if (l_MappingFamily == 0) return l_Data;
			ReadOnlySpan<byte> l_Streams = stackalloc byte[] { 0, 0, 2, 2, 3, 4, 4, 5 };
			ReadOnlySpan<byte> l_Coupled = stackalloc byte[] { 0, 0, 1, 2, 2, 2, 3, 3 };
			ReadOnlySpan<byte> l_Mapping = a_Channels switch
			{
				3 => stackalloc byte[] { 0, 2, 1 },
				4 => stackalloc byte[] { 0, 1, 2, 3 },
				5 => stackalloc byte[] { 0, 4, 1, 2, 3 },
				6 => stackalloc byte[] { 0, 4, 1, 2, 3, 5 },
				7 => stackalloc byte[] { 0, 4, 1, 2, 3, 5, 6 },
				_ => stackalloc byte[] { 0, 6, 1, 2, 3, 4, 5, 7 }
			};
			l_Data[19] = l_Streams[a_Channels - 1];
			l_Data[20] = l_Coupled[a_Channels - 1];
			l_Mapping.CopyTo(l_Data.AsSpan(21));
			return l_Data;
		}

		private sealed class TsAudioPacket
		{
			public byte[] Data { get; }
			public long Position { get; }
			public long PresentationTimestamp { get; set; }
			public long DecodeTimestamp { get; set; }
			public long Duration { get; }
			public int SampleCount { get; }
			public bool Corrupt { get; }
			public long DecodedFramePosition { get; set; }

			public TsAudioPacket(byte[] a_Data, long a_Position, long a_Pts, long a_Dts, long a_Duration, int a_SampleCount, bool a_Corrupt)
			{
				Data = a_Data; Position = a_Position; PresentationTimestamp = a_Pts; DecodeTimestamp = a_Dts;
				Duration = a_Duration; SampleCount = a_SampleCount; Corrupt = a_Corrupt;
			}
		}

		private readonly struct PesSegment
		{
			public long Start { get; }
			public long End { get; }
			public long PresentationTimestamp { get; }
			public long DecodeTimestamp { get; }
			public long Position { get; }

			public PesSegment(long a_Start, int a_Length, long a_Pts, long a_Dts, long a_Position)
			{
				Start = a_Start; End = a_Start + a_Length; PresentationTimestamp = a_Pts; DecodeTimestamp = a_Dts; Position = a_Position;
			}
		}
	}
}
