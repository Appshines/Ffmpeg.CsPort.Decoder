// SPDX-FileCopyrightText: The respective FFmpeg copyright holders; see UPSTREAM-COPYRIGHTS.txt
// SPDX-FileCopyrightText: 2026 Ffmpeg.CsPort.Decoder contributors
// SPDX-License-Identifier: LGPL-2.1-or-later AND MIT
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
/*
 * MIT-licensed Ogg portions:
 * Copyright (C) 2005 Michael Ahlberg, Måns Rullgård
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
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
	/// Parses Ogg pages and exposes FFmpeg-compatible Vorbis and Opus metadata, packets, granule timing, and trimming.
	/// </summary>
	public sealed class OggAudioDemuxer : ISeekableAudioDemuxer
	{
		private const ulong MissingGranule = ulong.MaxValue;

		private readonly Stream stream;
		private readonly List<OggStreamState> streams = new List<OggStreamState>();
		private readonly List<OggPacket> packets = new List<OggPacket>();
		private int packetIndex;
		private OggStreamState selectedStream;

		public OggAudioDemuxer(Stream stream)
		{
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => packets.Count == 0 ? 0 : packets[0].PresentationTimestamp;

		/// <summary>
		/// Reads all logical pages once so continued packets, final granule trimming, and stream duration can be resolved exactly.
		/// </summary>
		public int ReadHeader()
		{
			try
			{
				if (!stream.CanSeek)
					return FfmpegError.InvalidArgument;
				stream.Position = 0;
				streams.Clear();
				packets.Clear();
				packetIndex = 0;
				selectedStream = null;
				while (stream.Position < stream.Length)
				{
					var result = ReadPage();
					if (result < 0)
						return result;
				}

				if (selectedStream == null || !selectedStream.HeadersComplete)
					return FfmpegError.InvalidData;
				FinalizeTiming(selectedStream);
				if (selectedStream.CodecId == AudioCodecId.Opus)
					ApplyOpusPreSkip(selectedStream);
				StreamInfo.StreamIndex = selectedStream.Index;
				StreamInfo.CodecId = selectedStream.CodecId;
				StreamInfo.SampleRate = selectedStream.SampleRate;
				StreamInfo.Channels = selectedStream.Channels;
				StreamInfo.BitRate = selectedStream.BitRate;
				StreamInfo.Duration = selectedStream.FinalGranule < 0 ? 0 : selectedStream.FinalGranule;
				StreamInfo.TimeBaseNumerator = 1;
				StreamInfo.TimeBaseDenominator = selectedStream.SampleRate;
				StreamInfo.StartSkipSamples = selectedStream.PreSkip;
				StreamInfo.EndPaddingSamples = packets.Count == 0 ? 0 : packets[packets.Count - 1].DiscardPadding;
				StreamInfo.CodecExtraData = selectedStream.CodecId == AudioCodecId.Opus
					? selectedStream.Identification
					: CreateXiphHeaders(selectedStream);
				return 0;
			} catch (EndOfStreamException)
			{
				return FfmpegError.EndOfFile;
			} catch (IOException)
			{
				return FfmpegError.InvalidData;
			} catch (OverflowException)
			{
				return FfmpegError.InvalidData;
			}
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (packetIndex >= packets.Count)
				return FfmpegError.EndOfFile;
			var source = packets[packetIndex];
			if (destination.Length < source.Data.Length)
				return FfmpegError.InvalidArgument;
			source.Data.AsSpan().CopyTo(destination);
			packetIndex++;
			packet = new DemuxedAudioPacket(
				source.Data.Length,
				source.Position,
				source.PresentationTimestamp,
				source.PresentationTimestamp,
				source.Duration,
				source.StreamIndex,
				false,
				source.SkipSamples,
				source.DiscardPadding);
			return source.Data.Length;
		}

		/// <summary>Uses the packet/granule index assembled during Ogg header parsing for direct seeks.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (packets.Count == 0) { a_ActualTimestamp = 0; return false; }
			var l_Low = 0; var l_High = packets.Count - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (packets[l_Middle].PresentationTimestamp <= a_Timestamp) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			packetIndex = l_Low; a_ActualTimestamp = packets[l_Low].PresentationTimestamp; return true;
		}

		/// <summary>
		/// Validates and dispatches one physical page while preserving the packet start page across continuation boundaries.
		/// </summary>
		private int ReadPage()
		{
			var pagePosition = stream.Position;
			Span<byte> header = stackalloc byte[27];
			ReadExactly(header);
			if (header[0] != (byte)'O' || header[1] != (byte)'g' || header[2] != (byte)'g' || header[3] != (byte)'S' || header[4] != 0)
				return FfmpegError.InvalidData;

			var flags = header[5];
			var granule = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(6, 8));
			var serial = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(14, 4));
			var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(22, 4));
			var segments = new byte[header[26]];
			ReadExactly(segments);
			var payloadSize = 0;
			for (var index = 0; index < segments.Length; index++)
				payloadSize = checked(payloadSize + segments[index]);
			var payload = new byte[payloadSize];
			ReadExactly(payload);
			if (!ValidateChecksum(header, segments, payload, expectedChecksum))
				return FfmpegError.InvalidData;

			var state = FindOrCreateStream(serial);
			state.PageStartedWithPendingGranule = state.PendingGranule;
			state.PageDataPacketCount = 0;
			var packetStart = packets.Count;
			var payloadOffset = 0;
			var segmentIndex = 0;
			if ((flags & 1) != 0 && state.Assembly.Count == 0)
			{
				while (segmentIndex < segments.Length)
				{
					var segmentLength = segments[segmentIndex++];
					payloadOffset += segmentLength;
					if (segmentLength < 255)
						break;
				}
			}
			if (state.Assembly.Count == 0)
				state.PacketPosition = pagePosition;

			for (; segmentIndex < segments.Length; segmentIndex++)
			{
				var segmentLength = segments[segmentIndex];
				for (var index = 0; index < segmentLength; index++)
					state.Assembly.Add(payload[payloadOffset + index]);
				payloadOffset += segmentLength;
				if (segmentLength < 255)
				{
					CompletePacket(state, flags, granule);
					state.PacketPosition = pagePosition;
				}
			}

			if (selectedStream == state)
				FinalizePagePackets(state, packetStart, flags, granule);
			return 0;
		}

		private void CompletePacket(OggStreamState state, int flags, ulong granule)
		{
			var data = state.Assembly.ToArray();
			state.Assembly.Clear();
			if (!state.HeadersComplete)
			{
				ParseAudioHeader(state, data);
				return;
			}
			if (state.CodecId == AudioCodecId.Opus && IsOpusMetadataPacket(data))
				return;
			if (selectedStream != state || data.Length == 0)
				return;

			var carriesPreviousPage = state.PageDataPacketCount == 0 && state.PageStartedWithPendingGranule;
			if (state.CodecId == AudioCodecId.Vorbis && (flags & 4) == 0 && granule != MissingGranule && !carriesPreviousPage)
				state.DurationParser.Reset();
			var duration = ParsePacketDuration(state, data);
			if (duration < 0)
				return;
			packets.Add(new OggPacket(data, state.PacketPosition, state.Index, duration));
			state.PageDataPacketCount++;
			if (carriesPreviousPage)
				state.PendingGranule = false;
			if ((flags & 4) != 0 && granule != MissingGranule)
				state.FinalGranule = unchecked((long)granule);
		}

		/// <summary>
		/// Applies FFmpeg's first-page granule back-calculation and EOS-only final packet duration reduction.
		/// </summary>
		private void FinalizePagePackets(OggStreamState state, int firstPacket, int flags, ulong granule)
		{
			if (firstPacket >= packets.Count)
				return;
			if ((flags & 4) == 0 && granule != MissingGranule)
			{
				var backCalculatedStart = firstPacket;
				if (state.PageStartedWithPendingGranule)
				{
					packets[firstPacket].PresentationTimestamp = state.NextTimestamp;
					backCalculatedStart++;
				}
				for (var packet = backCalculatedStart; packet < packets.Count; packet++)
				{
					if (state.DurationParser != null)
						state.DurationParser.Reset();
					long remainingDuration = 0;
					for (var scan = packet; scan < packets.Count; scan++)
						remainingDuration += ParsePacketDuration(state, packets[scan].Data);
					packets[packet].PresentationTimestamp = unchecked((long)granule) - remainingDuration;
					if (state.DurationParser != null)
						state.DurationParser.Reset();
					packets[packet].Duration = ParsePacketDuration(state, packets[packet].Data);
				}
				state.NextTimestamp = unchecked((long)granule);
				state.HasTimestamp = true;
				state.PendingGranule = true;
				return;
			}

			for (var index = firstPacket; index < packets.Count; index++)
			{
				var packet = packets[index];
				packet.PresentationTimestamp = state.HasTimestamp ? state.NextTimestamp : DemuxedAudioPacket.NoTimestamp;
				state.NextTimestamp += packet.Duration;
				state.HasTimestamp = true;
			}
			if ((flags & 4) != 0 && granule != MissingGranule)
			{
				var last = packets[packets.Count - 1];
				var correctedDuration = unchecked((long)granule) - last.PresentationTimestamp;
				if (correctedDuration >= 0 && correctedDuration < last.Duration)
				{
					last.DiscardPadding = checked((int)(last.Duration - correctedDuration));
					last.Duration = correctedDuration;
					state.NextTimestamp = unchecked((long)granule);
				}
			}
		}

		private int ParseAudioHeader(OggStreamState state, byte[] data)
		{
			if (state.CodecId == AudioCodecId.Opus || (state.CodecId == AudioCodecId.None && IsOpusHead(data)))
				return ParseOpusHeader(state, data);
			return ParseVorbisHeader(state, data);
		}

		/// <summary>Accepts OpusHead and OpusTags while preserving the complete identification packet as decoder extradata.</summary>
		private int ParseOpusHeader(OggStreamState state, byte[] data)
		{
			if (state.HeaderCount == 0 && IsOpusHead(data))
			{
				if (data.Length < 19 || (data[8] & 0xf0) != 0)
					return FfmpegError.InvalidData;
				var channels = data[9];
				var mappingFamily = data[18];
				if (channels <= 0 || (mappingFamily == 0 && channels > 2) ||
					(mappingFamily != 0 && data.Length < 21 + channels))
					return FfmpegError.InvalidData;
				state.CodecId = AudioCodecId.Opus;
				state.Channels = channels;
				state.SampleRate = 48000;
				state.PreSkip = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10, 2));
				state.Identification = data;
				state.HeaderCount = 1;
				if (selectedStream == null)
					selectedStream = state;
				return 0;
			}
			if (state.HeaderCount == 1 && IsOpusTags(data))
			{
				state.Comment = data;
				state.HeaderCount = 2;
				state.HeadersComplete = true;
				return 0;
			}
			return FfmpegError.InvalidData;
		}

		/// <summary>
		/// Accepts the three ordered Vorbis headers and initializes the duration parser once setup modes are available.
		/// </summary>
		private int ParseVorbisHeader(OggStreamState state, byte[] data)
		{
			if (data.Length < 7 || data[1] != (byte)'v' || data[2] != (byte)'o' || data[3] != (byte)'r' ||
				data[4] != (byte)'b' || data[5] != (byte)'i' || data[6] != (byte)'s')
				return FfmpegError.InvalidData;

			if (state.HeaderCount == 0 && data[0] == 1)
			{
				if (data.Length != 30 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(7, 4)) != 0 || (data[29] & 1) == 0)
					return FfmpegError.InvalidData;
				state.Channels = data[11];
				state.SampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4));
				state.BitRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20, 4));
				var smallExponent = data[28] & 15;
				var largeExponent = data[28] >> 4;
				if (state.Channels <= 0 || state.SampleRate <= 0 || smallExponent < 6 || largeExponent > 13 || smallExponent > largeExponent)
					return FfmpegError.InvalidData;
				state.Identification = data;
				state.CodecId = AudioCodecId.Vorbis;
				state.SmallBlockSize = 1 << smallExponent;
				state.LargeBlockSize = 1 << largeExponent;
				state.HeaderCount = 1;
				if (selectedStream == null)
					selectedStream = state;
				return 0;
			}
			if (state.HeaderCount == 1 && data[0] == 3)
			{
				state.Comment = data;
				state.HeaderCount = 2;
				return 0;
			}
			if (state.HeaderCount == 2 && data[0] == 5)
			{
				state.Setup = data;
				state.DurationParser = VorbisDurationParser.Create(data, state.SmallBlockSize, state.LargeBlockSize);
				if (state.DurationParser == null)
					return FfmpegError.InvalidData;
				state.HeaderCount = 3;
				state.HeadersComplete = true;
				return 0;
			}
			return FfmpegError.InvalidData;
		}

		private static int ParsePacketDuration(OggStreamState state, byte[] data)
		{
			if (state.CodecId == AudioCodecId.Opus)
				return ParseOpusDuration(data);
			return state.DurationParser == null ? FfmpegError.InvalidData : state.DurationParser.ParseDuration(data);
		}

		/// <summary>Ports the compact packet-duration calculation from FFmpeg's oggparseopus.c.</summary>
		private static int ParseOpusDuration(byte[] data)
		{
			if (data == null || data.Length == 0)
				return FfmpegError.InvalidData;
			var toc = data[0];
			var configuration = toc >> 3;
			var countCode = toc & 3;
			var frameSize = configuration < 12
				? Math.Max(480, 960 * (configuration & 3))
				: configuration < 16 ? 480 << (configuration & 1) : 120 << (configuration & 3);
			var frameCount = 1;
			if (countCode == 3)
			{
				if (data.Length < 2)
					return FfmpegError.InvalidData;
				frameCount = data[1] & 0x3f;
			} else if (countCode != 0)
			{
				frameCount = 2;
			}
			return frameSize * frameCount;
		}

		/// <summary>Shifts coded granule timestamps by pre-skip after EOS duration trimming has been resolved.</summary>
		private void ApplyOpusPreSkip(OggStreamState state)
		{
			for (var index = 0; index < packets.Count; index++)
			{
				if (packets[index].StreamIndex != state.Index)
					continue;
				packets[index].PresentationTimestamp -= state.PreSkip;
			}
			if (packets.Count > 0)
				packets[0].SkipSamples = state.PreSkip;
		}

		private static bool IsOpusMetadataPacket(byte[] data)
		{
			return IsOpusHead(data) || IsOpusTags(data);
		}

		private static bool IsOpusHead(byte[] data)
		{
			return HasPrefix(data, "OpusHead");
		}

		private static bool IsOpusTags(byte[] data)
		{
			return HasPrefix(data, "OpusTags");
		}

		private static bool HasPrefix(byte[] data, string value)
		{
			if (data == null || data.Length < value.Length)
				return false;
			for (var index = 0; index < value.Length; index++)
				if (data[index] != value[index])
					return false;
			return true;
		}

		private OggStreamState FindOrCreateStream(uint serial)
		{
			for (var index = 0; index < streams.Count; index++)
			{
				if (streams[index].Serial == serial)
					return streams[index];
			}
			var result = new OggStreamState(serial, streams.Count);
			streams.Add(result);
			return result;
		}

		private void FinalizeTiming(OggStreamState state)
		{
			if (state.FinalGranule >= 0)
				return;
			for (var index = packets.Count - 1; index >= 0; index--)
			{
				if (packets[index].StreamIndex == state.Index)
				{
					state.FinalGranule = packets[index].PresentationTimestamp + packets[index].Duration;
					return;
				}
			}
		}

		private static byte[] CreateXiphHeaders(OggStreamState state)
		{
			var lacing0 = (state.Identification.Length + 254) / 255;
			var lacing1 = (state.Comment.Length + 254) / 255;
			var result = new byte[1 + lacing0 + lacing1 + state.Identification.Length + state.Comment.Length + state.Setup.Length];
			var offset = 0;
			result[offset++] = 2;
			offset = WriteLacing(result, offset, state.Identification.Length);
			offset = WriteLacing(result, offset, state.Comment.Length);
			state.Identification.CopyTo(result, offset);
			offset += state.Identification.Length;
			state.Comment.CopyTo(result, offset);
			offset += state.Comment.Length;
			state.Setup.CopyTo(result, offset);
			return result;
		}

		private static int WriteLacing(byte[] destination, int offset, int length)
		{
			while (length >= 255)
			{
				destination[offset++] = 255;
				length -= 255;
			}
			destination[offset++] = (byte)length;
			return offset;
		}

		private static bool ValidateChecksum(ReadOnlySpan<byte> header, byte[] segments, byte[] payload, uint expected)
		{
			uint checksum = 0;
			for (var index = 0; index < header.Length; index++)
			{
				var value = index >= 22 && index < 26 ? (byte)0 : header[index];
				checksum = UpdateChecksum(checksum, value);
			}
			for (var index = 0; index < segments.Length; index++)
				checksum = UpdateChecksum(checksum, segments[index]);
			for (var index = 0; index < payload.Length; index++)
				checksum = UpdateChecksum(checksum, payload[index]);
			return checksum == expected;
		}

		private static uint UpdateChecksum(uint checksum, byte value)
		{
			checksum ^= (uint)value << 24;
			for (var bit = 0; bit < 8; bit++)
				checksum = (checksum & 0x80000000) != 0 ? checksum << 1 ^ 0x04c11db7u : checksum << 1;
			return checksum;
		}

		private void ReadExactly(Span<byte> destination)
		{
			var offset = 0;
			while (offset < destination.Length)
			{
				var read = stream.Read(destination.Slice(offset));
				if (read <= 0)
					throw new EndOfStreamException();
				offset += read;
			}
		}

		/// <summary>Tracks packet assembly, Vorbis headers, parser timing, and final granule state for one logical stream.</summary>
		private sealed class OggStreamState
		{
			public uint Serial { get; }
			public int Index { get; }
			public List<byte> Assembly { get; } = new List<byte>();
			public long PacketPosition { get; set; }
			public int HeaderCount { get; set; }
			public bool HeadersComplete { get; set; }
			public AudioCodecId CodecId { get; set; }
			public byte[] Identification { get; set; }
			public byte[] Comment { get; set; }
			public byte[] Setup { get; set; }
			public int Channels { get; set; }
			public int SampleRate { get; set; }
			public long BitRate { get; set; }
			public int PreSkip { get; set; }
			public int SmallBlockSize { get; set; }
			public int LargeBlockSize { get; set; }
			public VorbisDurationParser DurationParser { get; set; }
			public bool HasTimestamp { get; set; }
			public long NextTimestamp { get; set; }
			public long FinalGranule { get; set; } = -1;
			public bool PendingGranule { get; set; }
			public bool PageStartedWithPendingGranule { get; set; }
			public int PageDataPacketCount { get; set; }

			public OggStreamState(uint serial, int index)
			{
				Serial = serial;
				Index = index;
			}
		}

		/// <summary>Stores one selected logical-stream packet and its resolved FFmpeg timing and trimming fields.</summary>
		private sealed class OggPacket
		{
			public byte[] Data { get; }
			public long Position { get; }
			public int StreamIndex { get; }
			public long Duration { get; set; }
			public long PresentationTimestamp { get; set; }
			public int SkipSamples { get; set; }
			public int DiscardPadding { get; set; }

			public OggPacket(byte[] data, long position, int streamIndex, long duration)
			{
				Data = data;
				Position = position;
				StreamIndex = streamIndex;
				Duration = duration;
			}
		}

		/// <summary>Ports the reverse setup-header mode scan and stateful packet-duration calculation from vorbis_parser.c.</summary>
		private sealed class VorbisDurationParser
		{
			private readonly int[] blockSizes;
			private readonly bool[] modeBlockSizes;
			private readonly int modeMask;
			private readonly int previousMask;
			private int previousBlockSize;

			private VorbisDurationParser(int smallBlockSize, int largeBlockSize, bool[] modeBlockSizes)
			{
				blockSizes = new[] { smallBlockSize, largeBlockSize };
				this.modeBlockSizes = modeBlockSizes;
				var modeBits = 0;
				for (var value = modeBlockSizes.Length - 1; value > 0; value >>= 1)
					modeBits++;
				modeMask = ((1 << modeBits) - 1) << 1;
				previousMask = (modeMask | 1) + 1;
				previousBlockSize = blockSizes[modeBlockSizes[0] ? 1 : 0];
			}

			/// <summary>
			/// Finds the framing bit from the reversed packet, validates candidate mode records, and extracts their block flags.
			/// </summary>
			public static VorbisDurationParser Create(byte[] setup, int smallBlockSize, int largeBlockSize)
			{
				if (setup == null || setup.Length < 7)
					return null;
				var reversed = new byte[setup.Length];
				for (var index = 0; index < setup.Length; index++)
					reversed[index] = setup[setup.Length - 1 - index];
				var reader = new MostSignificantBitReader(reversed);
				var framingPosition = 0;
				while (reader.BitsLeft > 97)
				{
					if (reader.Read(1) != 0)
					{
						framingPosition = reader.Position;
						break;
					}
				}
				if (framingPosition == 0)
					return null;

				var modeCount = 0;
				var lastModeCount = 0;
				while (reader.BitsLeft >= 97)
				{
					if (reader.Read(8) > 63 || reader.Read(16) != 0 || reader.Read(16) != 0)
						break;
					reader.Skip(1);
					modeCount++;
					if (modeCount > 64)
						break;
					var probe = reader;
					if (probe.Read(6) + 1 == modeCount)
						lastModeCount = modeCount;
				}
				if (lastModeCount <= 0 || lastModeCount > 63)
					return null;

				reader = new MostSignificantBitReader(reversed);
				reader.Skip(framingPosition);
				var flags = new bool[lastModeCount];
				for (var index = lastModeCount - 1; index >= 0; index--)
				{
					reader.Skip(40);
					flags[index] = reader.Read(1) != 0;
				}
				return new VorbisDurationParser(smallBlockSize, largeBlockSize, flags);
			}

			public int ParseDuration(byte[] packet)
			{
				if (packet == null || packet.Length == 0 || (packet[0] & 1) != 0)
					return FfmpegError.InvalidData;
				var mode = modeBlockSizes.Length == 1 ? 0 : (packet[0] & modeMask) >> 1;
				if (mode >= modeBlockSizes.Length)
					return FfmpegError.InvalidData;
				var prior = previousBlockSize;
				if (modeBlockSizes[mode])
					prior = blockSizes[(packet[0] & previousMask) != 0 ? 1 : 0];
				var current = blockSizes[modeBlockSizes[mode] ? 1 : 0];
				previousBlockSize = current;
				return (prior + current) >> 2;
			}

			public void Reset()
			{
				previousBlockSize = blockSizes[0];
			}
		}

		private struct MostSignificantBitReader
		{
			private readonly byte[] data;
			public int Position { get; private set; }
			public int BitsLeft => data.Length * 8 - Position;

			public MostSignificantBitReader(byte[] data)
			{
				this.data = data;
				Position = 0;
			}

			public int Read(int count)
			{
				var result = 0;
				for (var index = 0; index < count; index++)
				{
					result = result << 1 | (data[Position >> 3] >> (7 - (Position & 7)) & 1);
					Position++;
				}
				return result;
			}

			public void Skip(int count)
			{
				Position += count;
			}
		}
	}
}
