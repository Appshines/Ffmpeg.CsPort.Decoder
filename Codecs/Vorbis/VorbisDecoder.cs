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
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Transforms;

namespace Ffmpeg.CsPort.Decoder.Codecs.Vorbis
{
	/// <summary>
	/// Ports FFmpeg's scalar Vorbis setup parser and float decoder, including floors, residues, coupling, IMDCT, and overlap.
	/// </summary>
	public sealed class VorbisDecoder
	{
		private const int MaximumVlcs = 1 << 16;
		private const int MaximumPartitions = 1 << 20;

		private readonly BitReader reader = new BitReader();
		private int channels;
		private int sampleRate;
		private readonly int[] blockSizes = new int[2];
		private readonly float[][] windows = new float[2][];
		private VorbisCodebook[] codebooks;
		private VorbisFloor[] floors;
		private VorbisResidue[] residues;
		private VorbisMapping[] mappings;
		private VorbisMode[] modes;
		private FfmpegFloatMdct[] mdct = new FfmpegFloatMdct[2];
		private float[] channelResidues;
		private float[] saved;
		private float[][] outputPlanes;
		private float[][] floorByCodecChannel;
		private byte[] noResidue;
		private byte[] doNotDecode;
		private byte[] residueChannel;
		private ushort[] floorY;
		private ushort[] floorYFinal;
		private int[] floorFlags;
		private int modeNumber;
		private int previousWindow = -1;
		private bool firstFrame;

		public int Channels => channels;

		public int SampleRate => sampleRate;

		private VorbisDecoder()
		{
		}

		/// <summary>
		/// Splits the Xiph extradata and parses FFmpeg's identification and setup state without retaining packet-time allocations.
		/// </summary>
		public static int Initialize(byte[] extraData, out VorbisDecoder decoder)
		{
			decoder = null;
			if (extraData == null || !SplitXiphHeaders(extraData, out var identification, out _, out var setup))
				return FfmpegError.InvalidData;

			var result = new VorbisDecoder();
			if (result.reader.InitializeBytes(identification, identification.Length, true) < 0 || result.reader.ReadBits(8) != 1)
				return FfmpegError.InvalidData;
			var parseResult = result.ParseIdentificationHeader();
			if (parseResult < 0)
				return parseResult;
			if (result.reader.InitializeBytes(setup, setup.Length, true) < 0 || result.reader.ReadBits(8) != 5)
				return FfmpegError.InvalidData;
			parseResult = result.ParseSetupHeader();
			if (parseResult < 0)
				return parseResult;
			result.InitializeDecodeBuffers();
			decoder = result;
			return 0;
		}

		/// <summary>
		/// Decodes one audio packet to FFmpeg-ordered planar float samples and returns the consumed packet byte count.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength <= 0 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			if (reader.Initialize(packet, packetOffset, packetLength * 8, true) < 0)
				return FfmpegError.InvalidData;
			var length = ParseAudioPacket();
			if (length <= 0)
				return length;
			var planeSize = checked(length * 4);
			var dataSize = checked(planeSize * channels);
			if (output.Length < dataSize)
				return FfmpegError.InvalidArgument;
			for (var channel = 0; channel < channels; channel++)
			{
				var plane = outputPlanes[channel];
				var destinationOffset = channel * planeSize;
				for (var sample = 0; sample < length; sample++)
				{
					BinaryPrimitives.WriteInt32LittleEndian(
						output.Slice(destinationOffset + sample * 4, 4),
						BitConverter.SingleToInt32Bits(plane[sample]));
				}
			}
			if (!firstFrame)
				firstFrame = true;
			frame = new AudioFrameInfo(length, channels, AudioSampleFormat.FloatPlanar, channels, planeSize, dataSize);
			return packetLength;
		}

		public void Flush()
		{
			Array.Clear(saved, 0, saved.Length);
			previousWindow = -1;
			firstFrame = false;
		}

		private int ParseIdentificationHeader()
		{
			if (!ReadSignature())
				return FfmpegError.InvalidData;
			var version = reader.ReadBitsLong(32);
			if (version != 0)
				return FfmpegError.InvalidData;
			channels = (int)reader.ReadBits(8);
			if (channels <= 0 || channels > 255)
				return FfmpegError.InvalidData;
			sampleRate = unchecked((int)reader.ReadBitsLong(32));
			if (sampleRate <= 0)
				return FfmpegError.InvalidData;
			reader.ReadBitsLong(32);
			reader.ReadBitsLong(32);
			reader.ReadBitsLong(32);
			var smallExponent = (int)reader.ReadBits(4);
			var largeExponent = (int)reader.ReadBits(4);
			if (smallExponent < 6 || smallExponent > 13 || largeExponent < 6 || largeExponent > 13 || largeExponent < smallExponent)
				return FfmpegError.InvalidData;
			blockSizes[0] = 1 << smallExponent;
			blockSizes[1] = 1 << largeExponent;
			windows[0] = VorbisTables.GetWindow(smallExponent);
			windows[1] = VorbisTables.GetWindow(largeExponent);
			if (reader.ReadBit() == 0)
				return FfmpegError.InvalidData;
			mdct[0] = new FfmpegFloatMdct(blockSizes[0] >> 1, true, -1.0f);
			mdct[1] = new FfmpegFloatMdct(blockSizes[1] >> 1, true, -1.0f);
			return 0;
		}

		private int ParseSetupHeader()
		{
			if (!ReadSignature())
				return FfmpegError.InvalidData;
			var result = ParseCodebooks();
			if (result < 0)
				return result;
			result = ParseTimeDomainTransforms();
			if (result < 0)
				return result;
			result = ParseFloors();
			if (result < 0)
				return result;
			result = ParseResidues();
			if (result < 0)
				return result;
			result = ParseMappings();
			if (result < 0)
				return result;
			result = ParseModes();
			if (result < 0)
				return result;
			return reader.ReadBit() != 0 ? 0 : FfmpegError.InvalidData;
		}

