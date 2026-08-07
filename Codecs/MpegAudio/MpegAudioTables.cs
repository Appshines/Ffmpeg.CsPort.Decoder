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

namespace Ffmpeg.CsPort.Decoder.Codecs.MpegAudio
{
	/// <summary>
	/// Holds FFmpeg's MPEG-audio allocation, Huffman, scale-factor, requantization, and synthesis tables.
	/// </summary>
	internal static partial class MpegAudioTables
	{
		private const int FractionOne = 1 << 23;
		private const double ImdctScalar = 1.759;
		internal const int MdctBufferSize = 40;

		internal static readonly ushort[] ScaleFactorModShift = new ushort[64];
		internal static readonly int[][] DivisionTables = { new int[64], new int[256], null, new int[2048] };
		internal static readonly Vlc[] HuffmanVlcs = new Vlc[16];
		internal static readonly Vlc[] QuadVlcs = new Vlc[2];
		internal static readonly ushort[] LongBandIndexes = new ushort[9 * 23];
		internal static readonly float[] ExpTable = new float[512];
		internal static readonly float[] ExpValueTable = new float[512 * 16];
		internal static readonly sbyte[] Table43Exponents = new sbyte[(8191 + 16) * 4];
		internal static readonly uint[] Table43Values = new uint[(8191 + 16) * 4];
		internal static readonly int[] ScaleFactorMultipliers = new int[15 * 3];
		internal static readonly int[] ScaleFactorMultipliers2 = new int[3 * 3];
		internal static readonly float[] IsTableLsf = new float[2 * 2 * 16];
		internal static readonly float[] SynthesisWindow = new float[512 + 256];
		internal static readonly float[] MdctWindows = new float[8 * MdctBufferSize];

		internal static readonly float[] IsTable =
		{
			0.0f, 0.2113248705863952637f, 0.3660253882408142090f, 0.5f,
			0.6339746117591857910f, 0.7886751294136047363f, 1.0f, 0.0f,
			0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
			1.0f, 0.7886751294136047363f, 0.6339746117591857910f, 0.5f,
			0.3660253882408142090f, 0.2113248705863952637f, 0.0f, 0.0f,
			0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f
		};

		internal static readonly float[] CsaTable =
		{
			0.8574929237365722656f, -0.5144957900047302246f, 0.3429971337318420410f, -1.371988654136657715f,
			0.8817420005798339844f, -0.4717319905757904053f, 0.4100100100040435791f, -1.353474020957946777f,
			0.9496286511421203613f, -0.3133774697780609131f, 0.6362511515617370605f, -1.263006091117858887f,
			0.9833145737648010254f, -0.1819131970405578613f, 0.8014013767242431641f, -1.165227770805358887f,
			0.9955177903175354004f, -0.09457419067621231079f, 0.9009436368942260742f, -1.090092062950134277f,
			0.9991605877876281738f, -0.04096558317542076111f, 0.9581949710845947266f, -1.040126085281372070f,
			0.9998992085456848145f, -0.01419856864959001541f, 0.9857006072998046875f, -1.014097809791564941f,
			0.9999931454658508301f, -0.003699974622577428818f, 0.9962931871414184570f, -1.003693103790283203f
		};

		static MpegAudioTables()
		{
			InitializeScaleFactors();
			InitializeRequantization();
			InitializeVlcTables();
			InitializeSynthesisTables();
		}

