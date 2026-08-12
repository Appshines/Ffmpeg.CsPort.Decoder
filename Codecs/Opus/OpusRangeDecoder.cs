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
using System.Numerics;

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>Ports FFmpeg's Opus range and tail-bit decoder with unsigned wraparound semantics.</summary>
	internal sealed class OpusRangeDecoder
	{
		private const uint Bottom = 1u << 23;
		private const uint Top = 1u << 31;
		private byte[] buffer;
		private int offset;
		private int size;
		private int bitPosition;
		private int rawPosition;
		private uint rawBytes;
		private uint rawCacheLength;
		private uint rawCacheValue;

		public uint Range;
		public uint Value;
		public uint TotalBits;
		public int DataSize { get; private set; }

		public uint Tell => TotalBits - (uint)Log2(Range) - 1;

		public uint TellFraction
		{
			get
			{
				var totalBits = TotalBits << 3;
				var rangeBits = Log2(Range) + 1;
				var workingRange = Range >> (rangeBits - 16);
				for (var index = 0; index < 3; index++)
				{
					workingRange = workingRange * workingRange >> 15;
					var bit = workingRange >> 16;
					rangeBits = rangeBits << 1 | (int)bit;
					workingRange >>= (int)bit;
				}
				return totalBits - (uint)rangeBits;
			}
		}

		public int Initialize(byte[] data, int dataOffset, int dataSize)
		{
			if (data == null || dataOffset < 0 || dataSize < 0 || dataSize > data.Length - dataOffset)
				return -1;
			buffer = data;
			offset = dataOffset;
			size = dataSize;
			DataSize = dataSize;
			bitPosition = 0;
			Range = 128;
			Value = 127 - ReadForwardBits(7);
			TotalBits = 9;
			Normalize();
			InitializeRaw(dataOffset + dataSize, (uint)dataSize);
			return 0;
		}

		public uint DecodeCdf(ushort[] cdf, int cdfOffset = 0)
		{
			var total = cdf[cdfOffset++];
			var scale = Range / total;
			var symbol = Value / scale + 1;
			symbol = total - Math.Min(symbol, total);
			var index = 0;
			while (cdf[cdfOffset + index] <= symbol)
				index++;
			var high = cdf[cdfOffset + index];
			var low = index != 0 ? cdf[cdfOffset + index - 1] : 0u;
			Update(scale, low, high, total);
			return (uint)index;
		}

		public uint DecodeLog(uint bits)
		{
			var scale = Range >> (int)bits;
			uint result;
			if (Value >= scale)
			{
				Value -= scale;
				Range -= scale;
				result = 0;
			} else
			{
				Range = scale;
				result = 1;
			}
			Normalize();
			return result;
		}

		public uint DecodeUInt(uint count)
		{
			var bits = IntegerLog(count - 1);
			var total = bits > 8 ? ((count - 1) >> (bits - 8)) + 1 : count;
			var scale = Range / total;
			var value = Value / scale + 1;
			value = total - Math.Min(value, total);
			Update(scale, value, value + 1, total);
			if (bits <= 8)
				return value;
			value = value << (bits - 8) | GetRaw((uint)(bits - 8));
			return Math.Min(value, count - 1);
		}

		public uint DecodeUIntStep(int k0)
		{
			var total = (uint)((k0 + 1) * 3 + k0);
			var scale = Range / total;
			var symbol = Value / scale + 1;
			symbol = total - Math.Min(symbol, total);
			var value = symbol < (k0 + 1) * 3 ? symbol / 3 : symbol - (uint)((k0 + 1) * 2);
			var low = value <= k0 ? 3 * value : value - 1 - (uint)k0 + (uint)(3 * (k0 + 1));
			var high = value <= k0 ? 3 * (value + 1) : value - (uint)k0 + (uint)(3 * (k0 + 1));
			Update(scale, low, high, total);
			return value;
		}

		public uint DecodeUIntTriangular(int qn)
		{
			var total = (uint)(((qn >> 1) + 1) * ((qn >> 1) + 1));
			var scale = Range / total;
			var center = Value / scale + 1;
			center = total - Math.Min(center, total);
			uint value;
			uint low;
			uint symbol;
			if (center < total >> 1)
			{
				value = (IntegerSquareRoot(8 * center + 1) - 1) >> 1;
				low = value * (value + 1) >> 1;
				symbol = value + 1;
			} else
			{
				value = (uint)(2 * (qn + 1)) - IntegerSquareRoot(8 * (total - center - 1) + 1) >> 1;
				low = total - (uint)((qn + 1 - value) * (qn + 2 - value) >> 1);
				symbol = (uint)(qn + 1) - value;
			}
			Update(scale, low, low + symbol, total);
			return value;
		}

		public int DecodeLaplace(uint symbol, int decay)
		{
			var value = 0;
			uint low = 0;
			var scale = Range >> 15;
			var center = Value / scale + 1;
			center = 32768 - Math.Min(center, 32768u);
			if (center >= symbol)
			{
				value++;
				low = symbol;
				symbol = 1 + ((32768 - 32 - symbol) * (uint)(16384 - decay) >> 15);
				while (symbol > 1 && center >= low + 2 * symbol)
				{
					value++;
					symbol *= 2;
					low += symbol;
					symbol = ((symbol - 2) * (uint)decay >> 15) + 1;
				}
				if (symbol <= 1)
				{
					var distance = (center - low) >> 1;
					value += (int)distance;
					low += 2 * distance;
				}
				if (center < low + symbol)
					value = -value;
				else
					low += symbol;
			}
			Update(scale, low, Math.Min(low + symbol, 32768u), 32768);
			return value;
		}

		public uint GetRaw(uint count)
		{
			while (rawBytes != 0 && rawCacheLength < count)
			{
				rawCacheValue |= (uint)buffer[--rawPosition] << (int)rawCacheLength;
				rawCacheLength += 8;
				rawBytes--;
			}
			var result = count == 32 ? rawCacheValue : rawCacheValue & ((1u << (int)count) - 1);
			rawCacheValue >>= (int)count;
			rawCacheLength -= count;
			TotalBits += count;
			return result;
		}

		public void InitializeRaw(int rightEnd, uint bytes)
		{
			rawPosition = rightEnd;
			rawBytes = bytes;
			rawCacheLength = 0;
			rawCacheValue = 0;
			DataSize = (int)bytes;
		}

		private void Update(uint scale, uint low, uint high, uint total)
		{
			Value -= scale * (total - high);
			Range = low != 0 ? scale * (high - low) : Range - scale * (total - high);
			Normalize();
		}

		private void Normalize()
		{
			while (Range <= Bottom)
			{
				Value = ((Value << 8) | (ReadForwardBits(8) ^ 255u)) & (Top - 1);
				Range <<= 8;
				TotalBits += 8;
			}
		}

		private uint ReadForwardBits(int count)
		{
			uint result = 0;
			for (var index = 0; index < count; index++)
			{
				var absoluteBit = bitPosition++;
				var byteIndex = absoluteBit >> 3;
				var bit = byteIndex < size ? buffer[offset + byteIndex] >> (7 - (absoluteBit & 7)) & 1 : 0;
				result = result << 1 | (uint)bit;
			}
			return result;
		}

		private static int Log2(uint value)
		{
			// LeadingZeroCount(0) is 32, yielding the reference value -1. For nonzero
			// values, subtracting from 31 is exactly the highest set-bit index.
			return 31 - BitOperations.LeadingZeroCount(value);
		}

		private static int IntegerLog(uint value)
		{
			// The zero case becomes 0; every nonzero result is Log2(value) + 1.
			return 32 - BitOperations.LeadingZeroCount(value);
		}

		private static uint IntegerSquareRoot(uint value)
		{
			uint result = 0;
			uint bit = 1u << 30;
			while (bit > value)
				bit >>= 2;
			while (bit != 0)
			{
				if (value >= result + bit)
				{
					value -= result + bit;
					result = (result >> 1) + bit;
				} else
					result >>= 1;
				bit >>= 2;
			}
			return result;
		}
	}
}