		/// <summary>
		/// Parses ordered or sparse code lengths, lookup-one vectors, canonical Vorbis codes, and FFmpeg little-endian VLC tables.
		/// </summary>
		private int ParseCodebooks()
		{
			var codebookCount = (int)reader.ReadBits(8) + 1;
			codebooks = new VorbisCodebook[codebookCount];
			var temporaryLengths = new byte[MaximumVlcs];
			var temporaryCodes = new uint[MaximumVlcs];
			var multiplicands = new ushort[MaximumVlcs];
			for (var codebookIndex = 0; codebookIndex < codebookCount; codebookIndex++)
			{
				var codebook = new VorbisCodebook();
				codebooks[codebookIndex] = codebook;
				Array.Clear(temporaryLengths, 0, temporaryLengths.Length);
				if (reader.ReadBits(24) != 0x564342)
					return FfmpegError.InvalidData;
				codebook.Dimensions = (int)reader.ReadBits(16);
				if (codebook.Dimensions <= 0 || codebook.Dimensions > 16)
					return FfmpegError.InvalidData;
				var entries = (int)reader.ReadBits(24);
				if (entries < 0 || entries > MaximumVlcs)
					return FfmpegError.InvalidData;
				var usedEntries = 0;
				if (reader.ReadBit() == 0)
				{
					var sparse = reader.ReadBit() != 0;
					for (var entry = 0; entry < entries; entry++)
					{
						if (!sparse || reader.ReadBit() != 0)
						{
							temporaryLengths[entry] = (byte)(reader.ReadBits(5) + 1);
							usedEntries++;
						}
					}
				} else
				{
					var currentEntry = 0;
					var currentLength = (int)reader.ReadBits(5) + 1;
					usedEntries = entries;
					while (currentEntry < usedEntries && currentLength <= 32)
					{
						var number = (int)reader.ReadBitsOrZero(IntegerLog(entries - currentEntry));
						for (var index = currentEntry; index < number + currentEntry && index < usedEntries; index++)
							temporaryLengths[index] = (byte)currentLength;
						currentEntry += number;
						currentLength++;
					}
					if (currentEntry > usedEntries)
						return FfmpegError.InvalidData;
				}

				codebook.LookupType = (int)reader.ReadBits(4);
				if (codebook.LookupType == 1)
				{
					var lookupValues = (int)NthRoot((uint)entries, (uint)codebook.Dimensions);
					var minimum = VorbisFloatToFloat(reader.ReadBitsLong(32));
					var delta = VorbisFloatToFloat(reader.ReadBitsLong(32));
					var valueBits = (int)reader.ReadBits(4) + 1;
					var sequence = reader.ReadBit() != 0;
					if (!float.IsFinite(minimum) || !float.IsFinite(delta))
						return FfmpegError.InvalidData;
					for (var index = 0; index < lookupValues; index++)
						multiplicands[index] = (ushort)reader.ReadBits(valueBits);
					codebook.Codevectors = usedEntries == 0 ? null : new float[usedEntries * codebook.Dimensions];
					var usedIndex = 0;
					for (var entry = 0; entry < entries; entry++)
					{
						if (temporaryLengths[entry] == 0)
							continue;
						var last = 0.0f;
						var lookupOffset = entry;
						for (var dimension = 0; dimension < codebook.Dimensions; dimension++)
						{
							var multiplicandOffset = lookupOffset % lookupValues;
							var value = (float)multiplicands[multiplicandOffset] * delta + minimum + last;
							codebook.Codevectors[usedIndex * codebook.Dimensions + dimension] = value;
							if (sequence)
								last = value;
							lookupOffset /= lookupValues;
						}
						temporaryLengths[usedIndex] = temporaryLengths[entry];
						usedIndex++;
					}
					if (usedIndex != usedEntries)
						return FfmpegError.InvalidData;
					entries = usedEntries;
				} else if (codebook.LookupType >= 2)
				{
					return FfmpegError.InvalidData;
				}

				var lengths = new byte[entries];
				var codes = new uint[entries];
				Array.Copy(temporaryLengths, lengths, entries);
				if (LengthsToCodes(lengths, temporaryCodes, entries) < 0)
					return FfmpegError.InvalidData;
				Array.Copy(temporaryCodes, codes, entries);
				var maximumLength = 0;
				for (var entry = 0; entry < entries; entry++)
					maximumLength = Math.Max(maximumLength, lengths[entry]);
				codebook.RootBits = maximumLength > 24 ? 11 : 8;
				codebook.MaximumDepth = (maximumLength + codebook.RootBits - 1) / codebook.RootBits;
				codebook.Vlc = new Vlc();
				var vlcResult = codebook.Vlc.InitializeSparse(codebook.RootBits, lengths, codes, null, VlcFlags.LittleEndian);
				if (vlcResult < 0)
					return vlcResult;
			}
			return 0;
		}

		private int ParseTimeDomainTransforms()
		{
			var count = (int)reader.ReadBits(6) + 1;
			for (var index = 0; index < count; index++)
			{
				if (reader.ReadBits(16) != 0)
					return FfmpegError.InvalidData;
			}
			return 0;
		}

