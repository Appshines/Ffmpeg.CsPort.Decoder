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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Formats
{
	/// <summary>
	/// Ports FFmpeg's PCM codec selection, sample-width, and default packet-size helpers used by audio demuxers.
	/// </summary>
	internal static class PcmFormat
	{
		public static AudioCodecId GetCodecId(int bitsPerSample, bool floatingPoint, bool bigEndian, int signedFlags)
		{
			if (bitsPerSample <= 0 || bitsPerSample > 64)
				return AudioCodecId.None;

			if (floatingPoint)
			{
				if (bitsPerSample == 32)
					return bigEndian ? AudioCodecId.PcmF32BigEndian : AudioCodecId.PcmF32LittleEndian;
				if (bitsPerSample == 64)
					return bigEndian ? AudioCodecId.PcmF64BigEndian : AudioCodecId.PcmF64LittleEndian;
				return AudioCodecId.None;
			}

			var bytesPerSample = (bitsPerSample + 7) >> 3;
			var signed = (signedFlags & (1 << (bytesPerSample - 1))) != 0;
			if (signed)
			{
				switch (bytesPerSample)
				{
					case 1: return AudioCodecId.PcmS8;
					case 2: return bigEndian ? AudioCodecId.PcmS16BigEndian : AudioCodecId.PcmS16LittleEndian;
					case 3: return bigEndian ? AudioCodecId.PcmS24BigEndian : AudioCodecId.PcmS24LittleEndian;
					case 4: return bigEndian ? AudioCodecId.PcmS32BigEndian : AudioCodecId.PcmS32LittleEndian;
					case 8: return bigEndian ? AudioCodecId.PcmS64BigEndian : AudioCodecId.PcmS64LittleEndian;
				}
			} else
			{
				switch (bytesPerSample)
				{
					case 1: return AudioCodecId.PcmU8;
					case 2: return bigEndian ? AudioCodecId.PcmU16BigEndian : AudioCodecId.PcmU16LittleEndian;
					case 3: return bigEndian ? AudioCodecId.PcmU24BigEndian : AudioCodecId.PcmU24LittleEndian;
					case 4: return bigEndian ? AudioCodecId.PcmU32BigEndian : AudioCodecId.PcmU32LittleEndian;
				}
			}

			return AudioCodecId.None;
		}

		/// <summary>
		/// Mirrors av_get_bits_per_sample for every PCM identifier implemented by the port.
		/// </summary>
		public static int GetBitsPerSample(AudioCodecId codecId)
		{
			switch (codecId)
			{
				case AudioCodecId.PcmALaw:
				case AudioCodecId.PcmMuLaw:
				case AudioCodecId.PcmVidc:
				case AudioCodecId.PcmS8:
				case AudioCodecId.PcmS8Planar:
				case AudioCodecId.PcmSga:
				case AudioCodecId.PcmU8:
					return 8;
				case AudioCodecId.PcmS16BigEndian:
				case AudioCodecId.PcmS16BigEndianPlanar:
				case AudioCodecId.PcmS16LittleEndian:
				case AudioCodecId.PcmS16LittleEndianPlanar:
				case AudioCodecId.PcmU16BigEndian:
				case AudioCodecId.PcmU16LittleEndian:
					return 16;
				case AudioCodecId.PcmS24Daud:
				case AudioCodecId.PcmS24BigEndian:
				case AudioCodecId.PcmS24LittleEndian:
				case AudioCodecId.PcmS24LittleEndianPlanar:
				case AudioCodecId.PcmU24BigEndian:
				case AudioCodecId.PcmU24LittleEndian:
					return 24;
				case AudioCodecId.PcmS32BigEndian:
				case AudioCodecId.PcmS32LittleEndian:
				case AudioCodecId.PcmS32LittleEndianPlanar:
				case AudioCodecId.PcmU32BigEndian:
				case AudioCodecId.PcmU32LittleEndian:
				case AudioCodecId.PcmF32BigEndian:
				case AudioCodecId.PcmF32LittleEndian:
				case AudioCodecId.PcmF24LittleEndian:
				case AudioCodecId.PcmF16LittleEndian:
					return 32;
				case AudioCodecId.PcmF64BigEndian:
				case AudioCodecId.PcmF64LittleEndian:
				case AudioCodecId.PcmS64BigEndian:
				case AudioCodecId.PcmS64LittleEndian:
					return 64;
				default:
					return 0;
			}
		}

		public static int GetDefaultPacketSize(AudioStreamInfo stream)
		{
			if (stream.BlockAlign <= 0)
				return FfmpegError.InvalidArgument;

			var maximumSamples = int.MaxValue / stream.BlockAlign;
			var bitsPerSample = GetBitsPerSample(stream.CodecId);
			var bitRate = stream.BitRate;
			if (bitsPerSample > 0 && stream.SampleRate > 0 && stream.Channels > 0 &&
				(long)stream.SampleRate * stream.Channels < long.MaxValue / bitsPerSample)
			{
				bitRate = bitsPerSample * (long)stream.SampleRate * stream.Channels;
			}

			int numberOfSamples;
			if (bitRate > 0)
			{
				var candidate = bitRate / 8 / 10 / stream.BlockAlign;
				candidate = Math.Clamp(candidate, 1, maximumSamples);
				numberOfSamples = 1 << HighestSetBit((int)candidate);
			} else
			{
				numberOfSamples = Math.Clamp(4096 / stream.BlockAlign, 1, maximumSamples);
			}

			return stream.BlockAlign * numberOfSamples;
		}

		private static int HighestSetBit(int value)
		{
			var result = 0;
			while ((value >>= 1) != 0)
				result++;
			return result;
		}
	}
}
