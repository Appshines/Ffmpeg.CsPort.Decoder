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
 * PORT-NOTE: 1:1 translation. Performance-motivated, semantics-preserving transformations
 * applied (see repository history); bit-exactness remains verified by the conformance tests.
 */
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Transforms;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>
	/// Ports FFmpeg's scalar AAC-LC/HE-AAC raw-data-block decoder, including spectral tools, IMDCT, SBR, and overlap.
	/// </summary>
	public sealed class AacLcDecoder
	{
		private const int NoiseBandType = 13;
		private const int IntensityBandType2 = 14;
		private const int IntensityBandType = 15;
		private const int ScaleDifferenceZero = 60;
		private const int PowerScaleFactorZero = 200;
		private const int NoiseOffset = 90;
		private const int NoisePre = 256;
		private static readonly byte[,] GainModes =
		{
			{ 1, 0, 5 }, { 2, 1, 2 }, { 8, 0, 2 }, { 2, 1, 5 }
		};

		private readonly BitReader reader = new BitReader();
		private readonly BitReader configReader = new BitReader();
		private readonly AacSbrBitstream sbrBitstream = new AacSbrBitstream();
		private readonly AacSbrProcessor sbrProcessor = new AacSbrProcessor();
		private readonly AacChannelElement[,] elements = new AacChannelElement[4, 64];
		private readonly AacChannelElement[,] programConfigTagMap = new AacChannelElement[4, 16];
		private readonly AacProgramConfigEntry[] programConfigLayout = new AacProgramConfigEntry[64];
		private readonly AacProgramConfigEntry[] programConfigOrder = new AacProgramConfigEntry[64];
		private readonly byte[,] elementPresence = new byte[4, 64];
		private readonly AacPulse pulse = new AacPulse();
		private readonly float[] mdctBuffer = new float[1024];
		private readonly float[] temporary = new float[128];
		private readonly float[] lpc = new float[20];
		private readonly byte[] fillBuffer = new byte[256];
		private readonly FfmpegFloatMdct mdct1024;
		private readonly FfmpegFloatMdct mdct128;
		private AacSingleChannelElement[] outputChannels;
		private int objectType;
		private int samplingIndex;
		private int sampleRate;
		private int coreSampleRate;
		private int extensionSampleRate;
		private int sbrMode = -1;
		private int psMode = -1;
		private int channelConfiguration;
		private int channels;
		private int randomState = 0x1f2e3d4c;
		private int tagsMapped;
		private int skipSamples;
		private bool configured;

		public int Channels => channels;
		public int SampleRate => sampleRate;
		public int OutputFrameSampleCount => sbrMode == 1 && extensionSampleRate > coreSampleRate ? 2048 : 1024;

		private AacLcDecoder()
		{
			var longScale = (float)((1.0 / 1024) / 32768.0f);
			var shortScale = (float)((1.0 / 128) / 32768.0f);
			mdct1024 = new FfmpegFloatMdct(1024, true, longScale);
			mdct128 = new FfmpegFloatMdct(128, true, shortScale);
		}

		/// <summary>
		/// Parses an MPEG-4 AudioSpecificConfig for AAC-LC, or creates an unconfigured decoder that accepts an ADTS header on its first packet.
		/// </summary>
		public static int Initialize(byte[] extraData, out AacLcDecoder decoder)
		{
			return Initialize(extraData, extraData == null ? 0 : extraData.Length, out decoder);
		}

		internal static int Initialize(byte[] extraData, int extraDataLength, out AacLcDecoder decoder)
		{
			var result = new AacLcDecoder();
			if (extraData != null && extraDataLength != 0)
			{
				var status = result.ParseAudioSpecificConfig(extraData, extraDataLength);
				if (status < 0)
				{
					decoder = null;
					return status;
				}
			}
			decoder = result;
			return 0;
		}

		/// <summary>
		/// Decodes one raw AAC-LC or HE-AAC access unit, or one complete ADTS frame, to FFmpeg-ordered planar float samples.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength <= 0 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			if (reader.Initialize(packet, packetOffset, packetLength * 8) < 0)
				return FfmpegError.InvalidData;
			if (reader.BitsLeft >= 12 && reader.ShowBits(12) == 0xfff)
			{
				var result = ParseAdtsHeader(packetLength);
				if (result < 0)
					return result;
			}
			if (!configured || objectType != 2)
				return FfmpegError.InvalidData;
			if (output.Length < checked(1024 * channels * sizeof(float)))
				return FfmpegError.InvalidArgument;

			Array.Clear(elementPresence, 0, elementPresence.Length);
			tagsMapped = 0;
			var audioFound = false;
			var payloadAlignment = reader.Position;
			AacChannelElement previousElement = null;
			var previousElementType = AacElementType.End;
			while (true)
			{
				if (reader.BitsLeft < 3)
					return FfmpegError.InvalidData;
				var type = (AacElementType)reader.ReadBits(3);
				if (type == AacElementType.End)
					break;
				if (reader.BitsLeft < 4)
					return FfmpegError.InvalidData;
				var elementId = (int)reader.ReadBits(4);
				if (type < AacElementType.DataStream)
				{
					var presence = ++elementPresence[(int)type, elementId];
					if (presence > 2)
						return FfmpegError.InvalidData;
				}

				var status = 0;
				switch (type)
				{
					case AacElementType.SingleChannel:
					case AacElementType.LowFrequency:
					{
						var element = GetChannelElement(type, elementId);
						if (element == null)
							return FfmpegError.InvalidData;
						element.Present = true;
						status = DecodeIndividualChannelStream(element.Channels[0], false);
						audioFound = true;
						previousElement = element;
						previousElementType = type;
						break;
					}
					case AacElementType.ChannelPair:
					{
						var element = GetChannelElement(type, elementId);
						if (element == null)
							return FfmpegError.InvalidData;
						element.Present = true;
						status = DecodeChannelPair(element);
						audioFound = true;
						previousElement = element;
						previousElementType = type;
						break;
					}
					case AacElementType.CouplingChannel:
						return FfmpegError.NotImplemented;
					case AacElementType.DataStream:
						status = SkipDataStreamElement();
						break;
					case AacElementType.ProgramConfig:
						status = ParseProgramConfig(payloadAlignment, true);
						break;
					case AacElementType.Fill:
						status = DecodeFillElement(elementId, previousElement, previousElementType);
						break;
					default:
						return FfmpegError.InvalidData;
				}
				if (status < 0)
					return status;
			}
			if (!audioFound)
				return FfmpegError.InvalidData;

			for (var type = (int)AacElementType.LowFrequency; type >= 0; type--)
			{
				for (var elementId = 0; elementId < 64; elementId++)
				{
					var element = elements[type, elementId];
					if (element == null || !element.Present)
						continue;
					if (element.Channels[0].Tns.Present)
						ApplyTemporalNoiseShaping(element.Channels[0]);
					ImdctAndWindow(element.Channels[0]);
					if (type == (int)AacElementType.ChannelPair)
					{
						if (element.Channels[1].Tns.Present)
							ApplyTemporalNoiseShaping(element.Channels[1]);
						ImdctAndWindow(element.Channels[1]);
					}
					if (sbrMode == 1)
						sbrProcessor.Apply(element, (AacElementType)type, sampleRate, psMode == 1);
					element.Present = false;
				}
			}
			if (skipSamples >= 1024)
			{
				skipSamples -= 1024;
				return FfmpegError.TryAgain;
			}

			var frameSamples = sbrMode == 1 && extensionSampleRate > coreSampleRate ? 2048 : 1024;
			var planeSize = frameSamples * sizeof(float);
			var dataSize = checked(planeSize * channels);
			if (output.Length < dataSize)
				return FfmpegError.InvalidArgument;
			for (var channel = 0; channel < channels; channel++)
			{
				var source = outputChannels[channel].Output;
				var destinationOffset = channel * planeSize;
				if (BitConverter.IsLittleEndian)
				{
					MemoryMarshal.AsBytes(source.AsSpan(0, frameSamples))
						.CopyTo(output.Slice(destinationOffset, planeSize));
				} else for (var sample = 0; sample < frameSamples; sample++)
				{
					BinaryPrimitives.WriteInt32LittleEndian(
						output.Slice(destinationOffset + sample * sizeof(float), sizeof(float)),
						BitConverter.SingleToInt32Bits(source[sample]));
				}
			}
			frame = new AudioFrameInfo(frameSamples, channels, AudioSampleFormat.FloatPlanar, channels, planeSize, dataSize);
			return packetLength;
		}

		public void Flush()
		{
			for (var type = 0; type < 4; type++)
			{
				for (var id = 0; id < 64; id++)
				{
					var element = elements[type, id];
					if (element == null)
						continue;
					Array.Clear(element.Channels[0].Saved, 0, element.Channels[0].Saved.Length);
					Array.Clear(element.Channels[1].Saved, 0, element.Channels[1].Saved.Length);
				}
			}
		}

		/// <summary>
		/// Parses explicit or sync-extension HE-AAC signaling around the LC GA-specific configuration in FFmpeg bit order.
		/// </summary>
		private int ParseAudioSpecificConfig(byte[] extraData, int extraDataLength)
		{
			if (extraDataLength < 0 || extraDataLength > extraData.Length || reader.Initialize(extraData, extraDataLength * 8) < 0)
				return FfmpegError.InvalidData;
			objectType = ReadObjectType();
			var parsedSampleRate = ReadSampleRate(out samplingIndex);
			if (parsedSampleRate <= 0 || samplingIndex > 12)
				return FfmpegError.InvalidData;
			channelConfiguration = (int)reader.ReadBits(4);
			sbrMode = -1;
			psMode = -1;
			extensionSampleRate = 0;
			if (objectType == 5 || objectType == 29)
			{
				psMode = objectType == 29 ? 1 : -1;
				sbrMode = 1;
				extensionSampleRate = ReadSampleRate(out _);
				objectType = ReadObjectType();
			}
			if (objectType != 2)
				return FfmpegError.NotImplemented;
			if (reader.ReadBit() != 0)
				return FfmpegError.NotImplemented;
			if (reader.ReadBit() != 0)
				reader.SkipBits(14);
			reader.ReadBit();
			coreSampleRate = parsedSampleRate;
			if (channelConfiguration == 0)
			{
				reader.SkipBits(4);
				var programConfigResult = ParseProgramConfig(0, true);
				if (programConfigResult < 0)
					return programConfigResult;
				if (channels > 1)
					psMode = 0;
			}
			ScanSyncExtension(extraData, extraDataLength, reader.Position);
			sampleRate = sbrMode == 1 && extensionSampleRate > 0 ? extensionSampleRate : coreSampleRate;
			return channelConfiguration == 0 ? 0 : ConfigureChannels(channelConfiguration);
		}

		private void ScanSyncExtension(byte[] extraData, int extraDataLength, int position)
		{
			if (sbrMode == 1 || configReader.Initialize(extraData, extraDataLength * 8) < 0)
				return;
			configReader.Seek(position);
			while (configReader.BitsLeft > 15)
			{
				if (configReader.ShowBits(11) == 0x2b7)
				{
					configReader.SkipBits(11);
					var extensionObjectType = ReadObjectType(configReader);
					if (extensionObjectType == 5 && configReader.ReadBit() != 0)
					{
						sbrMode = 1;
						extensionSampleRate = ReadSampleRate(configReader, out _);
						if (extensionSampleRate == coreSampleRate)
							sbrMode = -1;
					}
					if (configReader.BitsLeft > 11 && configReader.ReadBits(11) == 0x548)
						psMode = (int)configReader.ReadBit();
					break;
				}
				configReader.SkipBits(1);
			}
		}

		/// <summary>
		/// Consumes an ADTS fixed/variable header and updates the LC channel configuration before the raw data block is decoded.
		/// </summary>
		private int ParseAdtsHeader(int packetLength)
		{
			if (reader.ReadBits(12) != 0xfff)
				return FfmpegError.InvalidData;
			reader.SkipBits(3);
			var crcAbsent = reader.ReadBit() != 0;
			var parsedObjectType = (int)reader.ReadBits(2) + 1;
			var parsedSamplingIndex = (int)reader.ReadBits(4);
			if (parsedSamplingIndex >= AacTables.SampleRates.Length || AacTables.SampleRates[parsedSamplingIndex] == 0)
				return FfmpegError.InvalidData;
			reader.SkipBits(1);
			var parsedConfiguration = (int)reader.ReadBits(3);
			reader.SkipBits(4);
			var frameLength = (int)reader.ReadBits(13);
			if (frameLength < 7 || frameLength > packetLength)
				return FfmpegError.InvalidData;
			reader.SkipBits(11);
			var rawDataBlocks = (int)reader.ReadBits(2) + 1;
			if (rawDataBlocks != 1 || parsedObjectType != 2)
				return FfmpegError.NotImplemented;
			if (!crcAbsent)
				reader.SkipBits(16);
			objectType = parsedObjectType;
			samplingIndex = parsedSamplingIndex;
			sampleRate = AacTables.SampleRates[samplingIndex];
			coreSampleRate = sampleRate;
			extensionSampleRate = 0;
			sbrMode = -1;
			psMode = -1;
			if (!configured || channelConfiguration != parsedConfiguration)
				return ConfigureChannels(parsedConfiguration);
			return 0;
		}

		private int ReadObjectType()
		{
			return ReadObjectType(reader);
		}

		private static int ReadObjectType(BitReader source)
		{
			var value = (int)source.ReadBits(5);
			return value == 31 ? 32 + (int)source.ReadBits(6) : value;
		}

		private int ReadSampleRate(out int index)
		{
			return ReadSampleRate(reader, out index);
		}

		private static int ReadSampleRate(BitReader source, out int index)
		{
			index = (int)source.ReadBits(4);
			return index == 15 ? (int)source.ReadBits(24) : AacTables.SampleRates[index];
		}

		/// <summary>
		/// Allocates the standard AAC element map and assigns each syntax channel to FFmpeg's default AVChannelLayout plane order.
		/// </summary>
		private int ConfigureChannels(int configuration)
		{
			if (configuration < 1 || configuration > 7)
				return FfmpegError.NotImplemented;
			channelConfiguration = configuration;
			channels = configuration == 1 && psMode == 1 ? 2 : AacTables.ChannelsPerConfiguration[configuration];
			outputChannels = new AacSingleChannelElement[channels];
			switch (configuration)
			{
				case 1:
					AssignOutput(0, AacElementType.SingleChannel, 0, 0);
					if (psMode == 1)
						AssignOutput(1, AacElementType.SingleChannel, 0, 1);
					break;
				case 2:
					AssignOutput(0, AacElementType.ChannelPair, 0, 0);
					AssignOutput(1, AacElementType.ChannelPair, 0, 1);
					break;
				case 3:
					AssignOutput(0, AacElementType.ChannelPair, 0, 0);
					AssignOutput(1, AacElementType.ChannelPair, 0, 1);
					AssignOutput(2, AacElementType.SingleChannel, 0, 0);
					break;
				case 4:
					AssignOutput(0, AacElementType.ChannelPair, 0, 0);
					AssignOutput(1, AacElementType.ChannelPair, 0, 1);
					AssignOutput(2, AacElementType.SingleChannel, 0, 0);
					AssignOutput(3, AacElementType.SingleChannel, 1, 0);
					break;
				case 5:
					AssignOutput(0, AacElementType.ChannelPair, 0, 0);
					AssignOutput(1, AacElementType.ChannelPair, 0, 1);
					AssignOutput(2, AacElementType.SingleChannel, 0, 0);
					AssignOutput(3, AacElementType.ChannelPair, 1, 0);
					AssignOutput(4, AacElementType.ChannelPair, 1, 1);
					break;
				case 6:
					AssignOutput(0, AacElementType.ChannelPair, 0, 0);
					AssignOutput(1, AacElementType.ChannelPair, 0, 1);
					AssignOutput(2, AacElementType.SingleChannel, 0, 0);
					AssignOutput(3, AacElementType.LowFrequency, 0, 0);
					AssignOutput(4, AacElementType.ChannelPair, 1, 0);
					AssignOutput(5, AacElementType.ChannelPair, 1, 1);
					break;
				case 7:
					AssignOutput(0, AacElementType.ChannelPair, 0, 0);
					AssignOutput(1, AacElementType.ChannelPair, 0, 1);
					AssignOutput(2, AacElementType.SingleChannel, 0, 0);
					AssignOutput(3, AacElementType.LowFrequency, 0, 0);
					AssignOutput(4, AacElementType.ChannelPair, 2, 0);
					AssignOutput(5, AacElementType.ChannelPair, 2, 1);
					AssignOutput(6, AacElementType.ChannelPair, 1, 0);
					AssignOutput(7, AacElementType.ChannelPair, 1, 1);
					break;
			}
			configured = true;
			return 0;
		}

		private void AssignOutput(int outputIndex, AacElementType type, int id, int elementChannel)
		{
			var element = elements[(int)type, id];
			if (element == null)
			{
				element = new AacChannelElement();
				elements[(int)type, id] = element;
			}
			outputChannels[outputIndex] = element.Channels[elementChannel];
			element.Sbr.ElementType = type;
		}

		/// <summary>Parses one AAC program_config_element, orders its elements like FFmpeg's channel-layout sniffer, and installs its tag map.</summary>
		private int ParseProgramConfig(int alignmentReference, bool configure)
		{
			reader.SkipBits(2);
			reader.ReadBits(4);
			var numberOfFront = (int)reader.ReadBits(4);
			var numberOfSide = (int)reader.ReadBits(4);
			var numberOfBack = (int)reader.ReadBits(4);
			var numberOfLowFrequency = (int)reader.ReadBits(2);
			var numberOfAssociatedData = (int)reader.ReadBits(3);
			var numberOfCoupling = (int)reader.ReadBits(4);
			if (reader.ReadBit() != 0)
				reader.SkipBits(4);
			if (reader.ReadBit() != 0)
				reader.SkipBits(4);
			if (reader.ReadBit() != 0)
				reader.SkipBits(3);
			var requiredBits = 5 * (numberOfFront + numberOfSide + numberOfBack + numberOfCoupling) +
				4 * (numberOfLowFrequency + numberOfAssociatedData + numberOfCoupling);
			if (reader.BitsLeft < requiredBits)
				return FfmpegError.InvalidData;
			var tags = 0;
			tags = ReadProgramConfigGroup(tags, numberOfFront, 1);
			tags = ReadProgramConfigGroup(tags, numberOfSide, 2);
			tags = ReadProgramConfigGroup(tags, numberOfBack, 3);
			tags = ReadProgramConfigGroup(tags, numberOfLowFrequency, 4);
			reader.SkipBits(4 * numberOfAssociatedData);
			tags = ReadProgramConfigGroup(tags, numberOfCoupling, 5);
			reader.SkipBits((alignmentReference - reader.Position) & 7);
			var commentBits = (int)reader.ReadBits(8) * 8;
			if (reader.BitsLeft < commentBits)
				return FfmpegError.InvalidData;
			reader.SkipBits(commentBits);
			return configure ? ConfigureProgramConfig(tags) : tags;
		}

		private int ReadProgramConfigGroup(int offset, int count, int position)
		{
			for (var index = 0; index < count; index++)
			{
				AacElementType type;
				if (position == 4)
				{
					type = AacElementType.LowFrequency;
				} else if (position == 5)
				{
					reader.SkipBits(1);
					type = AacElementType.CouplingChannel;
				} else
				{
					type = reader.ReadBit() != 0 ? AacElementType.ChannelPair : AacElementType.SingleChannel;
				}
				programConfigLayout[offset + index].Type = type;
				programConfigLayout[offset + index].Id = (int)reader.ReadBits(4);
				programConfigLayout[offset + index].Position = position;
				programConfigLayout[offset + index].ChannelKey = 0;
			}
			return offset + count;
		}

		/// <summary>Recreates FFmpeg's PCE syntax-ID remapping and default planar channel order for recognized AAC spatial groups.</summary>
		private int ConfigureProgramConfig(int tags)
		{
			var typeCounts = new int[4];
			var idMap = new byte[4, 16];
			Array.Clear(programConfigTagMap, 0, programConfigTagMap.Length);
			for (var index = 0; index < tags; index++)
			{
				var entry = programConfigLayout[index];
				if ((int)entry.Type >= 4 || entry.Id >= 16)
					return FfmpegError.InvalidData;
				idMap[(int)entry.Type, entry.Id] = (byte)typeCounts[(int)entry.Type]++;
			}
			OrderProgramConfig(tags);
			var outputCount = 0;
			for (var index = 0; index < tags; index++)
			{
				var entry = programConfigLayout[index];
				if (entry.Type == AacElementType.CouplingChannel)
					continue;
				outputCount += entry.Type == AacElementType.ChannelPair ? 2 : 1;
			}
			if (outputCount <= 0)
				return FfmpegError.InvalidData;
			channels = outputCount;
			outputChannels = new AacSingleChannelElement[channels];
			var outputIndex = 0;
			for (var index = 0; index < tags; index++)
			{
				var entry = programConfigLayout[index];
				var internalId = idMap[(int)entry.Type, entry.Id];
				var element = elements[(int)entry.Type, internalId];
				if (element == null)
				{
					element = new AacChannelElement();
					elements[(int)entry.Type, internalId] = element;
				}
				programConfigTagMap[(int)entry.Type, entry.Id] = element;
				element.Sbr.ElementType = entry.Type;
				if (entry.Type == AacElementType.CouplingChannel)
					continue;
				outputChannels[outputIndex++] = element.Channels[0];
				if (entry.Type == AacElementType.ChannelPair)
					outputChannels[outputIndex++] = element.Channels[1];
			}
			channelConfiguration = 0;
			configured = true;
			return 0;
		}

		/// <summary>Assigns standard AVChannel bit positions to PCE entries and sorts complete syntax elements by their resulting masks.</summary>
		private void OrderProgramConfig(int tags)
		{
			var current = 0;
			for (var layer = 0; layer < 3 && current < tags; layer++)
			{
				for (var position = 1; position <= 4; position++)
					AssignProgramConfigChannels(tags, layer, position, ref current);
			}
			var count = current;
			var length = count;
			do
			{
				var nextLength = 0;
				for (var index = 1; index < length; index++)
				{
					if (programConfigOrder[index - 1].ChannelKey > programConfigOrder[index].ChannelKey)
					{
						var temporaryEntry = programConfigOrder[index - 1];
						programConfigOrder[index - 1] = programConfigOrder[index];
						programConfigOrder[index] = temporaryEntry;
						nextLength = index;
					}
				}
				length = nextLength;
			} while (length > 0);
			for (var index = 0; index < count; index++)
				programConfigLayout[index] = programConfigOrder[index];
		}

		/// <summary>Maps one homogeneous PCE spatial group onto FFmpeg's layer-specific single and paired channel positions.</summary>
		private void AssignProgramConfigChannels(int tags, int layer, int position, ref int current)
		{
			var numberOfChannels = CountProgramConfigChannels(tags, position, current);
			if (numberOfChannels < 0 || numberOfChannels > 5)
				return;
			var index = current;
			if (position == 4)
			{
				var channelIndex = 0;
				while (numberOfChannels > 0)
				{
					var channel = ProgramConfigChannelMap[layer, position - 1, channelIndex++];
					if (channel < 0)
						return;
					programConfigOrder[index] = programConfigLayout[index];
					programConfigOrder[index].ChannelKey = 1UL << channel;
					index++;
					numberOfChannels--;
				}
				current = index;
				return;
			}
			while ((numberOfChannels & 1) != 0)
			{
				var channel = ProgramConfigChannelMap[layer, position - 1, 0];
				if (channel < 0)
					return;
				if (channel == 512)
					break;
				programConfigOrder[index] = programConfigLayout[index];
				programConfigOrder[index].ChannelKey = 1UL << channel;
				index++;
				numberOfChannels--;
			}
			var pairIndex = position != 2 && numberOfChannels <= 3 ? 3 : 1;
			while (numberOfChannels >= 2)
			{
				var left = ProgramConfigChannelMap[layer, position - 1, pairIndex];
				var right = ProgramConfigChannelMap[layer, position - 1, pairIndex + 1];
				if (left < 0 || right < 0)
					return;
				if (programConfigLayout[index].Type == AacElementType.ChannelPair)
				{
					programConfigOrder[index] = programConfigLayout[index];
					programConfigOrder[index].ChannelKey = 1UL << left | 1UL << right;
					index++;
				} else
				{
					programConfigOrder[index] = programConfigLayout[index];
					programConfigOrder[index].ChannelKey = 1UL << left;
					programConfigOrder[index + 1] = programConfigLayout[index + 1];
					programConfigOrder[index + 1].ChannelKey = 1UL << right;
					index += 2;
				}
				pairIndex += 2;
				numberOfChannels -= 2;
			}
			while ((numberOfChannels & 1) != 0)
			{
				var channel = ProgramConfigChannelMap[layer, position - 1, 5];
				if (channel < 0)
					return;
				programConfigOrder[index] = programConfigLayout[index];
				programConfigOrder[index].ChannelKey = 1UL << channel;
				index++;
				numberOfChannels--;
			}
			current = index;
		}

		private int CountProgramConfigChannels(int tags, int position, int current)
		{
			var numberOfChannels = 0;
			var firstPair = false;
			var singleParity = false;
			for (var index = current; index < tags; index++)
			{
				if (programConfigLayout[index].Position != position)
					break;
				if (programConfigLayout[index].Type == AacElementType.ChannelPair)
				{
					if (singleParity)
					{
						if (position == 1 && !firstPair)
							singleParity = false;
						else
							return -1;
					}
					numberOfChannels += 2;
					firstPair = true;
				} else
				{
					numberOfChannels++;
					if (position != 4)
						singleParity = !singleParity;
				}
			}
			if (singleParity && position == 1 && firstPair)
				return -1;
			return numberOfChannels;
		}

		private static readonly int[,,] ProgramConfigChannelMap =
		{
			{ { 2, 6, 7, 0, 1, -1 }, { 512, -1, -1, -1, -1, -1 }, { 512, 9, 10, 4, 5, 8 }, { 3, 35, -1, -1, -1, -1 } },
			{ { 13, -1, -1, 12, 14, -1 }, { 512, 36, 37, -1, -1, 11 }, { 512, -1, -1, 15, 17, 16 }, { -1, -1, -1, -1, -1, -1 } },
			{ { 38, -1, -1, 39, 40, -1 }, { -1, -1, -1, -1, -1, -1 }, { -1, -1, -1, -1, -1, -1 }, { -1, -1, -1, -1, -1, -1 } }
		};

		private void EnableParametricStereoOutput()
		{
			if (channelConfiguration != 1 || channels == 2)
				return;
			psMode = 1;
			channels = 2;
			outputChannels = new AacSingleChannelElement[2];
			AssignOutput(0, AacElementType.SingleChannel, 0, 0);
			AssignOutput(1, AacElementType.SingleChannel, 0, 1);
		}

		/// <summary>
		/// Reproduces FFmpeg's default channel-configuration tag remapping, whose element IDs are advisory and whose syntax order selects the allocated channel element.
		/// </summary>
		private AacChannelElement GetChannelElement(AacElementType type, int elementId)
		{
			if (channelConfiguration == 0)
				return (int)type < 4 && (uint)elementId < 16U ? programConfigTagMap[(int)type, elementId] : null;
			switch (channelConfiguration)
			{
				case 7:
					if (tagsMapped == 3 && type == AacElementType.ChannelPair)
					{
						tagsMapped++;
						return elements[(int)AacElementType.ChannelPair, 2];
					}
					goto case 6;
				case 6:
					if (tagsMapped == AacTables.TagsPerConfiguration[channelConfiguration] - 1 &&
						(type == AacElementType.LowFrequency || type == AacElementType.SingleChannel))
					{
						tagsMapped++;
						return elements[(int)AacElementType.LowFrequency, 0];
					}
					goto case 5;
				case 5:
					if (tagsMapped == 2 && type == AacElementType.ChannelPair)
					{
						tagsMapped++;
						return elements[(int)AacElementType.ChannelPair, 1];
					}
					goto case 4;
				case 4:
					if (tagsMapped == AacTables.TagsPerConfiguration[channelConfiguration] - 1 &&
						(type == AacElementType.LowFrequency || type == AacElementType.SingleChannel))
					{
						tagsMapped++;
						return elements[(int)AacElementType.SingleChannel, 1];
					}
					if (tagsMapped == 2 && channelConfiguration == 4 && type == AacElementType.SingleChannel)
					{
						tagsMapped++;
						return elements[(int)AacElementType.SingleChannel, 1];
					}
					goto case 3;
				case 3:
				case 2:
					if (tagsMapped == (channelConfiguration != 2 ? 1 : 0) && type == AacElementType.ChannelPair)
					{
						tagsMapped++;
						return elements[(int)AacElementType.ChannelPair, 0];
					}
					if (tagsMapped == 1 && channelConfiguration == 2 && type == AacElementType.SingleChannel)
					{
						tagsMapped++;
						return elements[(int)AacElementType.SingleChannel, 1];
					}
					goto case 1;
				case 1:
					if (tagsMapped == 0 && type == AacElementType.SingleChannel)
					{
						tagsMapped++;
						return elements[(int)AacElementType.SingleChannel, 0];
					}
					break;
			}
			return null;
		}

		/// <summary>
		/// Decodes a channel-pair element's common window, mid/side mask, two individual streams, and intensity reconstruction.
		/// </summary>
		private int DecodeChannelPair(AacChannelElement element)
		{
			var commonWindow = reader.ReadBit() != 0;
			var midSidePresent = 0;
			if (commonWindow)
			{
				var result = DecodeIndividualChannelStreamInfo(element.Channels[0].Stream);
				if (result < 0)
					return result;
				var secondPreviousWindow = element.Channels[1].Stream.CurrentKaiserBessel;
				element.Channels[1].Stream.CopyCommonWindowFrom(element.Channels[0].Stream, secondPreviousWindow);
				midSidePresent = (int)reader.ReadBits(2);
				if (midSidePresent == 3)
					return FfmpegError.InvalidData;
				if (midSidePresent != 0)
					DecodeMidSideMask(element, midSidePresent);
			}
			var status = DecodeIndividualChannelStream(element.Channels[0], commonWindow);
			if (status < 0)
				return status;
			status = DecodeIndividualChannelStream(element.Channels[1], commonWindow);
			if (status < 0)
				return status;
			if (commonWindow && midSidePresent != 0)
				ApplyMidSideStereo(element);
			ApplyIntensityStereo(element, midSidePresent);
			return 0;
		}

		private void DecodeMidSideMask(AacChannelElement element, int present)
		{
			var stream = element.Channels[0].Stream;
			var count = stream.NumberOfWindowGroups * stream.MaximumScaleFactorBand;
			element.MaximumStereoScaleFactorBand = stream.MaximumScaleFactorBand;
			if (present == 1)
			{
				for (var index = 0; index < count; index++)
					element.MidSideMask[index] = (byte)reader.ReadBit();
			} else
			{
				for (var index = 0; index < count; index++)
					element.MidSideMask[index] = 1;
			}
		}

		/// <summary>
		/// Decodes one ICS header, band map, scale factors, optional pulse/TNS/gain syntax, and its spectral coefficients.
		/// </summary>
		private int DecodeIndividualChannelStream(AacSingleChannelElement channel, bool commonWindow)
		{
			pulse.NumberOfPulses = 0;
			var globalGain = (int)reader.ReadBits(8);
			if (!commonWindow)
			{
				var result = DecodeIndividualChannelStreamInfo(channel.Stream);
				if (result < 0)
					return result;
			}
			var status = DecodeBandTypes(channel);
			if (status < 0)
				return status;
			status = DecodeScaleFactors(channel, globalGain);
			if (status < 0)
				return status;
			DequantizeScaleFactors(channel);

			var pulsePresent = reader.ReadBit() != 0;
			if (pulsePresent)
			{
				if (channel.Stream.CurrentWindowSequence == AacWindowSequence.EightShort)
					return FfmpegError.InvalidData;
				if (DecodePulses(channel.Stream) < 0)
					return FfmpegError.InvalidData;
			}
			channel.Tns.Present = reader.ReadBit() != 0;
			if (channel.Tns.Present)
			{
				status = DecodeTemporalNoiseShaping(channel);
				if (status < 0)
					return status;
			}
			if (reader.ReadBit() != 0)
				SkipGainControl(channel.Stream);
			return DecodeSpectrumAndDequantize(channel, pulsePresent);
		}

		/// <summary>
		/// Updates the previous window state and selects FFmpeg's long or short scale-factor-band tables for this sampling index.
		/// </summary>
		private int DecodeIndividualChannelStreamInfo(AacIndividualChannelStream stream)
		{
			if (reader.ReadBit() != 0)
				return FfmpegError.InvalidData;
			stream.PreviousWindowSequence = stream.CurrentWindowSequence;
			stream.CurrentWindowSequence = (AacWindowSequence)reader.ReadBits(2);
			stream.PreviousKaiserBessel = stream.CurrentKaiserBessel;
			stream.CurrentKaiserBessel = reader.ReadBit() != 0;
			stream.PreviousNumberOfWindowGroups = Math.Max(stream.NumberOfWindowGroups, 1);
			stream.NumberOfWindowGroups = 1;
			stream.GroupLengths[0] = 1;
			if (stream.CurrentWindowSequence == AacWindowSequence.EightShort)
			{
				stream.MaximumScaleFactorBand = (int)reader.ReadBits(4);
				for (var index = 0; index < 7; index++)
				{
					if (reader.ReadBit() != 0)
					{
						stream.GroupLengths[stream.NumberOfWindowGroups - 1]++;
					} else
					{
						stream.NumberOfWindowGroups++;
						stream.GroupLengths[stream.NumberOfWindowGroups - 1] = 1;
					}
				}
				stream.NumberOfWindows = 8;
				stream.ScaleFactorBandOffsets = AacTables.ScaleFactorBandOffsets128[samplingIndex];
				stream.NumberOfScaleFactorBands = AacTables.NumberOfScaleFactorBands128[samplingIndex];
				stream.TnsMaximumBands = AacTables.TnsMaximumBands128[samplingIndex];
			} else
			{
				stream.MaximumScaleFactorBand = (int)reader.ReadBits(6);
				stream.NumberOfWindows = 1;
				stream.ScaleFactorBandOffsets = AacTables.ScaleFactorBandOffsets1024[samplingIndex];
				stream.NumberOfScaleFactorBands = AacTables.NumberOfScaleFactorBands1024[samplingIndex];
				stream.TnsMaximumBands = AacTables.TnsMaximumBands1024[samplingIndex];
				if (reader.ReadBit() != 0)
					return FfmpegError.InvalidData;
			}
			if (stream.MaximumScaleFactorBand > stream.NumberOfScaleFactorBands)
			{
				stream.MaximumScaleFactorBand = 0;
				return FfmpegError.InvalidData;
			}
			return 0;
		}

		private int DecodeBandTypes(AacSingleChannelElement channel)
		{
			var stream = channel.Stream;
			var bitCount = stream.CurrentWindowSequence == AacWindowSequence.EightShort ? 3 : 5;
			for (var group = 0; group < stream.NumberOfWindowGroups; group++)
			{
				var band = 0;
				while (band < stream.MaximumScaleFactorBand)
				{
					var sectionEnd = band;
					var bandType = (int)reader.ReadBits(4);
					if (bandType == 12)
						return FfmpegError.InvalidData;
					int increment;
					do
					{
						increment = (int)reader.ReadBits(bitCount);
						sectionEnd += increment;
						if (reader.BitsLeft < 0 || sectionEnd > stream.MaximumScaleFactorBand)
							return FfmpegError.InvalidData;
					} while (increment == (1 << bitCount) - 1);
					for (; band < sectionEnd; band++)
						channel.BandTypes[group * stream.MaximumScaleFactorBand + band] = (byte)bandType;
				}
			}
			return 0;
		}

		/// <summary>
		/// Accumulates the three independent AAC scale-factor domains and clips them at FFmpeg's source boundaries.
		/// </summary>
		private int DecodeScaleFactors(AacSingleChannelElement channel, int globalGain)
		{
			var stream = channel.Stream;
			var spectralOffset = globalGain;
			var noiseOffset = globalGain - NoiseOffset;
			var intensityOffset = 0;
			var noiseFlag = true;
			for (var group = 0; group < stream.NumberOfWindowGroups; group++)
			{
				for (var band = 0; band < stream.MaximumScaleFactorBand; band++)
				{
					var index = group * stream.MaximumScaleFactorBand + band;
					switch (channel.BandTypes[index])
					{
						case 0:
							channel.ScaleFactorOffsets[index] = 0;
							break;
						case IntensityBandType:
						case IntensityBandType2:
							intensityOffset += reader.ReadVlc(AacTables.ScaleFactorVlc.Table, 7, 3) - ScaleDifferenceZero;
							intensityOffset = Math.Clamp(intensityOffset, -155, 100);
							channel.ScaleFactorOffsets[index] = intensityOffset - 100;
							break;
						case NoiseBandType:
							if (noiseFlag)
							{
								noiseOffset += (int)reader.ReadBits(9) - NoisePre;
								noiseFlag = false;
							} else
							{
								noiseOffset += reader.ReadVlc(AacTables.ScaleFactorVlc.Table, 7, 3) - ScaleDifferenceZero;
							}
							noiseOffset = Math.Clamp(noiseOffset, -100, 155);
							channel.ScaleFactorOffsets[index] = noiseOffset;
							break;
						default:
							spectralOffset += reader.ReadVlc(AacTables.ScaleFactorVlc.Table, 7, 3) - ScaleDifferenceZero;
							if (spectralOffset < 0 || spectralOffset > 255)
								return FfmpegError.InvalidData;
							channel.ScaleFactorOffsets[index] = spectralOffset - 100;
							break;
					}
				}
			}
			return 0;
		}

		private static void DequantizeScaleFactors(AacSingleChannelElement channel)
		{
			var stream = channel.Stream;
			var index = 0;
			for (var group = 0; group < stream.NumberOfWindowGroups; group++)
			{
				for (var band = 0; band < stream.MaximumScaleFactorBand; band++, index++)
				{
					var offset = channel.ScaleFactorOffsets[index];
					switch (channel.BandTypes[index])
					{
						case 0:
							channel.ScaleFactors[index] = 0.0f;
							break;
						case IntensityBandType:
						case IntensityBandType2:
							channel.ScaleFactors[index] = AacTables.PowerTwoScaleFactors[-offset - 100 + PowerScaleFactorZero];
							break;
						case NoiseBandType:
							channel.ScaleFactors[index] = -AacTables.PowerTwoScaleFactors[offset + PowerScaleFactorZero];
							break;
						default:
							channel.ScaleFactors[index] = -AacTables.PowerTwoScaleFactors[offset + PowerScaleFactorZero];
							break;
					}
				}
			}
		}

		private int DecodePulses(AacIndividualChannelStream stream)
		{
			pulse.NumberOfPulses = (int)reader.ReadBits(2) + 1;
			var band = (int)reader.ReadBits(6);
			if (band >= stream.NumberOfScaleFactorBands)
				return FfmpegError.InvalidData;
			pulse.Positions[0] = stream.ScaleFactorBandOffsets[band] + (int)reader.ReadBits(5);
			if (pulse.Positions[0] >= stream.ScaleFactorBandOffsets[stream.NumberOfScaleFactorBands])
				return FfmpegError.InvalidData;
			pulse.Amplitudes[0] = (int)reader.ReadBits(4);
			for (var index = 1; index < pulse.NumberOfPulses; index++)
			{
				pulse.Positions[index] = pulse.Positions[index - 1] + (int)reader.ReadBits(5);
				if (pulse.Positions[index] >= stream.ScaleFactorBandOffsets[stream.NumberOfScaleFactorBands])
					return FfmpegError.InvalidData;
				pulse.Amplitudes[index] = (int)reader.ReadBits(4);
			}
			return 0;
		}

		/// <summary>
		/// Parses every window's AAC-LC TNS filters and resolves the reflection-coefficient map selected by resolution/compression.
		/// </summary>
		private int DecodeTemporalNoiseShaping(AacSingleChannelElement channel)
		{
			var stream = channel.Stream;
			var shortWindow = stream.CurrentWindowSequence == AacWindowSequence.EightShort;
			var maximumOrder = shortWindow ? 7 : 12;
			for (var window = 0; window < stream.NumberOfWindows; window++)
			{
				var filterCount = (int)reader.ReadBits(2 - (shortWindow ? 1 : 0));
				channel.Tns.NumberOfFilters[window] = filterCount;
				if (filterCount == 0)
					continue;
				var coefficientResolution = (int)reader.ReadBit();
				for (var filter = 0; filter < filterCount; filter++)
				{
					channel.Tns.Length[window, filter] = (int)reader.ReadBits(6 - 2 * (shortWindow ? 1 : 0));
					var order = (int)reader.ReadBits(5 - 2 * (shortWindow ? 1 : 0));
					channel.Tns.Order[window, filter] = order;
					if (order > maximumOrder)
					{
						channel.Tns.Order[window, filter] = 0;
						return FfmpegError.InvalidData;
					}
					if (order != 0)
					{
						channel.Tns.Direction[window, filter] = (int)reader.ReadBit();
						var compressed = (int)reader.ReadBit();
						var coefficientBits = coefficientResolution + 3 - compressed;
						var map = AacTables.TnsCoefficientMaps[2 * compressed + coefficientResolution];
						for (var index = 0; index < order; index++)
							channel.Tns.Coefficients[window, filter, index] = map[(int)reader.ReadBits(coefficientBits)];
					}
				}
			}
			return 0;
		}

		private void SkipGainControl(AacIndividualChannelStream stream)
		{
			var mode = (int)stream.CurrentWindowSequence;
			var maximumBand = (int)reader.ReadBits(2);
			for (var band = 0; band < maximumBand; band++)
			{
				for (var window = 0; window < GainModes[mode, 0]; window++)
				{
					var adjustments = (int)reader.ReadBits(3);
					for (var adjustment = 0; adjustment < adjustments; adjustment++)
						reader.SkipBits(4 + (window == 0 && GainModes[mode, 1] != 0 ? 4 : GainModes[mode, 2]));
				}
			}
		}

		/// <summary>
		/// Decodes all grouped spectral bands with FFmpeg's packed codevector symbols, sign-bit order, escape values, noise, and pulses.
		/// </summary>
		private int DecodeSpectrumAndDequantize(AacSingleChannelElement channel, bool pulsePresent)
		{
			var stream = channel.Stream;
			var coefficientWindowLength = 1024 / stream.NumberOfWindows;
			var offsets = stream.ScaleFactorBandOffsets;
			for (var window = 0; window < stream.NumberOfWindows; window++)
				Array.Clear(channel.Coefficients, window * 128 + offsets[stream.MaximumScaleFactorBand], coefficientWindowLength - offsets[stream.MaximumScaleFactorBand]);

			var scaleFactorIndex = 0;
			var coefficientBase = 0;
			for (var group = 0; group < stream.NumberOfWindowGroups; group++)
			{
				var groupLength = stream.GroupLengths[group];
				for (var band = 0; band < stream.MaximumScaleFactorBand; band++, scaleFactorIndex++)
				{
					var bandType = channel.BandTypes[scaleFactorIndex];
					var bandOffset = offsets[band];
					var bandLength = offsets[band + 1] - bandOffset;
					if (bandType == 0 || bandType >= IntensityBandType2)
					{
						for (var window = 0; window < groupLength; window++)
							Array.Clear(channel.Coefficients, coefficientBase + window * 128 + bandOffset, bandLength);
					} else if (bandType == NoiseBandType)
					{
						for (var window = 0; window < groupLength; window++)
						{
							var destination = coefficientBase + window * 128 + bandOffset;
							var energy = 0.0f;
							for (var index = 0; index < bandLength; index++)
							{
								randomState = unchecked((int)(unchecked((uint)randomState * 1664525u) + 1013904223u));
								channel.Coefficients[destination + index] = randomState;
							}
							for (var index = 0; index < bandLength; index++)
								energy += channel.Coefficients[destination + index] * channel.Coefficients[destination + index];
							var scale = channel.ScaleFactors[scaleFactorIndex] / MathF.Sqrt(energy);
							for (var index = 0; index < bandLength; index++)
								channel.Coefficients[destination + index] *= scale;
						}
					} else
					{
						var codebook = bandType - 1;
						for (var window = 0; window < groupLength; window++)
						{
							var status = DecodeSpectralBand(
								channel.Coefficients,
								coefficientBase + window * 128 + bandOffset,
								bandLength,
								codebook,
								channel.ScaleFactors[scaleFactorIndex]);
							if (status < 0)
								return status;
						}
					}
				}
				coefficientBase += groupLength << 7;
			}

			if (pulsePresent)
			{
				var band = 0;
				for (var index = 0; index < pulse.NumberOfPulses; index++)
				{
					var position = pulse.Positions[index];
					var coefficient = channel.Coefficients[position];
					while (offsets[band + 1] <= position)
						band++;
					if (channel.BandTypes[band] != NoiseBandType && channel.ScaleFactors[band] != 0.0f)
					{
						var adjusted = (float)-pulse.Amplitudes[index];
						if (coefficient != 0.0f)
						{
							coefficient /= channel.ScaleFactors[band];
							adjusted = coefficient / MathF.Sqrt(MathF.Sqrt(MathF.Abs(coefficient))) +
								(coefficient > 0.0f ? -adjusted : adjusted);
						}
						channel.Coefficients[position] = MathF.Cbrt(MathF.Abs(adjusted)) * adjusted * channel.ScaleFactors[band];
					}
				}
			}
			return 0;
		}

		/// <summary>
		/// Expands one AAC spectral codebook band while preserving packed nibbles, nonzero-mask sign consumption, and escape ordering.
		/// </summary>
		private int DecodeSpectralBand(float[] coefficients, int offset, int length, int codebook, float scale)
		{
			var bitReader = reader.OpenLocal();
			// Every decode-error return below closes this OPEN_READER state before leaving the method.
			var values = AacTables.SpectralVectorValues[codebook];
			var vectorSizeClass = codebook >> 1;
			if (vectorSizeClass == 0)
			{
				for (var remaining = length; remaining != 0; remaining -= 4)
				{
					if (!ReadSpectralSymbol(ref bitReader, codebook, out var symbol))
					{
						bitReader.Close();
						return FfmpegError.InvalidData;
					}
					coefficients[offset++] = values[symbol & 3] * scale;
					coefficients[offset++] = values[symbol >> 2 & 3] * scale;
					coefficients[offset++] = values[symbol >> 4 & 3] * scale;
					coefficients[offset++] = values[symbol >> 6 & 3] * scale;
				}
			} else if (vectorSizeClass == 1)
			{
				for (var remaining = length; remaining != 0; remaining -= 4)
				{
					if (!ReadSpectralSymbol(ref bitReader, codebook, out var symbol))
					{
						bitReader.Close();
						return FfmpegError.InvalidData;
					}
					var nonzeroCount = symbol >> 8 & 15;
					var nonzeroMask = symbol >> 12;
					var signs = nonzeroCount != 0 ? bitReader.ReadBits(nonzeroCount) : 0;
					var signIndex = nonzeroCount - 1;
					for (var component = 0; component < 4; component++)
					{
						var signedScale = scale;
						if ((nonzeroMask & 1 << component) != 0)
						{
							if (((signs >> signIndex) & 1) != 0)
								signedScale = ToggleSign(signedScale);
							signIndex--;
						}
						coefficients[offset++] = values[symbol >> (2 * component) & 3] * signedScale;
					}
				}
			} else if (vectorSizeClass == 2)
			{
				for (var remaining = length; remaining != 0; remaining -= 2)
				{
					if (!ReadSpectralSymbol(ref bitReader, codebook, out var symbol))
					{
						bitReader.Close();
						return FfmpegError.InvalidData;
					}
					coefficients[offset++] = values[symbol & 15] * scale;
					coefficients[offset++] = values[symbol >> 4 & 15] * scale;
				}
			} else if (vectorSizeClass == 3 || vectorSizeClass == 4)
			{
				for (var remaining = length; remaining != 0; remaining -= 2)
				{
					if (!ReadSpectralSymbol(ref bitReader, codebook, out var symbol))
					{
						bitReader.Close();
						return FfmpegError.InvalidData;
					}
					var nonzeroCount = symbol >> 8 & 15;
					var signs = nonzeroCount != 0 ? bitReader.ReadBits(nonzeroCount) << (symbol >> 12) : 0;
					var firstScale = (signs & 2) != 0 ? ToggleSign(scale) : scale;
					var secondScale = (signs & 1) != 0 ? ToggleSign(scale) : scale;
					coefficients[offset++] = values[symbol & 15] * firstScale;
					coefficients[offset++] = values[symbol >> 4 & 15] * secondScale;
				}
			} else
			{
				var start = offset;
				for (var remaining = length; remaining != 0; remaining -= 2)
				{
					if (!ReadSpectralSymbol(ref bitReader, codebook, out var symbol))
					{
						bitReader.Close();
						return FfmpegError.InvalidData;
					}
					if (symbol == 0)
					{
						coefficients[offset++] = 0.0f;
						coefficients[offset++] = 0.0f;
						continue;
					}
					var signCount = symbol >> 12;
					var escapeMask = symbol >> 8;
					var signs = signCount != 0 ? bitReader.ReadBits(signCount) : 0;
					var signIndex = signCount - 1;
					for (var component = 0; component < 2; component++)
					{
						float value;
						if ((escapeMask & 1 << component) != 0)
						{
							var leadingOnes = 0;
							while (bitReader.ReadBit() != 0)
							{
								leadingOnes++;
								if (leadingOnes > 8)
								{
									bitReader.Close();
									return FfmpegError.InvalidData;
								}
							}
							var bitCount = leadingOnes + 4;
							var magnitude = (1 << bitCount) + (int)bitReader.ReadBits(bitCount);
							value = AacTables.CubeRootTable[magnitude];
						} else
						{
							value = values[symbol & 15];
						}
						if (value != 0.0f)
						{
							if (((signs >> signIndex) & 1) != 0)
								value = ToggleSign(value);
							signIndex--;
						}
						coefficients[offset++] = value;
						symbol >>= 4;
					}
				}
				for (var index = 0; index < length; index++)
					coefficients[start + index] *= scale;
			}
			bitReader.Close();
			return 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool ReadSpectralSymbol(ref BitReader.BitReaderLocal bitReader, int codebook, out int symbol)
		{
			var value = bitReader.ReadVlc(AacTables.SpectralVlcs[codebook].Table, 8, 2);
			if (value == -1)
			{
				symbol = 0;
				return false;
			}
			symbol = unchecked((ushort)value);
			return true;
		}

		private static float ToggleSign(float value)
		{
			return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value) ^ int.MinValue);
		}

		/// <summary>
		/// Applies FFmpeg's in-place mid/side butterfly only to bands allowed by both channels' band types.
		/// </summary>
		private static void ApplyMidSideStereo(AacChannelElement element)
		{
			var stream = element.Channels[0].Stream;
			var left = element.Channels[0].Coefficients;
			var right = element.Channels[1].Coefficients;
			var coefficientBase = 0;
			for (var group = 0; group < stream.NumberOfWindowGroups; group++)
			{
				for (var band = 0; band < element.MaximumStereoScaleFactorBand; band++)
				{
					var index = group * element.MaximumStereoScaleFactorBand + band;
					if (element.MidSideMask[index] != 0 && element.Channels[0].BandTypes[index] < NoiseBandType &&
						element.Channels[1].BandTypes[index] < NoiseBandType)
					{
						for (var window = 0; window < stream.GroupLengths[group]; window++)
						{
							var start = coefficientBase + window * 128 + stream.ScaleFactorBandOffsets[band];
							var length = stream.ScaleFactorBandOffsets[band + 1] - stream.ScaleFactorBandOffsets[band];
							for (var coefficient = 0; coefficient < length; coefficient++)
							{
								var difference = left[start + coefficient] - right[start + coefficient];
								left[start + coefficient] += right[start + coefficient];
								right[start + coefficient] = difference;
							}
						}
					}
				}
				coefficientBase += stream.GroupLengths[group] * 128;
			}
		}

		/// <summary>
		/// Reconstructs intensity-coded right-channel bands from the left channel with the optional mid/side polarity inversion.
		/// </summary>
		private static void ApplyIntensityStereo(AacChannelElement element, int midSidePresent)
		{
			var rightChannel = element.Channels[1];
			var stream = rightChannel.Stream;
			var left = element.Channels[0].Coefficients;
			var right = rightChannel.Coefficients;
			var coefficientBase = 0;
			for (var group = 0; group < stream.NumberOfWindowGroups; group++)
			{
				for (var band = 0; band < stream.MaximumScaleFactorBand; band++)
				{
					var index = group * stream.MaximumScaleFactorBand + band;
					var bandType = rightChannel.BandTypes[index];
					if (bandType == IntensityBandType || bandType == IntensityBandType2)
					{
						var polarity = -1 + 2 * (bandType - IntensityBandType2);
						if (midSidePresent != 0)
							polarity *= 1 - 2 * element.MidSideMask[index];
						var scale = polarity * rightChannel.ScaleFactors[index];
						for (var window = 0; window < stream.GroupLengths[group]; window++)
						{
							var start = coefficientBase + window * 128 + stream.ScaleFactorBandOffsets[band];
							var length = stream.ScaleFactorBandOffsets[band + 1] - stream.ScaleFactorBandOffsets[band];
							for (var coefficient = 0; coefficient < length; coefficient++)
								right[start + coefficient] = left[start + coefficient] * scale;
						}
					}
				}
				coefficientBase += stream.GroupLengths[group] * 128;
			}
		}

		/// <summary>
		/// Builds each reflection filter's LPC coefficients and applies the source-order all-pole TNS recurrence in place.
		/// </summary>
		private void ApplyTemporalNoiseShaping(AacSingleChannelElement channel)
		{
			var stream = channel.Stream;
			var maximumBand = Math.Min(stream.TnsMaximumBands, stream.MaximumScaleFactorBand);
			if (maximumBand == 0)
				return;
			for (var window = 0; window < stream.NumberOfWindows; window++)
			{
				var bottom = stream.NumberOfScaleFactorBands;
				for (var filter = 0; filter < channel.Tns.NumberOfFilters[window]; filter++)
				{
					var top = bottom;
					bottom = Math.Max(0, top - channel.Tns.Length[window, filter]);
					var order = channel.Tns.Order[window, filter];
					if (order == 0)
						continue;
					for (var index = 0; index < order; index++)
					{
						var reflection = -channel.Tns.Coefficients[window, filter, index];
						lpc[index] = reflection;
						for (var pair = 0; pair < (index + 1) >> 1; pair++)
						{
							var forward = lpc[pair];
							var backward = lpc[index - 1 - pair];
							lpc[pair] = forward + reflection * backward;
							lpc[index - 1 - pair] = backward + reflection * forward;
						}
					}
					var start = (int)stream.ScaleFactorBandOffsets[Math.Min(bottom, maximumBand)];
					var end = (int)stream.ScaleFactorBandOffsets[Math.Min(top, maximumBand)];
					var size = end - start;
					if (size <= 0)
						continue;
					var increment = 1;
					if (channel.Tns.Direction[window, filter] != 0)
					{
						increment = -1;
						start = end - 1;
					}
					start += window * 128;
					for (var coefficient = 0; coefficient < size; coefficient++, start += increment)
					{
						for (var tap = 1; tap <= Math.Min(coefficient, order); tap++)
							channel.Coefficients[start] -= channel.Coefficients[start - tap * increment] * lpc[tap - 1];
					}
				}
			}
		}

		/// <summary>
		/// Executes the scalar long/short inverse MDCTs and reproduces FFmpeg's four transition-specific overlap branches.
		/// </summary>
		private void ImdctAndWindow(AacSingleChannelElement channel)
		{
			var stream = channel.Stream;
			var shortWindow = stream.CurrentKaiserBessel ? AacTables.KaiserBessel128 : AacTables.Sine128;
			var previousLongWindow = stream.PreviousKaiserBessel ? AacTables.KaiserBessel1024 : AacTables.Sine1024;
			var previousShortWindow = stream.PreviousKaiserBessel ? AacTables.KaiserBessel128 : AacTables.Sine128;
			if (stream.CurrentWindowSequence == AacWindowSequence.EightShort)
			{
				for (var offset = 0; offset < 1024; offset += 128)
					mdct128.Transform(channel.Coefficients.AsSpan(offset, 128), mdctBuffer.AsSpan(offset, 128));
			} else
			{
				mdct1024.Transform(channel.Coefficients, mdctBuffer);
			}

			var previousLong = stream.PreviousWindowSequence == AacWindowSequence.OnlyLong ||
				stream.PreviousWindowSequence == AacWindowSequence.LongStop;
			var currentLong = stream.CurrentWindowSequence == AacWindowSequence.OnlyLong ||
				stream.CurrentWindowSequence == AacWindowSequence.LongStart;
			if (previousLong && currentLong)
			{
				VectorMultiplyWindow(channel.Output, 0, channel.Saved, 0, mdctBuffer, 0, previousLongWindow, 512);
			} else
			{
				Array.Copy(channel.Saved, 0, channel.Output, 0, 448);
				if (stream.CurrentWindowSequence == AacWindowSequence.EightShort)
				{
					VectorMultiplyWindow(channel.Output, 448, channel.Saved, 448, mdctBuffer, 0, previousShortWindow, 64);
					VectorMultiplyWindow(channel.Output, 576, mdctBuffer, 64, mdctBuffer, 128, shortWindow, 64);
					VectorMultiplyWindow(channel.Output, 704, mdctBuffer, 192, mdctBuffer, 256, shortWindow, 64);
					VectorMultiplyWindow(channel.Output, 832, mdctBuffer, 320, mdctBuffer, 384, shortWindow, 64);
					VectorMultiplyWindow(temporary, 0, mdctBuffer, 448, mdctBuffer, 512, shortWindow, 64);
					Array.Copy(temporary, 0, channel.Output, 960, 64);
				} else
				{
					VectorMultiplyWindow(channel.Output, 448, channel.Saved, 448, mdctBuffer, 0, previousShortWindow, 64);
					Array.Copy(mdctBuffer, 64, channel.Output, 576, 448);
				}
			}

			if (stream.CurrentWindowSequence == AacWindowSequence.EightShort)
			{
				Array.Copy(temporary, 64, channel.Saved, 0, 64);
				VectorMultiplyWindow(channel.Saved, 64, mdctBuffer, 576, mdctBuffer, 640, shortWindow, 64);
				VectorMultiplyWindow(channel.Saved, 192, mdctBuffer, 704, mdctBuffer, 768, shortWindow, 64);
				VectorMultiplyWindow(channel.Saved, 320, mdctBuffer, 832, mdctBuffer, 896, shortWindow, 64);
				Array.Copy(mdctBuffer, 960, channel.Saved, 448, 64);
			} else if (stream.CurrentWindowSequence == AacWindowSequence.LongStart)
			{
				Array.Copy(mdctBuffer, 512, channel.Saved, 0, 448);
				Array.Copy(mdctBuffer, 960, channel.Saved, 448, 64);
			} else
			{
				Array.Copy(mdctBuffer, 512, channel.Saved, 0, 512);
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

		private int SkipDataStreamElement()
		{
			var byteAlign = reader.ReadBit() != 0;
			var count = (int)reader.ReadBits(8);
			if (count == 255)
				count += (int)reader.ReadBits(8);
			if (byteAlign)
				reader.Align();
			if (reader.BitsLeft < 8 * count)
				return FfmpegError.InvalidData;
			reader.SkipBits(8 * count);
			return 0;
		}

		/// <summary>
		/// Dispatches SBR fill payloads to the preceding audio element and preserves FFmpeg's legacy libfaac delay marker.
		/// </summary>
		private int DecodeFillElement(int count, AacChannelElement previousElement, AacElementType previousElementType)
		{
			if (count == 15)
				count += (int)reader.ReadBits(8) - 1;
			if (count < 0 || reader.BitsLeft < 8 * count)
				return FfmpegError.InvalidData;
			var bits = 8 * count;
			if (bits < 4)
			{
				reader.SkipBits(bits);
				return 0;
			}
			var extensionType = (int)reader.ReadBits(4);
			bits -= 4;
			if ((extensionType == 13 || extensionType == 14) && previousElement != null && sbrMode != 0)
			{
				sbrMode = 1;
				if (psMode == -1 && channelConfiguration == 1)
					EnableParametricStereoOutput();
				if (extensionSampleRate == 0)
					extensionSampleRate = 2 * coreSampleRate;
				sampleRate = extensionSampleRate;
				return sbrBitstream.DecodeExtension(previousElement.Sbr, reader, extensionType == 14, count,
					previousElementType, coreSampleRate, psMode != 0);
			}
			if (extensionType != 0 || bits < 13 + 7 * 8)
			{
				reader.SkipBits(bits);
				return 0;
			}
			reader.SkipBits(13);
			bits -= 13;
			var length = 0;
			while (length + 1 < fillBuffer.Length && bits >= 8)
			{
				fillBuffer[length++] = (byte)reader.ReadBits(8);
				bits -= 8;
			}
			fillBuffer[length] = 0;
			if (IsLibFaacVersion(fillBuffer, length))
				skipSamples = 1024;
			reader.SkipBits(bits);
			return 0;
		}

		private static bool IsLibFaacVersion(byte[] value, int length)
		{
			if (length < 11 || value[0] != (byte)'l' || value[1] != (byte)'i' || value[2] != (byte)'b' || value[3] != (byte)'f' ||
				value[4] != (byte)'a' || value[5] != (byte)'a' || value[6] != (byte)'c' || value[7] != (byte)' ')
				return false;
			var index = 8;
			if (value[index] == (byte)'+' || value[index] == (byte)'-')
				index++;
			var digits = 0;
			while (index < length && value[index] >= (byte)'0' && value[index] <= (byte)'9')
			{
				index++;
				digits++;
			}
			if (digits == 0 || index >= length || value[index++] != (byte)'.')
				return false;
			if (index < length && (value[index] == (byte)'+' || value[index] == (byte)'-'))
				index++;
			digits = 0;
			while (index < length && value[index] >= (byte)'0' && value[index] <= (byte)'9')
			{
				index++;
				digits++;
			}
			return digits != 0;
		}
	}
}