		private static void InitializeScaleFactors()
		{
			for (var index = 0; index < 64; index++)
			{
				var shift = index / 3;
				var mod = index % 3;
				ScaleFactorModShift[index] = (ushort)(mod | shift << 2);
			}
			for (var index = 0; index < 15; index++)
			{
				var n = index + 2;
				var norm = (int)(((1L << n) * FractionOne) / ((1 << n) - 1));
				ScaleFactorMultipliers[index * 3] = (int)(norm * 2.0f);
				ScaleFactorMultipliers[index * 3 + 1] = (int)(norm * (0.7937005259f * 2.0f));
				ScaleFactorMultipliers[index * 3 + 2] = (int)(norm * (0.6299605249f * 2.0f));
			}
			var baseFactors = new[] { 4.0 / 3.0, 4.0 / 5.0, 4.0 / 9.0 };
			var modifiers = new[] { 1.0, 0.7937005259, 0.6299605249 };
			for (var row = 0; row < 3; row++)
				for (var column = 0; column < 3; column++)
					ScaleFactorMultipliers2[row * 3 + column] = (int)(baseFactors[row] * modifiers[column] * FractionOne + 0.5);

			for (var index = 0; index < 16; index++)
			{
				for (var row = 0; row < 2; row++)
				{
					var exponent = -(row + 1) * ((index + 1) >> 1);
					var value = (float)Math.Pow(2.0, exponent / 4.0);
					var selector = index & 1;
					IsTableLsf[(row * 2 + (selector ^ 1)) * 16 + index] = value;
					IsTableLsf[(row * 2 + selector) * 16 + index] = 1.0f;
				}
			}
		}

		/// <summary>
		/// Builds FFmpeg's power tables and grouped-quantizer divisions in their original loop order.
		/// </summary>
		private static void InitializeRequantization()
		{
			var exp2Lookup = new[] { 1.0, 1.18920711500272106672, Math.Sqrt(2.0), 1.68179283050742908606 };
			var power43Lookup = new double[16];
			for (var index = 0; index < 16; index++)
				power43Lookup[index] = index * Math.Cbrt(index);
			var exp2Base = 2.11758236813575084767080625169910490512847900390625e-22;
			for (var exponent = 0; exponent < 512; exponent++)
			{
				if (exponent != 0 && (exponent & 3) == 0)
					exp2Base *= 2;
				var exp2Value = exp2Base * exp2Lookup[exponent & 3] / ImdctScalar;
				for (var value = 0; value < 16; value++)
					ExpValueTable[exponent * 16 + value] = (float)(power43Lookup[value] * exp2Value);
				ExpTable[exponent] = ExpValueTable[exponent * 16 + 1];
			}

			var power43 = 0.0;
			for (var index = 1; index < Table43Values.Length; index++)
			{
				var value = index / 4;
				if ((index & 3) == 0)
					power43 = value / ImdctScalar * Math.Cbrt(value);
				var number = power43 * exp2Lookup[index & 3];
				var exponent = Math.ILogB(number) + 1;
				var fraction = Math.ScaleB(number, -exponent);
				var mantissa = (long)Math.Round(fraction * (1L << 31), MidpointRounding.ToEven);
				exponent += 23 - 31 + 5 - 100;
				Table43Values[index] = unchecked((uint)mantissa);
				Table43Exponents[index] = unchecked((sbyte)-exponent);
			}

			for (var table = 0; table < 4; table++)
			{
				if (QuantizationBits[table] >= 0)
					continue;
				var count = 1 << (-QuantizationBits[table] + 1);
				for (var index = 0; index < count; index++)
				{
					var value = index;
					var steps = QuantizationSteps[table];
					var first = value % steps;
					value /= steps;
					var second = value % steps;
					var third = value / steps;
					DivisionTables[table][index] = first + (second << 4) + (third << 8);
				}
			}
		}

