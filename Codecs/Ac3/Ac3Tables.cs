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
namespace Ffmpeg.CsPort.Decoder.Codecs.Ac3
{
	/// <summary>
	/// Stores the normative AC-3 framing tables used verbatim by FFmpeg's parser.
	/// </summary>
	internal static partial class Ac3Tables
	{
		public const int BlockSize = 256;

		public static readonly int[] SampleRates = { 48000, 44100, 32000, 0 };
		public static readonly byte[] Channels = { 2, 1, 2, 3, 3, 4, 4, 5 };
		public static readonly byte[] Eac3Blocks = { 1, 2, 3, 6 };
		public static readonly byte[] CenterLevels = { 4, 5, 6, 5 };
		public static readonly byte[] SurroundLevels = { 4, 6, 7, 6 };
		public static readonly ushort[] BitRates =
		{
			32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640
		};

		public static readonly ushort[,] FrameSizes =
		{
			{ 64, 69, 96 }, { 64, 70, 96 }, { 80, 87, 120 }, { 80, 88, 120 },
			{ 96, 104, 144 }, { 96, 105, 144 }, { 112, 121, 168 }, { 112, 122, 168 },
			{ 128, 139, 192 }, { 128, 140, 192 }, { 160, 174, 240 }, { 160, 175, 240 },
			{ 192, 208, 288 }, { 192, 209, 288 }, { 224, 243, 336 }, { 224, 244, 336 },
			{ 256, 278, 384 }, { 256, 279, 384 }, { 320, 348, 480 }, { 320, 349, 480 },
			{ 384, 417, 576 }, { 384, 418, 576 }, { 448, 487, 672 }, { 448, 488, 672 },
			{ 512, 557, 768 }, { 512, 558, 768 }, { 640, 696, 960 }, { 640, 697, 960 },
			{ 768, 835, 1152 }, { 768, 836, 1152 }, { 896, 975, 1344 }, { 896, 976, 1344 },
			{ 1024, 1114, 1536 }, { 1024, 1115, 1536 }, { 1152, 1253, 1728 }, { 1152, 1254, 1728 },
			{ 1280, 1393, 1920 }, { 1280, 1394, 1920 }
		};

