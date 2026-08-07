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
namespace Ffmpeg.CsPort.Decoder.Audio
{
	/// <summary>
	/// Mirrors the FFmpeg codec identifiers used by the managed audio port.
	/// </summary>
	public enum AudioCodecId
	{
		None = 0,
		PcmS16LittleEndian = 0x10000,
		PcmS16BigEndian,
		PcmU16LittleEndian,
		PcmU16BigEndian,
		PcmS8,
		PcmU8,
		PcmMuLaw,
		PcmALaw,
		PcmS32LittleEndian,
		PcmS32BigEndian,
		PcmU32LittleEndian,
		PcmU32BigEndian,
		PcmS24LittleEndian,
		PcmS24BigEndian,
		PcmU24LittleEndian,
		PcmU24BigEndian,
		PcmS24Daud,
		PcmZork,
		PcmS16LittleEndianPlanar,
		PcmDvd,
		PcmF32BigEndian,
		PcmF32LittleEndian,
		PcmF64BigEndian,
		PcmF64LittleEndian,
		PcmBluray,
		PcmLxf,
		S302M,
		PcmS8Planar,
		PcmS24LittleEndianPlanar,
		PcmS32LittleEndianPlanar,
		PcmS16BigEndianPlanar,
		PcmS64LittleEndian,
		PcmS64BigEndian,
		PcmF16LittleEndian,
		PcmF24LittleEndian,
		PcmVidc,
		PcmSga,
		AdpcmImaQuickTime = 0x11000,
		AdpcmImaWave,
		AdpcmImaDk3,
		AdpcmImaDk4,
		AdpcmImaWestwood,
		AdpcmImaSmjpeg,
		AdpcmMicrosoft,
		Adpcm4Xm,
		AdpcmXa,
		AdpcmAdx,
		AdpcmEa,
		AdpcmG726,
		AdpcmCreative,
		AdpcmSwf,
		AdpcmYamaha,
		AdpcmSoundBlasterPro4,
		AdpcmSoundBlasterPro3,
		AdpcmSoundBlasterPro2,
		AdpcmThp,
		AdpcmImaAmv,
		AdpcmEaR1,
		AdpcmEaR3,
		AdpcmEaR2,
		AdpcmImaEaSead,
		AdpcmImaEaEacs,
		AdpcmEaXas,
		AdpcmEaMaxisXa,
		AdpcmImaIss,
		AdpcmG722,
		AdpcmImaApc,
		AdpcmVima,
		AdpcmAfc,
		AdpcmImaOki,
		AdpcmDtk,
		AdpcmImaRad,
		AdpcmG726LittleEndian,
		AdpcmThpLittleEndian,
		AdpcmPsx,
		AdpcmAica,
		AdpcmImaDat4,
		AdpcmMtaf,
		AdpcmAgm,
		AdpcmArgo,
		AdpcmImaSsi,
		AdpcmZork,
		AdpcmImaApm,
		AdpcmImaAlp,
		AdpcmImaMtf,
		AdpcmImaCunning,
		AdpcmImaMoflex,
		AdpcmImaAcorn,
		AdpcmXmd,
		AdpcmImaXbox,
		AdpcmSanyo,
		AdpcmImaHvqm4,
		AdpcmImaPda,
		AdpcmN64,
		AdpcmImaHvqm2,
		AdpcmImaMagix,
		AdpcmPsxc,
		AdpcmCircus,
		AdpcmImaEscape,
		AmrNarrowBand = 0x12000,
		AmrWideBand,
		Mp2 = 0x15000,
		Mp3,
		Aac,
		Ac3,
		Dts,
		Vorbis,
		Dvaudio,
		WmaV1,
		WmaV2,
		Mace3,
		Mace6,
		Vmdaudio,
		Flac = 0x1500c,
		Alac = 0x15010,
		Qdm2 = 0x15013,
		Qcelp = 0x15018,
		WavPack = 0x15019,
		Ape = 0x15020,
		WmaVoice = 0x15024,
		WmaPro = 0x15025,
		WmaLossless = 0x15026,
		Eac3 = 0x15028,
		Mp1 = 0x1502a,
		Mp4Als = 0x1502d,
		AacLatm = 0x15031,
		Qdmc = 0x15032,
		Opus = 0x1503c,
		MpegH3dAudio = 0x1505b,
		Ac4 = 0x15067,
		GsmMicrosoft = 0x15070
	}
}