		/// <summary>
		/// Parses floor-1 prediction lists and floor-0 bark maps exactly once during setup.
		/// </summary>
		private int ParseFloors()
		{
			floors = new VorbisFloor[(int)reader.ReadBits(6) + 1];
			for (var floorIndex = 0; floorIndex < floors.Length; floorIndex++)
			{
				var floor = new VorbisFloor { Type = (int)reader.ReadBits(16) };
				floors[floorIndex] = floor;
				if (floor.Type == 1)
				{
					var value = new VorbisFloor1();
					floor.Floor1 = value;
					var maximumClass = -1;
					var floorValues = 2;
					value.Partitions = (int)reader.ReadBits(5);
					for (var partition = 0; partition < value.Partitions; partition++)
					{
						value.PartitionClass[partition] = (int)reader.ReadBits(4);
						maximumClass = Math.Max(maximumClass, value.PartitionClass[partition]);
					}
					for (var classIndex = 0; classIndex <= maximumClass; classIndex++)
					{
						value.ClassDimensions[classIndex] = (int)reader.ReadBits(3) + 1;
						value.ClassSubclasses[classIndex] = (int)reader.ReadBits(2);
						if (value.ClassSubclasses[classIndex] != 0)
						{
							value.ClassMasterbook[classIndex] = (int)reader.ReadBits(8);
							if (value.ClassMasterbook[classIndex] >= codebooks.Length)
								return FfmpegError.InvalidData;
						}
						for (var subclass = 0; subclass < 1 << value.ClassSubclasses[classIndex]; subclass++)
						{
							var book = (int)reader.ReadBits(8) - 1;
							if (book >= codebooks.Length)
								return FfmpegError.InvalidData;
							value.SubclassBooks[classIndex, subclass] = book;
						}
					}
					value.Multiplier = (int)reader.ReadBits(2) + 1;
					for (var partition = 0; partition < value.Partitions; partition++)
						floorValues += value.ClassDimensions[value.PartitionClass[partition]];
					value.List = new VorbisFloor1Entry[floorValues];
					var rangeBits = (int)reader.ReadBits(4);
					if (rangeBits == 0 && value.Partitions != 0)
						return FfmpegError.InvalidData;
					var rangeMaximum = 1 << rangeBits;
					if (rangeMaximum > blockSizes[1] / 2)
						return FfmpegError.InvalidData;
					value.List[0].X = 0;
					value.List[1].X = rangeMaximum;
					var coordinate = 2;
					for (var partition = 0; partition < value.Partitions; partition++)
					{
						var dimensions = value.ClassDimensions[value.PartitionClass[partition]];
						for (var dimension = 0; dimension < dimensions; dimension++)
							value.List[coordinate++].X = (int)reader.ReadBitsOrZero(rangeBits);
					}
					if (PrepareFloor1List(value.List) < 0)
						return FfmpegError.InvalidData;
				} else if (floor.Type == 0)
				{
					var value = new VorbisFloor0();
					floor.Floor0 = value;
					value.Order = (int)reader.ReadBits(8);
					value.Rate = (int)reader.ReadBits(16);
					value.BarkMapSize = (int)reader.ReadBits(16);
					if (value.Order == 0 || value.Rate == 0 || value.BarkMapSize == 0)
						return FfmpegError.InvalidData;
					value.AmplitudeBits = (int)reader.ReadBits(6);
					value.AmplitudeOffset = (int)reader.ReadBits(8);
					value.BookList = new int[(int)reader.ReadBits(4) + 1];
					var maximumDimension = 0;
					for (var book = 0; book < value.BookList.Length; book++)
					{
						value.BookList[book] = (int)reader.ReadBits(8);
						if (value.BookList[book] >= codebooks.Length)
							return FfmpegError.InvalidData;
						maximumDimension = Math.Max(maximumDimension, codebooks[value.BookList[book]].Dimensions);
					}
					CreateFloor0Maps(value);
					value.Lsp = new float[value.Order + 1 + maximumDimension];
				} else
				{
					return FfmpegError.InvalidData;
				}
			}
			return 0;
		}

		/// <summary>
		/// Parses residue ranges, cascades, codebook selections, and the reusable classification storage used by all packet passes.
		/// </summary>
		private int ParseResidues()
		{
			residues = new VorbisResidue[(int)reader.ReadBits(6) + 1];
			Span<byte> cascade = stackalloc byte[64];
			for (var residueIndex = 0; residueIndex < residues.Length; residueIndex++)
			{
				var residue = new VorbisResidue();
				residues[residueIndex] = residue;
				residue.Type = (int)reader.ReadBits(16);
				residue.Begin = (int)reader.ReadBits(24);
				residue.End = (int)reader.ReadBits(24);
				residue.PartitionSize = (int)reader.ReadBits(24) + 1;
				if (residue.Begin > residue.End || (residue.End - residue.Begin) / residue.PartitionSize > Math.Min(MaximumPartitions, 65535))
					return FfmpegError.InvalidData;
				residue.Classifications = (int)reader.ReadBits(6) + 1;
				residue.Classbook = (int)reader.ReadBits(8);
				if (residue.Classbook >= codebooks.Length)
					return FfmpegError.InvalidData;
				residue.PartitionsToRead = (residue.End - residue.Begin) / residue.PartitionSize;
				residue.Classifs = new byte[residue.PartitionsToRead * channels];
				for (var classification = 0; classification < residue.Classifications; classification++)
				{
					var lowBits = (int)reader.ReadBits(3);
					var highBits = reader.ReadBit() != 0 ? (int)reader.ReadBits(5) : 0;
					cascade[classification] = (byte)(highBits << 3 | lowBits);
				}
				for (var classification = 0; classification < residue.Classifications; classification++)
				{
					for (var pass = 0; pass < 8; pass++)
					{
						if ((cascade[classification] & 1 << pass) != 0)
						{
							var book = (int)reader.ReadBits(8);
							if (book >= codebooks.Length)
								return FfmpegError.InvalidData;
							residue.Books[classification, pass] = book;
							residue.MaximumPass = Math.Max(residue.MaximumPass, pass);
						} else
						{
							residue.Books[classification, pass] = -1;
						}
					}
				}
			}
			return 0;
		}

		/// <summary>
		/// Parses mapping-zero submaps and preserves the original channel-order coupling indices.
		/// </summary>
		private int ParseMappings()
		{
			mappings = new VorbisMapping[(int)reader.ReadBits(6) + 1];
			for (var mappingIndex = 0; mappingIndex < mappings.Length; mappingIndex++)
			{
				if (reader.ReadBits(16) != 0)
					return FfmpegError.InvalidData;
				var mapping = new VorbisMapping();
				mappings[mappingIndex] = mapping;
				mapping.Submaps = reader.ReadBit() != 0 ? (int)reader.ReadBits(4) + 1 : 1;
				if (reader.ReadBit() != 0)
				{
					mapping.CouplingSteps = (int)reader.ReadBits(8) + 1;
					if (channels < 2)
						return FfmpegError.InvalidData;
					mapping.Magnitude = new int[mapping.CouplingSteps];
					mapping.Angle = new int[mapping.CouplingSteps];
					var channelBits = IntegerLog(channels - 1);
					for (var step = 0; step < mapping.CouplingSteps; step++)
					{
						mapping.Magnitude[step] = (int)reader.ReadBits(channelBits);
						mapping.Angle[step] = (int)reader.ReadBits(channelBits);
						if (mapping.Magnitude[step] >= channels || mapping.Angle[step] >= channels)
							return FfmpegError.InvalidData;
					}
				} else
				{
					mapping.Magnitude = Array.Empty<int>();
					mapping.Angle = Array.Empty<int>();
				}
				if (reader.ReadBits(2) != 0)
					return FfmpegError.InvalidData;
				if (mapping.Submaps > 1)
				{
					mapping.Mux = new int[channels];
					for (var channel = 0; channel < channels; channel++)
						mapping.Mux[channel] = (int)reader.ReadBits(4);
				} else
				{
					mapping.Mux = new int[channels];
				}
				for (var submap = 0; submap < mapping.Submaps; submap++)
				{
					reader.SkipBits(8);
					mapping.SubmapFloor[submap] = (int)reader.ReadBits(8);
					mapping.SubmapResidue[submap] = (int)reader.ReadBits(8);
					if (mapping.SubmapFloor[submap] >= floors.Length || mapping.SubmapResidue[submap] >= residues.Length)
						return FfmpegError.InvalidData;
				}
			}
			return 0;
		}

