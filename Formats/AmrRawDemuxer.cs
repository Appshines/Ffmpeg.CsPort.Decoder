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
	/// Parses AMR-NB and AMR-WB storage files and emits one complete speech frame per packet.
	/// </summary>
	public sealed class AmrRawDemuxer : ISeekableAudioDemuxer, IFrameSeekableAudioDemuxer, IDecodedFrameCountAudioDemuxer
	{
		private static readonly byte[] s_NarrowBandMagic = { (byte)'#', (byte)'!', (byte)'A', (byte)'M', (byte)'R', (byte)'\n' };
		private static readonly byte[] s_WideBandMagic = { (byte)'#', (byte)'!', (byte)'A', (byte)'M', (byte)'R', (byte)'-', (byte)'W', (byte)'B', (byte)'\n' };
		private static readonly byte[] s_NarrowBandFrameSizes = { 13, 14, 16, 18, 20, 21, 27, 32, 6, 1, 1, 1, 1, 1, 1, 1 };
		private static readonly byte[] s_WideBandFrameSizes = { 18, 24, 33, 37, 41, 47, 51, 59, 61, 6, 1, 1, 1, 1, 1, 1 };

		private readonly FormatReader _Reader;
		private AmrFrame[] _Frames = Array.Empty<AmrFrame>();
		private int[] _DecodableFrameIndices = Array.Empty<int>();
		private int _CurrentFrame;

		public AmrRawDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => _Frames.Length == 0 ? 0 : _Frames[0].Timestamp;
		public long DecodedFrameCount { get; private set; }

		/// <summary>
		/// Validates the storage magic and indexes every supported speech frame without decoding it.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0))
				return FfmpegError.InvalidArgument;
			Span<byte> magic = stackalloc byte[s_WideBandMagic.Length];
			if (!_Reader.ReadExactly(magic.Slice(0, s_NarrowBandMagic.Length)))
				return FfmpegError.EndOfFile;
			var wideBand = magic.Slice(0, s_NarrowBandMagic.Length).SequenceEqual(s_WideBandMagic.AsSpan(0, s_NarrowBandMagic.Length));
			if (wideBand)
			{
				if (!_Reader.ReadExactly(magic.Slice(s_NarrowBandMagic.Length, s_WideBandMagic.Length - s_NarrowBandMagic.Length)) ||
					!magic.SequenceEqual(s_WideBandMagic))
					return FfmpegError.InvalidData;
			} else if (!magic.Slice(0, s_NarrowBandMagic.Length).SequenceEqual(s_NarrowBandMagic))
			{
				return FfmpegError.InvalidData;
			}

			var frameSizes = wideBand ? s_WideBandFrameSizes : s_NarrowBandFrameSizes;
			var frames = new List<AmrFrame>();
			var decodableFrameIndices = new List<int>();
			var position = (long)(wideBand ? s_WideBandMagic.Length : s_NarrowBandMagic.Length);
			var timestamp = 0L;
			var decodedFramePosition = 0L;
			while (position < _Reader.Length)
			{
				if (!_Reader.Seek(position) || !_Reader.ReadByte(out var toc))
					return FfmpegError.EndOfFile;
				var mode = toc >> 3 & 15;
				if (mode >= frameSizes.Length)
					return FfmpegError.PatchWelcome;
				var size = frameSizes[mode];
				if (position > _Reader.Length - size)
					return FfmpegError.InvalidData;
				var decodable = mode < 8;
				frames.Add(new AmrFrame(position, size, timestamp, decodedFramePosition, decodable));
				if (decodable)
				{
					decodableFrameIndices.Add(frames.Count - 1);
					decodedFramePosition += wideBand ? 320 : 160;
				}
				position += size;
				timestamp += wideBand ? 320 : 160;
			}
			if (frames.Count == 0)
				return FfmpegError.InvalidData;
			_Frames = frames.ToArray();
			_DecodableFrameIndices = decodableFrameIndices.ToArray();
			DecodedFrameCount = decodedFramePosition;
			_CurrentFrame = 0;
			StreamInfo.CodecId = wideBand ? AudioCodecId.AmrWideBand : AudioCodecId.AmrNarrowBand;
			StreamInfo.SampleRate = wideBand ? 16000 : 8000;
			StreamInfo.Channels = 1;
			StreamInfo.Duration = timestamp;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = StreamInfo.SampleRate;
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
			var duration = StreamInfo.CodecId == AudioCodecId.AmrWideBand ? 320 : 160;
			packet = new DemuxedAudioPacket(read, frame.Position, frame.Timestamp, frame.Timestamp, duration, 0, false);
			_CurrentFrame++;
			return read;
		}

		/// <summary>Uses the fixed-duration AMR frame table built during header parsing for direct seeks.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (_Frames.Length == 0)
			{
				a_ActualTimestamp = 0;
				return false;
			}
			var l_FrameDuration = StreamInfo.CodecId == AudioCodecId.AmrWideBand ? 320 : 160;
			_CurrentFrame = (int)Math.Min(_Frames.Length - 1, Math.Max(0L, a_Timestamp / l_FrameDuration));
			a_ActualTimestamp = _Frames[_CurrentFrame].Timestamp;
			return true;
		}

		/// <summary>Maps decoded output frames around rejected SID and reserved packets without rescanning the AMR file.</summary>
		public bool TrySeekToFrame(long a_FrameIndex, out long a_ActualFrameIndex)
		{
			if (_DecodableFrameIndices.Length == 0)
			{
				a_ActualFrameIndex = 0;
				return false;
			}
			var l_Low = 0;
			var l_High = _DecodableFrameIndices.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Frames[_DecodableFrameIndices[l_Middle]].DecodedFramePosition <= a_FrameIndex)
					l_Low = l_Middle;
				else
					l_High = l_Middle - 1;
			}
			_CurrentFrame = _DecodableFrameIndices[l_Low];
			a_ActualFrameIndex = _Frames[_CurrentFrame].DecodedFramePosition;
			return true;
		}

		private readonly struct AmrFrame
		{
			public long Position { get; }
			public int Size { get; }
			public long Timestamp { get; }
			public long DecodedFramePosition { get; }
			public bool Decodable { get; }

			public AmrFrame(long position, int size, long timestamp, long decodedFramePosition, bool decodable)
			{
				Position = position;
				Size = size;
				Timestamp = timestamp;
				DecodedFramePosition = decodedFramePosition;
				Decodable = decodable;
			}
		}
	}
}
