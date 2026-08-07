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
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Codecs.Dca;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports FFmpeg's raw DTS parser/demuxer packet boundaries, core-stream timestamps, and direct seek index with a buffered vectorized sync scan.
	/// </summary>
	public sealed class DtsRawDemuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer
	{
		private const int RawTimeBase = 90000;
		private const int ScanBufferSize = 128 * 1024;
		private const int ScanOverlap = 5;
		private static readonly byte[] s_LbrLayoutChannels = { 1, 2, 3, 2, 3, 4, 5 };
		private static readonly SearchValues<byte> s_SyncFirstBytes = SearchValues.Create(new byte[] { 0x1f, 0x64, 0x7f, 0xfe, 0xff });
		private readonly FormatReader _Reader;
		private readonly byte[] _Header = new byte[32];
		private readonly byte[] _NormalizedHeader = new byte[DcaBitstream.CoreFrameHeaderSize];
		private readonly byte[] _ExtensionFrame = new byte[0x104000];
		private readonly byte[] _ScanBuffer = new byte[ScanBufferSize + ScanOverlap];
		private readonly DcaExssParser _ExtensionParser = new DcaExssParser();
		private readonly BitReader _ExtensionBits = new BitReader();
		private DtsRawFrame[] _Frames = Array.Empty<DtsRawFrame>();
		private int _CurrentFrame;

		public DtsRawDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo { CodecId = AudioCodecId.Dts, TimeBaseNumerator = 1, TimeBaseDenominator = RawTimeBase };
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => _Frames.Length == 0 ? 0 : _Frames[0].Timestamp;

		/// <summary>
		/// Scans DTS core and extension-substream markers sequentially, joining a trailing EXSS frame to its aligned core frame.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0) || _Reader.Length < 4) return FfmpegError.InvalidArgument;
			var markers = FindParserMarkers();
			if (markers.Count == 0) return FfmpegError.InvalidData;

			var frames = new List<DtsRawFrame>();
			var timestamp = 0L;
			var decodedFramePosition = 0L;
			var firstSampleRate = 0;
			var firstChannels = 0;
			var firstBitRate = 0L;
			var lbrSampleRateCode = -1;
			for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
			{
				var position = markers[markerIndex];
				if (TryReadCoreHeader(position, out var header))
				{
					var streamSampleRate = header.SampleRate * (header.ExtensionAudioPresent != 0 && header.ExtensionAudioType == 2 ? 2 : 1);
					var streamChannels = header.Channels + (header.ExtensionAudioPresent != 0 && header.ExtensionAudioType == 0 ? 1 : 0);
					var streamBitRate = (long)DcaTables.BitRates[header.BitRateCode];
					if (streamBitRate <= 3) streamBitRate = 0;
					var physicalCoreSize = GetPhysicalCoreFrameSize(position, header.FrameSize);
					if (physicalCoreSize <= 0) continue;
					var minimumPacketEnd = Math.Min(position + physicalCoreSize, _Reader.Length);
					var alignedCoreEnd = Math.Min(position + ((physicalCoreSize + 3) & ~3), _Reader.Length);
					if (alignedCoreEnd <= _Reader.Length - 4 && TryReadMarker(alignedCoreEnd, out var extensionMarker) && extensionMarker == DcaBitstream.ExtensionSubstreamSyncWord)
					{
						var extensionSize = ReadExtensionSubstreamSize(alignedCoreEnd);
						if (extensionSize > 0)
						{
							minimumPacketEnd = Math.Min(alignedCoreEnd + extensionSize, _Reader.Length);
							if (TryReadExtensionFrame(alignedCoreEnd, extensionSize) && _ExtensionParser.Parse(_ExtensionFrame, 0, extensionSize) >= 0)
							{
								var asset = _ExtensionParser.Asset;
								if (asset.MaximumSampleRate != 0) streamSampleRate = asset.MaximumSampleRate;
								if (asset.TotalChannels != 0) streamChannels = asset.TotalChannels;
								if ((asset.ExtensionMask & 0x3e0) != 0) streamBitRate = 0;
							}
						}
					}
					TryReadMarker(position, out var coreMarker);
					var packetEnd = FindNextPacketMarker(markers, markerIndex + 1, minimumPacketEnd, coreMarker, false);
					if (firstSampleRate == 0)
					{
						firstSampleRate = streamSampleRate;
						firstChannels = streamChannels;
						firstBitRate = streamBitRate;
					}
					var duration = (long)header.PcmBlocks * 32 * RawTimeBase / header.SampleRate;
					frames.Add(new DtsRawFrame(position, frames.Count == 0 ? 0 : position, checked((int)(packetEnd - position)), timestamp, duration, decodedFramePosition));
					timestamp += duration;
					decodedFramePosition += (long)header.PcmBlocks * 32 * streamSampleRate / header.SampleRate;
					while (markerIndex + 1 < markers.Count && markers[markerIndex + 1] < packetEnd) markerIndex++;
					continue;
				}

				if (!TryReadMarker(position, out var marker) || marker != DcaBitstream.ExtensionSubstreamSyncWord) continue;
				var standaloneSize = ReadExtensionSubstreamSize(position);
				if (standaloneSize <= 0 || position > _Reader.Length - standaloneSize || !TryReadExtensionFrame(position, standaloneSize)) continue;
				if (!TryReadStandaloneMetadata(standaloneSize, ref lbrSampleRateCode, firstChannels, out var sampleRate, out var channels, out var sampleCount, out var bitRate)) continue;
				if (firstSampleRate == 0)
				{
					firstSampleRate = sampleRate;
					firstChannels = channels;
					firstBitRate = bitRate;
				}
				var standaloneDuration = (long)sampleCount * RawTimeBase / sampleRate;
				var standaloneEnd = FindNextPacketMarker(markers, markerIndex + 1, position + standaloneSize, marker, true);
				frames.Add(new DtsRawFrame(position, frames.Count == 0 ? 0 : position, checked((int)(standaloneEnd - position)), timestamp, standaloneDuration, decodedFramePosition));
				timestamp += standaloneDuration;
				decodedFramePosition += sampleCount;
				while (markerIndex + 1 < markers.Count && markers[markerIndex + 1] < standaloneEnd) markerIndex++;
			}
			if (firstSampleRate == 0 || frames.Count == 0) return FfmpegError.InvalidData;
			_Frames = frames.ToArray();
			_CurrentFrame = 0;
			StreamInfo.SampleRate = firstSampleRate;
			StreamInfo.Channels = firstChannels;
			StreamInfo.BitRate = firstBitRate;
			StreamInfo.Duration = firstBitRate == 0 ? 0 : (_Reader.Length * 8 * RawTimeBase + firstBitRate / 2) / firstBitRate;
			return 0;
		}

		/// <summary>Finds parser sync markers in buffered blocks with a vectorized search over the possible first bytes.</summary>
		private List<long> FindParserMarkers()
		{
			var l_Markers = new List<long>();
			var l_Overlap = 0;
			var l_BufferPosition = 0L;
			while (true)
			{
				var l_Read = _Reader.Read(_ScanBuffer.AsSpan(l_Overlap, ScanBufferSize));
				var l_Length = l_Overlap + l_Read;
				var l_EndOfFile = _Reader.Position >= _Reader.Length || l_Read == 0;
				var l_ProcessLength = l_EndOfFile ? l_Length : l_Length - ScanOverlap;
				var l_SearchOffset = 0;
				while (l_SearchOffset < l_ProcessLength)
				{
					var l_FoundOffset = _ScanBuffer.AsSpan(l_SearchOffset, l_ProcessLength - l_SearchOffset).IndexOfAny(s_SyncFirstBytes);
					if (l_FoundOffset < 0) break;
					var l_Offset = l_SearchOffset + l_FoundOffset;
					if (IsParserMarker(_ScanBuffer.AsSpan(l_Offset, l_Length - l_Offset))) l_Markers.Add(l_BufferPosition + l_Offset);
					l_SearchOffset = l_Offset + 1;
				}
				if (l_EndOfFile) break;
				Array.Copy(_ScanBuffer, l_Length - ScanOverlap, _ScanBuffer, 0, ScanOverlap);
				l_BufferPosition += l_Length - ScanOverlap;
				l_Overlap = ScanOverlap;
			}
			return l_Markers;
		}

		private static bool IsParserMarker(ReadOnlySpan<byte> a_Data)
		{
			if (a_Data.Length < 4) return false;
			var l_Marker = BinaryPrimitives.ReadUInt32BigEndian(a_Data);
			if (!DcaBitstream.IsSyncWord(l_Marker)) return false;
			if (l_Marker == DcaBitstream.ExtensionSubstreamSyncWord) return true;
			if (a_Data.Length < 6) return false;
			var l_State = (ulong)l_Marker << 16 | (ulong)a_Data[4] << 8 | a_Data[5];
			switch (l_Marker)
			{
				case DcaBitstream.Core14LittleEndianSyncWord: return (l_State & 0xfffffffff0ffUL) == ((ulong)l_Marker << 16 | 0xf007UL);
				case DcaBitstream.Core14BigEndianSyncWord: return (l_State & 0xfffffffffff0UL) == ((ulong)l_Marker << 16 | 0x07f0UL);
				case DcaBitstream.CoreLittleEndianSyncWord: return (l_State & 0xffffffff00fcUL) == ((ulong)l_Marker << 16 | 0x00fcUL);
				case DcaBitstream.CoreBigEndianSyncWord: return (l_State & 0xfffffffffc00UL) == ((ulong)l_Marker << 16 | 0xfc00UL);
				default: return false;
			}
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (_CurrentFrame >= _Frames.Length) return FfmpegError.EndOfFile;
			ref var frame = ref _Frames[_CurrentFrame];
			if (destination.Length < frame.Size || !_Reader.Seek(frame.ReadPosition)) return FfmpegError.InvalidArgument;
			var read = _Reader.Read(destination.Slice(0, frame.Size));
			if (read != frame.Size) return FfmpegError.EndOfFile;
			packet = new DemuxedAudioPacket(read, frame.Position, frame.Timestamp, frame.Timestamp, frame.Duration, 0, false);
			_CurrentFrame++;
			return read;
		}

		/// <summary>Uses the scanned DTS access-unit table for direct timestamp seeks.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (_Frames.Length == 0) { a_ActualTimestamp = 0; return false; }
			var l_Low = 0; var l_High = _Frames.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Frames[l_Middle].Timestamp <= a_Timestamp) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			_CurrentFrame = l_Low; a_ActualTimestamp = _Frames[l_Low].Timestamp; return true;
		}

		/// <summary>Uses exact cumulative DTS sample counts instead of the rounded 90 kHz packet durations.</summary>
		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			if (_Frames.Length == 0) { a_ActualFrameIndex = 0; return false; }
			var l_Low = 0; var l_High = _Frames.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Frames[l_Middle].DecodedFramePosition <= a_FrameIndex) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			_CurrentFrame = l_Low; a_ActualFrameIndex = _Frames[l_Low].DecodedFramePosition; return true;
		}

		private bool TryReadCoreHeader(long position, out DcaCoreFrameHeader header)
		{
			header = default;
			if (position > _Reader.Length - DcaBitstream.CoreFrameHeaderSize || !_Reader.Seek(position) || !_Reader.ReadExactly(_Header)) return false;
			var converted = DcaBitstream.ConvertBitstream(_Header, 0, _Header.Length, _NormalizedHeader, _NormalizedHeader.Length);
			return converted >= DcaBitstream.CoreFrameHeaderSize && DcaBitstream.ParseCoreFrameHeader(_NormalizedHeader, 0, converted, out header) == 0;
		}

		private int GetPhysicalCoreFrameSize(long position, int normalizedSize)
		{
			if (!TryReadMarker(position, out var marker)) return 0;
			return marker == DcaBitstream.Core14BigEndianSyncWord || marker == DcaBitstream.Core14LittleEndianSyncWord
				? (normalizedSize * 16 + 13) / 14 : normalizedSize;
		}

		private long FindNextPacketMarker(List<long> markers, int startIndex, long minimumPosition, uint currentMarker, bool acceptAnyMarker)
		{
			for (var index = startIndex; index < markers.Count; index++)
			{
				var position = markers[index];
				if (position < minimumPosition || !TryReadMarker(position, out var marker)) continue;
				if (acceptAnyMarker || marker == currentMarker) return position;
			}
			return _Reader.Length;
		}

		private bool TryReadMarker(long position, out uint marker)
		{
			marker = 0;
			if (!_Reader.Seek(position) || !_Reader.ReadExactly(_Header.AsSpan(0, 4))) return false;
			marker = BinaryPrimitives.ReadUInt32BigEndian(_Header.AsSpan(0, 4));
			return true;
		}

		private int ReadExtensionSubstreamSize(long position)
		{
			if (position > _Reader.Length - 10 || !_Reader.Seek(position) || !_Reader.ReadExactly(_Header.AsSpan(0, 10))) return 0;
			var state = 0UL;
			for (var index = 0; index < 10; index++) state = state << 8 | _Header[index];
			return (state & 0x2000000000UL) != 0 ? (int)((state >> 5) & 0xfffff) + 1 : (int)((state >> 13) & 0xffff) + 1;
		}

		private bool TryReadExtensionFrame(long position, int size)
		{
			return size <= _ExtensionFrame.Length && _Reader.Seek(position) && _Reader.ReadExactly(_ExtensionFrame.AsSpan(0, size));
		}

		private bool TryReadStandaloneMetadata(int extensionSize, ref int sampleRateCode, int knownChannels, out int sampleRate, out int channels, out int sampleCount, out int bitRate)
		{
			sampleRate = channels = sampleCount = bitRate = 0;
			if (_ExtensionParser.Parse(_ExtensionFrame, 0, extensionSize) < 0) return false;
			if ((_ExtensionParser.Asset.ExtensionMask & 0x100) == 0) return TryReadXllMetadata(extensionSize, out sampleRate, out channels, out sampleCount);
			var offset = _ExtensionParser.Asset.LbrOffset;
			var size = _ExtensionParser.Asset.LbrSize;
			if (offset < 0 || size < 5 || offset > extensionSize - size || BinaryPrimitives.ReadUInt32BigEndian(_ExtensionFrame.AsSpan(offset, 4)) != 0x0a801921) return false;
			var headerType = _ExtensionFrame[offset + 4];
			if (headerType == 2)
			{
				if (size < 16) return false;
				sampleRateCode = _ExtensionFrame[offset + 5];
				if ((uint)sampleRateCode >= DcaTables.SamplingFrequencies.Length) return false;
				var channelMask = BinaryPrimitives.ReadUInt16LittleEndian(_ExtensionFrame.AsSpan(offset + 6, 2));
				var flags = _ExtensionFrame[offset + 10];
				var highBitRate = _ExtensionFrame[offset + 11];
				bitRate = BinaryPrimitives.ReadUInt16LittleEndian(_ExtensionFrame.AsSpan(offset + 14, 2)) | (highBitRate & 0xf0) << 12;
				if ((channelMask & 7) == 0) return false;
				channels = s_LbrLayoutChannels[(channelMask & 7) - 1] + (((flags & 2) != 0 && DcaTables.SamplingFrequencies[sampleRateCode] == 48000) ? 1 : 0);
				if ((flags & 0x20) != 0) channels = 2;
			} else if (headerType != 1) return false;
			if ((uint)sampleRateCode >= DcaTables.SamplingFrequencies.Length) return false;
			sampleRate = DcaTables.SamplingFrequencies[sampleRateCode];
			if (sampleRate == 0) return false;
			if (channels == 0) channels = knownChannels;
			sampleCount = 1024 << DcaTables.FrequencyRanges[sampleRateCode];
			return channels != 0;
		}

		private bool TryReadXllMetadata(int extensionSize, out int sampleRate, out int channels, out int sampleCount)
		{
			sampleRate = channels = sampleCount = 0;
			var asset = _ExtensionParser.Asset;
			if ((asset.ExtensionMask & 0x200) == 0 || asset.XllOffset < 0 || asset.XllSize < 8 || asset.XllOffset > extensionSize - asset.XllSize ||
				_ExtensionBits.Initialize(_ExtensionFrame, asset.XllOffset, asset.XllSize * 8) < 0 || _ExtensionBits.ReadBitsLong(32) != 0x41a29547 ||
				_ExtensionBits.ReadBits(4) != 0) return false;
			_ExtensionBits.SkipBits(8);
			_ExtensionBits.SkipBits((int)_ExtensionBits.ReadBits(5) + 1);
			_ExtensionBits.SkipBits(4);
			var sampleCountLog2 = (int)_ExtensionBits.ReadBits(4) + (int)_ExtensionBits.ReadBits(4);
			if (sampleCountLog2 > 24 || asset.MaximumSampleRate == 0 || asset.TotalChannels == 0) return false;
			sampleRate = asset.MaximumSampleRate;
			channels = asset.TotalChannels;
			sampleCount = (1 + (sampleRate > 96000 ? 1 : 0)) << sampleCountLog2;
			return true;
		}

		private readonly struct DtsRawFrame
		{
			public readonly long ReadPosition;
			public readonly long Position;
			public readonly int Size;
			public readonly long Timestamp;
			public readonly long Duration;
			public readonly long DecodedFramePosition;

			public DtsRawFrame(long readPosition, long position, int size, long timestamp, long duration, long decodedFramePosition)
			{
				ReadPosition = readPosition;
				Position = position;
				Size = size;
				Timestamp = timestamp;
				Duration = duration;
				DecodedFramePosition = decodedFramePosition;
			}
		}
	}
}
