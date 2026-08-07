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

namespace Ffmpeg.CsPort.Decoder.Resampling
{
	/// <summary>
	/// Ports InstantDj's libswresample path: scalar input conversion, default-layout rematrixing,
	/// Kaiser rate conversion, and packed floating-point output without native dependencies.
	/// </summary>
	public sealed class FfmpegAudioResampler
	{
		private const int MaximumChannels = 16;
		private const int MaximumSamples = 65536;
		private const double SquareRootOneHalf = 0.70710678118654752440;
		private const float DefaultMixLevel = 0.70710677f;
		private readonly int inputSampleRate;
		private readonly int outputSampleRate;
		private readonly int inputChannels;
		private readonly int outputChannels;
		private readonly AudioSampleFormat inputFormat;
		private readonly bool inputPlanar;
		private readonly int inputBytesPerSample;
		private readonly bool useDouble;
		private readonly bool rematrix;
		private readonly bool resample;
		private readonly bool resampleFirst;
		private readonly float[,] floatMatrix;
		private readonly double[,] doubleMatrix;
		private readonly float[][] floatInput;
		private readonly float[][] floatMiddle;
		private readonly float[][] floatOutput;
		private readonly double[][] doubleInput;
		private readonly double[][] doubleMiddle;
		private readonly double[][] doubleOutput;
		private readonly FfmpegFloatResampler floatResampler;
		private readonly FfmpegDoubleResampler doubleResampler;
		private int pendingIndex;
		private int pendingCount;

		/// <summary>
		/// Selects FFmpeg's internal FLTP or DBLP scalar path and builds the default channel-layout matrix.
		/// </summary>
		public FfmpegAudioResampler(int inputSampleRate, int outputSampleRate, int inputChannels, int outputChannels,
			AudioSampleFormat inputFormat, ulong inputChannelLayout = 0, ulong outputChannelLayout = 0, int filterSize = 32)
		{
			if (inputSampleRate <= 0 || outputSampleRate <= 0 || inputChannels <= 0 || inputChannels > MaximumChannels ||
				outputChannels <= 0 || outputChannels > MaximumChannels || !TryGetFormat(inputFormat, out inputPlanar, out inputBytesPerSample))
				throw new ArgumentOutOfRangeException();
			this.inputSampleRate = inputSampleRate;
			this.outputSampleRate = outputSampleRate;
			this.inputChannels = inputChannels;
			this.outputChannels = outputChannels;
			this.inputFormat = inputFormat;
			if (inputChannelLayout == 0) inputChannelLayout = GetDefaultChannelLayout(inputChannels);
			if (outputChannelLayout == 0) outputChannelLayout = GetDefaultChannelLayout(outputChannels);
			if (CountBits(inputChannelLayout) != inputChannels || CountBits(outputChannelLayout) != outputChannels)
				throw new ArgumentOutOfRangeException();
			useDouble = inputBytesPerSample > 4;
			rematrix = inputChannelLayout != outputChannelLayout;
			resample = inputSampleRate != outputSampleRate;
			resampleFirst = outputChannels / inputChannels - 1 < outputSampleRate / (float)inputSampleRate - 1.0f;

			floatInput = CreateFloatPlanes(inputChannels);
			floatMiddle = CreateFloatPlanes(Math.Max(inputChannels, outputChannels));
			floatOutput = CreateFloatPlanes(outputChannels);
			doubleInput = CreateDoublePlanes(inputChannels);
			doubleMiddle = CreateDoublePlanes(Math.Max(inputChannels, outputChannels));
			doubleOutput = CreateDoublePlanes(outputChannels);
			floatMatrix = new float[outputChannels, inputChannels];
			doubleMatrix = new double[outputChannels, inputChannels];
			BuildMatrix(inputChannelLayout, outputChannelLayout);

			if (resample)
			{
				var resampleChannels = resampleFirst ? inputChannels : outputChannels;
				if (useDouble) doubleResampler = new FfmpegDoubleResampler(inputSampleRate, outputSampleRate, resampleChannels, filterSize);
				else floatResampler = new FfmpegFloatResampler(inputSampleRate, outputSampleRate, resampleChannels, filterSize);
			}
		}

		public void Reset()
		{
			pendingIndex = 0;
			pendingCount = 0;
			floatResampler?.Reset();
			doubleResampler?.Reset();
		}

