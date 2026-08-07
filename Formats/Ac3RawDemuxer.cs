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
using System.Collections.Generic;
using System.IO;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Codecs.Ac3;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports FFmpeg's parsed raw AC-3/E-AC-3 audio path into complete sync-frame packets with sample timestamps.
	/// </summary>
	public sealed class Ac3RawDemuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer, IDecodedFrameCountAudioDemuxer
	{
		private const int RawTimeBase = 90000;
		private readonly FormatReader _Reader;
		private readonly byte[] _HeaderBytes = new byte[32];
		private Ac3RawFrame[] _Frames = Array.Empty<Ac3RawFrame>();
		private int[] _DecodableFrameIndices = Array.Empty<int>();
		private int _CurrentFrame;

		public Ac3RawDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo { TimeBaseNumerator = 1, TimeBaseDenominator = RawTimeBase };
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => _Frames.Length == 0 ? 0 : _Frames[0].Timestamp;
		public long DecodedFrameCount { get; private set; }

		/// <summary>
		/// Scans sync frames in parser order, grouping E-AC-3 dependent frames with their preceding independent frame.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0)) return FfmpegError.InvalidArgument;
			var frames = new List<Ac3RawFrame>();
			var position = 0L;
			var timestamp = 0L;
			var decodedFramePosition = 0L;
			var firstHeader = default(Ac3Header);
			var foundHeader = false;
			var streamChannels = 0;
			var currentChannelLayout = 0UL;
			var firstCodedFrameIndex = 0;

			while (position <= _Reader.Length - Ac3HeaderParser.HeaderSize)
			{
				if (!TryReadHeader(position, out var header))
				{
					if (foundHeader && frames.Count != 0)
					{
						var previous = frames[frames.Count - 1];
						previous.Size++;
						previous.HasCleanBoundary = false;
						frames[frames.Count - 1] = previous;
					}
					position++;
					continue;
				}
				var availableFrameSize = checked((int)Math.Min(header.FrameSize, _Reader.Length - position));
				if (!foundHeader)
				{
					firstHeader = header;
					foundHeader = true;
					if (position > 0)
					{
						var leadingDuration = (long)header.NumberOfSamples * RawTimeBase / header.SampleRate;
						frames.Add(new Ac3RawFrame(0, checked((int)position), timestamp, leadingDuration,
							header.NumberOfSamples, decodedFramePosition, false));
						timestamp += leadingDuration;
						decodedFramePosition += header.NumberOfSamples;
						firstCodedFrameIndex = 1;
					}
				}

				if (header.FrameType == (int)Eac3FrameType.Dependent && frames.Count != 0)
				{
					var lastIndex = frames.Count - 1;
					var previous = frames[lastIndex];
					previous.Size += availableFrameSize;
					frames[lastIndex] = previous;
					currentChannelLayout |= GetDependentChannelLayout(header.ChannelMap);
					streamChannels = Math.Max(streamChannels, CountBits(currentChannelLayout));
				} else
				{
					currentChannelLayout = GetChannelLayout(header.ChannelMode, header.LowFrequencyEffects);
					streamChannels = Math.Max(streamChannels, CountBits(currentChannelLayout));
					var duration = (long)header.NumberOfSamples * RawTimeBase / header.SampleRate;
					frames.Add(new Ac3RawFrame(position, availableFrameSize, timestamp, duration,
						header.NumberOfSamples, decodedFramePosition, true));
					timestamp += duration;
					decodedFramePosition += header.NumberOfSamples;
				}
				position += header.FrameSize;
			}
			if (foundHeader && position < _Reader.Length && frames.Count != 0)
			{
				var previous = frames[frames.Count - 1];
				previous.Size += checked((int)(_Reader.Length - position));
				previous.HasCleanBoundary = false;
				frames[frames.Count - 1] = previous;
			}

			if (!foundHeader || frames.Count == 0) return FfmpegError.InvalidData;
			BuildDecodedFrameIndex(frames);
			_Frames = frames.ToArray();
			_CurrentFrame = 0;
			StreamInfo.CodecId = firstHeader.IsEnhanced ? AudioCodecId.Eac3 : AudioCodecId.Ac3;
			StreamInfo.SampleRate = firstHeader.SampleRate;
			StreamInfo.Channels = streamChannels;
			StreamInfo.BitRate = firstHeader.IsEnhanced ? (long)_Frames[firstCodedFrameIndex].Size * 8 * firstHeader.SampleRate / firstHeader.NumberOfSamples : firstHeader.BitRate;
			StreamInfo.Duration = StreamInfo.BitRate == 0 ? timestamp : (_Reader.Length * 8 * RawTimeBase + StreamInfo.BitRate / 2) / StreamInfo.BitRate;
			return 0;
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (_CurrentFrame >= _Frames.Length) return FfmpegError.EndOfFile;
			ref var frame = ref _Frames[_CurrentFrame];
			if (destination.Length < frame.Size || !_Reader.Seek(frame.Position)) return FfmpegError.InvalidArgument;
			var read = _Reader.Read(destination.Slice(0, frame.Size));
			if (read != frame.Size) return FfmpegError.EndOfFile;
			packet = new DemuxedAudioPacket(read, frame.Position, frame.Timestamp, frame.Timestamp, frame.Duration, 0, false);
			_CurrentFrame++;
			return read;
		}

		/// <summary>Uses the scanned AC-3/E-AC-3 frame table for direct timestamp seeks.</summary>
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

		/// <summary>Uses exact cumulative AC-3 sample counts instead of the rounded 90 kHz packet durations.</summary>
		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			if (_DecodableFrameIndices.Length == 0) { a_ActualFrameIndex = 0; return false; }
			var l_Low = 0; var l_High = _DecodableFrameIndices.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Frames[_DecodableFrameIndices[l_Middle]].DecodedFramePosition <= a_FrameIndex) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			_CurrentFrame = _DecodableFrameIndices[l_Low];
			a_ActualFrameIndex = _Frames[_CurrentFrame].DecodedFramePosition;
			return true;
		}

		/// <summary>Validates only parser packets containing non-frame bytes and builds their decoded-output seek positions.</summary>
		private void BuildDecodedFrameIndex(List<Ac3RawFrame> a_Frames)
		{
			var l_MaximumPacketSize = 0;
			for (var l_Index = 0; l_Index < a_Frames.Count; l_Index++)
				if (!a_Frames[l_Index].HasCleanBoundary)
					l_MaximumPacketSize = Math.Max(l_MaximumPacketSize, a_Frames[l_Index].Size);
			var l_Packet = l_MaximumPacketSize == 0 ? Array.Empty<byte>() : new byte[l_MaximumPacketSize];
			var l_Output = l_MaximumPacketSize == 0 ? Array.Empty<byte>() : new byte[1536 * 16 * sizeof(float)];
			var l_DecodableFrameIndices = new List<int>(a_Frames.Count);
			var l_DecodedFramePosition = 0L;
			for (var l_Index = 0; l_Index < a_Frames.Count; l_Index++)
			{
				var l_Frame = a_Frames[l_Index];
				var l_ProducesOutput = l_Frame.HasCleanBoundary;
				if (!l_ProducesOutput && _Reader.Seek(l_Frame.Position) && _Reader.ReadExactly(l_Packet.AsSpan(0, l_Frame.Size)))
				{
					var l_Decoder = new Ac3Decoder();
					l_ProducesOutput = l_Decoder.DecodeFrame(l_Packet, 0, l_Frame.Size, l_Output, out var l_Decoded) >= 0 &&
						l_Decoded.NumberOfSamples > 0;
				}
				l_Frame.DecodedFramePosition = l_DecodedFramePosition;
				l_Frame.ProducesOutput = l_ProducesOutput;
				a_Frames[l_Index] = l_Frame;
				if (!l_ProducesOutput)
					continue;
				l_DecodableFrameIndices.Add(l_Index);
				l_DecodedFramePosition += l_Frame.SampleCount;
			}
			_DecodableFrameIndices = l_DecodableFrameIndices.ToArray();
			DecodedFrameCount = l_DecodedFramePosition;
		}

		private bool TryReadHeader(long position, out Ac3Header header)
		{
			header = default;
			if (!_Reader.Seek(position) || !_Reader.ReadExactly(_HeaderBytes)) return false;
			if (_HeaderBytes[0] == 0x77 && _HeaderBytes[1] == 0x0b)
			{
				for (var index = 0; index < _HeaderBytes.Length; index += 2)
				{
					var value = _HeaderBytes[index];
					_HeaderBytes[index] = _HeaderBytes[index + 1];
					_HeaderBytes[index + 1] = value;
				}
			}
			return Ac3HeaderParser.Parse(_HeaderBytes, 0, _HeaderBytes.Length, out header) == 0;
		}

		private static ulong GetChannelLayout(int channelMode, int lowFrequencyEffects)
		{
			var layout = channelMode switch
			{
				0 => (1UL << 0) | (1UL << 1),
				1 => 1UL << 2,
				2 => (1UL << 0) | (1UL << 1),
				3 => (1UL << 0) | (1UL << 1) | (1UL << 2),
				4 => (1UL << 0) | (1UL << 1) | (1UL << 8),
				5 => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 8),
				6 => (1UL << 0) | (1UL << 1) | (1UL << 9) | (1UL << 10),
				_ => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 9) | (1UL << 10)
			};
			return lowFrequencyEffects != 0 ? layout | (1UL << 3) : layout;
		}

		private static ulong GetDependentChannelLayout(int channelMap)
		{
			ReadOnlySpan<ulong> locations = stackalloc ulong[]
			{
				1UL << 0, 1UL << 2, 1UL << 1, 1UL << 9, 1UL << 10,
				(1UL << 6) | (1UL << 7), (1UL << 4) | (1UL << 5), 1UL << 8, 1UL << 11,
				(1UL << 33) | (1UL << 34), (1UL << 31) | (1UL << 32), (1UL << 12) | (1UL << 14),
				1UL << 13, (1UL << 15) | (1UL << 17), 1UL << 35, 1UL << 3
			};
			var layout = 0UL;
			for (var index = 0; index < 16; index++) if ((channelMap & (1 << (15 - index))) != 0) layout |= locations[index];
			return layout;
		}

		private static int CountBits(ulong value)
		{
			var count = 0;
			while (value != 0) { value &= value - 1; count++; }
			return count;
		}

		private struct Ac3RawFrame
		{
			public readonly long Position;
			public int Size;
			public readonly long Timestamp;
			public readonly long Duration;
			public readonly int SampleCount;
			public long DecodedFramePosition;
			public bool HasCleanBoundary;
			public bool ProducesOutput;

			public Ac3RawFrame(long position, int size, long timestamp, long duration, int sampleCount,
				long decodedFramePosition, bool hasCleanBoundary)
			{
				Position = position;
				Size = size;
				Timestamp = timestamp;
				Duration = duration;
				SampleCount = sampleCount;
				DecodedFramePosition = decodedFramePosition;
				HasCleanBoundary = hasCleanBoundary;
				ProducesOutput = hasCleanBoundary;
			}
		}
	}
}
