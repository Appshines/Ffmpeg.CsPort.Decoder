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
using Ffmpeg.CsPort.Decoder.Resampling;

namespace Ffmpeg.CsPort.Decoder.Codecs.Opus
{
	/// <summary>Ports FFmpeg's mono, stereo, and channel-mapped multistream Opus dispatcher and scalar CELT/SILK output.</summary>
	public sealed class OpusDecoder
	{
		private static readonly int[] s_SilkFrameDurationMilliseconds = { 10, 20, 40, 60, 10, 20, 40, 60, 10, 20, 40, 60, 10, 20, 10, 20 };
		private static readonly int[] s_SilkResampleDelay = { 4, 8, 11, 11, 11 };
		private readonly OpusPacket packet = new OpusPacket();
		private readonly OpusRangeDecoder range = new OpusRangeDecoder();
		private readonly OpusRangeDecoder redundancyRange = new OpusRangeDecoder();
		private readonly float[][] samples = { new float[6000], new float[6000] };
		private readonly float[][] silkSamples = { new float[960], new float[960] };
		private readonly float[][] celtSamples = { new float[960], new float[960] };
		private readonly float[][] redundancySamples = { new float[960], new float[960] };
		private readonly float[][] celtDelay = { new float[1024], new float[1024] };
		private readonly float[][] silence = { new float[16], new float[16] };
		private readonly FfmpegFloatResampler[] resamplers = new FfmpegFloatResampler[3];
		private CeltDecoder celt;
		private SilkDecoder silk;
		private FfmpegFloatResampler resampler;
		private int resamplerSampleRate;
		private int delayedSamples;
		private int celtDelayCount;
		private int redundancyIndex;
		private int channels;
		private short gainInteger;
		private float gain = 1.0f;
		private int initialPreSkip;
		private int preSkipRemaining;
		private OpusDecoder[] multistreamDecoders;
		private byte[] multistreamMapping;
		private byte[][] multistreamOutputs;
		private int coupledStreamCount;

		public int Channels => channels;
		public int SampleRate => 48000;

		private OpusDecoder()
		{
		}

		/// <summary>Parses OpusHead mapping families zero and one and preallocates all mono/stereo stream decoder state.</summary>
		public static int Initialize(byte[] extraData, out OpusDecoder decoder)
		{
			decoder = null;
			if (extraData == null || extraData.Length < 19 || extraData[0] != (byte)'O' || extraData[1] != (byte)'p' || extraData[2] != (byte)'u' || extraData[3] != (byte)'s' ||
				extraData[4] != (byte)'H' || extraData[5] != (byte)'e' || extraData[6] != (byte)'a' || extraData[7] != (byte)'d' || extraData[8] > 15)
				return FfmpegError.InvalidData;
			var result = new OpusDecoder();
			result.channels = extraData[9];
			if (result.channels < 1 || result.channels > 8)
				return FfmpegError.InvalidData;
			var mappingFamily = extraData[18];
			if (mappingFamily == 1)
			{
				if (extraData.Length < 21 + result.channels || InitializeMultistream(result, extraData) < 0)
					return FfmpegError.InvalidData;
				decoder = result;
				return 0;
			}
			if (mappingFamily != 0 || result.channels > 2)
				return FfmpegError.InvalidData;
			result.gainInteger = BinaryPrimitives.ReadInt16LittleEndian(extraData.AsSpan(16, 2));
			result.initialPreSkip = result.preSkipRemaining = BinaryPrimitives.ReadUInt16LittleEndian(extraData.AsSpan(10, 2));
			if (result.gainInteger != 0) result.gain = (float)Math.Pow(10, result.gainInteger / (20.0 * 256));
			result.celt = new CeltDecoder(result.channels);
			result.silk = new SilkDecoder(result.channels);
			result.resamplers[0] = new FfmpegFloatResampler(8000, 48000, result.channels);
			result.resamplers[1] = new FfmpegFloatResampler(12000, 48000, result.channels);
			result.resamplers[2] = new FfmpegFloatResampler(16000, 48000, result.channels);
			decoder = result;
			return 0;
		}

