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
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Resampling
{
	/// <summary>Ports libswresample's scalar planar-float Kaiser resampling path and its streaming buffers.</summary>
	internal sealed class FfmpegFloatResampler
	{
		private const int PhaseShift = 10;
		private const int MaximumBufferedSamples = 65536;
		private readonly int channels;
		private readonly int configuredFilterSize;
		private readonly int phaseCount;
		private readonly int filterLength;
		private readonly int filterAllocation;
		private readonly float[] filterBank;
		private readonly float[][] inputBuffer;
		private int sourceIncrement;
		private int destinationIncrement;
		private int destinationIncrementDivision;
		private int destinationIncrementRemainder;
		private int index;
		private int fraction;
		private int inputBufferIndex;
		private int inputBufferCount;
		private bool inputConstrained;
		private bool flushed;
		private readonly int initialIndex;

		public FfmpegFloatResampler(int inputSampleRate, int outputSampleRate, int channels)
			: this(inputSampleRate, outputSampleRate, channels, 16)
		{
		}

		internal FfmpegFloatResampler(int inputSampleRate, int outputSampleRate, int channels, int filterSize)
		{
			if (inputSampleRate <= 0 || outputSampleRate <= 0 || channels <= 0 || filterSize <= 0)
				throw new ArgumentOutOfRangeException();
			this.channels = channels;
			configuredFilterSize = filterSize;
			var cutoff = 0.97;
			var factor = Math.Min(outputSampleRate * cutoff / inputSampleRate, 1.0);
			var currentPhaseCount = 1 << PhaseShift;
			filterLength = Math.Max((int)Math.Ceiling(configuredFilterSize / factor), 1);
			if (filterLength > 1) filterLength = filterLength + 1 & ~1;
			FfmpegMath.Reduce(out var exactPhaseCount, out _, outputSampleRate, inputSampleRate, int.MaxValue);
			if (exactPhaseCount <= currentPhaseCount) currentPhaseCount = exactPhaseCount;
			phaseCount = currentPhaseCount;
			filterAllocation = filterLength + 7 & ~7;
			filterBank = new float[filterAllocation * (phaseCount + 1)];
			BuildFilter(factor, 9.0);
			for (var filter = 0; filter < filterAllocation - 1; filter++) filterBank[filterAllocation * phaseCount + 1 + filter] = filterBank[filter];
			filterBank[filterAllocation * phaseCount] = filterBank[filterAllocation - 1];
			if (!FfmpegMath.Reduce(out sourceIncrement, out destinationIncrement,
				outputSampleRate, inputSampleRate * (long)phaseCount, int.MaxValue / 2))
				throw new ArgumentOutOfRangeException();
			while (destinationIncrement < 1 << 20 && sourceIncrement < 1 << 20)
			{
				destinationIncrement *= 2;
				sourceIncrement *= 2;
			}
			destinationIncrementDivision = destinationIncrement / sourceIncrement;
			destinationIncrementRemainder = destinationIncrement % sourceIncrement;
			initialIndex = index = -phaseCount * ((filterLength - 1) / 2);
			inputBuffer = new float[channels][];
			for (var channel = 0; channel < channels; channel++) inputBuffer[channel] = new float[MaximumBufferedSamples];
		}

		public void Reset()
		{
			index = initialIndex;
			fraction = 0;
			inputBufferIndex = 0;
			inputBufferCount = 0;
			inputConstrained = false;
			flushed = false;
		}

		/// <summary>Consumes one planar input block and writes as many scalar-path output samples as the destination permits.</summary>
		public int Convert(float[][] output, int outputOffset, int outputCount, float[][] input, int inputOffset, int inputCount)
		{
			if (outputCount < 0 || inputCount < 0 || outputOffset < 0 || inputOffset < 0 ||
				(output != null && !ValidatePlanes(output, outputOffset, outputCount)) ||
				(input != null && !ValidatePlanes(input, inputOffset, inputCount)) || input == null && inputCount != 0)
				return -22;
			if (input == null)
			{
				if (!flushed) AddFlushReflection();
				inputConstrained = false;
				flushed = true;
			}

			var outputWritten = 0;
			var border = InvertInitialBuffer(input, inputOffset, inputCount);
			if (border == int.MaxValue) return 0;
			if (border < 0) return border;
			if (border != 0)
			{
				inputOffset += border;
				inputCount -= border;
				inputConstrained = false;
			}

			var padding = 7;
			do
			{
				if (!inputConstrained && inputBufferCount != 0)
				{
					var converted = Resample(output, outputOffset + outputWritten, outputCount - outputWritten,
						inputBuffer, inputBufferIndex, inputBufferCount, out var consumed);
					outputWritten += converted;
					inputBufferCount -= consumed;
					inputBufferIndex += consumed;
					if (inputCount == 0) break;
					if (inputBufferCount <= border)
					{
						inputOffset -= inputBufferCount;
						inputCount += inputBufferCount;
						inputBufferCount = 0;
						inputBufferIndex = 0;
						border = 0;
					}
				}

				if ((flushed || inputCount > padding) && inputBufferCount == 0)
				{
					var converted = Resample(output, outputOffset + outputWritten, outputCount - outputWritten,
						input, inputOffset, Math.Max(inputCount - padding, 0), out var consumed);
					outputWritten += converted;
					inputCount -= consumed;
					inputOffset += consumed;
				}

				if (inputCount != 0)
				{
					CompactInputBuffer(inputCount);
					var count = inputCount;
					if (inputBufferCount != 0 && inputBufferCount + 2 < count && outputCount - outputWritten != 0) count = inputBufferCount + 2;
					CopyPlanes(input, inputOffset, inputBuffer, inputBufferIndex + inputBufferCount, count);
					inputBufferCount += count;
					inputCount -= count;
					inputOffset += count;
					border += count;
					inputConstrained = false;
					if (inputBufferCount != count || inputCount != 0) continue;
					if (padding != 0) { padding = 0; continue; }
				}
				break;
			} while (true);

			inputConstrained = outputCount - outputWritten != 0;
			return outputWritten;
		}

		private int InvertInitialBuffer(float[][] input, int inputOffset, int inputCount)
		{
			if (index >= 0) return 0;
			var count = Math.Min(inputCount + inputBufferCount, filterLength + 1);
			for (var sample = inputBufferCount; sample < count; sample++)
				for (var channel = 0; channel < channels; channel++)
					inputBuffer[channel][filterLength + sample] = input[channel][inputOffset + sample - inputBufferCount];
			if (count < filterLength + 1)
			{
				inputBufferCount = count;
				inputBufferIndex = filterLength;
				return int.MaxValue;
			}
			for (var sample = 1; sample <= filterLength; sample++)
				for (var channel = 0; channel < channels; channel++)
					inputBuffer[channel][filterLength - sample] = inputBuffer[channel][filterLength + sample];
			var consumed = count - inputBufferCount;
			inputBufferIndex = filterLength;
			while (index < 0)
			{
				inputBufferIndex--;
				index += phaseCount;
			}
			inputBufferCount = Math.Max(inputBufferCount + filterLength, 1 + filterLength * 2) - inputBufferIndex;
			return Math.Max(consumed, 0);
		}

		private int Resample(float[][] output, int outputOffset, int outputCount, float[][] input, int inputOffset, int inputCount, out int consumed)
		{
			var endIndex = (1L + inputCount - filterLength) * phaseCount;
			var deltaFraction = (endIndex - index) * sourceIncrement - fraction;
			var outputSamples = (int)((deltaFraction + destinationIncrement - 1) / destinationIncrement);
			outputSamples = Math.Max(Math.Min(outputCount, outputSamples), 0);
			consumed = 0;
			if (outputSamples == 0) return 0;
			for (var channel = 0; channel < channels; channel++)
				consumed = destinationIncrementRemainder != 0 || fraction != 0
					? ResampleLinear(output[channel], outputOffset, input[channel], inputOffset, outputSamples, channel + 1 == channels)
					: ResampleCommon(output[channel], outputOffset, input[channel], inputOffset, outputSamples, channel + 1 == channels);
			return outputSamples;
		}

		private int ResampleCommon(float[] output, int outputOffset, float[] input, int inputOffset, int count, bool updateContext)
		{
			var currentIndex = index;
			var currentFraction = fraction;
			var sampleIndex = 0;
			while (currentIndex >= phaseCount) { sampleIndex++; currentIndex -= phaseCount; }
			for (var outputIndex = 0; outputIndex < count; outputIndex++)
			{
				var filterOffset = filterAllocation * currentIndex;
				float value = 0;
				float secondValue = 0;
				var filter = 0;
				for (; filter + 1 < filterLength; filter += 2)
				{
					value += input[inputOffset + sampleIndex + filter] * filterBank[filterOffset + filter];
					secondValue += input[inputOffset + sampleIndex + filter + 1] * filterBank[filterOffset + filter + 1];
				}
				if (filter < filterLength) value += input[inputOffset + sampleIndex + filter] * filterBank[filterOffset + filter];
				output[outputOffset + outputIndex] = value + secondValue;
				currentFraction += destinationIncrementRemainder;
				currentIndex += destinationIncrementDivision;
				if (currentFraction >= sourceIncrement) { currentFraction -= sourceIncrement; currentIndex++; }
				while (currentIndex >= phaseCount) { sampleIndex++; currentIndex -= phaseCount; }
			}
			if (updateContext) { fraction = currentFraction; index = currentIndex; }
			return sampleIndex;
		}

		private int ResampleLinear(float[] output, int outputOffset, float[] input, int inputOffset, int count, bool updateContext)
		{
			var currentIndex = index;
			var currentFraction = fraction;
			var sampleIndex = 0;
			var inverseSourceIncrement = 1.0 / sourceIncrement;
			while (currentIndex >= phaseCount) { sampleIndex++; currentIndex -= phaseCount; }
			for (var outputIndex = 0; outputIndex < count; outputIndex++)
			{
				var filterOffset = filterAllocation * currentIndex;
				float value = 0;
				float nextValue = 0;
				for (var filter = 0; filter < filterLength; filter++)
				{
					value += input[inputOffset + sampleIndex + filter] * filterBank[filterOffset + filter];
					nextValue += input[inputOffset + sampleIndex + filter] * filterBank[filterOffset + filterAllocation + filter];
				}
				value += (float)((nextValue - value) * inverseSourceIncrement * currentFraction);
				output[outputOffset + outputIndex] = value;
				currentFraction += destinationIncrementRemainder;
				currentIndex += destinationIncrementDivision;
				if (currentFraction >= sourceIncrement) { currentFraction -= sourceIncrement; currentIndex++; }
				while (currentIndex >= phaseCount) { sampleIndex++; currentIndex -= phaseCount; }
			}
			if (updateContext) { fraction = currentFraction; index = currentIndex; }
			return sampleIndex;
		}

		private void BuildFilter(double factor, double kaiserBeta)
		{
			var phaseRows = phaseCount % 2 != 0 ? phaseCount : phaseCount / 2 + 1;
			var table = new double[filterLength + 1];
			var sineLookup = new double[phaseRows];
			var center = (filterLength - 1) / 2;
			double normalization = 0;
			if (factor > 1.0) factor = 1.0;
			if (factor == 1.0)
				for (var phase = 0; phase < phaseRows; phase++) sineLookup[phase] = Math.Sin(Math.PI * phase / phaseCount) * ((center & 1) != 0 ? 1 : -1);
			for (var phase = 0; phase < phaseRows; phase++)
			{
				var sine = sineLookup[phase];
				for (var filter = 0; filter < filterLength; filter++)
				{
					var x = Math.PI * ((double)(filter - center) - (double)phase / phaseCount) * factor;
					double value;
					if (x == 0) value = 1.0;
					else if (factor == 1.0) value = sine / x;
					else value = Math.Sin(x) / x;
					var window = 2.0 * x / (factor * filterLength * Math.PI);
					value *= FfmpegMath.BesselI0(kaiserBeta * Math.Sqrt(Math.Max(1 - window * window, 0)));
					table[filter] = value;
					sine = -sine;
					if (phase == 0) normalization += value;
				}
				for (var filter = 0; filter < filterLength; filter++) filterBank[phase * filterAllocation + filter] = (float)(table[filter] / normalization);
				if (phaseCount % 2 != 0) continue;
				for (var filter = 0; filter < filterLength; filter++)
					filterBank[(phaseCount - phase) * filterAllocation + filterLength - 1 - filter] = filterBank[phase * filterAllocation + filter];
			}
		}

		private void AddFlushReflection()
		{
			var reflection = (Math.Min(inputBufferCount, filterLength) + 1) / 2;
			CompactInputBuffer(reflection);
			for (var channel = 0; channel < channels; channel++)
				for (var sample = 0; sample < reflection; sample++)
					inputBuffer[channel][inputBufferIndex + inputBufferCount + sample] = inputBuffer[channel][inputBufferIndex + inputBufferCount - sample - 1];
			inputBufferCount += reflection;
		}

		private void CompactInputBuffer(int additionalCount)
		{
			if (inputBufferIndex + inputBufferCount + additionalCount <= MaximumBufferedSamples) return;
			if (inputBufferCount + additionalCount > MaximumBufferedSamples) throw new InvalidOperationException("The resampler input buffer capacity was exceeded.");
			for (var channel = 0; channel < channels; channel++) Array.Copy(inputBuffer[channel], inputBufferIndex, inputBuffer[channel], 0, inputBufferCount);
			inputBufferIndex = 0;
		}

		private bool ValidatePlanes(float[][] planes, int offset, int count)
		{
			if (planes.Length < channels) return false;
			for (var channel = 0; channel < channels; channel++) if (planes[channel] == null || offset > planes[channel].Length - count) return false;
			return true;
		}

		private void CopyPlanes(float[][] source, int sourceOffset, float[][] destination, int destinationOffset, int count)
		{
			for (var channel = 0; channel < channels; channel++) Array.Copy(source[channel], sourceOffset, destination[channel], destinationOffset, count);
		}
	}
}
