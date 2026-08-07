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

namespace Ffmpeg.CsPort.Decoder.Codecs.Ape
{
	/// <summary>
	/// Holds one channel's adaptive FIR coefficients, history, and moving average for an APE filter level.
	/// </summary>
	internal sealed class ApeFilterState
	{
		public ApeFilterState(int order)
		{
			Order = order;
			Buffer = new short[order * 3 + ApeDecoder.HistorySize];
		}

		public int Order { get; }
		public short[] Buffer { get; }
		public int DelayOffset { get; set; }
		public int AdaptCoefficientOffset { get; set; }
		public uint Average { get; set; }

		public void Initialize()
		{
			Array.Clear(Buffer, 0, Order * 3);
			DelayOffset = Order * 3;
			AdaptCoefficientOffset = Order * 2;
			Average = 0;
		}
	}

	/// <summary>
	/// Mirrors FFmpeg's Rice state for one APE entropy channel.
	/// </summary>
	internal struct ApeRiceState
	{
		public uint K;
		public uint Sum;
	}

	/// <summary>
	/// Mirrors the unsigned arithmetic state of APE's range decoder.
	/// </summary>
	internal struct ApeRangeState
	{
		public uint Low;
		public uint Range;
		public uint Help;
		public uint Buffer;
	}

	/// <summary>
	/// Holds the pre-3.95 32-bit adaptive predictor state and its sliding history window.
	/// </summary>
	internal sealed class ApePredictor32State
	{
		public int[] History { get; } = new int[ApeDecoder.HistorySize + ApeDecoder.PredictorSize];
		public int[] LastA { get; } = new int[2];
		public int[] FilterA { get; } = new int[2];
		public int[] FilterB { get; } = new int[2];
		public uint[,] CoefficientsA { get; } = new uint[2, 4];
		public uint[,] CoefficientsB { get; } = new uint[2, 5];
		public int BufferOffset { get; set; }
		public uint SamplePosition { get; set; }
	}

	/// <summary>
	/// Holds the 3.95-and-newer 64-bit adaptive predictor state and supports exact interim-state copies for 24-bit audio.
	/// </summary>
	internal sealed class ApePredictor64State
	{
		public long[] History { get; } = new long[ApeDecoder.HistorySize + ApeDecoder.PredictorSize];
		public long[] LastA { get; } = new long[2];
		public long[] FilterA { get; } = new long[2];
		public long[] FilterB { get; } = new long[2];
		public ulong[,] CoefficientsA { get; } = new ulong[2, 4];
		public ulong[,] CoefficientsB { get; } = new ulong[2, 5];
		public int BufferOffset { get; set; }

		public void CopyFrom(ApePredictor64State source)
		{
			Array.Copy(source.History, History, History.Length);
			Array.Copy(source.LastA, LastA, LastA.Length);
			Array.Copy(source.FilterA, FilterA, FilterA.Length);
			Array.Copy(source.FilterB, FilterB, FilterB.Length);
			Buffer.BlockCopy(source.CoefficientsA, 0, CoefficientsA, 0, sizeof(ulong) * 8);
			Buffer.BlockCopy(source.CoefficientsB, 0, CoefficientsB, 0, sizeof(ulong) * 10);
			BufferOffset = source.BufferOffset;
		}
	}
}