		/// <summary>Creates one existing mono/stereo decoder per coded stream and validates every output-channel mapping.</summary>
		private static int InitializeMultistream(OpusDecoder a_Result, byte[] a_ExtraData)
		{
			var l_StreamCount = a_ExtraData[19];
			var l_CoupledCount = a_ExtraData[20];
			if (l_StreamCount < 1 || l_CoupledCount > l_StreamCount || l_StreamCount + l_CoupledCount > a_Result.channels)
				return FfmpegError.InvalidData;
			a_Result.multistreamDecoders = new OpusDecoder[l_StreamCount];
			a_Result.multistreamOutputs = new byte[l_StreamCount][];
			a_Result.multistreamMapping = new byte[a_Result.channels];
			a_Result.coupledStreamCount = l_CoupledCount;
			for (var l_Channel = 0; l_Channel < a_Result.channels; l_Channel++)
			{
				var l_Mapping = a_ExtraData[21 + GetVorbisChannelIndex(a_Result.channels, l_Channel)];
				if (l_Mapping != 255 && l_Mapping >= l_StreamCount + l_CoupledCount)
					return FfmpegError.InvalidData;
				a_Result.multistreamMapping[l_Channel] = l_Mapping;
			}
			for (var l_Stream = 0; l_Stream < l_StreamCount; l_Stream++)
			{
				var l_StreamHead = new byte[19];
				Array.Copy(a_ExtraData, l_StreamHead, l_StreamHead.Length);
				var l_StreamChannels = l_Stream < l_CoupledCount ? 2 : 1;
				l_StreamHead[9] = (byte)l_StreamChannels;
				l_StreamHead[18] = 0;
				if (Initialize(l_StreamHead, out a_Result.multistreamDecoders[l_Stream]) < 0)
					return FfmpegError.InvalidData;
				a_Result.multistreamOutputs[l_Stream] = new byte[OpusPacket.MaximumPacketDuration * l_StreamChannels * sizeof(float)];
			}
			return 0;
		}

		private static int GetVorbisChannelIndex(int a_Channels, int a_NativeChannel)
		{
			ReadOnlySpan<byte> l_Offsets = a_Channels switch
			{
				3 => stackalloc byte[] { 0, 2, 1 },
				4 => stackalloc byte[] { 0, 1, 2, 3 },
				5 => stackalloc byte[] { 0, 2, 1, 3, 4 },
				6 => stackalloc byte[] { 0, 2, 1, 5, 3, 4 },
				7 => stackalloc byte[] { 0, 2, 1, 6, 5, 3, 4 },
				8 => stackalloc byte[] { 0, 2, 1, 7, 5, 6, 3, 4 },
				_ => stackalloc byte[] { 0, 1 }
			};
			return l_Offsets[a_NativeChannel];
		}

		/// <summary>Parses all frames in one Opus packet and writes FFmpeg-ordered planar float bit patterns.</summary>
		public int DecodeFrame(byte[] input, int inputOffset, int inputLength, Span<byte> output, out AudioFrameInfo frame)
		{
			if (multistreamDecoders != null)
				return DecodeMultistream(input, inputOffset, inputLength, output, out frame);
			return DecodeFrameCore(input, inputOffset, inputLength, output, false, out frame);
		}