		private int ParseModes()
		{
			modes = new VorbisMode[(int)reader.ReadBits(6) + 1];
			for (var index = 0; index < modes.Length; index++)
			{
				modes[index].BlockFlag = (int)reader.ReadBit();
				modes[index].WindowType = (int)reader.ReadBits(16);
				modes[index].TransformType = (int)reader.ReadBits(16);
				modes[index].Mapping = (int)reader.ReadBits(8);
				if (modes[index].Mapping >= mappings.Length)
					return FfmpegError.InvalidData;
			}
			return 0;
		}

		private void InitializeDecodeBuffers()
		{
			var halfLargeBlock = blockSizes[1] / 2;
			channelResidues = new float[halfLargeBlock * channels];
			saved = new float[blockSizes[1] / 4 * channels];
			outputPlanes = new float[channels][];
			floorByCodecChannel = new float[channels][];
			for (var channel = 0; channel < channels; channel++)
				outputPlanes[channel] = new float[halfLargeBlock];
			var offsets = channels <= 8 ? VorbisTables.ChannelLayoutOffsets[channels - 1] : null;
			for (var channel = 0; channel < channels; channel++)
				floorByCodecChannel[channel] = outputPlanes[offsets == null ? channel : offsets[channel]];
			noResidue = new byte[channels];
			doNotDecode = new byte[channels];
			residueChannel = new byte[channels];
			floorY = new ushort[258];
			floorYFinal = new ushort[258];
			floorFlags = new int[258];
		}

		/// <summary>
		/// Decodes one Vorbis audio packet through floor, residue, inverse coupling, IMDCT, and the previous-block overlap window.
		/// </summary>
		private int ParseAudioPacket()
		{
			var packetPreviousWindow = previousWindow;
			if (reader.ReadBit() != 0)
				return FfmpegError.InvalidData;
			if (modes.Length == 1)
			{
				modeNumber = 0;
			} else
			{
				modeNumber = (int)reader.ReadBits(IntegerLog(modes.Length - 1));
				if (modeNumber >= modes.Length)
					return FfmpegError.InvalidData;
			}

			var mapping = mappings[modes[modeNumber].Mapping];
			var blockFlag = modes[modeNumber].BlockFlag;
			var blockSize = blockSizes[blockFlag];
			var vectorLength = blockSize / 2;
			if (blockFlag != 0)
			{
				var code = (int)reader.ReadBits(2);
				if (packetPreviousWindow < 0)
					packetPreviousWindow = code >> 1;
			} else if (packetPreviousWindow < 0)
			{
				packetPreviousWindow = 0;
			}

			Array.Clear(channelResidues, 0, channels * vectorLength);
			for (var channel = 0; channel < channels; channel++)
				Array.Clear(floorByCodecChannel[channel], 0, vectorLength);

			for (var channel = 0; channel < channels; channel++)
			{
				var floorIndex = mapping.Submaps > 1
					? mapping.SubmapFloor[mapping.Mux[channel]]
					: mapping.SubmapFloor[0];
				var floor = floors[floorIndex];
				var result = floor.Type == 0
					? DecodeFloor0(floor.Floor0, floorByCodecChannel[channel], blockFlag)
					: DecodeFloor1(floor.Floor1, floorByCodecChannel[channel]);
				if (result < 0)
					return FfmpegError.InvalidData;
				noResidue[channel] = (byte)result;
			}

			for (var step = mapping.CouplingSteps - 1; step >= 0; step--)
			{
				var magnitude = mapping.Magnitude[step];
				var angle = mapping.Angle[step];
				if ((noResidue[magnitude] & noResidue[angle]) == 0)
				{
					noResidue[magnitude] = 0;
					noResidue[angle] = 0;
				}
			}

			var residueNumber = 0;
			var channelsLeft = channels;
			var residueBaseOffset = 0;
			for (var submap = 0; submap < mapping.Submaps; submap++)
			{
				var usedChannels = 0;
				for (var channel = 0; channel < channels; channel++)
				{
					if (mapping.Submaps == 1 || submap == mapping.Mux[channel])
					{
						residueChannel[channel] = (byte)residueNumber;
						doNotDecode[usedChannels] = noResidue[channel] != 0 ? (byte)1 : (byte)0;
						usedChannels++;
						residueNumber++;
					}
				}

				if (channelsLeft < usedChannels)
					return FfmpegError.InvalidData;
				if (usedChannels != 0)
				{
					var result = DecodeResidue(
						residues[mapping.SubmapResidue[submap]],
						usedChannels,
						doNotDecode,
						channelResidues,
						residueBaseOffset,
						vectorLength,
						channelsLeft);
					if (result < 0)
						return result;
				}
				residueBaseOffset += usedChannels * vectorLength;
				channelsLeft -= usedChannels;
			}
			if (channelsLeft > 0)
				return FfmpegError.InvalidData;

			for (var step = mapping.CouplingSteps - 1; step >= 0; step--)
			{
				var magnitudeOffset = residueChannel[mapping.Magnitude[step]] * vectorLength;
				var angleOffset = residueChannel[mapping.Angle[step]] * vectorLength;
				InverseCoupling(channelResidues, magnitudeOffset, angleOffset, vectorLength);
			}

			for (var channel = channels - 1; channel >= 0; channel--)
			{
				var residueOffset = residueChannel[channel] * vectorLength;
				var floor = floorByCodecChannel[channel];
				for (var index = 0; index < vectorLength; index++)
					floor[index] *= channelResidues[residueOffset + index];
				mdct[blockFlag].Transform(
					floor.AsSpan(0, vectorLength),
					channelResidues.AsSpan(residueOffset, vectorLength));
			}

			var returnLength = (blockSize + blockSizes[packetPreviousWindow]) / 4;
			for (var channel = 0; channel < channels; channel++)
			{
				var smallBlock = blockSizes[0];
				var largeBlock = blockSizes[1];
				var residueOffset = residueChannel[channel] * vectorLength;
				var savedOffset = channel * largeBlock / 4;
				var destination = floorByCodecChannel[channel];
				var window = windows[blockFlag & packetPreviousWindow];
				if (blockFlag == packetPreviousWindow)
				{
					VectorMultiplyWindow(destination, 0, saved, savedOffset, channelResidues, residueOffset, window, blockSize / 4);
				} else if (blockFlag > packetPreviousWindow)
				{
					VectorMultiplyWindow(destination, 0, saved, savedOffset, channelResidues, residueOffset, window, smallBlock / 4);
					Array.Copy(channelResidues, residueOffset + smallBlock / 4, destination, smallBlock / 2, (largeBlock - smallBlock) / 4);
				} else
				{
					Array.Copy(saved, savedOffset, destination, 0, (largeBlock - smallBlock) / 4);
					VectorMultiplyWindow(
						destination,
						(largeBlock - smallBlock) / 4,
						saved,
						savedOffset + (largeBlock - smallBlock) / 4,
						channelResidues,
						residueOffset,
						window,
						smallBlock / 4);
				}
				Array.Copy(channelResidues, residueOffset + blockSize / 4, saved, savedOffset, blockSize / 4);
			}

			previousWindow = blockFlag;
			return returnLength;
		}

