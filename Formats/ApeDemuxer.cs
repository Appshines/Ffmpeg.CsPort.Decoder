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
	/// Ports FFmpeg's Monkey's Audio demuxer, including legacy headers, seek tables, aligned frames, and decoder preambles.
	/// </summary>
	public sealed class ApeDemuxer : ISeekableAudioDemuxer
	{
		private const uint ApeMarker = 0x2043414d;
		private const int MinimumVersion = 3800;
		private const int MaximumVersion = 3990;
		private const ushort FormatFlag8Bit = 1;
		private const ushort FormatFlagHasPeakLevel = 4;
		private const ushort FormatFlag24Bit = 8;
		private const ushort FormatFlagHasSeekElements = 16;
		private const ushort FormatFlagCreateWaveHeader = 32;

		private readonly FormatReader _Reader;
		private ApeFrame[] _Frames = Array.Empty<ApeFrame>();
		private int _CurrentFrame;
		private ushort _FileVersion;
		private ushort _CompressionType;
		private ushort _FormatFlags;
		private uint _BlocksPerFrame;
		private uint _FinalFrameBlocks;
		private uint _TotalFrames;
		private uint _WaveTailLength;

		public ApeDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo { CodecId = AudioCodecId.Ape };
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => _Frames.Length == 0 ? 0 : _Frames[0].PresentationTimestamp;

		/// <summary>
		/// Parses new and legacy APE descriptors exactly, then derives every aligned compressed frame from the seek table.
		/// </summary>
		public int ReadHeader()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0))
				return FfmpegError.InvalidArgument;
			var junkLength = checked((uint)_Reader.Position);
			if (!_Reader.ReadUInt32LittleEndian(out var marker) || marker != ApeMarker ||
				!_Reader.ReadUInt16LittleEndian(out _FileVersion))
				return FfmpegError.InvalidData;
			if (_FileVersion < MinimumVersion || _FileVersion > MaximumVersion)
				return FfmpegError.PatchWelcome;

			uint descriptorLength;
			uint headerLength;
			uint seekTableLength;
			uint waveHeaderLength;
			ushort bitsPerSample;
			ushort channels;
			uint sampleRate;
			if (_FileVersion >= 3980)
			{
				if (!_Reader.ReadUInt16LittleEndian(out _) ||
					!_Reader.ReadUInt32LittleEndian(out descriptorLength) ||
					!_Reader.ReadUInt32LittleEndian(out headerLength) ||
					!_Reader.ReadUInt32LittleEndian(out seekTableLength) ||
					!_Reader.ReadUInt32LittleEndian(out waveHeaderLength) ||
					!_Reader.ReadUInt32LittleEndian(out _) ||
					!_Reader.ReadUInt32LittleEndian(out _) ||
					!_Reader.ReadUInt32LittleEndian(out _WaveTailLength) ||
					!_Reader.Skip(16))
					return FfmpegError.EndOfFile;
				if (descriptorLength > 52 && !_Reader.Skip(descriptorLength - 52))
					return FfmpegError.EndOfFile;
				if (!_Reader.ReadUInt16LittleEndian(out _CompressionType) ||
					!_Reader.ReadUInt16LittleEndian(out _FormatFlags) ||
					!_Reader.ReadUInt32LittleEndian(out _BlocksPerFrame) ||
					!_Reader.ReadUInt32LittleEndian(out _FinalFrameBlocks) ||
					!_Reader.ReadUInt32LittleEndian(out _TotalFrames) ||
					!_Reader.ReadUInt16LittleEndian(out bitsPerSample) ||
					!_Reader.ReadUInt16LittleEndian(out channels) ||
					!_Reader.ReadUInt32LittleEndian(out sampleRate))
					return FfmpegError.EndOfFile;
			} else
			{
				descriptorLength = 0;
				headerLength = 32;
				if (!_Reader.ReadUInt16LittleEndian(out _CompressionType) ||
					!_Reader.ReadUInt16LittleEndian(out _FormatFlags) ||
					!_Reader.ReadUInt16LittleEndian(out channels) ||
					!_Reader.ReadUInt32LittleEndian(out sampleRate) ||
					!_Reader.ReadUInt32LittleEndian(out waveHeaderLength) ||
					!_Reader.ReadUInt32LittleEndian(out _WaveTailLength) ||
					!_Reader.ReadUInt32LittleEndian(out _TotalFrames) ||
					!_Reader.ReadUInt32LittleEndian(out _FinalFrameBlocks))
					return FfmpegError.EndOfFile;
				if ((_FormatFlags & FormatFlagHasPeakLevel) != 0)
				{
					if (!_Reader.Skip(4))
						return FfmpegError.EndOfFile;
					headerLength += 4;
				}
				if ((_FormatFlags & FormatFlagHasSeekElements) != 0)
				{
					if (!_Reader.ReadUInt32LittleEndian(out seekTableLength))
						return FfmpegError.EndOfFile;
					headerLength += 4;
					seekTableLength *= 4;
				} else
				{
					seekTableLength = _TotalFrames * 4;
				}
				bitsPerSample = (_FormatFlags & FormatFlag8Bit) != 0 ? (ushort)8 :
					(_FormatFlags & FormatFlag24Bit) != 0 ? (ushort)24 : (ushort)16;
				_BlocksPerFrame = _FileVersion >= 3950 ? 73728u * 4 :
					_FileVersion >= 3900 || (_FileVersion >= 3800 && _CompressionType >= 4000) ? 73728u : 9216u;
				if ((_FormatFlags & FormatFlagCreateWaveHeader) == 0 && !_Reader.Skip(waveHeaderLength))
					return FfmpegError.EndOfFile;
			}

			if (_TotalFrames == 0 || seekTableLength / 4 < _TotalFrames || channels == 0 || sampleRate == 0)
				return FfmpegError.InvalidData;
			var firstFrame = (ulong)junkLength + descriptorLength + headerLength + seekTableLength + waveHeaderLength;
			if (_FileVersion < 3810)
				firstFrame += _TotalFrames;
			if (firstFrame > long.MaxValue || _TotalFrames > int.MaxValue)
				return FfmpegError.InvalidData;

			_Frames = new ApeFrame[(int)_TotalFrames];
			_Frames[0].Position = (long)firstFrame;
			_Frames[0].Blocks = _BlocksPerFrame;
			if (!_Reader.ReadUInt32LittleEndian(out _))
				return FfmpegError.InvalidData;
			for (var index = 1; index < _Frames.Length; index++)
			{
				if (!_Reader.ReadUInt32LittleEndian(out var seekEntry))
					return FfmpegError.InvalidData;
				_Frames[index].Position = seekEntry + junkLength;
				_Frames[index].Blocks = _BlocksPerFrame;
				_Frames[index - 1].Size = _Frames[index].Position - _Frames[index - 1].Position;
				_Frames[index].Skip = (int)((_Frames[index].Position - _Frames[0].Position) & 3);
			}
			var unusedSeekEntries = seekTableLength / 4 - _TotalFrames;
			if (unusedSeekEntries != 0 && !_Reader.Skip(unusedSeekEntries))
				return FfmpegError.InvalidData;

			_Frames[^1].Blocks = _FinalFrameBlocks;
			var finalSize = _Reader.Length - _Frames[^1].Position - _WaveTailLength;
			finalSize -= finalSize & 3;
			if (finalSize <= 0)
				finalSize = _FinalFrameBlocks * 8L;
			_Frames[^1].Size = finalSize;
			for (var index = 0; index < _Frames.Length; index++)
			{
				if (_Frames[index].Skip != 0)
				{
					_Frames[index].Position -= _Frames[index].Skip;
					_Frames[index].Size += _Frames[index].Skip;
				}
				if (_Frames[index].Size <= 0 || _Frames[index].Size > int.MaxValue - 11)
					return FfmpegError.InvalidData;
				_Frames[index].Size = (_Frames[index].Size + 3) & ~3L;
			}
			if (_FileVersion < 3810)
			{
				for (var index = 0; index < _Frames.Length; index++)
				{
					if (!_Reader.ReadByte(out var bits))
						return FfmpegError.InvalidData;
					if (index != 0 && bits != 0)
						_Frames[index - 1].Size += 4;
					_Frames[index].Skip = _Frames[index].Skip * 8 + bits;
				}
			}

			var totalSamples = (long)(_TotalFrames - 1) * _BlocksPerFrame + _FinalFrameBlocks;
			var extraData = new byte[6];
			BinaryPrimitives.WriteUInt16LittleEndian(extraData, _FileVersion);
			BinaryPrimitives.WriteUInt16LittleEndian(extraData.AsSpan(2), _CompressionType);
			BinaryPrimitives.WriteUInt16LittleEndian(extraData.AsSpan(4), _FormatFlags);
			StreamInfo.CodecTag = 0x20455041;
			StreamInfo.SampleRate = checked((int)sampleRate);
			StreamInfo.Channels = channels;
			StreamInfo.BitsPerCodedSample = bitsPerSample;
			StreamInfo.Duration = totalSamples;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = sampleRate;
			StreamInfo.CodecExtraData = extraData;
			_CurrentFrame = 0;
			var pts = 0L;
			for (var index = 0; index < _Frames.Length; index++)
			{
				_Frames[index].PresentationTimestamp = pts;
				pts += _BlocksPerFrame;
			}
			return 0;
		}

		/// <summary>
		/// Emits FFmpeg's eight-byte block-count/bit-skip preamble followed by one aligned compressed APE frame.
		/// </summary>
		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (_CurrentFrame >= _Frames.Length)
				return FfmpegError.EndOfFile;
			ref var frame = ref _Frames[_CurrentFrame];
			var packetSize = checked((int)frame.Size + 8);
			if (destination.Length < packetSize)
				return FfmpegError.InvalidArgument;
			if (!_Reader.Seek(frame.Position))
				return FfmpegError.InvalidArgument;
			BinaryPrimitives.WriteUInt32LittleEndian(destination, frame.Blocks);
			BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4), frame.Skip);
			var read = _Reader.Read(destination.Slice(8, (int)frame.Size));
			if (read <= 0)
				return FfmpegError.EndOfFile;
			packetSize = read + 8;
			packet = new DemuxedAudioPacket(
				packetSize,
				-1,
				frame.PresentationTimestamp,
				frame.PresentationTimestamp,
				frame.Blocks,
				0,
				false);
			_CurrentFrame++;
			return packetSize;
		}

		/// <summary>Uses the APE seek table and resolved block timestamps for direct seeks.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (_Frames.Length == 0) { a_ActualTimestamp = 0; return false; }
			var l_Low = 0; var l_High = _Frames.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Frames[l_Middle].PresentationTimestamp <= a_Timestamp) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			_CurrentFrame = l_Low; a_ActualTimestamp = _Frames[l_Low].PresentationTimestamp; return true;
		}

		private struct ApeFrame
		{
			public long Position;
			public long Size;
			public uint Blocks;
			public int Skip;
			public long PresentationTimestamp;
		}
	}
}
