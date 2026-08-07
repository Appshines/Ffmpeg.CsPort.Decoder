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
using System.Text;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Codecs.Opus;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Parses the audio path of FFmpeg's Matroska/WebM demuxer, including EBML/Xiph/fixed lacing and Opus trimming.
	/// </summary>
	public sealed class MatroskaAudioDemuxer : ISeekableAudioDemuxer
	{
		private const ulong SegmentId = 0x18538067;
		private const ulong InfoId = 0x1549a966;
		private const ulong TimecodeScaleId = 0x2ad7b1;
		private const ulong DurationId = 0x4489;
		private const ulong TracksId = 0x1654ae6b;
		private const ulong TrackEntryId = 0xae;
		private const ulong TrackNumberId = 0xd7;
		private const ulong TrackTypeId = 0x83;
		private const ulong CodecIdId = 0x86;
		private const ulong CodecPrivateId = 0x63a2;
		private const ulong CodecDelayId = 0x56aa;
		private const ulong SeekPreRollId = 0x56bb;
		private const ulong DefaultDurationId = 0x23e383;
		private const ulong TrackTimecodeScaleId = 0x23314f;
		private const ulong AudioId = 0xe1;
		private const ulong SamplingFrequencyId = 0xb5;
		private const ulong OutputSamplingFrequencyId = 0x78b5;
		private const ulong ChannelsId = 0x9f;
		private const ulong BitDepthId = 0x6264;
		private const ulong ClusterId = 0x1f43b675;
		private const ulong ClusterTimecodeId = 0xe7;
		private const ulong SimpleBlockId = 0xa3;
		private const ulong BlockGroupId = 0xa0;
		private const ulong BlockId = 0xa1;
		private const ulong BlockDurationId = 0x9b;
		private const ulong DiscardPaddingId = 0x75a2;

		private readonly Stream stream;
		private readonly List<MatroskaTrack> tracks = new List<MatroskaTrack>();
		private readonly List<MatroskaPacket> packets = new List<MatroskaPacket>();
		private readonly OpusPacket parsedOpusPacket = new OpusPacket();
		private byte[] data;
		private ulong timecodeScale = 1000000;
		private double duration;
		private MatroskaTrack selectedTrack;
		private int packetIndex;

		public MatroskaAudioDemuxer(Stream stream)
		{
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;

		/// <summary>Reads segment metadata, selects the first supported audio track, and resolves all block packets once.</summary>
		public int ReadHeader()
		{
			try
			{
				if (!stream.CanSeek || stream.Length > int.MaxValue)
					return FfmpegError.InvalidArgument;
				stream.Position = 0;
				data = new byte[(int)stream.Length];
				ReadExactly(data);
				tracks.Clear();
				packets.Clear();
				selectedTrack = null;
				packetIndex = 0;
				timecodeScale = 1000000;
				duration = 0;

				var position = 0;
				var segmentFound = false;
				while (position < data.Length)
				{
					if (!ReadElement(ref position, data.Length, out var element))
						return FfmpegError.InvalidData;
					if (element.Id == SegmentId)
					{
						segmentFound = true;
						var result = ParseSegment(element.PayloadOffset, element.EndOffset);
						if (result < 0)
							return result;
						break;
					}
					position = element.EndOffset;
				}
				if (!segmentFound || selectedTrack == null || selectedTrack.CodecPrivate == null)
					return FfmpegError.InvalidData;

				var divisor = GreatestCommonDivisor(timecodeScale, 1000000000);
				StreamInfo.StreamIndex = selectedTrack.StreamIndex;
				StreamInfo.CodecId = AudioCodecId.Opus;
				StreamInfo.SampleRate = 48000;
				StreamInfo.Channels = selectedTrack.Channels;
				StreamInfo.BitsPerCodedSample = selectedTrack.BitDepth;
				StreamInfo.Duration = duration > 0 ? checked((long)Math.Round(duration, MidpointRounding.AwayFromZero)) : ResolvePacketDuration();
				StreamInfo.TimeBaseNumerator = checked((long)(timecodeScale / divisor));
				StreamInfo.TimeBaseDenominator = checked((long)(1000000000 / divisor));
				StreamInfo.StartSkipSamples = RescaleNearest(selectedTrack.CodecDelay, 48000, 1000000000);
				StreamInfo.EndPaddingSamples = packets.Count == 0 ? 0 : packets[packets.Count - 1].DiscardPadding;
				StreamInfo.CodecExtraData = selectedTrack.CodecPrivate;
				ResolveDecodedFrameStarts();
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
			packet = new DemuxedAudioPacket(
				source.Data.Length,
				source.Position,
				source.PresentationTimestamp,
				source.PresentationTimestamp,
				source.Duration,
				source.StreamIndex,
				false,
				packetIndex == 0 ? StreamInfo.StartSkipSamples : source.SkipSamples,
				source.DiscardPadding);
			packetIndex++;
			return source.Data.Length;
		}

		/// <summary>Uses exact cumulative Opus sample counts instead of millisecond-quantized Matroska timestamps.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (packets.Count == 0) { a_ActualTimestamp = 0; return false; }
			var l_TargetFrame = RescaleNearest((ulong)Math.Max(0L, a_Timestamp), 48000 * (ulong)StreamInfo.TimeBaseNumerator,
				(ulong)StreamInfo.TimeBaseDenominator);
			var l_Low = 0; var l_High = packets.Count - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (packets[l_Middle].DecodedFrameStart <= l_TargetFrame) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			packetIndex = l_Low;
			a_ActualTimestamp = RescaleNearest((ulong)packets[l_Low].DecodedFrameStart,
				(ulong)StreamInfo.TimeBaseDenominator, 48000 * (ulong)StreamInfo.TimeBaseNumerator);
			return true;
		}

		private void ResolveDecodedFrameStarts()
		{
			var l_DecodedFrameStart = 0L;
			for (var l_Index = 0; l_Index < packets.Count; l_Index++)
			{
				var l_Packet = packets[l_Index];
				l_Packet.DecodedFrameStart = l_DecodedFrameStart;
				if (OpusPacketParser.Parse(parsedOpusPacket, l_Packet.Data, 0, l_Packet.Data.Length, false) >= 0)
					l_DecodedFrameStart += parsedOpusPacket.FrameDuration * parsedOpusPacket.FrameCount;
			}
		}

		private int ParseSegment(int start, int end)
		{
			var position = start;
			while (position < end)
			{
				if (!ReadElement(ref position, end, out var element))
					return FfmpegError.InvalidData;
				int result;
				switch (element.Id)
				{
					case InfoId:
						result = ParseInfo(element.PayloadOffset, element.EndOffset);
						break;
					case TracksId:
						result = ParseTracks(element.PayloadOffset, element.EndOffset);
						break;
					case ClusterId:
						result = ParseCluster(element.PayloadOffset, element.EndOffset);
						break;
					default:
						result = 0;
						break;
				}
				if (result < 0)
					return result;
				position = element.EndOffset;
			}
			return 0;
		}

		private int ParseInfo(int start, int end)
		{
			var position = start;
			while (position < end)
			{
				if (!ReadElement(ref position, end, out var element))
					return FfmpegError.InvalidData;
				if (element.Id == TimecodeScaleId)
					timecodeScale = ReadUnsigned(element);
				else if (element.Id == DurationId)
					duration = ReadFloat(element);
				position = element.EndOffset;
			}
			return timecodeScale == 0 ? FfmpegError.InvalidData : 0;
		}

		private int ParseTracks(int start, int end)
		{
			var position = start;
			while (position < end)
			{
				if (!ReadElement(ref position, end, out var element))
					return FfmpegError.InvalidData;
				if (element.Id == TrackEntryId)
				{
					var track = new MatroskaTrack { StreamIndex = tracks.Count, SamplingFrequency = 8000, Channels = 1, TimecodeScale = 1.0 };
					var result = ParseTrack(track, element.PayloadOffset, element.EndOffset);
					if (result < 0)
						return result;
					tracks.Add(track);
					if (selectedTrack == null && track.Type == 2 && track.CodecIdentifier == "A_OPUS")
						selectedTrack = track;
				}
				position = element.EndOffset;
			}
			return 0;
		}

		private int ParseTrack(MatroskaTrack track, int start, int end)
		{
			var position = start;
			while (position < end)
			{
				if (!ReadElement(ref position, end, out var element))
					return FfmpegError.InvalidData;
				switch (element.Id)
				{
					case TrackNumberId: track.Number = ReadUnsigned(element); break;
					case TrackTypeId: track.Type = ReadUnsigned(element); break;
					case CodecIdId: track.CodecIdentifier = ReadString(element); break;
					case CodecPrivateId: track.CodecPrivate = ReadBinary(element); break;
					case CodecDelayId: track.CodecDelay = ReadUnsigned(element); break;
					case SeekPreRollId: track.SeekPreRoll = ReadUnsigned(element); break;
					case DefaultDurationId: track.DefaultDuration = ReadUnsigned(element); break;
					case TrackTimecodeScaleId: track.TimecodeScale = ReadFloat(element); break;
					case AudioId:
						var result = ParseAudio(track, element.PayloadOffset, element.EndOffset);
						if (result < 0) return result;
						break;
				}
				position = element.EndOffset;
			}
			return track.Number == 0 ? FfmpegError.InvalidData : 0;
		}

		private int ParseAudio(MatroskaTrack track, int start, int end)
		{
			var position = start;
			while (position < end)
			{
				if (!ReadElement(ref position, end, out var element))
					return FfmpegError.InvalidData;
				if (element.Id == SamplingFrequencyId)
					track.SamplingFrequency = ReadFloat(element);
				else if (element.Id == OutputSamplingFrequencyId)
					track.OutputSamplingFrequency = ReadFloat(element);
				else if (element.Id == ChannelsId)
					track.Channels = checked((int)ReadUnsigned(element));
				else if (element.Id == BitDepthId)
					track.BitDepth = checked((int)ReadUnsigned(element));
				position = element.EndOffset;
			}
			return 0;
		}

		private int ParseCluster(int start, int end)
		{
			ulong clusterTimecode = ulong.MaxValue;
			var position = start;
			while (position < end)
			{
				if (!ReadElement(ref position, end, out var element))
					return FfmpegError.InvalidData;
				int result;
				if (element.Id == ClusterTimecodeId)
				{
					clusterTimecode = ReadUnsigned(element);
					result = 0;
				} else if (element.Id == SimpleBlockId)
					result = ParseBlock(element.PayloadOffset, element.PayloadSize, element.PayloadOffset, clusterTimecode, 0, 0);
				else if (element.Id == BlockGroupId)
					result = ParseBlockGroup(element.PayloadOffset, element.EndOffset, clusterTimecode);
				else
					result = 0;
				if (result < 0)
					return result;
				position = element.EndOffset;
			}
			return 0;
		}

		private int ParseBlockGroup(int start, int end, ulong clusterTimecode)
		{
			var blockOffset = -1;
			var blockSize = 0;
			long blockPosition = 0;
			ulong blockDuration = 0;
			long discardPadding = 0;
			var position = start;
			while (position < end)
			{
				if (!ReadElement(ref position, end, out var element))
					return FfmpegError.InvalidData;
				if (element.Id == BlockId)
				{
					blockOffset = element.PayloadOffset;
					blockSize = element.PayloadSize;
					blockPosition = element.PayloadOffset;
				} else if (element.Id == BlockDurationId)
					blockDuration = ReadUnsigned(element);
				else if (element.Id == DiscardPaddingId)
					discardPadding = ReadSigned(element);
				position = element.EndOffset;
			}
			return blockOffset < 0 ? 0 : ParseBlock(blockOffset, blockSize, blockPosition, clusterTimecode, blockDuration, discardPadding);
		}

		/// <summary>Parses FFmpeg's four Matroska lace layouts and assigns per-lace timing and padding.</summary>
		private int ParseBlock(int offset, int size, long position, ulong clusterTimecode, ulong blockDuration, long discardPaddingNanoseconds)
		{
			var pointer = offset;
			var end = offset + size;
			if (!ReadVariableInteger(ref pointer, end, false, out var trackNumber, out _))
				return FfmpegError.InvalidData;
			var track = FindTrack(trackNumber);
			if (track == null || end - pointer < 3)
				return FfmpegError.InvalidData;
			var blockTime = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(pointer, 2));
			pointer += 2;
			var flags = data[pointer++];
			var payloadSize = end - pointer;
			var laceType = flags >> 1 & 3;
			Span<int> laceSizes = stackalloc int[256];
			var laceCount = 1;
			if (laceType == 0)
				laceSizes[0] = payloadSize;
			else
			{
				if (payloadSize <= 0)
					return FfmpegError.InvalidData;
				laceCount = data[pointer++] + 1;
				payloadSize--;
				if (laceType == 1)
				{
					var total = 0;
					for (var lace = 0; lace < laceCount - 1; lace++)
					{
						int value;
						do
						{
							if (pointer >= end)
								return FfmpegError.InvalidData;
							value = data[pointer++];
							payloadSize--;
							laceSizes[lace] += value;
							total += value;
						} while (value == 255);
					}
					if (payloadSize < total)
						return FfmpegError.InvalidData;
					laceSizes[laceCount - 1] = payloadSize - total;
				} else if (laceType == 2)
				{
					if (payloadSize % laceCount != 0)
						return FfmpegError.InvalidData;
					for (var lace = 0; lace < laceCount; lace++)
						laceSizes[lace] = payloadSize / laceCount;
				} else
				{
					if (!ReadVariableInteger(ref pointer, end, false, out var firstSize, out _ ) || firstSize > int.MaxValue)
						return FfmpegError.InvalidData;
					laceSizes[0] = (int)firstSize;
					long total = laceSizes[0];
					for (var lace = 1; lace < laceCount - 1; lace++)
					{
						if (!ReadVariableInteger(ref pointer, end, true, out var encodedDifference, out var encodedLength))
							return FfmpegError.InvalidData;
						var bias = (1L << (7 * encodedLength - 1)) - 1;
						var difference = (long)encodedDifference - bias;
						var current = laceSizes[lace - 1] + difference;
						if (current < 0 || current > int.MaxValue)
							return FfmpegError.InvalidData;
						laceSizes[lace] = (int)current;
						total += current;
					}
					payloadSize = end - pointer;
					if (payloadSize < total)
						return FfmpegError.InvalidData;
					laceSizes[laceCount - 1] = checked((int)(payloadSize - total));
				}
			}

			if (track != selectedTrack)
				return 0;
			if (clusterTimecode == ulong.MaxValue || (blockTime < 0 && clusterTimecode < (ulong)-blockTime))
				return 0;
			var codecDelayTicks = RescaleNearest(track.CodecDelay, 1, timecodeScale);
			long timestamp = checked((long)((double)clusterTimecode / track.TimecodeScale)) + blockTime - codecDelayTicks;
			if (blockDuration == 0 && track.DefaultDuration != 0)
				blockDuration = track.DefaultDuration * (ulong)laceCount / timecodeScale;
			var skipSamples = discardPaddingNanoseconds < 0 ? RescaleNearest((ulong)-discardPaddingNanoseconds, 48000, 1000000000) : 0;
			var discardPadding = discardPaddingNanoseconds > 0 ? RescaleNearest((ulong)discardPaddingNanoseconds, 48000, 1000000000) : 0;
			for (var lace = 0; lace < laceCount; lace++)
			{
				var laceDuration = checked((long)(blockDuration * (ulong)(lace + 1) / (ulong)laceCount - blockDuration * (ulong)lace / (ulong)laceCount));
				if (laceDuration == 0 && OpusPacketParser.Parse(parsedOpusPacket, data, pointer, laceSizes[lace], false) >= 0)
					laceDuration = RescaleNearest((ulong)(parsedOpusPacket.FrameDuration * parsedOpusPacket.FrameCount), 1000000000, 48000 * timecodeScale);
				var packetData = new byte[laceSizes[lace]];
				data.AsSpan(pointer, packetData.Length).CopyTo(packetData);
				packets.Add(new MatroskaPacket(packetData, position, timestamp, laceDuration, track.StreamIndex, skipSamples, discardPadding));
				if (laceDuration != 0)
					timestamp += laceDuration;
				else
					timestamp = DemuxedAudioPacket.NoTimestamp;
				pointer += packetData.Length;
			}
			return pointer == end ? 0 : FfmpegError.InvalidData;
		}

		private MatroskaTrack FindTrack(ulong number)
		{
			for (var index = 0; index < tracks.Count; index++)
				if (tracks[index].Number == number)
					return tracks[index];
			return null;
		}

		private long ResolvePacketDuration()
		{
			if (packets.Count == 0)
				return 0;
			var last = packets[packets.Count - 1];
			return last.PresentationTimestamp == DemuxedAudioPacket.NoTimestamp ? 0 : last.PresentationTimestamp + last.Duration;
		}

		private bool ReadElement(ref int position, int parentEnd, out EbmlElement element)
		{
			element = default;
			if (!ReadVariableInteger(ref position, parentEnd, true, out var id, out _))
				return false;
			if (!ReadVariableInteger(ref position, parentEnd, false, out var sizeValue, out var sizeLength))
				return false;
			var unknownValue = sizeLength == 8 ? (1UL << 56) - 1 : (1UL << (7 * sizeLength)) - 1;
			var payloadSize = sizeValue == unknownValue ? parentEnd - position : checked((int)sizeValue);
			if (payloadSize < 0 || payloadSize > parentEnd - position)
				return false;
			element = new EbmlElement(id, position, payloadSize);
			return true;
		}

		private bool ReadVariableInteger(ref int position, int end, bool preserveMarker, out ulong value, out int length)
		{
			value = 0;
			length = 0;
			if (position >= end)
				return false;
			var first = data[position];
			var marker = 0x80;
			length = 1;
			while (length <= 8 && (first & marker) == 0)
			{
				marker >>= 1;
				length++;
			}
			if (length > 8 || length > end - position)
				return false;
			value = preserveMarker ? first : (ulong)(first & (marker - 1));
			position++;
			for (var index = 1; index < length; index++)
				value = value << 8 | data[position++];
			return true;
		}

		private ulong ReadUnsigned(EbmlElement element)
		{
			if (element.PayloadSize < 1 || element.PayloadSize > 8)
				throw new InvalidDataException();
			ulong value = 0;
			for (var index = 0; index < element.PayloadSize; index++)
				value = value << 8 | data[element.PayloadOffset + index];
			return value;
		}

		private long ReadSigned(EbmlElement element)
		{
			var value = ReadUnsigned(element);
			var shift = 64 - element.PayloadSize * 8;
			return unchecked((long)(value << shift)) >> shift;
		}

		private double ReadFloat(EbmlElement element)
		{
			if (element.PayloadSize == 4)
				return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(element.PayloadOffset, 4)));
			if (element.PayloadSize == 8)
				return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(element.PayloadOffset, 8)));
			throw new InvalidDataException();
		}

		private string ReadString(EbmlElement element)
		{
			return Encoding.UTF8.GetString(data, element.PayloadOffset, element.PayloadSize).TrimEnd('\0');
		}

		private byte[] ReadBinary(EbmlElement element)
		{
			var result = new byte[element.PayloadSize];
			data.AsSpan(element.PayloadOffset, element.PayloadSize).CopyTo(result);
			return result;
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

		private static ulong GreatestCommonDivisor(ulong left, ulong right)
		{
			while (right != 0)
			{
				var remainder = left % right;
				left = right;
				right = remainder;
			}
			return left;
		}

		private static int RescaleNearest(ulong value, ulong numerator, ulong denominator)
		{
			return checked((int)((value * numerator + denominator / 2) / denominator));
		}

		private readonly struct EbmlElement
		{
			public readonly ulong Id;
			public readonly int PayloadOffset;
			public readonly int PayloadSize;
			public int EndOffset => PayloadOffset + PayloadSize;

			public EbmlElement(ulong id, int payloadOffset, int payloadSize)
			{
				Id = id;
				PayloadOffset = payloadOffset;
				PayloadSize = payloadSize;
			}
		}

		/// <summary>Stores the subset of one Matroska TrackEntry that affects audio packet decoding and timing.</summary>
		private sealed class MatroskaTrack
		{
			public int StreamIndex;
			public ulong Number;
			public ulong Type;
			public string CodecIdentifier;
			public byte[] CodecPrivate;
			public ulong CodecDelay;
			public ulong SeekPreRoll;
			public ulong DefaultDuration;
			public double TimecodeScale;
			public double SamplingFrequency;
			public double OutputSamplingFrequency;
			public int Channels;
			public int BitDepth;
		}

		/// <summary>Stores one selected Matroska lace as the packet fields returned by ReadPacket.</summary>
		private sealed class MatroskaPacket
		{
			public readonly byte[] Data;
			public readonly long Position;
			public readonly long PresentationTimestamp;
			public readonly long Duration;
			public readonly int StreamIndex;
			public readonly int SkipSamples;
			public readonly int DiscardPadding;
			public long DecodedFrameStart;

			public MatroskaPacket(byte[] data, long position, long presentationTimestamp, long duration, int streamIndex, int skipSamples, int discardPadding)
			{
				Data = data;
				Position = position;
				PresentationTimestamp = presentationTimestamp;
				Duration = duration;
				StreamIndex = streamIndex;
				SkipSamples = skipSamples;
				DiscardPadding = discardPadding;
			}
		}
	}
}