		/// <summary>Decodes one Opus packet while optionally returning the exact self-delimited byte count required by multistream framing.</summary>
		private int DecodeFrameCore(byte[] input, int inputOffset, int inputLength, Span<byte> output, bool selfDelimiting,
			out AudioFrameInfo frame)
		{
			frame = default;
			if (input == null || inputOffset < 0 || inputLength <= 0 || inputLength > input.Length - inputOffset)
				return FfmpegError.InvalidArgument;
			var status = OpusPacketParser.Parse(packet, input, inputOffset, inputLength, selfDelimiting);
			if (status < 0) return status;
			var maximumDecodedSampleCount = packet.FrameCount * packet.FrameDuration + delayedSamples;
			if (maximumDecodedSampleCount > samples[0].Length) return FfmpegError.InvalidData;
			var maximumSkippedSamples = Math.Min(preSkipRemaining, maximumDecodedSampleCount);
			var maximumSampleCount = maximumDecodedSampleCount - maximumSkippedSamples;
			if (output.Length < maximumSampleCount * sizeof(float) * channels) return FfmpegError.InvalidArgument;
			var decodedSampleCount = 0;
			var silkSampleRate = GetSilkSampleRate(packet.Configuration);
			if (resampler != null && (packet.Mode == OpusMode.Celt || resamplerSampleRate != silkSampleRate))
			{
				status = resampler.Convert(samples, 0, delayedSamples, null, 0, 0);
				if (status != delayedSamples) return FfmpegError.InvalidData;
				if (celtDelayCount != 0)
				{
					if (celtDelayCount != delayedSamples) return FfmpegError.InvalidData;
					for (var channel = 0; channel < channels; channel++) for (var sample = 0; sample < delayedSamples; sample++)
						samples[channel][sample] += celtDelay[channel][sample] * 1.0f;
					celtDelayCount = 0;
				}
				if (redundancyIndex != 0)
				{
					for (var channel = 0; channel < channels; channel++)
						Fade(samples[channel], 0, samples[channel], 0, redundancySamples[channel], 120 + redundancyIndex,
							OpusTables.CeltWindow2, redundancyIndex, 120 - redundancyIndex);
					redundancyIndex = 0;
				}
				decodedSampleCount += status;
				resampler = null;
				delayedSamples = 0;
			}
			for (var frameIndex = 0; frameIndex < packet.FrameCount; frameIndex++)
			{
				var frameOffset = inputOffset + packet.FrameOffsets[frameIndex];
				var frameLength = packet.FrameSizes[frameIndex];
				if (range.Initialize(input, frameOffset, frameLength) < 0) return FfmpegError.InvalidData;
				var frameSamples = packet.FrameDuration;
				var delayedBeforeFrame = delayedSamples;
				if (packet.Mode == OpusMode.Silk || packet.Mode == OpusMode.Hybrid)
				{
					if (resampler == null)
					{
						resampler = resamplers[silkSampleRate == 8000 ? 0 : silkSampleRate == 12000 ? 1 : 2];
						resampler.Reset();
						resamplerSampleRate = silkSampleRate;
						status = resampler.Convert(null, 0, 0, silence, 0, s_SilkResampleDelay[(int)packet.Bandwidth]);
						if (status < 0) return status;
					}
					var nativeSamples = silk.DecodeSuperframe(range, silkSamples, 0,
						packet.Bandwidth < OpusBandwidth.Wideband ? packet.Bandwidth : OpusBandwidth.Wideband,
						packet.Stereo + 1, s_SilkFrameDurationMilliseconds[packet.Configuration]);
					if (nativeSamples < 0) return nativeSamples;
					frameSamples = resampler.Convert(samples, decodedSampleCount, packet.FrameDuration, silkSamples, 0, nativeSamples);
					if (frameSamples < 0) return frameSamples;
					delayedSamples += packet.FrameDuration - frameSamples;
				} else silk.Flush();

				var consumedBits = (int)range.Tell;
				var redundancy = 0;
				if (packet.Mode == OpusMode.Hybrid && consumedBits + 37 <= frameLength * 8) redundancy = (int)range.DecodeLog(12);
				else if (packet.Mode == OpusMode.Silk && consumedBits + 17 <= frameLength * 8) redundancy = 1;
				var redundancyPosition = 0;
				var redundancySize = 0;
				var celtDataSize = frameLength;
				if (redundancy != 0)
				{
					redundancyPosition = (int)range.DecodeLog(1);
					redundancySize = packet.Mode == OpusMode.Hybrid ? (int)range.DecodeUInt(256) + 2 : frameLength - (consumedBits + 7) / 8;
					celtDataSize -= redundancySize;
					if (celtDataSize < 0) return FfmpegError.InvalidData;
					if (redundancyPosition != 0)
					{
						status = DecodeRedundancy(input, frameOffset + celtDataSize, redundancySize);
						if (status < 0) return status;
						celt.Flush();
					}
				}

				if (packet.Mode == OpusMode.Celt || packet.Mode == OpusMode.Hybrid)
				{
					var celtOutputSampleCount = frameSamples;
					var delaySampleCount = celtDelayCount;
					if (delaySampleCount != 0)
					{
						if (packet.Mode != OpusMode.Hybrid) { celtDelayCount = 0; return FfmpegError.InvalidData; }
						for (var channel = 0; channel < channels; channel++)
						{
							Array.Copy(celtDelay[channel], 0, celtSamples[channel], 0, delaySampleCount);
							for (var sample = 0; sample < delaySampleCount; sample++) samples[channel][decodedSampleCount + sample] += celtSamples[channel][sample] * 1.0f;
						}
						celtDelayCount = 0;
						celtOutputSampleCount -= delaySampleCount;
					}
					range.InitializeRaw(frameOffset + celtDataSize, (uint)celtDataSize);
					var destination = packet.Mode == OpusMode.Celt ? samples : celtSamples;
					var destinationOffset = packet.Mode == OpusMode.Celt ? decodedSampleCount : 0;
					status = celt.Decode(range, destination, destinationOffset, packet.Stereo + 1, packet.FrameDuration,
						packet.Mode == OpusMode.Hybrid ? 17 : 0, OpusTables.CeltBandEnd[(int)packet.Bandwidth]);
					if (status < 0) return status;
					if (packet.Mode == OpusMode.Hybrid)
					{
						var celtDelaySamples = packet.FrameDuration - celtOutputSampleCount;
						for (var channel = 0; channel < channels; channel++)
						{
							for (var sample = 0; sample < celtOutputSampleCount; sample++)
								samples[channel][decodedSampleCount + delaySampleCount + sample] += celtSamples[channel][sample] * 1.0f;
							Array.Copy(celtSamples[channel], celtOutputSampleCount, celtDelay[channel], 0, celtDelaySamples);
						}
						celtDelayCount = celtDelaySamples;
					}
				} else celt.Flush();

				if (redundancyIndex != 0)
				{
					for (var channel = 0; channel < channels; channel++)
						Fade(samples[channel], decodedSampleCount, samples[channel], decodedSampleCount,
							redundancySamples[channel], 120 + redundancyIndex, OpusTables.CeltWindow2, redundancyIndex, 120 - redundancyIndex);
					redundancyIndex = 0;
				}
				if (redundancy != 0)
				{
					if (redundancyPosition == 0)
					{
						celt.Flush();
						status = DecodeRedundancy(input, frameOffset + celtDataSize, redundancySize);
						if (status < 0) return status;
						for (var channel = 0; channel < channels; channel++)
							Fade(samples[channel], decodedSampleCount + frameSamples - 120 + delayedBeforeFrame,
								samples[channel], decodedSampleCount + frameSamples - 120 + delayedBeforeFrame,
								redundancySamples[channel], 120, OpusTables.CeltWindow2, 0, 120 - delayedBeforeFrame);
						if (delayedBeforeFrame != 0) redundancyIndex = 120 - delayedBeforeFrame;
					} else
					{
						for (var channel = 0; channel < channels; channel++)
						{
							Array.Copy(redundancySamples[channel], 0, samples[channel], decodedSampleCount + delayedBeforeFrame, 120);
							Fade(samples[channel], decodedSampleCount + 120 + delayedBeforeFrame,
								redundancySamples[channel], 120, samples[channel], decodedSampleCount + 120 + delayedBeforeFrame,
								OpusTables.CeltWindow2, 0, 120);
						}
					}
				}
				decodedSampleCount += frameSamples;
			}
			var skippedSamples = Math.Min(preSkipRemaining, decodedSampleCount);
			var sampleCount = decodedSampleCount - skippedSamples;
			var planeSize = sampleCount * sizeof(float);
			for (var channel = 0; channel < channels; channel++) for (var sample = 0; sample < sampleCount; sample++)
			{
				var value = gainInteger == 0 ? samples[channel][sample + skippedSamples] : samples[channel][sample + skippedSamples] * gain;
				BinaryPrimitives.WriteInt32LittleEndian(output.Slice(channel * planeSize + sample * 4, 4), BitConverter.SingleToInt32Bits(value));
			}
			frame = new AudioFrameInfo(sampleCount, channels, AudioSampleFormat.FloatPlanar, channels, planeSize, planeSize * channels);
			preSkipRemaining -= skippedSamples;
			return selfDelimiting ? packet.PacketSize : inputLength;
		}

