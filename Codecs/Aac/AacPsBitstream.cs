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
using Ffmpeg.CsPort.Decoder.Bitstream;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Ports FFmpeg's scalar AAC Parametric Stereo header, envelope, and differential Huffman parser.</summary>
	internal static class AacPsBitstream
	{
		private const int IidFrequencyFine = 0;
		private const int IidTimeFine = 1;
		private const int IidFrequency = 2;
		private const int IidTime = 3;
		private const int IccFrequency = 4;
		private const int IccTime = 5;
		private const int IpdFrequency = 6;
		private const int IpdTime = 7;
		private const int OpdFrequency = 8;
		private const int OpdTime = 9;
		private static readonly int[,] NumberOfEnvelopes = { { 0, 1, 2, 4 }, { 1, 2, 3, 4 } };
		private static readonly int[] NumberOfIidIccParameters = { 10, 20, 34, 10, 20, 34 };
		private static readonly int[] NumberOfIpdOpdParameters = { 5, 11, 17, 5, 11, 17 };
		private static readonly int[] IidHuffman = { IidFrequency, IidFrequencyFine, IidTime, IidTimeFine };

		/// <summary>Reads at most the declared SBR-extension bit count and mirrors FFmpeg's state reset on malformed PS data.</summary>
		public static int ReadData(AacParametricStereo ps, BitReader reader, int bitsLeft)
		{
			var common = ps.Common;
			var start = reader.Position;
			var header = reader.ReadBit() != 0;
			if (header)
			{
				common.EnableIid = reader.ReadBit() != 0;
				if (common.EnableIid)
				{
					var mode = (int)reader.ReadBits(3);
					if (mode > 5)
						return Fail(common, reader, start, bitsLeft);
					common.NumberOfIidParameters = NumberOfIidIccParameters[mode];
					common.IidQuantization = mode > 2 ? 1 : 0;
					common.NumberOfIpdOpdParameters = NumberOfIpdOpdParameters[mode];
				}
				common.EnableIcc = reader.ReadBit() != 0;
				if (common.EnableIcc)
				{
					common.IccMode = (int)reader.ReadBits(3);
					if (common.IccMode > 5)
						return Fail(common, reader, start, bitsLeft);
					common.NumberOfIccParameters = NumberOfIidIccParameters[common.IccMode];
				}
				common.EnableExtension = reader.ReadBit() != 0;
			}

			common.FrameClass = (int)reader.ReadBit();
			common.PreviousNumberOfEnvelopes = common.NumberOfEnvelopes;
			common.NumberOfEnvelopes = NumberOfEnvelopes[common.FrameClass, reader.ReadBits(2)];
			common.BorderPosition[0] = -1;
			if (common.FrameClass != 0)
			{
				for (var envelope = 1; envelope <= common.NumberOfEnvelopes; envelope++)
				{
					common.BorderPosition[envelope] = (int)reader.ReadBits(5);
					if (common.BorderPosition[envelope] < common.BorderPosition[envelope - 1])
						return Fail(common, reader, start, bitsLeft);
				}
			} else
			{
				var shift = common.NumberOfEnvelopes == 4 ? 2 : common.NumberOfEnvelopes == 2 ? 1 : 0;
				for (var envelope = 1; envelope <= common.NumberOfEnvelopes; envelope++)
					common.BorderPosition[envelope] = (envelope * 32 >> shift) - 1;
			}

			if (common.EnableIid)
			{
				for (var envelope = 0; envelope < common.NumberOfEnvelopes; envelope++)
				{
					var timeDelta = (int)reader.ReadBit();
					if (!ReadParameters(reader, common, common.IidParameters,
						IidHuffman[2 * timeDelta + common.IidQuantization], envelope, timeDelta != 0,
						common.NumberOfIidParameters, 0, 0))
						return Fail(common, reader, start, bitsLeft);
				}
			} else
			{
				Clear(common.IidParameters);
			}

			if (common.EnableIcc)
			{
				for (var envelope = 0; envelope < common.NumberOfEnvelopes; envelope++)
				{
					var timeDelta = reader.ReadBit() != 0;
					if (!ReadParameters(reader, common, common.IccParameters, timeDelta ? IccTime : IccFrequency,
						envelope, timeDelta, common.NumberOfIccParameters, 0, 1))
						return Fail(common, reader, start, bitsLeft);
				}
			} else
			{
				Clear(common.IccParameters);
			}

			if (common.EnableExtension)
			{
				var count = (int)reader.ReadBits(4);
				if (count == 15)
					count += (int)reader.ReadBits(8);
				count *= 8;
				while (count > 7)
				{
					var extensionId = (int)reader.ReadBits(2);
					count -= 2 + ReadExtensionData(reader, common, extensionId);
				}
				if (count < 0)
					return Fail(common, reader, start, bitsLeft);
				reader.SkipBits(count);
			}

			if (common.NumberOfEnvelopes == 0 || common.BorderPosition[common.NumberOfEnvelopes] < 31)
			{
				var source = common.NumberOfEnvelopes != 0
					? common.NumberOfEnvelopes - 1 : common.PreviousNumberOfEnvelopes - 1;
				if (source >= 0 && source != common.NumberOfEnvelopes)
				{
					if (common.EnableIid)
						CopyRow(common.IidParameters, source, common.NumberOfEnvelopes);
					if (common.EnableIcc)
						CopyRow(common.IccParameters, source, common.NumberOfEnvelopes);
					if (common.EnableIpdOpd)
					{
						CopyRow(common.IpdParameters, source, common.NumberOfEnvelopes);
						CopyRow(common.OpdParameters, source, common.NumberOfEnvelopes);
					}
				}
				if (common.EnableIid)
				{
					for (var band = 0; band < common.NumberOfIidParameters; band++)
					{
						if (Math.Abs(common.IidParameters[common.NumberOfEnvelopes, band]) > 7 + 8 * common.IidQuantization)
							return Fail(common, reader, start, bitsLeft);
					}
				}
				if (common.EnableIcc)
				{
					for (var band = 0; band < common.NumberOfIidParameters; band++)
					{
						if ((uint)common.IccParameters[common.NumberOfEnvelopes, band] > 7U)
							return Fail(common, reader, start, bitsLeft);
					}
				}
				common.NumberOfEnvelopes++;
				common.BorderPosition[common.NumberOfEnvelopes] = 31;
			}

			common.Was34Bands = common.Is34Bands;
			if (common.EnableIid || common.EnableIcc)
				common.Is34Bands = common.EnableIid && common.NumberOfIidParameters == 34 ||
					common.EnableIcc && common.NumberOfIccParameters == 34;
			if (!common.EnableIpdOpd)
			{
				Clear(common.IpdParameters);
				Clear(common.OpdParameters);
			}
			if (header)
				common.Started = true;

			var consumed = reader.Position - start;
			return consumed <= bitsLeft ? consumed : Fail(common, reader, start, bitsLeft);
		}

		private static int ReadExtensionData(BitReader reader, AacPsCommon common, int extensionId)
		{
			if (extensionId != 0)
				return 0;
			var start = reader.Position;
			common.EnableIpdOpd = reader.ReadBit() != 0;
			if (common.EnableIpdOpd)
			{
				for (var envelope = 0; envelope < common.NumberOfEnvelopes; envelope++)
				{
					var timeDelta = reader.ReadBit() != 0;
					ReadParameters(reader, common, common.IpdParameters, timeDelta ? IpdTime : IpdFrequency,
						envelope, timeDelta, common.NumberOfIpdOpdParameters, 7, 2);
					timeDelta = reader.ReadBit() != 0;
					ReadParameters(reader, common, common.OpdParameters, timeDelta ? OpdTime : OpdFrequency,
						envelope, timeDelta, common.NumberOfIpdOpdParameters, 7, 2);
				}
			}
			reader.SkipBits(1);
			return reader.Position - start;
		}

		private static bool ReadParameters(BitReader reader, AacPsCommon common, sbyte[,] parameters,
			int tableIndex, int envelope, bool timeDelta, int count, int mask, int kind)
		{
			var maximumDepth = kind == 0 ? 3 : kind == 1 ? 2 : 1;
			if (timeDelta)
			{
				var previousEnvelope = envelope != 0 ? envelope - 1 : common.PreviousNumberOfEnvelopes - 1;
				previousEnvelope = Math.Max(previousEnvelope, 0);
				for (var band = 0; band < count; band++)
				{
					var value = parameters[previousEnvelope, band] +
						reader.ReadVlc(AacPsTables.HuffmanVlcs[tableIndex].Table,
							AacPsTables.HuffmanVlcs[tableIndex].RootBits, maximumDepth);
					if (mask != 0)
						value &= mask;
					parameters[envelope, band] = (sbyte)value;
					if (!IsValid(common, value, kind))
						return false;
				}
			} else
			{
				var value = 0;
				for (var band = 0; band < count; band++)
				{
					value += reader.ReadVlc(AacPsTables.HuffmanVlcs[tableIndex].Table,
						AacPsTables.HuffmanVlcs[tableIndex].RootBits, maximumDepth);
					if (mask != 0)
						value &= mask;
					parameters[envelope, band] = (sbyte)value;
					if (!IsValid(common, value, kind))
						return false;
				}
			}
			return true;
		}

		private static bool IsValid(AacPsCommon common, int value, int kind)
		{
			if (kind == 0)
				return Math.Abs(value) <= 7 + 8 * common.IidQuantization;
			return kind != 1 || (uint)value <= 7U;
		}

		private static int Fail(AacPsCommon common, BitReader reader, int start, int bitsLeft)
		{
			common.Started = false;
			reader.Seek(start + bitsLeft);
			Clear(common.IidParameters);
			Clear(common.IccParameters);
			Clear(common.IpdParameters);
			Clear(common.OpdParameters);
			return bitsLeft;
		}

		private static void Clear(sbyte[,] values)
		{
			Array.Clear(values, 0, values.Length);
		}

		private static void CopyRow(sbyte[,] values, int source, int destination)
		{
			for (var band = 0; band < 34; band++)
				values[destination, band] = values[source, band];
		}
	}
}
