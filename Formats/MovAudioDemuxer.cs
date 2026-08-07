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
using System.IO.Compression;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports FFmpeg's audio-only MOV/MP4 sample-table and movie-fragment demuxing path.
	/// </summary>
	public sealed class MovAudioDemuxer : ISeekableAudioDemuxer
	{
		private FormatReader _Reader;
		private readonly List<MovTrack> _Tracks = new List<MovTrack>();
		private readonly List<MovTrex> _Trex = new List<MovTrex>();
		private MovTrack _SelectedTrack;
		private MovPacket[] _Packets = Array.Empty<MovPacket>();
		private int _CurrentPacket;
		private uint _MovieTimeScale;

		public MovAudioDemuxer(Stream stream)
		{
			_Reader = new FormatReader(stream);
			StreamInfo = new AudioStreamInfo();
		}

		public AudioStreamInfo StreamInfo { get; }
		public long FirstTimestamp => 0;
		public int MaximumPacketSize { get; private set; }

		/// <summary>
		/// Parses bounded ISO base-media and QuickTime atoms, selects the first audio track, and builds FFmpeg-equivalent packet indexes.
		/// </summary>
		public int ReadHeader()
		{
			try { return ReadHeaderCore(); }
			catch (InvalidDataException) { return FfmpegError.InvalidData; }
			catch (OverflowException) { return FfmpegError.InvalidData; }
			catch (ArgumentException) { return FfmpegError.InvalidData; }
		}

		/// <summary>
		/// Performs the header parse while the public FFmpeg-style boundary converts malformed-input failures to AVERROR_INVALIDDATA.
		/// </summary>
		private int ReadHeaderCore()
		{
			if (!_Reader.CanSeek || !_Reader.Seek(0)) return FfmpegError.InvalidArgument;
			_Tracks.Clear(); _Trex.Clear(); _SelectedTrack = null; _Packets = Array.Empty<MovPacket>(); _CurrentPacket = 0; _MovieTimeScale = 0; MaximumPacketSize = 0;
			var result = ParseAtoms(_Reader.Length, null, null, null);
			if (result < 0) return result;
			SelectAudioTrack();
			if (_SelectedTrack == null || _SelectedTrack.TimeScale == 0) return FfmpegError.InvalidData;

			if (_SelectedTrack.FragmentPackets.Count == 0)
			{
				result = BuildClassicIndex(_SelectedTrack);
				if (result < 0) return result;
				_Packets = _SelectedTrack.Packets.ToArray();
			} else
			{
				_Packets = _SelectedTrack.FragmentPackets.ToArray();
				if (_SelectedTrack.Duration == 0 && _Packets.Length != 0)
					_SelectedTrack.Duration = _Packets[^1].DecodeTimestamp + _Packets[^1].Duration;
			}
			var l_CumulativeTimestamp = 0L;
			for (var l_Index = 0; l_Index < _Packets.Length; l_Index++)
			{
				_Packets[l_Index].CumulativeTimestamp = l_CumulativeTimestamp;
				l_CumulativeTimestamp += Math.Max(0L, _Packets[l_Index].Duration);
				MaximumPacketSize = Math.Max(MaximumPacketSize, _Packets[l_Index].Size);
			}

			StreamInfo.StreamIndex = _SelectedTrack.StreamIndex;
			StreamInfo.CodecId = _SelectedTrack.CodecId;
			StreamInfo.CodecTag = BinaryPrimitives.ReverseEndianness(_SelectedTrack.CodecTagBigEndian);
			StreamInfo.SampleRate = _SelectedTrack.SampleRate;
			StreamInfo.Channels = _SelectedTrack.Channels;
			StreamInfo.BitsPerCodedSample = _SelectedTrack.BitsPerCodedSample;
			StreamInfo.BlockAlign = _SelectedTrack.BytesPerFrame > 0 ? checked((int)_SelectedTrack.BytesPerFrame) : _SelectedTrack.SampleSize;
			StreamInfo.BitRate = _SelectedTrack.BitRate;
			StreamInfo.Duration = _SelectedTrack.Duration;
			StreamInfo.TimeBaseNumerator = 1;
			StreamInfo.TimeBaseDenominator = _SelectedTrack.TimeScale;
			StreamInfo.StartSkipSamples = _SelectedTrack.StartSkipSamples;
			StreamInfo.CodecExtraData = _SelectedTrack.CodecExtraData;
			return 0;
		}

		public int ReadPacket(Span<byte> destination, out DemuxedAudioPacket packet)
		{
			packet = default;
			if (_CurrentPacket >= _Packets.Length) return FfmpegError.EndOfFile;
			ref var source = ref _Packets[_CurrentPacket];
			if (source.Size < 0 || destination.Length < source.Size || !_Reader.Seek(source.Position)) return FfmpegError.InvalidArgument;
			var read = _Reader.Read(destination.Slice(0, source.Size));
			if (read != source.Size) return FfmpegError.EndOfFile;
			packet = new DemuxedAudioPacket(read, source.Position, source.PresentationTimestamp, source.DecodeTimestamp,
				source.Duration, _SelectedTrack.StreamIndex, false, _CurrentPacket == 0 ? _SelectedTrack.StartSkipSamples : 0, 0);
			_CurrentPacket++;
			return read;
		}

		/// <summary>Uses cumulative sample durations so edit-list and presentation-time gaps cannot offset a decoder seek.</summary>
		public bool TrySeekToTimestamp(long a_Timestamp, out long a_ActualTimestamp)
		{
			if (_Packets.Length == 0) { a_ActualTimestamp = 0; return false; }
			var l_Low = 0; var l_High = _Packets.Length - 1;
			while (l_Low < l_High)
			{
				var l_Middle = l_Low + ((l_High - l_Low + 1) / 2);
				if (_Packets[l_Middle].CumulativeTimestamp <= a_Timestamp) l_Low = l_Middle; else l_High = l_Middle - 1;
			}
			_CurrentPacket = l_Low; a_ActualTimestamp = _Packets[l_Low].CumulativeTimestamp; return true;
		}

		/// <summary>
		/// Selects an AAC access unit on the decoded-sample timeline, which remains exact when MP4 packet durations contain priming adjustments.
		/// </summary>
		public bool TrySeekToDecodedFrame(long a_FrameIndex, int a_FramesPerPacket, int a_InitialPacketsWithoutOutput,
			out long a_ActualFrameIndex)
		{
			a_ActualFrameIndex = 0;
			if (_Packets.Length == 0 || a_FrameIndex < 0 || a_FramesPerPacket <= 0 ||
				a_InitialPacketsWithoutOutput < 0 || a_InitialPacketsWithoutOutput >= _Packets.Length)
				return false;
			var l_PacketOffset = Math.Min(a_FrameIndex / a_FramesPerPacket,
				_Packets.Length - a_InitialPacketsWithoutOutput - 1L);
			_CurrentPacket = l_PacketOffset == 0
				? 0
				: checked(a_InitialPacketsWithoutOutput + (int)l_PacketOffset);
			a_ActualFrameIndex = checked(l_PacketOffset * a_FramesPerPacket);
			return true;
		}

		/// <summary>
		/// Walks only the atom containers used by FFmpeg's audio path and leaves every sibling seek position bounded by its parent.
		/// </summary>
		private int ParseAtoms(long end, MovTrack track, MovFragment fragment, MovFragmentTrack fragmentTrack)
		{
			while (_Reader.Position < end)
			{
				if (!ReadAtomHeader(end, out var atom)) return FfmpegError.InvalidData;
				int result;
				switch (atom.Type)
				{
					case 0x6d6f6f76: // moov
						result = ParseAtoms(atom.End, track, fragment, fragmentTrack); SelectAudioTrack(); break;
					case 0x636d6f76: result = ParseCompressedMovie(atom); break; // cmov
					case 0x7472616b: // trak
						var childTrack = new MovTrack { StreamIndex = _Tracks.Count }; _Tracks.Add(childTrack);
						result = ParseAtoms(atom.End, childTrack, fragment, fragmentTrack); break;
					case 0x6d646961: // mdia
					case 0x6d696e66: // minf
					case 0x7374626c: // stbl
					case 0x65647473: // edts
					case 0x6d766578: // mvex
						result = ParseAtoms(atom.End, track, fragment, fragmentTrack); break;
					case 0x6d6f6f66: // moof
						var childFragment = new MovFragment { Position = atom.Start };
						result = ParseAtoms(atom.End, track, childFragment, null); break;
					case 0x74726166: // traf
						var childFragmentTrack = new MovFragmentTrack();
						result = ParseAtoms(atom.End, track, fragment, childFragmentTrack);
						if (result >= 0) result = BuildFragmentIndex(fragment, childFragmentTrack); break;
					case 0x6d766864: result = ParseMovieHeader(atom); break; // mvhd
					case 0x746b6864: result = track == null ? 0 : ParseTrackHeader(atom, track); break; // tkhd
					case 0x6d646864: result = track == null ? 0 : ParseMediaHeader(atom, track); break; // mdhd
					case 0x68646c72: result = track == null ? 0 : ParseHandler(atom, track); break; // hdlr
					case 0x73747364: result = track == null || track.HandlerType != 0x736f756e ? 0 : ParseSampleDescriptions(atom, track); break; // stsd
					case 0x73747473: result = track == null || track.HandlerType != 0x736f756e ? 0 : ParseTimeToSample(atom, track); break; // stts
					case 0x63747473: result = track == null || track.HandlerType != 0x736f756e ? 0 : ParseCompositionOffsets(atom, track); break; // ctts
					case 0x73747363: result = track == null || track.HandlerType != 0x736f756e ? 0 : ParseSampleToChunk(atom, track); break; // stsc
					case 0x7374737a: // stsz
					case 0x73747a32: result = track == null || track.HandlerType != 0x736f756e ? 0 : ParseSampleSizes(atom, track); break; // stz2
					case 0x7374636f: // stco
					case 0x636f3634: result = track == null || track.HandlerType != 0x736f756e ? 0 : ParseChunkOffsets(atom, track); break; // co64
					case 0x656c7374: result = track == null ? 0 : ParseEditList(atom, track); break; // elst
					case 0x74726578: result = ParseTrex(atom); break; // trex
					case 0x74666864: result = fragmentTrack == null ? 0 : ParseTfhd(atom, fragment, fragmentTrack); break; // tfhd
					case 0x74666474: result = fragmentTrack == null ? 0 : ParseTfdt(atom, fragmentTrack); break; // tfdt
					case 0x7472756e: result = fragmentTrack == null ? 0 : ParseTrun(atom, fragmentTrack); break; // trun
					default: result = 0; break;
				}
				if (result < 0) return result;
				if (!_Reader.Seek(atom.End)) return FfmpegError.InvalidData;
			}
			return _Reader.Position == end ? 0 : FfmpegError.InvalidData;
		}

		private bool ReadAtomHeader(long parentEnd, out MovAtom atom)
		{
			atom = default; var start = _Reader.Position;
			if (parentEnd - start < 8 || !_Reader.ReadUInt32BigEndian(out var size32) || !_Reader.ReadUInt32BigEndian(out var type)) return false;
			ulong size = size32; var headerSize = 8L;
			if (size32 == 1)
			{
				if (!_Reader.ReadUInt64BigEndian(out size)) return false;
				headerSize = 16;
			} else if (size32 == 0) size = checked((ulong)(parentEnd - start));
			if (type == 0x75756964) { if (size < 24 || !_Reader.Skip(16)) return false; headerSize += 16; }
			if (size < (ulong)headerSize || size > long.MaxValue || start > parentEnd - (long)size) return false;
			atom = new MovAtom(start, start + headerSize, start + (long)size, type);
			return true;
		}

		private int ParseMovieHeader(MovAtom atom)
		{
			if (!ReadFullBox(out var version, out _)) return FfmpegError.EndOfFile;
			if (version > 1 || !_Reader.Skip(version == 1 ? 16 : 8) || !_Reader.ReadUInt32BigEndian(out _MovieTimeScale)) return FfmpegError.InvalidData;
			return 0;
		}

		/// <summary>
		/// Expands legacy QuickTime CMOV metadata with the built-in zlib stream and parses the decompressed atom payload in place.
		/// </summary>
		private int ParseCompressedMovie(MovAtom atom)
		{
			if (!ReadAtomHeader(atom.End, out var compression) || compression.Type != 0x64636f6d || compression.End - compression.Payload != 4 ||
				!_Reader.ReadUInt32BigEndian(out var algorithm) || algorithm != 0x7a6c6962 || !_Reader.Seek(compression.End) ||
				!ReadAtomHeader(atom.End, out var compressed) || compressed.Type != 0x636d7664 || !_Reader.ReadUInt32BigEndian(out var expandedSize) || expandedSize > int.MaxValue)
				return FfmpegError.InvalidData;
			var compressedLength = checked((int)(compressed.End - _Reader.Position)); var compressedBytes = new byte[compressedLength];
			if (!_Reader.ReadExactly(compressedBytes)) return FfmpegError.EndOfFile;
			var expandedBytes = new byte[expandedSize]; var expandedOffset = 0;
			using (var input = new MemoryStream(compressedBytes, false))
			using (var zlib = new ZLibStream(input, CompressionMode.Decompress, false))
			{
				while (expandedOffset < expandedBytes.Length)
				{
					var read = zlib.Read(expandedBytes, expandedOffset, expandedBytes.Length - expandedOffset); if (read <= 0) break; expandedOffset += read;
				}
			}
			if (expandedOffset != expandedBytes.Length) return FfmpegError.InvalidData;
			var originalReader = _Reader; var originalPosition = atom.End;
			using (var expanded = new MemoryStream(expandedBytes, false))
			{
				_Reader = new FormatReader(expanded); var result = ParseAtoms(expanded.Length, null, null, null); _Reader = originalReader;
				if (!originalReader.Seek(originalPosition)) return FfmpegError.InvalidData;
				return result;
			}
		}

		private int ParseTrackHeader(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out var version, out _) || version > 1 || !_Reader.Skip(version == 1 ? 16 : 8) || !_Reader.ReadUInt32BigEndian(out track.TrackId))
				return FfmpegError.EndOfFile;
			return 0;
		}

		private int ParseMediaHeader(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out var version, out _) || version > 1 || !_Reader.Skip(version == 1 ? 16 : 8) ||
				!_Reader.ReadUInt32BigEndian(out track.TimeScale)) return FfmpegError.EndOfFile;
			if (version == 1)
			{
				if (!_Reader.ReadUInt64BigEndian(out var duration)) return FfmpegError.EndOfFile;
				track.Duration = duration == ulong.MaxValue || duration > long.MaxValue ? 0 : (long)duration;
			} else
			{
				if (!_Reader.ReadUInt32BigEndian(out var duration)) return FfmpegError.EndOfFile;
				track.Duration = duration == uint.MaxValue ? 0 : duration;
			}
			return 0;
		}

		private int ParseHandler(MovAtom atom, MovTrack track)
		{
			if (!_Reader.Skip(8) || !_Reader.ReadUInt32BigEndian(out var handlerType)) return FfmpegError.EndOfFile;
			if (track.HandlerType == 0 || handlerType == 0x736f756e) track.HandlerType = handlerType;
			return 0;
		}

		/// <summary>
		/// Parses QuickTime audio sample-entry versions 0/1/2 and their codec-specific child atoms.
		/// </summary>
		private int ParseSampleDescriptions(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out _, out _) || !_Reader.ReadUInt32BigEndian(out var entryCount) || entryCount == 0 || entryCount > 1024) return FfmpegError.InvalidData;
			for (var entryIndex = 0U; entryIndex < entryCount; entryIndex++)
			{
				var start = _Reader.Position;
				if (!_Reader.ReadUInt32BigEndian(out var size) || !_Reader.ReadUInt32BigEndian(out var format) || size < 36 || start > atom.End - size) return FfmpegError.InvalidData;
				var entryEnd = start + size;
				if (!_Reader.Skip(6) || !_Reader.ReadUInt16BigEndian(out _)) return FfmpegError.EndOfFile;
				if (!_Reader.ReadUInt16BigEndian(out var version) || !_Reader.Skip(6) || !_Reader.ReadUInt16BigEndian(out var channels) ||
					!_Reader.ReadUInt16BigEndian(out var bits) || !_Reader.ReadUInt16BigEndian(out _) || !_Reader.ReadUInt16BigEndian(out _) ||
					!_Reader.ReadUInt32BigEndian(out var fixedRate)) return FfmpegError.EndOfFile;
				if (entryIndex == 0)
				{
					track.CodecTagBigEndian = format; track.CodecId = MapCodec(format, bits); track.Channels = channels;
					track.BitsPerCodedSample = bits; track.SampleRate = checked((int)(fixedRate >> 16));
				}
				if (version == 1)
				{
					if (!_Reader.ReadUInt32BigEndian(out var samplesPerFrame) || !_Reader.ReadUInt32BigEndian(out _) ||
						!_Reader.ReadUInt32BigEndian(out var bytesPerFrame) || !_Reader.ReadUInt32BigEndian(out _)) return FfmpegError.EndOfFile;
					if (entryIndex == 0) { track.SamplesPerFrame = samplesPerFrame; track.BytesPerFrame = bytesPerFrame; }
				} else if (version == 2)
				{
					if (!_Reader.Skip(4) || !_Reader.ReadUInt64BigEndian(out var rateBits) || !_Reader.ReadUInt32BigEndian(out var channelCount) ||
						!_Reader.Skip(4) || !_Reader.ReadUInt32BigEndian(out var codedBits) || !_Reader.ReadUInt32BigEndian(out var flags) ||
						!_Reader.ReadUInt32BigEndian(out var bytesPerFrame) || !_Reader.ReadUInt32BigEndian(out var samplesPerFrame)) return FfmpegError.EndOfFile;
					if (entryIndex == 0)
					{
						track.SampleRate = checked((int)BitConverter.Int64BitsToDouble(unchecked((long)rateBits))); track.Channels = checked((int)channelCount);
						track.BitsPerCodedSample = checked((int)codedBits); track.BytesPerFrame = bytesPerFrame; track.SamplesPerFrame = samplesPerFrame;
						if (format == 0x6c70636d) track.CodecId = MapLinearPcm(track.BitsPerCodedSample, flags);
					}
				}
				if (entryIndex == 0)
				{
					FinalizeAudioEntry(track);
					var result = ParseSampleEntryChildren(entryEnd, track);
					if (result < 0) return result;
				}
				if (!_Reader.Seek(entryEnd)) return FfmpegError.InvalidData;
			}
			return 0;
		}

		private int ParseSampleEntryChildren(long end, MovTrack track)
		{
			while (_Reader.Position < end)
			{
				if (end - _Reader.Position < 8) return _Reader.Seek(end) ? 0 : FfmpegError.InvalidData;
				if (!ReadAtomHeader(end, out var atom)) return FfmpegError.InvalidData;
				var result = 0;
				if (atom.Type == 0x65736473) result = ParseEsds(atom, track); // esds
				else if (atom.Type == 0x616c6163 && track.CodecId == AudioCodecId.Alac) result = ReadAtomAsExtraData(atom, track); // alac
				else if (atom.Type == 0x77617665) // wave
				{
					if (track.CodecId == AudioCodecId.Qdm2 || track.CodecId == AudioCodecId.Qdmc) result = ReadPayloadAsExtraData(atom, track);
					else result = ParseSampleEntryChildren(atom.End, track);
				} else if (atom.Type == 0x676c626c) result = ReadPayloadAsExtraData(atom, track); // glbl
				if (result < 0) return result;
				if (!_Reader.Seek(atom.End)) return FfmpegError.InvalidData;
			}
			if (track.CodecId == AudioCodecId.Alac && track.CodecExtraData != null && track.CodecExtraData.Length == 36)
			{
				track.Channels = track.CodecExtraData[21];
				track.SampleRate = checked((int)BinaryPrimitives.ReadUInt32BigEndian(track.CodecExtraData.AsSpan(32)));
			}
			return 0;
		}

		/// <summary>
		/// Extracts the MPEG-4 DecoderSpecificInfo bytes and the average bitrate from an ES descriptor.
		/// </summary>
		private int ParseEsds(MovAtom atom, MovTrack track)
		{
			if (!_Reader.Skip(4) || !ReadDescriptor(atom.End, out var tag, out _)) return FfmpegError.InvalidData;
			if (tag == 3)
			{
				if (!_Reader.ReadUInt16BigEndian(out _) || !_Reader.ReadByte(out var flags)) return FfmpegError.EndOfFile;
				if ((flags & 0x80) != 0 && !_Reader.Skip(2)) return FfmpegError.EndOfFile;
				if ((flags & 0x40) != 0) { if (!_Reader.ReadByte(out var length) || !_Reader.Skip(length)) return FfmpegError.EndOfFile; }
				if ((flags & 0x20) != 0 && !_Reader.Skip(2)) return FfmpegError.EndOfFile;
				if (!ReadDescriptor(atom.End, out tag, out _)) return FfmpegError.InvalidData;
			}
			if (tag != 4 || !_Reader.ReadByte(out var objectType) || !_Reader.Skip(4) || !_Reader.ReadUInt32BigEndian(out _) ||
				!_Reader.ReadUInt32BigEndian(out var averageBitRate)) return FfmpegError.InvalidData;
			track.BitRate = averageBitRate;
			if (!ReadDescriptor(atom.End, out tag, out var configLength) || tag != 5 || configLength <= 0 || configLength > atom.End - _Reader.Position) return 0;
			track.CodecExtraData = new byte[configLength];
			if (!_Reader.ReadExactly(track.CodecExtraData)) return FfmpegError.EndOfFile;
			if (objectType == 0x69 || objectType == 0x6b) track.CodecId = AudioCodecId.Mp3;
			else if (objectType == 0x40) ParseAudioSpecificConfig(track);
			return 0;
		}

		private bool ReadDescriptor(long end, out int tag, out int length)
		{
			tag = 0; length = 0;
			if (_Reader.Position >= end || !_Reader.ReadByte(out var tagByte)) return false;
			tag = tagByte;
			for (var index = 0; index < 4; index++)
			{
				if (_Reader.Position >= end || !_Reader.ReadByte(out var value)) return false;
				length = (length << 7) | (value & 0x7f);
				if ((value & 0x80) == 0) return true;
			}
			return true;
		}

		private void ParseAudioSpecificConfig(MovTrack track)
		{
			var containerSampleRate = track.SampleRate;
			var bits = new AscBitReader(track.CodecExtraData);
			var objectType = bits.ReadObjectType(); var sampleRate = bits.ReadSampleRate(); var channelConfiguration = bits.Read(4);
			var sbrObject = objectType == 5 || objectType == 29;
			var coreObjectType = objectType;
			if (objectType == 5 || objectType == 29) { var extensionRate = bits.ReadSampleRate(); coreObjectType = bits.ReadObjectType(); if (extensionRate > 0) sampleRate = extensionRate; if (objectType == 29 && channelConfiguration == 1) channelConfiguration = 2; }
			else
			{
				for (var position = bits.BitPosition; position + 21 <= track.CodecExtraData.Length * 8; position++)
				{
					if (AscBitReader.ReadAt(track.CodecExtraData, position, 11) != 0x2b7 || AscBitReader.ReadAt(track.CodecExtraData, position + 11, 5) != 5 || AscBitReader.ReadAt(track.CodecExtraData, position + 16, 1) == 0) continue;
					var rateIndex = AscBitReader.ReadAt(track.CodecExtraData, position + 17, 4);
					if (rateIndex == 15 && position + 45 <= track.CodecExtraData.Length * 8) sampleRate = AscBitReader.ReadAt(track.CodecExtraData, position + 21, 24);
					else if (rateIndex < AscBitReader.SampleRateCount) sampleRate = AscBitReader.GetSampleRate(rateIndex);
					break;
				}
			}
			for (var position = bits.BitPosition; position + 12 <= track.CodecExtraData.Length * 8; position++)
				if (AscBitReader.ReadAt(track.CodecExtraData, position, 11) == 0x548 && AscBitReader.ReadAt(track.CodecExtraData, position + 11, 1) != 0) { channelConfiguration = 2; break; }
			if (channelConfiguration == 0) channelConfiguration = ReadProgramConfigChannels(ref bits, coreObjectType);
			var channels = channelConfiguration == 7 ? 8 : channelConfiguration;
			var implicitSbr = sampleRate > 0 && sampleRate * 2 == containerSampleRate;
			if (sampleRate > track.SampleRate || track.SampleRate == 0) track.SampleRate = sampleRate;
			if (channels > 0)
			{
				if (implicitSbr || sbrObject) track.Channels = Math.Max(track.Channels, channels);
				else track.Channels = channels;
			}
			if (objectType == 36) track.CodecId = AudioCodecId.Mp4Als;
			else track.CodecId = AudioCodecId.Aac;
		}

		/// <summary>
		/// Counts single-channel and channel-pair elements from an AAC Program Config Element when channel_configuration is zero.
		/// </summary>
		private static int ReadProgramConfigChannels(ref AscBitReader bits, int objectType)
		{
			if (objectType != 1 && objectType != 2 && objectType != 3 && objectType != 4 && objectType != 6 && objectType != 7 && objectType != 17) return 0;
			bits.Read(1); if (bits.Read(1) != 0) bits.Read(14); bits.Read(1);
			bits.Read(4); bits.Read(2); bits.Read(4);
			var front = bits.Read(4); var side = bits.Read(4); var back = bits.Read(4); var lfe = bits.Read(2); var associated = bits.Read(3); var validCc = bits.Read(4);
			if (bits.Read(1) != 0) bits.Read(4); if (bits.Read(1) != 0) bits.Read(4); if (bits.Read(1) != 0) bits.Read(3);
			var channels = 0;
			for (var index = 0; index < front; index++) { channels += bits.Read(1) != 0 ? 2 : 1; bits.Read(4); }
			for (var index = 0; index < side; index++) { channels += bits.Read(1) != 0 ? 2 : 1; bits.Read(4); }
			for (var index = 0; index < back; index++) { channels += bits.Read(1) != 0 ? 2 : 1; bits.Read(4); }
			for (var index = 0; index < lfe; index++) { channels++; bits.Read(4); }
			for (var index = 0; index < associated; index++) bits.Read(4);
			for (var index = 0; index < validCc; index++) bits.Read(5);
			bits.Align(); var commentBytes = bits.Read(8); bits.Read(commentBytes * 8);
			return channels;
		}

		private int ReadAtomAsExtraData(MovAtom atom, MovTrack track)
		{
			var length = checked((int)(atom.End - atom.Start)); track.CodecExtraData = new byte[length];
			BinaryPrimitives.WriteUInt32BigEndian(track.CodecExtraData, checked((uint)length));
			BinaryPrimitives.WriteUInt32BigEndian(track.CodecExtraData.AsSpan(4), atom.Type);
			return _Reader.ReadExactly(track.CodecExtraData.AsSpan(8)) ? 0 : FfmpegError.EndOfFile;
		}

		private int ReadPayloadAsExtraData(MovAtom atom, MovTrack track)
		{
			var length = checked((int)(atom.End - atom.Payload)); track.CodecExtraData = new byte[length];
			return _Reader.ReadExactly(track.CodecExtraData) ? 0 : FfmpegError.EndOfFile;
		}

		private int ParseTimeToSample(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out _, out _) || !_Reader.ReadUInt32BigEndian(out var count) || count > (atom.End - _Reader.Position) / 8) return FfmpegError.InvalidData;
			track.Stts.Clear(); long total = 0;
			for (var index = 0U; index < count; index++)
			{
				if (!_Reader.ReadUInt32BigEndian(out var samples) || !_Reader.ReadUInt32BigEndian(out var duration)) return FfmpegError.EndOfFile;
				track.Stts.Add(new MovTimeEntry(samples, duration)); total = checked(total + samples * (long)duration);
			}
			track.SampleTableDuration = total;
			if (total > 0 && (track.Duration == 0 || total < track.Duration)) track.Duration = total;
			return 0;
		}

		private int ParseCompositionOffsets(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out var version, out _) || !_Reader.ReadUInt32BigEndian(out var count) || count > (atom.End - _Reader.Position) / 8) return FfmpegError.InvalidData;
			track.Ctts.Clear();
			for (var index = 0U; index < count; index++)
			{
				if (!_Reader.ReadUInt32BigEndian(out var samples) || !_Reader.ReadUInt32BigEndian(out var offsetBits)) return FfmpegError.EndOfFile;
				long offset = version == 1 ? unchecked((int)offsetBits) : offsetBits;
				if (samples != 0) track.Ctts.Add(new MovCompositionEntry(samples, offset));
				if (index + 2 < count && offset < 0) track.DecodeTimestampShift = Math.Max(track.DecodeTimestampShift, -(long)offset);
			}
			return 0;
		}

		private int ParseSampleToChunk(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out _, out _) || !_Reader.ReadUInt32BigEndian(out var count) || count > (atom.End - _Reader.Position) / 12) return FfmpegError.InvalidData;
			track.Stsc.Clear();
			for (var index = 0U; index < count; index++)
			{
				if (!_Reader.ReadUInt32BigEndian(out var first) || !_Reader.ReadUInt32BigEndian(out var samples) || !_Reader.ReadUInt32BigEndian(out var description)) return FfmpegError.EndOfFile;
				if (first == 0 || samples == 0 || description == 0) return FfmpegError.InvalidData;
				track.Stsc.Add(new MovSampleToChunk(first, samples, description));
			}
			return 0;
		}

		/// <summary>
		/// Reads fixed-width STSZ or packed 4/8/16-bit STZ2 sample sizes without changing their declared sample count.
		/// </summary>
		private int ParseSampleSizes(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out _, out _)) return FfmpegError.EndOfFile;
			uint fixedSize; int fieldSize;
			if (atom.Type == 0x7374737a)
			{
				if (!_Reader.ReadUInt32BigEndian(out fixedSize)) return FfmpegError.EndOfFile;
				fieldSize = 32;
			} else
			{
				if (!_Reader.Skip(3) || !_Reader.ReadByte(out var field)) return FfmpegError.EndOfFile;
				fixedSize = 0; fieldSize = field;
			}
			if (!_Reader.ReadUInt32BigEndian(out var count) || count > int.MaxValue) return FfmpegError.InvalidData;
			track.FixedSampleSize = fixedSize; track.SampleCount = count; track.SampleSizes = Array.Empty<uint>();
			if (fixedSize != 0) return 0;
			if (fieldSize != 4 && fieldSize != 8 && fieldSize != 16 && fieldSize != 32) return FfmpegError.InvalidData;
			track.SampleSizes = new uint[count];
			if (fieldSize == 32) for (var index = 0; index < track.SampleSizes.Length; index++) { if (!_Reader.ReadUInt32BigEndian(out track.SampleSizes[index])) return FfmpegError.EndOfFile; }
			else if (fieldSize == 16) for (var index = 0; index < track.SampleSizes.Length; index++) { if (!_Reader.ReadUInt16BigEndian(out var value)) return FfmpegError.EndOfFile; track.SampleSizes[index] = value; }
			else if (fieldSize == 8) for (var index = 0; index < track.SampleSizes.Length; index++) { if (!_Reader.ReadByte(out var value)) return FfmpegError.EndOfFile; track.SampleSizes[index] = value; }
			else for (var index = 0; index < track.SampleSizes.Length; index += 2) { if (!_Reader.ReadByte(out var value)) return FfmpegError.EndOfFile; track.SampleSizes[index] = (uint)(value >> 4); if (index + 1 < track.SampleSizes.Length) track.SampleSizes[index + 1] = (uint)(value & 15); }
			return 0;
		}

		private int ParseChunkOffsets(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out _, out _) || !_Reader.ReadUInt32BigEndian(out var count) || count > int.MaxValue) return FfmpegError.InvalidData;
			var elementSize = atom.Type == 0x7374636f ? 4 : 8;
			if (count > (atom.End - _Reader.Position) / elementSize) return FfmpegError.InvalidData;
			track.ChunkOffsets = new long[count];
			for (var index = 0; index < track.ChunkOffsets.Length; index++)
			{
				if (elementSize == 4) { if (!_Reader.ReadUInt32BigEndian(out var value)) return FfmpegError.EndOfFile; track.ChunkOffsets[index] = value; }
				else { if (!_Reader.ReadUInt64BigEndian(out var value) || value > long.MaxValue) return FfmpegError.InvalidData; track.ChunkOffsets[index] = (long)value; }
			}
			return 0;
		}

		private int ParseEditList(MovAtom atom, MovTrack track)
		{
			if (!ReadFullBox(out var version, out _) || version > 1 || !_Reader.ReadUInt32BigEndian(out var count)) return FfmpegError.InvalidData;
			var size = version == 1 ? 20 : 12;
			if (count > (atom.End - _Reader.Position) / size) return FfmpegError.InvalidData;
			track.Edits.Clear();
			for (var index = 0U; index < count; index++)
			{
				long duration; long mediaTime;
				if (version == 1)
				{
					if (!_Reader.ReadUInt64BigEndian(out var durationBits) || !_Reader.ReadUInt64BigEndian(out var timeBits)) return FfmpegError.EndOfFile;
					duration = durationBits > long.MaxValue ? -1 : (long)durationBits; mediaTime = unchecked((long)timeBits);
				} else
				{
					if (!_Reader.ReadUInt32BigEndian(out var durationBits) || !_Reader.ReadUInt32BigEndian(out var timeBits)) return FfmpegError.EndOfFile;
					duration = durationBits; mediaTime = unchecked((int)timeBits);
				}
				if (!_Reader.ReadUInt32BigEndian(out var rate) || duration < 0) return FfmpegError.InvalidData;
				track.Edits.Add(new MovEdit(duration, mediaTime, rate));
			}
			return 0;
		}

		private int ParseTrex(MovAtom atom)
		{
			if (!ReadFullBox(out _, out _) || !_Reader.ReadUInt32BigEndian(out var trackId) || !_Reader.ReadUInt32BigEndian(out var description) ||
				!_Reader.ReadUInt32BigEndian(out var duration) || !_Reader.ReadUInt32BigEndian(out var size) || !_Reader.ReadUInt32BigEndian(out var flags)) return FfmpegError.EndOfFile;
			_Trex.Add(new MovTrex(trackId, description, duration, size, flags)); return 0;
		}

		private int ParseTfhd(MovAtom atom, MovFragment fragment, MovFragmentTrack target)
		{
			if (!ReadFullBox(out _, out var flags) || !_Reader.ReadUInt32BigEndian(out target.TrackId)) return FfmpegError.EndOfFile;
			target.BaseDataOffset = fragment == null ? 0 : fragment.Position;
			if ((flags & 1) != 0) { if (!_Reader.ReadUInt64BigEndian(out var value) || value > long.MaxValue) return FfmpegError.InvalidData; target.BaseDataOffset = (long)value; }
			if ((flags & 2) != 0 && !_Reader.ReadUInt32BigEndian(out target.SampleDescriptionIndex)) return FfmpegError.EndOfFile;
			if ((flags & 8) != 0 && !_Reader.ReadUInt32BigEndian(out target.DefaultDuration)) return FfmpegError.EndOfFile;
			if ((flags & 16) != 0 && !_Reader.ReadUInt32BigEndian(out target.DefaultSize)) return FfmpegError.EndOfFile;
			if ((flags & 32) != 0 && !_Reader.ReadUInt32BigEndian(out target.DefaultFlags)) return FfmpegError.EndOfFile;
			return 0;
		}

		private int ParseTfdt(MovAtom atom, MovFragmentTrack target)
		{
			if (!ReadFullBox(out var version, out _) || version > 1) return FfmpegError.InvalidData;
			if (version == 1) { if (!_Reader.ReadUInt64BigEndian(out var value) || value > long.MaxValue) return FfmpegError.InvalidData; target.DecodeTime = (long)value; }
			else { if (!_Reader.ReadUInt32BigEndian(out var value)) return FfmpegError.EndOfFile; target.DecodeTime = value; }
			return 0;
		}

		/// <summary>
		/// Reads every optional TRUN field according to its flags while preserving signed version-1 composition offsets.
		/// </summary>
		private int ParseTrun(MovAtom atom, MovFragmentTrack target)
		{
			if (!ReadFullBox(out var version, out var flags) || !_Reader.ReadUInt32BigEndian(out var count) || count > int.MaxValue) return FfmpegError.InvalidData;
			var run = new MovRun { Version = version, Flags = flags, Samples = new MovRunSample[count] };
			if ((flags & 1) != 0) { if (!_Reader.ReadUInt32BigEndian(out var value)) return FfmpegError.EndOfFile; run.DataOffset = unchecked((int)value); run.HasDataOffset = true; }
			if ((flags & 4) != 0 && !_Reader.ReadUInt32BigEndian(out run.FirstSampleFlags)) return FfmpegError.EndOfFile;
			for (var index = 0; index < run.Samples.Length; index++)
			{
				ref var sample = ref run.Samples[index];
				if ((flags & 0x100) != 0 && !_Reader.ReadUInt32BigEndian(out sample.Duration)) return FfmpegError.EndOfFile;
				if ((flags & 0x200) != 0 && !_Reader.ReadUInt32BigEndian(out sample.Size)) return FfmpegError.EndOfFile;
				if ((flags & 0x400) != 0 && !_Reader.ReadUInt32BigEndian(out sample.Flags)) return FfmpegError.EndOfFile;
				if ((flags & 0x800) != 0) { if (!_Reader.ReadUInt32BigEndian(out var value)) return FfmpegError.EndOfFile; sample.CompositionOffset = version == 1 ? unchecked((int)value) : value; }
			}
			target.Runs.Add(run); return 0;
		}

		/// <summary>
		/// Materializes TFHD/TREX defaults and TRUN overrides into contiguous fragment packet positions and timestamps.
		/// </summary>
		private int BuildFragmentIndex(MovFragment fragment, MovFragmentTrack source)
		{
			if (fragment == null) return FfmpegError.InvalidData;
			MovTrack track = null;
			for (var index = 0; index < _Tracks.Count; index++) if (_Tracks[index].TrackId == source.TrackId) { track = _Tracks[index]; break; }
			if (track == null || track.HandlerType != 0x736f756e) return 0;
			MovTrex trex = null;
			for (var index = 0; index < _Trex.Count; index++) if (_Trex[index].TrackId == source.TrackId) { trex = _Trex[index]; break; }
			var defaultDuration = source.DefaultDuration != 0 ? source.DefaultDuration : trex == null ? 0 : trex.Duration;
			var defaultSize = source.DefaultSize != 0 ? source.DefaultSize : trex == null ? 0 : trex.Size;
			var dts = source.DecodeTime; var position = source.BaseDataOffset;
			for (var runIndex = 0; runIndex < source.Runs.Count; runIndex++)
			{
				var run = source.Runs[runIndex]; if (run.HasDataOffset) position = checked(source.BaseDataOffset + run.DataOffset);
				for (var sampleIndex = 0; sampleIndex < run.Samples.Length; sampleIndex++)
				{
					ref var sample = ref run.Samples[sampleIndex]; var duration = sample.Duration != 0 ? sample.Duration : defaultDuration; var size = sample.Size != 0 ? sample.Size : defaultSize;
					if (size > int.MaxValue) return FfmpegError.InvalidData;
					track.FragmentPackets.Add(new MovPacket(position, checked((int)size), dts + sample.CompositionOffset, dts, duration));
					position = checked(position + size); dts = checked(dts + duration);
				}
			}
			return 0;
		}

		/// <summary>
		/// Recreates FFmpeg's compressed-sample path and its special one-tick audio chunk grouping path.
		/// </summary>
		private int BuildClassicIndex(MovTrack track)
		{
			track.Packets.Clear();
			if (track.ChunkOffsets.Length == 0 || track.Stsc.Count == 0) return FfmpegError.InvalidData;
			var oneTickAudio = track.Stts.Count == 1 && track.Stts[0].Duration == 1;
			if (oneTickAudio)
			{
				var result = BuildOneTickAudioIndex(track); if (result < 0) return result;
			} else
			{
				var durations = ExpandDurations(track); var offsets = ExpandCompositionOffsets(track);
				if (track.SampleCount == 0 || durations.Length < track.SampleCount) return FfmpegError.InvalidData;
				var sampleIndex = 0; var stscIndex = 0; long dts = -track.DecodeTimestampShift;
				for (var chunkIndex = 0; chunkIndex < track.ChunkOffsets.Length && sampleIndex < track.SampleCount; chunkIndex++)
				{
					while (stscIndex + 1 < track.Stsc.Count && chunkIndex + 1 == track.Stsc[stscIndex + 1].FirstChunk) stscIndex++;
					var position = track.ChunkOffsets[chunkIndex]; var count = track.Stsc[stscIndex].SamplesPerChunk;
					for (var chunkSample = 0U; chunkSample < count && sampleIndex < track.SampleCount; chunkSample++, sampleIndex++)
					{
						var size = track.FixedSampleSize != 0 ? track.FixedSampleSize : track.SampleSizes[sampleIndex];
						if (size > int.MaxValue) return FfmpegError.InvalidData;
						var compositionOffset = sampleIndex < offsets.Length ? offsets[sampleIndex] : 0;
						track.Packets.Add(new MovPacket(position, checked((int)size), dts + track.DecodeTimestampShift + compositionOffset, dts, durations[sampleIndex]));
						position = checked(position + size); dts = checked(dts + durations[sampleIndex]);
					}
				}
			}
			ApplyEditList(track);
			if (oneTickAudio && track.Packets.Count != 0)
			{
				for (var index = 0; index + 1 < track.Packets.Count; index++)
				{
					var current = track.Packets[index]; current.Duration = track.Packets[index + 1].DecodeTimestamp - current.DecodeTimestamp; track.Packets[index] = current;
				}
				var last = track.Packets[^1]; if (track.Duration >= last.DecodeTimestamp) last.Duration = track.Duration - last.DecodeTimestamp; track.Packets[^1] = last;
			}
			return track.Packets.Count == 0 ? FfmpegError.InvalidData : 0;
		}

		private int BuildOneTickAudioIndex(MovTrack track)
		{
			var stscIndex = 0; long dts = 0;
			for (var chunkIndex = 0; chunkIndex < track.ChunkOffsets.Length; chunkIndex++)
			{
				while (stscIndex + 1 < track.Stsc.Count && chunkIndex + 1 == track.Stsc[stscIndex + 1].FirstChunk) stscIndex++;
				var remaining = track.Stsc[stscIndex].SamplesPerChunk; var position = track.ChunkOffsets[chunkIndex];
				while (remaining > 0)
				{
					uint samples; uint size;
					if (track.SamplesPerFrame >= 160) { samples = track.SamplesPerFrame; size = track.BytesPerFrame; }
					else if (track.SamplesPerFrame > 1)
					{
						samples = Math.Min((1024 / track.SamplesPerFrame) * track.SamplesPerFrame, remaining);
						size = checked(samples / track.SamplesPerFrame * track.BytesPerFrame);
					} else { samples = Math.Min(1024U, remaining); size = checked(samples * (uint)track.SampleSize); }
					if (size == 0 || size > int.MaxValue || samples > remaining) return FfmpegError.InvalidData;
					track.Packets.Add(new MovPacket(position, checked((int)size), dts, dts, samples));
					position = checked(position + size); dts = checked(dts + samples); remaining -= samples;
				}
			}
			return 0;
		}

		/// <summary>
		/// Applies the common audio edit-list mapping used by FFmpeg, including preroll timestamps and track-duration clipping.
		/// </summary>
		private void ApplyEditList(MovTrack track)
		{
			if (track.Edits.Count == 0) return;
			var mapped = new List<MovPacket>(); long outputOffset = 0; long totalEditDuration = 0; var firstNonEmpty = true;
			for (var editIndex = 0; editIndex < track.Edits.Count; editIndex++)
			{
				var edit = track.Edits[editIndex]; var editDuration = Rescale(edit.Duration, track.TimeScale, _MovieTimeScale == 0 ? 1 : _MovieTimeScale);
				totalEditDuration = checked(totalEditDuration + editDuration);
				if (edit.MediaTime == -1) { outputOffset = checked(outputOffset + editDuration); continue; }
				var search = Math.Max(edit.MediaTime - track.TimeScale, track.Packets[0].PresentationTimestamp); var startIndex = 0;
				for (var index = 0; index < track.Packets.Count && track.Packets[index].PresentationTimestamp <= search; index++) startIndex = index;
				var editEnd = checked(edit.MediaTime + editDuration);
				for (var index = startIndex; index < track.Packets.Count; index++)
				{
					var source = track.Packets[index]; var rawPts = source.PresentationTimestamp;
					source.DecodeTimestamp = checked(source.DecodeTimestamp - edit.MediaTime + outputOffset);
					source.PresentationTimestamp = checked(source.PresentationTimestamp - edit.MediaTime + outputOffset);
					mapped.Add(source);
					if (rawPts + source.Duration >= editEnd) break;
				}
				if (firstNonEmpty && edit.MediaTime > 0) track.StartSkipSamples = checked((int)Math.Min(edit.MediaTime, int.MaxValue));
				firstNonEmpty = false; outputOffset = checked(outputOffset + editDuration);
			}
			track.Packets.Clear(); track.Packets.AddRange(mapped);
			if (track.Duration == 0 || totalEditDuration < track.Duration) track.Duration = totalEditDuration;
		}

		private uint[] ExpandDurations(MovTrack track)
		{
			var result = new uint[track.SampleCount]; var offset = 0;
			for (var entryIndex = 0; entryIndex < track.Stts.Count && offset < result.Length; entryIndex++)
				for (var sample = 0U; sample < track.Stts[entryIndex].Count && offset < result.Length; sample++) result[offset++] = track.Stts[entryIndex].Duration;
			return result;
		}

		private long[] ExpandCompositionOffsets(MovTrack track)
		{
			if (track.Ctts.Count == 0) return Array.Empty<long>();
			var result = new long[track.SampleCount]; var offset = 0;
			for (var entryIndex = 0; entryIndex < track.Ctts.Count && offset < result.Length; entryIndex++)
				for (var sample = 0U; sample < track.Ctts[entryIndex].Count && offset < result.Length; sample++) result[offset++] = track.Ctts[entryIndex].Offset;
			return result;
		}

		private void SelectAudioTrack()
		{
			if (_SelectedTrack != null) return;
			for (var index = 0; index < _Tracks.Count; index++) if (_Tracks[index].HandlerType == 0x736f756e) { _SelectedTrack = _Tracks[index]; return; }
		}

		private bool ReadFullBox(out byte version, out int flags)
		{
			flags = 0;
			if (!_Reader.ReadByte(out version) || !_Reader.ReadByte(out var first) || !_Reader.ReadByte(out var second) || !_Reader.ReadByte(out var third)) return false;
			flags = (first << 16) | (second << 8) | third; return true;
		}

		private static void FinalizeAudioEntry(MovTrack track)
		{
			if (track.CodecId == AudioCodecId.Qcelp) { track.Channels = 1; track.SamplesPerFrame = 160; if (track.BytesPerFrame == 0) track.BytesPerFrame = 35; }
			if (track.CodecId == AudioCodecId.PcmF32BigEndian || track.CodecId == AudioCodecId.PcmF32LittleEndian) track.BitsPerCodedSample = 32;
			else if (track.CodecId == AudioCodecId.PcmF64BigEndian || track.CodecId == AudioCodecId.PcmF64LittleEndian) track.BitsPerCodedSample = 64;
			if (track.CodecId == AudioCodecId.PcmALaw || track.CodecId == AudioCodecId.PcmMuLaw) { track.BitsPerCodedSample = 8; track.SampleSize = track.Channels; }
			else if (IsPcm(track.CodecId)) track.SampleSize = checked(((track.BitsPerCodedSample + 7) / 8) * track.Channels);
		}

		private static AudioCodecId MapCodec(uint tag, int bits)
		{
			switch (tag)
			{
				case 0x6d703461: return AudioCodecId.Aac; // mp4a
				case 0x616c6163: return AudioCodecId.Alac; // alac
				case 0x61632d34: return AudioCodecId.Ac4; // ac-4
				case 0x6d686d31: return AudioCodecId.MpegH3dAudio; // mhm1
				case 0x51444d32: return AudioCodecId.Qdm2; // QDM2
				case 0x51444d43: return AudioCodecId.Qdmc; // QDMC
				case 0x51636c70: return AudioCodecId.Qcelp; // Qclp
				case 0x616c6177: return AudioCodecId.PcmALaw; // alaw
				case 0x756c6177: return AudioCodecId.PcmMuLaw; // ulaw
				case 0x666c3332: return AudioCodecId.PcmF32BigEndian; // fl32
				case 0x666c3634: return AudioCodecId.PcmF64BigEndian; // fl64
				case 0x736f7774: return bits == 8 ? AudioCodecId.PcmS8 : bits == 24 ? AudioCodecId.PcmS24LittleEndian : bits == 32 ? AudioCodecId.PcmS32LittleEndian : AudioCodecId.PcmS16LittleEndian; // sowt
				case 0x74776f73: return bits == 8 ? AudioCodecId.PcmS8 : bits == 24 ? AudioCodecId.PcmS24BigEndian : bits == 32 ? AudioCodecId.PcmS32BigEndian : AudioCodecId.PcmS16BigEndian; // twos
				case 0x72617720: return bits == 8 ? AudioCodecId.PcmU8 : AudioCodecId.PcmS16BigEndian; // raw 
				case 0x4e4f4e45: return bits == 8 ? AudioCodecId.PcmU8 : AudioCodecId.PcmS16BigEndian; // NONE
				case 0x696e3234: return AudioCodecId.PcmS24BigEndian; // in24
				case 0x696e3332: return AudioCodecId.PcmS32BigEndian; // in32
				default: return AudioCodecId.None;
			}
		}

		private static AudioCodecId MapLinearPcm(int bits, uint flags)
		{
			var isFloat = (flags & 1) != 0; var bigEndian = (flags & 2) != 0; var signed = (flags & 4) == 0 || bits > 8;
			if (isFloat) return bits == 64 ? (bigEndian ? AudioCodecId.PcmF64BigEndian : AudioCodecId.PcmF64LittleEndian) : (bigEndian ? AudioCodecId.PcmF32BigEndian : AudioCodecId.PcmF32LittleEndian);
			if (bits <= 8) return signed ? AudioCodecId.PcmS8 : AudioCodecId.PcmU8;
			if (bits <= 16) return bigEndian ? AudioCodecId.PcmS16BigEndian : AudioCodecId.PcmS16LittleEndian;
			if (bits <= 24) return bigEndian ? AudioCodecId.PcmS24BigEndian : AudioCodecId.PcmS24LittleEndian;
			return bigEndian ? AudioCodecId.PcmS32BigEndian : AudioCodecId.PcmS32LittleEndian;
		}

		private static bool IsPcm(AudioCodecId codecId)
		{
			return codecId >= AudioCodecId.PcmS16LittleEndian && codecId <= AudioCodecId.PcmSga;
		}

		private static long Rescale(long value, long numerator, long denominator)
		{
			if (denominator == 0) return 0;
			return checked((value * numerator + denominator / 2) / denominator);
		}

		private readonly struct MovAtom
		{
			public long Start { get; }
			public long Payload { get; }
			public long End { get; }
			public uint Type { get; }
			public MovAtom(long start, long payload, long end, uint type) { Start = start; Payload = payload; End = end; Type = type; }
		}

		/// <summary>Holds one track's audio parameters, sample tables, edits, and materialized packet indexes.</summary>
		private sealed class MovTrack
		{
			public int StreamIndex; public uint TrackId; public uint HandlerType; public uint TimeScale; public long Duration; public long SampleTableDuration;
			public AudioCodecId CodecId; public uint CodecTagBigEndian; public int SampleRate; public int Channels; public int BitsPerCodedSample;
			public int SampleSize; public uint SamplesPerFrame; public uint BytesPerFrame; public long BitRate; public byte[] CodecExtraData;
			public uint FixedSampleSize; public uint SampleCount; public uint[] SampleSizes = Array.Empty<uint>(); public long[] ChunkOffsets = Array.Empty<long>();
			public long DecodeTimestampShift; public int StartSkipSamples;
			public List<MovTimeEntry> Stts { get; } = new List<MovTimeEntry>(); public List<MovCompositionEntry> Ctts { get; } = new List<MovCompositionEntry>();
			public List<MovSampleToChunk> Stsc { get; } = new List<MovSampleToChunk>(); public List<MovEdit> Edits { get; } = new List<MovEdit>();
			public List<MovPacket> Packets { get; } = new List<MovPacket>(); public List<MovPacket> FragmentPackets { get; } = new List<MovPacket>();
		}

		private readonly struct MovTimeEntry { public uint Count { get; } public uint Duration { get; } public MovTimeEntry(uint count, uint duration) { Count = count; Duration = duration; } }
		private readonly struct MovCompositionEntry { public uint Count { get; } public long Offset { get; } public MovCompositionEntry(uint count, long offset) { Count = count; Offset = offset; } }
		private readonly struct MovSampleToChunk { public uint FirstChunk { get; } public uint SamplesPerChunk { get; } public uint Description { get; } public MovSampleToChunk(uint first, uint samples, uint description) { FirstChunk = first; SamplesPerChunk = samples; Description = description; } }
		private readonly struct MovEdit { public long Duration { get; } public long MediaTime { get; } public uint Rate { get; } public MovEdit(long duration, long mediaTime, uint rate) { Duration = duration; MediaTime = mediaTime; Rate = rate; } }
		/// <summary>Stores the default sample values declared by one MVEX/TREX track.</summary>
		private sealed class MovTrex { public uint TrackId { get; } public uint Description { get; } public uint Duration { get; } public uint Size { get; } public uint Flags { get; } public MovTrex(uint trackId, uint description, uint duration, uint size, uint flags) { TrackId = trackId; Description = description; Duration = duration; Size = size; Flags = flags; } }
		/// <summary>Identifies the base position of one MOOF fragment.</summary>
		private sealed class MovFragment { public long Position; }
		/// <summary>Collects TFHD, TFDT, and TRUN state for one fragment track.</summary>
		private sealed class MovFragmentTrack { public uint TrackId; public long BaseDataOffset; public uint SampleDescriptionIndex; public uint DefaultDuration; public uint DefaultSize; public uint DefaultFlags; public long DecodeTime; public List<MovRun> Runs { get; } = new List<MovRun>(); }
		/// <summary>Stores one TRUN's optional fields and per-sample overrides.</summary>
		private sealed class MovRun { public byte Version; public int Flags; public bool HasDataOffset; public int DataOffset; public uint FirstSampleFlags; public MovRunSample[] Samples; }
		private struct MovRunSample { public uint Duration; public uint Size; public uint Flags; public long CompositionOffset; }
		private struct MovPacket { public long Position; public int Size; public long PresentationTimestamp; public long DecodeTimestamp; public long Duration; public long CumulativeTimestamp; public MovPacket(long position, int size, long pts, long dts, long duration) { Position = position; Size = size; PresentationTimestamp = pts; DecodeTimestamp = dts; Duration = duration; CumulativeTimestamp = 0; } }

		private struct AscBitReader
		{
			private static readonly int[] SampleRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
			private readonly byte[] _Data; private int _BitPosition;
			public static int SampleRateCount => SampleRates.Length;
			public int BitPosition => _BitPosition;
			public AscBitReader(byte[] data) { _Data = data; _BitPosition = 0; }
			public int Read(int count) { var value = 0; for (var index = 0; index < count; index++) { value <<= 1; if (_BitPosition < _Data.Length * 8) value |= (_Data[_BitPosition >> 3] >> (7 - (_BitPosition & 7))) & 1; _BitPosition++; } return value; }
			public void Align() { _BitPosition = (_BitPosition + 7) & ~7; }
			public int ReadObjectType() { var value = Read(5); return value == 31 ? 32 + Read(6) : value; }
			public int ReadSampleRate() { var index = Read(4); return index == 15 ? Read(24) : index < SampleRates.Length ? SampleRates[index] : 0; }
			public static int GetSampleRate(int index) { return index >= 0 && index < SampleRates.Length ? SampleRates[index] : 0; }
			public static int ReadAt(byte[] data, int position, int count) { var value = 0; for (var index = 0; index < count; index++) { value <<= 1; var bit = position + index; if (bit < data.Length * 8) value |= (data[bit >> 3] >> (7 - (bit & 7))) & 1; } return value; }
		}
	}
}
