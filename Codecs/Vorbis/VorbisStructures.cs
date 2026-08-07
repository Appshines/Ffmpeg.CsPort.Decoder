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

namespace Ffmpeg.CsPort.Decoder.Codecs.Vorbis
{
	/// <summary>
	/// Holds the parsed scalar VLC and optional vector lookup for one Vorbis setup codebook.
	/// </summary>
	internal sealed class VorbisCodebook
	{
		public int Dimensions;
		public int LookupType;
		public int MaximumDepth;
		public int RootBits;
		public Vlc Vlc;
		public float[] Codevectors;
	}

	/// <summary>
	/// Holds either the floor-0 LSP configuration or the floor-1 partition and neighbour configuration.
	/// </summary>
	internal sealed class VorbisFloor
	{
		public int Type;
		public VorbisFloor0 Floor0;
		public VorbisFloor1 Floor1;
	}

	/// <summary>Stores floor-0 bark mapping, codebook selection, and reusable LSP state.</summary>
	internal sealed class VorbisFloor0
	{
		public int Order;
		public int Rate;
		public int BarkMapSize;
		public int[][] Map = new int[2][];
		public int[] MapSize = new int[2];
		public int AmplitudeBits;
		public int AmplitudeOffset;
		public int[] BookList;
		public float[] Lsp;
	}

	/// <summary>Stores floor-1 partition classes and the X-coordinate list prepared for rendering.</summary>
	internal sealed class VorbisFloor1
	{
		public int Partitions;
		public int[] PartitionClass = new int[32];
		public int[] ClassDimensions = new int[16];
		public int[] ClassSubclasses = new int[16];
		public int[] ClassMasterbook = new int[16];
		public int[,] SubclassBooks = new int[16, 8];
		public int Multiplier;
		public VorbisFloor1Entry[] List;
	}

	/// <summary>Stores one floor-1 X coordinate plus its sort and prediction-neighbour indices.</summary>
	internal struct VorbisFloor1Entry
	{
		public int X;
		public int Sort;
		public int Low;
		public int High;
	}

	/// <summary>Stores one residue backend, cascaded books, and reusable classification workspace.</summary>
	internal sealed class VorbisResidue
	{
		public int Type;
		public int Begin;
		public int End;
		public int PartitionSize;
		public int Classifications;
		public int Classbook;
		public int[,] Books = new int[64, 8];
		public int MaximumPass;
		public int PartitionsToRead;
		public byte[] Classifs;
	}

	/// <summary>Stores submap routing and square-polar coupling for one Vorbis mapping.</summary>
	internal sealed class VorbisMapping
	{
		public int Submaps;
		public int CouplingSteps;
		public int[] Magnitude;
		public int[] Angle;
		public int[] Mux;
		public int[] SubmapFloor = new int[16];
		public int[] SubmapResidue = new int[16];
	}

	/// <summary>Stores one mode's block flag and mapping selection.</summary>
	internal struct VorbisMode
	{
		public int BlockFlag;
		public int WindowType;
		public int TransformType;
		public int Mapping;
	}
}
