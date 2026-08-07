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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports FFmpeg's raw MP1/MP2/MP3 path, including ID3 skipping, sync validation, Xing/Info, VBRI, and LAME gapless fields.
	/// </summary>
	public sealed class MpegAudioDemuxer : ISeekableAudioDemuxer
	{
		private const long TimeBaseDenominator = 14112000;
		private readonly FormatReader _Reader;
		private readonly byte[] _HeaderBuffer = new byte[10];
		private MpegAudioFrame[] _Frames = Array.Empty<MpegAudioFrame>();
		private int _CurrentFrame;
		private long _PacketDuration;

		public MpegAudioDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo { CodecId = AudioCodecId.Mp3, TimeBaseNumerator = 1, TimeBaseDenominator = TimeBaseDenominator };
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => _Frames.Length == 0 ? 0 : _Frames[0].Timestamp;

		/// <summary>
		/// Finds two mask-compatible frames, parses optional VBR metadata, and materializes FFmpeg-parser-equivalent frame packets.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0)) return FfmpegError.InvalidArgument;
			var searchStart = SkipId3v2Tags();
			if (searchStart < 0) return FfmpegError.InvalidData;
			var vbrFrames = 0U; var vbrSize = 0U; var isConstantBitRateInfo = false;
			var firstPacketSearch = searchStart;
			if (TryReadHeader(searchStart, out var tagHeader) && tagHeader.Layer == 3 &&
				ParseVbrTags(searchStart, tagHeader, ref vbrFrames, ref vbrSize, ref isConstantBitRateInfo))
				firstPacketSearch += tagHeader.CodedFrameSize;
			if (!FindFirstFrame(firstPacketSearch, out var firstPacketPosition, out var firstHeader)) return FfmpegError.InvalidData;

			var frames = ScanFrames(firstPacketPosition, firstHeader);
			if (frames.Count == 0) return FfmpegError.InvalidData;
			_Frames = frames.ToArray(); _CurrentFrame = 0;
			StreamInfo.CodecId = firstHeader.CodecId; StreamInfo.SampleRate = firstHeader.SampleRate; StreamInfo.Channels = firstHeader.Channels;
			StreamInfo.BitRate = firstHeader.BitRate; StreamInfo.TimeBaseNumerator = 1; StreamInfo.TimeBaseDenominator = TimeBaseDenominator;
			_PacketDuration = Rescale(firstHeader.SamplesPerFrame, TimeBaseDenominator, firstHeader.SampleRate);
			for (var index = 0; index < _Frames.Length; index++) _Frames[index].Timestamp = index * _PacketDuration;

			if (vbrFrames != 0)
			{
				var fullSamples = (long)vbrFrames * firstHeader.SamplesPerFrame;
				StreamInfo.Duration = Rescale(fullSamples - StreamInfo.StartSkipSamples + 529 - StreamInfo.EndPaddingSamples - 529, TimeBaseDenominator, firstHeader.SampleRate);
				if (vbrSize != 0 && !isConstantBitRateInfo) StreamInfo.BitRate = Rescale(vbrSize, 8L * firstHeader.SampleRate, fullSamples);
			} else if (_Reader.Length < 64 * 1024)
			{
				StreamInfo.Duration = _Frames.Length * _PacketDuration;
			} else
			{
				StreamInfo.Duration = Rescale(_Reader.Length - firstPacketPosition, 8L * TimeBaseDenominator, firstHeader.BitRate);
			}
			return 0;
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (_CurrentFrame >= _Frames.Length) return FfmpegError.EndOfFile;
			ref var source = ref _Frames[_CurrentFrame];
			if (destination.Length < source.Size || !_Reader.Seek(source.Position)) return FfmpegError.InvalidArgument;
			var read = _Reader.Read(destination.Slice(0, source.Size));
			if (read != source.Size) return FfmpegError.EndOfFile;
			var skip = _CurrentFrame == 0 ? StreamInfo.StartSkipSamples : 0;
			var discard = _CurrentFrame == _Frames.Length - 1 ? StreamInfo.EndPaddingSamples : 0;
			packet = new DemuxedAudioPacket(read, source.Position, source.Timestamp, source.Timestamp, _PacketDuration, 0, false, skip, discard);
			_CurrentFrame++;
			return read;
		}

		/// <summary>Uses the scanned MPEG frame table to select the closest packet not after the requested timestamp.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			var l_Index = FindFrameAtOrBefore(a_Timestamp);
			if (l_Index < 0)
			{
				a_ActualTimestamp = 0;
				return false;
			}
			_CurrentFrame = l_Index;
			a_ActualTimestamp = _Frames[l_Index].Timestamp;
			return true;
		}

		private int FindFrameAtOrBefore(long a_Timestamp)
		{
			if (_Frames.Length == 0)
				return -1;
			var l_Low = 0;
			var l_High = _Frames.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Frames[l_Middle].Timestamp <= a_Timestamp)
					l_Low = l_Middle;
				else
					l_High = l_Middle - 1;
			}
			return l_Low;
		}

		private long SkipId3v2Tags()
		{
			var position = 0L;
			while (position <= _Reader.Length - 10)
			{
				if (!_Reader.Seek(position) || !_Reader.ReadExactly(_HeaderBuffer)) return -1;
				if (_HeaderBuffer[0] != (byte)'I' || _HeaderBuffer[1] != (byte)'D' || _HeaderBuffer[2] != (byte)'3' ||
					_HeaderBuffer[3] == 0xff || _HeaderBuffer[4] == 0xff || (_HeaderBuffer[6] & 0x80) != 0 ||
					(_HeaderBuffer[7] & 0x80) != 0 || (_HeaderBuffer[8] & 0x80) != 0 || (_HeaderBuffer[9] & 0x80) != 0) break;
				var length = ((_HeaderBuffer[6] & 0x7f) << 21) + ((_HeaderBuffer[7] & 0x7f) << 14) +
					((_HeaderBuffer[8] & 0x7f) << 7) + (_HeaderBuffer[9] & 0x7f) + 10;
				if ((_HeaderBuffer[5] & 0x10) != 0) length += 10;
				position += length;
			}
			return position;
		}

		private bool FindFirstFrame(long start, out long position, out MpegAudioHeader header)
		{
			var end = Math.Min(_Reader.Length - 4, start + 64 * 1024);
			for (position = start; position <= end; position++)
			{
				if (!TryReadHeader(position, out header) || position + header.CodedFrameSize > _Reader.Length) continue;
				if (position + header.CodedFrameSize + 4 > _Reader.Length) return true;
				if (TryReadHeader(position + header.CodedFrameSize, out var next) &&
					(ReadHeaderValue(position) & MpegAudioHeader.HeaderMask) == (ReadHeaderValue(position + header.CodedFrameSize) & MpegAudioHeader.HeaderMask)) return true;
			}
			header = default; position = 0; return false;
		}

		private List<MpegAudioFrame> ScanFrames(long start, MpegAudioHeader streamHeader)
		{
			var result = new List<MpegAudioFrame>(); var position = start;
			while (position <= _Reader.Length - 4)
			{
				if (TryReadHeader(position, out var header))
				{
					var available = checked((int)Math.Min(header.CodedFrameSize, _Reader.Length - position));
					result.Add(new MpegAudioFrame { Position = position, Size = available }); position += header.CodedFrameSize;
				} else position++;
			}
			return result;
		}

		/// <summary>
		/// Parses the first Layer III frame in FFmpeg's Xing/Info then VBRI order and derives LAME encoder delay/padding.
		/// </summary>
		private bool ParseVbrTags(long framePosition, MpegAudioHeader header, ref uint frameCount, ref uint fileSize, ref bool isConstantBitRate)
		{
			var offsets = new[,] { { 32, 17 }, { 17, 9 } };
			var tagPosition = framePosition + 4 + offsets[header.LowSamplingFrequency == 1 ? 1 : 0, header.Channels == 1 ? 1 : 0];
			if (_Reader.Seek(tagPosition) && _Reader.ReadUInt32BigEndian(out var marker) &&
				(marker == 0x58696e67U || marker == 0x496e666fU) && _Reader.ReadUInt32BigEndian(out var flags))
			{
				isConstantBitRate = marker == 0x496e666fU;
				if ((flags & 1) != 0 && !_Reader.ReadUInt32BigEndian(out frameCount)) return false;
				if ((flags & 2) != 0 && !_Reader.ReadUInt32BigEndian(out fileSize)) return false;
				if ((flags & 4) != 0 && !_Reader.Skip(100)) return false;
				if ((flags & 8) != 0 && !_Reader.Skip(4)) return false;
				Span<byte> version = stackalloc byte[9];
				if (!_Reader.ReadExactly(version) || !_Reader.Skip(12)) return true;
				Span<byte> padding = stackalloc byte[3];
				if (!_Reader.ReadExactly(padding)) return true;
				if ((version[0] == (byte)'L' && version[1] == (byte)'A' && version[2] == (byte)'M' && version[3] == (byte)'E') ||
					(version[0] == (byte)'L' && version[1] == (byte)'a' && version[2] == (byte)'v' && (version[3] == (byte)'f' || version[3] == (byte)'c')))
				{
					var value = (padding[0] << 16) | (padding[1] << 8) | padding[2]; var startPadding = value >> 12; var endPadding = value & 4095;
					StreamInfo.StartSkipSamples = startPadding + 529; StreamInfo.EndPaddingSamples = Math.Max(0, endPadding - 529);
				}
				return frameCount != 0 || fileSize != 0;
			}

			if (_Reader.Seek(framePosition + 4 + 32) && _Reader.ReadUInt32BigEndian(out var vbri) && vbri == 0x56425249U &&
				_Reader.ReadUInt16BigEndian(out var versionNumber) && versionNumber == 1 && _Reader.Skip(4) &&
				_Reader.ReadUInt32BigEndian(out fileSize) && _Reader.ReadUInt32BigEndian(out frameCount)) return true;
			return false;
		}

		private bool TryReadHeader(long position, out MpegAudioHeader header)
		{
			header = default; if (!_Reader.Seek(position) || !_Reader.ReadUInt32BigEndian(out var value)) return false;
			return header.Decode(value) == 0;
		}

		private uint ReadHeaderValue(long position)
		{
			return _Reader.Seek(position) && _Reader.ReadUInt32BigEndian(out var value) ? value : 0;
		}

		private static long Rescale(long value, long numerator, long denominator)
		{
			return checked((value * numerator + denominator / 2) / denominator);
		}

		private struct MpegAudioFrame
		{
			public long Position;
			public int Size;
			public long Timestamp;
		}
	}
}
