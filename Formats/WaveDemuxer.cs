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
using System.IO;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports the audio-only RIFF, RIFX, RF64, and BW64 paths from FFmpeg's wavdec.c and riffdec.c.
	/// </summary>
	public sealed class WaveDemuxer : ISeekableAudioDemuxer
	{
		private const uint RiffTag = 0x46464952;
		private const uint RifxTag = 0x58464952;
		private const uint Rf64Tag = 0x34364652;
		private const uint Bw64Tag = 0x34365742;
		private const uint WaveTag = 0x45564157;
		private const uint Ds64Tag = 0x34367364;
		private const uint FormatTag = 0x20746d66;
		private const uint DataTag = 0x61746164;
		private const uint FactTag = 0x74636166;

		private readonly FormatReader _Reader;
		private long _DataStart;
		private long _DataEnd;
		private long _FramesPerBlock = 1;
		private int _MaximumPacketSize;
		private int _Unaligned;
		private bool _BigEndian;
		private int _FormatExtraSize;
		private ushort _FormatExtraFirstWord;
		private long _CurrentTimestamp;

		public WaveDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;

		/// <summary>
		/// Scans WAV chunks in source order, keeps FFmpeg's final data-chunk selection, and derives stream duration.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0))
				return FfmpegError.InvalidArgument;

			_Unaligned = (int)(_Reader.Position & 1);
			if (!_Reader.ReadUInt32LittleEndian(out var startTag))
				return FfmpegError.InvalidData;

			var rf64 = false;
			var bw64 = false;
			switch (startTag)
			{
				case RiffTag:
					break;
				case RifxTag:
					_BigEndian = true;
					break;
				case Rf64Tag:
					rf64 = true;
					break;
				case Bw64Tag:
					bw64 = true;
					break;
				default:
					return FfmpegError.InvalidData;
			}

			if (!_Reader.ReadUInt32LittleEndian(out _) ||
				!_Reader.ReadUInt32LittleEndian(out var waveTag) || waveTag != WaveTag)
			{
				return FfmpegError.InvalidData;
			}

			long dataSize = 0;
			long sampleCount = 0;
			if (rf64 || bw64)
			{
				if (!_Reader.ReadUInt32LittleEndian(out var ds64Tag) || ds64Tag != Ds64Tag ||
					!_Reader.ReadUInt32LittleEndian(out var ds64Size) || ds64Size < 24 ||
					!_Reader.ReadUInt64LittleEndian(out _) ||
					!_Reader.ReadUInt64LittleEndian(out var dataSize64) ||
					!_Reader.ReadUInt64LittleEndian(out var sampleCount64) ||
					dataSize64 > long.MaxValue || sampleCount64 > long.MaxValue ||
					!_Reader.Skip(ds64Size - 24))
				{
					return FfmpegError.InvalidData;
				}
				dataSize = (long)dataSize64;
				sampleCount = (long)sampleCount64;
			}

			var gotFormat = false;
			long dataOffset = -1;
			while (_Reader.Position <= _Reader.Length - 8)
			{
				if (!ReadNextTag(out var tag, out var size))
					break;
				var payloadOffset = _Reader.Position;
				if (size > long.MaxValue - payloadOffset)
					return FfmpegError.InvalidData;
				var nextTagOffset = payloadOffset + size + (size & 1);

				switch (tag)
				{
					case FormatTag:
						if (!gotFormat)
						{
							var result = ParseFormat(size);
							if (result < 0)
								return result;
						}
						gotFormat = true;
						break;
					case DataTag:
						if (!gotFormat)
							return FfmpegError.InvalidData;

						if (rf64 || bw64)
						{
							if (dataSize > long.MaxValue - payloadOffset)
								return FfmpegError.InvalidData;
							_DataEnd = payloadOffset + dataSize;
							nextTagOffset = _DataEnd + (dataSize & 1);
						} else if (size > 0 && size != uint.MaxValue)
						{
							dataSize = size;
							_DataEnd = payloadOffset + size;
							nextTagOffset = _DataEnd + (size & 1);
						} else
						{
							dataSize = 0;
							_DataEnd = long.MaxValue;
							nextTagOffset = long.MaxValue;
						}
						dataOffset = payloadOffset;
						break;
					case FactTag:
						if (sampleCount == 0)
						{
							var success = _BigEndian
								? _Reader.ReadUInt32BigEndian(out var bigEndianCount)
								: _Reader.ReadUInt32LittleEndian(out bigEndianCount);
							if (success)
								sampleCount = bigEndianCount;
						}
						break;
				}

				if (nextTagOffset == long.MaxValue || nextTagOffset >= _Reader.Length)
					break;
				if (!SeekTag(nextTagOffset))
					break;
			}

			if (!gotFormat || dataOffset < 0 || !_Reader.Seek(dataOffset))
				return FfmpegError.InvalidData;
			_DataStart = dataOffset;

			if (dataSize > long.MaxValue >> 3)
				dataSize = 0;

			var bitsPerSample = PcmFormat.GetBitsPerSample(StreamInfo.CodecId);
			if (sampleCount == 0 || bitsPerSample > 0)
			{
				if (StreamInfo.Channels != 0 && dataSize != 0 && bitsPerSample != 0 && _DataEnd <= _Reader.Length)
					sampleCount = (dataSize << 3) / ((long)StreamInfo.Channels * bitsPerSample);
			}

			if (sampleCount != 0)
				StreamInfo.Duration = sampleCount;
			if (sampleCount > 0 && dataSize > 0 && StreamInfo.BlockAlign > 0)
			{
				var l_BlockCount = dataSize / StreamInfo.BlockAlign;
				if (l_BlockCount > 0)
					_FramesPerBlock = Math.Max(1L, sampleCount / l_BlockCount);
			}
			if (StreamInfo.CodecId == AudioCodecId.AdpcmMicrosoft && StreamInfo.Channels > 0)
				_FramesPerBlock = Math.Max(1L, (StreamInfo.BlockAlign - 6L * StreamInfo.Channels) * 2 / StreamInfo.Channels);
			else if (StreamInfo.CodecId == AudioCodecId.AdpcmImaWave && StreamInfo.Channels > 0 &&
				StreamInfo.BitsPerCodedSample >= 2 && StreamInfo.BitsPerCodedSample < 8)
			{
				var l_BlockSizes = new[] { 4, 12, 4, 20, 4, 8 };
				var l_BlockSamples = new[] { 16, 32, 8, 32, 4, 8 };
				var l_Index = StreamInfo.BitsPerCodedSample - 2;
				_FramesPerBlock = Math.Max(1L, 1L + (StreamInfo.BlockAlign - 4L * StreamInfo.Channels) /
					(l_BlockSizes[l_Index] * StreamInfo.Channels) * l_BlockSamples[l_Index]);
			}

			if (StreamInfo.CodecId == AudioCodecId.PcmS24LittleEndian &&
				StreamInfo.BlockAlign == StreamInfo.Channels * 4 && StreamInfo.BitsPerCodedSample == 24)
			{
				StreamInfo.CodecId = AudioCodecId.PcmF24LittleEndian;
			} else if (StreamInfo.CodecId == AudioCodecId.PcmS32LittleEndian &&
				StreamInfo.BlockAlign == StreamInfo.Channels * 4 && StreamInfo.BitsPerCodedSample == 32 &&
				_FormatExtraSize == 2 && _FormatExtraFirstWord == 1)
			{
				StreamInfo.CodecId = AudioCodecId.PcmF16LittleEndian;
				StreamInfo.BitsPerCodedSample = 16;
			}

			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = StreamInfo.SampleRate;
			_MaximumPacketSize = PcmFormat.GetDefaultPacketSize(StreamInfo);
			if (_MaximumPacketSize < 0)
				_MaximumPacketSize = 4096;
			return 0;
		}

		/// <summary>
		/// Reads the next WAV audio packet across data chunks while preserving block alignment and sample timestamps.
		/// </summary>
		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			var left = _DataEnd - _Reader.Position;
			if (left <= 0)
			{
				left = FindDataTag();
				if (left < 0)
					return FfmpegError.EndOfFile;
				if (left > long.MaxValue - _Reader.Position)
					return FfmpegError.InvalidData;
				_DataEnd = _Reader.Position + left;
			}

			var size = _MaximumPacketSize;
			if (StreamInfo.BlockAlign > 1)
			{
				if (size < StreamInfo.BlockAlign)
					size = StreamInfo.BlockAlign;
				size = size / StreamInfo.BlockAlign * StreamInfo.BlockAlign;
			}
			size = (int)Math.Min(size, left);
			if (destination.Length < size)
				return FfmpegError.InvalidArgument;

			var position = _Reader.Position;
			var read = _Reader.Read(destination.Slice(0, size));
			if (read <= 0)
				return FfmpegError.EndOfFile;
			var duration = StreamInfo.BlockAlign > 0 ? read / StreamInfo.BlockAlign * _FramesPerBlock : 0;
			packet = new DemuxedAudioPacket(
				read,
				position,
				_CurrentTimestamp,
				_CurrentTimestamp,
				duration,
				0,
				false);
			_CurrentTimestamp += duration;
			return read;
		}

		/// <summary>Seeks to an encoded WAV block and reports the decoded sample timestamp represented by that block.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			a_ActualTimestamp = 0;
			if (_DataStart <= 0 || StreamInfo.BlockAlign <= 0 || _FramesPerBlock <= 0)
				return false;
			var l_BlockIndex = Math.Max(0L, a_Timestamp) / _FramesPerBlock;
			var l_Position = _DataStart + (l_BlockIndex * StreamInfo.BlockAlign);
			if (l_Position >= _DataEnd)
				l_Position = Math.Max(_DataStart, _DataEnd - StreamInfo.BlockAlign);
			if (!_Reader.Seek(l_Position))
				return false;
			_CurrentTimestamp = ((l_Position - _DataStart) / StreamInfo.BlockAlign) * _FramesPerBlock;
			a_ActualTimestamp = _CurrentTimestamp;
			return true;
		}

		/// <summary>
		/// Parses WAVEFORMAT, WAVEFORMATEX, and WAVEFORMATEXTENSIBLE fields required by the audio-only path.
		/// </summary>
		private int ParseFormat(long size)
		{
			if (size < 14 || size > int.MaxValue)
				return FfmpegError.InvalidData;

			var start = _Reader.Position;
			ushort id;
			ushort channels;
			uint sampleRate;
			uint byteRate;
			ushort blockAlign;
			if (_BigEndian)
			{
				if (!_Reader.ReadUInt16BigEndian(out id) || !_Reader.ReadUInt16BigEndian(out channels) ||
					!_Reader.ReadUInt32BigEndian(out sampleRate) || !_Reader.ReadUInt32BigEndian(out byteRate) ||
					!_Reader.ReadUInt16BigEndian(out blockAlign))
					return FfmpegError.InvalidData;
			} else
			{
				if (!_Reader.ReadUInt16LittleEndian(out id) || !_Reader.ReadUInt16LittleEndian(out channels) ||
					!_Reader.ReadUInt32LittleEndian(out sampleRate) || !_Reader.ReadUInt32LittleEndian(out byteRate) ||
					!_Reader.ReadUInt16LittleEndian(out blockAlign))
					return FfmpegError.InvalidData;
			}

			ushort bitsPerSample = 8;
			if (size != 14)
			{
				var success = _BigEndian
					? _Reader.ReadUInt16BigEndian(out bitsPerSample)
					: _Reader.ReadUInt16LittleEndian(out bitsPerSample);
				if (!success)
					return FfmpegError.InvalidData;
			}

			StreamInfo.CodecTag = id == 0xfffe ? 0U : id;
			StreamInfo.CodecId = id == 0xfffe ? AudioCodecId.None : GetWaveCodecId(id, bitsPerSample);
			StreamInfo.Channels = channels;
			StreamInfo.SampleRate = unchecked((int)sampleRate);
			StreamInfo.BitRate = byteRate * 8L;
			StreamInfo.BlockAlign = blockAlign;
			StreamInfo.BitsPerCodedSample = bitsPerSample;

			if (size >= 18)
			{
				if (!_Reader.ReadUInt16LittleEndian(out var declaredExtraSize))
					return FfmpegError.InvalidData;
				var remaining = (int)size - 18;
				var extraSize = Math.Min(remaining, declaredExtraSize);
				var extraData = new byte[extraSize];
				if (!_Reader.ReadExactly(extraData))
					return FfmpegError.InvalidData;
				StreamInfo.CodecExtraData = extraData;
				if (extraSize >= 22 && id == 0xfffe)
				{
					var validBits = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(0, 2));
					var channelMask = BinaryPrimitives.ReadUInt32LittleEndian(extraData.AsSpan(2, 4));
					var subFormat = BinaryPrimitives.ReadUInt32LittleEndian(extraData.AsSpan(6, 4));
					if (validBits != 0)
						StreamInfo.BitsPerCodedSample = validBits;
					StreamInfo.ChannelMask = channelMask;
					StreamInfo.CodecTag = subFormat;
					StreamInfo.CodecId = GetWaveCodecId(subFormat, StreamInfo.BitsPerCodedSample);
				} else if (extraSize > 0)
				{
					_FormatExtraSize = extraSize;
					if (extraSize >= 2)
						_FormatExtraFirstWord = BinaryPrimitives.ReadUInt16LittleEndian(extraData);
				}
			}

			if (StreamInfo.SampleRate <= 0)
				return FfmpegError.InvalidData;
			if (start + size < start || !_Reader.Seek(start + size))
				return FfmpegError.InvalidData;
			return 0;
		}

		private static AudioCodecId GetWaveCodecId(uint tag, int bitsPerSample)
		{
				switch (tag)
				{
					case 0x0001: return PcmFormat.GetCodecId(bitsPerSample, false, false, ~1);
					case 0x0002: return AudioCodecId.AdpcmMicrosoft;
					case 0x0011: return AudioCodecId.AdpcmImaWave;
				case 0x0003: return PcmFormat.GetCodecId(bitsPerSample, true, false, 0);
				case 0x0006: return AudioCodecId.PcmALaw;
				case 0x0007: return AudioCodecId.PcmMuLaw;
				default: return AudioCodecId.None;
			}
		}

		private bool ReadNextTag(out uint tag, out long size)
		{
			size = 0;
			if (!_Reader.ReadUInt32LittleEndian(out tag))
				return false;
			if (_BigEndian)
			{
				if (!_Reader.ReadUInt32BigEndian(out var bigEndianSize))
					return false;
				size = bigEndianSize;
			} else
			{
				if (!_Reader.ReadUInt32LittleEndian(out var littleEndianSize))
					return false;
				size = littleEndianSize;
			}
			return true;
		}

		private bool SeekTag(long offset)
		{
			if (offset < long.MaxValue && ((offset + _Unaligned) & 1) != 0)
				offset++;
			return _Reader.Seek(offset);
		}

		private long FindDataTag()
		{
			if (((_Reader.Position + _Unaligned) & 1) != 0 && !_Reader.Skip(1))
				return FfmpegError.EndOfFile;
			while (_Reader.Position <= _Reader.Length - 8)
			{
				if (!ReadNextTag(out var tag, out var size))
					return FfmpegError.EndOfFile;
				if (tag == DataTag)
					return size;
				if (!_Reader.Skip(size + (size & 1)))
					return FfmpegError.EndOfFile;
			}
			return FfmpegError.EndOfFile;
		}
	}
}