		/// <summary>
		/// Converts packed or planar FFmpeg sample bytes to packed float output while preserving swr_convert's streaming state.
		/// </summary>
		public int Convert(float[] output, int outputOffset, int outputCount, byte[][] input, int inputOffset, int inputCount)
		{
			if (output == null || outputOffset < 0 || outputCount < 0 || outputCount > MaximumSamples || outputOffset > output.Length - outputCount * outputChannels ||
				inputOffset < 0 || inputCount < 0 || inputCount > MaximumSamples || !ValidateInput(input, inputOffset, inputCount))
				return FfmpegError.InvalidArgument;
			if (!resample)
				return ConvertWithoutResampling(output, outputOffset, outputCount, input, inputOffset, inputCount);

			if (useDouble)
			{
				if (input != null) ConvertInputToDouble(input, inputOffset, inputCount);
				var converted = ConvertResampledDouble(outputCount, input == null ? null : doubleInput, inputCount);
				WritePackedFloat(output, outputOffset, doubleOutput, converted);
				return converted;
			}
			if (input != null) ConvertInputToFloat(input, inputOffset, inputCount);
			var floatConverted = ConvertResampledFloat(outputCount, input == null ? null : floatInput, inputCount);
			WritePackedFloat(output, outputOffset, floatOutput, floatConverted);
			return floatConverted;
		}

		/// <summary>
		/// Runs format conversion and optional rematrixing when input and output rates require no sample interpolation.
		/// </summary>
		private int ConvertWithoutResampling(float[] output, int outputOffset, int outputCount, byte[][] input, int inputOffset, int inputCount)
		{
			var written = Math.Min(outputCount, pendingCount);
			if (written != 0)
			{
				if (useDouble) WritePackedFloat(output, outputOffset, doubleOutput, pendingIndex, written);
				else WritePackedFloat(output, outputOffset, floatOutput, pendingIndex, written);
				pendingIndex += written;
				pendingCount -= written;
				if (pendingCount == 0) pendingIndex = 0;
			}
			if (input == null || inputCount == 0) return written;

			if (useDouble)
			{
				ConvertInputToDouble(input, inputOffset, inputCount);
				if (rematrix) RematrixDouble(doubleMiddle, doubleInput, inputCount);
				else CopyDoublePlanes(doubleInput, doubleMiddle, inputCount);
			} else
			{
				ConvertInputToFloat(input, inputOffset, inputCount);
				if (rematrix) RematrixFloat(floatMiddle, floatInput, inputCount);
				else CopyFloatPlanes(floatInput, floatMiddle, inputCount);
			}
			if (pendingCount != 0)
			{
				if (pendingCount > MaximumSamples - inputCount) return FfmpegError.InvalidArgument;
				if (useDouble)
				{
					CompactDoublePending();
					for (var channel = 0; channel < outputChannels; channel++)
						Array.Copy(doubleMiddle[channel], 0, doubleOutput[channel], pendingCount, inputCount);
				} else
				{
					CompactFloatPending();
					for (var channel = 0; channel < outputChannels; channel++)
						Array.Copy(floatMiddle[channel], 0, floatOutput[channel], pendingCount, inputCount);
				}
				pendingCount += inputCount;
				return written;
			}
			var immediate = Math.Min(outputCount - written, inputCount);
			if (useDouble) WritePackedFloat(output, outputOffset + written * outputChannels, doubleMiddle, 0, immediate);
			else WritePackedFloat(output, outputOffset + written * outputChannels, floatMiddle, 0, immediate);
			var remainder = inputCount - immediate;
			if (remainder != 0)
			{
				if (useDouble)
					for (var channel = 0; channel < outputChannels; channel++)
						Array.Copy(doubleMiddle[channel], immediate, doubleOutput[channel], 0, remainder);
				else
					for (var channel = 0; channel < outputChannels; channel++)
						Array.Copy(floatMiddle[channel], immediate, floatOutput[channel], 0, remainder);
			}
			pendingIndex = 0;
			pendingCount = inputCount - immediate;
			return written + immediate;
		}

		private void CompactFloatPending()
		{
			if (pendingIndex == 0) return;
			for (var channel = 0; channel < outputChannels; channel++)
				Array.Copy(floatOutput[channel], pendingIndex, floatOutput[channel], 0, pendingCount);
			pendingIndex = 0;
		}

		private void CompactDoublePending()
		{
			if (pendingIndex == 0) return;
			for (var channel = 0; channel < outputChannels; channel++)
				Array.Copy(doubleOutput[channel], pendingIndex, doubleOutput[channel], 0, pendingCount);
			pendingIndex = 0;
		}

