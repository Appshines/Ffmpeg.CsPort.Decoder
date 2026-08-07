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
using Ffmpeg.CsPort.Decoder.Codecs.Wma;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports the audio path of FFmpeg's ASF demuxer: WAVEFORMAT stream headers, payload extensions,
	/// error-correction packet headers, compressed/multiple payloads, fragment assembly, and descrambling.
	/// </summary>
	public sealed class AsfAudioDemuxer : ISeekableAudioDemuxer
	{
		private const int FrameHeaderSize = 6;
		private static readonly byte[] HeaderGuid = { 0x30,0x26,0xb2,0x75,0x8e,0x66,0xcf,0x11,0xa6,0xd9,0x00,0xaa,0x00,0x62,0xce,0x6c };
		private static readonly byte[] FilePropertiesGuid = { 0xa1,0xdc,0xab,0x8c,0x47,0xa9,0xcf,0x11,0x8e,0xe4,0x00,0xc0,0x0c,0x20,0x53,0x65 };
		private static readonly byte[] StreamPropertiesGuid = { 0x91,0x07,0xdc,0xb7,0xb7,0xa9,0xcf,0x11,0x8e,0xe6,0x00,0xc0,0x0c,0x20,0x53,0x65 };
		private static readonly byte[] ExtendedStreamPropertiesGuid = { 0xcb,0xa5,0xe6,0x14,0x72,0xc6,0x32,0x43,0x83,0x99,0xa9,0x69,0x52,0x06,0x5b,0x5a };
		private static readonly byte[] AudioStreamGuid = { 0x40,0x9e,0x69,0xf8,0x4d,0x5b,0xcf,0x11,0xa8,0xfd,0x00,0x80,0x5f,0x5c,0x44,0x2b };
		private static readonly byte[] DataGuid = { 0x36,0x26,0xb2,0x75,0x8e,0x66,0xcf,0x11,0xa6,0xd9,0x00,0xaa,0x00,0x62,0xce,0x6c };
		private static readonly byte[] HeaderExtensionGuid = { 0xb5,0x03,0xbf,0x5f,0x2e,0xa9,0xcf,0x11,0x8e,0xe3,0x00,0xc0,0x0c,0x20,0x53,0x65 };

		private readonly Stream stream;
		private readonly AsfStreamState[] streams = new AsfStreamState[128];
		private readonly int[] streamIndexes = new int[128];
		private readonly uint[] streamBitRates = new uint[128];
		private readonly List<AsfPacket> packets = new List<AsfPacket>();
		private byte[] data;
		private AsfStreamState selectedStream;
		private uint preroll;
		private ulong playTime;
		private uint headerFlags;
		private uint minimumPacketSize;
		private uint maximumPacketSize;
		private ulong dataObjectOffset;
		private ulong dataObjectSize;
		private int dataOffset;
		private int packetIndex;
		private long[] decodedFrameStarts = Array.Empty<long>();

		public AsfAudioDemuxer(Stream stream)
		{
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => packets.Count == 0 ? 0 : packets[0].Timestamp;

		/// <summary>Reads the ASF object graph, selects the first supported audio stream, and reconstructs all media objects.</summary>
		public int ReadHeader()
		{
			try
			{
				if (!stream.CanSeek || stream.Length > int.MaxValue) return FfmpegError.InvalidArgument;
				stream.Position = 0;
				data = new byte[(int)stream.Length];
				ReadExactly(data);
				Array.Fill(streamIndexes, -1);
				Array.Clear(streams, 0, streams.Length);
				Array.Clear(streamBitRates, 0, streamBitRates.Length);
				packets.Clear();
				selectedStream = null;
				packetIndex = 0;
				var position = 0;
				if (!ReadGuid(ref position, HeaderGuid)) return FfmpegError.InvalidData;
				ReadUInt64(ref position);
				ReadUInt32(ref position);
				ReadByte(ref position);
				ReadByte(ref position);
				var streamCount = 0;
				var foundData = false;
				while (position + 24 <= data.Length)
				{
					var objectPosition = position;
					var guidOffset = position;
					position += 16;
					var objectSize = ReadUInt64(ref position);
					if (objectSize < 24 || objectSize > int.MaxValue || objectPosition > data.Length - (int)objectSize)
						return FfmpegError.InvalidData;
					var objectEnd = objectPosition + (int)objectSize;
					if (GuidEquals(guidOffset, DataGuid))
					{
						dataObjectOffset = (ulong)position;
						dataObjectSize = (headerFlags & 1) == 0 && objectSize >= 100 ? objectSize - 24 : ulong.MaxValue;
						ReadBytes(ref position, 16);
						ReadUInt64(ref position);
						ReadByte(ref position);
						ReadByte(ref position);
						dataOffset = position;
						foundData = true;
						break;
					}
					if (GuidEquals(guidOffset, FilePropertiesGuid))
						ParseFileProperties(ref position, objectEnd);
					else if (GuidEquals(guidOffset, StreamPropertiesGuid))
						ParseStreamProperties(ref position, objectEnd, streamCount++);
					else if (GuidEquals(guidOffset, ExtendedStreamPropertiesGuid))
					{
						ParseExtendedStreamProperties(ref position, objectEnd);
						if (position < objectEnd) continue;
					} else if (GuidEquals(guidOffset, HeaderExtensionGuid))
					{
						ReadBytes(ref position, 22);
						continue;
					}
					position = objectEnd;
				}
				if (!foundData || selectedStream == null || maximumPacketSize == 0) return FfmpegError.InvalidData;
				if (selectedStream.BitRate == 0) selectedStream.BitRate = streamBitRates[selectedStream.Id];
				StreamInfo.StreamIndex = selectedStream.StreamIndex;
				StreamInfo.CodecId = selectedStream.CodecId;
				StreamInfo.CodecTag = selectedStream.CodecTag;
				StreamInfo.SampleRate = selectedStream.SampleRate;
				StreamInfo.Channels = selectedStream.Channels;
				StreamInfo.ChannelMask = selectedStream.ChannelMask;
				StreamInfo.BitsPerCodedSample = selectedStream.BitsPerSample;
				StreamInfo.BlockAlign = selectedStream.BlockAlign;
				StreamInfo.BitRate = selectedStream.BitRate;
				StreamInfo.Duration = (headerFlags & 1) == 0 ? Math.Max((long)(playTime / 10000) - preroll, 0) : 0;
				StreamInfo.TimeBaseNumerator = 1;
				StreamInfo.TimeBaseDenominator = 1000;
				StreamInfo.CodecExtraData = selectedStream.ExtraData ?? Array.Empty<byte>();
				var result = ParseDataPackets();
				return result < 0 ? result : 0;
			} catch (EndOfStreamException)
			{
				return FfmpegError.EndOfFile;
			} catch (IOException)
			{
				return FfmpegError.InvalidData;
			} catch (OverflowException)
			{
				return FfmpegError.InvalidData;
			} catch (IndexOutOfRangeException)
			{
				return FfmpegError.InvalidData;
			}
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (packetIndex >= packets.Count) return FfmpegError.EndOfFile;
			var source = packets[packetIndex];
			if (destination.Length < source.Data.Length) return FfmpegError.InvalidArgument;
			source.Data.AsSpan().CopyTo(destination);
			packet = new DemuxedAudioPacket(source.Data.Length, source.Position, source.Timestamp, source.Timestamp,
				source.Duration, source.StreamIndex, false);
			packetIndex++;
			return source.Data.Length;
		}

		/// <summary>Uses the assembled ASF payload timestamps to select the packet immediately before the target.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (packets.Count == 0) { a_ActualTimestamp = 0; return false; }
			var l_Low = 0; var l_High = packets.Count - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (packets[l_Middle].Timestamp <= a_Timestamp) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			packetIndex = l_Low; a_ActualTimestamp = packets[l_Low].Timestamp; return true;
		}

		/// <summary>
		/// Seeks WMA v1/v2 on a sample index derived from superframe headers, avoiding irregular ASF millisecond timestamps.
		/// </summary>
		public bool TrySeekToDecodedFrame(long a_FrameIndex, WmaV1V2Decoder a_Decoder, out long a_ActualFrameIndex)
		{
			a_ActualFrameIndex = 0;
			if (a_FrameIndex < 0 || a_Decoder == null || packets.Count == 0)
				return false;
			if (decodedFrameStarts.Length != packets.Count)
				BuildDecodedFrameIndex(a_Decoder);
			if (decodedFrameStarts.Length == 0)
				return false;
			var l_Low = 0; var l_High = decodedFrameStarts.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (decodedFrameStarts[l_Middle] <= a_FrameIndex) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			packetIndex = l_Low;
			a_ActualFrameIndex = decodedFrameStarts[l_Low] +
				(l_Low == 0 ? 0 : a_Decoder.RandomAccessOutputDelaySampleCount);
			return true;
		}

		private void BuildDecodedFrameIndex(WmaV1V2Decoder a_Decoder)
		{
			var l_Result = new long[packets.Count];
			var l_FramePosition = 0L;
			var l_RemainingInitialSkip = a_Decoder.InitialSkipSampleCount;
			for (var l_Index = 0; l_Index < packets.Count; l_Index++)
			{
				l_Result[l_Index] = l_FramePosition;
				var l_DecodedSamples = a_Decoder.GetPacketDecodedSampleCount(packets[l_Index].Data, l_Index > 0);
				var l_SkippedSamples = Math.Min(l_RemainingInitialSkip, l_DecodedSamples);
				l_RemainingInitialSkip -= l_SkippedSamples;
				l_FramePosition += l_DecodedSamples - l_SkippedSamples;
			}
			decodedFrameStarts = l_Result;
		}

		private void ParseFileProperties(ref int position, int end)
		{
			ReadBytes(ref position, 16);
			ReadUInt64(ref position);
			ReadUInt64(ref position);
			ReadUInt64(ref position);
			playTime = ReadUInt64(ref position);
			ReadUInt64(ref position);
			preroll = ReadUInt32(ref position);
			ReadUInt32(ref position);
			headerFlags = ReadUInt32(ref position);
			minimumPacketSize = ReadUInt32(ref position);
			maximumPacketSize = ReadUInt32(ref position);
			ReadUInt32(ref position);
			if (minimumPacketSize >= 1U << 29 || position > end) throw new IndexOutOfRangeException();
		}

		/// <summary>Parses an ASF stream object and the embedded WAVEFORMATEX/WAVEFORMATEXTENSIBLE audio parameters.</summary>
		private void ParseStreamProperties(ref int position, int end, int streamIndex)
		{
			var typeGuidOffset = position;
			position += 16;
			ReadBytes(ref position, 16);
			ReadUInt64(ref position);
			var typeSpecificSize = checked((int)ReadUInt32(ref position));
			ReadUInt32(ref position);
			var streamId = ReadUInt16(ref position) & 0x7f;
			ReadUInt32(ref position);
			if (streamId >= streams.Length) throw new IndexOutOfRangeException();
			streamIndexes[streamId] = streamIndex;
			var state = streams[streamId] ?? new AsfStreamState { Id = streamId };
			streams[streamId] = state;
			state.StreamIndex = streamIndex;
			if (!GuidEquals(typeGuidOffset, AudioStreamGuid))
			{
				position = end;
				return;
			}
			var typeEnd = checked(position + typeSpecificSize);
			if (typeEnd > end || typeSpecificSize < 16) throw new IndexOutOfRangeException();
			var formatTag = ReadUInt16(ref position);
			state.CodecTag = formatTag;
			state.Channels = ReadUInt16(ref position);
			state.SampleRate = checked((int)ReadUInt32(ref position));
			state.BitRate = ReadUInt32(ref position) * 8L;
			state.BlockAlign = ReadUInt16(ref position);
			state.BitsPerSample = ReadUInt16(ref position);
			var extraSize = 0;
			if (position + 2 <= typeEnd) extraSize = Math.Min(ReadUInt16(ref position), typeEnd - position);
			if (formatTag == 0xfffe && extraSize >= 22)
			{
				state.BitsPerSample = ReadUInt16(ref position);
				state.ChannelMask = ReadUInt32(ref position);
				formatTag = ReadUInt16(ref position);
				position += 14;
				extraSize -= 22;
				state.CodecTag = formatTag;
			}
			state.ExtraData = new byte[extraSize];
			if (extraSize != 0)
			{
				data.AsSpan(position, extraSize).CopyTo(state.ExtraData);
				position += extraSize;
			}
			state.CodecId = MapCodec(formatTag);
			position = typeEnd;
			if (end - position >= 8)
			{
				state.DescrambleSpan = ReadByte(ref position);
				state.DescramblePacketSize = ReadUInt16(ref position);
				state.DescrambleChunkSize = ReadUInt16(ref position);
				ReadUInt16(ref position);
				ReadByte(ref position);
				if (state.DescrambleSpan > 1 && (state.DescrambleChunkSize == 0 ||
					state.DescramblePacketSize / state.DescrambleChunkSize <= 1 ||
					state.DescramblePacketSize % state.DescrambleChunkSize != 0)) state.DescrambleSpan = 0;
			}
			if (selectedStream == null && state.CodecId != AudioCodecId.None) selectedStream = state;
		}

		/// <summary>Records FFmpeg's extended stream bitrate and replicated-payload extension byte schedules.</summary>
		private void ParseExtendedStreamProperties(ref int position, int end)
		{
			ReadUInt64(ref position);
			ReadUInt64(ref position);
			var leakRate = ReadUInt32(ref position);
			for (var index = 0; index < 7; index++) ReadUInt32(ref position);
			var streamId = ReadUInt16(ref position);
			ReadUInt16(ref position);
			ReadUInt64(ref position);
			var streamNameCount = ReadUInt16(ref position);
			var payloadCount = ReadUInt16(ref position);
			if (streamId < 128)
			{
				streamBitRates[streamId] = leakRate;
				var state = streams[streamId] ?? new AsfStreamState { Id = streamId };
				streams[streamId] = state;
				state.PayloadExtensionCount = 0;
			}
			for (var index = 0; index < streamNameCount; index++)
			{
				ReadUInt16(ref position);
				var length = ReadUInt16(ref position);
				ReadBytes(ref position, length);
			}
			for (var index = 0; index < payloadCount; index++)
			{
				var type = data[position];
				ReadBytes(ref position, 16);
				var size = ReadUInt16(ref position);
				var informationLength = checked((int)ReadUInt32(ref position));
				ReadBytes(ref position, informationLength);
				if (streamId < 128 && index < 8)
				{
					var state = streams[streamId];
					state.PayloadTypes[state.PayloadExtensionCount] = type;
					state.PayloadSizes[state.PayloadExtensionCount] = size;
					state.PayloadExtensionCount++;
				}
			}
			if (position > end) throw new IndexOutOfRangeException();
		}

		/// <summary>Walks fixed-size ASF data packets and reproduces FFmpeg's fragment and compressed-payload state machine.</summary>
		private int ParseDataPackets()
		{
			var position = dataOffset;
			var usesStandardErrorCorrection = 0;
			while (position < data.Length)
			{
				var packetPosition = position;
				if (dataObjectSize != ulong.MaxValue && (ulong)packetPosition - dataObjectOffset >= dataObjectSize) break;
				var headerSize = 8;
				int packetFlags;
				int packetProperty;
				if (usesStandardErrorCorrection > 0)
				{
					var found = false;
					for (var remaining = 32768; remaining > 0 && position + 3 <= data.Length; remaining--)
					{
						if (data[position] == 0x82 && data[position + 1] == 0 && data[position + 2] == 0)
						{
							position += 3;
							found = true;
							break;
						}
						position++;
					}
					if (!found) return FfmpegError.InvalidData;
					headerSize += 3;
					packetFlags = ReadByte(ref position);
					packetProperty = ReadByte(ref position);
				} else
				{
					var first = ReadByte(ref position);
					if ((first & 0x80) != 0)
					{
						headerSize++;
						var second = -1;
						var third = -1;
						if ((first & 0x60) == 0)
						{
							second = ReadByte(ref position);
							third = ReadByte(ref position);
							var correctionLength = (first & 15) - 2;
							if (correctionLength < 0) return FfmpegError.InvalidData;
							ReadBytes(ref position, correctionLength);
							headerSize += first & 15;
						}
						usesStandardErrorCorrection = first == 0x82 && second == 0 && third == 0 ? 1 : -1;
						packetFlags = ReadByte(ref position);
					} else
					{
						usesStandardErrorCorrection = -1;
						packetFlags = first;
					}
					packetProperty = ReadByte(ref position);
				}
				var packetLength = ReadVariable(ref position, packetFlags >> 5, maximumPacketSize, ref headerSize);
				ReadVariable(ref position, packetFlags >> 1, 0, ref headerSize);
				var padding = ReadVariable(ref position, packetFlags >> 3, 0, ref headerSize);
				if (packetLength == 0 || packetLength >= 1U << 29 || padding >= packetLength) return FfmpegError.InvalidData;
				var packetTimestamp = ReadUInt32(ref position);
				ReadUInt16(ref position);
				var segmentSizeType = 0x80;
				var segments = 1;
				if ((packetFlags & 1) != 0)
				{
					segmentSizeType = ReadByte(ref position);
					headerSize++;
					segments = segmentSizeType & 0x3f;
				}
				if (headerSize > packetLength - padding) return FfmpegError.InvalidData;
				var packetSizeLeft = checked((int)(packetLength - padding - (uint)headerSize));
				if (packetLength < minimumPacketSize) padding += minimumPacketSize - packetLength;
				var packetTimeStart = 0L;
				var packetTimeDelta = 0;
				var packetMultiSize = 0;
				var activeState = (AsfStreamState)null;
				var activeStreamIndex = -1;
				var fragmentOffset = 0U;
				var fragmentSize = 0U;
				var replicatedSize = 0U;
				var fragmentTimestamp = DemuxedAudioPacket.NoTimestamp;
				while (packetSizeLeft >= FrameHeaderSize && (segments >= 1 || packetTimeStart != 0))
				{
					if (packetTimeStart == 0)
					{
						var frameSize = 1;
						var streamNumber = ReadByte(ref position);
						segments--;
						activeStreamIndex = streamIndexes[streamNumber & 0x7f];
						activeState = streams[streamNumber & 0x7f];
						ReadVariable(ref position, packetProperty >> 4, 0, ref frameSize);
						fragmentOffset = ReadVariable(ref position, packetProperty >> 2, 0, ref frameSize);
						replicatedSize = ReadVariable(ref position, packetProperty, 0, ref frameSize);
						if (frameSize + replicatedSize > packetSizeLeft) return FfmpegError.InvalidData;
						if (replicatedSize >= 8)
						{
							var replicatedEnd = checked(position + (int)replicatedSize);
							if (activeState != null) activeState.ObjectSize = checked((int)ReadUInt32(ref position)); else ReadUInt32(ref position);
							fragmentTimestamp = ReadUInt32(ref position);
							if (activeState != null)
								for (var extension = 0; extension < activeState.PayloadExtensionCount; extension++)
								{
									var extensionSize = activeState.PayloadSizes[extension];
									if (extensionSize == ushort.MaxValue) extensionSize = ReadUInt16(ref position);
									var extensionEnd = position + extensionSize;
									if (extensionEnd > replicatedEnd) break;
									if (activeState.PayloadTypes[extension] == 0x2a && extensionSize >= 24)
									{
										position += 8;
										var timestamp = ReadUInt64(ref position);
										ReadUInt64(ref position);
										fragmentTimestamp = timestamp == ulong.MaxValue ? DemuxedAudioPacket.NoTimestamp : checked((long)(timestamp / 10000));
									}
									position = extensionEnd;
								}
							position = replicatedEnd;
							frameSize += checked((int)replicatedSize);
						} else if (replicatedSize == 1)
						{
							packetTimeStart = fragmentOffset;
							fragmentOffset = 0;
							fragmentTimestamp = packetTimestamp;
							packetTimeDelta = ReadByte(ref position);
							frameSize++;
						} else if (replicatedSize != 0)
						{
							return FfmpegError.InvalidData;
						}
						if ((packetFlags & 1) != 0)
						{
							fragmentSize = ReadVariable(ref position, segmentSizeType >> 6, 0, ref frameSize);
							if (frameSize > packetSizeLeft || fragmentSize > packetSizeLeft - frameSize + padding) return FfmpegError.InvalidData;
							if (fragmentSize > packetSizeLeft - frameSize)
							{
								var difference = checked((int)fragmentSize - (packetSizeLeft - frameSize));
								packetSizeLeft += difference;
								padding -= checked((uint)difference);
							}
						} else
						{
							fragmentSize = checked((uint)(packetSizeLeft - frameSize));
						}
						if (replicatedSize == 1) packetMultiSize = checked((int)fragmentSize);
						packetSizeLeft -= frameSize;
						if (activeState == null || activeState != selectedStream)
						{
							ReadBytes(ref position, checked((int)fragmentSize));
							packetSizeLeft -= checked((int)fragmentSize);
							packetTimeStart = 0;
							continue;
						}
					}
					if (activeState == null) return FfmpegError.InvalidData;
					if (activeState.FragmentOffset == 0 && fragmentOffset != 0)
					{
						ReadBytes(ref position, checked((int)fragmentSize));
						packetSizeLeft -= checked((int)fragmentSize);
						continue;
					}
					if (replicatedSize == 1)
					{
						fragmentTimestamp = packetTimeStart;
						packetTimeStart += packetTimeDelta;
						fragmentSize = ReadByte(ref position);
						activeState.ObjectSize = checked((int)fragmentSize);
						packetSizeLeft--;
						packetMultiSize--;
						if (packetMultiSize < activeState.ObjectSize)
						{
							packetTimeStart = 0;
							ReadBytes(ref position, packetMultiSize);
							packetSizeLeft -= packetMultiSize;
							continue;
						}
						packetMultiSize -= activeState.ObjectSize;
					}
					if (activeState.PacketData == null || activeState.PacketData.Length != activeState.ObjectSize ||
						activeState.FragmentOffset + fragmentSize > activeState.ObjectSize)
					{
						activeState.FragmentOffset = 0;
						activeState.PacketData = new byte[activeState.ObjectSize];
						activeState.PacketClean = false;
						activeState.Timestamp = fragmentTimestamp == DemuxedAudioPacket.NoTimestamp ? fragmentTimestamp : fragmentTimestamp - preroll;
						activeState.PacketPosition = packetPosition;
					}
					packetSizeLeft -= checked((int)fragmentSize);
					if (packetSizeLeft < 0) continue;
					if (fragmentOffset >= activeState.PacketData.Length || fragmentSize > activeState.PacketData.Length - fragmentOffset)
						return FfmpegError.InvalidData;
					if (fragmentOffset != activeState.FragmentOffset && !activeState.PacketClean)
					{
						Array.Clear(activeState.PacketData, activeState.FragmentOffset, activeState.PacketData.Length - activeState.FragmentOffset);
						activeState.PacketClean = true;
					}
					data.AsSpan(position, checked((int)fragmentSize)).CopyTo(activeState.PacketData.AsSpan(checked((int)fragmentOffset)));
					position += checked((int)fragmentSize);
					activeState.FragmentOffset += checked((int)fragmentSize);
					if (activeState.FragmentOffset == activeState.PacketData.Length)
					{
						var packetData = Descramble(activeState);
						var duration = (activeState.CodecId == AudioCodecId.WmaV1 || activeState.CodecId == AudioCodecId.WmaV2) && activeState.BitRate > 0 ?
							packetData.Length * 8000L / activeState.BitRate : 0;
						packets.Add(new AsfPacket(packetData, activeState.PacketPosition, activeState.Timestamp, duration, activeStreamIndex));
						activeState.FragmentOffset = 0;
						activeState.PacketData = null;
						if (replicatedSize == 1 && packetMultiSize == 0) packetTimeStart = 0;
					}
				}
				var skip = checked(packetSizeLeft + (int)padding);
				ReadBytes(ref position, skip);
			}
			return 0;
		}

		private static AudioCodecId MapCodec(ushort tag)
		{
			return tag switch
			{
				0x0160 => AudioCodecId.WmaV1,
				0x0161 => AudioCodecId.WmaV2,
				0x0162 => AudioCodecId.WmaPro,
				0x0163 => AudioCodecId.WmaLossless,
				0x000a => AudioCodecId.WmaVoice,
				_ => AudioCodecId.None
			};
		}

		private static byte[] Descramble(AsfStreamState state)
		{
			if (state.DescrambleSpan <= 1 || state.PacketData.Length != state.DescramblePacketSize * state.DescrambleSpan)
				return state.PacketData;
			var result = new byte[state.PacketData.Length];
			for (var offset = 0; offset < result.Length; offset += state.DescrambleChunkSize)
			{
				var chunk = offset / state.DescrambleChunkSize;
				var row = chunk / state.DescrambleSpan;
				var column = chunk % state.DescrambleSpan;
				var source = row + column * state.DescramblePacketSize / state.DescrambleChunkSize;
				Array.Copy(state.PacketData, source * state.DescrambleChunkSize, result, offset, state.DescrambleChunkSize);
			}
			return result;
		}

		private uint ReadVariable(ref int position, int selector, uint defaultValue, ref int size)
		{
			switch (selector & 3)
			{
				case 3: size += 4; return ReadUInt32(ref position);
				case 2: size += 2; return ReadUInt16(ref position);
				case 1: size++; return ReadByte(ref position);
				default: return defaultValue;
			}
		}

		private bool ReadGuid(ref int position, byte[] expected)
		{
			if (position > data.Length - 16) return false;
			var equal = data.AsSpan(position, 16).SequenceEqual(expected);
			position += 16;
			return equal;
		}

		private bool GuidEquals(int position, byte[] expected) =>
			position >= 0 && position <= data.Length - 16 && data.AsSpan(position, 16).SequenceEqual(expected);

		private byte ReadByte(ref int position)
		{
			if ((uint)position >= (uint)data.Length) throw new IndexOutOfRangeException();
			return data[position++];
		}

		private ushort ReadUInt16(ref int position)
		{
			if (position > data.Length - 2) throw new IndexOutOfRangeException();
			var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
			position += 2;
			return value;
		}

		private uint ReadUInt32(ref int position)
		{
			if (position > data.Length - 4) throw new IndexOutOfRangeException();
			var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
			position += 4;
			return value;
		}

		private ulong ReadUInt64(ref int position)
		{
			if (position > data.Length - 8) throw new IndexOutOfRangeException();
			var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position));
			position += 8;
			return value;
		}

		private void ReadBytes(ref int position, int count)
		{
			if (count < 0 || position < 0 || position > data.Length - count) throw new IndexOutOfRangeException();
			position += count;
		}

		private void ReadExactly(byte[] destination)
		{
			var offset = 0;
			while (offset < destination.Length)
			{
				var count = stream.Read(destination, offset, destination.Length - offset);
				if (count == 0) throw new EndOfStreamException();
				offset += count;
			}
		}

		/// <summary>
		/// Tracks one ASF audio stream's format, replicated-data timing, and fragmented payload assembly.
		/// </summary>
		private sealed class AsfStreamState
		{
			public int Id;
			public int StreamIndex;
			public AudioCodecId CodecId;
			public ushort CodecTag;
			public int SampleRate;
			public int Channels;
			public uint ChannelMask;
			public int BitsPerSample;
			public int BlockAlign;
			public long BitRate;
			public byte[] ExtraData;
			public int DescrambleSpan;
			public int DescramblePacketSize;
			public int DescrambleChunkSize;
			public int PayloadExtensionCount;
			public readonly byte[] PayloadTypes = new byte[8];
			public readonly ushort[] PayloadSizes = new ushort[8];
			public int ObjectSize;
			public byte[] PacketData;
			public int FragmentOffset;
			public bool PacketClean;
			public long Timestamp;
			public long PacketPosition;
		}

		private readonly struct AsfPacket
		{
			public AsfPacket(byte[] data, long position, long timestamp, long duration, int streamIndex)
			{
				Data = data; Position = position; Timestamp = timestamp; Duration = duration; StreamIndex = streamIndex;
			}
			public byte[] Data { get; }
			public long Position { get; }
			public long Timestamp { get; }
			public long Duration { get; }
			public int StreamIndex { get; }
		}
	}
}
