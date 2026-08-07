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
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs
{
	/// <summary>
	/// Ports FFmpeg's pcm.c decoder paths while preserving their coded- and decoded-sample representations.
	/// </summary>
	public sealed class PcmDecoder
	{
		private readonly AudioCodecId _CodecId;
		private readonly AudioSampleFormat _SampleFormat;
		private readonly int _CodedSampleSize;
		private readonly int _OutputSampleSize;
		private readonly int _BitsPerCodedSample;
		private readonly short[] _LookupTable;

		private PcmDecoder(
			AudioCodecId codecId,
			AudioSampleFormat sampleFormat,
			int codedSampleSize,
			int outputSampleSize,
			int bitsPerCodedSample,
			short[] lookupTable)
		{
			_CodecId = codecId;
			_SampleFormat = sampleFormat;
			_CodedSampleSize = codedSampleSize;
			_OutputSampleSize = outputSampleSize;
			_BitsPerCodedSample = bitsPerCodedSample;
			_LookupTable = lookupTable;
		}

		public AudioCodecId CodecId => _CodecId;
		public AudioSampleFormat SampleFormat => _SampleFormat;

		public static int Initialize(AudioCodecId codecId, int bitsPerCodedSample, out PcmDecoder decoder)
		{
			decoder = null;
			if (!TryGetConfiguration(codecId, out var sampleFormat, out var codedSize, out var outputSize))
				return FfmpegError.InvalidArgument;

			if ((codecId == AudioCodecId.PcmF16LittleEndian || codecId == AudioCodecId.PcmF24LittleEndian) &&
				(bitsPerCodedSample < 1 || bitsPerCodedSample > 24))
			{
				return FfmpegError.InvalidData;
			}

			short[] lookupTable = null;
			if (codecId == AudioCodecId.PcmALaw || codecId == AudioCodecId.PcmMuLaw || codecId == AudioCodecId.PcmVidc)
			{
				lookupTable = new short[256];
				for (var index = 0; index < lookupTable.Length; index++)
				{
					lookupTable[index] = codecId == AudioCodecId.PcmALaw
						? (short)DecodeALaw((byte)index)
						: codecId == AudioCodecId.PcmMuLaw
							? (short)DecodeMuLaw((byte)index)
							: (short)DecodeVidc((byte)index);
				}
			}

			decoder = new PcmDecoder(codecId, sampleFormat, codedSize, outputSize, bitsPerCodedSample, lookupTable);
			return 0;
		}

		public int GetOutputBufferSize(int packetSize, int channels, out int outputSize)
		{
			outputSize = 0;
			if (channels == 0)
				return FfmpegError.InvalidArgument;

			var codedFrameSize = channels * _CodedSampleSize;
			if (codedFrameSize != 0 && packetSize % codedFrameSize != 0 && packetSize < codedFrameSize)
				return FfmpegError.InvalidData;

			var usableSize = codedFrameSize == 0 ? packetSize : packetSize - packetSize % codedFrameSize;
			var codedSamples = usableSize / _CodedSampleSize;
			var decodedSamples = _CodecId == AudioCodecId.PcmLxf ? codedSamples * 2 : codedSamples;
			if (decodedSamples > int.MaxValue / _OutputSampleSize)
				return FfmpegError.InvalidArgument;

			outputSize = decodedSamples * _OutputSampleSize;
			return usableSize;
		}

		/// <summary>
		/// Decodes one packet in the same operation order as pcm_decode_frame and writes planar channels consecutively.
		/// </summary>
		public int Decode(ReadOnlySpan<byte> packet, int channels, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			var sizeResult = GetOutputBufferSize(packet.Length, channels, out var outputSize);
			if (sizeResult < 0)
				return sizeResult;
			if (output.Length < outputSize)
				return FfmpegError.InvalidArgument;

			var codedSamples = sizeResult / _CodedSampleSize;
			var samplesPerBlock = _CodecId == AudioCodecId.PcmLxf ? 2 : 1;
			var numberOfSamples = codedSamples * samplesPerBlock / channels;
			var planar = IsPlanar(_SampleFormat);
			var planeCount = planar ? channels : 1;
			var planeSize = planar ? numberOfSamples * _OutputSampleSize : outputSize;

			var result = DecodeSamples(packet.Slice(0, sizeResult), channels, codedSamples, output.Slice(0, outputSize));
			if (result < 0)
				return result;

			frame = new AudioFrameInfo(numberOfSamples, channels, _SampleFormat, planeCount, planeSize, outputSize);
			return packet.Length;
		}

		/// <summary>
		/// Implements every decoder switch branch from pcm.c without allocations or reordered sample operations.
		/// </summary>
		private int DecodeSamples(ReadOnlySpan<byte> source, int channels, int sampleCount, Span<byte> destination)
		{
			switch (_CodecId)
			{
				case AudioCodecId.PcmU32LittleEndian:
					Decode32(source, destination, sampleCount, false, 0, 0x80000000U);
					break;
				case AudioCodecId.PcmU32BigEndian:
					Decode32(source, destination, sampleCount, true, 0, 0x80000000U);
					break;
				case AudioCodecId.PcmS24LittleEndian:
					Decode24To32(source, destination, sampleCount, false, 8, 0);
					break;
				case AudioCodecId.PcmS24LittleEndianPlanar:
					Decode24To32Planar(source, destination, sampleCount, channels, false, 8, 0);
					break;
				case AudioCodecId.PcmS24BigEndian:
					Decode24To32(source, destination, sampleCount, true, 8, 0);
					break;
				case AudioCodecId.PcmU24LittleEndian:
					Decode24To32(source, destination, sampleCount, false, 8, 0x800000);
					break;
				case AudioCodecId.PcmU24BigEndian:
					Decode24To32(source, destination, sampleCount, true, 8, 0x800000);
					break;
				case AudioCodecId.PcmS24Daud:
					DecodeDaud(source, destination, sampleCount);
					break;
				case AudioCodecId.PcmU16LittleEndian:
					Decode16(source, destination, sampleCount, false, 0x8000);
					break;
				case AudioCodecId.PcmU16BigEndian:
					Decode16(source, destination, sampleCount, true, 0x8000);
					break;
				case AudioCodecId.PcmS8:
					for (var index = 0; index < sampleCount; index++)
						destination[index] = unchecked((byte)(source[index] + 128));
					break;
				case AudioCodecId.PcmSga:
					DecodeSga(source, destination, sampleCount);
					break;
				case AudioCodecId.PcmS8Planar:
					for (var index = 0; index < sampleCount; index++)
						destination[index] = unchecked((byte)(source[index] + 128));
					break;
				case AudioCodecId.PcmS64BigEndian:
				case AudioCodecId.PcmF64BigEndian:
					Decode64(source, destination, sampleCount, true);
					break;
				case AudioCodecId.PcmF32BigEndian:
				case AudioCodecId.PcmS32BigEndian:
					Decode32(source, destination, sampleCount, true, 0, 0);
					break;
				case AudioCodecId.PcmS16BigEndian:
					Decode16(source, destination, sampleCount, true, 0);
					break;
				case AudioCodecId.PcmS16BigEndianPlanar:
					Decode16Planar(source, destination, sampleCount, channels, true, 0);
					break;
				case AudioCodecId.PcmF64LittleEndian:
				case AudioCodecId.PcmS64LittleEndian:
				case AudioCodecId.PcmF32LittleEndian:
				case AudioCodecId.PcmS32LittleEndian:
				case AudioCodecId.PcmS16LittleEndian:
				case AudioCodecId.PcmU8:
					source.CopyTo(destination);
					break;
				case AudioCodecId.PcmF24LittleEndian:
				case AudioCodecId.PcmF16LittleEndian:
					source.CopyTo(destination);
					ScaleFloatSamples(destination, sampleCount);
					break;
				case AudioCodecId.PcmS16LittleEndianPlanar:
				case AudioCodecId.PcmS32LittleEndianPlanar:
					source.CopyTo(destination);
					break;
				case AudioCodecId.PcmALaw:
				case AudioCodecId.PcmMuLaw:
				case AudioCodecId.PcmVidc:
					DecodeLookup(source, destination, sampleCount);
					break;
				case AudioCodecId.PcmLxf:
					DecodeLxf(source, destination, sampleCount, channels);
					break;
				default:
					return -1;
			}

			return 0;
		}

		/// <summary>
		/// Reproduces pcm_decode_init's codec table, including coded widths that differ from output widths.
		/// </summary>
		private static bool TryGetConfiguration(
			AudioCodecId codecId,
			out AudioSampleFormat sampleFormat,
			out int codedSize,
			out int outputSize)
		{
			sampleFormat = AudioSampleFormat.None;
			codedSize = 0;
			outputSize = 0;
			switch (codecId)
			{
				case AudioCodecId.PcmALaw:
				case AudioCodecId.PcmMuLaw:
				case AudioCodecId.PcmVidc:
					sampleFormat = AudioSampleFormat.Signed16; codedSize = 1; outputSize = 2; break;
				case AudioCodecId.PcmS8:
				case AudioCodecId.PcmSga:
				case AudioCodecId.PcmU8:
					sampleFormat = AudioSampleFormat.Unsigned8; codedSize = 1; outputSize = 1; break;
				case AudioCodecId.PcmS8Planar:
					sampleFormat = AudioSampleFormat.Unsigned8Planar; codedSize = 1; outputSize = 1; break;
				case AudioCodecId.PcmS16BigEndian:
				case AudioCodecId.PcmS16LittleEndian:
				case AudioCodecId.PcmU16BigEndian:
				case AudioCodecId.PcmU16LittleEndian:
					sampleFormat = AudioSampleFormat.Signed16; codedSize = 2; outputSize = 2; break;
				case AudioCodecId.PcmS16BigEndianPlanar:
				case AudioCodecId.PcmS16LittleEndianPlanar:
					sampleFormat = AudioSampleFormat.Signed16Planar; codedSize = 2; outputSize = 2; break;
				case AudioCodecId.PcmS24Daud:
					sampleFormat = AudioSampleFormat.Signed16; codedSize = 3; outputSize = 2; break;
				case AudioCodecId.PcmS24BigEndian:
				case AudioCodecId.PcmS24LittleEndian:
				case AudioCodecId.PcmU24BigEndian:
				case AudioCodecId.PcmU24LittleEndian:
					sampleFormat = AudioSampleFormat.Signed32; codedSize = 3; outputSize = 4; break;
				case AudioCodecId.PcmS24LittleEndianPlanar:
					sampleFormat = AudioSampleFormat.Signed32Planar; codedSize = 3; outputSize = 4; break;
				case AudioCodecId.PcmS32BigEndian:
				case AudioCodecId.PcmS32LittleEndian:
				case AudioCodecId.PcmU32BigEndian:
				case AudioCodecId.PcmU32LittleEndian:
					sampleFormat = AudioSampleFormat.Signed32; codedSize = 4; outputSize = 4; break;
				case AudioCodecId.PcmS32LittleEndianPlanar:
					sampleFormat = AudioSampleFormat.Signed32Planar; codedSize = 4; outputSize = 4; break;
				case AudioCodecId.PcmF32BigEndian:
				case AudioCodecId.PcmF32LittleEndian:
				case AudioCodecId.PcmF16LittleEndian:
				case AudioCodecId.PcmF24LittleEndian:
					sampleFormat = AudioSampleFormat.Float; codedSize = 4; outputSize = 4; break;
				case AudioCodecId.PcmF64BigEndian:
				case AudioCodecId.PcmF64LittleEndian:
					sampleFormat = AudioSampleFormat.Double; codedSize = 8; outputSize = 8; break;
				case AudioCodecId.PcmS64BigEndian:
				case AudioCodecId.PcmS64LittleEndian:
					sampleFormat = AudioSampleFormat.Signed64; codedSize = 8; outputSize = 8; break;
				case AudioCodecId.PcmLxf:
					sampleFormat = AudioSampleFormat.Signed32Planar; codedSize = 5; outputSize = 4; break;
				default:
					return false;
			}

			return true;
		}

		private static void Decode16(ReadOnlySpan<byte> source, Span<byte> destination, int count, bool bigEndian, ushort offset)
		{
			for (var index = 0; index < count; index++)
			{
				var sourceOffset = index * 2;
				var value = bigEndian
					? BinaryPrimitives.ReadUInt16BigEndian(source.Slice(sourceOffset, 2))
					: BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(sourceOffset, 2));
				BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(sourceOffset, 2), unchecked((ushort)(value - offset)));
			}
		}

		private static void Decode16Planar(ReadOnlySpan<byte> source, Span<byte> destination, int count, int channels, bool bigEndian, ushort offset)
		{
			var samplesPerChannel = count / channels;
			var sourceOffset = 0;
			for (var channel = 0; channel < channels; channel++)
			{
				var destinationOffset = channel * samplesPerChannel * 2;
				for (var index = 0; index < samplesPerChannel; index++)
				{
					var value = bigEndian
						? BinaryPrimitives.ReadUInt16BigEndian(source.Slice(sourceOffset, 2))
						: BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(sourceOffset, 2));
					BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(destinationOffset, 2), unchecked((ushort)(value - offset)));
					sourceOffset += 2;
					destinationOffset += 2;
				}
			}
		}

		private static void Decode24To32(ReadOnlySpan<byte> source, Span<byte> destination, int count, bool bigEndian, int shift, uint offset)
		{
			for (var index = 0; index < count; index++)
			{
				var sourceOffset = index * 3;
				var value = ReadUInt24(source, sourceOffset, bigEndian);
				var decoded = unchecked((value - offset) << shift);
				BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(index * 4, 4), decoded);
			}
		}

		private static void Decode24To32Planar(ReadOnlySpan<byte> source, Span<byte> destination, int count, int channels, bool bigEndian, int shift, uint offset)
		{
			var samplesPerChannel = count / channels;
			var sourceOffset = 0;
			for (var channel = 0; channel < channels; channel++)
			{
				var destinationOffset = channel * samplesPerChannel * 4;
				for (var index = 0; index < samplesPerChannel; index++)
				{
					var value = ReadUInt24(source, sourceOffset, bigEndian);
					var decoded = unchecked((value - offset) << shift);
					BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(destinationOffset, 4), decoded);
					sourceOffset += 3;
					destinationOffset += 4;
				}
			}
		}

		private static void Decode32(ReadOnlySpan<byte> source, Span<byte> destination, int count, bool bigEndian, int shift, uint offset)
		{
			for (var index = 0; index < count; index++)
			{
				var sourceOffset = index * 4;
				var value = bigEndian
					? BinaryPrimitives.ReadUInt32BigEndian(source.Slice(sourceOffset, 4))
					: BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(sourceOffset, 4));
				var decoded = unchecked((value - offset) << shift);
				BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(sourceOffset, 4), decoded);
			}
		}

		private static void Decode64(ReadOnlySpan<byte> source, Span<byte> destination, int count, bool bigEndian)
		{
			for (var index = 0; index < count; index++)
			{
				var sourceOffset = index * 8;
				var value = bigEndian
					? BinaryPrimitives.ReadUInt64BigEndian(source.Slice(sourceOffset, 8))
					: BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(sourceOffset, 8));
				BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(sourceOffset, 8), value);
			}
		}

		private static void DecodeDaud(ReadOnlySpan<byte> source, Span<byte> destination, int count)
		{
			for (var index = 0; index < count; index++)
			{
				var sourceOffset = index * 3;
				var value = ReadUInt24(source, sourceOffset, true);
				value >>= 4;
				var decoded = (ushort)(ReverseByte((byte)(value >> 8)) + (ReverseByte((byte)value) << 8));
				BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(index * 2, 2), decoded);
			}
		}

		private static void DecodeSga(ReadOnlySpan<byte> source, Span<byte> destination, int count)
		{
			for (var index = 0; index < count; index++)
			{
				var sign = source[index] >> 7;
				var magnitude = source[index] & 0x7f;
				destination[index] = (byte)(sign != 0 ? 128 - magnitude : 128 + magnitude);
			}
		}

		private void DecodeLookup(ReadOnlySpan<byte> source, Span<byte> destination, int count)
		{
			for (var index = 0; index < count; index++)
				BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(index * 2, 2), _LookupTable[source[index]]);
		}

		private void ScaleFloatSamples(Span<byte> destination, int count)
		{
			var scale = (float)(1.0 / (1 << (_BitsPerCodedSample - 1)));
			for (var index = 0; index < count; index++)
			{
				var offset = index * 4;
				var bits = BinaryPrimitives.ReadInt32LittleEndian(destination.Slice(offset, 4));
				var value = BitConverter.Int32BitsToSingle(bits);
				value *= scale;
				BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), BitConverter.SingleToInt32Bits(value));
			}
		}

		private static void DecodeLxf(ReadOnlySpan<byte> source, Span<byte> destination, int count, int channels)
		{
			var blocksPerChannel = count / channels;
			var sourceOffset = 0;
			for (var channel = 0; channel < channels; channel++)
			{
				var destinationOffset = channel * blocksPerChannel * 8;
				for (var index = 0; index < blocksPerChannel; index++)
				{
					var first = ((uint)source[sourceOffset + 2] << 28) |
						((uint)source[sourceOffset + 1] << 20) |
						((uint)source[sourceOffset] << 12) |
						((uint)(source[sourceOffset + 2] & 0x0f) << 8) |
						source[sourceOffset + 1];
					var second = ((uint)source[sourceOffset + 4] << 24) |
						((uint)source[sourceOffset + 3] << 16) |
						((uint)(source[sourceOffset + 2] & 0xf0) << 8) |
						((uint)source[sourceOffset + 4] << 4) |
						((uint)source[sourceOffset + 3] >> 4);
					BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(destinationOffset, 4), first);
					BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(destinationOffset + 4, 4), second);
					sourceOffset += 5;
					destinationOffset += 8;
				}
			}
		}

		private static uint ReadUInt24(ReadOnlySpan<byte> source, int offset, bool bigEndian)
		{
			return bigEndian
				? ((uint)source[offset] << 16) | ((uint)source[offset + 1] << 8) | source[offset + 2]
				: source[offset] | ((uint)source[offset + 1] << 8) | ((uint)source[offset + 2] << 16);
		}

		private static byte ReverseByte(byte value)
		{
			value = (byte)((value >> 4) | (value << 4));
			value = (byte)(((value & 0xcc) >> 2) | ((value & 0x33) << 2));
			return (byte)(((value & 0xaa) >> 1) | ((value & 0x55) << 1));
		}

		private static int DecodeALaw(byte value)
		{
			value ^= 0x55;
			var decoded = value & 0x0f;
			var segment = (value & 0x70) >> 4;
			if (segment != 0)
				decoded = (decoded + decoded + 1 + 32) << (segment + 2);
			else
				decoded = (decoded + decoded + 1) << 3;
			return (value & 0x80) != 0 ? decoded : -decoded;
		}

		private static int DecodeMuLaw(byte value)
		{
			value = unchecked((byte)~value);
			var decoded = ((value & 0x0f) << 3) + 0x84;
			decoded <<= (value & 0x70) >> 4;
			return (value & 0x80) != 0 ? 0x84 - decoded : decoded - 0x84;
		}

		private static int DecodeVidc(byte value)
		{
			var decoded = (((value & 0x1e) >> 1) << 3) + 0x84;
			decoded <<= (value & 0xe0) >> 5;
			return (value & 1) != 0 ? 0x84 - decoded : decoded - 0x84;
		}

		private static bool IsPlanar(AudioSampleFormat sampleFormat)
		{
			return sampleFormat == AudioSampleFormat.Unsigned8Planar ||
				sampleFormat == AudioSampleFormat.Signed16Planar ||
				sampleFormat == AudioSampleFormat.Signed32Planar ||
				sampleFormat == AudioSampleFormat.FloatPlanar ||
				sampleFormat == AudioSampleFormat.DoublePlanar ||
				sampleFormat == AudioSampleFormat.Signed64Planar;
		}
	}
}