		/// <summary>Splits self-delimited coded streams, decodes each without allocation, and remaps their planar channels.</summary>
		private int DecodeMultistream(byte[] a_Input, int a_InputOffset, int a_InputLength, Span<byte> a_Output,
			out AudioFrameInfo a_Frame)
		{
			a_Frame = default;
			if (a_Input == null || a_InputOffset < 0 || a_InputLength <= 0 || a_InputLength > a_Input.Length - a_InputOffset)
				return FfmpegError.InvalidArgument;
			var l_Offset = a_InputOffset;
			var l_Remaining = a_InputLength;
			var l_SampleCount = -1;
			for (var l_Stream = 0; l_Stream < multistreamDecoders.Length; l_Stream++)
			{
				var l_SelfDelimiting = l_Stream + 1 < multistreamDecoders.Length;
				var l_Result = multistreamDecoders[l_Stream].DecodeFrameCore(a_Input, l_Offset, l_Remaining,
					multistreamOutputs[l_Stream], l_SelfDelimiting, out var l_StreamFrame);
				if (l_Result < 0) return l_Result;
				if (l_Result <= 0 || l_Result > l_Remaining || l_SampleCount >= 0 && l_StreamFrame.NumberOfSamples != l_SampleCount)
					return FfmpegError.InvalidData;
				l_SampleCount = l_StreamFrame.NumberOfSamples;
				l_Offset += l_Result;
				l_Remaining -= l_Result;
			}
			if (l_Remaining != 0 || l_SampleCount < 0 || a_Output.Length < l_SampleCount * channels * sizeof(float))
				return FfmpegError.InvalidData;
			var l_PlaneSize = l_SampleCount * sizeof(float);
			for (var l_Channel = 0; l_Channel < channels; l_Channel++)
			{
				var l_Mapping = multistreamMapping[l_Channel];
				if (l_Mapping == 255)
				{
					a_Output.Slice(l_Channel * l_PlaneSize, l_PlaneSize).Clear();
					continue;
				}
				var l_Stream = l_Mapping < coupledStreamCount * 2
					? l_Mapping >> 1
					: coupledStreamCount + l_Mapping - coupledStreamCount * 2;
				var l_StreamChannel = l_Mapping < coupledStreamCount * 2 ? l_Mapping & 1 : 0;
				multistreamOutputs[l_Stream].AsSpan(l_StreamChannel * l_PlaneSize, l_PlaneSize)
					.CopyTo(a_Output.Slice(l_Channel * l_PlaneSize, l_PlaneSize));
			}
			a_Frame = new AudioFrameInfo(l_SampleCount, channels, AudioSampleFormat.FloatPlanar, channels,
				l_PlaneSize, l_PlaneSize * channels);
			return a_InputLength;
		}

