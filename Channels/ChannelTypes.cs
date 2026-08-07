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
namespace Ffmpeg.CsPort.Decoder.Channels
{
	/// <summary>
	/// Matches FFmpeg's channel identifiers, including the sparse downmix, binaural, and ambisonic ranges.
	/// </summary>
	internal enum AvChannel
	{
		None = -1,
		FrontLeft,
		FrontRight,
		FrontCenter,
		LowFrequency,
		BackLeft,
		BackRight,
		FrontLeftOfCenter,
		FrontRightOfCenter,
		BackCenter,
		SideLeft,
		SideRight,
		TopCenter,
		TopFrontLeft,
		TopFrontCenter,
		TopFrontRight,
		TopBackLeft,
		TopBackCenter,
		TopBackRight,
		StereoLeft = 29,
		StereoRight,
		WideLeft,
		WideRight,
		SurroundDirectLeft,
		SurroundDirectRight,
		LowFrequency2,
		TopSideLeft,
		TopSideRight,
		BottomFrontCenter,
		BottomFrontLeft,
		BottomFrontRight,
		SideSurroundLeft,
		SideSurroundRight,
		TopSurroundLeft,
		TopSurroundRight,
		BinauralLeft = 61,
		BinauralRight,
		Unused = 0x200,
		Unknown = 0x300,
		AmbisonicBase = 0x400,
		AmbisonicEnd = 0x7ff
	}

	/// <summary>
	/// Matches the storage and semantic ordering modes of FFmpeg AVChannelLayout.
	/// </summary>
	internal enum AvChannelOrder
	{
		Unspecified,
		Native,
		Custom,
		Ambisonic
	}

	/// <summary>
	/// Stores one custom-layout channel, its optional user name, and opaque caller data.
	/// </summary>
	internal struct AvChannelCustom
	{
		public AvChannel Id;
		public string Name;
		public object Opaque;
	}

	/// <summary>
	/// Represents FFmpeg AVChannelLayout without native memory ownership or platform-specific APIs.
	/// </summary>
	internal struct AvChannelLayout
	{
		public AvChannelOrder Order;
		public int ChannelCount;
		public ulong Mask;
		public AvChannelCustom[] Map;
		public object Opaque;
	}
}
