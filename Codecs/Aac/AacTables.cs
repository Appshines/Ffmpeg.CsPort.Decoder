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
using Ffmpeg.CsPort.Decoder.Windows;

namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>
	/// Owns FFmpeg's AAC-LC Huffman, scale-factor, codevector, band-offset, TNS, and analysis-window tables.
	/// </summary>
	internal static partial class AacTables
	{
		internal static readonly int[] SampleRates =
		{
			96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
			16000, 12000, 11025, 8000, 7350, 0, 0, 0
		};

		internal static readonly int[] ChannelsPerConfiguration =
		{
			0, 1, 2, 3, 4, 5, 6, 8, 0, 0, 0, 7, 8, 24, 8
		};

		internal static readonly int[] TagsPerConfiguration =
		{
			0, 1, 1, 2, 3, 3, 4, 5, 0, 0, 0, 5, 5, 16, 5, 0
		};

		internal static readonly float[] PowerTwoScaleFactors = new float[428];
		internal static readonly float[] CubeRootTable = new float[1 << 13];
		internal static readonly float[] Sine1024 = CodecWindows.GetSineWindow(10);
		internal static readonly float[] Sine128 = CodecWindows.GetSineWindow(7);
		internal static readonly float[] KaiserBessel1024 = new float[1024];
		internal static readonly float[] KaiserBessel128 = new float[128];
		internal static readonly Vlc ScaleFactorVlc;
		internal static readonly Vlc[] SpectralVlcs = new Vlc[11];

		static AacTables()
		{
			InitializeScaleFactorTables();
			InitializeCubeRoots();
			CodecWindows.InitializeKaiserBesselWindow(KaiserBessel1024, 4.0f, 1024);
			CodecWindows.InitializeKaiserBesselWindow(KaiserBessel128, 6.0f, 128);
			ScaleFactorVlc = new Vlc();
			if (ScaleFactorVlc.InitializeSparse(7, ScaleFactorBits, ScaleFactorCodes) < 0)
				throw new InvalidOperationException("FFmpeg AAC scale-factor VLC initialization failed.");
			for (var index = 0; index < SpectralVlcs.Length; index++)
			{
				var symbols = new short[SpectralVectorIndexes[index].Length];
				for (var symbol = 0; symbol < symbols.Length; symbol++)
					symbols[symbol] = unchecked((short)SpectralVectorIndexes[index][symbol]);
				var vlc = new Vlc();
				if (vlc.InitializeSparse(8, SpectralBits[index], SpectralCodes[index], symbols) < 0)
					throw new InvalidOperationException("FFmpeg AAC spectral VLC initialization failed.");
				SpectralVlcs[index] = vlc;
			}
		}

		/// <summary>
		/// Replays FFmpeg's float increment schedule so every quarter-power scale factor is rounded at the same assignments.
		/// </summary>
		private static void InitializeScaleFactorTables()
		{
			var exponentLookup = new float[]
			{
				1.00000000000000000000f, 1.04427378242741384032f, 1.09050773266525765921f, 1.13878863475669165370f,
				1.18920711500272106672f, 1.24185781207348404859f, 1.29683955465100966593f, 1.35425554693689272830f,
				1.41421356237309504880f, 1.47682614593949931139f, 1.54221082540794082361f, 1.61049033194925430818f,
				1.68179283050742908606f, 1.75625216037329948311f, 1.83400808640934246349f, 1.91520656139714729387f
			};
			var first = 8.8817841970012523233890533447265625e-16f;
			var previousFirstIncrement = 0;
			var previousSecondIncrement = 8;
			var second = 3.63797880709171295166015625e-12f;
			for (var index = 0; index < PowerTwoScaleFactors.Length; index++)
			{
				var firstIncrement = 4 * (index % 4);
				var secondIncrement = (8 + 3 * index) % 16;
				if (firstIncrement < previousFirstIncrement)
					first *= 2;
				if (secondIncrement < previousSecondIncrement)
					second *= 2;
				PowerTwoScaleFactors[index] = first * exponentLookup[firstIncrement];
				previousFirstIncrement = firstIncrement;
				previousSecondIncrement = secondIncrement;
			}
		}

		/// <summary>
		/// Replays cbrt_tablegen_common.c and cbrt_tablegen.h, including double products and the final float conversion.
		/// </summary>
		private static void InitializeCubeRoots()
		{
			var temporary = new double[CubeRootTable.Length / 2];
			Array.Fill(temporary, 1.0);
			for (var index = 1; index < 45; index++)
			{
				if (temporary[index] == 1.0)
				{
					var value = 2 * index + 1;
					var cubeRoot = value * Math.Cbrt(value);
					for (var multiple = value; multiple < CubeRootTable.Length; multiple *= value)
					{
						for (var target = multiple >> 1; target < temporary.Length; target += multiple)
							temporary[target] *= cubeRoot;
					}
				}
			}
			for (var index = 45; index < temporary.Length; index++)
			{
				if (temporary[index] == 1.0)
				{
					var value = 2 * index + 1;
					var cubeRoot = value * Math.Cbrt(value);
					for (var target = index; target < temporary.Length; target += value)
						temporary[target] *= cubeRoot;
				}
			}
			var cubeRootTwo = 2 * Math.Cbrt(2);
			for (var index = temporary.Length - 1; index >= 0; index--)
			{
				var value = temporary[index];
				for (var target = 2 * index + 1; target < CubeRootTable.Length; target *= 2)
				{
					CubeRootTable[target] = (float)value;
					value *= cubeRootTwo;
				}
			}
			CubeRootTable[0] = 0.0f;
		}
	}
}