		/// <summary>
		/// Reconstructs a floor-0 curve from LSP codevectors and the block-specific bark map using FFmpeg's scalar operation order.
		/// </summary>
		private int DecodeFloor0(VorbisFloor0 floor, float[] vector, int blockFlag)
		{
			if (floor.AmplitudeBits == 0)
				return 1;
			var amplitude = reader.ReadBits64(floor.AmplitudeBits);
			if (amplitude == 0)
				return 1;

			var bookIndex = (int)reader.ReadBits(IntegerLog(floor.BookList.Length));
			if (bookIndex >= floor.BookList.Length)
				bookIndex = 0;
			var codebook = codebooks[floor.BookList[bookIndex]];
			if (codebook.Codevectors == null)
				return FfmpegError.InvalidData;
			var last = 0.0f;
			var lspLength = 0;
			while (lspLength < floor.Order)
			{
				var vectorOffset = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
				if (vectorOffset < 0)
					return FfmpegError.InvalidData;
				vectorOffset *= codebook.Dimensions;
				var index = 0;
				for (; index < codebook.Dimensions; index++)
					floor.Lsp[lspLength + index] = codebook.Codevectors[vectorOffset + index] + last;
				last = floor.Lsp[lspLength + index - 1];
				lspLength += codebook.Dimensions;
			}

			var windowStep = (float)(Math.PI / floor.BarkMapSize);
			for (var index = 0; index < floor.Order; index++)
				floor.Lsp[index] = (float)(2.0f * Math.Cos(floor.Lsp[index]));
			var outputIndex = 0;
			while (outputIndex < floor.MapSize[blockFlag])
			{
				var condition = floor.Map[blockFlag][outputIndex];
				var p = 0.5f;
				var q = 0.5f;
				var twoCosine = (float)(2.0f * Math.Cos(windowStep * condition));
				var lspIndex = 0;
				for (; lspIndex + 1 < floor.Order; lspIndex += 2)
				{
					q *= floor.Lsp[lspIndex] - twoCosine;
					p *= floor.Lsp[lspIndex + 1] - twoCosine;
				}
				if (lspIndex == floor.Order)
				{
					p *= p * (2.0f - twoCosine);
					q *= q * (2.0f + twoCosine);
				} else
				{
					q *= twoCosine - floor.Lsp[lspIndex];
					p *= p * (4.0f - twoCosine * twoCosine);
					q *= q;
				}
				if (p + q == 0.0f)
					return FfmpegError.InvalidData;
				q = (float)Math.Exp((((amplitude * (ulong)floor.AmplitudeOffset) /
					(((1UL << floor.AmplitudeBits) - 1) * Math.Sqrt(p + q))) - floor.AmplitudeOffset) * 0.11512925f);
				do
				{
					vector[outputIndex] = q;
					outputIndex++;
				} while (floor.Map[blockFlag][outputIndex] == condition);
			}
			return 0;
		}

		/// <summary>
		/// Decodes floor-1 partition values, predicts unused coordinates, and renders the exact inverse-dB piecewise curve.
		/// </summary>
		private int DecodeFloor1(VorbisFloor1 floor, float[] vector)
		{
			if (reader.ReadBit() == 0)
				return 1;
			var range = floor.Multiplier switch
			{
				1 => 256,
				2 => 128,
				3 => 86,
				_ => 64
			};
			floorY[0] = (ushort)reader.ReadBits(IntegerLog(range - 1));
			floorY[1] = (ushort)reader.ReadBits(IntegerLog(range - 1));
			var offset = 2;
			for (var partition = 0; partition < floor.Partitions; partition++)
			{
				var partitionClass = floor.PartitionClass[partition];
				var dimensions = floor.ClassDimensions[partitionClass];
				var subclassBits = floor.ClassSubclasses[partitionClass];
				var subclassMask = (1 << subclassBits) - 1;
				var classValue = 0;
				if (subclassBits != 0)
				{
					var master = codebooks[floor.ClassMasterbook[partitionClass]];
					classValue = reader.ReadVlc(master.Vlc.Table, master.RootBits, 3);
					if (classValue < 0)
						return FfmpegError.InvalidData;
				}
				for (var dimension = 0; dimension < dimensions; dimension++)
				{
					var book = floor.SubclassBooks[partitionClass, classValue & subclassMask];
					classValue >>= subclassBits;
					if (book > -1)
					{
						var codebook = codebooks[book];
						var value = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
						if (value < 0)
							return FfmpegError.InvalidData;
						floorY[offset + dimension] = (ushort)value;
					} else
					{
						floorY[offset + dimension] = 0;
					}
				}
				offset += dimensions;
			}

			floorFlags[0] = 1;
			floorFlags[1] = 1;
			floorYFinal[0] = floorY[0];
			floorYFinal[1] = floorY[1];
			for (var index = 2; index < floor.List.Length; index++)
			{
				var low = floor.List[index].Low;
				var high = floor.List[index].High;
				var deltaY = floorYFinal[high] - floorYFinal[low];
				var deltaX = floor.List[high].X - floor.List[low].X;
				var absoluteDeltaY = Math.Abs(deltaY);
				var error = absoluteDeltaY * (floor.List[index].X - floor.List[low].X);
				var distance = error / deltaX;
				var predicted = deltaY < 0 ? floorYFinal[low] - distance : floorYFinal[low] + distance;
				var value = floorY[index];
				var highRoom = range - predicted;
				var lowRoom = predicted;
				var room = Math.Min(highRoom, lowRoom) * 2;
				if (value != 0)
				{
					floorFlags[low] = 1;
					floorFlags[high] = 1;
					floorFlags[index] = 1;
					if (value >= room)
					{
						floorYFinal[index] = highRoom > lowRoom
							? ClipUInt16(value - lowRoom + predicted)
							: ClipUInt16(predicted - value + highRoom - 1);
					} else
					{
						floorYFinal[index] = (value & 1) != 0
							? ClipUInt16(predicted - (value + 1) / 2)
							: ClipUInt16(predicted + value / 2);
					}
				} else
				{
					floorFlags[index] = 0;
					floorYFinal[index] = ClipUInt16(predicted);
				}
			}
			RenderFloor1List(floor.List, floorYFinal, floorFlags, floor.Multiplier, vector, floor.List[1].X);
			return 0;
		}

