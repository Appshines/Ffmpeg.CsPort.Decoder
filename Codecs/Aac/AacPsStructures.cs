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
namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Stores persistent Parametric Stereo syntax state shared by the PS bitstream parser and signal processor.</summary>
	internal sealed class AacPsCommon
	{
		public bool Started;
		public bool EnableIid;
		public int IidQuantization;
		public int NumberOfIidParameters;
		public int NumberOfIpdOpdParameters;
		public bool EnableIcc;
		public int IccMode;
		public int NumberOfIccParameters;
		public bool EnableExtension;
		public int FrameClass;
		public int PreviousNumberOfEnvelopes;
		public int NumberOfEnvelopes;
		public bool EnableIpdOpd;
		public int[] BorderPosition = new int[6];
		public sbyte[,] IidParameters = new sbyte[5, 34];
		public sbyte[,] IccParameters = new sbyte[5, 34];
		public sbyte[,] IpdParameters = new sbyte[5, 34];
		public sbyte[,] OpdParameters = new sbyte[5, 34];
		public bool Is34Bands;
		public bool Was34Bands;
	}

	/// <summary>Owns all persistent and reusable scalar workspaces for FFmpeg-compatible AAC Parametric Stereo processing.</summary>
	internal sealed class AacParametricStereo
	{
		public AacPsCommon Common = new AacPsCommon();
		public float[,,] InputBuffer = new float[5, 44, 2];
		public float[,,] Delay = new float[91, 46, 2];
		public float[,,,] AllPassDelay = new float[50, 3, 37, 2];
		public float[] PeakDecayEnergy = new float[34];
		public float[] SmoothedPower = new float[34];
		public float[] SmoothedPeakDifference = new float[34];
		public float[,,] H11 = new float[2, 6, 34];
		public float[,,] H12 = new float[2, 6, 34];
		public float[,,] H21 = new float[2, 6, 34];
		public float[,,] H22 = new float[2, 6, 34];
		public float[,,] LeftBuffer = new float[91, 32, 2];
		public float[,,] RightBuffer = new float[91, 32, 2];
		public sbyte[] OpdHistory = new sbyte[34];
		public sbyte[] IpdHistory = new sbyte[34];
		public float[,] Power = new float[34, 32];
		public float[,] TransientGain = new float[34, 32];
		public float[,] HybridTemporary = new float[8, 2];
		public sbyte[,] IidMapped = new sbyte[5, 34];
		public sbyte[,] IccMapped = new sbyte[5, 34];
		public sbyte[,] IpdMapped = new sbyte[5, 34];
		public sbyte[,] OpdMapped = new sbyte[5, 34];
		public float[,] Matrix = new float[2, 4];
		public float[,] MatrixStep = new float[2, 4];
	}
}
