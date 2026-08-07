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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Splits raw ADTS or LOAS/LATM byte streams into complete AAC packets for the managed file decoder.
	/// </summary>
	public sealed class RawAacDemuxer : ISeekableAudioDemuxer
	{
		private static readonly int[] s_AdtsSampleRates =
		{
			96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
			16000, 12000, 11025, 8000, 7350
		};

		private readonly FormatReader _Reader;
		private readonly bool _IsLatm;
		private readonly byte[] _Header = new byte[10];
		private RawAacFrame[] _Frames = Array.Empty<RawAacFrame>();
		private int _CurrentFrame;

		public RawAacDemuxer(Stream stream, bool isLatm)
		{
			_Reader = new FormatReader(stream);
			_IsLatm = isLatm;
			StreamInfo = new AudioStreamInfo
			{
				CodecId = isLatm ? AudioCodecId.AacLatm : AudioCodecId.Aac
			};
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => _Frames.Length == 0 ? 0 : _Frames[0].Timestamp;

		/// <summary>
		/// Scans complete sync frames once, preserving their byte positions and sample durations.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0))
				return FfmpegError.InvalidArgument;
			var frames = new List<RawAacFrame>();
			var position = SkipId3v2Tags();
			if (position < 0)
				return FfmpegError.InvalidData;
			var timestamp = 0L;
			var sampleRate = 0;
			var channels = 0;
			while (position <= _Reader.Length - (_IsLatm ? 3 : 7))
			{
				if (!TryReadFrame(position, out var size, out var samples, out var frameSampleRate, out var frameChannels))
				{
					position++;
					continue;
				}
				if (position > _Reader.Length - size)
					break;
				frames.Add(new RawAacFrame(position, size, timestamp, samples));
				timestamp += samples;
				if (sampleRate == 0 && frameSampleRate > 0)
				{
					sampleRate = frameSampleRate;
					channels = frameChannels;
				}
				position += size;
			}
			if (frames.Count == 0)
				return FfmpegError.InvalidData;
			_Frames = frames.ToArray();
			_CurrentFrame = 0;
			StreamInfo.SampleRate = sampleRate;
			StreamInfo.Channels = channels;
			StreamInfo.Duration = timestamp;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = sampleRate;
			return 0;
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (_CurrentFrame >= _Frames.Length)
				return FfmpegError.EndOfFile;
			ref var frame = ref _Frames[_CurrentFrame];
			if (destination.Length < frame.Size || !_Reader.Seek(frame.Position))
				return FfmpegError.InvalidArgument;
			var read = _Reader.Read(destination.Slice(0, frame.Size));
			if (read != frame.Size)
				return FfmpegError.EndOfFile;
			packet = new DemuxedAudioPacket(read, frame.Position, frame.Timestamp, frame.Timestamp, frame.Duration, 0, false);
			_CurrentFrame++;
			return read;
		}

		/// <summary>Uses the ADTS/LOAS frame table built during header parsing for direct seeks.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (_Frames.Length == 0)
			{
				a_ActualTimestamp = 0;
				return false;
			}
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
			_CurrentFrame = l_Low;
			a_ActualTimestamp = _Frames[l_Low].Timestamp;
			return true;
		}

		private bool TryReadFrame(long position, out int size, out int samples, out int sampleRate, out int channels)
		{
			size = samples = sampleRate = channels = 0;
			var headerSize = _IsLatm ? 3 : 7;
			if (!_Reader.Seek(position) || !_Reader.ReadExactly(_Header.AsSpan(0, headerSize)))
				return false;
			if (_IsLatm)
			{
				if (_Header[0] != 0x56 || (_Header[1] & 0xe0) != 0xe0)
					return false;
				size = ((_Header[1] & 0x1f) << 8 | _Header[2]) + 3;
				samples = 1024;
				return size > 3;
			}

			if (_Header[0] != 0xff || (_Header[1] & 0xf6) != 0xf0)
				return false;
			var sampleRateIndex = _Header[2] >> 2 & 15;
			if (sampleRateIndex >= s_AdtsSampleRates.Length)
				return false;
			size = ((_Header[3] & 3) << 11) | (_Header[4] << 3) | (_Header[5] >> 5);
			var minimumSize = (_Header[1] & 1) != 0 ? 7 : 9;
			if (size < minimumSize)
				return false;
			samples = 1024 * ((_Header[6] & 3) + 1);
			sampleRate = s_AdtsSampleRates[sampleRateIndex];
			channels = ((_Header[2] & 1) << 2) | (_Header[3] >> 6);
			return channels > 0;
		}

		private long SkipId3v2Tags()
		{
			var position = 0L;
			while (position <= _Reader.Length - 10)
			{
				if (!_Reader.Seek(position) || !_Reader.ReadExactly(_Header))
					return -1;
				if (_Header[0] != (byte)'I' || _Header[1] != (byte)'D' || _Header[2] != (byte)'3')
					break;
				if ((_Header[6] & 0x80) != 0 || (_Header[7] & 0x80) != 0 || (_Header[8] & 0x80) != 0 || (_Header[9] & 0x80) != 0)
					return -1;
				var size = ((_Header[6] & 0x7f) << 21) | ((_Header[7] & 0x7f) << 14) |
					((_Header[8] & 0x7f) << 7) | (_Header[9] & 0x7f);
				position += size + 10L + ((_Header[5] & 0x10) != 0 ? 10 : 0);
			}
			return position;
		}

		private readonly struct RawAacFrame
		{
			public long Position { get; }
			public int Size { get; }
			public long Timestamp { get; }
			public int Duration { get; }

			public RawAacFrame(long position, int size, long timestamp, int duration)
			{
				Position = position;
				Size = size;
				Timestamp = timestamp;
				Duration = duration;
			}
		}
	}
}