		/// <summary>
		/// Expands one residue classword per active channel through FFmpeg's reciprocal-table division into partition classifications.
		/// </summary>
		private int SetupClassifications(VorbisResidue residue, byte[] skip, int usedChannels, int partitionCount, int partitionsToRead)
		{
			var codebook = codebooks[residue.Classbook];
			var classificationsPerCode = codebook.Dimensions;
			var inverseClassifications = VorbisTables.Reciprocal[residue.Classifications];
			var classificationOffset = 0;
			for (var channel = 0; channel < usedChannels; channel++)
			{
				if (skip[channel] == 0)
				{
					var temporary = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
					if (temporary < 0)
						return FfmpegError.InvalidData;
					if (residue.Classifications == 1)
					{
						for (var index = partitionCount + classificationsPerCode - 1; index >= partitionCount; index--)
						{
							if (index < partitionsToRead)
								residue.Classifs[classificationOffset + index] = 0;
						}
					} else
					{
						for (var index = partitionCount + classificationsPerCode - 1; index >= partitionCount; index--)
						{
							var divided = (int)(((ulong)(uint)temporary * inverseClassifications) >> 32);
							if (index < partitionsToRead)
								residue.Classifs[classificationOffset + index] = (byte)(temporary - divided * residue.Classifications);
							temporary = divided;
						}
					}
				}
				classificationOffset += partitionsToRead;
			}
			return 0;
		}

		private int DecodeResidue(
			VorbisResidue residue,
			int channelCount,
			byte[] skip,
			float[] vector,
			int vectorOffset,
			int vectorLength,
			int channelsLeft)
		{
			if (residue.Type < 0 || residue.Type > 2)
				return FfmpegError.InvalidData;
			return DecodeResidueInternal(residue, channelCount, skip, vector, vectorOffset, vectorLength, channelsLeft, residue.Type);
		}

		/// <summary>
		/// Applies all residue passes and FFmpeg's specialized type-2 stereo layouts without changing VLC or accumulation order.
		/// </summary>
		private int DecodeResidueInternal(
			VorbisResidue residue,
			int channelCount,
			byte[] skip,
			float[] vector,
			int vectorOffset,
			int vectorLength,
			int channelsLeft,
			int residueType)
		{
			var classificationsPerCode = codebooks[residue.Classbook].Dimensions;
			var usedChannels = channelCount;
			var maximumOutput = (channelCount - 1) * vectorLength;
			var partitionsToRead = residue.PartitionsToRead;
			var libVorbisBug = false;
			if (residueType == 2)
			{
				for (var channel = 1; channel < channelCount; channel++)
					skip[0] &= skip[channel];
				if (skip[0] != 0)
					return 0;
				usedChannels = 1;
				maximumOutput += residue.End / channelCount;
			} else
			{
				maximumOutput += residue.End;
			}

			if (maximumOutput > channelsLeft * vectorLength)
			{
				if (maximumOutput <= channelsLeft * vectorLength + residue.PartitionSize * usedChannels / channelCount)
				{
					partitionsToRead--;
					libVorbisBug = true;
				} else
				{
					return FfmpegError.InvalidData;
				}
			}

			for (var pass = 0; pass <= residue.MaximumPass; pass++)
			{
				var outputOffset = residue.Begin;
				var partitionCount = 0;
				while (partitionCount < partitionsToRead)
				{
					if (pass == 0)
					{
						var result = SetupClassifications(residue, skip, usedChannels, partitionCount, partitionsToRead);
						if (result < 0)
							return result;
					}
					for (var classIndex = 0; classIndex < classificationsPerCode && partitionCount < partitionsToRead; classIndex++)
					{
						var classificationOffset = 0;
						for (var channel = 0; channel < usedChannels; channel++)
						{
							if (skip[channel] == 0)
							{
								var vectorClass = residue.Classifs[classificationOffset + partitionCount];
								var vectorBook = residue.Books[vectorClass, pass];
								if (vectorBook >= 0 && codebooks[vectorBook].Codevectors != null)
								{
									var codebook = codebooks[vectorBook];
									var dimensions = codebook.Dimensions;
									var step = FastDivide(residue.PartitionSize << 1, dimensions << 1);
									if (reader.BitsLeft < 0)
										return 0;
									var result = DecodeResiduePartition(
										codebook,
										residueType,
										channelCount,
										vector,
										vectorOffset,
										vectorLength,
										outputOffset,
										channel,
										step);
									if (result < 0)
										return result;
								}
							}
							classificationOffset += partitionsToRead;
						}
						partitionCount++;
						outputOffset += residue.PartitionSize;
					}
				}
				if (libVorbisBug && pass == 0)
				{
					var classbook = codebooks[residue.Classbook];
					for (var channel = 0; channel < usedChannels; channel++)
					{
						if (skip[channel] == 0)
							reader.ReadVlc(classbook.Vlc.Table, classbook.RootBits, 3);
					}
				}
			}
			return 0;
		}