		private int ConvertResampledFloat(int outputCount, float[][] input, int inputCount)
		{
			if (resampleFirst)
			{
				var count = floatResampler.Convert(floatMiddle, 0, outputCount, input, 0, inputCount);
				if (count < 0) return count;
				if (rematrix) RematrixFloat(floatOutput, floatMiddle, count);
				else CopyFloatPlanes(floatMiddle, floatOutput, count);
				return count;
			}
			if (input != null)
			{
				if (rematrix) RematrixFloat(floatMiddle, input, inputCount);
				else CopyFloatPlanes(input, floatMiddle, inputCount);
			}
			return floatResampler.Convert(floatOutput, 0, outputCount, input == null ? null : floatMiddle, 0, inputCount);
		}

		private int ConvertResampledDouble(int outputCount, double[][] input, int inputCount)
		{
			if (resampleFirst)
			{
				var count = doubleResampler.Convert(doubleMiddle, 0, outputCount, input, 0, inputCount);
				if (count < 0) return count;
				if (rematrix) RematrixDouble(doubleOutput, doubleMiddle, count);
				else CopyDoublePlanes(doubleMiddle, doubleOutput, count);
				return count;
			}
			if (input != null)
			{
				if (rematrix) RematrixDouble(doubleMiddle, input, inputCount);
				else CopyDoublePlanes(input, doubleMiddle, inputCount);
			}
			return doubleResampler.Convert(doubleOutput, 0, outputCount, input == null ? null : doubleMiddle, 0, inputCount);
		}

		private void ConvertInputToFloat(byte[][] input, int inputOffset, int inputCount)
		{
			for (var channel = 0; channel < inputChannels; channel++)
			{
				var plane = inputPlanar ? input[channel] : input[0];
				var byteOffset = (inputPlanar ? inputOffset : inputOffset * inputChannels + channel) * inputBytesPerSample;
				var byteStride = (inputPlanar ? 1 : inputChannels) * inputBytesPerSample;
				for (var sample = 0; sample < inputCount; sample++, byteOffset += byteStride)
				{
					float value;
					switch (GetPackedFormat(inputFormat))
					{
						case AudioSampleFormat.Unsigned8: value = (plane[byteOffset] - 0x80) * (1.0f / (1 << 7)); break;
						case AudioSampleFormat.Signed16: value = BinaryPrimitives.ReadInt16LittleEndian(plane.AsSpan(byteOffset)) * (1.0f / (1 << 15)); break;
						case AudioSampleFormat.Signed32: value = BinaryPrimitives.ReadInt32LittleEndian(plane.AsSpan(byteOffset)) * (1.0f / (1U << 31)); break;
						default: value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(plane.AsSpan(byteOffset))); break;
					}
					floatInput[channel][sample] = value;
				}
			}
		}

