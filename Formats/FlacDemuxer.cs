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
using System.IO;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Codecs.Flac;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports the native FLAC metadata demuxer and emits parser-equivalent complete frames with exact timing and positions.
	/// </summary>
	public sealed class FlacDemuxer : ISeekableAudioDemuxer
	{
		private const uint FlacMarker = 0x43614c66;
		private const int SeekScanBufferSize = 4 * 1024 * 1024;
		// Below this remaining byte range the seek bisection switches to the exact frame-by-frame walk.
		private const int SeekBisectionLinearThresholdBytes = 128 * 1024;
		private const int SeekSyncScanWindowBytes = 64 * 1024;

		private readonly FormatReader _Reader;
		private readonly byte[] _HeaderPreview = new byte[32];
		private readonly BitReader _HeaderReader = new BitReader();
		private long _FirstFramePosition;
		private byte[] _SeekScanWindow;
		private byte[] _SeekVerifyBuffer;

		public FlacDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo { CodecId = AudioCodecId.Flac };
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;

		/// <summary>
		/// Consumes all FLAC metadata blocks and maps the mandatory STREAMINFO fields to FFmpeg stream parameters.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0) ||
				!_Reader.ReadUInt32LittleEndian(out var marker) || marker != FlacMarker)
			{
				return FfmpegError.InvalidData;
			}

			var foundStreamInfo = false;
			var last = false;
			while (!last)
			{
				if (!_Reader.ReadByte(out var typeAndLast) || !_Reader.ReadByte(out var sizeHigh) ||
					!_Reader.ReadByte(out var sizeMiddle) || !_Reader.ReadByte(out var sizeLow))
				{
					return FfmpegError.EndOfFile;
				}
				last = (typeAndLast & 0x80) != 0;
				var metadataType = typeAndLast & 0x7f;
				var metadataSize = sizeHigh << 16 | sizeMiddle << 8 | sizeLow;
				if (metadataType == 0)
				{
					if (foundStreamInfo || metadataSize != 34)
						return FfmpegError.InvalidData;
					var block = new byte[34];
					if (!_Reader.ReadExactly(block))
						return FfmpegError.EndOfFile;
					var parsed = new FlacStreamInfo();
					var parseResult = parsed.Parse(block);
					if (parseResult < 0)
						return parseResult;
					StreamInfo.CodecExtraData = block;
					StreamInfo.SampleRate = parsed.SampleRate;
					StreamInfo.Channels = parsed.Channels;
					StreamInfo.BitsPerCodedSample = parsed.BitsPerSample;
					StreamInfo.Duration = parsed.TotalSamples;
					StreamInfo.TimeBaseNumerator = 1;
					StreamInfo.TimeBaseDenominator = parsed.SampleRate;
					foundStreamInfo = true;
				} else if (!_Reader.Skip(metadataSize))
				{
					return FfmpegError.EndOfFile;
				}
			}

			_FirstFramePosition = _Reader.Position;
			return foundStreamInfo ? 0 : FfmpegError.InvalidData;
		}

		/// <summary>
		/// Finds the next frame boundary using FLAC CRC-16 plus a valid following header and returns parser timing fields.
		/// </summary>
		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (_Reader.Position >= _Reader.Length)
				return FfmpegError.EndOfFile;
			var position = _Reader.Position;
			if (!PeekFrameHeader(out var header))
				return FfmpegError.InvalidData;

			ushort crc = 0;
			var size = 0;
			while (_Reader.Position < _Reader.Length)
			{
				if (size >= destination.Length)
				{
					_Reader.Seek(position);
					return FfmpegError.InvalidArgument;
				}
				if (!_Reader.ReadByte(out var value))
					break;
				destination[size++] = value;
				crc = FlacCrc.Update16(crc, value);
				if (size >= 10 && crc == 0 &&
					(_Reader.Position == _Reader.Length || PeekFrameHeader(out _)))
				{
					var timestamp = header.IsVariableBlockSize
						? header.FrameOrSampleNumber
						: header.FrameOrSampleNumber * header.BlockSize;
					packet = new DemuxedAudioPacket(
						size,
						position,
						timestamp,
						timestamp,
						header.BlockSize,
						0,
						false);
					return size;
				}
			}

			_Reader.Seek(position);
			return FfmpegError.InvalidData;
		}

		/// <summary>
		/// Seeks by byte-position bisection over the frame sync codes instead of a whole-file index
		/// scan: FLAC frame headers carry their own CRC-8 and the exact frame/sample number, so any
		/// byte position resolves to the timestamp of the next real frame with a bounded window
		/// scan. The final anchor is CRC-16 verified and refined by an exact frame-by-frame walk,
		/// which keeps the returned frame position and timestamp identical to the former full index.
		/// </summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			a_ActualTimestamp = 0;
			if (_FirstFramePosition <= 0 || _FirstFramePosition >= _Reader.Length)
				return false;

			// The first frame is a trustworthy anchor (position from ReadHeader, timestamp zero).
			var l_BestPosition = _FirstFramePosition;
			var l_BestTimestamp = 0L;
			var l_Low = _FirstFramePosition + 1;
			var l_High = _Reader.Length;
			while (l_High - l_Low > SeekBisectionLinearThresholdBytes)
			{
				var l_Middle = l_Low + ((l_High - l_Low) / 2);
				if (!TryFindFrameForBisection(l_Middle, l_High, a_Timestamp, out var l_FramePosition, out var l_FrameTimestamp))
				{
					// No frame at or after the probe position: keep searching in the lower half.
					l_High = l_Middle;
					continue;
				}

				if (l_FrameTimestamp > a_Timestamp)
				{
					l_High = l_FramePosition;
					continue;
				}

				l_BestPosition = l_FramePosition;
				l_BestTimestamp = l_FrameTimestamp;
				l_Low = l_FramePosition + 1;
			}

			// Exact frame-by-frame walk over the remaining window, mirroring the former index choice
			// of the last frame at or before the requested timestamp.
			if (!_Reader.Seek(l_BestPosition))
				return false;
			if (_SeekVerifyBuffer == null)
				_SeekVerifyBuffer = new byte[SeekScanBufferSize];
			while (true)
			{
				var l_PacketPosition = _Reader.Position;
				var l_Result = ReadPacket(_SeekVerifyBuffer, out var l_Packet);
				if (l_Result < 0)
					break;
				if (l_Packet.PresentationTimestamp > a_Timestamp)
					break;
				l_BestPosition = l_PacketPosition;
				l_BestTimestamp = l_Packet.PresentationTimestamp;
			}

			if (!_Reader.Seek(l_BestPosition))
				return false;
			a_ActualTimestamp = l_BestTimestamp;
			return true;
		}

		/// <summary>
		/// Finds the first real frame at or after the probe position. Candidates are located over
		/// the two sync bytes plus the CRC-8 protected header parse; a candidate that would move
		/// the bisection anchor forward is additionally CRC-16 verified over the complete frame, so
		/// a false sync can never produce a wrong anchor (an unverified rejection merely shrinks the
		/// search window, which stays correct because frame timestamps are monotonic).
		/// </summary>
		private bool TryFindFrameForBisection(
			long a_ScanStart,
			long a_ScanLimit,
			long a_TargetTimestamp,
			out long a_FramePosition,
			out long a_FrameTimestamp)
		{
			a_FramePosition = 0;
			a_FrameTimestamp = 0;
			if (_SeekScanWindow == null)
				_SeekScanWindow = new byte[SeekSyncScanWindowBytes];
			var l_WindowStart = a_ScanStart;
			while (l_WindowStart < a_ScanLimit)
			{
				var l_WindowLength = (int)Math.Min(_SeekScanWindow.Length, a_ScanLimit - l_WindowStart);
				if (!_Reader.Seek(l_WindowStart))
					return false;
				var l_Read = _Reader.Read(_SeekScanWindow.AsSpan(0, l_WindowLength));
				if (l_Read <= 1)
					return false;

				for (var l_Index = 0; l_Index < l_Read - 1; l_Index++)
				{
					if (_SeekScanWindow[l_Index] != 0xff || (_SeekScanWindow[l_Index + 1] & 0xfc) != 0xf8)
						continue;
					var l_CandidatePosition = l_WindowStart + l_Index;
					if (!_Reader.Seek(l_CandidatePosition) || !PeekFrameHeader(out var l_Header))
						continue;
					var l_CandidateTimestamp = l_Header.IsVariableBlockSize
						? l_Header.FrameOrSampleNumber
						: l_Header.FrameOrSampleNumber * l_Header.BlockSize;
					if (l_CandidateTimestamp > a_TargetTimestamp)
					{
						a_FramePosition = l_CandidatePosition;
						a_FrameTimestamp = l_CandidateTimestamp;
						return true;
					}

					if (_SeekVerifyBuffer == null)
						_SeekVerifyBuffer = new byte[SeekScanBufferSize];
					if (!_Reader.Seek(l_CandidatePosition) || ReadPacket(_SeekVerifyBuffer, out _) < 0)
						continue;
					a_FramePosition = l_CandidatePosition;
					a_FrameTimestamp = l_CandidateTimestamp;
					return true;
				}

				// Overlap by one byte so a sync pair on the window boundary is not skipped.
				l_WindowStart += l_Read - 1;
			}

			return false;
		}

		private bool PeekFrameHeader(out FlacFrameHeader header)
		{
			header = default;
			var position = _Reader.Position;
			var available = (int)Math.Min(_HeaderPreview.Length, _Reader.Length - position);
			if (available < 6)
				return false;
			var read = _Reader.Read(_HeaderPreview.AsSpan(0, available));
			if (!_Reader.Seek(position) || read < 6)
				return false;
			return FlacFrameHeaderParser.Parse(_HeaderPreview, 0, read, _HeaderReader, out header) >= 0;
		}

	}
}
