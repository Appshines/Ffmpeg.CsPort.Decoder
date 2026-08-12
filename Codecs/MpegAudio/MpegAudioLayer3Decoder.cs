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
 * PORT-NOTE: 1:1 translation. Performance-motivated, semantics-preserving transformations
 * applied (see repository history); bit-exactness remains verified by the conformance tests.
 */
using System;
using System.Runtime.CompilerServices;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.MpegAudio
{
	/// <summary>
	/// Implements the Layer III side-information, reservoir, Huffman, stereo, antialias, and hybrid-synthesis path.
	/// </summary>
	public sealed partial class MpegAudioDecoder
	{
		private const int ModeExtensionIntensityStereo = 1;
		private const int ModeExtensionMiddleSideStereo = 2;

		/// <summary>
		/// Parses Layer III side information, reconstructs the byte reservoir, and decodes both MPEG-1 granules or one LSF granule.
		/// </summary>
		private int DecodeLayer3(MpegAudioHeader header, byte[] packet, int packetOffset, int packetLength)
		{
			int mainDataBegin;
			int granuleCount;
			if (header.LowSamplingFrequency != 0)
			{
				mainDataBegin = (int)_Reader.ReadBits(8);
				_Reader.SkipBits(header.Channels);
				granuleCount = 1;
			} else
			{
				mainDataBegin = (int)_Reader.ReadBits(9);
				_Reader.SkipBits(header.Channels == 2 ? 3 : 5);
				granuleCount = 2;
				for (var channel = 0; channel < header.Channels; channel++)
				{
					_Granules[channel, 0].Scfsi = 0;
					_Granules[channel, 1].Scfsi = (byte)_Reader.ReadBits(4);
				}
			}

			for (var granule = 0; granule < granuleCount; granule++)
			{
				for (var channel = 0; channel < header.Channels; channel++)
				{
					var state = _Granules[channel, granule];
					state.Part23Length = (int)_Reader.ReadBits(12);
					state.BigValues = (int)_Reader.ReadBits(9);
					if (state.BigValues > 288) return FfmpegError.InvalidData;
					state.GlobalGain = (int)_Reader.ReadBits(8);
					if ((header.ModeExtension & (ModeExtensionMiddleSideStereo | ModeExtensionIntensityStereo)) == ModeExtensionMiddleSideStereo)
						state.GlobalGain -= 2;
					state.ScaleFactorCompress = (int)_Reader.ReadBits(header.LowSamplingFrequency != 0 ? 9 : 4);
					var blockSplit = _Reader.ReadBit() != 0;
					if (blockSplit)
					{
						state.BlockType = (byte)_Reader.ReadBits(2);
						if (state.BlockType == 0) return FfmpegError.InvalidData;
						state.SwitchPoint = (byte)_Reader.ReadBit();
						for (var index = 0; index < 2; index++) state.TableSelect[index] = (int)_Reader.ReadBits(5);
						for (var index = 0; index < 3; index++) state.SubblockGain[index] = (int)_Reader.ReadBits(3);
						InitializeShortRegion(header, state);
					} else
					{
						state.BlockType = 0; state.SwitchPoint = 0;
						for (var index = 0; index < 3; index++) state.TableSelect[index] = (int)_Reader.ReadBits(5);
						var regionAddress1 = (int)_Reader.ReadBits(4); var regionAddress2 = (int)_Reader.ReadBits(3);
						InitializeLongRegion(header, state, regionAddress1, regionAddress2);
					}
					RegionOffsetsToSizes(state);
					ComputeBandIndexes(header, state);
					state.Preflag = header.LowSamplingFrequency == 0 ? (int)_Reader.ReadBit() : 0;
					state.ScaleFactorScale = (byte)_Reader.ReadBit(); state.Count1TableSelect = (byte)_Reader.ReadBit();
				}
			}

			var sideInformationBytes = (_Reader.Position + 7) >> 3;
			var currentDataOffset = packetOffset + 4 + sideInformationBytes;
			var currentDataLength = packetLength - 4 - sideInformationBytes;
			if (currentDataLength < 0 || _ReservoirLength + currentDataLength > _MainData.Length)
				return FfmpegError.InvalidData;
			Array.Copy(_Reservoir, 0, _MainData, 0, _ReservoirLength);
			Array.Copy(packet, currentDataOffset, _MainData, _ReservoirLength, currentDataLength);
			var mainDataStart = _ReservoirLength - mainDataBegin;
			if (mainDataStart < 0)
			{
				for (var granule = 0; granule < granuleCount; granule++)
					for (var channel = 0; channel < header.Channels; channel++)
					{
						Array.Clear(_Granules[channel, granule].SubbandHybrid);
						ComputeImdct(_Granules[channel, granule], _SubbandSamples[channel], 18 * granule * SubbandLimit, _MdctBuffers[channel]);
					}
				UpdateReservoir(_ReservoirLength + currentDataLength);
				return granuleCount * 18;
			}
			if (_Reader.Initialize(_MainData, mainDataStart, (_ReservoirLength + currentDataLength - mainDataStart) * 8) < 0)
				return FfmpegError.InvalidData;

			Span<short> exponents = stackalloc short[576];
			for (var granule = 0; granule < granuleCount; granule++)
			{
				for (var channel = 0; channel < header.Channels; channel++)
				{
					var state = _Granules[channel, granule];
					var bitsPosition = _Reader.Position;
					ReadScaleFactors(header, channel, granule, state);
					ExponentsFromScaleFactors(header, state, exponents);
					HuffmanDecode(state, exponents, bitsPosition + state.Part23Length);
				}
				if (header.Mode == 1) ComputeStereo(header, _Granules[0, granule], _Granules[1, granule]);
				for (var channel = 0; channel < header.Channels; channel++)
				{
					var state = _Granules[channel, granule];
					ReorderBlock(header, state); ComputeAntialias(state);
					ComputeImdct(state, _SubbandSamples[channel], 18 * granule * SubbandLimit, _MdctBuffers[channel]);
				}
			}

			UpdateReservoir(_ReservoirLength + currentDataLength);
			return granuleCount * 18;
		}

		private void UpdateReservoir(int combinedLength)
		{
			_ReservoirLength = Math.Min(_Reservoir.Length, combinedLength);
			Array.Copy(_MainData, combinedLength - _ReservoirLength, _Reservoir, 0, _ReservoirLength);
		}

		private static void InitializeShortRegion(MpegAudioHeader header, MpegAudioGranule state)
		{
			if (state.BlockType == 2) state.RegionSize[0] = header.SampleRateIndex != 8 ? 18 : 36;
			else if (header.SampleRateIndex <= 2) state.RegionSize[0] = 18;
			else if (header.SampleRateIndex != 8) state.RegionSize[0] = 27;
			else state.RegionSize[0] = 54;
			state.RegionSize[1] = 288;
		}

		private static void InitializeLongRegion(MpegAudioHeader header, MpegAudioGranule state, int firstAddress, int secondAddress)
		{
			state.RegionSize[0] = MpegAudioTables.LongBandIndexes[header.SampleRateIndex * 23 + firstAddress + 1];
			var index = Math.Min(firstAddress + secondAddress + 2, 22);
			state.RegionSize[1] = MpegAudioTables.LongBandIndexes[header.SampleRateIndex * 23 + index];
		}

		private static void RegionOffsetsToSizes(MpegAudioGranule state)
		{
			var previous = 0; state.RegionSize[2] = 288;
			for (var index = 0; index < 3; index++)
			{
				var end = Math.Min(state.RegionSize[index], state.BigValues); state.RegionSize[index] = end - previous; previous = end;
			}
		}

		private static void ComputeBandIndexes(MpegAudioHeader header, MpegAudioGranule state)
		{
			if (state.BlockType == 2)
			{
				if (state.SwitchPoint != 0)
				{
					state.LongEnd = header.SampleRateIndex <= 2 ? 8 : 6; state.ShortStart = 3;
				} else
				{
					state.LongEnd = 0; state.ShortStart = 0;
				}
			} else
			{
				state.ShortStart = 13; state.LongEnd = 22;
			}
		}

		/// <summary>
		/// Expands MPEG-1 scfsi or MPEG-2/2.5 LSF scale-factor partitions without altering syntax-read order.
		/// </summary>
		private void ReadScaleFactors(MpegAudioHeader header, int channel, int granule, MpegAudioGranule state)
		{
			if (header.LowSamplingFrequency == 0)
			{
				var firstLength = MpegAudioTables.ScaleFactorLengths[state.ScaleFactorCompress];
				var secondLength = MpegAudioTables.ScaleFactorLengths[16 + state.ScaleFactorCompress];
				var destination = 0;
				if (state.BlockType == 2)
				{
					var firstCount = state.SwitchPoint != 0 ? 17 : 18;
					for (var index = 0; index < firstCount; index++) state.ScaleFactors[destination++] = firstLength != 0 ? (byte)_Reader.ReadBits(firstLength) : (byte)0;
					for (var index = 0; index < 18; index++) state.ScaleFactors[destination++] = secondLength != 0 ? (byte)_Reader.ReadBits(secondLength) : (byte)0;
					for (var index = 0; index < 3; index++) state.ScaleFactors[destination++] = 0;
				} else
				{
					var firstGranuleFactors = _Granules[channel, 0].ScaleFactors;
					for (var group = 0; group < 4; group++)
					{
						var count = group == 0 ? 6 : 5;
						if ((state.Scfsi & 8 >> group) == 0)
						{
							var length = group < 2 ? firstLength : secondLength;
							for (var index = 0; index < count; index++) state.ScaleFactors[destination++] = length != 0 ? (byte)_Reader.ReadBits(length) : (byte)0;
						} else
						{
							for (var index = 0; index < count; index++) { state.ScaleFactors[destination] = firstGranuleFactors[destination]; destination++; }
						}
					}
					state.ScaleFactors[destination] = 0;
				}
				return;
			}

			var tableIndex = state.BlockType == 2 ? state.SwitchPoint != 0 ? 2 : 1 : 0;
			var scale = state.ScaleFactorCompress;
			Span<int> lengths = stackalloc int[4];
			int countTable;
			if ((header.ModeExtension & ModeExtensionIntensityStereo) != 0 && channel == 1)
			{
				scale >>= 1;
				if (scale < 180) { ExpandLsfScaleFactors(lengths, scale, 6, 6, 0); countTable = 3; }
				else if (scale < 244) { ExpandLsfScaleFactors(lengths, scale - 180, 4, 4, 0); countTable = 4; }
				else { ExpandLsfScaleFactors(lengths, scale - 244, 3, 0, 0); countTable = 5; }
			} else
			{
				if (scale < 400) { ExpandLsfScaleFactors(lengths, scale, 5, 4, 4); countTable = 0; }
				else if (scale < 500) { ExpandLsfScaleFactors(lengths, scale - 400, 5, 4, 0); countTable = 1; }
				else { ExpandLsfScaleFactors(lengths, scale - 500, 3, 0, 0); countTable = 2; state.Preflag = 1; }
			}
			var destinationIndex = 0;
			for (var group = 0; group < 4; group++)
			{
				var count = MpegAudioTables.LsfScaleFactorCounts[(countTable * 3 + tableIndex) * 4 + group];
				var length = lengths[group];
				for (var index = 0; index < count; index++) state.ScaleFactors[destinationIndex++] = length != 0 ? (byte)_Reader.ReadBits(length) : (byte)0;
			}
			for (; destinationIndex < 40; destinationIndex++) state.ScaleFactors[destinationIndex] = 0;
		}

		private static void ExpandLsfScaleFactors(Span<int> lengths, int scale, int firstBase, int secondBase, int thirdBase)
		{
			SplitLsf(ref scale, thirdBase, out lengths[3]); SplitLsf(ref scale, secondBase, out lengths[2]);
			SplitLsf(ref scale, firstBase, out lengths[1]); lengths[0] = scale;
		}

		private static void SplitLsf(ref int scale, int radix, out int value)
		{
			if (radix == 3) { var quotient = scale * 171 >> 9; value = scale - 3 * quotient; scale = quotient; }
			else if (radix == 4) { value = scale & 3; scale >>= 2; }
			else if (radix == 5) { var quotient = scale * 205 >> 10; value = scale - 5 * quotient; scale = quotient; }
			else if (radix == 6) { var quotient = scale * 171 >> 10; value = scale - 6 * quotient; scale = quotient; }
			else value = 0;
		}

		private static void ExponentsFromScaleFactors(MpegAudioHeader header, MpegAudioGranule state, Span<short> exponents)
		{
			var destination = 0; var gain = state.GlobalGain - 210; var shift = state.ScaleFactorScale + 1;
			for (var band = 0; band < state.LongEnd; band++)
			{
				var value = gain - ((state.ScaleFactors[band] + MpegAudioTables.Pretab[state.Preflag * 22 + band]) << shift) + 400;
				var length = MpegAudioTables.LongBandSizes[header.SampleRateIndex * 22 + band];
				for (var index = 0; index < length; index++) exponents[destination++] = (short)value;
			}
			if (state.ShortStart >= 13) return;
			Span<int> gains = stackalloc int[3];
			for (var window = 0; window < 3; window++) gains[window] = gain - (state.SubblockGain[window] << 3);
			var scaleFactor = state.LongEnd;
			for (var band = state.ShortStart; band < 13; band++)
			{
				var length = MpegAudioTables.ShortBandSizes[header.SampleRateIndex * 13 + band];
				for (var window = 0; window < 3; window++)
				{
					var value = gains[window] - (state.ScaleFactors[scaleFactor++] << shift) + 400;
					for (var index = 0; index < length; index++) exponents[destination++] = (short)value;
				}
			}
		}

		/// <summary>
		/// Decodes all big-value pairs and count1 quadruples through the canonical FFmpeg VLC tables.
		/// </summary>
		private void HuffmanDecode(MpegAudioGranule state, Span<short> exponents, int endPosition)
		{
			var bitReader = _Reader.OpenLocal();
			// The method deliberately has one exit so this CLOSE_READER equivalent cannot be bypassed.
				var spectralIndex = 0; endPosition = Math.Min(endPosition, _Reader.SizeInBits);
				for (var region = 0; region < 3; region++)
				{
					var pairs = state.RegionSize[region]; if (pairs == 0) continue;
					var selection = state.TableSelect[region]; var table = MpegAudioTables.HuffmanData[selection * 2]; var linearBits = MpegAudioTables.HuffmanData[selection * 2 + 1];
					if (table == 0) { Array.Clear(state.SubbandHybrid, spectralIndex, 2 * pairs); spectralIndex += 2 * pairs; continue; }
					for (; pairs > 0; pairs--)
					{
						if (bitReader.Position >= endPosition) break;
						var symbol = bitReader.ReadVlc(MpegAudioTables.HuffmanVlcs[table].Table, 7, 3);
						if (symbol == 0) { state.SubbandHybrid[spectralIndex++] = 0; state.SubbandHybrid[spectralIndex++] = 0; continue; }
						var exponent = exponents[spectralIndex];
						if ((symbol & 16) != 0)
						{
							var first = symbol >> 5; var second = symbol & 15;
							state.SubbandHybrid[spectralIndex] = DecodeHuffmanValue(ref bitReader, first, linearBits, exponent);
							state.SubbandHybrid[spectralIndex + 1] = DecodeHuffmanValue(ref bitReader, second, linearBits, exponents[spectralIndex + 1]);
						} else
						{
							var first = symbol >> 5; var second = symbol & 15; first += second;
							state.SubbandHybrid[spectralIndex + (second != 0 ? 1 : 0)] = DecodeHuffmanValue(ref bitReader, first, linearBits, exponent);
							state.SubbandHybrid[spectralIndex + (second == 0 ? 1 : 0)] = 0;
						}
						spectralIndex += 2;
					}
				}

				var quadVlc = MpegAudioTables.QuadVlcs[state.Count1TableSelect];
				while (spectralIndex <= 572 && bitReader.Position < endPosition)
				{
					var symbol = bitReader.ReadVlc(quadVlc.Table, quadVlc.RootBits, 1);
					state.SubbandHybrid[spectralIndex] = 0; state.SubbandHybrid[spectralIndex + 1] = 0;
					state.SubbandHybrid[spectralIndex + 2] = 0; state.SubbandHybrid[spectralIndex + 3] = 0;
					while (symbol != 0)
					{
						var relative = symbol >= 8 ? 0 : symbol >= 4 ? 1 : symbol >= 2 ? 2 : 3;
						symbol ^= 8 >> relative;
						state.SubbandHybrid[spectralIndex + relative] = FlipSign(ref bitReader, MpegAudioTables.ExpTable[exponents[spectralIndex + relative]]);
					}
					spectralIndex += 4;
				}
				if (spectralIndex < 576) Array.Clear(state.SubbandHybrid, spectralIndex, 576 - spectralIndex);
				bitReader.SkipBits(endPosition - bitReader.Position);
			bitReader.Close();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static float DecodeHuffmanValue(ref BitReader.BitReaderLocal bitReader, int value, int linearBits, int exponent)
		{
			if (value < 15) return FlipSign(ref bitReader, MpegAudioTables.ExpValueTable[exponent * 16 + value]);
			value += (int)bitReader.ReadBitsOrZero(linearBits); var result = UnscaleLayer3(value, exponent);
			if (bitReader.ReadBit() != 0) result = -result; return result;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static float FlipSign(ref BitReader.BitReaderLocal bitReader, float value)
		{
			var bits = BitConverter.SingleToInt32Bits(value) ^ (int)(bitReader.ReadBit() << 31);
			return BitConverter.Int32BitsToSingle(bits);
		}

		private static float UnscaleLayer3(int value, int exponent)
		{
			var tableIndex = 4 * value + (exponent & 3); var shift = MpegAudioTables.Table43Exponents[tableIndex] - (exponent >> 2);
			if ((uint)shift > 31) return 0;
			var mantissa = MpegAudioTables.Table43Values[tableIndex]; mantissa = (mantissa + ((1U << shift) >> 1)) >> shift;
			return mantissa;
		}

		private static void ReorderBlock(MpegAudioHeader header, MpegAudioGranule state)
		{
			if (state.BlockType != 2) return;
			var pointer = state.SwitchPoint != 0 ? header.SampleRateIndex != 8 ? 36 : 72 : 0;
			Span<float> temporary = stackalloc float[576];
			for (var band = state.ShortStart; band < 13; band++)
			{
				var length = MpegAudioTables.ShortBandSizes[header.SampleRateIndex * 13 + band]; var source = pointer; var destination = 0;
				for (var index = length; index > 0; index--)
				{
					temporary[destination++] = state.SubbandHybrid[pointer]; temporary[destination++] = state.SubbandHybrid[pointer + length];
					temporary[destination++] = state.SubbandHybrid[pointer + 2 * length]; pointer++;
				}
				pointer += 2 * length; temporary.Slice(0, length * 3).CopyTo(state.SubbandHybrid.AsSpan(source));
			}
		}

		/// <summary>
		/// Applies intensity and middle/side stereo from high bands downward so nonzero detection matches FFmpeg.
		/// </summary>
		private static void ComputeStereo(MpegAudioHeader header, MpegAudioGranule first, MpegAudioGranule second)
		{
			if ((header.ModeExtension & ModeExtensionIntensityStereo) != 0)
			{
				var lsfTable = header.LowSamplingFrequency != 0; var maximumScale = lsfTable ? 16 : 7;
				var firstOffset = 576; var secondOffset = 576; Span<int> shortNonzero = stackalloc int[3];
				var scaleIndex = (13 - second.ShortStart) * 3 + second.LongEnd - 3;
				for (var band = 12; band >= second.ShortStart; band--)
				{
					if (band != 11) scaleIndex -= 3;
					var length = MpegAudioTables.ShortBandSizes[header.SampleRateIndex * 13 + band];
					for (var window = 2; window >= 0; window--)
					{
						firstOffset -= length; secondOffset -= length;
						if (shortNonzero[window] == 0)
						{
							var found = false; for (var index = 0; index < length; index++) if (second.SubbandHybrid[secondOffset + index] != 0) { found = true; break; }
							var scale = second.ScaleFactors[scaleIndex + window];
							if (found) shortNonzero[window] = 1;
							if (!found && scale < maximumScale) ApplyIntensity(first, second, firstOffset, secondOffset, length, lsfTable, second.ScaleFactorCompress, scale);
							else if ((header.ModeExtension & ModeExtensionMiddleSideStereo) != 0) ApplyMiddleSide(first, second, firstOffset, secondOffset, length);
						} else if ((header.ModeExtension & ModeExtensionMiddleSideStereo) != 0) ApplyMiddleSide(first, second, firstOffset, secondOffset, length);
					}
				}
				var nonzero = shortNonzero[0] | shortNonzero[1] | shortNonzero[2];
				for (var band = second.LongEnd - 1; band >= 0; band--)
				{
					var length = MpegAudioTables.LongBandSizes[header.SampleRateIndex * 22 + band]; firstOffset -= length; secondOffset -= length;
					if (nonzero == 0)
					{
						var found = false; for (var index = 0; index < length; index++) if (second.SubbandHybrid[secondOffset + index] != 0) { found = true; break; }
						if (found) nonzero = 1;
						var scale = second.ScaleFactors[band == 21 ? 20 : band];
						if (!found && scale < maximumScale) ApplyIntensity(first, second, firstOffset, secondOffset, length, lsfTable, second.ScaleFactorCompress, scale);
						else if ((header.ModeExtension & ModeExtensionMiddleSideStereo) != 0) ApplyMiddleSide(first, second, firstOffset, secondOffset, length);
					} else if ((header.ModeExtension & ModeExtensionMiddleSideStereo) != 0) ApplyMiddleSide(first, second, firstOffset, secondOffset, length);
				}
			} else if ((header.ModeExtension & ModeExtensionMiddleSideStereo) != 0)
			{
				for (var index = 0; index < 576; index++) { var left = first.SubbandHybrid[index]; var right = second.SubbandHybrid[index]; first.SubbandHybrid[index] = left + right; second.SubbandHybrid[index] = left - right; }
			}
		}

		private static void ApplyIntensity(MpegAudioGranule first, MpegAudioGranule second, int firstOffset, int secondOffset, int length, bool lsf, int compress, int scale)
		{
			var tableOffset = lsf ? (((compress & 1) * 2) * 16) : 0;
			var firstFactor = lsf ? MpegAudioTables.IsTableLsf[tableOffset + scale] : MpegAudioTables.IsTable[scale];
			var secondFactor = lsf ? MpegAudioTables.IsTableLsf[tableOffset + 16 + scale] : MpegAudioTables.IsTable[16 + scale];
			for (var index = 0; index < length; index++) { var value = first.SubbandHybrid[firstOffset + index]; first.SubbandHybrid[firstOffset + index] = value * firstFactor; second.SubbandHybrid[secondOffset + index] = value * secondFactor; }
		}

		private static void ApplyMiddleSide(MpegAudioGranule first, MpegAudioGranule second, int firstOffset, int secondOffset, int length)
		{
			const float inverseSqrtTwo = 0.70710678118654752440f;
			for (var index = 0; index < length; index++) { var left = first.SubbandHybrid[firstOffset + index]; var right = second.SubbandHybrid[secondOffset + index]; first.SubbandHybrid[firstOffset + index] = (left + right) * inverseSqrtTwo; second.SubbandHybrid[secondOffset + index] = (left - right) * inverseSqrtTwo; }
		}

		private static void ComputeAntialias(MpegAudioGranule state)
		{
			var count = state.BlockType == 2 ? state.SwitchPoint == 0 ? 0 : 1 : SubbandLimit - 1; var pointer = 18;
			for (var band = count; band > 0; band--)
			{
				for (var index = 0; index < 8; index++)
				{
					var first = state.SubbandHybrid[pointer - 1 - index]; var second = state.SubbandHybrid[pointer + index];
					state.SubbandHybrid[pointer - 1 - index] = first * MpegAudioTables.CsaTable[index * 4] - second * MpegAudioTables.CsaTable[index * 4 + 1];
					state.SubbandHybrid[pointer + index] = first * MpegAudioTables.CsaTable[index * 4 + 1] + second * MpegAudioTables.CsaTable[index * 4];
				}
				pointer += 18;
			}
		}

		/// <summary>
		/// Performs long and short inverse MDCT overlap-add and clears inactive subbands exactly as FFmpeg.
		/// </summary>
		private static void ComputeImdct(MpegAudioGranule state, float[] subbandSamples, int outputOffset, float[] mdctBuffer)
		{
			var pointer = 576;
			while (pointer >= 36)
			{
				pointer -= 6; var nonzero = 0;
				for (var index = 0; index < 6; index++) nonzero |= BitConverter.SingleToInt32Bits(state.SubbandHybrid[pointer + index]);
				if (nonzero != 0) break;
			}
			var subbandLimit = pointer / 18 + 1;
			var longEnd = state.BlockType == 2 ? state.SwitchPoint != 0 ? 2 : 0 : subbandLimit;
			MpegAudioDsp.Imdct36Blocks(subbandSamples, outputOffset, mdctBuffer, state.SubbandHybrid, 0, longEnd, state.SwitchPoint != 0, state.BlockType);
			var bufferOffset = 4 * 18 * (longEnd >> 2) + (longEnd & 3); var spectralOffset = 18 * longEnd;
			Span<float> shortOutput = stackalloc float[12];
			for (var subband = longEnd; subband < subbandLimit; subband++)
			{
				var windowOffset = (2 + (4 & -(subband & 1))) * MpegAudioTables.MdctBufferSize; var output = outputOffset + subband;
				for (var index = 0; index < 6; index++) { subbandSamples[output] = mdctBuffer[bufferOffset + 4 * index]; output += SubbandLimit; }
				Imdct12(shortOutput, state.SubbandHybrid, spectralOffset);
				for (var index = 0; index < 6; index++) { subbandSamples[output] = shortOutput[index] * MpegAudioTables.MdctWindows[windowOffset + index] + mdctBuffer[bufferOffset + 4 * (index + 6)]; mdctBuffer[bufferOffset + 4 * (index + 12)] = shortOutput[index + 6] * MpegAudioTables.MdctWindows[windowOffset + index + 6]; output += SubbandLimit; }
				Imdct12(shortOutput, state.SubbandHybrid, spectralOffset + 1);
				for (var index = 0; index < 6; index++) { subbandSamples[output] = shortOutput[index] * MpegAudioTables.MdctWindows[windowOffset + index] + mdctBuffer[bufferOffset + 4 * (index + 12)]; mdctBuffer[bufferOffset + 4 * index] = shortOutput[index + 6] * MpegAudioTables.MdctWindows[windowOffset + index + 6]; output += SubbandLimit; }
				Imdct12(shortOutput, state.SubbandHybrid, spectralOffset + 2);
				for (var index = 0; index < 6; index++) { mdctBuffer[bufferOffset + 4 * index] = shortOutput[index] * MpegAudioTables.MdctWindows[windowOffset + index] + mdctBuffer[bufferOffset + 4 * index]; mdctBuffer[bufferOffset + 4 * (index + 6)] = shortOutput[index + 6] * MpegAudioTables.MdctWindows[windowOffset + index + 6]; mdctBuffer[bufferOffset + 4 * (index + 12)] = 0; }
				spectralOffset += 18; bufferOffset += (subband & 3) != 3 ? 1 : 69;
			}
			for (var subband = subbandLimit; subband < SubbandLimit; subband++)
			{
				var output = outputOffset + subband;
				for (var index = 0; index < 18; index++) { subbandSamples[output] = mdctBuffer[bufferOffset + 4 * index]; mdctBuffer[bufferOffset + 4 * index] = 0; output += SubbandLimit; }
				bufferOffset += (subband & 3) != 3 ? 1 : 69;
			}
		}

		private static void Imdct12(Span<float> output, float[] input, int inputOffset)
		{
			var in0 = input[inputOffset]; var in1 = input[inputOffset + 3] + input[inputOffset]; var in2 = input[inputOffset + 6] + input[inputOffset + 3];
			var in3 = input[inputOffset + 9] + input[inputOffset + 6]; var in4 = input[inputOffset + 12] + input[inputOffset + 9]; var in5 = input[inputOffset + 15] + input[inputOffset + 12];
			in5 += in3; in3 += in1; var c3 = (float)(0.86602540378443864676 / 2); var c4 = (float)(0.70710678118654752439 / 2);
			var c5 = (float)(0.51763809020504152469 / 2); var c6 = (float)(1.93185165257813657349 / 4);
			in2 = 2 * c3 * in2; in3 = 4 * c3 * in3; var first = in0 - in4; var second = 2 * c4 * (in1 - in5);
			output[7] = output[10] = first + second; output[1] = output[4] = first - second;
			in0 += in4 * 0.5f; in4 = in0 + in2; in5 += 2 * in1; in1 = c5 * (in5 + in3);
			output[8] = output[9] = in4 + in1; output[2] = output[3] = in4 - in1;
			in0 -= in2; in5 = 2 * c6 * (in5 - in3); output[0] = output[5] = in0 - in5; output[6] = output[11] = in0 + in5;
		}
	}
}
