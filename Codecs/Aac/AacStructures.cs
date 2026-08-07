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
	/// <summary>Identifies the AAC raw-data-block syntax element encoded by each three-bit element tag.</summary>
	internal enum AacElementType
	{
		SingleChannel,
		ChannelPair,
		CouplingChannel,
		LowFrequency,
		DataStream,
		ProgramConfig,
		Fill,
		End
	}

	/// <summary>Describes one syntax element and its spatial group from an AAC program_config_element.</summary>
	internal struct AacProgramConfigEntry
	{
		public AacElementType Type;
		public int Id;
		public int Position;
		public ulong ChannelKey;
	}

	/// <summary>Identifies the four AAC window transition sequences used for spectral overlap.</summary>
	internal enum AacWindowSequence
	{
		OnlyLong,
		LongStart,
		EightShort,
		LongStop
	}

	/// <summary>
	/// Stores one individual channel stream's current/previous window state, grouping, and selected scale-factor-band table.
	/// </summary>
	internal sealed class AacIndividualChannelStream
	{
		public int MaximumScaleFactorBand;
		public AacWindowSequence CurrentWindowSequence;
		public AacWindowSequence PreviousWindowSequence;
		public bool CurrentKaiserBessel;
		public bool PreviousKaiserBessel;
		public int NumberOfWindowGroups;
		public int PreviousNumberOfWindowGroups;
		public byte[] GroupLengths = new byte[8];
		public ushort[] ScaleFactorBandOffsets;
		public int NumberOfScaleFactorBands;
		public int NumberOfWindows;
		public int TnsMaximumBands;

		public void CopyCommonWindowFrom(AacIndividualChannelStream source, bool previousKaiserBessel)
		{
			MaximumScaleFactorBand = source.MaximumScaleFactorBand;
			CurrentWindowSequence = source.CurrentWindowSequence;
			PreviousWindowSequence = source.PreviousWindowSequence;
			CurrentKaiserBessel = source.CurrentKaiserBessel;
			PreviousKaiserBessel = previousKaiserBessel;
			NumberOfWindowGroups = source.NumberOfWindowGroups;
			PreviousNumberOfWindowGroups = source.PreviousNumberOfWindowGroups;
			for (var index = 0; index < GroupLengths.Length; index++)
				GroupLengths[index] = source.GroupLengths[index];
			ScaleFactorBandOffsets = source.ScaleFactorBandOffsets;
			NumberOfScaleFactorBands = source.NumberOfScaleFactorBands;
			NumberOfWindows = source.NumberOfWindows;
			TnsMaximumBands = source.TnsMaximumBands;
		}
	}

	/// <summary>Stores all decoded TNS filters and reflection coefficients for one AAC channel.</summary>
	internal sealed class AacTemporalNoiseShaping
	{
		public bool Present;
		public int[] NumberOfFilters = new int[8];
		public int[,] Length = new int[8, 4];
		public int[,] Direction = new int[8, 4];
		public int[,] Order = new int[8, 4];
		public float[,,] Coefficients = new float[8, 4, 20];
	}

	/// <summary>
	/// Owns the persistent overlap state and all reusable per-frame spectral workspaces for one AAC channel.
	/// </summary>
	internal sealed class AacSingleChannelElement
	{
		public AacIndividualChannelStream Stream = new AacIndividualChannelStream();
		public AacTemporalNoiseShaping Tns = new AacTemporalNoiseShaping();
		public byte[] BandTypes = new byte[128];
		public int[] ScaleFactorOffsets = new int[128];
		public float[] ScaleFactors = new float[128];
		public float[] Coefficients = new float[1024];
		public float[] Saved = new float[512];
		public float[] Output = new float[2048];
	}

	/// <summary>Combines one or two AAC channels with their shared mid/side and intensity-stereo masks.</summary>
	internal sealed class AacChannelElement
	{
		public AacSingleChannelElement[] Channels =
		{
			new AacSingleChannelElement(), new AacSingleChannelElement()
		};
		public byte[] MidSideMask = new byte[128];
		public int MaximumStereoScaleFactorBand;
		public bool Present;
		public AacSpectralBandReplication Sbr = new AacSpectralBandReplication();
	}

	/// <summary>Stores the up to four spectral pulse adjustments attached to one long AAC window.</summary>
	internal sealed class AacPulse
	{
		public int NumberOfPulses;
		public int[] Positions = new int[4];
		public int[] Amplitudes = new int[4];
	}
}
