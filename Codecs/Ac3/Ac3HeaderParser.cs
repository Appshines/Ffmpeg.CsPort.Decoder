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
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Ac3
{
	/// <summary>
	/// Ports FFmpeg's AC-3 parser header path and preserves its validation and field derivation order.
	/// </summary>
	internal static class Ac3HeaderParser
	{
		public const int HeaderSize = 7;

		/// <summary>
		/// Parses the common sync information needed to delimit both legacy AC-3 and enhanced E-AC-3 frames.
		/// </summary>
		public static int Parse(byte[] data, int offset, int length, out Ac3Header header)
		{
			header = default;
			if (data == null || offset < 0 || length < HeaderSize || offset > data.Length - length)
				return FfmpegError.InvalidData;

			var bits = new BitReader();
			if (bits.Initialize(data, offset, length * 8) < 0 || bits.ReadBits(16) != 0x0b77)
				return FfmpegError.InvalidData;
			return ParseAfterSync(bits, out header);
		}

		public static int Parse(BitReader bits, out Ac3Header header)
		{
			header = default;
			if (bits == null || bits.BitsLeft < HeaderSize * 8 || bits.ReadBits(16) != 0x0b77)
				return FfmpegError.InvalidData;
			return ParseAfterSync(bits, out header);
		}

		private static int ParseAfterSync(BitReader bits, out Ac3Header header)
		{
			header = default;
			header.BitstreamId = (int)(bits.ShowBitsLong(29) & 0x1f);
			if (header.BitstreamId > 16) return FfmpegError.InvalidData;
			header.NumberOfBlocks = 6;
			header.CenterMixLevel = 5;
			header.SurroundMixLevel = 6;

			if (header.BitstreamId <= 10)
				return ParseAc3(bits, ref header);
			return ParseEac3(bits, ref header);
		}

		private static int ParseAc3(BitReader bits, ref Ac3Header header)
		{
			bits.SkipBits(16);
			header.SampleRateCode = (int)bits.ReadBits(2);
			if (header.SampleRateCode == 3) return FfmpegError.InvalidData;
			var frameSizeCode = (int)bits.ReadBits(6);
			if (frameSizeCode > 37) return FfmpegError.InvalidData;

			var bitRateCode = frameSizeCode >> 1;
			bits.SkipBits(5);
			header.BitstreamMode = (int)bits.ReadBits(3);
			header.ChannelMode = (int)bits.ReadBits(3);
			if (header.ChannelMode == 2)
			{
				bits.SkipBits(2);
			} else
			{
				if ((header.ChannelMode & 1) != 0 && header.ChannelMode != 1)
					header.CenterMixLevel = Ac3Tables.CenterLevels[bits.ReadBits(2)];
				if ((header.ChannelMode & 4) != 0)
					header.SurroundMixLevel = Ac3Tables.SurroundLevels[bits.ReadBits(2)];
			}
			header.LowFrequencyEffects = (int)bits.ReadBit();
			header.SampleRateShift = (header.BitstreamId > 8 ? header.BitstreamId : 8) - 8;
			header.SampleRate = Ac3Tables.SampleRates[header.SampleRateCode] >> header.SampleRateShift;
			header.BitRate = Ac3Tables.BitRates[bitRateCode] * 1000 >> header.SampleRateShift;
			header.Channels = Ac3Tables.Channels[header.ChannelMode] + header.LowFrequencyEffects;
			header.FrameSize = Ac3Tables.FrameSizes[frameSizeCode, header.SampleRateCode] * 2;
			header.FrameType = (int)Eac3FrameType.Ac3Convert;
			return ParseAc3Metadata(bits, ref header);
		}

		private static int ParseAc3Metadata(BitReader bits, ref Ac3Header header)
		{
			var count = header.ChannelMode != 0 ? 1 : 2;
			for (var channel = 0; channel < count; channel++)
			{
				var dialogNormalization = -(int)bits.ReadBits(5);
				var compressionExists = (int)bits.ReadBit();
				var heavyDynamicRange = compressionExists != 0 ? (int)bits.ReadBits(8) : 0;
				if (channel == 0)
				{
					header.DialogNormalization0 = dialogNormalization;
					header.CompressionExists0 = compressionExists;
					header.HeavyDynamicRange0 = heavyDynamicRange;
				} else
				{
					header.DialogNormalization1 = dialogNormalization;
					header.CompressionExists1 = compressionExists;
					header.HeavyDynamicRange1 = heavyDynamicRange;
				}
				if (bits.ReadBit() != 0) bits.SkipBits(8);
				if (bits.ReadBit() != 0) bits.SkipBits(7);
			}
			bits.SkipBits(2);
			if (header.BitstreamId != 6)
			{
				if (bits.ReadBit() != 0) bits.SkipBits(14);
				if (bits.ReadBit() != 0) bits.SkipBits(14);
			} else
			{
				if (bits.ReadBit() != 0) bits.SkipBits(14);
				if (bits.ReadBit() != 0) bits.SkipBits(14);
			}
			if (bits.ReadBit() != 0)
			{
				var additionalBytes = (int)bits.ReadBits(6);
				do bits.SkipBits(8); while (additionalBytes-- != 0);
			}
			return 0;
		}

		private static int ParseEac3(BitReader bits, ref Ac3Header header)
		{
			header.FrameType = (int)bits.ReadBits(2);
			if (header.FrameType == (int)Eac3FrameType.Reserved) return FfmpegError.InvalidData;
			header.SubstreamId = (int)bits.ReadBits(3);
			header.FrameSize = ((int)bits.ReadBits(11) + 1) << 1;
			if (header.FrameSize < HeaderSize) return FfmpegError.InvalidData;

			header.SampleRateCode = (int)bits.ReadBits(2);
			if (header.SampleRateCode == 3)
			{
				var secondSampleRateCode = (int)bits.ReadBits(2);
				if (secondSampleRateCode == 3) return FfmpegError.InvalidData;
				header.SampleRate = Ac3Tables.SampleRates[secondSampleRateCode] / 2;
				header.SampleRateShift = 1;
			} else
			{
				header.NumberOfBlocks = Ac3Tables.Eac3Blocks[bits.ReadBits(2)];
				header.SampleRate = Ac3Tables.SampleRates[header.SampleRateCode];
			}
			header.ChannelMode = (int)bits.ReadBits(3);
			header.LowFrequencyEffects = (int)bits.ReadBit();
			header.BitRate = (int)(8L * header.FrameSize * header.SampleRate / (header.NumberOfBlocks * Ac3Tables.BlockSize));
			header.Channels = Ac3Tables.Channels[header.ChannelMode] + header.LowFrequencyEffects;
			return ParseEac3Metadata(bits, ref header);
		}

		/// <summary>
		/// Consumes E-AC-3 bit-stream information in the same conditional order used by FFmpeg's public parser.
		/// </summary>
		private static int ParseEac3Metadata(BitReader bits, ref Ac3Header header)
		{
			if (header.FrameType == (int)Eac3FrameType.Reserved || header.SubstreamId != 0) return FfmpegError.InvalidData;
			bits.SkipBits(5);
			var count = header.ChannelMode != 0 ? 1 : 2;
			for (var channel = 0; channel < count; channel++)
			{
				var dialogNormalization = -(int)bits.ReadBits(5);
				var compressionExists = (int)bits.ReadBit();
				var heavyDynamicRange = compressionExists != 0 ? (int)bits.ReadBits(8) : 0;
				if (channel == 0)
				{
					header.DialogNormalization0 = dialogNormalization;
					header.CompressionExists0 = compressionExists;
					header.HeavyDynamicRange0 = heavyDynamicRange;
				} else
				{
					header.DialogNormalization1 = dialogNormalization;
					header.CompressionExists1 = compressionExists;
					header.HeavyDynamicRange1 = heavyDynamicRange;
				}
			}

			if (header.FrameType == (int)Eac3FrameType.Dependent && bits.ReadBit() != 0) header.ChannelMap = (int)bits.ReadBits(16);
			if (bits.ReadBit() != 0)
			{
				if (header.ChannelMode > 2)
				{
					header.PreferredDownmix = (int)bits.ReadBits(2);
					if ((header.ChannelMode & 1) != 0)
					{
						header.CenterMixLevelLtRt = (int)bits.ReadBits(3);
						header.CenterMixLevel = (int)bits.ReadBits(3);
					}
					if ((header.ChannelMode & 4) != 0)
					{
						header.SurroundMixLevelLtRt = MathMax((int)bits.ReadBits(3), 3);
						header.SurroundMixLevel = MathMax((int)bits.ReadBits(3), 3);
					}
				}
				if (header.LowFrequencyEffects != 0 && bits.ReadBit() != 0)
				{
					header.LowFrequencyEffectsMixLevelExists = 1;
					header.LowFrequencyEffectsMixLevel = (int)bits.ReadBits(5);
				}
				if (header.FrameType == (int)Eac3FrameType.Independent)
				{
					for (var channel = 0; channel < count; channel++) if (bits.ReadBit() != 0) bits.SkipBits(6);
					if (bits.ReadBit() != 0) bits.SkipBits(6);
					switch (bits.ReadBits(2))
					{
						case 1: bits.SkipBits(5); break;
						case 2: bits.SkipBits(12); break;
						case 3: bits.SkipBits(((int)bits.ReadBits(5) + 2) << 3); break;
					}
					if (header.ChannelMode < 2)
						for (var channel = 0; channel < count; channel++) if (bits.ReadBit() != 0) bits.SkipBits(14);
					if (bits.ReadBit() != 0)
						for (var block = 0; block < header.NumberOfBlocks; block++) if (header.NumberOfBlocks == 1 || bits.ReadBit() != 0) bits.SkipBits(5);
				}
			}

			if (bits.ReadBit() != 0)
			{
				header.BitstreamMode = (int)bits.ReadBits(3);
				bits.SkipBits(2);
				if (header.ChannelMode == 2)
				{
					header.DolbySurroundMode = (int)bits.ReadBits(2);
					header.DolbyHeadphoneMode = (int)bits.ReadBits(2);
				}
				if (header.ChannelMode >= 6) header.DolbySurroundExMode = (int)bits.ReadBits(2);
				for (var channel = 0; channel < count; channel++) if (bits.ReadBit() != 0) bits.SkipBits(8);
				if (header.SampleRateCode != 3) bits.SkipBits(1);
			}
			if (header.FrameType == (int)Eac3FrameType.Independent && header.NumberOfBlocks != 6) bits.SkipBits(1);
			if (header.FrameType == (int)Eac3FrameType.Ac3Convert && (header.NumberOfBlocks == 6 || bits.ReadBit() != 0)) bits.SkipBits(6);
			if (bits.ReadBit() != 0)
			{
				var additionalBytes = (int)bits.ReadBits(6);
				for (var index = 0; index < additionalBytes + 1; index++)
				{
					if (index == 0)
					{
						bits.SkipBits(7);
						header.ExtensionTypeA = (int)bits.ReadBit();
						if (header.ExtensionTypeA != 0) { bits.SkipBits(8); index++; }
					} else bits.SkipBits(8);
				}
			}
			return 0;
		}

		private static int MathMax(int value, int minimum)
		{
			return value < minimum ? minimum : value;
		}
	}
}
