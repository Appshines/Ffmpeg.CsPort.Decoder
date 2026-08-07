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
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.MpegAudio
{
	/// <summary>
	/// Ports FFmpeg's MPEG-audio header validation and frame-size derivation for Layers I, II, and III.
	/// </summary>
	public struct MpegAudioHeader
	{
		public const uint HeaderMask = 0xfffe0ccfU;

		public int CodedFrameSize { get; private set; }
		public bool ErrorProtection { get; private set; }
		public int Layer { get; private set; }
		public int SampleRate { get; private set; }
		public int SampleRateIndex { get; private set; }
		public int BitRate { get; private set; }
		public int Channels { get; private set; }
		public int Mode { get; private set; }
		public int ModeExtension { get; private set; }
		public int LowSamplingFrequency { get; private set; }
		public int SamplesPerFrame => Layer == 1 ? 384 : Layer == 3 && LowSamplingFrequency != 0 ? 576 : 1152;
		public AudioCodecId CodecId => Layer == 1 ? AudioCodecId.Mp1 : Layer == 2 ? AudioCodecId.Mp2 : AudioCodecId.Mp3;

		public static int Check(uint header)
		{
			if ((header & 0xffe00000U) != 0xffe00000U ||
				(header & (3U << 19)) == 1U << 19 ||
				(header & (3U << 17)) == 0 ||
				(header & (15U << 12)) == 15U << 12 ||
				(header & (3U << 10)) == 3U << 10)
			{
				return FfmpegError.InvalidData;
			}
			return 0;
		}

		/// <summary>
		/// Decodes the sync header without changing FFmpeg's integer operation order for bitrate and padding.
		/// </summary>
		public int Decode(uint header)
		{
			var result = Check(header);
			if (result < 0)
				return result;

			int mpeg25;
			if ((header & 1U << 20) != 0)
			{
				LowSamplingFrequency = (header & 1U << 19) != 0 ? 0 : 1;
				mpeg25 = 0;
			} else
			{
				LowSamplingFrequency = 1;
				mpeg25 = 1;
			}

			Layer = 4 - (int)((header >> 17) & 3);
			var sampleRateIndex = (int)((header >> 10) & 3);
			if (sampleRateIndex >= MpegAudioTables.Frequencies.Length)
				sampleRateIndex = 0;
			SampleRate = MpegAudioTables.Frequencies[sampleRateIndex] >> (LowSamplingFrequency + mpeg25);
			sampleRateIndex += 3 * (LowSamplingFrequency + mpeg25);
			SampleRateIndex = sampleRateIndex;
			ErrorProtection = (((header >> 16) & 1) ^ 1) != 0;
			var bitrateIndex = (int)((header >> 12) & 15);
			var padding = (int)((header >> 9) & 1);
			Mode = (int)((header >> 6) & 3);
			ModeExtension = (int)((header >> 4) & 3);
			Channels = Mode == 3 ? 1 : 2;

			if (bitrateIndex == 0)
				return 1;
			var tableIndex = (LowSamplingFrequency * 3 + Layer - 1) * 15 + bitrateIndex;
			var frameSize = (int)MpegAudioTables.BitRates[tableIndex];
			BitRate = frameSize * 1000;
			switch (Layer)
			{
				case 1:
					frameSize = (frameSize * 12000) / SampleRate;
					CodedFrameSize = (frameSize + padding) * 4;
					break;
				case 2:
					CodedFrameSize = frameSize * 144000 / SampleRate + padding;
					break;
				default:
					CodedFrameSize = frameSize * 144000 / (SampleRate << LowSamplingFrequency) + padding;
					break;
			}
			return 0;
		}
	}
}
