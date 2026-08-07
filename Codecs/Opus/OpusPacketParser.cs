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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>Ports FFmpeg's Opus packet framing parser and duration/mode derivation.</summary>
	internal static class OpusPacketParser
	{
		private static readonly ushort[] FrameDurations =
		{
			480, 960, 1920, 2880, 480, 960, 1920, 2880,
			480, 960, 1920, 2880, 480, 960, 480, 960,
			120, 240, 480, 960, 120, 240, 480, 960,
			120, 240, 480, 960, 120, 240, 480, 960
		};

		/// <summary>Splits all four Opus framing codes, including self-delimited and padded VBR packets.</summary>
		public static int Parse(OpusPacket packet, byte[] buffer, int offset, int size, bool selfDelimiting)
		{
			packet.Clear();
			if (buffer == null || offset < 0 || size < 1 || size > buffer.Length - offset)
				return FfmpegError.InvalidData;
			var pointer = offset;
			var end = offset + size;
			var padding = 0;
			var value = (int)buffer[pointer++];
			packet.Code = value & 3;
			packet.Stereo = value >> 2 & 1;
			packet.Configuration = value >> 3 & 31;
			if (packet.Code >= 2 && size < 2)
				return Fail(packet);

			var frameBytes = 0;
			switch (packet.Code)
			{
				case 0:
					packet.FrameCount = 1;
					if (selfDelimiting)
					{
						frameBytes = ReadLacing16(buffer, ref pointer, end);
						if (frameBytes < 0 || frameBytes > end - pointer)
							return Fail(packet);
						end = pointer + frameBytes;
						size = end - offset;
					}
					frameBytes = end - pointer;
					if (frameBytes > OpusPacket.MaximumFrameSize)
						return Fail(packet);
					packet.FrameOffsets[0] = pointer - offset;
					packet.FrameSizes[0] = frameBytes;
					break;
				case 1:
					packet.FrameCount = 2;
					if (selfDelimiting)
					{
						frameBytes = ReadLacing16(buffer, ref pointer, end);
						if (frameBytes < 0 || 2 * frameBytes > end - pointer)
							return Fail(packet);
						end = pointer + 2 * frameBytes;
						size = end - offset;
					}
					frameBytes = end - pointer;
					if ((frameBytes & 1) != 0 || frameBytes >> 1 > OpusPacket.MaximumFrameSize)
						return Fail(packet);
					packet.FrameOffsets[0] = pointer - offset;
					packet.FrameSizes[0] = frameBytes >> 1;
					packet.FrameOffsets[1] = packet.FrameOffsets[0] + packet.FrameSizes[0];
					packet.FrameSizes[1] = frameBytes >> 1;
					break;
				case 2:
					packet.FrameCount = 2;
					packet.VariableBitRate = 1;
					frameBytes = ReadLacing16(buffer, ref pointer, end);
					if (frameBytes < 0)
						return Fail(packet);
					if (selfDelimiting)
					{
						var length = ReadLacing16(buffer, ref pointer, end);
						if (length < 0 || length + frameBytes > end - pointer)
							return Fail(packet);
						end = pointer + frameBytes + length;
						size = end - offset;
					}
					packet.FrameOffsets[0] = pointer - offset;
					packet.FrameSizes[0] = frameBytes;
					frameBytes = end - pointer - packet.FrameSizes[0];
					if (frameBytes < 0 || frameBytes > OpusPacket.MaximumFrameSize)
						return Fail(packet);
					packet.FrameOffsets[1] = packet.FrameOffsets[0] + packet.FrameSizes[0];
					packet.FrameSizes[1] = frameBytes;
					break;
				case 3:
					value = buffer[pointer++];
					packet.FrameCount = value & 63;
					padding = value >> 6 & 1;
					packet.VariableBitRate = value >> 7 & 1;
					if (packet.FrameCount == 0 || packet.FrameCount > OpusPacket.MaximumFrames)
						return Fail(packet);
					if (padding != 0)
					{
						padding = ReadLacingFull(buffer, ref pointer, end);
						if (padding < 0)
							return Fail(packet);
					}
					if (packet.VariableBitRate != 0)
					{
						var totalBytes = 0;
						for (var index = 0; index < packet.FrameCount - 1; index++)
						{
							frameBytes = ReadLacing16(buffer, ref pointer, end);
							if (frameBytes < 0)
								return Fail(packet);
							packet.FrameSizes[index] = frameBytes;
							totalBytes += frameBytes;
						}
						if (selfDelimiting)
						{
							var length = ReadLacing16(buffer, ref pointer, end);
							if (length < 0 || length + totalBytes + padding > end - pointer)
								return Fail(packet);
							end = pointer + totalBytes + length + padding;
							size = end - offset;
						}
						frameBytes = end - pointer - padding;
						if (totalBytes > frameBytes)
							return Fail(packet);
						packet.FrameOffsets[0] = pointer - offset;
						for (var index = 1; index < packet.FrameCount; index++)
							packet.FrameOffsets[index] = packet.FrameOffsets[index - 1] + packet.FrameSizes[index - 1];
						packet.FrameSizes[packet.FrameCount - 1] = frameBytes - totalBytes;
					} else
					{
						if (selfDelimiting)
						{
							frameBytes = ReadLacing16(buffer, ref pointer, end);
							if (frameBytes < 0 || packet.FrameCount * frameBytes + padding > end - pointer)
								return Fail(packet);
							end = pointer + packet.FrameCount * frameBytes + padding;
							size = end - offset;
						} else
						{
							frameBytes = end - pointer - padding;
							if (frameBytes % packet.FrameCount != 0 || frameBytes / packet.FrameCount > OpusPacket.MaximumFrameSize)
								return Fail(packet);
							frameBytes /= packet.FrameCount;
						}
						packet.FrameOffsets[0] = pointer - offset;
						packet.FrameSizes[0] = frameBytes;
						for (var index = 1; index < packet.FrameCount; index++)
						{
							packet.FrameOffsets[index] = packet.FrameOffsets[index - 1] + packet.FrameSizes[index - 1];
							packet.FrameSizes[index] = frameBytes;
						}
					}
					break;
			}

			packet.PacketSize = size;
			packet.DataSize = size - padding;
			packet.FrameDuration = FrameDurations[packet.Configuration];
			if (packet.FrameDuration * packet.FrameCount > OpusPacket.MaximumPacketDuration)
				return Fail(packet);
			if (packet.Configuration < 12)
			{
				packet.Mode = OpusMode.Silk;
				packet.Bandwidth = (OpusBandwidth)(packet.Configuration >> 2);
			} else if (packet.Configuration < 16)
			{
				packet.Mode = OpusMode.Hybrid;
				packet.Bandwidth = OpusBandwidth.Superwideband + (packet.Configuration >= 14 ? 1 : 0);
			} else
			{
				packet.Mode = OpusMode.Celt;
				var bandwidth = (packet.Configuration - 16) >> 2;
				if (bandwidth != 0)
					bandwidth++;
				packet.Bandwidth = (OpusBandwidth)bandwidth;
			}
			return 0;
		}

		private static int ReadLacing16(byte[] buffer, ref int pointer, int end)
		{
			if (pointer >= end)
				return FfmpegError.InvalidData;
			var value = (int)buffer[pointer++];
			if (value >= 252)
			{
				if (pointer >= end)
					return FfmpegError.InvalidData;
				value += 4 * buffer[pointer++];
			}
			return value;
		}

		private static int ReadLacingFull(byte[] buffer, ref int pointer, int end)
		{
			var value = 0;
			while (true)
			{
				if (pointer >= end || value > int.MaxValue - 254)
					return FfmpegError.InvalidData;
				var next = buffer[pointer++];
				value += next;
				if (next < 255)
					return value;
				value--;
			}
		}

		private static int Fail(OpusPacket packet)
		{
			packet.Clear();
			return FfmpegError.InvalidData;
		}
	}
}
