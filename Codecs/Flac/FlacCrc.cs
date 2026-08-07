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

namespace Ffmpeg.CsPort.Decoder.Codecs.Flac
{
	/// <summary>
	/// Implements the scalar FLAC CRC-8 and CRC-16 polynomials used for headers and complete frame boundaries.
	/// </summary>
	internal static class FlacCrc
	{
		public static byte Compute8(ReadOnlySpan<byte> data)
		{
			byte crc = 0;
			for (var index = 0; index < data.Length; index++)
			{
				crc ^= data[index];
				for (var bit = 0; bit < 8; bit++)
					crc = (byte)((crc & 0x80) != 0 ? crc << 1 ^ 0x07 : crc << 1);
			}
			return crc;
		}

		public static ushort Update16(ushort crc, byte value)
		{
			crc ^= (ushort)(value << 8);
			for (var bit = 0; bit < 8; bit++)
				crc = (ushort)((crc & 0x8000) != 0 ? crc << 1 ^ 0x8005 : crc << 1);
			return crc;
		}

		public static ushort Compute16(ReadOnlySpan<byte> data)
		{
			ushort crc = 0;
			for (var index = 0; index < data.Length; index++)
				crc = Update16(crc, data[index]);
			return crc;
		}
	}
}
