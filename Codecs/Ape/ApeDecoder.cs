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
using System.Numerics;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Ape
{
	/// <summary>
	/// Ports FFmpeg's Monkey's Audio decoder with identical entropy, adaptive-filter, predictor, and planar output arithmetic.
	/// </summary>
	public sealed class ApeDecoder
	{
		internal const int HistorySize = 512;
		internal const int PredictorSize = 50;

		private const int PredictorOrder = 8;
		private const int YDelayA = 18 + PredictorOrder * 4;
		private const int YDelayB = 18 + PredictorOrder * 3;
		private const int XDelayA = 18 + PredictorOrder * 2;
		private const int XDelayB = 18 + PredictorOrder;
		private const int YAdaptCoefficientsA = 18;
		private const int XAdaptCoefficientsA = 14;
		private const int YAdaptCoefficientsB = 10;
		private const int XAdaptCoefficientsB = 5;
		private const int FrameCodeStereoSilence = 3;
		private const int FrameCodePseudoStereo = 4;
		private const uint BottomValue = 0x00800000;
		private const int ModelElements = 64;

		private readonly int _Channels;
		private readonly int _BitsPerSample;
		private readonly int _FileVersion;
		private readonly int _CompressionLevel;
		private readonly int _FilterSet;
		private readonly AudioSampleFormat _SampleFormat;
		private readonly BitReader _BitReader = new BitReader();
		private readonly ApePredictor32State _Predictor32 = new ApePredictor32State();
		private readonly ApePredictor64State _Predictor64 = new ApePredictor64State();
		private readonly ApePredictor64State _InterimPredictor64 = new ApePredictor64State();
		private readonly ApeFilterState[,] _Filters = new ApeFilterState[3, 2];
		private readonly int[] _LongFilterCoefficients = new int[256];
		private readonly int[] _LongFilterDelay = new int[512];
		private readonly int[] _ExtraHighDelay = new int[8];
		private readonly uint[] _ExtraHighCoefficients = new uint[8];

		private ApeRangeState _Range;
		private ApeRiceState _RiceX;
		private ApeRiceState _RiceY;
		private byte[] _Data = Array.Empty<byte>();
		private int _Pointer;
		private int _DataEnd;
		private int _SamplesRemaining;
		private int _PacketSize;
		private int _FrameFlags;
		private bool _Error;
		private int _InterimMode;
		private int[][] _Decoded = { Array.Empty<int>(), Array.Empty<int>() };
		private int[][] _Interim = { Array.Empty<int>(), Array.Empty<int>() };

		private ApeDecoder(int channels, int bitsPerSample, byte[] extraData)
		{
			_Channels = channels;
			_BitsPerSample = bitsPerSample;
			_FileVersion = BinaryPrimitives.ReadUInt16LittleEndian(extraData);
			_CompressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(2));
			_FilterSet = _CompressionLevel / 1000 - 1;
			_SampleFormat = bitsPerSample == 8 ? AudioSampleFormat.Unsigned8Planar :
				bitsPerSample == 16 ? AudioSampleFormat.Signed16Planar : AudioSampleFormat.Signed32Planar;
			_InterimMode = bitsPerSample == 24 ? -1 : 0;
			for (var level = 0; level < 3; level++)
			{
				var order = ApeTables.FilterOrders[_FilterSet, level];
				if (order == 0)
					break;
				_Filters[level, 0] = new ApeFilterState(order);
				_Filters[level, 1] = new ApeFilterState(order);
			}
		}

		public AudioCodecId CodecId => AudioCodecId.Ape;
		public int Channels => _Channels;
		public AudioSampleFormat SampleFormat => _SampleFormat;

		public static int Initialize(int channels, int bitsPerCodedSample, byte[] extraData, out ApeDecoder decoder)
		{
			decoder = null;
			if (extraData == null || extraData.Length != 6 || channels < 1 || channels > 2)
				return FfmpegError.InvalidArgument;
			if (bitsPerCodedSample != 8 && bitsPerCodedSample != 16 && bitsPerCodedSample != 24)
				return FfmpegError.PatchWelcome;
			var fileVersion = BinaryPrimitives.ReadUInt16LittleEndian(extraData);
			var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(2));
			if (compressionLevel == 0 || compressionLevel % 1000 != 0 || compressionLevel > 5000 ||
				(fileVersion < 3930 && compressionLevel == 5000))
				return FfmpegError.InvalidData;
			decoder = new ApeDecoder(channels, bitsPerCodedSample, extraData);
			return 0;
		}

		/// <summary>
		/// Starts or continues one APE packet and emits FFmpeg's default maximum of 4,608 planar samples per call.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < 0 || packetLength > packet.Length - packetOffset)
				return FfmpegError.InvalidArgument;
			if (_SamplesRemaining == 0)
			{
				if (packetLength == 0)
					return 0;
				var packetResult = StartPacket(packet, packetOffset, packetLength);
				if (packetResult < 0)
					return packetResult;
			}

			var blocksToDecode = Math.Min(4608, _SamplesRemaining);
			if (_FileVersion < 3930)
				blocksToDecode = _SamplesRemaining;
			var alignedBlocks = (blocksToDecode + 7) & ~7;
			EnsureDecodeBuffers(alignedBlocks);
			Array.Clear(_Decoded[0], 0, alignedBlocks);
			Array.Clear(_Decoded[1], 0, alignedBlocks);
			if (_InterimMode < 0)
			{
				Array.Clear(_Interim[0], 0, alignedBlocks);
				Array.Clear(_Interim[1], 0, alignedBlocks);
			}
			_Error = false;
			if (_Channels == 1 || (_FrameFlags & FrameCodePseudoStereo) != 0)
				UnpackMono(blocksToDecode);
			else
				UnpackStereo(blocksToDecode);
			if (_Error)
			{
				_SamplesRemaining = 0;
				return FfmpegError.InvalidData;
			}

			var bytesPerSample = _BitsPerSample == 8 ? 1 : _BitsPerSample == 16 ? 2 : 4;
			var totalSize = blocksToDecode * _Channels * bytesPerSample;
			if (output.Length < totalSize)
				return FfmpegError.InvalidArgument;
			WriteOutput(output, blocksToDecode, bytesPerSample);
			frame = new AudioFrameInfo(
				blocksToDecode,
				_Channels,
				_SampleFormat,
				_Channels,
				blocksToDecode * bytesPerSample,
				totalSize);
			_SamplesRemaining -= blocksToDecode;
			return _SamplesRemaining == 0 ? _PacketSize : 0;
		}

		/// <summary>
		/// Byte-swaps the packet exactly like FFmpeg's scalar bswap DSP, consumes its demuxer preamble, and initializes frame state.
		/// </summary>
		private int StartPacket(byte[] packet, int packetOffset, int packetLength)
		{
			if (packetLength < 8)
				return FfmpegError.InvalidData;
			var alignedSize = packetLength & ~3;
			var dataSize = alignedSize + (_FileVersion < 3950 ? 2 : 0);
			if (_Data.Length < dataSize)
				_Data = new byte[dataSize];
			for (var offset = 0; offset < alignedSize; offset += 4)
			{
				_Data[offset] = packet[packetOffset + offset + 3];
				_Data[offset + 1] = packet[packetOffset + offset + 2];
				_Data[offset + 2] = packet[packetOffset + offset + 1];
				_Data[offset + 3] = packet[packetOffset + offset];
			}
			if (dataSize > alignedSize)
				Array.Clear(_Data, alignedSize, dataSize - alignedSize);
			_Pointer = 0;
			_DataEnd = dataSize;
			_PacketSize = packetLength;
			var numberOfBlocks = ReadBigEndianUInt32();
			var offsetBitsOrBytes = ReadBigEndianUInt32();
			if (_FileVersion >= 3900)
			{
				if (offsetBitsOrBytes > 3 || offsetBitsOrBytes > _DataEnd - _Pointer)
					return FfmpegError.InvalidData;
				_Pointer += (int)offsetBitsOrBytes;
			} else
			{
				if (_BitReader.Initialize(_Data, _Pointer, (_DataEnd - _Pointer) * 8) < 0)
					return FfmpegError.InvalidData;
				_BitReader.SkipBits(_FileVersion > 3800 ? checked((int)offsetBitsOrBytes * 8) : checked((int)offsetBitsOrBytes));
			}
			if (numberOfBlocks == 0 || numberOfBlocks > int.MaxValue / 8 - 8)
				return FfmpegError.InvalidData;
			var result = InitializeFrameDecoder();
			if (result < 0)
				return FfmpegError.InvalidData;
			_SamplesRemaining = (int)numberOfBlocks;
			return 0;
		}

		private void EnsureDecodeBuffers(int alignedBlocks)
		{
			if (_Decoded[0].Length < alignedBlocks)
			{
				_Decoded[0] = new int[alignedBlocks];
				_Decoded[1] = new int[alignedBlocks];
			}
			if (_InterimMode < 0 && _Interim[0].Length < alignedBlocks)
			{
				_Interim[0] = new int[alignedBlocks];
				_Interim[1] = new int[alignedBlocks];
			}
		}

		private int InitializeFrameDecoder()
		{
			var result = InitializeEntropyDecoder();
			if (result < 0)
				return result;
			InitializePredictorDecoder();
			for (var level = 0; level < 3; level++)
			{
				if (ApeTables.FilterOrders[_FilterSet, level] == 0)
					break;
				_Filters[level, 0].Initialize();
				_Filters[level, 1].Initialize();
			}
			return 0;
		}

		private int InitializeEntropyDecoder()
		{
			if (_FileVersion >= 3900)
			{
				if (_DataEnd - _Pointer < 6)
					return FfmpegError.InvalidData;
				_ = ReadBigEndianUInt32();
			} else
			{
				_ = _BitReader.ReadBitsLong(32);
			}
			_FrameFlags = 0;
			if (_FileVersion > 3820)
			{
				var crc = _FileVersion >= 3900
					? BinaryPrimitives.ReadUInt32BigEndian(_Data.AsSpan(_Pointer - 4, 4))
					: _BitReader.ShowBitsLong(1) == uint.MaxValue ? 0u : 0u;
				if (_FileVersion < 3900)
				{
					var crcPosition = _BitReader.Position - 32;
					_BitReader.Seek(crcPosition);
					crc = _BitReader.ReadBitsLong(32);
				}
				if ((crc & 0x80000000) != 0)
				{
					if (_DataEnd - _Pointer < 6)
						return FfmpegError.InvalidData;
					_FrameFlags = unchecked((int)ReadBigEndianUInt32());
				}
			}
			_RiceX.K = _RiceY.K = 10;
			_RiceX.Sum = _RiceY.Sum = 1u << 14;
			if (_FileVersion >= 3900)
			{
				_Pointer++;
				RangeStartDecoding();
			}
			return 0;
		}

		/// <summary>
		/// Resets APE predictor filters and selects the version-specific coefficient and adaptation initialization.
		/// </summary>
		private void InitializePredictorDecoder()
		{
			Array.Clear(_Predictor32.History, 0, PredictorSize);
			Array.Clear(_Predictor64.History, 0, PredictorSize);
			_Predictor32.BufferOffset = 0;
			_Predictor64.BufferOffset = 0;
			Array.Clear(_Predictor32.CoefficientsA);
			Array.Clear(_Predictor32.CoefficientsB);
			Array.Clear(_Predictor64.CoefficientsA);
			Array.Clear(_Predictor64.CoefficientsB);
			if (_FileVersion < 3930)
			{
				if (_CompressionLevel == 1000)
				{
					_Predictor32.CoefficientsA[0, 0] = 375;
					_Predictor32.CoefficientsA[1, 0] = 375;
				} else
				{
					for (var channel = 0; channel < 2; channel++)
					{
						_Predictor32.CoefficientsA[channel, 0] = 64;
						_Predictor32.CoefficientsA[channel, 1] = 115;
						_Predictor32.CoefficientsA[channel, 2] = 64;
						_Predictor32.CoefficientsB[channel, 0] = 740;
					}
				}
			} else
			{
				for (var channel = 0; channel < 2; channel++)
				{
					_Predictor32.CoefficientsA[channel, 0] = 360;
					_Predictor32.CoefficientsA[channel, 1] = 317;
					_Predictor32.CoefficientsA[channel, 2] = unchecked((uint)-109);
					_Predictor32.CoefficientsA[channel, 3] = 98;
					_Predictor64.CoefficientsA[channel, 0] = 360;
					_Predictor64.CoefficientsA[channel, 1] = 317;
					_Predictor64.CoefficientsA[channel, 2] = unchecked((ulong)-109L);
					_Predictor64.CoefficientsA[channel, 3] = 98;
				}
			}
			Array.Clear(_Predictor32.FilterA);
			Array.Clear(_Predictor32.FilterB);
			Array.Clear(_Predictor32.LastA);
			Array.Clear(_Predictor64.FilterA);
			Array.Clear(_Predictor64.FilterB);
			Array.Clear(_Predictor64.LastA);
			_Predictor32.SamplePosition = 0;
		}

		private void UnpackMono(int count)
		{
			if ((_FrameFlags & FrameCodeStereoSilence) != 0)
				return;
			EntropyDecodeMono(count);
			if (_Error)
				return;
			PredictorDecodeMono(count);
			if (_Channels == 2)
				Array.Copy(_Decoded[0], _Decoded[1], count);
		}

		private void UnpackStereo(int count)
		{
			if ((_FrameFlags & FrameCodeStereoSilence) == FrameCodeStereoSilence)
				return;
			EntropyDecodeStereo(count);
			if (_Error)
				return;
			PredictorDecodeStereo(count);
			for (var index = 0; index < count; index++)
			{
				var left = unchecked((int)((uint)_Decoded[1][index] - (uint)(_Decoded[0][index] / 2)));
				var right = unchecked((int)((uint)left + (uint)_Decoded[0][index]));
				_Decoded[0][index] = left;
				_Decoded[1][index] = right;
			}
		}

		private void EntropyDecodeMono(int count)
		{
			if (_FileVersion < 3860)
				DecodeArray0000(_Decoded[0], ref _RiceY, count);
			else
				for (var index = 0; index < count; index++)
					_Decoded[0][index] = DecodeEntropyValue(ref _RiceY);
		}

		private void EntropyDecodeStereo(int count)
		{
			if (_FileVersion < 3860)
			{
				DecodeArray0000(_Decoded[0], ref _RiceY, count);
				DecodeArray0000(_Decoded[1], ref _RiceX, count);
				return;
			}
			if (_FileVersion < 3900)
			{
				for (var index = 0; index < count; index++)
					_Decoded[0][index] = DecodeValue3860(ref _RiceY);
				for (var index = 0; index < count; index++)
					_Decoded[1][index] = DecodeValue3860(ref _RiceX);
				return;
			}
			if (_FileVersion < 3930)
			{
				for (var index = 0; index < count; index++)
					_Decoded[0][index] = DecodeValue3900(ref _RiceY);
				RangeNormalize();
				_Pointer--;
				RangeStartDecoding();
				for (var index = 0; index < count; index++)
					_Decoded[1][index] = DecodeValue3900(ref _RiceX);
				return;
			}
			for (var index = 0; index < count; index++)
			{
				_Decoded[0][index] = DecodeEntropyValue(ref _RiceY);
				_Decoded[1][index] = DecodeEntropyValue(ref _RiceX);
			}
		}

		private int DecodeEntropyValue(ref ApeRiceState rice)
		{
			return _FileVersion >= 3990 ? DecodeValue3990(ref rice) : DecodeValue3900(ref rice);
		}

		private int DecodeValue3860(ref ApeRiceState rice)
		{
			uint overflow = ReadUnary();
			if (_FileVersion > 3880)
			{
				while (overflow >= 16)
				{
					overflow -= 16;
					rice.K += 4;
				}
			}
			uint value;
			if (rice.K == 0)
				value = overflow;
			else if (rice.K <= 25)
				value = unchecked((overflow << (int)rice.K) + _BitReader.ReadBits((int)rice.K));
			else
			{
				_Error = true;
				return FfmpegError.InvalidData;
			}
			rice.Sum = unchecked(rice.Sum + value - ((rice.Sum + 8) >> 4));
			if (rice.Sum < (rice.K != 0 ? 1u << ((int)rice.K + 4) : 0))
				rice.K--;
			else if (rice.Sum >= 1u << ((int)rice.K + 5) && rice.K < 24)
				rice.K++;
			return UnmapValue(value);
		}

		private int DecodeValue3900(ref ApeRiceState rice)
		{
			var overflow = (uint)RangeGetSymbol(ApeTables.Counts3970, ApeTables.CountsDifference3970);
			int temporaryK;
			if (overflow == ModelElements - 1)
			{
				temporaryK = RangeDecodeBits(5);
				overflow = 0;
			} else
			{
				temporaryK = rice.K < 1 ? 0 : (int)rice.K - 1;
			}
			uint value;
			if (temporaryK <= 16 || _FileVersion < 3910)
			{
				if (temporaryK > 23)
					return FfmpegError.InvalidData;
				value = (uint)RangeDecodeBits(temporaryK);
			} else if (temporaryK <= 31)
			{
				value = (uint)RangeDecodeBits(16);
				value |= (uint)RangeDecodeBits(temporaryK - 16) << 16;
			} else
			{
				return FfmpegError.InvalidData;
			}
			value = unchecked(value + (overflow << temporaryK));
			UpdateRice(ref rice, value);
			return UnmapValue(value);
		}

		private int DecodeValue3990(ref ApeRiceState rice)
		{
			var pivot = Math.Max(rice.Sum >> 5, 1u);
			var overflow = (uint)RangeGetSymbol(ApeTables.Counts3980, ApeTables.CountsDifference3980);
			if (overflow == ModelElements - 1)
			{
				overflow = (uint)RangeDecodeBits(16) << 16;
				overflow |= (uint)RangeDecodeBits(16);
			}
			uint baseValue;
			if (pivot < 0x10000)
			{
				baseValue = (uint)RangeDecodeCumulativeFrequency(pivot);
				RangeUpdate(1, baseValue);
			} else
			{
				var baseHigh = pivot;
				var baseBits = 0;
				while ((baseHigh & ~0xffffu) != 0)
				{
					baseHigh >>= 1;
					baseBits++;
				}
				baseHigh = (uint)RangeDecodeCumulativeFrequency(baseHigh + 1);
				RangeUpdate(1, baseHigh);
				var baseLow = (uint)RangeDecodeCumulativeFrequency(1u << baseBits);
				RangeUpdate(1, baseLow);
				baseValue = (baseHigh << baseBits) + baseLow;
			}
			var value = unchecked(baseValue + overflow * pivot);
			UpdateRice(ref rice, value);
			return UnmapValue(value);
		}

		private static void UpdateRice(ref ApeRiceState rice, uint value)
		{
			var limit = rice.K != 0 ? 1u << ((int)rice.K + 4) : 0;
			rice.Sum = unchecked(rice.Sum + ((value + 1) / 2) - ((rice.Sum + 16) >> 5));
			if (rice.Sum < limit)
				rice.K--;
			else if (rice.Sum >= 1u << ((int)rice.K + 5) && rice.K < 24)
				rice.K++;
		}

		private static int UnmapValue(uint value)
		{
			return unchecked((int)(((value >> 1) ^ ((value & 1) - 1)) + 1));
		}

		/// <summary>
		/// Ports the legacy 3.80-and-older order-zero Rice array decoder without changing its rolling-window updates.
		/// </summary>
		private void DecodeArray0000(int[] output, ref ApeRiceState rice, int count)
		{
			rice.Sum = 0;
			var index = 0;
			for (; index < Math.Min(count, 5); index++)
			{
				output[index] = unchecked((int)ReadRiceOrderZero(10));
				rice.Sum = unchecked(rice.Sum + (uint)output[index]);
			}
			if (count > 5)
			{
				rice.K = (uint)GetK(unchecked((int)(rice.Sum / 10)));
				if (rice.K < 24)
				{
					for (; index < Math.Min(count, 64); index++)
					{
						output[index] = unchecked((int)ReadRiceOrderZero((int)rice.K));
						rice.Sum = unchecked(rice.Sum + (uint)output[index]);
						rice.K = (uint)GetK(unchecked((int)(rice.Sum / ((index + 1) * 2u))));
						if (rice.K >= 24)
							break;
					}
					if (index == 64 && count > 64)
					{
						rice.K = (uint)GetK(unchecked((int)(rice.Sum >> 7)));
						var maximum = 1u << ((int)rice.K + 7);
						var minimum = rice.K != 0 ? 1u << ((int)rice.K + 6) : 0;
						for (; index < count; index++)
						{
							if (_BitReader.BitsLeft < 1)
							{
								_Error = true;
								return;
							}
							output[index] = unchecked((int)ReadRiceOrderZero((int)rice.K));
							rice.Sum = unchecked(rice.Sum + (uint)output[index] - (uint)output[index - 64]);
							while (rice.Sum < minimum)
							{
								rice.K--;
								minimum = rice.K != 0 ? minimum >> 1 : 0;
								maximum >>= 1;
							}
							while (rice.Sum >= maximum)
							{
								rice.K++;
								if (rice.K > 24)
									return;
								maximum <<= 1;
								minimum = minimum != 0 ? minimum << 1 : 128;
							}
						}
					}
				}
			}
			for (index = 0; index < count; index++)
				output[index] = ((output[index] >> 1) ^ ((output[index] & 1) - 1)) + 1;
		}

		private uint ReadRiceOrderZero(int k)
		{
			var value = ReadUnary();
			if (k != 0)
				value = (value << k) | _BitReader.ReadBits(k);
			return value;
		}

		private uint ReadUnary()
		{
			uint count = 0;
			while (_BitReader.BitsLeft > 0 && _BitReader.ReadBit() != 1)
				count++;
			return count;
		}

		private static int GetK(int sum)
		{
			return sum == 0 ? 0 : BitOperations.Log2(unchecked((uint)sum)) + 1;
		}

		private void RangeStartDecoding()
		{
			_Range.Buffer = ReadByte();
			_Range.Low = _Range.Buffer >> 1;
			_Range.Range = 1u << 7;
		}

		private void RangeNormalize()
		{
			while (_Range.Range <= BottomValue)
			{
				_Range.Buffer <<= 8;
				if (_Pointer < _DataEnd)
					_Range.Buffer += _Data[_Pointer++];
				else
					_Error = true;
				_Range.Low = (_Range.Low << 8) | ((_Range.Buffer >> 1) & 0xff);
				_Range.Range <<= 8;
			}
		}

		private int RangeDecodeCumulativeFrequency(uint total)
		{
			RangeNormalize();
			_Range.Help = _Range.Range / total;
			return unchecked((int)(_Range.Low / _Range.Help));
		}

		private int RangeDecodeCumulativeShift(int shift)
		{
			RangeNormalize();
			_Range.Help = _Range.Range >> shift;
			return unchecked((int)(_Range.Low / _Range.Help));
		}

		private void RangeUpdate(uint symbolFrequency, uint lowerFrequency)
		{
			_Range.Low -= _Range.Help * lowerFrequency;
			_Range.Range = _Range.Help * symbolFrequency;
		}

		private int RangeDecodeBits(int count)
		{
			var symbol = RangeDecodeCumulativeShift(count);
			RangeUpdate(1, unchecked((uint)symbol));
			return symbol;
		}

		private int RangeGetSymbol(ushort[] counts, ushort[] differences)
		{
			var cumulativeFrequency = RangeDecodeCumulativeShift(16);
			if (cumulativeFrequency > 65492)
			{
				var symbol = cumulativeFrequency - 65535 + 63;
				RangeUpdate(1, unchecked((uint)cumulativeFrequency));
				if (cumulativeFrequency > 65535)
					_Error = true;
				return symbol;
			}
			var result = 0;
			while (counts[result + 1] <= cumulativeFrequency)
				result++;
			RangeUpdate(differences[result], counts[result]);
			return result;
		}

		private void PredictorDecodeMono(int count)
		{
			if (_FileVersion < 3930)
				PredictorDecodeMono3800(count);
			else if (_FileVersion < 3950)
				PredictorDecodeMono3930(count);
			else
				PredictorDecodeMono3950(count);
		}

		private void PredictorDecodeStereo(int count)
		{
			if (_FileVersion < 3930)
				PredictorDecodeStereo3800(count);
			else if (_FileVersion < 3950)
				PredictorDecodeStereo3930(count);
			else
				PredictorDecodeStereo3950(count);
		}

		private static int ApeSign(int value)
		{
			return (value < 0 ? 1 : 0) - (value > 0 ? 1 : 0);
		}

		private int FilterFast3320(int decoded, int filter, int delayA)
		{
			var predictor = _Predictor32;
			var offset = predictor.BufferOffset;
			predictor.History[offset + delayA] = predictor.LastA[filter];
			if (predictor.SamplePosition < 3)
			{
				predictor.LastA[filter] = decoded;
				predictor.FilterA[filter] = decoded;
				return decoded;
			}
			var prediction = unchecked((int)((uint)(predictor.History[offset + delayA] * 2) -
				(uint)predictor.History[offset + delayA - 1]));
			predictor.LastA[filter] = unchecked((int)((uint)decoded +
				(uint)(unchecked((int)((uint)prediction * predictor.CoefficientsA[filter, 0])) >> 9)));
			if ((decoded ^ prediction) > 0)
				predictor.CoefficientsA[filter, 0]++;
			else
				predictor.CoefficientsA[filter, 0]--;
			predictor.FilterA[filter] = unchecked((int)((uint)predictor.FilterA[filter] + (uint)predictor.LastA[filter]));
			return predictor.FilterA[filter];
		}

		/// <summary>
		/// Reconstructs the legacy 3.80 predictor with explicit unsigned wraparound at every C promotion boundary.
		/// </summary>
		private int Filter3800(uint decoded, int filter, int delayA, int delayB, int start, int shift)
		{
			var predictor = _Predictor32;
			var offset = predictor.BufferOffset;
			predictor.History[offset + delayA] = predictor.LastA[filter];
			predictor.History[offset + delayB] = predictor.FilterB[filter];
			if (predictor.SamplePosition < start)
			{
				var result = unchecked((int)(decoded + (uint)predictor.FilterA[filter]));
				predictor.LastA[filter] = unchecked((int)decoded);
				predictor.FilterB[filter] = unchecked((int)decoded);
				predictor.FilterA[filter] = result;
				return result;
			}
			var d2 = predictor.History[offset + delayA];
			var d1 = unchecked((int)((uint)(predictor.History[offset + delayA] -
				unchecked((int)(uint)predictor.History[offset + delayA - 1])) * 2));
			var d0 = unchecked((int)((uint)predictor.History[offset + delayA] +
				(uint)((predictor.History[offset + delayA - 2] -
					unchecked((int)(uint)predictor.History[offset + delayA - 1])) * 8)));
			var d3 = unchecked((int)((uint)(predictor.History[offset + delayB] * 2) -
				(uint)predictor.History[offset + delayB - 1]));
			var d4 = predictor.History[offset + delayB];
			var predictionA = unchecked((int)(
				(uint)d0 * predictor.CoefficientsA[filter, 0] +
				(uint)d1 * predictor.CoefficientsA[filter, 1] +
				(uint)d2 * predictor.CoefficientsA[filter, 2]));
			var sign = ApeSign(unchecked((int)decoded));
			predictor.CoefficientsA[filter, 0] = unchecked(predictor.CoefficientsA[filter, 0] +
				(uint)((((d0 >> 30) & 2) - 1) * sign));
			predictor.CoefficientsA[filter, 1] = unchecked(predictor.CoefficientsA[filter, 1] +
				(uint)((((d1 >> 28) & 8) - 4) * sign));
			predictor.CoefficientsA[filter, 2] = unchecked(predictor.CoefficientsA[filter, 2] +
				(uint)((((d2 >> 28) & 8) - 4) * sign));
			var predictionB = unchecked((int)((uint)d3 * predictor.CoefficientsB[filter, 0] -
				(uint)d4 * predictor.CoefficientsB[filter, 1]));
			predictor.LastA[filter] = unchecked((int)(decoded + (uint)(predictionA >> 11)));
			sign = ApeSign(predictor.LastA[filter]);
			predictor.CoefficientsB[filter, 0] = unchecked(predictor.CoefficientsB[filter, 0] +
				(uint)((((d3 >> 29) & 4) - 2) * sign));
			predictor.CoefficientsB[filter, 1] = unchecked(predictor.CoefficientsB[filter, 1] -
				(uint)((((d4 >> 30) & 2) - 1) * sign));
			predictor.FilterB[filter] = unchecked((int)((uint)predictor.LastA[filter] + (uint)(predictionB >> shift)));
			predictor.FilterA[filter] = unchecked((int)((uint)predictor.FilterB[filter] +
				(uint)(unchecked((int)((uint)predictor.FilterA[filter] * 31)) >> 5)));
			return predictor.FilterA[filter];
		}

		private void PredictorDecodeStereo3800(int count)
		{
			var start = 4;
			var shift = 10;
			ApplyLegacyLongFilters(count, ref start, ref shift, true);
			for (var index = 0; index < count; index++)
			{
				var x = _Decoded[0][index];
				var y = _Decoded[1][index];
				if (_CompressionLevel == 1000)
				{
					_Decoded[0][index] = FilterFast3320(y, 0, YDelayA);
					_Decoded[1][index] = FilterFast3320(x, 1, XDelayA);
				} else
				{
					_Decoded[0][index] = Filter3800(unchecked((uint)y), 0, YDelayA, YDelayB, start, shift);
					_Decoded[1][index] = Filter3800(unchecked((uint)x), 1, XDelayA, XDelayB, start, shift);
				}
				AdvancePredictor32();
			}
		}

		private void PredictorDecodeMono3800(int count)
		{
			var start = 4;
			var shift = 10;
			ApplyLegacyLongFilters(count, ref start, ref shift, false);
			for (var index = 0; index < count; index++)
			{
				_Decoded[0][index] = _CompressionLevel == 1000
					? FilterFast3320(_Decoded[0][index], 0, YDelayA)
					: Filter3800(unchecked((uint)_Decoded[0][index]), 0, YDelayA, YDelayB, start, shift);
				AdvancePredictor32();
			}
		}

		private void AdvancePredictor32()
		{
			_Predictor32.BufferOffset++;
			_Predictor32.SamplePosition++;
			if (_Predictor32.BufferOffset == HistorySize)
			{
				Array.Copy(_Predictor32.History, _Predictor32.BufferOffset, _Predictor32.History, 0, PredictorSize);
				_Predictor32.BufferOffset = 0;
			}
		}

		private void ApplyLegacyLongFilters(int count, ref int start, ref int shift, bool stereo)
		{
			if (_CompressionLevel == 3000)
			{
				start = 16;
				LongFilterHigh3800(_Decoded[0], 16, 9, count);
				if (stereo) LongFilterHigh3800(_Decoded[1], 16, 9, count);
			} else if (_CompressionLevel == 4000)
			{
				var order = 128;
				var secondShift = 11;
				if (_FileVersion >= 3830)
				{
					order <<= 1;
					shift++;
					secondShift++;
					LongFilterExtraHigh3830(_Decoded[0], order, count - order);
					if (stereo) LongFilterExtraHigh3830(_Decoded[1], order, count - order);
				}
				start = order;
				LongFilterHigh3800(_Decoded[0], order, secondShift, count);
				if (stereo) LongFilterHigh3800(_Decoded[1], order, secondShift, count);
			}
		}

		/// <summary>
		/// Applies the legacy high-order adaptive FIR using the source's 256-sample sliding delay window.
		/// </summary>
		private void LongFilterHigh3800(int[] buffer, int order, int shift, int length)
		{
			if (order >= length)
				return;
			Array.Clear(_LongFilterCoefficients, 0, order);
			Array.Copy(buffer, 0, _LongFilterDelay, 0, order);
			var delayOffset = 0;
			for (var index = order; index < length; index++)
			{
				var dotProduct = 0;
				var sign = ApeSign(buffer[index]);
				for (var coefficient = 0; coefficient < order; coefficient++)
				{
					dotProduct = unchecked((int)((uint)dotProduct +
						(uint)_LongFilterDelay[delayOffset + coefficient] * (uint)_LongFilterCoefficients[coefficient]));
					if (sign == 1)
						_LongFilterCoefficients[coefficient] += (_LongFilterDelay[delayOffset + coefficient] >> 31) | 1;
					else if (sign == -1)
						_LongFilterCoefficients[coefficient] -= (_LongFilterDelay[delayOffset + coefficient] >> 31) | 1;
				}
				buffer[index] = unchecked((int)((uint)buffer[index] - (uint)(dotProduct >> shift)));
				delayOffset++;
				_LongFilterDelay[delayOffset + order - 1] = buffer[index];
				if (delayOffset == 256)
				{
					Array.Copy(_LongFilterDelay, delayOffset, _LongFilterDelay, 0, 256);
					delayOffset = 0;
				}
			}
		}

		private void LongFilterExtraHigh3830(int[] buffer, int offset, int length)
		{
			Array.Clear(_ExtraHighDelay);
			Array.Clear(_ExtraHighCoefficients);
			for (var index = 0; index < length; index++)
			{
				var dotProduct = 0;
				var sign = ApeSign(buffer[offset + index]);
				for (var coefficient = 7; coefficient >= 0; coefficient--)
				{
					dotProduct = unchecked((int)((uint)dotProduct +
						(uint)_ExtraHighDelay[coefficient] * _ExtraHighCoefficients[coefficient]));
					_ExtraHighCoefficients[coefficient] = unchecked(_ExtraHighCoefficients[coefficient] +
						(uint)(((_ExtraHighDelay[coefficient] >> 31) | 1) * sign));
				}
				for (var coefficient = 7; coefficient > 0; coefficient--)
					_ExtraHighDelay[coefficient] = _ExtraHighDelay[coefficient - 1];
				_ExtraHighDelay[0] = buffer[offset + index];
				buffer[offset + index] = unchecked((int)((uint)buffer[offset + index] - (uint)(dotProduct >> 9)));
			}
		}

		private int PredictorUpdate3930(int decoded, int filter, int delayA)
		{
			var offset = _Predictor32.BufferOffset;
			_Predictor32.History[offset + delayA] = _Predictor32.LastA[filter];
			var d0 = unchecked((uint)_Predictor32.History[offset + delayA]);
			var d1 = d0 - unchecked((uint)_Predictor32.History[offset + delayA - 1]);
			var d2 = unchecked((uint)_Predictor32.History[offset + delayA - 1]) -
				unchecked((uint)_Predictor32.History[offset + delayA - 2]);
			var d3 = unchecked((uint)_Predictor32.History[offset + delayA - 2]) -
				unchecked((uint)_Predictor32.History[offset + delayA - 3]);
			var prediction = unchecked((int)(
				d0 * _Predictor32.CoefficientsA[filter, 0] +
				d1 * _Predictor32.CoefficientsA[filter, 1] +
				d2 * _Predictor32.CoefficientsA[filter, 2] +
				d3 * _Predictor32.CoefficientsA[filter, 3]));
			_Predictor32.LastA[filter] = unchecked(decoded + (prediction >> 9));
			_Predictor32.FilterA[filter] = unchecked(_Predictor32.LastA[filter] +
				(unchecked((int)((uint)_Predictor32.FilterA[filter] * 31)) >> 5));
			var sign = ApeSign(decoded);
			_Predictor32.CoefficientsA[filter, 0] = unchecked(_Predictor32.CoefficientsA[filter, 0] + (uint)(((int)d0 < 0 ? 1 : -1) * sign));
			_Predictor32.CoefficientsA[filter, 1] = unchecked(_Predictor32.CoefficientsA[filter, 1] + (uint)(((int)d1 < 0 ? 1 : -1) * sign));
			_Predictor32.CoefficientsA[filter, 2] = unchecked(_Predictor32.CoefficientsA[filter, 2] + (uint)(((int)d2 < 0 ? 1 : -1) * sign));
			_Predictor32.CoefficientsA[filter, 3] = unchecked(_Predictor32.CoefficientsA[filter, 3] + (uint)(((int)d3 < 0 ? 1 : -1) * sign));
			return _Predictor32.FilterA[filter];
		}

		private void PredictorDecodeStereo3930(int count)
		{
			ApplyFilters(count, true);
			for (var index = 0; index < count; index++)
			{
				var y = _Decoded[1][index];
				var x = _Decoded[0][index];
				_Decoded[0][index] = PredictorUpdate3930(y, 0, YDelayA);
				_Decoded[1][index] = PredictorUpdate3930(x, 1, XDelayA);
				AdvancePredictor32WithoutSamplePosition();
			}
		}

		private void PredictorDecodeMono3930(int count)
		{
			ApplyFilters(count, false);
			for (var index = 0; index < count; index++)
			{
				_Decoded[0][index] = PredictorUpdate3930(_Decoded[0][index], 0, YDelayA);
				AdvancePredictor32WithoutSamplePosition();
			}
		}

		private void AdvancePredictor32WithoutSamplePosition()
		{
			_Predictor32.BufferOffset++;
			if (_Predictor32.BufferOffset == HistorySize)
			{
				Array.Copy(_Predictor32.History, _Predictor32.BufferOffset, _Predictor32.History, 0, PredictorSize);
				_Predictor32.BufferOffset = 0;
			}
		}

		/// <summary>
		/// Reconstructs one 3.95 predictor channel while preserving 32-bit interim truncation and 64-bit coefficient updates.
		/// </summary>
		private static int PredictorUpdateFilter(
			ApePredictor64State predictor,
			int decoded,
			int filter,
			int delayA,
			int delayB,
			int adaptA,
			int adaptB,
			int interimMode)
		{
			var offset = predictor.BufferOffset;
			predictor.History[offset + delayA] = predictor.LastA[filter];
			predictor.History[offset + adaptA] = ApeSign(unchecked((int)predictor.History[offset + delayA]));
			predictor.History[offset + delayA - 1] = unchecked((long)((ulong)predictor.History[offset + delayA] -
				(ulong)predictor.History[offset + delayA - 1]));
			predictor.History[offset + adaptA - 1] = ApeSign(unchecked((int)predictor.History[offset + delayA - 1]));
			var predictionA = unchecked((long)(
				(ulong)predictor.History[offset + delayA] * predictor.CoefficientsA[filter, 0] +
				(ulong)predictor.History[offset + delayA - 1] * predictor.CoefficientsA[filter, 1] +
				(ulong)predictor.History[offset + delayA - 2] * predictor.CoefficientsA[filter, 2] +
				(ulong)predictor.History[offset + delayA - 3] * predictor.CoefficientsA[filter, 3]));
			predictor.History[offset + delayB] = unchecked(predictor.FilterA[filter ^ 1] -
				(unchecked((long)((ulong)predictor.FilterB[filter] * 31)) >> 5));
			predictor.History[offset + adaptB] = ApeSign(unchecked((int)predictor.History[offset + delayB]));
			predictor.History[offset + delayB - 1] = unchecked((long)((ulong)predictor.History[offset + delayB] -
				(ulong)predictor.History[offset + delayB - 1]));
			predictor.History[offset + adaptB - 1] = ApeSign(unchecked((int)predictor.History[offset + delayB - 1]));
			predictor.FilterB[filter] = predictor.FilterA[filter ^ 1];
			var predictionB = unchecked((long)(
				(ulong)predictor.History[offset + delayB] * predictor.CoefficientsB[filter, 0] +
				(ulong)predictor.History[offset + delayB - 1] * predictor.CoefficientsB[filter, 1] +
				(ulong)predictor.History[offset + delayB - 2] * predictor.CoefficientsB[filter, 2] +
				(ulong)predictor.History[offset + delayB - 3] * predictor.CoefficientsB[filter, 3] +
				(ulong)predictor.History[offset + delayB - 4] * predictor.CoefficientsB[filter, 4]));
			if (interimMode < 1)
			{
				predictionA = unchecked((int)predictionA);
				predictionB = unchecked((int)predictionB);
				predictor.LastA[filter] = unchecked((int)((uint)decoded +
					(uint)(unchecked((int)(predictionA + (predictionB >> 1))) >> 10)));
			} else
			{
				predictor.LastA[filter] = unchecked(decoded +
					(unchecked((long)((ulong)predictionA + (ulong)(predictionB >> 1))) >> 10));
			}
			predictor.FilterA[filter] = unchecked(predictor.LastA[filter] +
				(unchecked((long)((ulong)predictor.FilterA[filter] * 31)) >> 5));
			var sign = ApeSign(decoded);
			for (var coefficient = 0; coefficient < 4; coefficient++)
				predictor.CoefficientsA[filter, coefficient] = unchecked(predictor.CoefficientsA[filter, coefficient] +
					(ulong)(predictor.History[offset + adaptA - coefficient] * sign));
			for (var coefficient = 0; coefficient < 5; coefficient++)
				predictor.CoefficientsB[filter, coefficient] = unchecked(predictor.CoefficientsB[filter, coefficient] +
					(ulong)(predictor.History[offset + adaptB - coefficient] * sign));
			return unchecked((int)predictor.FilterA[filter]);
		}

		/// <summary>
		/// Runs the one- or two-pass 3.95 stereo predictor, including FFmpeg's 24-bit interim-overflow mode switch.
		/// </summary>
		private void PredictorDecodeStereo3950(int count)
		{
			ApplyFilters(count, true);
			var passes = 1;
			if (_InterimMode == -1)
			{
				_InterimPredictor64.CopyFrom(_Predictor64);
				passes++;
				Array.Copy(_Decoded[0], _Interim[0], count);
				Array.Copy(_Decoded[1], _Interim[1], count);
			}
			for (var pass = 0; pass < passes; pass++)
			{
				var interimMode = _InterimMode > 0 || pass != 0 ? 1 : 0;
				var predictor = pass != 0 ? _InterimPredictor64 : _Predictor64;
				var first = pass != 0 ? _Interim[0] : _Decoded[0];
				var second = pass != 0 ? _Interim[1] : _Decoded[1];
				predictor.BufferOffset = 0;
				for (var index = 0; index < count; index++)
				{
					var firstValue = PredictorUpdateFilter(predictor, first[index], 0, YDelayA, YDelayB,
						YAdaptCoefficientsA, YAdaptCoefficientsB, interimMode);
					var secondValue = PredictorUpdateFilter(predictor, second[index], 1, XDelayA, XDelayB,
						XAdaptCoefficientsA, XAdaptCoefficientsB, interimMode);
					first[index] = firstValue;
					second[index] = secondValue;
					if (passes > 1)
					{
						var left = unchecked((int)((uint)secondValue - (uint)(firstValue / 2)));
						var right = unchecked((int)((uint)left + (uint)firstValue));
						if (Math.Min(NegativeAbsolute(left), NegativeAbsolute(right)) < -(1 << 23))
						{
							_InterimMode = interimMode == 0 ? 1 : 0;
							break;
						}
					}
					AdvancePredictor64(predictor);
				}
			}
			if (passes > 1 && _InterimMode > 0)
			{
				Array.Copy(_Interim[0], _Decoded[0], count);
				Array.Copy(_Interim[1], _Decoded[1], count);
				_Predictor64.CopyFrom(_InterimPredictor64);
				_Predictor64.BufferOffset = 0;
			}
		}

		private void PredictorDecodeMono3950(int count)
		{
			ApplyFilters(count, false);
			var current = unchecked((int)_Predictor64.LastA[0]);
			for (var index = 0; index < count; index++)
			{
				var value = _Decoded[0][index];
				var offset = _Predictor64.BufferOffset;
				_Predictor64.History[offset + YDelayA] = current;
				_Predictor64.History[offset + YDelayA - 1] = unchecked((long)(
					(ulong)_Predictor64.History[offset + YDelayA] -
					(ulong)_Predictor64.History[offset + YDelayA - 1]));
				var prediction = unchecked((int)(
					(ulong)_Predictor64.History[offset + YDelayA] * _Predictor64.CoefficientsA[0, 0] +
					(ulong)_Predictor64.History[offset + YDelayA - 1] * _Predictor64.CoefficientsA[0, 1] +
					(ulong)_Predictor64.History[offset + YDelayA - 2] * _Predictor64.CoefficientsA[0, 2] +
					(ulong)_Predictor64.History[offset + YDelayA - 3] * _Predictor64.CoefficientsA[0, 3]));
				current = unchecked((int)((uint)value + (ulong)(prediction >> 10)));
				_Predictor64.History[offset + YAdaptCoefficientsA] = ApeSign(unchecked((int)_Predictor64.History[offset + YDelayA]));
				_Predictor64.History[offset + YAdaptCoefficientsA - 1] = ApeSign(unchecked((int)_Predictor64.History[offset + YDelayA - 1]));
				var sign = ApeSign(value);
				for (var coefficient = 0; coefficient < 4; coefficient++)
					_Predictor64.CoefficientsA[0, coefficient] = unchecked(_Predictor64.CoefficientsA[0, coefficient] +
						(ulong)(_Predictor64.History[offset + YAdaptCoefficientsA - coefficient] * sign));
				AdvancePredictor64(_Predictor64);
				_Predictor64.FilterA[0] = unchecked((long)((ulong)current +
					(ulong)(unchecked((long)((ulong)_Predictor64.FilterA[0] * 31)) >> 5)));
				_Decoded[0][index] = unchecked((int)_Predictor64.FilterA[0]);
			}
			_Predictor64.LastA[0] = current;
		}

		private static int NegativeAbsolute(int value)
		{
			return value > 0 ? -value : value;
		}

		private static void AdvancePredictor64(ApePredictor64State predictor)
		{
			predictor.BufferOffset++;
			if (predictor.BufferOffset == HistorySize)
			{
				Array.Copy(predictor.History, predictor.BufferOffset, predictor.History, 0, PredictorSize);
				predictor.BufferOffset = 0;
			}
		}

		private void ApplyFilters(int count, bool stereo)
		{
			for (var level = 0; level < 3; level++)
			{
				var order = ApeTables.FilterOrders[_FilterSet, level];
				if (order == 0)
					break;
				ApplyFilter(_Filters[level, 0], _Decoded[0], count, ApeTables.FilterFractionBits[_FilterSet, level]);
				if (stereo)
					ApplyFilter(_Filters[level, 1], _Decoded[1], count, ApeTables.FilterFractionBits[_FilterSet, level]);
			}
		}

		/// <summary>
		/// Applies one scalar FFmpeg APE FIR level and updates coefficients in the same combined dot-product loop.
		/// </summary>
		private void ApplyFilter(ApeFilterState filter, int[] data, int count, int fractionBits)
		{
			var order = filter.Order;
			for (var sample = 0; sample < count; sample++)
			{
				uint scalar = 0;
				var sign = ApeSign(data[sample]);
				for (var coefficient = 0; coefficient < order; coefficient++)
				{
					scalar = unchecked(scalar + (uint)(filter.Buffer[coefficient] *
						filter.Buffer[filter.DelayOffset - order + coefficient]));
					filter.Buffer[coefficient] = unchecked((short)(filter.Buffer[coefficient] +
						sign * filter.Buffer[filter.AdaptCoefficientOffset - order + coefficient]));
				}
				var result = unchecked((int)(((long)unchecked((int)scalar) + (1L << (fractionBits - 1))) >> fractionBits));
				result = unchecked((int)((uint)result + (uint)data[sample]));
				data[sample] = result;
				filter.Buffer[filter.DelayOffset++] = (short)Math.Clamp(result, short.MinValue, short.MaxValue);
				if (_FileVersion < 3980)
				{
					filter.Buffer[filter.AdaptCoefficientOffset] = (short)(result == 0 ? 0 : ((result >> 28) & 8) - 4);
					filter.Buffer[filter.AdaptCoefficientOffset - 4] >>= 1;
					filter.Buffer[filter.AdaptCoefficientOffset - 8] >>= 1;
				} else
				{
					var absolute = unchecked((uint)(result < 0 ? -result : result));
					filter.Buffer[filter.AdaptCoefficientOffset] = absolute != 0
						? unchecked((short)(ApeSign(result) *
							(8 << ((absolute > filter.Average * 3UL ? 1 : 0) +
							(absolute > filter.Average + filter.Average / 3 ? 1 : 0)))))
						: (short)0;
					filter.Average = unchecked((uint)(filter.Average +
						(unchecked((int)(absolute - filter.Average)) / 16)));
					filter.Buffer[filter.AdaptCoefficientOffset - 1] >>= 1;
					filter.Buffer[filter.AdaptCoefficientOffset - 2] >>= 1;
					filter.Buffer[filter.AdaptCoefficientOffset - 8] >>= 1;
				}
				filter.AdaptCoefficientOffset++;
				if (filter.DelayOffset == order + HistorySize + order * 2)
				{
					Array.Copy(filter.Buffer, filter.DelayOffset - order * 2, filter.Buffer, order, order * 2);
					filter.DelayOffset = order * 3;
					filter.AdaptCoefficientOffset = order * 2;
				}
			}
		}

		private void WriteOutput(Span<byte> output, int count, int bytesPerSample)
		{
			for (var channel = 0; channel < _Channels; channel++)
			{
				var planeOffset = channel * count * bytesPerSample;
				for (var sample = 0; sample < count; sample++)
				{
					var offset = planeOffset + sample * bytesPerSample;
					if (_BitsPerSample == 8)
						output[offset] = unchecked((byte)((_Decoded[channel][sample] + 0x80u) & 0xff));
					else if (_BitsPerSample == 16)
						BinaryPrimitives.WriteInt16LittleEndian(output.Slice(offset, 2), unchecked((short)_Decoded[channel][sample]));
					else
						BinaryPrimitives.WriteInt32LittleEndian(output.Slice(offset, 4),
							unchecked((int)((uint)_Decoded[channel][sample] * 256)));
				}
			}
		}

		private byte ReadByte()
		{
			return _Pointer < _DataEnd ? _Data[_Pointer++] : (byte)0;
		}

		private uint ReadBigEndianUInt32()
		{
			if (_DataEnd - _Pointer < 4)
			{
				_Pointer = _DataEnd;
				return 0;
			}
			var value = BinaryPrimitives.ReadUInt32BigEndian(_Data.AsSpan(_Pointer, 4));
			_Pointer += 4;
			return value;
		}
	}
}