		/// <summary>
		/// Builds MPEG Layer III scale-factor and Huffman VLCs from FFmpeg's canonical source tables.
		/// </summary>
		private static void InitializeVlcTables()
		{
			var lengthOffset = 0;
			for (var table = 1; table < 16; table++)
			{
				var count = HuffmanSizesMinusOne[table - 1] + 1;
				var lengths = new sbyte[count];
				var symbols = new short[count];
				for (var index = 0; index < count; index++)
				{
					lengths[index] = (sbyte)HuffmanLengths[lengthOffset + index];
					var source = HuffmanSymbols[lengthOffset + index];
					var high = source & 0xf0;
					var low = source & 0x0f;
					symbols[index] = (short)((high << 1) | (high != 0 && low != 0 ? 1 << 4 : 0) | low);
				}
				var vlc = new Vlc();
				if (vlc.InitializeFromLengths(7, lengths, symbols) < 0)
					throw new InvalidOperationException("Invalid FFmpeg MPEG-audio Huffman table.");
				HuffmanVlcs[table] = vlc;
				lengthOffset += count;
			}

			for (var table = 0; table < 2; table++)
			{
				var lengths = new byte[16];
				var codes = new uint[16];
				for (var index = 0; index < 16; index++)
				{
					lengths[index] = QuadBits[table * 16 + index];
					codes[index] = QuadCodes[table * 16 + index];
				}
				var vlc = new Vlc();
				if (vlc.InitializeSparse(table == 0 ? 6 : 4, lengths, codes) < 0)
					throw new InvalidOperationException("Invalid FFmpeg MPEG-audio count1 table.");
				QuadVlcs[table] = vlc;
			}

			for (var row = 0; row < 9; row++)
			{
				var position = 0;
				for (var band = 0; band < 22; band++)
				{
					LongBandIndexes[row * 23 + band] = (ushort)position;
					position += LongBandSizes[row * 22 + band] >> 1;
				}
				LongBandIndexes[row * 23 + 22] = (ushort)position;
			}
		}

		/// <summary>
		/// Expands MPEG synthesis windows and cosine coefficients using FFmpeg's initialization order.
		/// </summary>
		private static void InitializeSynthesisTables()
		{
			for (var index = 0; index < 257; index++)
			{
				var value = (float)(SynthesisWindowSource[index] * (1.0 / (1L << 39)));
				SynthesisWindow[index] = value;
				if ((index & 63) != 0)
					value = -value;
				if (index != 0)
					SynthesisWindow[512 - index] = value;
			}
			for (var index = 0; index < 8; index++)
				for (var item = 0; item < 16; item++)
					SynthesisWindow[512 + 16 * index + item] = SynthesisWindow[64 * index + 32 - item];
			for (var index = 0; index < 8; index++)
				for (var item = 0; item < 16; item++)
					SynthesisWindow[512 + 128 + 16 * index + item] = SynthesisWindow[64 * index + 48 - item];

			for (var index = 0; index < 36; index++)
			{
				for (var window = 0; window < 4; window++)
				{
					if (window == 2 && index % 3 != 1)
						continue;
					var value = Math.Sin(Math.PI * (index + 0.5) / 36.0);
					if (window == 1)
					{
						if (index >= 30) value = 0;
						else if (index >= 24) value = Math.Sin(Math.PI * (index - 18 + 0.5) / 12.0);
						else if (index >= 18) value = 1;
					} else if (window == 3)
					{
						if (index < 6) value = 0;
						else if (index < 12) value = Math.Sin(Math.PI * (index - 6 + 0.5) / 12.0);
						else if (index < 18) value = 1;
					}
					value *= 0.5 * ImdctScalar / Math.Cos(Math.PI * (2 * index + 19) / 72.0);
					var destination = window * MdctBufferSize + (window == 2 ? index / 3 : index < 18 ? index : index + 2);
					MdctWindows[destination] = (float)(value / 32);
				}
			}
			for (var window = 0; window < 4; window++)
			{
				for (var index = 0; index < MdctBufferSize; index += 2)
				{
					MdctWindows[(window + 4) * MdctBufferSize + index] = MdctWindows[window * MdctBufferSize + index];
					MdctWindows[(window + 4) * MdctBufferSize + index + 1] = -MdctWindows[window * MdctBufferSize + index + 1];
				}
			}
		}

		internal static byte[] GetAllocationTable(int table)
		{
			return table < 2 ? AllocationTable1 : table < 4 ? AllocationTable3 : AllocationTable4;
		}
	}
}
