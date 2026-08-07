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
using System.Collections.Generic;
using Ffmpeg.CsPort.Decoder.Audio;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs
{
	/// <summary>
	/// Ports FFmpeg's WavPack 4 lossless integer entropy and decorrelation decoder for mono, stereo, and multiblock streams.
	/// </summary>
	public sealed class WavPackDecoder
	{
		private const uint c_Signature = 0x6b707677;
		private const uint c_Mono = 0x00000004;
		private const uint c_Hybrid = 0x00000008;
		private const uint c_JointStereo = 0x00000010;
		private const uint c_Float = 0x00000080;
		private const uint c_FalseStereo = 0x40000000;
		private const uint c_Dsd = 0x80000000;
		private static readonly byte[] s_Exp2 =
		{
			0x00,0x01,0x01,0x02,0x03,0x03,0x04,0x05,0x06,0x06,0x07,0x08,0x08,0x09,0x0a,0x0b,
			0x0b,0x0c,0x0d,0x0e,0x0e,0x0f,0x10,0x10,0x11,0x12,0x13,0x13,0x14,0x15,0x16,0x16,
			0x17,0x18,0x19,0x19,0x1a,0x1b,0x1c,0x1d,0x1d,0x1e,0x1f,0x20,0x20,0x21,0x22,0x23,
			0x24,0x24,0x25,0x26,0x27,0x28,0x28,0x29,0x2a,0x2b,0x2c,0x2c,0x2d,0x2e,0x2f,0x30,
			0x30,0x31,0x32,0x33,0x34,0x35,0x35,0x36,0x37,0x38,0x39,0x3a,0x3a,0x3b,0x3c,0x3d,
			0x3e,0x3f,0x40,0x41,0x41,0x42,0x43,0x44,0x45,0x46,0x47,0x48,0x48,0x49,0x4a,0x4b,
			0x4c,0x4d,0x4e,0x4f,0x50,0x51,0x51,0x52,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5a,
			0x5b,0x5c,0x5d,0x5e,0x5e,0x5f,0x60,0x61,0x62,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
			0x6a,0x6b,0x6c,0x6d,0x6e,0x6f,0x70,0x71,0x72,0x73,0x74,0x75,0x76,0x77,0x78,0x79,
			0x7a,0x7b,0x7c,0x7d,0x7e,0x7f,0x80,0x81,0x82,0x83,0x84,0x85,0x87,0x88,0x89,0x8a,
			0x8b,0x8c,0x8d,0x8e,0x8f,0x90,0x91,0x92,0x93,0x95,0x96,0x97,0x98,0x99,0x9a,0x9b,
			0x9c,0x9d,0x9f,0xa0,0xa1,0xa2,0xa3,0xa4,0xa5,0xa6,0xa8,0xa9,0xaa,0xab,0xac,0xad,
			0xaf,0xb0,0xb1,0xb2,0xb3,0xb4,0xb6,0xb7,0xb8,0xb9,0xba,0xbc,0xbd,0xbe,0xbf,0xc0,
			0xc2,0xc3,0xc4,0xc5,0xc6,0xc8,0xc9,0xca,0xcb,0xcd,0xce,0xcf,0xd0,0xd2,0xd3,0xd4,
			0xd6,0xd7,0xd8,0xd9,0xdb,0xdc,0xdd,0xde,0xe0,0xe1,0xe2,0xe4,0xe5,0xe6,0xe8,0xe9,
			0xea,0xec,0xed,0xee,0xf0,0xf1,0xf2,0xf4,0xf5,0xf6,0xf8,0xf9,0xfa,0xfc,0xfd,0xff
		};

		private readonly int _Channels;
		private readonly List<FrameState> _States = new List<FrameState>();
		private int[] _Samples = Array.Empty<int>();

		private WavPackDecoder(int a_Channels) { _Channels = a_Channels; }
		public int Channels => _Channels;

		public static int Initialize(int a_Channels, byte[] a_ExtraData, out WavPackDecoder a_Decoder)
		{
			a_Decoder = null;
			if (a_Channels <= 0 || a_Channels > 4096 || a_ExtraData == null || a_ExtraData.Length < 2) return FfmpegError.InvalidArgument;
			var l_Version = BinaryPrimitives.ReadUInt16LittleEndian(a_ExtraData);
			if (l_Version < 0x402 || l_Version > 0x410) return FfmpegError.PatchWelcome;
			a_Decoder = new WavPackDecoder(a_Channels);
			return 0;
		}

		/// <summary>Decodes every physical block in one demuxed WavPack access unit into FFmpeg-compatible planar integer samples.</summary>
		public int DecodeFrame(byte[] a_Packet, int a_Offset, int a_Length, Span<byte> a_Output, out AudioFrameInfo a_Frame)
		{
			a_Frame = default;
			if (a_Packet == null || a_Offset < 0 || a_Length <= 32 || a_Length > a_Packet.Length - a_Offset) return FfmpegError.InvalidArgument;
			var l_End = a_Offset + a_Length;
			var l_Position = a_Offset;
			var l_ChannelOffset = 0;
			var l_SampleCount = -1;
			var l_BytesPerSample = 0;
			var l_BlockNumber = 0;
			while (l_Position < l_End)
			{
				if (l_Position > l_End - 32 || BinaryPrimitives.ReadUInt32LittleEndian(a_Packet.AsSpan(l_Position, 4)) != c_Signature) return FfmpegError.InvalidData;
				var l_StoredSize = BinaryPrimitives.ReadUInt32LittleEndian(a_Packet.AsSpan(l_Position + 4, 4));
				var l_BlockSize = checked((int)l_StoredSize + 8);
				if (l_BlockSize < 32 || l_Position > l_End - l_BlockSize) return FfmpegError.InvalidData;
				var l_CurrentSamples = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(a_Packet.AsSpan(l_Position + 20, 4)));
				if (l_CurrentSamples <= 0 || l_CurrentSamples > 150000 || (l_SampleCount >= 0 && l_CurrentSamples != l_SampleCount)) return FfmpegError.InvalidData;
				var l_Flags = BinaryPrimitives.ReadUInt32LittleEndian(a_Packet.AsSpan(l_Position + 24, 4));
				if ((l_Flags & (c_Hybrid | c_Float | c_Dsd)) != 0) return FfmpegError.PatchWelcome;
				var l_CurrentBytes = (l_Flags & 3) <= 1 ? 2 : 4;
				if (l_BytesPerSample != 0 && l_CurrentBytes != l_BytesPerSample) return FfmpegError.InvalidData;
				l_SampleCount = l_CurrentSamples; l_BytesPerSample = l_CurrentBytes;
				var l_CodedChannels = (l_Flags & c_Mono) != 0 ? 1 : 2;
				EnsureSampleCapacity(l_SampleCount, _Channels);
				var l_State = GetState(l_BlockNumber++);
				var l_Result = DecodeBlock(a_Packet, l_Position, l_BlockSize, l_State, l_ChannelOffset, l_CodedChannels, l_SampleCount, l_BytesPerSample);
				if (l_Result < 0) return l_Result;
				l_ChannelOffset += l_CodedChannels;
				l_Position += l_BlockSize;
			}
			if (l_ChannelOffset != _Channels || l_SampleCount <= 0) return FfmpegError.InvalidData;
			var l_DataSize = checked(l_SampleCount * _Channels * l_BytesPerSample);
			if (a_Output.Length < l_DataSize) return FfmpegError.InvalidArgument;
			WriteOutput(a_Output, l_SampleCount, l_BytesPerSample);
			var l_Format = l_BytesPerSample == 2 ? AudioSampleFormat.Signed16Planar : AudioSampleFormat.Signed32Planar;
			a_Frame = new AudioFrameInfo(l_SampleCount, _Channels, l_Format, _Channels, l_SampleCount * l_BytesPerSample, l_DataSize);
			return a_Length;
		}

		private int DecodeBlock(byte[] a_Data, int a_BlockStart, int a_BlockSize, FrameState a_State, int a_ChannelOffset, int a_CodedChannels, int a_Samples, int a_BytesPerSample)
		{
			a_State.Reset();
			var l_Flags = BinaryPrimitives.ReadUInt32LittleEndian(a_Data.AsSpan(a_BlockStart + 24, 4));
			a_State.Stereo = (l_Flags & c_Mono) == 0;
			a_State.StereoInput = (l_Flags & c_FalseStereo) == 0 && a_State.Stereo;
			a_State.Joint = (l_Flags & c_JointStereo) != 0;
			a_State.PostShift = a_BytesPerSample * 8 - ((((int)l_Flags & 3) + 1) << 3) + ((int)(l_Flags >> 13) & 0x1f);
			if (a_State.PostShift < 0 || a_State.PostShift > 31) return FfmpegError.InvalidData;
			var l_Result = ParseMetadata(a_Data, a_BlockStart + 32, a_BlockStart + a_BlockSize, a_State);
			if (l_Result < 0) return l_Result;
			if (!a_State.GotTerms || !a_State.GotWeights || !a_State.GotSamples || !a_State.GotEntropy || !a_State.GotData) return FfmpegError.InvalidData;
			if (a_State.StereoInput)
				return UnpackStereo(a_State, a_ChannelOffset, a_Samples, a_BytesPerSample == 2);
			l_Result = UnpackMono(a_State, a_ChannelOffset, a_Samples, a_BytesPerSample == 2);
			if (l_Result >= 0 && a_CodedChannels == 2)
				Array.Copy(_Samples, a_ChannelOffset * a_Samples, _Samples, (a_ChannelOffset + 1) * a_Samples, a_Samples);
			return l_Result;
		}

		/// <summary>Parses decorrelation, entropy, integer-extension, and packed-data metadata in one physical block.</summary>
		private static int ParseMetadata(byte[] a_Data, int a_Position, int a_End, FrameState a_State)
		{
			while (a_Position < a_End)
			{
				if (a_Position > a_End - 2) return FfmpegError.InvalidData;
				var l_Id = a_Data[a_Position++];
				var l_Words = (int)a_Data[a_Position++];
				if ((l_Id & 0x80) != 0)
				{
					if (a_Position > a_End - 2) return FfmpegError.InvalidData;
					l_Words |= a_Data[a_Position++] << 8 | a_Data[a_Position++] << 16;
				}
				var l_StoredSize = checked(l_Words * 2);
				var l_Size = l_StoredSize - (((l_Id & 0x40) != 0) ? 1 : 0);
				if (l_Size < 0 || a_Position > a_End - l_StoredSize) return FfmpegError.InvalidData;
				var l_Result = ParseMetadataItem(l_Id & 0x3f, a_Data, a_Position, l_Size, a_State);
				if (l_Result < 0) return l_Result;
				a_Position += l_StoredSize;
			}
			return 0;
		}

		private static int ParseMetadataItem(int a_Id, byte[] a_Data, int a_Position, int a_Size, FrameState a_State)
		{
			switch (a_Id)
			{
				case 2: return ParseTerms(a_Data, a_Position, a_Size, a_State);
				case 3: return ParseWeights(a_Data, a_Position, a_Size, a_State);
				case 4: return ParseSamples(a_Data, a_Position, a_Size, a_State);
				case 5: return ParseEntropy(a_Data, a_Position, a_Size, a_State);
				case 9: return ParseIntegerInfo(a_Data, a_Position, a_Size, a_State);
				case 10:
					a_State.Data.Initialize(a_Data, a_Position, a_Size); a_State.GotData = true; return 0;
				case 12:
					if (a_Size <= 4) return FfmpegError.InvalidData;
					a_State.ExtraData.Initialize(a_Data, a_Position, a_Size); if (!a_State.ExtraData.Skip(32)) return FfmpegError.InvalidData;
					a_State.GotExtraData = true; return 0;
				default: return 0;
			}
		}

		private static int ParseTerms(byte[] a_Data, int a_Position, int a_Size, FrameState a_State)
		{
			if (a_Size > 16) return FfmpegError.InvalidData;
			a_State.TermCount = a_Size;
			for (var l_Index = 0; l_Index < a_Size; l_Index++)
			{
				var l_Value = a_Data[a_Position + l_Index]; var l_Term = a_State.Terms[a_Size - l_Index - 1];
				l_Term.Value = (l_Value & 0x1f) - 5; l_Term.Delta = l_Value >> 5;
				if (l_Term.Value < -3 || l_Term.Value == 0 || l_Term.Value > 18) return FfmpegError.InvalidData;
			}
			a_State.GotTerms = true; return 0;
		}

		private static int ParseWeights(byte[] a_Data, int a_Position, int a_Size, FrameState a_State)
		{
			if (!a_State.GotTerms) return FfmpegError.InvalidData;
			var l_Weights = a_Size >> (a_State.StereoInput ? 1 : 0);
			if (l_Weights > a_State.TermCount) return FfmpegError.InvalidData;
			for (var l_Index = 0; l_Index < l_Weights; l_Index++)
			{
				var l_Term = a_State.Terms[a_State.TermCount - l_Index - 1];
				l_Term.WeightA = ExpandWeight(unchecked((sbyte)a_Data[a_Position++]));
				if (a_State.StereoInput) l_Term.WeightB = ExpandWeight(unchecked((sbyte)a_Data[a_Position++]));
			}
			a_State.GotWeights = true; return 0;
		}

		private static int ParseSamples(byte[] a_Data, int a_Position, int a_Size, FrameState a_State)
		{
			if (!a_State.GotTerms) return FfmpegError.InvalidData;
			var l_End = a_Position + a_Size;
			for (var l_Index = a_State.TermCount - 1; l_Index >= 0 && a_Position < l_End; l_Index--)
			{
				var l_Term = a_State.Terms[l_Index];
				if (l_Term.Value > 8)
				{
					if (!ReadExpandedPair(a_Data, ref a_Position, l_End, l_Term.SamplesA)) return FfmpegError.InvalidData;
					if (a_State.StereoInput && !ReadExpandedPair(a_Data, ref a_Position, l_End, l_Term.SamplesB)) return FfmpegError.InvalidData;
				} else if (l_Term.Value < 0)
				{
					if (!ReadExpanded(a_Data, ref a_Position, l_End, out l_Term.SamplesA[0]) || !ReadExpanded(a_Data, ref a_Position, l_End, out l_Term.SamplesB[0])) return FfmpegError.InvalidData;
				} else
				{
					for (var l_Sample = 0; l_Sample < l_Term.Value; l_Sample++)
					{
						if (!ReadExpanded(a_Data, ref a_Position, l_End, out l_Term.SamplesA[l_Sample])) return FfmpegError.InvalidData;
						if (a_State.StereoInput && !ReadExpanded(a_Data, ref a_Position, l_End, out l_Term.SamplesB[l_Sample])) return FfmpegError.InvalidData;
					}
				}
			}
			a_State.GotSamples = true; return 0;
		}

		private static int ParseEntropy(byte[] a_Data, int a_Position, int a_Size, FrameState a_State)
		{
			if (a_Size != 6 * (a_State.StereoInput ? 2 : 1)) return FfmpegError.InvalidData;
			for (var l_Channel = 0; l_Channel <= (a_State.StereoInput ? 1 : 0); l_Channel++)
				for (var l_Index = 0; l_Index < 3; l_Index++, a_Position += 2)
					a_State.Channels[l_Channel].Median[l_Index] = Exp2(BinaryPrimitives.ReadInt16LittleEndian(a_Data.AsSpan(a_Position, 2)));
			a_State.GotEntropy = true; return 0;
		}

		private static int ParseIntegerInfo(byte[] a_Data, int a_Position, int a_Size, FrameState a_State)
		{
			if (a_Size != 4 || a_Data[a_Position] > 30) return FfmpegError.InvalidData;
			a_State.ExtraBits = a_Data[a_Position];
			if (a_Data[a_Position + 1] != 0) a_State.Shift = a_Data[a_Position + 1];
			if (a_Data[a_Position + 2] != 0) { a_State.And = 1; a_State.Or = 1; a_State.Shift = a_Data[a_Position + 2]; }
			if (a_Data[a_Position + 3] != 0) { a_State.And = 1; a_State.Shift = a_Data[a_Position + 3]; }
			return a_State.Shift <= 31 ? 0 : FfmpegError.InvalidData;
		}

		/// <summary>Restores the two entropy channels, applies every decorrelation term, and reconstructs joint stereo.</summary>
		private int UnpackStereo(FrameState a_State, int a_ChannelOffset, int a_SampleCount, bool a_Use16BitArithmetic)
		{
			var l_Position = 0;
			for (var l_Sample = 0; l_Sample < a_SampleCount; l_Sample++)
			{
				if (!TryGetValue(a_State, 0, out var l_Left) || !TryGetValue(a_State, 1, out var l_Right)) return FfmpegError.InvalidData;
				for (var l_Index = 0; l_Index < a_State.TermCount; l_Index++)
					DecorrelateStereo(a_State.Terms[l_Index], ref l_Left, ref l_Right, l_Position, a_Use16BitArithmetic);
				l_Position = (l_Position + 1) & 7;
				if (a_State.Joint) l_Left = unchecked(l_Left + (l_Right = unchecked(l_Right - (l_Left >> 1))));
				_Samples[a_ChannelOffset * a_SampleCount + l_Sample] = RestoreInteger(a_State, l_Left);
				_Samples[(a_ChannelOffset + 1) * a_SampleCount + l_Sample] = RestoreInteger(a_State, l_Right);
			}
			return 0;
		}

		private int UnpackMono(FrameState a_State, int a_ChannelOffset, int a_SampleCount, bool a_Use16BitArithmetic)
		{
			var l_Position = 0;
			for (var l_Sample = 0; l_Sample < a_SampleCount; l_Sample++)
			{
				if (!TryGetValue(a_State, 0, out var l_Value)) return FfmpegError.InvalidData;
				for (var l_Index = 0; l_Index < a_State.TermCount; l_Index++)
					l_Value = DecorrelateMono(a_State.Terms[l_Index], l_Value, l_Position, a_Use16BitArithmetic);
				l_Position = (l_Position + 1) & 7;
				_Samples[a_ChannelOffset * a_SampleCount + l_Sample] = RestoreInteger(a_State, l_Value);
			}
			return 0;
		}

		private static void DecorrelateStereo(DecorrelationTerm a_Term, ref int a_Left, ref int a_Right, int a_Position, bool a_Use16Bit)
		{
			if (a_Term.Value > 0)
			{
				int l_A; int l_B; int l_Store;
				if (a_Term.Value > 8)
				{
					l_A = a_Term.Value % 2 != 0 ? unchecked(2 * a_Term.SamplesA[0] - a_Term.SamplesA[1]) : unchecked((3 * a_Term.SamplesA[0] - a_Term.SamplesA[1]) >> 1);
					l_B = a_Term.Value % 2 != 0 ? unchecked(2 * a_Term.SamplesB[0] - a_Term.SamplesB[1]) : unchecked((3 * a_Term.SamplesB[0] - a_Term.SamplesB[1]) >> 1);
					a_Term.SamplesA[1] = a_Term.SamplesA[0]; a_Term.SamplesB[1] = a_Term.SamplesB[0]; l_Store = 0;
				} else { l_A = a_Term.SamplesA[a_Position]; l_B = a_Term.SamplesB[a_Position]; l_Store = (a_Position + a_Term.Value) & 7; }
				var l_ResidualLeft = a_Left; var l_ResidualRight = a_Right;
				a_Left = ApplyWeight(a_Left, a_Term.WeightA, l_A, a_Use16Bit); a_Right = ApplyWeight(a_Right, a_Term.WeightB, l_B, a_Use16Bit);
				UpdateWeight(a_Term, true, l_A, l_ResidualLeft, false); UpdateWeight(a_Term, false, l_B, l_ResidualRight, false);
				a_Term.SamplesA[l_Store] = a_Left; a_Term.SamplesB[l_Store] = a_Right;
				return;
			}
			if (a_Term.Value == -1)
			{
				var l_ResidualLeft = a_Left; a_Left = ApplyWeight(a_Left, a_Term.WeightA, a_Term.SamplesA[0], a_Use16Bit);
				UpdateWeight(a_Term, true, a_Term.SamplesA[0], l_ResidualLeft, true);
				var l_ResidualRight = a_Right; a_Right = ApplyWeight(a_Right, a_Term.WeightB, a_Left, a_Use16Bit);
				UpdateWeight(a_Term, false, a_Left, l_ResidualRight, true); a_Term.SamplesA[0] = a_Right; return;
			}
			var l_RightResidual = a_Right; var l_RightSource = a_Term.SamplesB[0]; a_Right = ApplyWeight(a_Right, a_Term.WeightB, l_RightSource, a_Use16Bit);
			UpdateWeight(a_Term, false, l_RightSource, l_RightResidual, true);
			var l_LeftSource = a_Right;
			if (a_Term.Value == -3) { l_LeftSource = a_Term.SamplesA[0]; a_Term.SamplesA[0] = a_Right; }
			var l_LeftResidual = a_Left; a_Left = ApplyWeight(a_Left, a_Term.WeightA, l_LeftSource, a_Use16Bit);
			UpdateWeight(a_Term, true, l_LeftSource, l_LeftResidual, true); a_Term.SamplesB[0] = a_Left;
		}

		private static int DecorrelateMono(DecorrelationTerm a_Term, int a_Value, int a_Position, bool a_Use16Bit)
		{
			int l_Source; int l_Store;
			if (a_Term.Value > 8)
			{
				l_Source = a_Term.Value % 2 != 0 ? unchecked(2 * a_Term.SamplesA[0] - a_Term.SamplesA[1]) : unchecked((3 * a_Term.SamplesA[0] - a_Term.SamplesA[1]) >> 1);
				a_Term.SamplesA[1] = a_Term.SamplesA[0]; l_Store = 0;
			} else { l_Source = a_Term.SamplesA[a_Position]; l_Store = (a_Position + a_Term.Value) & 7; }
			var l_Residual = a_Value; var l_Result = ApplyWeight(a_Value, a_Term.WeightA, l_Source, a_Use16Bit);
			UpdateWeight(a_Term, true, l_Source, l_Residual, false); a_Term.SamplesA[l_Store] = l_Result; return l_Result;
		}

		/// <summary>Decodes one WavPack entropy value including zero runs, adaptive medians, and truncated-binary tails.</summary>
		private static bool TryGetValue(FrameState a_State, int a_Channel, out int a_Value)
		{
			var l_Channel = a_State.Channels[a_Channel]; a_Value = 0;
			if (a_State.Channels[0].Median[0] < 2 && a_State.Channels[1].Median[0] < 2 && !a_State.Zero && !a_State.One)
			{
				if (a_State.Zeroes > 0)
				{
					a_State.Zeroes--; if (a_State.Zeroes > 0) return true;
				} else
				{
					if (!a_State.Data.TryReadUnary(out var l_Zeroes)) return false;
					if (l_Zeroes >= 2)
					{
						if (l_Zeroes >= 32 || !a_State.Data.TryRead(l_Zeroes - 1, out var l_Tail)) return false;
						l_Zeroes = l_Tail | 1 << (l_Zeroes - 1);
					}
					a_State.Zeroes = l_Zeroes;
					if (a_State.Zeroes > 0)
					{
						Array.Clear(a_State.Channels[0].Median, 0, 3); Array.Clear(a_State.Channels[1].Median, 0, 3); return true;
					}
				}
			}
			int l_Index;
			if (a_State.Zero) { l_Index = 0; a_State.Zero = false; }
			else
			{
				if (!a_State.Data.TryReadUnary(out l_Index)) return false;
				if (l_Index == 16)
				{
					if (!a_State.Data.TryReadUnary(out var l_More)) return false;
					if (l_More < 2) l_Index += l_More;
					else { if (l_More >= 32 || !a_State.Data.TryRead(l_More - 1, out var l_Tail)) return false; l_Index += l_Tail | 1 << (l_More - 1); }
				}
				if (a_State.One) { a_State.One = (l_Index & 1) != 0; l_Index = (l_Index >> 1) + 1; }
				else { a_State.One = (l_Index & 1) != 0; l_Index >>= 1; }
				a_State.Zero = !a_State.One;
			}
			var l_Base = 0; int l_Add;
			if (l_Index == 0) { l_Add = GetMedian(l_Channel, 0) - 1; DecreaseMedian(l_Channel, 0); }
			else if (l_Index == 1) { l_Base = GetMedian(l_Channel, 0); l_Add = GetMedian(l_Channel, 1) - 1; IncreaseMedian(l_Channel, 0); DecreaseMedian(l_Channel, 1); }
			else if (l_Index == 2) { l_Base = GetMedian(l_Channel, 0) + GetMedian(l_Channel, 1); l_Add = GetMedian(l_Channel, 2) - 1; IncreaseMedian(l_Channel, 0); IncreaseMedian(l_Channel, 1); DecreaseMedian(l_Channel, 2); }
			else { l_Base = unchecked(GetMedian(l_Channel, 0) + GetMedian(l_Channel, 1) + GetMedian(l_Channel, 2) * (l_Index - 2)); l_Add = GetMedian(l_Channel, 2) - 1; IncreaseMedian(l_Channel, 0); IncreaseMedian(l_Channel, 1); IncreaseMedian(l_Channel, 2); }
			if (!TryReadTail(a_State.Data, unchecked((uint)l_Add), out var l_TailValue) || !a_State.Data.TryRead(1, out var l_Sign)) return false;
			var l_Result = unchecked((uint)l_Base + l_TailValue); a_Value = l_Sign != 0 ? unchecked((int)~l_Result) : unchecked((int)l_Result); return true;
		}

		private static bool TryReadTail(LittleBitReader a_Reader, uint a_Maximum, out uint a_Value)
		{
			a_Value = 0; if (a_Maximum < 1) return true;
			var l_Bits = 31 - System.Numerics.BitOperations.LeadingZeroCount(a_Maximum);
			var l_Escape = (1U << (l_Bits + 1)) - a_Maximum - 1;
			if (!a_Reader.TryRead(l_Bits, out var l_Result)) return false;
			if ((uint)l_Result >= l_Escape) { if (!a_Reader.TryRead(1, out var l_Bit)) return false; l_Result = unchecked((int)((uint)l_Result * 2 - l_Escape + (uint)l_Bit)); }
			a_Value = unchecked((uint)l_Result); return true;
		}

		private static int RestoreInteger(FrameState a_State, int a_Value)
		{
			var l_Value = unchecked((uint)a_Value);
			if (a_State.ExtraBits > 0)
			{
				l_Value <<= a_State.ExtraBits;
				if (a_State.GotExtraData && a_State.ExtraData.TryRead(a_State.ExtraBits, out var l_Extra)) l_Value |= unchecked((uint)l_Extra);
			}
			var l_Bit = (l_Value & a_State.And) | a_State.Or;
			l_Value = unchecked(((l_Value + l_Bit) << a_State.Shift) - l_Bit);
			return unchecked((int)(l_Value << a_State.PostShift));
		}

		private static int ApplyWeight(int a_Residual, int a_Weight, int a_Source, bool a_Use16Bit)
		{
			if (!a_Use16Bit) return unchecked(a_Residual + (int)((a_Weight * (long)a_Source + 512) >> 10));
			return unchecked(a_Residual + ((a_Weight * a_Source + 512) >> 10));
		}

		private static void UpdateWeight(DecorrelationTerm a_Term, bool a_Left, int a_Source, int a_Residual, bool a_Clip)
		{
			if (a_Source == 0 || a_Residual == 0) return;
			var l_Change = ((a_Source ^ a_Residual) < 0 ? -a_Term.Delta : a_Term.Delta);
			if (a_Left) a_Term.WeightA += l_Change; else a_Term.WeightB += l_Change;
			if (!a_Clip) return;
			if (a_Left) a_Term.WeightA = Math.Clamp(a_Term.WeightA, -1024, 1024); else a_Term.WeightB = Math.Clamp(a_Term.WeightB, -1024, 1024);
		}

		private static int GetMedian(EntropyChannel a_Channel, int a_Index) => (a_Channel.Median[a_Index] >> 4) + 1;
		private static void DecreaseMedian(EntropyChannel a_Channel, int a_Index) => a_Channel.Median[a_Index] -= (a_Channel.Median[a_Index] + (128 >> a_Index) - 2) / (128 >> a_Index) * 2;
		private static void IncreaseMedian(EntropyChannel a_Channel, int a_Index) => a_Channel.Median[a_Index] += (a_Channel.Median[a_Index] + (128 >> a_Index)) / (128 >> a_Index) * 5;
		private static int ExpandWeight(int a_Value) { var l_Result = a_Value * 8; if (l_Result > 0) l_Result += (l_Result + 64) >> 7; return l_Result; }

		private static int Exp2(short a_Value)
		{
			var l_Negative = a_Value < 0; var l_Value = l_Negative ? -a_Value : a_Value;
			var l_Result = s_Exp2[l_Value & 0xff] | 0x100; l_Value >>= 8;
			if ((uint)l_Value > 31) return int.MinValue;
			l_Result = l_Value > 9 ? unchecked(l_Result << (l_Value - 9)) : l_Result >> (9 - l_Value);
			return l_Negative ? -l_Result : l_Result;
		}

		private static bool ReadExpandedPair(byte[] a_Data, ref int a_Position, int a_End, int[] a_Target)
		{
			if (!ReadExpanded(a_Data, ref a_Position, a_End, out var l_First) || !ReadExpanded(a_Data, ref a_Position, a_End, out var l_Second)) return false;
			a_Target[0] = l_First; a_Target[1] = l_Second; return true;
		}

		private static bool ReadExpanded(byte[] a_Data, ref int a_Position, int a_End, out int a_Value)
		{
			a_Value = 0; if (a_Position > a_End - 2) return false;
			a_Value = Exp2(BinaryPrimitives.ReadInt16LittleEndian(a_Data.AsSpan(a_Position, 2))); a_Position += 2; return true;
		}

		private void EnsureSampleCapacity(int a_Samples, int a_Channels)
		{
			var l_Size = checked(a_Samples * a_Channels); if (_Samples.Length < l_Size) _Samples = new int[l_Size];
		}

		private FrameState GetState(int a_Index)
		{
			while (_States.Count <= a_Index) _States.Add(new FrameState()); return _States[a_Index];
		}

		private void WriteOutput(Span<byte> a_Output, int a_Samples, int a_BytesPerSample)
		{
			var l_Count = a_Samples * _Channels;
			if (a_BytesPerSample == 2)
				for (var l_Index = 0; l_Index < l_Count; l_Index++) BinaryPrimitives.WriteInt16LittleEndian(a_Output.Slice(l_Index * 2, 2), unchecked((short)_Samples[l_Index]));
			else
				for (var l_Index = 0; l_Index < l_Count; l_Index++) BinaryPrimitives.WriteInt32LittleEndian(a_Output.Slice(l_Index * 4, 4), _Samples[l_Index]);
		}

		private sealed class FrameState
		{
			public readonly DecorrelationTerm[] Terms = new DecorrelationTerm[16];
			public readonly EntropyChannel[] Channels = { new EntropyChannel(), new EntropyChannel() };
			public readonly LittleBitReader Data = new LittleBitReader(); public readonly LittleBitReader ExtraData = new LittleBitReader();
			public int TermCount; public int ExtraBits; public int Shift; public uint And; public uint Or; public int PostShift; public int Zeroes;
			public bool Stereo; public bool StereoInput; public bool Joint; public bool Zero; public bool One; public bool GotExtraData;
			public bool GotTerms; public bool GotWeights; public bool GotSamples; public bool GotEntropy; public bool GotData;
			public FrameState() { for (var l_Index = 0; l_Index < Terms.Length; l_Index++) Terms[l_Index] = new DecorrelationTerm(); }
			public void Reset()
			{
				TermCount = ExtraBits = Shift = PostShift = Zeroes = 0; And = Or = 0; Stereo = StereoInput = Joint = Zero = One = GotExtraData = false;
				GotTerms = GotWeights = GotSamples = GotEntropy = GotData = false;
				for (var l_Index = 0; l_Index < Terms.Length; l_Index++) Terms[l_Index].Reset(); Channels[0].Reset(); Channels[1].Reset();
			}
		}

		private sealed class DecorrelationTerm
		{
			public int Delta; public int Value; public int WeightA; public int WeightB; public readonly int[] SamplesA = new int[8]; public readonly int[] SamplesB = new int[8];
			public void Reset() { Delta = Value = WeightA = WeightB = 0; Array.Clear(SamplesA, 0, SamplesA.Length); Array.Clear(SamplesB, 0, SamplesB.Length); }
		}

		private sealed class EntropyChannel { public readonly int[] Median = new int[3]; public void Reset() => Array.Clear(Median, 0, Median.Length); }

		/// <summary>Reads WavPack's least-significant-bit-first packed integers with exact bounds.</summary>
		private sealed class LittleBitReader
		{
			private byte[] _Data; private int _Start; private int _BitLength; private int _Position;
			public void Initialize(byte[] a_Data, int a_Start, int a_Length) { _Data = a_Data; _Start = a_Start; _BitLength = a_Length * 8; _Position = 0; }
			public bool Skip(int a_Count) { if (a_Count < 0 || _Position > _BitLength - a_Count) return false; _Position += a_Count; return true; }
			public bool TryRead(int a_Count, out int a_Value)
			{
				a_Value = 0; if (a_Count < 0 || a_Count > 31 || _Position > _BitLength - a_Count) return false;
				for (var l_Index = 0; l_Index < a_Count; l_Index++) { var l_Bit = _Position + l_Index; a_Value |= ((_Data[_Start + (l_Bit >> 3)] >> (l_Bit & 7)) & 1) << l_Index; }
				_Position += a_Count; return true;
			}
			public bool TryReadUnary(out int a_Value)
			{
				a_Value = 0; while (a_Value < 33) { if (!TryRead(1, out var l_Bit)) return false; if (l_Bit == 0) break; a_Value++; } return true;
			}
		}
	}
}