		public void Flush()
		{
			if (multistreamDecoders != null)
			{
				for (var l_Stream = 0; l_Stream < multistreamDecoders.Length; l_Stream++) multistreamDecoders[l_Stream].Flush();
				return;
			}
			celt.Flush();
			silk.Flush();
			resampler = null;
			delayedSamples = 0;
			celtDelayCount = 0;
			redundancyIndex = 0;
			preSkipRemaining = initialPreSkip;
		}

		/// <summary>
		/// Clears codec history for a random-access decode without applying the stream-start pre-skip again.
		/// </summary>
		public void PrepareForSeek()
		{
			if (multistreamDecoders != null)
			{
				for (var l_Stream = 0; l_Stream < multistreamDecoders.Length; l_Stream++) multistreamDecoders[l_Stream].PrepareForSeek();
				return;
			}
			Flush();
			preSkipRemaining = 0;
		}

		private int DecodeRedundancy(byte[] input, int offset, int size)
		{
			var status = redundancyRange.Initialize(input, offset, size);
			if (status < 0) return status;
			return celt.Decode(redundancyRange, redundancySamples, 0, packet.Stereo + 1, 240, 0, OpusTables.CeltBandEnd[(int)packet.Bandwidth]);
		}

		private static void Fade(float[] output, int outputOffset, float[] first, int firstOffset, float[] second, int secondOffset,
			float[] window, int windowOffset, int length)
		{
			for (var sample = 0; sample < length; sample++)
			{
				var secondWeighted = second[secondOffset + sample] * window[windowOffset + sample];
				output[outputOffset + sample] = (float)(secondWeighted +
					first[firstOffset + sample] * (1.0 - window[windowOffset + sample]));
			}
		}

		private static int GetSilkSampleRate(int configuration)
		{
			if (configuration < 4) return 8000;
			if (configuration < 8) return 12000;
			return 16000;
		}
	}
}