		/// <summary>
		/// Accumulates one residue codebook partition in the source type-0, type-1, optimized stereo type-2, or general type-2 layout.
		/// </summary>
		private int DecodeResiduePartition(
			VorbisCodebook codebook,
			int residueType,
			int channelCount,
			float[] vector,
			int vectorOffset,
			int vectorLength,
			int outputOffset,
			int channel,
			int step)
		{
			var dimensions = codebook.Dimensions;
			if (residueType == 0)
			{
				var position = outputOffset + channel * vectorLength;
				for (var index = 0; index < step; index++)
				{
					var codeOffset = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
					if (codeOffset < 0)
						return codeOffset;
					codeOffset *= dimensions;
					for (var dimension = 0; dimension < dimensions; dimension++)
						vector[vectorOffset + position + index + dimension * step] += codebook.Codevectors[codeOffset + dimension];
				}
			} else if (residueType == 1)
			{
				var position = outputOffset + channel * vectorLength;
				for (var index = 0; index < step; index++)
				{
					var codeOffset = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
					if (codeOffset < 0)
						return codeOffset;
					codeOffset *= dimensions;
					for (var dimension = 0; dimension < dimensions; dimension++, position++)
						vector[vectorOffset + position] += codebook.Codevectors[codeOffset + dimension];
				}
			} else if (channelCount == 2 && (outputOffset & 1) == 0 && (dimensions & 1) == 0)
			{
				var position = outputOffset >> 1;
				if (dimensions == 2)
				{
					for (var index = 0; index < step; index++)
					{
						var codeOffset = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
						if (codeOffset < 0)
							return codeOffset;
						codeOffset *= 2;
						vector[vectorOffset + position + index] += codebook.Codevectors[codeOffset];
						vector[vectorOffset + position + index + vectorLength] += codebook.Codevectors[codeOffset + 1];
					}
				} else if (dimensions == 4)
				{
					for (var index = 0; index < step; index++, position += 2)
					{
						var codeOffset = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
						if (codeOffset < 0)
							return codeOffset;
						codeOffset *= 4;
						vector[vectorOffset + position] += codebook.Codevectors[codeOffset];
						vector[vectorOffset + position + 1] += codebook.Codevectors[codeOffset + 2];
						vector[vectorOffset + position + vectorLength] += codebook.Codevectors[codeOffset + 1];
						vector[vectorOffset + position + vectorLength + 1] += codebook.Codevectors[codeOffset + 3];
					}
				} else
				{
					for (var index = 0; index < step; index++)
					{
						var codeOffset = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
						if (codeOffset < 0)
							return codeOffset;
						codeOffset *= dimensions;
						for (var dimension = 0; dimension < dimensions; dimension += 2, position++)
						{
							vector[vectorOffset + position] += codebook.Codevectors[codeOffset + dimension];
							vector[vectorOffset + position + vectorLength] += codebook.Codevectors[codeOffset + dimension + 1];
						}
					}
				}
			} else
			{
				var dividedOffset = channelCount == 1 ? outputOffset : FastDivide(outputOffset, channelCount);
				var moduloOffset = outputOffset - dividedOffset * channelCount;
				for (var index = 0; index < step; index++)
				{
					var codeOffset = reader.ReadVlc(codebook.Vlc.Table, codebook.RootBits, 3);
					if (codeOffset < 0)
						return codeOffset;
					codeOffset *= dimensions;
					for (var dimension = 0; dimension < dimensions; dimension++)
					{
						vector[vectorOffset + dividedOffset + moduloOffset * vectorLength] += codebook.Codevectors[codeOffset + dimension];
						moduloOffset++;
						if (moduloOffset == channelCount)
						{
							dividedOffset++;
							moduloOffset = 0;
						}
					}
				}
			}
			return 0;
		}

		private static int FastDivide(int value, int divisor)
		{
			return (int)(((ulong)(uint)value * VorbisTables.Reciprocal[divisor]) >> 32);
		}

		private static void InverseCoupling(float[] values, int magnitudeOffset, int angleOffset, int length)
		{
			for (var index = 0; index < length; index++)
			{
				var angle = values[angleOffset + index];
				var magnitude = values[magnitudeOffset + index];
				if (magnitude > 0.0f)
				{
					if (angle > 0.0f)
					{
						values[angleOffset + index] = magnitude - angle;
					} else
					{
						values[angleOffset + index] = magnitude;
						values[magnitudeOffset + index] = magnitude + angle;
					}
				} else if (angle > 0.0f)
				{
					values[angleOffset + index] = magnitude + angle;
				} else
				{
					values[angleOffset + index] = magnitude;
					values[magnitudeOffset + index] = magnitude - angle;
				}
			}
		}

		private static void VectorMultiplyWindow(
			float[] destination,
			int destinationOffset,
			float[] source0,
			int source0Offset,
			float[] source1,
			int source1Offset,
			float[] window,
			int length)
		{
			for (int left = -length, right = length - 1; left < 0; left++, right--)
			{
				var first = source0[source0Offset + length + left];
				var second = source1[source1Offset + right];
				var windowLeft = window[length + left];
				var windowRight = window[length + right];
				destination[destinationOffset + length + left] = first * windowRight - second * windowLeft;
				destination[destinationOffset + length + right] = first * windowLeft + second * windowRight;
			}
		}

		private static void RenderFloor1List(
			VorbisFloor1Entry[] list,
			ushort[] yList,
			int[] flags,
			int multiplier,
			float[] output,
			int samples)
		{
			var lastX = 0;
			var lastY = yList[0] * multiplier;
			for (var index = 1; index < list.Length; index++)
			{
				var position = list[index].Sort;
				if (flags[position] != 0)
				{
					var x = list[position].X;
					var y = yList[position] * multiplier;
					if (lastX < samples)
						RenderLine(lastX, lastY, Math.Min(x, samples), y, output);
					lastX = x;
					lastY = y;
				}
				if (lastX >= samples)
					break;
			}
			if (lastX < samples)
				RenderLine(lastX, lastY, samples, lastY, output);
		}

		/// <summary>
		/// Renders one floor-1 line using FFmpeg's shallow-slope unrolling and integer error progression.
		/// </summary>
		private static void RenderLine(int x0, int y0, int x1, int y1, float[] output)
		{
			var deltaY = y1 - y0;
			var deltaX = x1 - x0;
			var absoluteDeltaY = Math.Abs(deltaY);
			var signY = deltaY < 0 ? -1 : 1;
			output[x0] = VorbisTables.InverseDb[ClipUInt8(y0)];
			if (absoluteDeltaY * 2 <= deltaX)
			{
				var error = -deltaX;
				var x = x0 - (x1 - 1);
				var outputOffset = x1 - 1;
				while (++x < 0)
				{
					error += absoluteDeltaY;
					if (error >= 0)
					{
						error += absoluteDeltaY - deltaX;
						y0 += signY;
						output[outputOffset + x] = VorbisTables.InverseDb[ClipUInt8(y0)];
						x++;
					}
					output[outputOffset + x] = VorbisTables.InverseDb[ClipUInt8(y0)];
				}
				if (x <= 0)
				{
					if (error + absoluteDeltaY >= 0)
						y0 += signY;
					output[outputOffset + x] = VorbisTables.InverseDb[ClipUInt8(y0)];
				}
			} else
			{
				var baseValue = deltaY / deltaX;
				var x = x0;
				var y = y0;
				var error = -deltaX;
				absoluteDeltaY -= Math.Abs(baseValue) * deltaX;
				while (++x < x1)
				{
					y += baseValue;
					error += absoluteDeltaY;
					if (error >= 0)
					{
						error -= deltaX;
						y += signY;
					}
					output[x] = VorbisTables.InverseDb[ClipUInt8(y)];
				}
			}
		}

