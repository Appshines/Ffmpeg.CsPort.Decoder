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
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>
	/// Ports FFmpeg's LOAS/LATM AAC wrapper, including StreamMuxConfig, in-band AudioSpecificConfig, payload lengths, and LC delegation.
	/// </summary>
	public sealed class AacLatmDecoder
	{
		private const int LoasSyncWord = 0x2b7;
		private readonly BitReader reader = new BitReader();
		private readonly BitReader configReader = new BitReader();
		private readonly byte[] extraData = new byte[16];
		private readonly byte[] previousExtraData = new byte[16];
		private readonly byte[] payload = new byte[8192];
		private AacLcDecoder decoder;
		private byte[] currentPacket;
		private int currentPacketOffset;
		private int currentPacketLength;
		private int previousExtraDataLength;
		private int audioMuxVersionA;
		private int frameLengthType;
		private int frameLength;

		public int Channels => decoder == null ? 0 : decoder.Channels;
		public int SampleRate => decoder == null ? 0 : decoder.SampleRate;

		private AacLatmDecoder()
		{
		}

		/// <summary>Creates a LATM decoder and optionally primes its AAC core from out-of-band AudioSpecificConfig bytes.</summary>
		public static int Initialize(byte[] codecExtraData, out AacLatmDecoder result)
		{
			var value = new AacLatmDecoder();
			if (codecExtraData != null && codecExtraData.Length != 0)
			{
				var status = AacLcDecoder.Initialize(codecExtraData, out value.decoder);
				if (status < 0)
				{
					result = null;
					return status;
				}
			}
			result = value;
			return 0;
		}

		/// <summary>
		/// Decodes one complete LOAS frame, applies an in-band mux configuration when present, and returns FFmpeg's three-byte-inclusive mux length.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength <= 0 || packetLength > packet.Length - packetOffset ||
				reader.Initialize(packet, packetOffset, packetLength * 8) < 0)
				return FfmpegError.InvalidArgument;
			currentPacket = packet;
			currentPacketOffset = packetOffset;
			currentPacketLength = packetLength;
			if (reader.BitsLeft < 24 || reader.ReadBits(11) != LoasSyncWord)
				return FfmpegError.InvalidData;
			var muxLength = (int)reader.ReadBits(13) + 3;
			if (muxLength > packetLength)
				return FfmpegError.InvalidData;
			var status = ReadAudioMuxElement(out var payloadLength);
			if (status != 0)
				return status < 0 ? status : packetLength;
			if (decoder == null)
				return packetLength;
			if (payloadLength < 0 || payloadLength > payload.Length || payloadLength * 8 > reader.BitsLeft)
				return FfmpegError.InvalidData;
			for (var index = 0; index < payloadLength; index++)
				payload[index] = (byte)reader.ReadBits(8);
			if (payloadLength >= 2 && (payload[0] << 4 | payload[1] >> 4) == 0xfff)
				return FfmpegError.InvalidData;
			status = decoder.DecodeFrame(payload, 0, payloadLength, output, out frame);
			if (status < 0)
				return status;
			return muxLength;
		}

		private int ReadAudioMuxElement(out int payloadLength)
		{
			payloadLength = 0;
			var useSameMux = reader.ReadBit() != 0;
			if (!useSameMux)
			{
				var status = ReadStreamMuxConfig();
				if (status < 0)
					return status;
			} else if (decoder == null)
			{
				return 1;
			}
			if (audioMuxVersionA != 0)
				return 0;
			payloadLength = ReadPayloadLengthInfo();
			if (payloadLength < 0 || (long)payloadLength * 8 > reader.BitsLeft || (long)payloadLength * 8 + 256 < reader.BitsLeft)
				return FfmpegError.InvalidData;
			return 0;
		}

		/// <summary>
		/// Parses the single-program, single-layer StreamMuxConfig path and preserves FFmpeg's frame-length and optional-data consumption order.
		/// </summary>
		private int ReadStreamMuxConfig()
		{
			var audioMuxVersion = (int)reader.ReadBit();
			audioMuxVersionA = audioMuxVersion != 0 ? (int)reader.ReadBit() : 0;
			if (audioMuxVersionA != 0)
				return 0;
			if (audioMuxVersion != 0)
				ReadLatmValue();
			reader.SkipBits(1);
			reader.SkipBits(6);
			if (reader.ReadBits(4) != 0)
				return FfmpegError.PatchWelcome;
			if (reader.ReadBits(3) != 0)
				return FfmpegError.PatchWelcome;
			var status = DecodeAudioSpecificConfig(audioMuxVersion != 0 ? (int)ReadLatmValue() : 0);
			if (status < 0)
				return status;
			frameLengthType = (int)reader.ReadBits(3);
			switch (frameLengthType)
			{
				case 0:
					reader.SkipBits(8);
					break;
				case 1:
					frameLength = (int)reader.ReadBits(9);
					break;
				case 3:
				case 4:
				case 5:
					reader.SkipBits(6);
					break;
				case 6:
				case 7:
					reader.SkipBits(1);
					break;
			}
			if (reader.ReadBit() != 0)
			{
				if (audioMuxVersion != 0)
				{
					ReadLatmValue();
				} else
				{
					int escape;
					do
					{
						if (reader.BitsLeft < 9)
							return FfmpegError.InvalidData;
						escape = (int)reader.ReadBit();
						reader.SkipBits(8);
					} while (escape != 0);
				}
			}
			if (reader.ReadBit() != 0)
				reader.SkipBits(8);
			return reader.BitsLeft < 0 ? FfmpegError.InvalidData : 0;
		}

		/// <summary>
		/// Determines the LC AudioSpecificConfig bit length, copies its potentially unaligned bytes, and replaces state only when the configuration changes.
		/// </summary>
		private int DecodeAudioSpecificConfig(int declaredLength)
		{
			var start = reader.Position;
			var available = reader.BitsLeft;
			if (available <= 0 || declaredLength < 0)
				return FfmpegError.InvalidData;
			if (configReader.Initialize(currentPacket, currentPacketOffset, currentPacketLength * 8) < 0)
				return FfmpegError.InvalidData;
			configReader.Seek(start);
			var objectType = ReadObjectType(configReader);
			var samplingIndex = (int)configReader.ReadBits(4);
			if (samplingIndex == 15)
				configReader.SkipBits(24);
			configReader.SkipBits(4);
			if (objectType == 5 || objectType == 29)
				return FfmpegError.NotImplemented;
			if (objectType != 2)
				return FfmpegError.NotImplemented;
			if (configReader.ReadBit() != 0)
				return FfmpegError.NotImplemented;
			if (configReader.ReadBit() != 0)
				configReader.SkipBits(14);
			configReader.ReadBit();
			var consumed = configReader.Position - start;
			var configBits = declaredLength == 0 ? consumed : Math.Min(declaredLength, available);
			if (consumed > configBits || configBits <= 0 || configBits > extraData.Length * 8)
				return FfmpegError.InvalidData;
			configReader.Seek(start);
			Array.Clear(extraData, 0, extraData.Length);
			var byteLength = (configBits + 7) / 8;
			for (var index = 0; index < byteLength; index++)
			{
				var bits = Math.Min(8, configBits - index * 8);
				extraData[index] = (byte)(configReader.ReadBits(bits) << (8 - bits));
			}
			var changed = byteLength != previousExtraDataLength;
			for (var index = 0; !changed && index < byteLength; index++)
				changed = extraData[index] != previousExtraData[index];
			if (changed || decoder == null)
			{
				var status = AacLcDecoder.Initialize(extraData, byteLength, out var configuredDecoder);
				if (status < 0)
					return status;
				decoder = configuredDecoder;
				previousExtraDataLength = byteLength;
				for (var index = 0; index < byteLength; index++)
					previousExtraData[index] = extraData[index];
			}
			reader.SkipBits(configBits);
			return 0;
		}

		private static int ReadObjectType(BitReader source)
		{
			var value = (int)source.ReadBits(5);
			return value == 31 ? 32 + (int)source.ReadBits(6) : value;
		}

		private uint ReadLatmValue()
		{
			var length = (int)reader.ReadBits(2);
			return reader.ReadBitsLong((length + 1) * 8);
		}

		private int ReadPayloadLengthInfo()
		{
			if (frameLengthType == 0)
			{
				var length = 0;
				int value;
				do
				{
					if (reader.BitsLeft < 8)
						return FfmpegError.InvalidData;
					value = (int)reader.ReadBits(8);
					length += value;
				} while (value == 255);
				return length;
			}
			if (frameLengthType == 1)
				return frameLength;
			if (frameLengthType == 3 || frameLengthType == 5 || frameLengthType == 7)
				reader.SkipBits(2);
			return 0;
		}
	}
}
