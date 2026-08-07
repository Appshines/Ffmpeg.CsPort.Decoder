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
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Mathematics;
using Ffmpeg.CsPort.Decoder.Transforms;

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>Ports FFmpeg's scalar CELT frame decoder, synthesis overlap, postfilter, and deemphasis.</summary>
	internal sealed class CeltDecoder
	{
		private const float SilenceEnergy = -28.0f;
		private readonly CeltFrame frame = new CeltFrame();
		private readonly CeltPvq pvq = new CeltPvq();
		private readonly CeltAllocation allocation;

		public CeltDecoder(int outputChannels, bool applyPhaseInversion = true)
		{
			frame.OutputChannels = outputChannels;
			frame.ApplyPhaseInversion = applyPhaseInversion;
			for (var index = 0; index < frame.Mdct.Length; index++)
				frame.Mdct[index] = new FfmpegFloatMdct(15 << (index + 3), true, -1.0f / 32768);
			allocation = new CeltAllocation(pvq);
			Flush();
		}

		/// <summary>Decodes one range-coded CELT frame to caller-owned planar output at the requested offsets.</summary>
		public int Decode(OpusRangeDecoder range, float[][] output, int outputOffset, int channels, int frameSize, int startBand, int endBand)
		{
			if ((channels != 1 && channels != 2) || startBand < 0 || startBand > endBand || endBand > 21)
				return FfmpegError.InvalidData;
			frame.Silence = frame.Transient = frame.Anticollapse = 0;
			frame.Flushed = false;
			frame.Channels = channels;
			frame.StartBand = startBand;
			frame.EndBand = endBand;
			frame.FrameBits = GetRawByteCount(range) * 8;
			frame.Size = IntegerLog2(frameSize / 120);
			if (frame.Size > 3 || frameSize != 120 * (1 << frame.Size)) return FfmpegError.InvalidData;
			for (var channel = 0; channel < channels; channel++) { Array.Clear(frame.Blocks[channel].Coefficients, 0, 960); Array.Clear(frame.Blocks[channel].CollapseMasks, 0, 21); }
			var consumed = (int)range.Tell;
			if (consumed >= frame.FrameBits) frame.Silence = 1; else if (consumed == 1) frame.Silence = (int)range.DecodeLog(15);
			if (frame.Silence != 0) { consumed = frame.FrameBits; range.TotalBits += (uint)(frame.FrameBits - range.Tell); }
			consumed = ParsePostfilter(range, consumed);
			if (frame.Size != 0 && consumed + 3 <= frame.FrameBits) frame.Transient = (int)range.DecodeLog(3);
			frame.BlocksCount = frame.Transient != 0 ? 1 << frame.Size : 1;
			frame.BlockSize = frameSize / frame.BlocksCount;
			if (channels == 1) for (var band = 0; band < 21; band++) frame.Blocks[0].Energy[band] = Math.Max(frame.Blocks[0].Energy[band], frame.Blocks[1].Energy[band]);
			DecodeCoarseEnergy(range);
			DecodeTfChanges(range);
			allocation.DecodeAllocation(frame, range);
			DecodeFineEnergy(range);
			allocation.DecodeBands(frame, range);
			if (frame.AnticollapseNeeded != 0) frame.Anticollapse = (int)range.GetRaw(1);
			DecodeFinalEnergy(range);
			for (var channel = 0; channel < channels; channel++) { if (frame.Anticollapse != 0) ProcessAnticollapse(frame.Blocks[channel]); Denormalize(frame.Blocks[channel]); }
			var result = Synthesize(output, outputOffset, frameSize);
			frame.Seed = range.Range;
			return result;
		}

		public void Flush()
		{
			if (frame.Flushed) return;
			for (var channel = 0; channel < 2; channel++)
			{
				var block = frame.Blocks[channel];
				for (var band = 0; band < 21; band++) block.PreviousEnergy[band] = block.PreviousEnergy[21 + band] = SilenceEnergy;
				Array.Clear(block.Energy, 0, 21); Array.Clear(block.Buffer, 0, block.Buffer.Length);
				Array.Clear(block.PostfilterGains, 0, 3); Array.Clear(block.PostfilterGainsOld, 0, 3); Array.Clear(block.PostfilterGainsNew, 0, 3);
				block.EmphasisCoefficient = 0;
			}
			frame.Seed = 0; frame.Flushed = true;
		}

		private void DecodeCoarseEnergy(OpusRangeDecoder range)
		{
			Span<float> previous = stackalloc float[2];
			var alpha = OpusTables.CeltAlphaCoef[frame.Size]; var beta = OpusTables.CeltBetaCoef[frame.Size]; var modelOffset = frame.Size * 84;
			if (range.Tell + 3 <= frame.FrameBits && range.DecodeLog(3) != 0) { alpha = 0; beta = 1.0f - 4915.0f / 32768.0f; modelOffset += 42; }
			for (var band = 0; band < 21; band++) for (var channel = 0; channel < frame.Channels; channel++)
			{
				var block = frame.Blocks[channel];
				if (band < frame.StartBand || band >= frame.EndBand) { block.Energy[band] = 0; continue; }
				var available = frame.FrameBits - (int)range.Tell; float value;
				if (available >= 15) value = range.DecodeLaplace((uint)OpusTables.CeltCoarseEnergyDist[modelOffset + (Math.Min(band, 20) << 1)] << 7, OpusTables.CeltCoarseEnergyDist[modelOffset + (Math.Min(band, 20) << 1) + 1] << 6);
				else if (available >= 2) { var decoded = (int)range.DecodeCdf(OpusTables.CeltModelTapset); value = (decoded >> 1) ^ -(decoded & 1); }
				else if (available >= 1) value = -(float)range.DecodeLog(1); else value = -1;
				block.Energy[band] = Math.Max(-9.0f, block.Energy[band]) * alpha + previous[channel] + value; previous[channel] += beta * value;
			}
		}

		private void DecodeFineEnergy(OpusRangeDecoder range)
		{
			for (var band = frame.StartBand; band < frame.EndBand; band++) if (frame.FineBits[band] != 0) for (var channel = 0; channel < frame.Channels; channel++)
				frame.Blocks[channel].Energy[band] += (range.GetRaw((uint)frame.FineBits[band]) + 0.5f) * (1 << (14 - frame.FineBits[band])) / 16384.0f - 0.5f;
		}

		private void DecodeFinalEnergy(OpusRangeDecoder range)
		{
			var bitsLeft = frame.FrameBits - (int)range.Tell;
			for (var priority = 0; priority < 2; priority++) for (var band = frame.StartBand; band < frame.EndBand && bitsLeft >= frame.Channels; band++)
				if (frame.FinePriority[band] == priority && frame.FineBits[band] < 8) for (var channel = 0; channel < frame.Channels; channel++)
				{ frame.Blocks[channel].Energy[band] += (range.GetRaw(1) - 0.5f) * (1 << (13 - frame.FineBits[band])) / 16384.0f; bitsLeft--; }
		}

		private void DecodeTfChanges(OpusRangeDecoder range)
		{
			var difference = 0; var changed = 0; var consumed = (int)range.Tell; var bits = frame.Transient != 0 ? 2 : 4;
			var selectBit = frame.Size != 0 && consumed + bits + 1 <= frame.FrameBits;
			for (var band = frame.StartBand; band < frame.EndBand; band++) { if (consumed + bits + (selectBit ? 1 : 0) <= frame.FrameBits) { difference ^= (int)range.DecodeLog((uint)bits); consumed = (int)range.Tell; changed |= difference; } frame.TfChange[band] = difference; bits = frame.Transient != 0 ? 4 : 5; }
			var select = 0; var baseOffset = ((frame.Size * 2 + frame.Transient) * 2) * 2;
			if (selectBit && OpusTables.CeltTfSelect[baseOffset + changed] != OpusTables.CeltTfSelect[baseOffset + 2 + changed]) select = (int)range.DecodeLog(1);
			for (var band = frame.StartBand; band < frame.EndBand; band++) frame.TfChange[band] = OpusTables.CeltTfSelect[baseOffset + select * 2 + frame.TfChange[band]];
		}

		private int ParsePostfilter(OpusRangeDecoder range, int consumed)
		{
			Array.Clear(frame.Blocks[0].PostfilterGainsNew, 0, 3); Array.Clear(frame.Blocks[1].PostfilterGainsNew, 0, 3);
			if (frame.StartBand == 0 && consumed + 16 <= frame.FrameBits)
			{
				if (range.DecodeLog(1) != 0)
				{
					var octave = (int)range.DecodeUInt(6); var period = (16 << octave) + (int)range.GetRaw((uint)(4 + octave)) - 1; var gain = 0.09375f * (range.GetRaw(3) + 1);
					var tapset = range.Tell + 2 <= frame.FrameBits ? (int)range.DecodeCdf(OpusTables.CeltModelTapset) : 0;
					for (var channel = 0; channel < 2; channel++) { var block = frame.Blocks[channel]; block.PostfilterPeriodNew = Math.Max(period, 15); for (var tap = 0; tap < 3; tap++) block.PostfilterGainsNew[tap] = gain * OpusTables.CeltPostfilterTaps[tapset * 3 + tap]; }
				}
				consumed = (int)range.Tell;
			}
			return consumed;
		}

		private static int IntegerLog2(int value) { var result = -1; while (value != 0) { value >>= 1; result++; } return result; }
		private static int GetRawByteCount(OpusRangeDecoder range) => range.DataSize;

		private void Denormalize(CeltBlock block)
		{
			for (var band = frame.StartBand; band < frame.EndBand; band++)
			{
				var offset = OpusTables.CeltFreqBands[band] << frame.Size;
				var scale = FfmpegMath.Exp2Float(Math.Min(block.Energy[band] + OpusTables.CeltMeanEnergy[band], 32.0f));
				for (var index = 0; index < OpusTables.CeltFreqRange[band] << frame.Size; index++) block.Coefficients[offset + index] *= scale;
			}
		}

		private void ProcessAnticollapse(CeltBlock block)
		{
			for (var band = frame.StartBand; band < frame.EndBand; band++)
			{
				var length = OpusTables.CeltFreqRange[band] << frame.Size; var depth = (1 + frame.Pulses[band]) / length;
				var threshold = FfmpegMath.Exp2Float(-1.0f - 0.125f * depth); var prior0 = block.PreviousEnergy[band]; var prior1 = block.PreviousEnergy[21 + band];
				if (frame.Channels == 1) { prior0 = Math.Max(prior0, frame.Blocks[1].PreviousEnergy[band]); prior1 = Math.Max(prior1, frame.Blocks[1].PreviousEnergy[21 + band]); }
				var difference = Math.Max(0, block.Energy[band] - Math.Min(prior0, prior1)); var magnitude = FfmpegMath.Exp2Float(1 - difference);
				if (frame.Size == 3) magnitude = (float)(magnitude * 1.41421356237309504880);
				var inverseLength = 1.0f / MathF.Sqrt(length);
				magnitude = Math.Min(threshold, magnitude) * inverseLength;
				var offset = OpusTables.CeltFreqBands[band] << frame.Size; var changed = false;
				for (var time = 0; time < 1 << frame.Size; time++) if ((block.CollapseMasks[band] & 1 << time) == 0) for (var index = 0; index < OpusTables.CeltFreqRange[band]; index++) { block.Coefficients[offset + (index << frame.Size) + time] = (frame.NextRandom() & 0x8000) != 0 ? magnitude : -magnitude; changed = true; }
				if (changed) Renormalize(block.Coefficients, offset, length);
			}
		}

		/// <summary>Applies FFmpeg's strided IMDCT blocks, CELT window overlap, postfilter, and recursive deemphasis.</summary>
		private int Synthesize(float[][] output, int outputOffset, int frameSize)
		{
			var downmix = false;
			if (frame.OutputChannels < frame.Channels) { for (var index = 0; index < frameSize; index++) frame.Blocks[0].Coefficients[index] += frame.Blocks[1].Coefficients[index]; downmix = true; }
			else if (frame.OutputChannels > frame.Channels) Array.Copy(frame.Blocks[0].Coefficients, frame.Blocks[1].Coefficients, frameSize);
			if (frame.Silence != 0) for (var channel = 0; channel < 2; channel++) { for (var band = 0; band < 21; band++) frame.Blocks[channel].Energy[band] = SilenceEnergy; Array.Clear(frame.Blocks[channel].Coefficients, 0, 960); }
			var transform = frame.Mdct[frame.Transient != 0 ? 0 : frame.Size];
			for (var channel = 0; channel < frame.OutputChannels; channel++)
			{
				var block = frame.Blocks[channel];
				for (var subblock = 0; subblock < frame.BlocksCount; subblock++)
				{
					for (var index = 0; index < frame.BlockSize; index++) block.Samples[index] = block.Coefficients[subblock + index * frame.BlocksCount];
					var baseOffset = 1024 + subblock * frame.BlockSize; transform.Transform(block.Samples.AsSpan(0, frame.BlockSize), block.Buffer.AsSpan(baseOffset + 60, frame.BlockSize));
					Window(block.Buffer, baseOffset);
				}
				if (downmix) for (var index = 0; index < frameSize; index++) block.Buffer[1024 + index] *= 0.5f;
				Postfilter(block, frameSize);
				var coefficient = block.EmphasisCoefficient; var source = 1024 - frameSize; var emphasis = OpusTables.OpusDeemphWeights[0];
				for (var index = 0; index < frameSize; index++) coefficient = output[channel][outputOffset + index] =
					block.Buffer[source + index] + FfmpegMath.MultiplyFloat(coefficient, emphasis);
				block.EmphasisCoefficient = float.IsNormal(coefficient) ? coefficient : 0;
			}
			if (frame.Channels == 1) Array.Copy(frame.Blocks[0].Energy, frame.Blocks[1].Energy, 21);
			for (var channel = 0; channel < 2; channel++)
			{
				var block = frame.Blocks[channel];
				if (frame.Transient == 0) { Array.Copy(block.PreviousEnergy, 0, block.PreviousEnergy, 21, 21); Array.Copy(block.Energy, 0, block.PreviousEnergy, 0, 21); }
				else for (var band = 0; band < 21; band++) block.PreviousEnergy[band] = Math.Min(block.PreviousEnergy[band], block.Energy[band]);
				for (var band = 0; band < frame.StartBand; band++) { block.PreviousEnergy[band] = SilenceEnergy; block.Energy[band] = 0; }
				for (var band = frame.EndBand; band < 21; band++) { block.PreviousEnergy[band] = SilenceEnergy; block.Energy[band] = 0; }
			}
			return 0;
		}

		private static void Window(float[] buffer, int offset)
		{
			for (var index = -60; index < 0; index++)
			{
				var reverse = -index - 1; var s0 = buffer[offset + 60 + index]; var s1 = buffer[offset + 60 + reverse];
				var wi = OpusTables.CeltWindowPadded[8 + 60 + index]; var wj = OpusTables.CeltWindowPadded[8 + 60 + reverse];
				buffer[offset + 60 + index] = FfmpegMath.MultiplyFloat(s0, wj) - FfmpegMath.MultiplyFloat(s1, wi);
				buffer[offset + 60 + reverse] = FfmpegMath.MultiplyFloat(s0, wi) + FfmpegMath.MultiplyFloat(s1, wj);
			}
		}

		private void Postfilter(CeltBlock block, int length)
		{
			ApplyTransition(block, 1024); block.PostfilterPeriodOld = block.PostfilterPeriod; Array.Copy(block.PostfilterGains, block.PostfilterGainsOld, 3);
			block.PostfilterPeriod = block.PostfilterPeriodNew; Array.Copy(block.PostfilterGainsNew, block.PostfilterGains, 3);
			if (length > 120) { ApplyTransition(block, 1144); var filterLength = length - 240; if (block.PostfilterGains[0] > 1.192092896e-7f && filterLength > 0) ApplyPostfilter(block, 1264, filterLength); block.PostfilterPeriodOld = block.PostfilterPeriod; Array.Copy(block.PostfilterGains, block.PostfilterGainsOld, 3); }
			Array.Copy(block.Buffer, length, block.Buffer, 0, 1084);
		}

		private static void ApplyTransition(CeltBlock block, int offset)
		{
			if (block.PostfilterGains[0] == 0 && block.PostfilterGainsOld[0] == 0) return;
			var oldPeriod = block.PostfilterPeriodOld; var period = block.PostfilterPeriod;
			var x1 = block.Buffer[offset - period + 1]; var x2 = block.Buffer[offset - period]; var x3 = block.Buffer[offset - period - 1]; var x4 = block.Buffer[offset - period - 2];
			for (var index = 0; index < 120; index++)
			{
				var weight = OpusTables.CeltWindow2[index];
				var x0 = block.Buffer[offset + index - period + 2];
				double filtered = (1.0 - weight) * block.PostfilterGainsOld[0] * block.Buffer[offset + index - oldPeriod];
				filtered += (1.0 - weight) * block.PostfilterGainsOld[1] * (block.Buffer[offset + index - oldPeriod - 1] + block.Buffer[offset + index - oldPeriod + 1]);
				filtered += (1.0 - weight) * block.PostfilterGainsOld[2] * (block.Buffer[offset + index - oldPeriod - 2] + block.Buffer[offset + index - oldPeriod + 2]);
				filtered += FfmpegMath.MultiplyFloat(FfmpegMath.MultiplyFloat(weight, block.PostfilterGains[0]), x2);
				filtered += FfmpegMath.MultiplyFloat(FfmpegMath.MultiplyFloat(weight, block.PostfilterGains[1]), x1 + x3);
				filtered += FfmpegMath.MultiplyFloat(FfmpegMath.MultiplyFloat(weight, block.PostfilterGains[2]), x0 + x4);
				block.Buffer[offset + index] = (float)(block.Buffer[offset + index] + filtered);
				x4 = x3; x3 = x2; x2 = x1; x1 = x0;
			}
		}

		private static void ApplyPostfilter(CeltBlock block, int offset, int length)
		{
			var period = block.PostfilterPeriod; var x4 = block.Buffer[offset - period - 2]; var x3 = block.Buffer[offset - period - 1]; var x2 = block.Buffer[offset - period]; var x1 = block.Buffer[offset - period + 1];
			for (var index = 0; index < length; index++)
			{
				var x0 = block.Buffer[offset + index - period + 2];
				var filtered = FfmpegMath.MultiplyFloat(block.PostfilterGains[0], x2);
				filtered += FfmpegMath.MultiplyFloat(block.PostfilterGains[1], x1 + x3);
				filtered += FfmpegMath.MultiplyFloat(block.PostfilterGains[2], x0 + x4);
				block.Buffer[offset + index] += filtered;
				x4 = x3; x3 = x2; x2 = x1; x1 = x0;
			}
		}

		private static void Renormalize(float[] values, int offset, int length) { var energy = 1e-15f; for (var index = 0; index < length; index++) energy += FfmpegMath.MultiplyFloat(values[offset + index], values[offset + index]); var scale = 1.0f / MathF.Sqrt(energy); for (var index = 0; index < length; index++) values[offset + index] = FfmpegMath.MultiplyFloat(values[offset + index], scale); }
	}
}
