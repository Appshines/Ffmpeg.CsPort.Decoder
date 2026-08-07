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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports the audio-only AIFF and AIFC paths from FFmpeg's aiffdec.c, including COMM and SSND timing rules.
	/// </summary>
	public sealed class AiffDemuxer : ISeekableAudioDemuxer
	{
		private const uint FormTag = 0x4d524f46;
		private const uint AiffTag = 0x46464941;
		private const uint AifcTag = 0x43464941;
		private const uint CommTag = 0x4d4d4f43;
		private const uint SsndTag = 0x444e5353;
		private const uint FverTag = 0x52455646;
		private const uint AiffVersion = 0;
		private const uint AifcVersion1 = 0xa2805140;
		private const int MaximumPacketSize = 4096;

		private readonly FormatReader _Reader;
		private long _DataStart;
		private long _DataEnd;
		private int _BlockDuration;
		private long _CurrentTimestamp;

		public AiffDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;

		/// <summary>
		/// Scans FORM chunks, preserves FFmpeg's COMM/SSND ordering behavior, and seeks to the resolved sound offset.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0) ||
				!_Reader.ReadUInt32LittleEndian(out var formTag) || formTag != FormTag ||
				!_Reader.ReadUInt32BigEndian(out var formSize) || formSize < 4 ||
				!_Reader.ReadUInt32LittleEndian(out var formType))
			{
				return FfmpegError.InvalidData;
			}

			uint version;
			if (formType == AiffTag)
				version = AiffVersion;
			else if (formType == AifcTag)
				version = AifcVersion1;
			else
				return FfmpegError.InvalidData;

			long remainingFileSize = formSize - 4;
			long soundOffset = 0;
			long numberOfFrames = 0;
			while (remainingFileSize > 0 && _Reader.Position <= _Reader.Length - 8)
			{
				if (!_Reader.ReadUInt32LittleEndian(out var tag) || !_Reader.ReadUInt32BigEndian(out var chunkSize))
					return FfmpegError.InvalidData;
				remainingFileSize -= chunkSize + 8L;
				var payloadOffset = _Reader.Position;

				switch (tag)
				{
					case CommTag:
						var headerResult = ParseCommonChunk(chunkSize, version, out numberOfFrames);
						if (headerResult < 0)
							return headerResult;
						if (soundOffset > 0)
							return FinishHeader(soundOffset, numberOfFrames);
						break;
					case FverTag:
						if (!_Reader.ReadUInt32BigEndian(out version))
							return FfmpegError.InvalidData;
						break;
					case SsndTag:
						if (chunkSize < 8 || chunkSize > long.MaxValue - payloadOffset)
							return FfmpegError.InvalidData;
						_DataEnd = payloadOffset + chunkSize;
						if (!_Reader.ReadUInt32BigEndian(out var offset) || !_Reader.ReadUInt32BigEndian(out _))
							return FfmpegError.InvalidData;
						soundOffset = offset + _Reader.Position;
						break;
				}

				var nextOffset = payloadOffset + chunkSize + (chunkSize & 1);
				if (nextOffset < payloadOffset || nextOffset > _Reader.Length || !_Reader.Seek(nextOffset))
					break;
				if ((chunkSize & 1) != 0)
					remainingFileSize--;
			}

			return FinishHeader(soundOffset, numberOfFrames);
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			var maximumSize = _DataEnd - _Reader.Position;
			if (maximumSize <= 0)
				return FfmpegError.EndOfFile;
			if (StreamInfo.BlockAlign == 0)
				return FfmpegError.InvalidData;
			maximumSize -= maximumSize % StreamInfo.BlockAlign;
			if (maximumSize == 0)
				return FfmpegError.EndOfFile;

			var size = StreamInfo.CodecId == AudioCodecId.AdpcmImaQuickTime
				? StreamInfo.BlockAlign
				: MaximumPacketSize / StreamInfo.BlockAlign * StreamInfo.BlockAlign;
			if (size == 0)
				return FfmpegError.InvalidData;
			size = (int)Math.Min(maximumSize, size);
			if (destination.Length < size)
				return FfmpegError.InvalidArgument;

			var position = _Reader.Position;
			var read = _Reader.Read(destination.Slice(0, size));
			if (read <= 0)
				return FfmpegError.EndOfFile;

			var corrupt = read < size;
			if (size >= StreamInfo.BlockAlign)
				corrupt = false;
			var duration = read / StreamInfo.BlockAlign * (long)_BlockDuration;
			packet = new DemuxedAudioPacket(
				read,
				position,
				_CurrentTimestamp,
				_CurrentTimestamp,
				duration,
				0,
				corrupt);
			_CurrentTimestamp += duration;
			return read;
		}

		/// <summary>Seeks directly to the aligned AIFF sound block containing the requested sample timestamp.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			a_ActualTimestamp = 0;
			if (_DataStart <= 0 || StreamInfo.BlockAlign <= 0 || _BlockDuration <= 0)
				return false;
			var l_BlockIndex = Math.Max(0L, a_Timestamp) / _BlockDuration;
			var l_Position = _DataStart + (l_BlockIndex * StreamInfo.BlockAlign);
			if (l_Position >= _DataEnd)
				l_Position = Math.Max(_DataStart, _DataEnd - StreamInfo.BlockAlign);
			if (!_Reader.Seek(l_Position))
				return false;
			_CurrentTimestamp = ((l_Position - _DataStart) / StreamInfo.BlockAlign) * _BlockDuration;
			a_ActualTimestamp = _CurrentTimestamp;
			return true;
		}

		/// <summary>
		/// Parses COMM numeric fields and AIFC compression tags without changing their source rounding order.
		/// </summary>
		private int ParseCommonChunk(long size, uint version, out long numberOfFrames)
		{
			numberOfFrames = 0;
			var paddedSize = size + (size & 1);
			if (paddedSize < 18 ||
				!_Reader.ReadUInt16BigEndian(out var channels) ||
				!_Reader.ReadUInt32BigEndian(out var frameCount) ||
				!_Reader.ReadUInt16BigEndian(out var bitsPerSample) ||
				!_Reader.ReadUInt16BigEndian(out var exponentBits) ||
				!_Reader.ReadUInt64BigEndian(out var mantissa))
			{
				return FfmpegError.InvalidData;
			}

			var exponent = exponentBits - 16383 - 63;
			if (exponent < -63 || exponent > 63)
				return FfmpegError.InvalidData;
			var sampleRate = exponent >= 0
				? unchecked((int)(mantissa << exponent))
				: unchecked((int)((mantissa + (1UL << (-exponent - 1))) >> -exponent));
			if (sampleRate <= 0)
				return FfmpegError.InvalidData;

			StreamInfo.Channels = channels;
			StreamInfo.BitsPerCodedSample = bitsPerSample;
			StreamInfo.SampleRate = sampleRate;
			numberOfFrames = frameCount;
			paddedSize -= 18;

			if (paddedSize < 4)
			{
				version = AiffVersion;
			} else if (version == AifcVersion1)
			{
				if (!_Reader.ReadUInt32LittleEndian(out var codecTag))
					return FfmpegError.InvalidData;
				StreamInfo.CodecTag = codecTag;
				StreamInfo.CodecId = GetAiffCodecId(codecTag);
				paddedSize -= 4;
			}

			if (version != AifcVersion1 || StreamInfo.CodecId == AudioCodecId.PcmS16BigEndian)
			{
				StreamInfo.CodecId = GetUncompressedCodecId(bitsPerSample);
				StreamInfo.BitsPerCodedSample = PcmFormat.GetBitsPerSample(StreamInfo.CodecId);
				_BlockDuration = 1;
			} else
			{
				switch (StreamInfo.CodecId)
				{
					case AudioCodecId.PcmF32BigEndian:
					case AudioCodecId.PcmF64BigEndian:
					case AudioCodecId.PcmS16LittleEndian:
				case AudioCodecId.PcmALaw:
				case AudioCodecId.PcmMuLaw:
					_BlockDuration = 1;
					break;
				case AudioCodecId.AdpcmImaQuickTime:
					StreamInfo.BlockAlign = 34 * channels;
					StreamInfo.BitsPerCodedSample = 4;
					_BlockDuration = 64;
					break;
				default:
					_BlockDuration = 1;
						break;
				}
			}

			if (StreamInfo.BlockAlign == 0)
				StreamInfo.BlockAlign = PcmFormat.GetBitsPerSample(StreamInfo.CodecId) * channels >> 3;
			if (_BlockDuration != 0)
				StreamInfo.BitRate = (long)sampleRate * StreamInfo.BlockAlign * 8 / _BlockDuration;
			if (paddedSize != 0 && !_Reader.Skip(paddedSize))
				return FfmpegError.InvalidData;
			return 0;
		}

		private int FinishHeader(long soundOffset, long numberOfFrames)
		{
			if (StreamInfo.BlockAlign <= 0 || _BlockDuration < 0 || soundOffset <= 0 || !_Reader.Seek(soundOffset))
				return FfmpegError.InvalidData;
			_DataStart = soundOffset;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = StreamInfo.SampleRate;
			StreamInfo.Duration = numberOfFrames * _BlockDuration;
			return 0;
		}

		private static AudioCodecId GetUncompressedCodecId(int bitsPerSample)
		{
			if (bitsPerSample <= 8)
				return AudioCodecId.PcmS8;
			if (bitsPerSample <= 16)
				return AudioCodecId.PcmS16BigEndian;
			if (bitsPerSample <= 24)
				return AudioCodecId.PcmS24BigEndian;
			if (bitsPerSample <= 32)
				return AudioCodecId.PcmS32BigEndian;
			return AudioCodecId.None;
		}

		private static AudioCodecId GetAiffCodecId(uint tag)
		{
			var normalizedTag = tag | 0x20202020U;
			switch (normalizedTag)
			{
				case 0x656e6f6e: return AudioCodecId.PcmS16BigEndian;
				case 0x20776172: return AudioCodecId.PcmU8;
				case 0x32336c66: return AudioCodecId.PcmF32BigEndian;
				case 0x34366c66: return AudioCodecId.PcmF64BigEndian;
				case 0x77616c61: return AudioCodecId.PcmALaw;
				case 0x77616c75: return AudioCodecId.PcmMuLaw;
				case 0x34326e69: return AudioCodecId.PcmS24BigEndian;
				case 0x32336e69: return AudioCodecId.PcmS32BigEndian;
				case 0x736f7774: return AudioCodecId.PcmS16BigEndian;
				case 0x74776f73: return AudioCodecId.PcmS16LittleEndian;
				case 0x34616d69: return AudioCodecId.AdpcmImaQuickTime;
				default: return AudioCodecId.None;
			}
		}
	}
}
