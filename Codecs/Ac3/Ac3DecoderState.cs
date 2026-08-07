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
	/// Owns all persistent FFmpeg AC-3 block state and preallocated coefficient, overlap, and output buffers.
	/// </summary>
	internal sealed class Ac3DecoderState
	{
		public readonly int[] CouplingInUse = new int[6];
		public readonly int[] CouplingStrategyExists = new int[6];
		public readonly int[] ChannelInCoupling = new int[7];
		public readonly int[] FirstCouplingCoordinates = new int[7];
		public readonly int[] PhaseFlags = new int[18];
		public readonly byte[] CouplingBandStructure = new byte[18];
		public readonly byte[] CouplingBandSizes = new byte[18];
		public readonly int[][] CouplingCoordinates = CreateIntArrays(7, 18);
		public readonly int[] StartFrequency = new int[7];
		public readonly int[] EndFrequency = new int[7];
		public readonly int[] NumberOfExponentGroups = new int[7];
		public readonly sbyte[][] Exponents = CreateSByteArrays(7, 256);
		public readonly int[][] ExponentStrategy = CreateIntArrays(6, 7);
		public readonly int[] SignalToNoiseOffset = new int[7];
		public readonly int[] FastGain = new int[7];
		public readonly byte[][] BitAllocationPointers = CreateByteArrays(7, 256);
		public readonly short[][] PowerSpectralDensity = CreateShortArrays(7, 256);
		public readonly short[][] BandPowerSpectralDensity = CreateShortArrays(7, 50);
		public readonly short[][] Mask = CreateShortArrays(7, 50);
		public readonly int[] DeltaMode = new int[7];
		public readonly int[] DeltaSegmentCount = new int[7];
		public readonly byte[][] DeltaOffsets = CreateByteArrays(7, 8);
		public readonly byte[][] DeltaLengths = CreateByteArrays(7, 8);
		public readonly byte[][] DeltaValues = CreateByteArrays(7, 8);
		public readonly int[] DitherFlag = new int[7];
		public readonly int[] BlockSwitch = new int[7];
		public readonly int[] ChannelUsesAdaptiveHybridTransform = new int[7];
		public readonly int[] ChannelUsesSpectralExtension = new int[7];
		public readonly int[] FirstSpectralExtensionCoordinates = new int[7];
		public readonly sbyte[] SpectralExtensionAttenuationCode = new sbyte[7];
		public readonly byte[] SpectralExtensionBandStructure = new byte[17];
		public readonly byte[] SpectralExtensionBandSizes = new byte[17];
		public readonly float[][] SpectralExtensionNoiseBlend = CreateFloatArrays(7, 17);
		public readonly float[][] SpectralExtensionSignalBlend = CreateFloatArrays(7, 17);
		public readonly int[][] FixedCoefficients = CreateIntArrays(7, 256);
		public readonly float[][] TransformCoefficients = CreateFloatArrays(7, 256);
		public readonly float[][] Delay = CreateFloatArrays(16, 256);
		public readonly float[][] Output = CreateFloatArrays(16, 1536);
		public readonly float[][] PreviousOutput = CreateFloatArrays(16, 256);
		public readonly int[][] PreMantissa = CreateIntArrays(7, 256 * 6);
		public readonly int[] GainAdaptiveQuantizationGain = new int[256];
		public readonly float[] Window = new float[256];
		public readonly float[] TemporaryOutput = new float[256];
		public readonly float[] ShortTransformInput = new float[128];
		public readonly float[] ShortTransformOutput = new float[128];
		public readonly uint[] DitherState = new uint[64];
		public readonly byte[] InputBuffer = new byte[32768];
		public readonly int[] OutputMap = new int[16];

		public Ac3BitAllocationParameters BitAllocationParameters;
		public int DitherIndex;
		public int BitstreamId;
		public int FrameType;
		public int ChannelMode;
		public int LowFrequencyEffects;
		public int SampleRate;
		public int BitRate;
		public int FrameSize;
		public int ChannelMap;
		public int Channels;
		public int FullBandwidthChannels;
		public int LowFrequencyEffectsChannel;
		public int Downmixed = 1;
		public int NumberOfBlocks;
		public int NumberOfCouplingBands;
		public int PhaseFlagsInUse;
		public int NumberOfRematrixingBands;
		public readonly int[] RematrixingFlags = new int[4];
		public int FirstCouplingLeak;
		public int IsEnhanced;
		public int SignalToNoiseOffsetStrategy;
		public int BlockSwitchSyntax;
		public int DitherFlagSyntax;
		public int BitAllocationSyntax;
		public int FastGainSyntax;
		public int DeltaBitAllocationSyntax;
		public int SkipSyntax;
		public int SpectralExtensionInUse;
		public int SpectralExtensionSourceStartFrequency;
		public int SpectralExtensionDestinationStartFrequency;
		public int SpectralExtensionDestinationEndFrequency;
		public int NumberOfSpectralExtensionBands;
		public readonly float[] DynamicRange = { 1.0f, 1.0f };

		private static int[][] CreateIntArrays(int count, int length)
		{
			var result = new int[count][];
			for (var index = 0; index < count; index++) result[index] = new int[length];
			return result;
		}

		private static byte[][] CreateByteArrays(int count, int length)
		{
			var result = new byte[count][];
			for (var index = 0; index < count; index++) result[index] = new byte[length];
			return result;
		}

		private static sbyte[][] CreateSByteArrays(int count, int length)
		{
			var result = new sbyte[count][];
			for (var index = 0; index < count; index++) result[index] = new sbyte[length];
			return result;
		}

		private static short[][] CreateShortArrays(int count, int length)
		{
			var result = new short[count][];
			for (var index = 0; index < count; index++) result[index] = new short[length];
			return result;
		}

		private static float[][] CreateFloatArrays(int count, int length)
		{
			var result = new float[count][];
			for (var index = 0; index < count; index++) result[index] = new float[length];
			return result;
		}
	}
}