		private static ushort ClipUInt16(int value)
		{
			if (value < 0)
				return 0;
			if (value > ushort.MaxValue)
				return ushort.MaxValue;
			return (ushort)value;
		}

		private static int ClipUInt8(int value)
		{
			if (value < 0)
				return 0;
			if (value > byte.MaxValue)
				return byte.MaxValue;
			return value;
		}

		private bool ReadSignature()
		{
			return reader.ReadBits(8) == (byte)'v' && reader.ReadBits(8) == (byte)'o' && reader.ReadBits(8) == (byte)'r' &&
				reader.ReadBits(8) == (byte)'b' && reader.ReadBits(8) == (byte)'i' && reader.ReadBits(8) == (byte)'s';
		}

		private static bool SplitXiphHeaders(byte[] data, out byte[] first, out byte[] second, out byte[] third)
		{
			first = null;
			second = null;
			third = null;
			if (data.Length < 3 || data[0] != 2)
				return false;
			var offset = 1;
			if (!ReadXiphLength(data, ref offset, out var firstLength) || !ReadXiphLength(data, ref offset, out var secondLength) ||
				firstLength < 30 || firstLength > data.Length - offset || secondLength > data.Length - offset - firstLength)
				return false;
			var thirdLength = data.Length - offset - firstLength - secondLength;
			if (thirdLength <= 0)
				return false;
			first = data.AsSpan(offset, firstLength).ToArray();
			offset += firstLength;
			second = data.AsSpan(offset, secondLength).ToArray();
			offset += secondLength;
			third = data.AsSpan(offset, thirdLength).ToArray();
			return true;
		}

		private static bool ReadXiphLength(byte[] data, ref int offset, out int length)
		{
			length = 0;
			while (offset < data.Length)
			{
				var value = data[offset++];
				length = checked(length + value);
				if (value < 255)
					return true;
			}
			return false;
		}

		private static float VorbisFloatToFloat(uint value)
		{
			var mantissa = (float)(value & 0x1fffff);
			var exponent = (int)((value & 0x7fe00000) >> 21);
			if ((value & 0x80000000) != 0)
				mantissa = -mantissa;
			return MathF.ScaleB(mantissa, exponent - 20 - 768);
		}

		private static uint NthRoot(uint value, uint root)
		{
			uint result = 0;
			uint product;
			do
			{
				result++;
				product = result;
				for (var index = 0; index < root - 1; index++)
					product = unchecked(product * result);
			} while (product <= value);
			return result - 1;
		}

		/// <summary>
		/// Recreates Vorbis's canonical low-bit-first code assignment without changing sparse entry order.
		/// </summary>
		private static int LengthsToCodes(byte[] lengths, uint[] codes, int count)
		{
			var exitAtLevel = new uint[33];
			exitAtLevel[0] = 404;
			var position = 0;
			while (position < count && lengths[position] == 0)
				position++;
			if (position == count)
				return 0;
			codes[position] = 0;
			if (lengths[position] > 32)
				return FfmpegError.InvalidData;
			for (var level = 0; level < lengths[position]; level++)
				exitAtLevel[level + 1] = 1u << level;
			position++;
			var next = position;
			while (next < count && lengths[next] == 0)
				next++;
			if (next == count)
				return 0;
			for (; position < count; position++)
			{
				if (lengths[position] > 32)
					return FfmpegError.InvalidData;
				if (lengths[position] == 0)
					continue;
				var level = lengths[position];
				while (level > 0 && exitAtLevel[level] == 0)
					level--;
				if (level == 0)
					return FfmpegError.InvalidData;
				var code = exitAtLevel[level];
				exitAtLevel[level] = 0;
				for (var child = level + 1; child <= lengths[position]; child++)
					exitAtLevel[child] = code + (1u << (child - 1));
				codes[position] = code;
			}
			for (var level = 1; level < 33; level++)
			{
				if (exitAtLevel[level] != 0)
					return FfmpegError.InvalidData;
			}
			return 0;
		}

		private static int IntegerLog(int value)
		{
			var result = 0;
			for (var current = value; current > 0; current >>= 1)
				result++;
			return result;
		}

		/// <summary>
		/// Resolves each floor-1 point's prediction neighbours and preserves FFmpeg's in-place coordinate sort order.
		/// </summary>
		private static int PrepareFloor1List(VorbisFloor1Entry[] list)
		{
			list[0].Sort = 0;
			list[1].Sort = 1;
			for (var index = 2; index < list.Length; index++)
			{
				list[index].Low = 0;
				list[index].High = 1;
				list[index].Sort = index;
				for (var prior = 2; prior < index; prior++)
				{
					var coordinate = list[prior].X;
					if (coordinate < list[index].X)
					{
						if (coordinate > list[list[index].Low].X)
							list[index].Low = prior;
					} else if (coordinate < list[list[index].High].X)
					{
						list[index].High = prior;
					}
				}
			}
			for (var left = 0; left < list.Length - 1; left++)
			{
				for (var right = left + 1; right < list.Length; right++)
				{
					if (list[left].X == list[right].X)
						return FfmpegError.InvalidData;
					if (list[list[left].Sort].X > list[list[right].Sort].X)
						(list[left].Sort, list[right].Sort) = (list[right].Sort, list[left].Sort);
				}
			}
			return 0;
		}

		private void CreateFloor0Maps(VorbisFloor0 floor)
		{
			for (var blockFlag = 0; blockFlag < 2; blockFlag++)
			{
				var length = blockSizes[blockFlag] / 2;
				var map = new int[length + 1];
				floor.Map[blockFlag] = map;
				for (var index = 0; index < length; index++)
				{
					map[index] = (int)Math.Floor(Bark(floor.Rate * index / (2.0f * length)) *
						(floor.BarkMapSize / Bark(floor.Rate / 2.0f)));
					if (floor.BarkMapSize - 1 < map[index])
						map[index] = floor.BarkMapSize - 1;
				}
				map[length] = -1;
				floor.MapSize[blockFlag] = length;
			}
		}

		private static float Bark(float value)
		{
			return (float)(13.1f * Math.Atan(0.00074f * value) + 2.24f * Math.Atan(1.85e-8f * value * value) + 1e-4f * value);
		}
	}
}