		public static readonly byte[] RematrixBands = { 13, 25, 37, 61, 253 };
		public static readonly byte[] DefaultCouplingBandStructure = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1 };
		public static readonly byte[] BitAllocationPointers =
		{
			0, 1, 1, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6,
			6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 10,
			10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14,
			14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15, 15
		};
		public static readonly byte[] SlowDecay = { 0x0f, 0x11, 0x13, 0x15 };
		public static readonly byte[] FastDecay = { 0x3f, 0x53, 0x67, 0x7b };
		public static readonly ushort[] SlowGain = { 0x540, 0x4d8, 0x478, 0x410 };
		public static readonly ushort[] DecibelsPerBit = { 0x000, 0x700, 0x900, 0xb00 };
		public static readonly short[] Floor = { 0x2f0, 0x2b0, 0x270, 0x230, 0x1f0, 0x170, 0x0f0, unchecked((short)0xf800) };
		public static readonly ushort[] FastGain = { 0x080, 0x100, 0x180, 0x200, 0x280, 0x300, 0x380, 0x400 };
		public static readonly byte[] QuantizationBits = { 0, 3, 5, 7, 11, 15, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16 };
		public static readonly byte[] EnhancedBitAllocationPointers =
		{
			0, 1, 2, 3, 4, 5, 6, 7, 8, 8, 8, 8, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14,
			14, 14, 14, 15, 15, 15, 15, 16, 16, 16, 16, 17, 17, 17, 17, 18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19, 19
		};
		public static readonly byte[] DefaultSpectralExtensionBandStructure = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1 };
		public static readonly byte[] BitsPerEnhancedBitAllocationPointer = { 0, 2, 3, 4, 5, 7, 8, 9, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16 };
		public static readonly short[] GainAdaptiveQuantizationRemap1 = { 4681, 2185, 1057, 520, 258, 129, 64, 32, 16, 8, 2, 0 };
		public static readonly short[,] GainAdaptiveQuantizationRemap24A =
		{
			{-10923,-4681},{-14043,-6554},{-15292,-7399},{-15855,-7802},{-16124,-7998},
			{-16255,-8096},{-16320,-8144},{-16352,-8168},{-16368,-8180}
		};
		public static readonly short[,] GainAdaptiveQuantizationRemap24B =
		{
			{-5461,-1170},{-11703,-4915},{-14199,-6606},{-15327,-7412},{-15864,-7805},
			{-16126,-7999},{-16255,-8096},{-16320,-8144},{-16352,-8168}
		};
		public static readonly float[,] SpectralExtensionAttenuation =
		{
			{0.954841603910416503f,0.911722488558216804f,0.870550563296124125f},{0.911722488558216804f,0.831237896142787758f,0.757858283255198995f},
			{0.870550563296124125f,0.757858283255198995f,0.659753955386447100f},{0.831237896142787758f,0.690956439983888004f,0.574349177498517438f},
			{0.793700525984099792f,0.629960524947436595f,0.500000000000000000f},{0.757858283255198995f,0.574349177498517438f,0.435275281648062062f},
			{0.723634618720189082f,0.523647061410313364f,0.378929141627599553f},{0.690956439983888004f,0.477420801955208307f,0.329876977693223550f},
			{0.659753955386447100f,0.435275281648062062f,0.287174588749258719f},{0.629960524947436595f,0.396850262992049896f,0.250000000000000000f},
			{0.601512518041058319f,0.361817309360094541f,0.217637640824031003f},{0.574349177498517438f,0.329876977693223550f,0.189464570813799776f},
			{0.548412489847312945f,0.300756259020529160f,0.164938488846611775f},{0.523647061410313364f,0.274206244923656473f,0.143587294374629387f},
			{0.500000000000000000f,0.250000000000000000f,0.125000000000000000f},{0.477420801955208307f,0.227930622139554201f,0.108818820412015502f},
			{0.455861244279108402f,0.207809474035696939f,0.094732285406899888f},{0.435275281648062062f,0.189464570813799776f,0.082469244423305887f},
			{0.415618948071393879f,0.172739109995972029f,0.071793647187314694f},{0.396850262992049896f,0.157490131236859149f,0.062500000000000000f},
			{0.378929141627599553f,0.143587294374629387f,0.054409410206007751f},{0.361817309360094541f,0.130911765352578369f,0.047366142703449930f},
			{0.345478219991944002f,0.119355200488802049f,0.041234622211652958f},{0.329876977693223550f,0.108818820412015502f,0.035896823593657347f},
			{0.314980262473718298f,0.099212565748012460f,0.031250000000000000f},{0.300756259020529160f,0.090454327340023621f,0.027204705103003875f},
			{0.287174588749258719f,0.082469244423305887f,0.023683071351724965f},{0.274206244923656473f,0.075189064755132290f,0.020617311105826479f},
			{0.261823530705156682f,0.068551561230914118f,0.017948411796828673f},{0.250000000000000000f,0.062500000000000000f,0.015625000000000000f},
			{0.238710400977604098f,0.056982655534888536f,0.013602352551501938f},{0.227930622139554201f,0.051952368508924235f,0.011841535675862483f}
		};
		public static readonly byte[,] EnhancedFrameExponentStrategy =
		{
			{1,0,0,0,0,0},{1,0,0,0,0,3},{1,0,0,0,2,0},{1,0,0,0,3,3},
			{2,0,0,2,0,0},{2,0,0,2,0,3},{2,0,0,3,2,0},{2,0,0,3,3,3},
			{2,0,1,0,0,0},{2,0,2,0,0,3},{2,0,2,0,2,0},{2,0,2,0,3,3},
			{2,0,3,2,0,0},{2,0,3,2,0,3},{2,0,3,3,2,0},{2,0,3,3,3,3},
			{3,1,0,0,0,0},{3,1,0,0,0,3},{3,2,0,0,2,0},{3,2,0,0,3,3},
			{3,2,0,2,0,0},{3,2,0,2,0,3},{3,2,0,3,2,0},{3,2,0,3,3,3},
			{3,3,1,0,0,0},{3,3,2,0,0,3},{3,3,2,0,2,0},{3,3,2,0,3,3},
			{3,3,3,2,0,0},{3,3,3,2,0,3},{3,3,3,3,2,0},{3,3,3,3,3,3}
		};

		public static readonly byte[,] UngroupThreeInFiveBits =
		{
			{ 0, 0, 0 }, { 0, 0, 1 }, { 0, 0, 2 }, { 0, 1, 0 }, { 0, 1, 1 }, { 0, 1, 2 }, { 0, 2, 0 }, { 0, 2, 1 },
			{ 0, 2, 2 }, { 1, 0, 0 }, { 1, 0, 1 }, { 1, 0, 2 }, { 1, 1, 0 }, { 1, 1, 1 }, { 1, 1, 2 }, { 1, 2, 0 },
			{ 1, 2, 1 }, { 1, 2, 2 }, { 2, 0, 0 }, { 2, 0, 1 }, { 2, 0, 2 }, { 2, 1, 0 }, { 2, 1, 1 }, { 2, 1, 2 },
			{ 2, 2, 0 }, { 2, 2, 1 }, { 2, 2, 2 }, { 3, 0, 0 }, { 3, 0, 1 }, { 3, 0, 2 }, { 3, 1, 0 }, { 3, 1, 1 }
		};

		public static readonly int[,] ChannelMap =
		{
			{ 0, 1, -1, -1, -1, -1 }, { 0, 1, 2, -1, -1, -1 },
			{ 0, -1, -1, -1, -1, -1 }, { 0, 1, -1, -1, -1, -1 },
			{ 0, 1, -1, -1, -1, -1 }, { 0, 1, 2, -1, -1, -1 },
			{ 0, 2, 1, -1, -1, -1 }, { 0, 2, 1, 3, -1, -1 },
			{ 0, 1, 2, -1, -1, -1 }, { 0, 1, 3, 2, -1, -1 },
			{ 0, 2, 1, 3, -1, -1 }, { 0, 2, 1, 4, 3, -1 },
			{ 0, 1, 2, 3, -1, -1 }, { 0, 1, 4, 2, 3, -1 },
			{ 0, 2, 1, 3, 4, -1 }, { 0, 2, 1, 5, 3, 4 }
		};
	}
}
