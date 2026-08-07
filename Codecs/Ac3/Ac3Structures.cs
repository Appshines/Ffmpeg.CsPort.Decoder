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
	/// Carries the structural AC-3 or E-AC-3 sync-frame fields shared by the raw demuxer and decoder.
	/// </summary>
	internal struct Ac3Header
	{
		public int BitstreamId;
		public int BitstreamMode;
		public int FrameType;
		public int SubstreamId;
		public int SampleRateCode;
		public int SampleRateShift;
		public int SampleRate;
		public int BitRate;
		public int FrameSize;
		public int NumberOfBlocks;
		public int ChannelMode;
		public int Channels;
		public int LowFrequencyEffects;
		public int CenterMixLevel;
		public int SurroundMixLevel;
		public int DialogNormalization0;
		public int DialogNormalization1;
		public int CompressionExists0;
		public int CompressionExists1;
		public int HeavyDynamicRange0;
		public int HeavyDynamicRange1;
		public int ChannelMap;
		public int PreferredDownmix;
		public int CenterMixLevelLtRt;
		public int SurroundMixLevelLtRt;
		public int LowFrequencyEffectsMixLevelExists;
		public int LowFrequencyEffectsMixLevel;
		public int DolbySurroundMode;
		public int DolbyHeadphoneMode;
		public int DolbySurroundExMode;
		public int ExtensionTypeA;

		public bool IsEnhanced => BitstreamId > 10;
		public int NumberOfSamples => NumberOfBlocks * Ac3Tables.BlockSize;
	}

	/// <summary>
	/// Stores the psychoacoustic bit-allocation parameters reused between AC-3 audio blocks.
	/// </summary>
	internal struct Ac3BitAllocationParameters
	{
		public int SampleRateCode;
		public int SampleRateShift;
		public int SlowGain;
		public int SlowDecay;
		public int FastDecay;
		public int DecibelsPerBit;
		public int Floor;
		public int CouplingFastLeak;
		public int CouplingSlowLeak;
	}

	/// <summary>
	/// Identifies the E-AC-3 sync-frame relationship encoded by strmtyp.
	/// </summary>
	internal enum Eac3FrameType
	{
		Independent = 0,
		Dependent = 1,
		Ac3Convert = 2,
		Reserved = 3
	}
}