		private void ConvertInputToDouble(byte[][] input, int inputOffset, int inputCount)
		{
			for (var channel = 0; channel < inputChannels; channel++)
			{
				var plane = inputPlanar ? input[channel] : input[0];
				var byteOffset = (inputPlanar ? inputOffset : inputOffset * inputChannels + channel) * inputBytesPerSample;
				var byteStride = (inputPlanar ? 1 : inputChannels) * inputBytesPerSample;
				for (var sample = 0; sample < inputCount; sample++, byteOffset += byteStride)
				{
					double value;
					if (GetPackedFormat(inputFormat) == AudioSampleFormat.Signed64)
						value = BinaryPrimitives.ReadInt64LittleEndian(plane.AsSpan(byteOffset)) * (1.0 / (1UL << 63));
					else
						value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(plane.AsSpan(byteOffset)));
					doubleInput[channel][sample] = value;
				}
			}
		}

		/// <summary>
		/// Applies the scalar single-precision rematrix in FFmpeg's output-channel, input-channel, sample loop order.
		/// </summary>
		private void RematrixFloat(float[][] output, float[][] input, int count)
		{
			if (inputChannels == 6 && outputChannels == 2)
			{
				for (var sample = 0; sample < count; sample++)
				{
					var common = input[2][sample] * floatMatrix[0, 2] + input[3][sample] * floatMatrix[0, 3];
					output[0][sample] = common + input[0][sample] * floatMatrix[0, 0] + input[4][sample] * floatMatrix[0, 4];
					output[1][sample] = common + input[1][sample] * floatMatrix[1, 1] + input[5][sample] * floatMatrix[1, 5];
				}
				return;
			}
			if (inputChannels == 8 && outputChannels == 2)
			{
				for (var sample = 0; sample < count; sample++)
				{
					var common = input[2][sample] * floatMatrix[0, 2] + input[3][sample] * floatMatrix[0, 3];
					output[0][sample] = common + input[0][sample] * floatMatrix[0, 0] + input[4][sample] * floatMatrix[0, 4] + input[6][sample] * floatMatrix[0, 6];
					output[1][sample] = common + input[1][sample] * floatMatrix[1, 1] + input[5][sample] * floatMatrix[1, 5] + input[7][sample] * floatMatrix[1, 7];
				}
				return;
			}
			for (var outputChannel = 0; outputChannel < outputChannels; outputChannel++)
			{
				var coefficientCount = 0;
				var first = 0;
				var second = 0;
				for (var inputChannel = 0; inputChannel < inputChannels; inputChannel++)
					if (floatMatrix[outputChannel, inputChannel] != 0) { if (coefficientCount++ == 0) first = inputChannel; else if (coefficientCount == 2) second = inputChannel; }
				for (var sample = 0; sample < count; sample++)
				{
					if (coefficientCount == 0) output[outputChannel][sample] = 0;
					else if (coefficientCount == 1) output[outputChannel][sample] = input[first][sample] * floatMatrix[outputChannel, first];
					else if (coefficientCount == 2) output[outputChannel][sample] = input[first][sample] * floatMatrix[outputChannel, first] + input[second][sample] * floatMatrix[outputChannel, second];
					else
					{
						float value = 0;
						for (var inputChannel = 0; inputChannel < inputChannels; inputChannel++)
							if (floatMatrix[outputChannel, inputChannel] != 0) value += input[inputChannel][sample] * floatMatrix[outputChannel, inputChannel];
						output[outputChannel][sample] = value;
					}
				}
			}
		}

		/// <summary>
		/// Applies the scalar double-precision rematrix in FFmpeg's output-channel, input-channel, sample loop order.
		/// </summary>
		private void RematrixDouble(double[][] output, double[][] input, int count)
		{
			if (inputChannels == 6 && outputChannels == 2)
			{
				for (var sample = 0; sample < count; sample++)
				{
					var common = input[2][sample] * doubleMatrix[0, 2] + input[3][sample] * doubleMatrix[0, 3];
					output[0][sample] = common + input[0][sample] * doubleMatrix[0, 0] + input[4][sample] * doubleMatrix[0, 4];
					output[1][sample] = common + input[1][sample] * doubleMatrix[1, 1] + input[5][sample] * doubleMatrix[1, 5];
				}
				return;
			}
			if (inputChannels == 8 && outputChannels == 2)
			{
				for (var sample = 0; sample < count; sample++)
				{
					var common = input[2][sample] * doubleMatrix[0, 2] + input[3][sample] * doubleMatrix[0, 3];
					output[0][sample] = common + input[0][sample] * doubleMatrix[0, 0] + input[4][sample] * doubleMatrix[0, 4] + input[6][sample] * doubleMatrix[0, 6];
					output[1][sample] = common + input[1][sample] * doubleMatrix[1, 1] + input[5][sample] * doubleMatrix[1, 5] + input[7][sample] * doubleMatrix[1, 7];
				}
				return;
			}
			for (var outputChannel = 0; outputChannel < outputChannels; outputChannel++)
			{
				var coefficientCount = 0;
				var first = 0;
				var second = 0;
				for (var inputChannel = 0; inputChannel < inputChannels; inputChannel++)
					if (doubleMatrix[outputChannel, inputChannel] != 0) { if (coefficientCount++ == 0) first = inputChannel; else if (coefficientCount == 2) second = inputChannel; }
				for (var sample = 0; sample < count; sample++)
				{
					if (coefficientCount == 0) output[outputChannel][sample] = 0;
					else if (coefficientCount == 1) output[outputChannel][sample] = input[first][sample] * doubleMatrix[outputChannel, first];
					else if (coefficientCount == 2) output[outputChannel][sample] = input[first][sample] * doubleMatrix[outputChannel, first] + input[second][sample] * doubleMatrix[outputChannel, second];
					else
					{
						double value = 0;
						for (var inputChannel = 0; inputChannel < inputChannels; inputChannel++)
							if (doubleMatrix[outputChannel, inputChannel] != 0) value += input[inputChannel][sample] * doubleMatrix[outputChannel, inputChannel];
						output[outputChannel][sample] = value;
					}
				}
			}
		}

		/// <summary>Builds FFmpeg's default matrix coefficients for named layouts in channel-bit order.</summary>
		private void BuildMatrix(ulong inputLayout, ulong outputLayout)
		{
			var named = new double[36, 36];
			var centerMixLevel = (double)DefaultMixLevel;
			var surroundMixLevel = (double)DefaultMixLevel;
			var unaccounted = inputLayout & ~outputLayout;
			for (var channel = 0; channel < 36; channel++) if (Has(inputLayout, channel) && Has(outputLayout, channel)) named[channel, channel] = 1.0;
			if (Has(unaccounted, 2))
			{
				var coefficient = Has(inputLayout, 0) || Has(inputLayout, 1) ? centerMixLevel : SquareRootOneHalf;
				named[0, 2] += coefficient; named[1, 2] += coefficient;
			}
			if ((unaccounted & 3) != 0 && Has(outputLayout, 2))
			{
				named[2, 0] += SquareRootOneHalf; named[2, 1] += SquareRootOneHalf;
				if (Has(inputLayout, 2)) named[2, 2] = centerMixLevel * Math.Sqrt(2.0);
			}
			if (Has(unaccounted, 8))
			{
				if (Has(outputLayout, 4)) { named[4, 8] += SquareRootOneHalf; named[5, 8] += SquareRootOneHalf; }
				else if (Has(outputLayout, 9)) { named[9, 8] += SquareRootOneHalf; named[10, 8] += SquareRootOneHalf; }
				else if (Has(outputLayout, 0))
				{
					var coefficient = surroundMixLevel * SquareRootOneHalf;
					named[0, 8] += coefficient; named[1, 8] += coefficient;
				}
				else if (Has(outputLayout, 2)) named[2, 8] += surroundMixLevel * SquareRootOneHalf;
			}
			if (Has(unaccounted, 4))
			{
				if (Has(outputLayout, 8)) { named[8, 4] += SquareRootOneHalf; named[8, 5] += SquareRootOneHalf; }
				else if (Has(outputLayout, 9))
				{
					var coefficient = Has(inputLayout, 9) ? SquareRootOneHalf : 1.0;
					named[9, 4] += coefficient; named[10, 5] += coefficient;
				} else if (Has(outputLayout, 0)) { named[0, 4] += surroundMixLevel; named[1, 5] += surroundMixLevel; }
				else if (Has(outputLayout, 2))
				{
					var coefficient = surroundMixLevel * SquareRootOneHalf;
					named[2, 4] += coefficient; named[2, 5] += coefficient;
				}
			}
			if (Has(unaccounted, 9))
			{
				if (Has(outputLayout, 4))
				{
					var coefficient = Has(inputLayout, 4) ? SquareRootOneHalf : 1.0;
					named[4, 9] += coefficient; named[5, 10] += coefficient;
				} else if (Has(outputLayout, 8)) { named[8, 9] += SquareRootOneHalf; named[8, 10] += SquareRootOneHalf; }
				else if (Has(outputLayout, 0)) { named[0, 9] += surroundMixLevel; named[1, 10] += surroundMixLevel; }
				else if (Has(outputLayout, 2))
				{
					var coefficient = surroundMixLevel * SquareRootOneHalf;
					named[2, 9] += coefficient; named[2, 10] += coefficient;
				}
			}
			if (Has(unaccounted, 3))
			{
				if (Has(outputLayout, 2)) named[2, 3] += 0.0;
				else if (Has(outputLayout, 0)) { named[0, 3] += 0.0; named[1, 3] += 0.0; }
			}

			for (var outputChannel = 0; outputChannel < outputChannels; outputChannel++)
			{
				var outputBit = GetChannelBit(outputLayout, outputChannel);
				for (var inputChannel = 0; inputChannel < inputChannels; inputChannel++)
				{
					var inputBit = GetChannelBit(inputLayout, inputChannel);
					doubleMatrix[outputChannel, inputChannel] = named[outputBit, inputBit];
					floatMatrix[outputChannel, inputChannel] = (float)named[outputBit, inputBit];
				}
			}
		}

		private bool ValidateInput(byte[][] input, int inputOffset, int inputCount)
		{
			if (input == null) return inputCount == 0;
			var planeCount = inputPlanar ? inputChannels : 1;
			if (input.Length < planeCount) return false;
			var required = (inputOffset + inputCount) * inputBytesPerSample * (inputPlanar ? 1 : inputChannels);
			for (var plane = 0; plane < planeCount; plane++) if (input[plane] == null || input[plane].Length < required) return false;
			return true;
		}

		private static bool TryGetFormat(AudioSampleFormat format, out bool planar, out int bytesPerSample)
		{
			planar = format >= AudioSampleFormat.Unsigned8Planar && format <= AudioSampleFormat.DoublePlanar ||
				format == AudioSampleFormat.Signed64Planar;
			var packed = GetPackedFormat(format);
			bytesPerSample = packed == AudioSampleFormat.Unsigned8 ? 1 : packed == AudioSampleFormat.Signed16 ? 2 :
				packed == AudioSampleFormat.Signed32 || packed == AudioSampleFormat.Float ? 4 :
				packed == AudioSampleFormat.Double || packed == AudioSampleFormat.Signed64 ? 8 : 0;
			return bytesPerSample != 0;
		}

		private static AudioSampleFormat GetPackedFormat(AudioSampleFormat format)
		{
			if (format == AudioSampleFormat.Signed64Planar) return AudioSampleFormat.Signed64;
			return format >= AudioSampleFormat.Unsigned8Planar && format <= AudioSampleFormat.DoublePlanar
				? (AudioSampleFormat)((int)format - 5) : format;
		}

		private static ulong GetDefaultChannelLayout(int channels)
		{
			return channels switch
			{
				1 => 1UL << 2,
				2 => (1UL << 0) | (1UL << 1),
				3 => (1UL << 0) | (1UL << 1) | (1UL << 3),
				4 => (1UL << 0) | (1UL << 1) | (1UL << 4) | (1UL << 5),
				5 => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 4) | (1UL << 5),
				6 => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 9) | (1UL << 10),
				7 => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 8) | (1UL << 9) | (1UL << 10),
				8 => (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 4) | (1UL << 5) | (1UL << 9) | (1UL << 10),
				_ => channels == 0 ? 0 : (1UL << channels) - 1
			};
		}

		private static bool Has(ulong layout, int channel) => (layout & (1UL << channel)) != 0;

		private static int GetChannelBit(ulong layout, int index)
		{
			for (var bit = 0; bit < 64; bit++) if ((layout & (1UL << bit)) != 0 && index-- == 0) return bit;
			return -1;
		}

		private static int CountBits(ulong value)
		{
			var count = 0;
			while (value != 0) { value &= value - 1; count++; }
			return count;
		}

		private static float[][] CreateFloatPlanes(int channels)
		{
			var planes = new float[channels][];
			for (var channel = 0; channel < channels; channel++) planes[channel] = new float[MaximumSamples];
			return planes;
		}

		private static double[][] CreateDoublePlanes(int channels)
		{
			var planes = new double[channels][];
			for (var channel = 0; channel < channels; channel++) planes[channel] = new double[MaximumSamples];
			return planes;
		}

		private void CopyFloatPlanes(float[][] source, float[][] destination, int count)
		{
			for (var channel = 0; channel < outputChannels; channel++) Array.Copy(source[channel], destination[channel], count);
		}

		private void CopyDoublePlanes(double[][] source, double[][] destination, int count)
		{
			for (var channel = 0; channel < outputChannels; channel++) Array.Copy(source[channel], destination[channel], count);
		}

		private void WritePackedFloat(float[] destination, int destinationOffset, float[][] source, int count)
		{
			WritePackedFloat(destination, destinationOffset, source, 0, count);
		}

		private void WritePackedFloat(float[] destination, int destinationOffset, float[][] source, int sourceOffset, int count)
		{
			for (var sample = 0; sample < count; sample++)
				for (var channel = 0; channel < outputChannels; channel++)
					destination[destinationOffset++] = source[channel][sourceOffset + sample];
		}

		private void WritePackedFloat(float[] destination, int destinationOffset, double[][] source, int count)
		{
			WritePackedFloat(destination, destinationOffset, source, 0, count);
		}

		private void WritePackedFloat(float[] destination, int destinationOffset, double[][] source, int sourceOffset, int count)
		{
			for (var sample = 0; sample < count; sample++)
				for (var channel = 0; channel < outputChannels; channel++)
					destination[destinationOffset++] = (float)source[channel][sourceOffset + sample];
		}
	}
}
