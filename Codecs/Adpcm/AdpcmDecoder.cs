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
using System.Buffers.Binary;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Adpcm
{
	/// <summary>
	/// Ports FFmpeg's stateful ADPCM packet decoder and preserves each codec's packed or planar S16 layout.
	/// </summary>
	public sealed class AdpcmDecoder
	{
		private readonly AdpcmChannelStatus[] _Status = new AdpcmChannelStatus[14];
		private readonly AdpcmByteReader _Reader = new AdpcmByteReader();
		private readonly AdpcmByteReader _ExtraReader = new AdpcmByteReader();
		private readonly BitReader _BitReader = new BitReader();
		private readonly AudioCodecId _CodecId;
		private readonly int _Channels;
		private readonly int _BitsPerCodedSample;
		private readonly int _BlockAlign;
		private readonly byte[] _ExtraData;
		private readonly int _VqaVersion;
		private readonly AudioSampleFormat _SampleFormat;
		private readonly int[] _N64Coefficients = new int[128];
		private readonly int[] _N64Codes = new int[16];
		private readonly short[] _N64History = new short[8];
		private readonly short[] _N64Output = new short[16];
		private readonly byte[] _ImaWaveTemporary = new byte[20];
		private readonly int[] _ThpCoefficients = new int[14 * 16];
		private bool _HasThpStatus;
		private short[] _Samples = Array.Empty<short>();

		private AdpcmDecoder(
			AudioCodecId codecId,
			int channels,
			int bitsPerCodedSample,
			int blockAlign,
			byte[] extraData,
			int vqaVersion,
			AudioSampleFormat sampleFormat)
		{
			_CodecId = codecId;
			_Channels = channels;
			_BitsPerCodedSample = bitsPerCodedSample;
			_BlockAlign = blockAlign;
			_ExtraData = extraData ?? Array.Empty<byte>();
			_VqaVersion = vqaVersion;
			_SampleFormat = sampleFormat;
			for (var index = 0; index < _Status.Length; index++)
				_Status[index] = new AdpcmChannelStatus();
			if (codecId == AudioCodecId.AdpcmImaApm && _ExtraData.Length >= 28)
			{
				_Status[0].Predictor = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(_ExtraData.AsSpan(16, 4)), -262144, 262143);
				_Status[0].StepIndex = (short)Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(_ExtraData.AsSpan(20, 4)), 0, 88);
				_Status[1].Predictor = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(_ExtraData.AsSpan(4, 4)), -262144, 262143);
				_Status[1].StepIndex = (short)Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(_ExtraData.AsSpan(8, 4)), 0, 88);
			}
			if (codecId == AudioCodecId.AdpcmImaApc && _ExtraData.Length >= 8)
			{
				_Status[0].Predictor = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(_ExtraData.AsSpan(0, 4)), -262144, 262143);
				_Status[1].Predictor = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(_ExtraData.AsSpan(4, 4)), -262144, 262143);
			}
			if (codecId == AudioCodecId.AdpcmCreative)
				_Status[0].Step = _Status[1].Step = 511;
		}

		public AudioCodecId CodecId => _CodecId;
		public int Channels => _Channels;
		public AudioSampleFormat SampleFormat => _SampleFormat;

		/// <summary>
		/// Validates codec-specific channel, bit-depth, block, and extradata constraints before creating decoder state.
		/// </summary>
		public static int Initialize(
			AudioCodecId codecId,
			int channels,
			int bitsPerCodedSample,
			int blockAlign,
			byte[] extraData,
			out AdpcmDecoder decoder,
			int vqaVersion = 0)
		{
			decoder = null;
			var minimumChannels = codecId == AudioCodecId.AdpcmDtk || codecId == AudioCodecId.AdpcmMtaf ? 2 : 1;
			var maximumChannels = codecId == AudioCodecId.AdpcmImaAmv || codecId == AudioCodecId.AdpcmN64 ? 1 :
				codecId == AudioCodecId.AdpcmMtaf || codecId == AudioCodecId.AdpcmPsx || codecId == AudioCodecId.AdpcmPsxc ? 8 :
				codecId == AudioCodecId.AdpcmImaDat4 || codecId == AudioCodecId.AdpcmThp || codecId == AudioCodecId.AdpcmThpLittleEndian ? 14 :
				codecId == AudioCodecId.AdpcmMicrosoft || codecId == AudioCodecId.AdpcmAfc || codecId == AudioCodecId.AdpcmEaXas ||
				codecId == AudioCodecId.AdpcmEaR1 || codecId == AudioCodecId.AdpcmEaR2 || codecId == AudioCodecId.AdpcmEaR3 ? 6 : 2;
			if (channels < minimumChannels || channels > maximumChannels)
				return FfmpegError.InvalidArgument;
			if (codecId == AudioCodecId.AdpcmMtaf && (channels & 1) != 0)
				return FfmpegError.PatchWelcome;
			if (codecId == AudioCodecId.AdpcmImaWave && (bitsPerCodedSample < 2 || bitsPerCodedSample > 5))
				return FfmpegError.InvalidData;
			if (codecId == AudioCodecId.AdpcmImaXbox && bitsPerCodedSample != 4)
				return FfmpegError.InvalidData;
			if (codecId == AudioCodecId.AdpcmArgo && (bitsPerCodedSample != 4 || blockAlign != 17 * channels))
				return FfmpegError.InvalidData;
			if (codecId == AudioCodecId.AdpcmZork && bitsPerCodedSample != 8)
				return FfmpegError.InvalidData;
			if (codecId == AudioCodecId.AdpcmSanyo && (bitsPerCodedSample < 3 || bitsPerCodedSample > 5))
				return FfmpegError.InvalidData;
			if (codecId == AudioCodecId.AdpcmPsx && (blockAlign <= 0 || blockAlign % (16 * channels) != 0))
				return FfmpegError.InvalidData;
			if (codecId == AudioCodecId.AdpcmPsxc && (blockAlign <= 0 || blockAlign % channels != 0))
				return FfmpegError.InvalidData;
			if (codecId == AudioCodecId.AdpcmImaWestwood && extraData != null && extraData.Length >= 2)
				vqaVersion = BinaryPrimitives.ReadUInt16LittleEndian(extraData);

			AudioSampleFormat sampleFormat;
			switch (codecId)
			{
				case AudioCodecId.AdpcmImaQuickTime:
				case AudioCodecId.AdpcmImaWave:
				case AudioCodecId.AdpcmAica:
				case AudioCodecId.AdpcmImaCunning:
				case AudioCodecId.AdpcmImaDat4:
				case AudioCodecId.AdpcmImaXbox:
				case AudioCodecId.Adpcm4Xm:
				case AudioCodecId.AdpcmImaMoflex:
				case AudioCodecId.AdpcmAfc:
				case AudioCodecId.AdpcmArgo:
				case AudioCodecId.AdpcmEaXas:
				case AudioCodecId.AdpcmDtk:
				case AudioCodecId.AdpcmN64:
				case AudioCodecId.AdpcmPsx:
				case AudioCodecId.AdpcmPsxc:
				case AudioCodecId.AdpcmSanyo:
				case AudioCodecId.AdpcmThp:
				case AudioCodecId.AdpcmThpLittleEndian:
				case AudioCodecId.AdpcmXmd:
				case AudioCodecId.AdpcmXa:
				case AudioCodecId.AdpcmEaR1:
				case AudioCodecId.AdpcmEaR2:
				case AudioCodecId.AdpcmEaR3:
				case AudioCodecId.AdpcmMtaf:
					sampleFormat = AudioSampleFormat.Signed16Planar;
					break;
				case AudioCodecId.AdpcmMicrosoft:
					sampleFormat = channels > 2 ? AudioSampleFormat.Signed16Planar : AudioSampleFormat.Signed16;
					break;
				case AudioCodecId.AdpcmImaWestwood:
					sampleFormat = vqaVersion == 3 ? AudioSampleFormat.Signed16Planar : AudioSampleFormat.Signed16;
					break;
				case AudioCodecId.AdpcmImaSsi:
				case AudioCodecId.AdpcmImaApm:
				case AudioCodecId.AdpcmImaAlp:
				case AudioCodecId.AdpcmImaApc:
				case AudioCodecId.AdpcmImaDk4:
				case AudioCodecId.AdpcmImaEaEacs:
				case AudioCodecId.AdpcmImaEaSead:
				case AudioCodecId.AdpcmImaEscape:
				case AudioCodecId.AdpcmImaIss:
				case AudioCodecId.AdpcmImaMagix:
				case AudioCodecId.AdpcmImaMtf:
				case AudioCodecId.AdpcmImaOki:
				case AudioCodecId.AdpcmImaPda:
				case AudioCodecId.AdpcmImaRad:
				case AudioCodecId.AdpcmImaSmjpeg:
				case AudioCodecId.AdpcmImaAcorn:
				case AudioCodecId.AdpcmCreative:
				case AudioCodecId.AdpcmSoundBlasterPro2:
				case AudioCodecId.AdpcmSoundBlasterPro3:
				case AudioCodecId.AdpcmSoundBlasterPro4:
				case AudioCodecId.AdpcmEa:
				case AudioCodecId.AdpcmEaMaxisXa:
				case AudioCodecId.AdpcmCircus:
				case AudioCodecId.AdpcmZork:
				case AudioCodecId.AdpcmAgm:
				case AudioCodecId.AdpcmImaAmv:
				case AudioCodecId.AdpcmImaDk3:
				case AudioCodecId.AdpcmImaHvqm2:
				case AudioCodecId.AdpcmImaHvqm4:
				case AudioCodecId.AdpcmSwf:
				case AudioCodecId.AdpcmYamaha:
					sampleFormat = AudioSampleFormat.Signed16;
					break;
				default:
					return FfmpegError.PatchWelcome;
			}

			decoder = new AdpcmDecoder(codecId, channels, bitsPerCodedSample, blockAlign, extraData, vqaVersion, sampleFormat);
			return 0;
		}

		/// <summary>
		/// Computes FFmpeg's packet sample count, dispatches the matching ADPCM syntax, and writes exact S16 bytes.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < 0 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			_Reader.Initialize(packet, packetOffset, packetLength);
			var numberOfSamples = GetNumberOfSamples(packet, packetOffset, packetLength);
			if (numberOfSamples <= 0)
				return FfmpegError.InvalidData;
			var totalSamples = numberOfSamples * _Channels;
			if (totalSamples < 0 || output.Length < totalSamples * 2)
				return FfmpegError.InvalidArgument;
			if (_Samples.Length < totalSamples)
				_Samples = new short[totalSamples];

			int result;
			switch (_CodecId)
			{
				case AudioCodecId.AdpcmImaQuickTime:
					result = DecodeImaQuickTime(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaWave:
					result = DecodeImaWave(numberOfSamples);
					break;
				case AudioCodecId.AdpcmMicrosoft:
					result = DecodeMicrosoft(numberOfSamples);
					break;
				case AudioCodecId.Adpcm4Xm:
					result = Decode4Xm(numberOfSamples);
					break;
				case AudioCodecId.AdpcmAica:
					DecodeAica(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaApc:
					DecodeImaApc(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaCunning:
					DecodeImaCunning(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaDat4:
					DecodeImaDat4(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaDk4:
					result = DecodeImaDk4(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaIss:
					result = DecodeImaIss(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaMagix:
					result = DecodeImaMagix(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaMoflex:
					result = DecodeImaMoflex(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaMtf:
					DecodeImaMtf(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaOki:
					DecodeImaOki(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaRad:
					result = DecodeImaRad(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaAcorn:
					result = DecodeImaAcorn(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaEaEacs:
					result = DecodeImaEaEacs(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaEaSead:
					DecodeImaEaSead(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaEscape:
					DecodeImaEscape(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaPda:
					result = DecodeImaPda(numberOfSamples, false);
					break;
				case AudioCodecId.AdpcmImaSmjpeg:
					result = DecodeImaPda(numberOfSamples, true);
					break;
				case AudioCodecId.AdpcmImaXbox:
					result = DecodeImaXbox(numberOfSamples);
					break;
				case AudioCodecId.AdpcmCreative:
					DecodeCreative(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmSoundBlasterPro2:
				case AudioCodecId.AdpcmSoundBlasterPro3:
				case AudioCodecId.AdpcmSoundBlasterPro4:
					DecodeSoundBlasterPro(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmArgo:
					DecodeArgo(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmCircus:
					DecodeCircus(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmZork:
					DecodeZork(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmEa:
					result = DecodeEa(numberOfSamples);
					break;
				case AudioCodecId.AdpcmEaMaxisXa:
					DecodeEaMaxisXa(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmEaXas:
					DecodeEaXas(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmAfc:
					DecodeAfc(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmAgm:
					result = DecodeAgm(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaAmv:
					result = DecodeImaAmv(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaDk3:
					result = DecodeImaDk3(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaHvqm2:
				case AudioCodecId.AdpcmImaHvqm4:
					result = DecodeImaHvqm(numberOfSamples);
					break;
				case AudioCodecId.AdpcmXmd:
					DecodeXmd(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmXa:
					result = DecodeXa(packet, packetOffset, packetLength, numberOfSamples);
					break;
				case AudioCodecId.AdpcmDtk:
					DecodeDtk(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmN64:
					result = DecodeN64(packet, packetOffset, packetLength, numberOfSamples);
					break;
				case AudioCodecId.AdpcmPsx:
				case AudioCodecId.AdpcmPsxc:
					result = DecodePlayStation(numberOfSamples);
					break;
				case AudioCodecId.AdpcmSanyo:
					result = DecodeSanyo(packet, packetOffset, numberOfSamples);
					break;
				case AudioCodecId.AdpcmThp:
				case AudioCodecId.AdpcmThpLittleEndian:
					result = DecodeThp(numberOfSamples);
					break;
				case AudioCodecId.AdpcmEaR1:
				case AudioCodecId.AdpcmEaR2:
				case AudioCodecId.AdpcmEaR3:
					result = DecodeEaR(numberOfSamples);
					break;
				case AudioCodecId.AdpcmMtaf:
					result = DecodeMtaf(numberOfSamples);
					break;
				case AudioCodecId.AdpcmImaSsi:
					DecodeImaSsi(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaApm:
					DecodeImaApm(numberOfSamples, false);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaAlp:
					DecodeImaApm(numberOfSamples, true);
					result = 0;
					break;
				case AudioCodecId.AdpcmImaWestwood:
					DecodeImaWestwood(numberOfSamples);
					result = 0;
					break;
				case AudioCodecId.AdpcmSwf:
					result = DecodeSwf(packet, packetOffset, packetLength, numberOfSamples);
					break;
				case AudioCodecId.AdpcmYamaha:
					DecodeYamaha(numberOfSamples);
					result = 0;
					break;
				default:
					return FfmpegError.PatchWelcome;
			}
			if (result < 0)
				return result;

			for (var index = 0; index < totalSamples; index++)
				BinaryPrimitives.WriteInt16LittleEndian(output.Slice(index * 2, 2), _Samples[index]);
			var planeCount = _SampleFormat == AudioSampleFormat.Signed16Planar ? _Channels : 1;
			var planeSize = _SampleFormat == AudioSampleFormat.Signed16Planar ? numberOfSamples * 2 : totalSamples * 2;
			frame = new AudioFrameInfo(numberOfSamples, _Channels, _SampleFormat, planeCount, planeSize, totalSamples * 2);
			var consumed = _CodecId == AudioCodecId.AdpcmSwf ? packetLength : _Reader.Position;
			return consumed > 0 ? consumed : FfmpegError.InvalidData;
		}

		/// <summary>
		/// Derives the decodable sample count from each ADPCM variant's block framing and validates packet bounds.
		/// </summary>
		private int GetNumberOfSamples(byte[] packet, int packetOffset, int packetLength)
		{
			switch (_CodecId)
			{
				case AudioCodecId.AdpcmImaQuickTime:
					return packetLength < 34 * _Channels ? 0 : 64;
				case AudioCodecId.AdpcmImaSsi:
				case AudioCodecId.AdpcmImaApm:
				case AudioCodecId.AdpcmImaAlp:
				case AudioCodecId.AdpcmImaWestwood:
				case AudioCodecId.AdpcmYamaha:
					return packetLength * 2 / _Channels;
				case AudioCodecId.AdpcmImaApc:
				case AudioCodecId.AdpcmImaCunning:
				case AudioCodecId.AdpcmImaEaSead:
				case AudioCodecId.AdpcmImaEscape:
				case AudioCodecId.AdpcmImaOki:
				case AudioCodecId.AdpcmAica:
				case AudioCodecId.AdpcmImaMtf:
					return packetLength * 2 / _Channels;
				case AudioCodecId.Adpcm4Xm:
				case AudioCodecId.AdpcmImaDat4:
				case AudioCodecId.AdpcmImaIss:
				case AudioCodecId.AdpcmImaMoflex:
				case AudioCodecId.AdpcmImaSmjpeg:
				case AudioCodecId.AdpcmImaAcorn:
					return (packetLength - 4 * _Channels) * 2 / _Channels;
				case AudioCodecId.AdpcmImaDk4:
				{
					var size = _BlockAlign > 0 ? Math.Min(packetLength, _BlockAlign) : packetLength;
					return size < 4 * _Channels ? 0 : 1 + (size - 4 * _Channels) * 2 / _Channels;
				}
				case AudioCodecId.AdpcmImaMagix:
				case AudioCodecId.AdpcmImaRad:
				case AudioCodecId.AdpcmImaPda:
				{
					var size = _BlockAlign > 0 ? Math.Min(packetLength, _BlockAlign) : packetLength;
					return (size - 4 * _Channels) * 2 / _Channels;
				}
				case AudioCodecId.AdpcmImaXbox:
				{
					var size = _BlockAlign > 0 ? Math.Min(packetLength, _BlockAlign) : packetLength;
					if (size < 4 * _Channels)
						return 0;
					var blockSize = AdpcmTables.ImaBlockSizes[_BitsPerCodedSample - 2];
					var blockSamples = AdpcmTables.ImaBlockSamples[_BitsPerCodedSample - 2];
					return (size - 4 * _Channels) / (blockSize * _Channels) * blockSamples;
				}
				case AudioCodecId.AdpcmImaEaEacs:
				{
					if (packetLength < 4 + 8 * _Channels)
						return 0;
					var codedSamples = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packetOffset, 4)));
					var maximumSamples = (packetLength - (4 + 8 * _Channels)) * 2 / _Channels;
					return codedSamples > 0 && codedSamples <= maximumSamples ? codedSamples : 0;
				}
				case AudioCodecId.AdpcmCreative:
					return packetLength * 2 / _Channels;
				case AudioCodecId.AdpcmSoundBlasterPro2:
				case AudioCodecId.AdpcmSoundBlasterPro3:
				case AudioCodecId.AdpcmSoundBlasterPro4:
				{
					var samplesPerByte = _CodecId == AudioCodecId.AdpcmSoundBlasterPro2 ? 4 :
						_CodecId == AudioCodecId.AdpcmSoundBlasterPro3 ? 3 : 2;
					var size = packetLength;
					var samples = 0;
					if (_Status[0].StepIndex == 0)
					{
						if (size < _Channels)
							return 0;
						samples++;
						size -= _Channels;
					}
					return samples + size * samplesPerByte / _Channels;
				}
				case AudioCodecId.AdpcmArgo:
					return _BlockAlign > 0 ? packetLength / _BlockAlign * 32 : 0;
				case AudioCodecId.AdpcmCircus:
				case AudioCodecId.AdpcmZork:
					return packetLength / _Channels;
				case AudioCodecId.AdpcmEaXas:
					return packetLength < 76 * _Channels ? 0 : 128;
				case AudioCodecId.AdpcmEaMaxisXa:
					return (packetLength - _Channels) / _Channels * 2;
				case AudioCodecId.AdpcmAfc:
					return packetLength / (9 * _Channels) * 16;
				case AudioCodecId.AdpcmEa:
				{
					if (packetLength < 12)
						return 0;
					var codedSamples = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packetOffset, 4)));
					codedSamples -= codedSamples % 28;
					var maximumSamples = (packetLength - 12) / (_Channels == 2 ? 30 : 15) * 28;
					return codedSamples > 0 && codedSamples <= maximumSamples ? codedSamples : 0;
				}
				case AudioCodecId.AdpcmAgm:
					return (packetLength - 4 * _Channels) * 2 / _Channels;
				case AudioCodecId.AdpcmImaAmv:
				{
					if (packetLength < 8)
						return 0;
					var codedSamples = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packetOffset + 4, 4)));
					return Math.Min((packetLength - 8) * 2, codedSamples);
				}
				case AudioCodecId.AdpcmImaDk3:
				{
					var size = _BlockAlign > 0 ? Math.Min(packetLength, _BlockAlign) : packetLength;
					return ((size - 16) * 2 / 3 * 4) / _Channels;
				}
				case AudioCodecId.AdpcmImaHvqm2:
					return packetLength >= 8 ? BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(packetOffset + 4, 2)) : 0;
				case AudioCodecId.AdpcmImaHvqm4:
				{
					if (packetLength < 6)
						return 0;
					var format = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(packetOffset, 2));
					var skip = 6 + (format == 1 ? 2 * _Channels : format == 3 ? 3 * _Channels : 0);
					return (packetLength - skip) * 2 / _Channels;
				}
				case AudioCodecId.AdpcmXmd:
					return packetLength / (21 * _Channels) * 32;
				case AudioCodecId.AdpcmXa:
					return packetLength / 128 * 224 / _Channels;
				case AudioCodecId.AdpcmDtk:
				case AudioCodecId.AdpcmPsx:
					return packetLength / (16 * _Channels) * 28;
				case AudioCodecId.AdpcmPsxc:
					return (packetLength - 1) / _Channels * 2;
				case AudioCodecId.AdpcmN64:
					return packetLength / 9 * 16;
				case AudioCodecId.AdpcmMtaf:
				{
					var size = _BlockAlign > 0 ? Math.Min(packetLength, _BlockAlign) : packetLength;
					return (size - 16 * (_Channels / 2)) * 2 / _Channels;
				}
				case AudioCodecId.AdpcmSanyo:
					return _ExtraData.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(_ExtraData) : 0;
				case AudioCodecId.AdpcmThp:
				case AudioCodecId.AdpcmThpLittleEndian:
				{
					if (_ExtraData.Length != 0)
						return packetLength * 14 / (8 * _Channels);
					if (packetLength < 8 + 36 * _Channels)
						return 0;
					var codedSamples = _CodecId == AudioCodecId.AdpcmThpLittleEndian
						? unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packetOffset + 4, 4)))
						: unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(packetOffset + 4, 4)));
					var size = (packetLength - 8 - 36 * _Channels) / _Channels;
					var maximum = size / 8 * 14 + (size % 8 > 1 ? (size % 8 - 1) * 2 : 0);
					return codedSamples > 0 && codedSamples <= maximum ? codedSamples : 0;
				}
				case AudioCodecId.AdpcmEaR1:
				case AudioCodecId.AdpcmEaR2:
				case AudioCodecId.AdpcmEaR3:
				{
					if (packetLength < 4)
						return 0;
					var codedSamples = _CodecId == AudioCodecId.AdpcmEaR3
						? unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(packetOffset, 4)))
						: unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(packetOffset, 4)));
					codedSamples -= codedSamples % 28;
					var headerSize = 4 + (_CodecId == AudioCodecId.AdpcmEaR1 ? 9 : 5) * _Channels;
					var maximum = (packetLength - headerSize) * 2 / _Channels;
					maximum -= maximum % 28;
					return codedSamples > 0 && codedSamples <= maximum ? codedSamples : 0;
				}
				case AudioCodecId.AdpcmImaWave:
				{
					var size = _BlockAlign > 0 ? Math.Min(packetLength, _BlockAlign) : packetLength;
					if (size < 4 * _Channels)
						return 0;
					var blockSize = AdpcmTables.ImaBlockSizes[_BitsPerCodedSample - 2];
					var blockSamples = AdpcmTables.ImaBlockSamples[_BitsPerCodedSample - 2];
					return 1 + (size - 4 * _Channels) / (blockSize * _Channels) * blockSamples;
				}
				case AudioCodecId.AdpcmMicrosoft:
				{
					var size = _BlockAlign > 0 ? Math.Min(packetLength, _BlockAlign) : packetLength;
					return (size - 6 * _Channels) * 2 / _Channels;
				}
				case AudioCodecId.AdpcmSwf:
				{
					var bufferBits = packetLength * 8 - 2;
					var bits = (packet[packetOffset] >> 6) + 2;
					var headerSize = 22 * _Channels;
					var blockSize = headerSize + bits * _Channels * 4095;
					var blocks = bufferBits / blockSize;
					var bitsLeft = bufferBits - blocks * blockSize;
					var samples = blocks * 4096;
					if (bitsLeft >= headerSize)
						samples += 1 + (bitsLeft - headerSize) / (bits * _Channels);
					return samples;
				}
				default:
					return 0;
			}
		}

		private int DecodeImaQuickTime(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				var status = _Status[channel];
				var predictor = _Reader.ReadInt16BigEndian();
				var stepIndex = predictor & 0x7f;
				predictor &= ~0x7f;
				if (status.StepIndex != stepIndex || Math.Abs(predictor - status.Predictor) > 0x7f)
				{
					status.StepIndex = (short)stepIndex;
					status.Predictor = predictor;
				}
				if ((uint)status.StepIndex > 88)
					return FfmpegError.InvalidData;
				var outputOffset = channel * numberOfSamples;
				for (var sample = 0; sample < 64; sample += 2)
				{
					var value = _Reader.ReadByte();
					_Samples[outputOffset + sample] = AdpcmSampleExpansion.ImaQuickTime(status, value & 15);
					_Samples[outputOffset + sample + 1] = AdpcmSampleExpansion.ImaQuickTime(status, value >> 4);
				}
			}
			return 0;
		}

		/// <summary>
		/// Decodes 2–5-bit IMA WAV blocks, preserving the per-channel little-endian bit packing.
		/// </summary>
		private int DecodeImaWave(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				var status = _Status[channel];
				status.Predictor = _Reader.ReadInt16LittleEndian();
				_Samples[channel * numberOfSamples] = (short)status.Predictor;
				status.StepIndex = (short)_Reader.ReadByte();
				_Reader.Skip(1);
				if ((uint)status.StepIndex > 88)
					return FfmpegError.InvalidData;
			}

			if (_BitsPerCodedSample != 4)
			{
				var samplesPerBlock = AdpcmTables.ImaBlockSamples[_BitsPerCodedSample - 2];
				var blockSize = AdpcmTables.ImaBlockSizes[_BitsPerCodedSample - 2];
				for (var block = 0; block < (numberOfSamples - 1) / samplesPerBlock; block++)
				{
					for (var channel = 0; channel < _Channels; channel++)
					{
						for (var index = 0; index < blockSize; index++)
						{
							var input = 4 * _Channels + blockSize * block * _Channels +
								(index % 4) + (index / 4) * (_Channels * 4) + channel * 4;
							_Reader.Seek(input);
							_ImaWaveTemporary[index] = (byte)_Reader.ReadByte();
						}
						_BitReader.Initialize(_ImaWaveTemporary, blockSize * 8, true);
						var outputOffset = channel * numberOfSamples + 1 + block * samplesPerBlock;
						for (var sample = 0; sample < samplesPerBlock; sample++)
						{
							var nibble = (int)_BitReader.ReadBits(_BitsPerCodedSample);
							_Samples[outputOffset + sample] = AdpcmSampleExpansion.ImaWave(_Status[channel], nibble, _BitsPerCodedSample);
						}
					}
				}
				_Reader.Seek(4 * _Channels + (numberOfSamples - 1) / samplesPerBlock * blockSize * _Channels);
			} else
			{
				for (var block = 0; block < (numberOfSamples - 1) / 8; block++)
				{
					for (var channel = 0; channel < _Channels; channel++)
					{
						var outputOffset = channel * numberOfSamples + 1 + block * 8;
						for (var sample = 0; sample < 8; sample += 2)
						{
							var value = _Reader.ReadByte();
							_Samples[outputOffset + sample] = AdpcmSampleExpansion.ImaQuickTime(_Status[channel], value & 15);
							_Samples[outputOffset + sample + 1] = AdpcmSampleExpansion.ImaQuickTime(_Status[channel], value >> 4);
						}
					}
				}
			}
			return 0;
		}

		/// <summary>
		/// Decodes both FFmpeg Microsoft ADPCM layouts: packed mono/stereo and planar multichannel.
		/// </summary>
		private int DecodeMicrosoft(int numberOfSamples)
		{
			if (_Channels > 2)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var predictor = _Reader.ReadByte();
					if (predictor > 6)
						return FfmpegError.InvalidData;
					var status = _Status[channel];
					status.Coefficient1 = AdpcmTables.AdaptCoefficient1[predictor];
					status.Coefficient2 = AdpcmTables.AdaptCoefficient2[predictor];
					status.Delta = _Reader.ReadInt16LittleEndian();
					status.Sample1 = _Reader.ReadInt16LittleEndian();
					status.Sample2 = _Reader.ReadInt16LittleEndian();
					var output = channel * numberOfSamples;
					_Samples[output++] = (short)status.Sample2;
					_Samples[output++] = (short)status.Sample1;
					for (var count = (numberOfSamples - 2) >> 1; count > 0; count--)
					{
						var value = _Reader.ReadByte();
						_Samples[output++] = AdpcmSampleExpansion.Microsoft(status, value >> 4);
						_Samples[output++] = AdpcmSampleExpansion.Microsoft(status, value & 15);
					}
				}
				return 0;
			}

			for (var channel = 0; channel < _Channels; channel++)
			{
				var predictor = _Reader.ReadByte();
				if (predictor > 6)
					return FfmpegError.InvalidData;
				_Status[channel].Coefficient1 = AdpcmTables.AdaptCoefficient1[predictor];
				_Status[channel].Coefficient2 = AdpcmTables.AdaptCoefficient2[predictor];
			}
			for (var channel = 0; channel < _Channels; channel++)
				_Status[channel].Delta = _Reader.ReadInt16LittleEndian();
			for (var channel = 0; channel < _Channels; channel++)
				_Status[channel].Sample1 = _Reader.ReadInt16LittleEndian();
			for (var channel = 0; channel < _Channels; channel++)
				_Status[channel].Sample2 = _Reader.ReadInt16LittleEndian();

			var outputOffset = 0;
			for (var channel = 0; channel < _Channels; channel++)
				_Samples[outputOffset++] = (short)_Status[channel].Sample2;
			for (var channel = 0; channel < _Channels; channel++)
				_Samples[outputOffset++] = (short)_Status[channel].Sample1;
			var stereo = _Channels == 2 ? 1 : 0;
			for (var count = (numberOfSamples - 2) >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[outputOffset++] = AdpcmSampleExpansion.Microsoft(_Status[0], value >> 4);
				_Samples[outputOffset++] = AdpcmSampleExpansion.Microsoft(_Status[stereo], value & 15);
			}
			return 0;
		}

		private int Decode4Xm(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].StepIndex = (short)_Reader.ReadInt16LittleEndian();
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			for (var channel = 0; channel < _Channels; channel++)
			{
				var output = channel * numberOfSamples;
				for (var count = numberOfSamples >> 1; count > 0; count--)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value & 15, 4);
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value >> 4, 4);
				}
			}
			return 0;
		}

		private void DecodeAica(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				var output = channel * numberOfSamples;
				for (var count = numberOfSamples >> 1; count > 0; count--)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.Yamaha(_Status[channel], value & 15);
					_Samples[output++] = AdpcmSampleExpansion.Yamaha(_Status[channel], value >> 4);
				}
			}
		}

		private void DecodeImaApc(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], value >> 4, 3);
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[stereo], value & 15, 3);
			}
		}

		private void DecodeImaCunning(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				var output = channel * numberOfSamples;
				for (var count = 0; count < numberOfSamples / 2; count++)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.ImaCunning(_Status[channel], value & 15);
					_Samples[output++] = AdpcmSampleExpansion.ImaCunning(_Status[channel], value >> 4);
				}
			}
		}

		private void DecodeImaDat4(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				var output = channel * numberOfSamples;
				_Reader.Skip(4);
				for (var sample = 0; sample < numberOfSamples; sample += 2)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value >> 4, 3);
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value & 15, 3);
				}
			}
		}

		private int DecodeImaDk4(int numberOfSamples)
		{
			var output = 0;
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				_Samples[output++] = (short)_Status[channel].Predictor;
				_Status[channel].StepIndex = (short)_Reader.ReadInt16LittleEndian();
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			var stereo = _Channels == 2 ? 1 : 0;
			for (var count = (numberOfSamples - 1) >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], value >> 4, 3);
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[stereo], value & 15, 3);
			}
			return 0;
		}

		private int DecodeImaIss(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				_Status[channel].StepIndex = (short)_Reader.ReadInt16LittleEndian();
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				var first = stereo != 0 ? value >> 4 : value & 15;
				var second = stereo != 0 ? value & 15 : value >> 4;
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], first, 3);
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[stereo], second, 3);
			}
			return 0;
		}

		private int DecodeImaMagix(int numberOfSamples)
		{
			if (_Channels == 1)
				return FfmpegError.PatchWelcome;
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				_Status[channel].StepIndex = (short)_Reader.ReadInt16LittleEndian();
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			var output = 0;
			for (var block = 0; block < _Channels * numberOfSamples / 16; block++)
			{
				var first = _Reader.ReadUInt32LittleEndian();
				var second = _Reader.ReadUInt32LittleEndian();
				for (var count = 8; count > 0; count--, first >>= 4, second >>= 4)
				{
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], (int)first & 15, 3);
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[1], (int)second & 15, 3);
				}
			}
			return 0;
		}

		private int DecodeImaMoflex(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].StepIndex = (short)_Reader.ReadInt16LittleEndian();
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			for (var subframe = 0; subframe < numberOfSamples / 256; subframe++)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var output = channel * numberOfSamples + 256 * subframe;
					for (var sample = 0; sample < 256; sample += 2)
					{
						var value = _Reader.ReadByte();
						_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value & 15, 3);
						_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value >> 4, 3);
					}
				}
			}
			return 0;
		}

		private void DecodeImaMtf(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples / 2; count > 0; count--)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.ImaMtf(_Status[channel], value >> 4);
					_Samples[output + stereo] = AdpcmSampleExpansion.ImaMtf(_Status[channel], value & 15);
				}
				output += _Channels;
			}
		}

		private void DecodeImaOki(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.ImaOki(_Status[0], value >> 4);
				_Samples[output++] = AdpcmSampleExpansion.ImaOki(_Status[stereo], value & 15);
			}
		}

		private int DecodeImaRad(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].StepIndex = (short)_Reader.ReadInt16LittleEndian();
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			Span<int> values = stackalloc int[2];
			var output = 0;
			for (var sample = 0; sample < numberOfSamples / 2; sample++)
			{
				values[0] = _Reader.ReadByte();
				if (_Channels == 2)
					values[1] = _Reader.ReadByte();
				for (var channel = 0; channel < _Channels; channel++)
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], values[channel] & 15, 3);
				for (var channel = 0; channel < _Channels; channel++)
					_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], values[channel] >> 4, 3);
			}
			return 0;
		}

		private int DecodeImaXbox(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				_Status[channel].StepIndex = (short)_Reader.ReadInt16LittleEndian();
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
				_Samples[channel * numberOfSamples] = (short)_Status[channel].Predictor;
			}
			for (var block = 0; block < numberOfSamples / 8; block++)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var output = channel * numberOfSamples + 1 + block * 8;
					for (var sample = 0; sample < 8; sample += 2)
					{
						var value = _Reader.ReadByte();
						var first = AdpcmSampleExpansion.Ima(_Status[channel], value & 15, 3);
						var second = AdpcmSampleExpansion.Ima(_Status[channel], value >> 4, 3);
						if (output < (channel + 1) * numberOfSamples)
							_Samples[output++] = first;
						if (output < (channel + 1) * numberOfSamples)
							_Samples[output++] = second;
					}
				}
			}
			return 0;
		}

		private int DecodeImaAcorn(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				_Status[channel].StepIndex = (short)(_Reader.ReadUInt16LittleEndian() & 0xff);
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], value & 15, 3);
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[stereo], value >> 4, 3);
			}
			return 0;
		}

		private int DecodeImaEaEacs(int numberOfSamples)
		{
			_Reader.Skip(4);
			for (var channel = 0; channel < _Channels; channel++)
			{
				var stepIndex = _Reader.ReadUInt32LittleEndian();
				if (stepIndex > 88)
					return FfmpegError.InvalidData;
				_Status[channel].StepIndex = (short)stepIndex;
			}
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = unchecked((int)_Reader.ReadUInt32LittleEndian());
				if (Math.Abs((long)_Status[channel].Predictor) > 65536)
					return FfmpegError.InvalidData;
			}
			DecodeImaPacked(numberOfSamples, 3, false);
			return 0;
		}

		private void DecodeImaEaSead(int numberOfSamples)
		{
			DecodeImaPacked(numberOfSamples, 6, false);
		}

		private void DecodeImaEscape(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.ImaEscape(_Status[0], value >> 4);
				_Samples[output++] = AdpcmSampleExpansion.ImaEscape(_Status[stereo], value & 15);
			}
		}

		private int DecodeImaPda(int numberOfSamples, bool bigEndian)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = bigEndian ? _Reader.ReadInt16BigEndian() : _Reader.ReadInt16LittleEndian();
				_Status[channel].StepIndex = (short)_Reader.ReadByte();
				_Reader.Skip(1);
				if ((uint)_Status[channel].StepIndex > 88)
					return FfmpegError.InvalidData;
			}
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[0], value >> 4);
				_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[stereo], value & 15);
			}
			return 0;
		}

		private void DecodeImaPacked(int numberOfSamples, int shift, bool lowNibbleFirst)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				var first = lowNibbleFirst ? value & 15 : value >> 4;
				var second = lowNibbleFirst ? value >> 4 : value & 15;
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], first, shift);
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[stereo], second, shift);
			}
		}

		private void DecodeCreative(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.Creative(_Status[0], value >> 4);
				_Samples[output++] = AdpcmSampleExpansion.Creative(_Status[stereo], value & 15);
			}
		}

		/// <summary>
		/// Preserves Sound Blaster Pro's first-packet seed and its distinct 2-, 2.6-, and 4-bit groupings.
		/// </summary>
		private void DecodeSoundBlasterPro(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			if (_Status[0].StepIndex == 0)
			{
				_Samples[output++] = (short)(128 * (_Reader.ReadByte() - 0x80));
				if (stereo != 0)
					_Samples[output++] = (short)(128 * (_Reader.ReadByte() - 0x80));
				_Status[0].StepIndex = 1;
				numberOfSamples--;
			}
			if (_CodecId == AudioCodecId.AdpcmSoundBlasterPro4)
			{
				for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[0], value >> 4, 4, 0);
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[stereo], value & 15, 4, 0);
				}
			} else if (_CodecId == AudioCodecId.AdpcmSoundBlasterPro3)
			{
				for (var count = (numberOfSamples << stereo) / 3; count > 0; count--)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[0], value >> 5, 3, 0);
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[0], value >> 2 & 7, 3, 0);
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[0], value & 3, 2, 0);
				}
			} else
			{
				for (var count = numberOfSamples >> (2 - stereo); count > 0; count--)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[0], value >> 6, 2, 2);
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[stereo], value >> 4 & 3, 2, 2);
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[0], value >> 2 & 3, 2, 2);
					_Samples[output++] = AdpcmSampleExpansion.SoundBlasterPro(_Status[stereo], value & 3, 2, 2);
				}
			}
		}

		private void DecodeArgo(int numberOfSamples)
		{
			var blocks = numberOfSamples / 32;
			for (var block = 0; block < blocks; block++)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var output = channel * numberOfSamples + block * 32;
					var control = _Reader.ReadByte();
					var shift = (control >> 4) + 2;
					for (var count = 0; count < 16; count++)
					{
						var value = _Reader.ReadByte();
						_Samples[output++] = AdpcmSampleExpansion.Argo(_Status[channel], value >> 4, shift, control & 4);
						_Samples[output++] = AdpcmSampleExpansion.Argo(_Status[channel], value & 15, shift, control & 4);
					}
				}
			}
		}

		private void DecodeCircus(int numberOfSamples)
		{
			var output = 0;
			for (var sample = 0; sample < numberOfSamples; sample++)
				for (var channel = 0; channel < _Channels; channel++)
					_Samples[output++] = AdpcmSampleExpansion.Circus(_Status[channel], _Reader.ReadByte());
		}

		private void DecodeZork(int numberOfSamples)
		{
			for (var index = 0; index < numberOfSamples * _Channels; index++)
				_Samples[index] = AdpcmSampleExpansion.Zork(_Status[index % _Channels], _Reader.ReadByte());
		}

		/// <summary>
		/// Decodes Electronic Arts' original 28-sample blocks with mono's two-nibble expansion order.
		/// </summary>
		private int DecodeEa(int numberOfSamples)
		{
			if (_Channels != 1 && _Channels != 2)
				return FfmpegError.InvalidData;
			_Reader.Skip(4);
			var currentLeft = _Reader.ReadInt16LittleEndian();
			var previousLeft = _Reader.ReadInt16LittleEndian();
			var currentRight = _Reader.ReadInt16LittleEndian();
			var previousRight = _Reader.ReadInt16LittleEndian();
			var output = 0;
			for (var block = 0; block < numberOfSamples / 28; block++)
			{
				var value = _Reader.ReadByte();
				var coefficient1Left = AdpcmTables.Ea[value >> 4];
				var coefficient2Left = AdpcmTables.Ea[(value >> 4) + 4];
				var coefficient1Right = AdpcmTables.Ea[value & 15];
				var coefficient2Right = AdpcmTables.Ea[(value & 15) + 4];
				int shiftLeft;
				var shiftRight = 0;
				if (_Channels == 2)
				{
					value = _Reader.ReadByte();
					shiftLeft = 20 - (value >> 4);
					shiftRight = 20 - (value & 15);
				} else
				{
					shiftLeft = 20 - (value & 15);
				}
				for (var count = 0; count < (_Channels == 2 ? 28 : 14); count++)
				{
					value = _Reader.ReadByte();
					var nextLeft = ((value >> 4) >= 8 ? (value >> 4) - 16 : value >> 4) * (1 << shiftLeft);
					nextLeft = (nextLeft + currentLeft * coefficient1Left + previousLeft * coefficient2Left + 0x80) >> 8;
					previousLeft = currentLeft;
					currentLeft = Math.Clamp(nextLeft, short.MinValue, short.MaxValue);
					_Samples[output++] = (short)currentLeft;
					if (_Channels == 2)
					{
						var nibble = value & 15;
						var nextRight = (nibble >= 8 ? nibble - 16 : nibble) * (1 << shiftRight);
						nextRight = (nextRight + currentRight * coefficient1Right + previousRight * coefficient2Right + 0x80) >> 8;
						previousRight = currentRight;
						currentRight = Math.Clamp(nextRight, short.MinValue, short.MaxValue);
						_Samples[output++] = (short)currentRight;
					} else
					{
						var nibble = value & 15;
						nextLeft = (nibble >= 8 ? nibble - 16 : nibble) * (1 << shiftLeft);
						nextLeft = (nextLeft + currentLeft * coefficient1Left + previousLeft * coefficient2Left + 0x80) >> 8;
						previousLeft = currentLeft;
						currentLeft = Math.Clamp(nextLeft, short.MinValue, short.MaxValue);
						_Samples[output++] = (short)currentLeft;
					}
				}
			}
			_Reader.Skip(_Channels == 2 ? 2 : 3);
			return 0;
		}

		private void DecodeEaMaxisXa(int numberOfSamples)
		{
			Span<int> coefficient1 = stackalloc int[2];
			Span<int> coefficient2 = stackalloc int[2];
			Span<int> shift = stackalloc int[2];
			for (var channel = 0; channel < _Channels; channel++)
			{
				var value = _Reader.ReadByte();
				coefficient1[channel] = AdpcmTables.Ea[value >> 4];
				coefficient2[channel] = AdpcmTables.Ea[(value >> 4) + 4];
				shift[channel] = 20 - (value & 15);
			}
			Span<int> values = stackalloc int[2];
			var output = 0;
			for (var pair = 0; pair < numberOfSamples / 2; pair++)
			{
				values[0] = _Reader.ReadByte();
				if (_Channels == 2)
					values[1] = _Reader.ReadByte();
				for (var nibbleShift = 4; nibbleShift >= 0; nibbleShift -= 4)
				{
					for (var channel = 0; channel < _Channels; channel++)
					{
						var nibble = values[channel] >> nibbleShift & 15;
						var sample = (nibble >= 8 ? nibble - 16 : nibble) * (1 << shift[channel]);
						sample = (sample + _Status[channel].Sample1 * coefficient1[channel] +
							_Status[channel].Sample2 * coefficient2[channel] + 0x80) >> 8;
						_Status[channel].Sample2 = _Status[channel].Sample1;
						_Status[channel].Sample1 = Math.Clamp(sample, short.MinValue, short.MaxValue);
						_Samples[output++] = (short)_Status[channel].Sample1;
					}
				}
			}
			_Reader.Skip(_Reader.BytesLeft);
		}

		private void DecodeEaXas(int numberOfSamples)
		{
			Span<int> coefficient1 = stackalloc int[4];
			Span<int> coefficient2 = stackalloc int[4];
			Span<int> shift = stackalloc int[4];
			for (var channel = 0; channel < _Channels; channel++)
			{
				for (var subframe = 0; subframe < 4; subframe++)
				{
					var value = _Reader.ReadInt16LittleEndian();
					coefficient1[subframe] = AdpcmTables.Ea[value & 15];
					coefficient2[subframe] = AdpcmTables.Ea[(value & 15) + 4];
					var output = channel * numberOfSamples + subframe * 32;
					_Samples[output] = (short)(value & ~15);
					value = _Reader.ReadInt16LittleEndian();
					shift[subframe] = 20 - (value & 15);
					_Samples[output + 1] = (short)(value & ~15);
				}
				for (var sample = 2; sample < 32; sample += 2)
				{
					for (var subframe = 0; subframe < 4; subframe++)
					{
						var output = channel * numberOfSamples + subframe * 32 + sample;
						var value = _Reader.ReadByte();
						var high = value >> 4;
						var level = (high >= 8 ? high - 16 : high) * (1 << shift[subframe]);
						var prediction = _Samples[output - 1] * coefficient1[subframe] + _Samples[output - 2] * coefficient2[subframe];
						_Samples[output] = (short)Math.Clamp((level + prediction + 0x80) >> 8, short.MinValue, short.MaxValue);
						var low = value & 15;
						level = (low >= 8 ? low - 16 : low) * (1 << shift[subframe]);
						prediction = _Samples[output] * coefficient1[subframe] + _Samples[output - 1] * coefficient2[subframe];
						_Samples[output + 1] = (short)Math.Clamp((level + prediction + 0x80) >> 8, short.MinValue, short.MaxValue);
					}
				}
			}
		}

		/// <summary>
		/// Expands Nintendo AFC frames with per-channel predictor history and coefficient selection.
		/// </summary>
		private void DecodeAfc(int numberOfSamples)
		{
			var samplesPerBlock = _ExtraData.Length == 1 && _ExtraData[0] != 0 ? _ExtraData[0] / 16 : numberOfSamples / 16;
			var blocks = _ExtraData.Length == 1 && _ExtraData[0] != 0 ? numberOfSamples / _ExtraData[0] : 1;
			for (var block = 0; block < blocks; block++)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var previous1 = _Status[channel].Sample1;
					var previous2 = _Status[channel].Sample2;
					var output = channel * numberOfSamples + block * 16;
					for (var subBlock = 0; subBlock < samplesPerBlock; subBlock++)
					{
						var value = _Reader.ReadByte();
						var scale = 1 << (value >> 4);
						var index = value & 15;
						var factor1 = AdpcmTables.Afc[0, index];
						var factor2 = AdpcmTables.Afc[1, index];
						for (var sample = 0; sample < 16; sample++)
						{
							int sampleData;
							if ((sample & 1) != 0)
								sampleData = (value & 15) >= 8 ? (value & 15) - 16 : value & 15;
							else
							{
								value = _Reader.ReadByte();
								sampleData = (value >> 4) >= 8 ? (value >> 4) - 16 : value >> 4;
							}
							sampleData = ((previous1 * factor1 + previous2 * factor2) >> 11) + sampleData * scale;
							var decoded = Math.Clamp(sampleData, short.MinValue, short.MaxValue);
							_Samples[output++] = (short)decoded;
							previous2 = previous1;
							previous1 = decoded;
						}
					}
					_Status[channel].Sample1 = previous1;
					_Status[channel].Sample2 = previous2;
				}
			}
			_Reader.Skip(_Reader.BytesLeft);
		}

		private int DecodeAgm(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
			for (var channel = 0; channel < _Channels; channel++)
				_Status[channel].Step = _Reader.ReadInt16LittleEndian();
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = 0; count < numberOfSamples >> (1 - stereo); count++)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.Agm(_Status[0], value & 15);
				_Samples[output++] = AdpcmSampleExpansion.Agm(_Status[stereo], value >> 4);
			}
			return 0;
		}

		private int DecodeImaAmv(int numberOfSamples)
		{
			_Status[0].Predictor = _Reader.ReadInt16LittleEndian();
			_Status[0].StepIndex = (short)_Reader.ReadByte();
			_Reader.Skip(5);
			if ((uint)_Status[0].StepIndex > 88)
				return FfmpegError.InvalidData;
			var output = 0;
			for (var count = numberOfSamples >> 1; count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], value >> 4, 3);
				_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[0], value & 15, 3);
			}
			if ((numberOfSamples & 1) != 0)
			{
				var value = _Reader.ReadByte();
				_Samples[output] = AdpcmSampleExpansion.Ima(_Status[0], value >> 4, 3);
			}
			return 0;
		}

		/// <summary>
		/// Decodes Duck DK3's alternating sum/difference nibble groups in their original consumption order.
		/// </summary>
		private int DecodeImaDk3(int numberOfSamples)
		{
			_Reader.Skip(10);
			_Status[0].Predictor = _Reader.ReadInt16LittleEndian();
			_Status[1].Predictor = _Reader.ReadInt16LittleEndian();
			_Status[0].StepIndex = (short)_Reader.ReadByte();
			_Status[1].StepIndex = (short)_Reader.ReadByte();
			if ((uint)_Status[0].StepIndex > 88 || (uint)_Status[1].StepIndex > 88)
				return FfmpegError.InvalidData;
			var lastByte = 0;
			var highNibbleNext = false;
			var output = 0;
			while (output < _Channels * numberOfSamples)
			{
				var nibble = ReadDk3Nibble(ref lastByte, ref highNibbleNext);
				AdpcmSampleExpansion.Ima(_Status[0], nibble, 3);
				nibble = ReadDk3Nibble(ref lastByte, ref highNibbleNext);
				AdpcmSampleExpansion.Ima(_Status[1], nibble, 3);
				_Samples[output++] = (short)(_Status[0].Predictor + _Status[1].Predictor);
				_Samples[output++] = (short)(_Status[0].Predictor - _Status[1].Predictor);
				if (output >= _Channels * numberOfSamples)
					break;
				nibble = ReadDk3Nibble(ref lastByte, ref highNibbleNext);
				AdpcmSampleExpansion.Ima(_Status[0], nibble, 3);
				_Samples[output++] = (short)(_Status[0].Predictor + _Status[1].Predictor);
				_Samples[output++] = (short)(_Status[0].Predictor - _Status[1].Predictor);
			}
			if ((_Reader.Position & 1) != 0)
				_Reader.Skip(1);
			return 0;
		}

		private int ReadDk3Nibble(ref int lastByte, ref bool highNibbleNext)
		{
			if (highNibbleNext)
			{
				highNibbleNext = false;
				return lastByte >> 4;
			}
			lastByte = _Reader.ReadByte();
			highNibbleNext = true;
			return lastByte & 15;
		}

		/// <summary>
		/// Decodes HVQM IMA headers and nibble groups while retaining each channel's predictor and step index.
		/// </summary>
		private int DecodeImaHvqm(int numberOfSamples)
		{
			var format = _Reader.ReadUInt16BigEndian();
			_Reader.Skip(4);
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			if (_CodecId == AudioCodecId.AdpcmImaHvqm2)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					if (format == 0)
					{
						var value = _Reader.ReadUInt16BigEndian();
						_Status[channel].Predictor = unchecked((short)(value & 0xff80));
						_Status[channel].StepIndex = (short)(value & 0x7f);
						_Samples[output++] = (short)_Status[channel].Predictor;
						numberOfSamples--;
					}
					_Status[channel].StepIndex = (short)Math.Clamp((int)_Status[channel].StepIndex, 0, 88);
				}
				var nibble = 0;
				for (var sample = 0; sample < numberOfSamples; sample++)
				{
					if ((sample & 1) == 0)
					{
						nibble = _Reader.ReadByte();
						_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[stereo], nibble >> 4);
					} else
					{
						_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[0], nibble & 15);
					}
				}
			} else
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					if (format == 1)
					{
						var value = _Reader.ReadUInt16BigEndian();
						_Status[channel].Predictor = unchecked((short)(value & 0xff80));
						_Status[channel].StepIndex = (short)(value & 0x7f);
					} else if (format == 3)
					{
						_Status[channel].Predictor = _Reader.ReadInt16BigEndian();
						_Status[channel].StepIndex = (short)_Reader.ReadByte();
					}
					_Status[channel].StepIndex = (short)Math.Clamp((int)_Status[channel].StepIndex, 0, 88);
				}
				if (format == 1 || format == 3)
				{
					for (var channel = 0; channel < _Channels; channel++)
						_Samples[output++] = (short)_Status[stereo - channel].Predictor;
					numberOfSamples--;
				}
				for (var sample = 0; sample < numberOfSamples; sample += 1 + (stereo == 0 ? 1 : 0))
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[stereo], value & 15);
					_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[0], value >> 4);
				}
			}
			_Reader.Skip(_Reader.BytesLeft);
			return 0;
		}

		private void DecodeXmd(int numberOfSamples)
		{
			var block = 0;
			while (_Reader.BytesLeft >= 21 * _Channels)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var output = channel * numberOfSamples + block * 32;
					var history1 = _Reader.ReadInt16LittleEndian();
					var history0 = _Reader.ReadInt16LittleEndian();
					var scale = _Reader.ReadUInt16LittleEndian();
					_Samples[output] = (short)history1;
					_Samples[output + 1] = (short)history0;
					for (var count = 0; count < 15; count++)
					{
						var value = _Reader.ReadByte();
						var first = value & 15;
						first = first >= 8 ? first - 16 : first;
						var decoded = first * scale + ((history0 * 3667 - history1 * 1642) >> 11);
						var decodedSample = (short)decoded;
						_Samples[output + 2 + count * 2] = decodedSample;
						history1 = history0;
						history0 = decodedSample;
						var second = value >> 4;
						second = second >= 8 ? second - 16 : second;
						decoded = second * scale + ((history0 * 3667 - history1 * 1642) >> 11);
						decodedSample = (short)decoded;
						_Samples[output + 3 + count * 2] = decodedSample;
						history1 = history0;
						history0 = decodedSample;
					}
				}
				block++;
			}
			_Reader.Skip(_Reader.BytesLeft);
		}

		/// <summary>
		/// Decodes one or more 128-byte CD-ROM XA sound groups into FFmpeg's planar channel arrangement.
		/// </summary>
		private int DecodeXa(byte[] packet, int packetOffset, int packetLength, int numberOfSamples)
		{
			var samplesPerBlock = 28 * (3 - _Channels) * 4;
			var sampleOffset = 0;
			for (var blockOffset = 0; blockOffset + 128 <= packetLength; blockOffset += 128)
			{
				var input = packetOffset + blockOffset;
				for (var group = 0; group < 4; group++)
				{
					var leftOutput = sampleOffset + group * 28 * (3 - _Channels);
					var rightOutput = _Channels == 1 ? leftOutput + 28 : numberOfSamples + leftOutput;
					var header = packet[input + 4 + group * 2];
					var shift = 12 - (header & 15);
					var filter = header >> 4;
					if (filter >= 5) filter = 0;
					if (shift < 0) shift = 0;
					var firstFactor = AdpcmTables.Xa[filter, 0];
					var secondFactor = AdpcmTables.Xa[filter, 1];
					var sample1 = _Status[0].Sample1;
					var sample2 = _Status[0].Sample2;
					for (var sample = 0; sample < 28; sample++)
					{
						var value = packet[input + 16 + group + sample * 4] & 15;
						value = value >= 8 ? value - 16 : value;
						var decoded = value * (1 << shift) + ((sample1 * firstFactor + sample2 * secondFactor + 32) >> 6);
						sample2 = sample1;
						sample1 = Math.Clamp(decoded, short.MinValue, short.MaxValue);
						_Samples[leftOutput + sample] = (short)sample1;
					}
					if (_Channels == 2)
					{
						_Status[0].Sample1 = sample1;
						_Status[0].Sample2 = sample2;
						sample1 = _Status[1].Sample1;
						sample2 = _Status[1].Sample2;
					}
					header = packet[input + 5 + group * 2];
					shift = 12 - (header & 15);
					filter = header >> 4;
					if (filter >= 5) filter = 0;
					if (shift < 0) shift = 0;
					firstFactor = AdpcmTables.Xa[filter, 0];
					secondFactor = AdpcmTables.Xa[filter, 1];
					for (var sample = 0; sample < 28; sample++)
					{
						var value = packet[input + 16 + group + sample * 4] >> 4;
						value = value >= 8 ? value - 16 : value;
						var decoded = value * (1 << shift) + ((sample1 * firstFactor + sample2 * secondFactor + 32) >> 6);
						sample2 = sample1;
						sample1 = Math.Clamp(decoded, short.MinValue, short.MaxValue);
						_Samples[rightOutput + sample] = (short)sample1;
					}
					if (_Channels == 2)
					{
						_Status[1].Sample1 = sample1;
						_Status[1].Sample2 = sample2;
					} else
					{
						_Status[0].Sample1 = sample1;
						_Status[0].Sample2 = sample2;
					}
				}
				sampleOffset += samplesPerBlock;
			}
			_Reader.Skip(_Reader.BytesLeft);
			return 0;
		}

		private void DecodeDtk(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Reader.Seek(0);
				var output = channel * numberOfSamples;
				for (var block = 0; block < numberOfSamples / 28; block++)
				{
					if (channel != 0) _Reader.Skip(1);
					var header = _Reader.ReadByte();
					_Reader.Skip(3 - channel);
					for (var sample = 0; sample < 28; sample++)
					{
						int previous;
						switch (header >> 4)
						{
							case 1: previous = _Status[channel].Sample1 * 0x3c; break;
							case 2: previous = _Status[channel].Sample1 * 0x73 - _Status[channel].Sample2 * 0x34; break;
							case 3: previous = _Status[channel].Sample1 * 0x62 - _Status[channel].Sample2 * 0x37; break;
							default: previous = 0; break;
						}
						previous = Math.Clamp((previous + 0x20) >> 6, -2097152, 2097151);
						var value = _Reader.ReadByte();
						var nibble = channel == 0 ? value & 15 : value >> 4;
						nibble = nibble >= 8 ? nibble - 16 : nibble;
						var sampleData = ((nibble * (1 << 12)) >> (header & 15)) * (1 << 6) + previous;
						_Samples[output++] = (short)Math.Clamp(sampleData >> 6, short.MinValue, short.MaxValue);
						_Status[channel].Sample2 = _Status[channel].Sample1;
						_Status[channel].Sample1 = sampleData;
					}
				}
			}
			_Reader.Skip(_Reader.BytesLeft);
		}

		/// <summary>
		/// Reconstructs Nintendo 64's two-stage eight-sample vector predictor with unsigned C accumulation.
		/// </summary>
		private int DecodeN64(byte[] packet, int packetOffset, int packetLength, int numberOfSamples)
		{
			Array.Clear(_N64Coefficients);
			if (_ExtraData.Length != 0)
			{
				_ExtraReader.Initialize(_ExtraData, 0, _ExtraData.Length);
				var version = _ExtraReader.ReadUInt16BigEndian();
				var order = _ExtraReader.ReadUInt16BigEndian();
				var entries = _ExtraReader.ReadUInt16BigEndian();
				if (version != 1 || order != 2 || entries > 8)
					return FfmpegError.InvalidData;
				for (var index = 0; index < order * entries * 8; index++)
					_N64Coefficients[index] = _ExtraReader.ReadInt16BigEndian();
			}
			for (var block = 0; block < packetLength / 9; block++)
			{
				Array.Clear(_N64History);
				_N64History[6] = (short)_Status[0].Sample2;
				_N64History[7] = (short)_Status[0].Sample1;
				var input = packetOffset + block * 9;
				var scale = 1 << (packet[input] >> 4 & 15);
				_ = Math.Min(packet[input] & 15, 8);
				for (var index = 0; index < 16; index += 2)
				{
					var value = packet[input + 1 + index / 2];
					var first = value >> 4;
					var second = value & 15;
					_N64Codes[index] = (first >= 8 ? first - 16 : first) * scale;
					_N64Codes[index + 1] = (second >= 8 ? second - 16 : second) * scale;
				}
				for (var subframe = 0; subframe < 2; subframe++)
				{
					for (var index = 0; index < 8; index++)
					{
						uint delta = 0;
						for (var order = 0; order < 2; order++)
							delta = unchecked(delta + (uint)(_N64Coefficients[order * 8 + index] * _N64History[6 + order]));
						for (var previous = index - 1; previous > -1; previous--)
							for (var order = 1; order < 2; order++)
								delta = unchecked(delta + (uint)(_N64Codes[subframe * 8 + index - 1 - previous] *
									_N64Coefficients[order * 8 + previous]));
						var sample = unchecked((int)((uint)(_N64Codes[subframe * 8 + index] * 2048) + delta)) / 2048;
						_N64Output[subframe * 8 + index] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
					}
					_N64History[6] = _N64Output[subframe * 8 + 6];
					_N64History[7] = _N64Output[subframe * 8 + 7];
				}
				Array.Copy(_N64Output, 0, _Samples, block * 16, 16);
				_Status[0].Sample2 = _N64History[6];
				_Status[0].Sample1 = _N64History[7];
			}
			_Reader.Skip(_Reader.BytesLeft);
			return 0;
		}

		/// <summary>
		/// Decodes PlayStation ADPCM sound units, loop flags, predictor filters, and interleaved channel blocks.
		/// </summary>
		private int DecodePlayStation(int numberOfSamples)
		{
			if (_CodecId == AudioCodecId.AdpcmPsxc)
			{
				var blocks = _Reader.BytesLeft / _BlockAlign;
				var samplesPerBlock = (_BlockAlign - 1) / _Channels * 2;
				for (var block = 0; block < blocks; block++)
				{
					for (var channel = 0; channel < _Channels; channel++)
					{
						var output = channel * numberOfSamples + block * samplesPerBlock;
						var header = _Reader.ReadByte();
						var shift = header & 15;
						var filter = header >> 4;
						if (filter >= 5)
							return FfmpegError.InvalidData;
						var value = 0;
						for (var sample = 0; sample < samplesPerBlock; sample++)
						{
							int scale;
							if ((sample & 1) != 0)
								scale = value >> 4;
							else
							{
								value = _Reader.ReadByte();
								scale = value & 15;
							}
							scale = scale >= 8 ? scale - 16 : scale;
							scale *= 1 << 12;
							var decoded = (scale >> shift) +
								(_Status[channel].Sample1 * AdpcmTables.Xa[filter, 0] + _Status[channel].Sample2 * AdpcmTables.Xa[filter, 1]) / 64;
							_Samples[output++] = (short)Math.Clamp(decoded, short.MinValue, short.MaxValue);
							_Status[channel].Sample2 = _Status[channel].Sample1;
							_Status[channel].Sample1 = decoded;
						}
					}
				}
				return 0;
			}

			var effectiveBlockAlign = Math.Max(_BlockAlign, 16 * _Channels);
			var blocksCount = _Reader.BytesLeft / effectiveBlockAlign;
			var numberPerBlock = 28 * effectiveBlockAlign / (16 * _Channels);
			for (var block = 0; block < blocksCount; block++)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var output = channel * numberOfSamples + block * numberPerBlock;
					for (var subBlock = 0; subBlock < numberPerBlock / 28; subBlock++)
					{
						var header = _Reader.ReadByte();
						var shift = header & 15;
						var filter = header >> 4;
						if (filter >= 5)
							return FfmpegError.InvalidData;
						var flag = _Reader.ReadByte() & 7;
						var value = 0;
						for (var sample = 0; sample < 28; sample++)
						{
							int scale;
							if ((sample & 1) != 0)
								scale = value >> 4;
							else
							{
								value = _Reader.ReadByte();
								scale = value & 15;
							}
							scale = scale >= 8 ? scale - 16 : scale;
							var decoded = 0;
							if (flag < 7)
							{
								scale *= 1 << 12;
								decoded = (scale >> shift) +
									(_Status[channel].Sample1 * AdpcmTables.Xa[filter, 0] + _Status[channel].Sample2 * AdpcmTables.Xa[filter, 1]) / 64;
							}
							_Samples[output++] = (short)Math.Clamp(decoded, short.MinValue, short.MaxValue);
							_Status[channel].Sample2 = _Status[channel].Sample1;
							_Status[channel].Sample1 = decoded;
						}
					}
				}
			}
			return 0;
		}

		private int DecodeSanyo(byte[] packet, int packetOffset, int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				_Status[channel].Step = _Reader.ReadInt16LittleEndian();
			}
			var bitOffset = packetOffset + 4 * _Channels;
			if (_BitReader.Initialize(packet, bitOffset, _Reader.BytesLeft * 8, true) < 0)
				return FfmpegError.InvalidData;
			for (var sample = 0; sample < numberOfSamples; sample++)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var bits = (int)_BitReader.ReadBits(_BitsPerCodedSample);
					var decoded = _BitsPerCodedSample == 3 ? AdpcmSampleExpansion.Sanyo3(_Status[channel], bits) :
						_BitsPerCodedSample == 4 ? AdpcmSampleExpansion.Sanyo4(_Status[channel], bits) :
						AdpcmSampleExpansion.Sanyo5(_Status[channel], bits);
					_Samples[channel * numberOfSamples + sample] = decoded;
				}
			}
			_BitReader.Align();
			_Reader.Skip(_BitReader.Position / 8);
			return 0;
		}

		/// <summary>
		/// Reads embedded or extradata THP coefficient tables and reconstructs each planar channel sequentially.
		/// </summary>
		private int DecodeThp(int numberOfSamples)
		{
			var littleEndian = _CodecId == AudioCodecId.AdpcmThpLittleEndian;
			if (_ExtraData.Length != 0)
			{
				if (_ExtraData.Length < 32 * _Channels)
					return FfmpegError.InvalidData;
				_ExtraReader.Initialize(_ExtraData, 0, _ExtraData.Length);
				for (var channel = 0; channel < _Channels; channel++)
					for (var index = 0; index < 16; index++)
						_ThpCoefficients[channel * 16 + index] = littleEndian
							? _ExtraReader.ReadInt16LittleEndian()
							: _ExtraReader.ReadInt16BigEndian();
			} else
			{
				_Reader.Skip(8);
				for (var channel = 0; channel < _Channels; channel++)
					for (var index = 0; index < 16; index++)
						_ThpCoefficients[channel * 16 + index] = littleEndian
							? _Reader.ReadInt16LittleEndian()
							: _Reader.ReadInt16BigEndian();
				if (!_HasThpStatus)
				{
					for (var channel = 0; channel < _Channels; channel++)
					{
						_Status[channel].Sample1 = littleEndian ? _Reader.ReadInt16LittleEndian() : _Reader.ReadInt16BigEndian();
						_Status[channel].Sample2 = littleEndian ? _Reader.ReadInt16LittleEndian() : _Reader.ReadInt16BigEndian();
					}
					_HasThpStatus = true;
				} else
				{
					_Reader.Skip(_Channels * 4);
				}
			}

			for (var channel = 0; channel < _Channels; channel++)
			{
				var output = channel * numberOfSamples;
				for (var block = 0; block < (numberOfSamples + 13) / 14; block++)
				{
					var value = _Reader.ReadByte();
					var index = value >> 4 & 7;
					var exponent = value & 15;
					var factor1 = (long)_ThpCoefficients[channel * 16 + index * 2];
					var factor2 = (long)_ThpCoefficients[channel * 16 + index * 2 + 1];
					for (var sample = 0; sample < 14 && block * 14 + sample < numberOfSamples; sample++)
					{
						int sampleData;
						if ((sample & 1) != 0)
							sampleData = value & 15;
						else
						{
							value = _Reader.ReadByte();
							sampleData = value >> 4;
						}
						sampleData = sampleData >= 8 ? sampleData - 16 : sampleData;
						var decoded = (int)((_Status[channel].Sample1 * factor1 + _Status[channel].Sample2 * factor2) >> 11) +
							sampleData * (1 << exponent);
						decoded = Math.Clamp(decoded, short.MinValue, short.MaxValue);
						_Samples[output++] = (short)decoded;
						_Status[channel].Sample2 = _Status[channel].Sample1;
						_Status[channel].Sample1 = decoded;
					}
				}
			}
			return 0;
		}

		/// <summary>
		/// Follows EA R1/R2/R3 per-channel offset tables, literal blocks, and persistent predictor state.
		/// </summary>
		private int DecodeEaR(int numberOfSamples)
		{
			var bigEndian = _CodecId == AudioCodecId.AdpcmEaR3;
			Span<int> offsets = stackalloc int[6];
			_Reader.Skip(4);
			for (var channel = 0; channel < _Channels; channel++)
			{
				var offset = bigEndian ? _Reader.ReadUInt32BigEndian() : _Reader.ReadUInt32LittleEndian();
				offsets[channel] = unchecked((int)offset) + (_Channels + 1) * 4;
			}
			for (var channel = 0; channel < _Channels; channel++)
			{
				_Reader.Seek(offsets[channel]);
				var output = channel * numberOfSamples;
				int currentSample;
				int previousSample;
				if (_CodecId == AudioCodecId.AdpcmEaR1)
				{
					currentSample = _Reader.ReadInt16LittleEndian();
					previousSample = _Reader.ReadInt16LittleEndian();
				} else
				{
					currentSample = _Status[channel].Predictor;
					previousSample = _Status[channel].PreviousSample;
				}
				for (var block = 0; block < numberOfSamples / 28; block++)
				{
					var value = _Reader.ReadByte();
					if (value == 0xee)
					{
						currentSample = _Reader.ReadInt16BigEndian();
						previousSample = _Reader.ReadInt16BigEndian();
						for (var sample = 0; sample < 28; sample++)
							_Samples[output++] = (short)_Reader.ReadInt16BigEndian();
					} else
					{
						var coefficient1 = AdpcmTables.Ea[value >> 4];
						var coefficient2 = AdpcmTables.Ea[(value >> 4) + 4];
						var shift = 20 - (value & 15);
						for (var sample = 0; sample < 28; sample++)
						{
							int nextSample;
							if ((sample & 1) != 0)
							{
								var nibble = value & 15;
								nibble = nibble >= 8 ? nibble - 16 : nibble;
								nextSample = unchecked((int)((uint)nibble << shift));
							} else
							{
								value = _Reader.ReadByte();
								var nibble = value >> 4;
								nibble = nibble >= 8 ? nibble - 16 : nibble;
								nextSample = unchecked((int)((uint)nibble << shift));
							}
							nextSample = unchecked(nextSample + currentSample * coefficient1 + previousSample * coefficient2);
							nextSample = Math.Clamp(nextSample >> 8, short.MinValue, short.MaxValue);
							previousSample = currentSample;
							currentSample = nextSample;
							_Samples[output++] = (short)currentSample;
						}
					}
				}
				if (_CodecId != AudioCodecId.AdpcmEaR1)
				{
					_Status[channel].Predictor = currentSample;
					_Status[channel].PreviousSample = previousSample;
				}
			}
			_Reader.Skip(_Reader.BytesLeft);
			return 0;
		}

		private int DecodeMtaf(int numberOfSamples)
		{
			for (var channel = 0; channel < _Channels; channel += 2)
			{
				_Reader.Skip(4);
				_Status[channel].Step = _Reader.ReadUInt16LittleEndian() & 31;
				_Status[channel + 1].Step = _Reader.ReadUInt16LittleEndian() & 31;
				_Status[channel].Predictor = _Reader.ReadInt16LittleEndian();
				_Reader.Skip(2);
				_Status[channel + 1].Predictor = _Reader.ReadInt16LittleEndian();
				_Reader.Skip(2);
				var output = channel * numberOfSamples;
				for (var sample = 0; sample < numberOfSamples; sample += 2)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.Mtaf(_Status[channel], value & 15);
					_Samples[output++] = AdpcmSampleExpansion.Mtaf(_Status[channel], value >> 4);
				}
				output = (channel + 1) * numberOfSamples;
				for (var sample = 0; sample < numberOfSamples; sample += 2)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = AdpcmSampleExpansion.Mtaf(_Status[channel + 1], value & 15);
					_Samples[output++] = AdpcmSampleExpansion.Mtaf(_Status[channel + 1], value >> 4);
				}
			}
			return 0;
		}

		private void DecodeImaSsi(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[0], value >> 4);
				_Samples[output++] = AdpcmSampleExpansion.ImaQuickTime(_Status[stereo], value & 15);
			}
		}

		private void DecodeImaApm(int numberOfSamples, bool alp)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples / 2; count > 0; count--)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var value = _Reader.ReadByte();
					_Samples[output++] = alp
						? AdpcmSampleExpansion.ImaAlp(_Status[channel], value >> 4, 2)
						: AdpcmSampleExpansion.ImaQuickTime(_Status[channel], value >> 4);
					_Samples[output + stereo] = alp
						? AdpcmSampleExpansion.ImaAlp(_Status[channel], value & 15, 2)
						: AdpcmSampleExpansion.ImaQuickTime(_Status[channel], value & 15);
				}
				output += _Channels;
			}
		}

		private void DecodeImaWestwood(int numberOfSamples)
		{
			if (_VqaVersion == 3)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var output = channel * numberOfSamples;
					for (var count = numberOfSamples / 2; count > 0; count--)
					{
						var value = _Reader.ReadByte();
						_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value & 15, 3);
						_Samples[output++] = AdpcmSampleExpansion.Ima(_Status[channel], value >> 4, 3);
					}
				}
				return;
			}

			var stereo = _Channels == 2 ? 1 : 0;
			var outputOffset = 0;
			for (var count = numberOfSamples / 2; count > 0; count--)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					var value = _Reader.ReadByte();
					_Samples[outputOffset++] = AdpcmSampleExpansion.Ima(_Status[channel], value & 15, 3);
					_Samples[outputOffset + stereo] = AdpcmSampleExpansion.Ima(_Status[channel], value >> 4, 3);
				}
				outputOffset += _Channels;
			}
		}

		private void DecodeYamaha(int numberOfSamples)
		{
			var stereo = _Channels == 2 ? 1 : 0;
			var output = 0;
			for (var count = numberOfSamples >> (1 - stereo); count > 0; count--)
			{
				var value = _Reader.ReadByte();
				_Samples[output++] = AdpcmSampleExpansion.Yamaha(_Status[0], value & 15);
				_Samples[output++] = AdpcmSampleExpansion.Yamaha(_Status[stereo], value >> 4);
			}
		}

		/// <summary>
		/// Decodes Shockwave's variable-width IMA blocks with the exact 4095-delta block boundary.
		/// </summary>
		private int DecodeSwf(byte[] packet, int packetOffset, int packetLength, int numberOfSamples)
		{
			if (_BitReader.Initialize(packet, packetOffset, packetLength * 8) < 0)
				return FfmpegError.InvalidData;
			var bitsPerCode = (int)_BitReader.ReadBits(2) + 2;
			var table = AdpcmTables.SwfIndex[bitsPerCode - 2];
			var firstBit = 1 << (bitsPerCode - 2);
			var signMask = 1 << (bitsPerCode - 1);
			var size = packetLength * 8;
			var output = 0;
			while (_BitReader.Position <= size - 22 * _Channels)
			{
				for (var channel = 0; channel < _Channels; channel++)
				{
					_Status[channel].Predictor = _BitReader.ReadSignedBits(16);
					_Samples[output++] = (short)_Status[channel].Predictor;
					_Status[channel].StepIndex = (short)_BitReader.ReadBits(6);
				}
				for (var count = 0; _BitReader.Position <= size - bitsPerCode * _Channels && count < 4095; count++)
				{
					for (var channel = 0; channel < _Channels; channel++)
					{
						var delta = (int)_BitReader.ReadBits(bitsPerCode);
						var step = AdpcmTables.Step[_Status[channel].StepIndex];
						var difference = 0;
						var bit = firstBit;
						do
						{
							if ((delta & bit) != 0)
								difference += step;
							step >>= 1;
							bit >>= 1;
						} while (bit != 0);
						difference += step;
						_Status[channel].Predictor += (delta & signMask) != 0 ? -difference : difference;
						_Status[channel].StepIndex += table[delta & ~signMask];
						_Status[channel].StepIndex = (short)Math.Clamp((int)_Status[channel].StepIndex, 0, 88);
						_Status[channel].Predictor = Math.Clamp(_Status[channel].Predictor, short.MinValue, short.MaxValue);
						if (output < numberOfSamples * _Channels)
							_Samples[output++] = (short)_Status[channel].Predictor;
					}
				}
			}
			return 0;
		}
	}
}
