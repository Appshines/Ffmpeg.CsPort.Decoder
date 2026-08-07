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
	/// Ports FFmpeg's Microsoft GSM 6.10 decoder, including its two 160-sample frames per 65-byte WAVE block and persistent synthesis filters.
	/// </summary>
	public sealed class GsmMicrosoftDecoder
	{
		private static readonly ushort[] s_LongTermGain = { 3277, 11469, 21299, 32767 };
		private static readonly short[] s_Dequant =
		{
			-28,-20,-12,-4,4,12,20,28,-56,-40,-24,-8,8,24,40,56,-84,-60,-36,-12,12,36,60,84,-112,-80,-48,-16,16,48,80,112,
			-140,-100,-60,-20,20,60,100,140,-168,-120,-72,-24,24,72,120,168,-196,-140,-84,-28,28,84,140,196,-224,-160,-96,-32,32,96,160,224,
			-252,-180,-108,-36,36,108,180,252,-280,-200,-120,-40,40,120,200,280,-308,-220,-132,-44,44,132,220,308,-336,-240,-144,-48,48,144,240,336,
			-364,-260,-156,-52,52,156,260,364,-392,-280,-168,-56,56,168,280,392,-420,-300,-180,-60,60,180,300,420,-448,-320,-192,-64,64,192,320,448,
			-504,-360,-216,-72,72,216,360,504,-560,-400,-240,-80,80,240,400,560,-616,-440,-264,-88,88,264,440,616,-672,-480,-288,-96,96,288,480,672,
			-728,-520,-312,-104,104,312,520,728,-784,-560,-336,-112,112,336,560,784,-840,-600,-360,-120,120,360,600,840,-896,-640,-384,-128,128,384,640,896,
			-1008,-720,-432,-144,144,432,720,1008,-1120,-800,-480,-160,160,480,800,1120,-1232,-880,-528,-176,176,528,880,1232,-1344,-960,-576,-192,192,576,960,1344,
			-1456,-1040,-624,-208,208,624,1040,1456,-1568,-1120,-672,-224,224,672,1120,1568,-1680,-1200,-720,-240,240,720,1200,1680,-1792,-1280,-768,-256,256,768,1280,1792,
			-2016,-1440,-864,-288,288,864,1440,2016,-2240,-1600,-960,-320,320,960,1600,2240,-2464,-1760,-1056,-352,352,1056,1760,2464,-2688,-1920,-1152,-384,384,1152,1920,2688,
			-2912,-2080,-1248,-416,416,1248,2080,2912,-3136,-2240,-1344,-448,448,1344,2240,3136,-3360,-2400,-1440,-480,480,1440,2400,3360,-3584,-2560,-1536,-512,512,1536,2560,3584,
			-4032,-2880,-1728,-576,576,1728,2880,4032,-4480,-3200,-1920,-640,640,1920,3200,4480,-4928,-3520,-2112,-704,704,2112,3520,4928,-5376,-3840,-2304,-768,768,2304,3840,5376,
			-5824,-4160,-2496,-832,832,2496,4160,5824,-6272,-4480,-2688,-896,896,2688,4480,6272,-6720,-4800,-2880,-960,960,2880,4800,6720,-7168,-5120,-3072,-1024,1024,3072,5120,7168,
			-8063,-5759,-3456,-1152,1152,3456,5760,8064,-8959,-6399,-3840,-1280,1280,3840,6400,8960,-9855,-7039,-4224,-1408,1408,4224,7040,9856,-10751,-7679,-4608,-1536,1536,4608,7680,10752,
			-11647,-8319,-4992,-1664,1664,4992,8320,11648,-12543,-8959,-5376,-1792,1792,5376,8960,12544,-13439,-9599,-5760,-1920,1920,5760,9600,13440,-14335,-10239,-6144,-2048,2048,6144,10240,14336,
			-16127,-11519,-6912,-2304,2304,6912,11519,16127,-17919,-12799,-7680,-2560,2560,7680,12799,17919,-19711,-14079,-8448,-2816,2816,8448,14079,19711,-21503,-15359,-9216,-3072,3072,9216,15359,21503,
			-23295,-16639,-9984,-3328,3328,9984,16639,23295,-25087,-17919,-10752,-3584,3584,10752,17919,25087,-26879,-19199,-11520,-3840,3840,11520,19199,26879,-28671,-20479,-12288,-4096,4096,12288,20479,28671
		};

		private readonly short[] _Reference = new short[280];
		private readonly int[] _FilterMemory = new int[9];
		private readonly int[,] _LogAreaRatios = new int[2, 8];
		private readonly short[] _OutputSamples = new short[320];
		private readonly int[] _ReflectionCoefficients = new int[8];
		private readonly LittleBitReader _BitReader = new LittleBitReader();
		private int _LogAreaIndex;
		private int _PostprocessMemory;

		public AudioSampleFormat SampleFormat => AudioSampleFormat.Signed16;
		public int Channels => 1;

		public static int Initialize(int a_Channels, int a_BlockAlign, out GsmMicrosoftDecoder a_Decoder)
		{
			a_Decoder = null;
			if (a_Channels != 1 || a_BlockAlign != 65) return FfmpegError.InvalidData;
			a_Decoder = new GsmMicrosoftDecoder();
			return 0;
		}

		/// <summary>Decodes the two interleaved Microsoft GSM frames from one exact 65-byte block.</summary>
		public int DecodeFrame(byte[] a_Packet, int a_Offset, int a_Length, Span<byte> a_Output, out AudioFrameInfo a_Frame)
		{
			a_Frame = default;
			if (a_Packet == null || a_Offset < 0 || a_Length < 65 || a_Length > a_Packet.Length - a_Offset || a_Output.Length < 640)
				return FfmpegError.InvalidArgument;
			_BitReader.Initialize(a_Packet, a_Offset, 65);
			DecodeBlock(_BitReader, _OutputSamples, 0);
			DecodeBlock(_BitReader, _OutputSamples, 160);
			for (var l_Index = 0; l_Index < _OutputSamples.Length; l_Index++)
				BinaryPrimitives.WriteInt16LittleEndian(a_Output.Slice(l_Index * 2, 2), _OutputSamples[l_Index]);
			a_Frame = new AudioFrameInfo(320, 1, AudioSampleFormat.Signed16, 1, 640, 640);
			return 65;
		}

		/// <summary>Reconstructs one 160-sample GSM frame through long-term, short-term, and postprocessing synthesis.</summary>
		private void DecodeBlock(LittleBitReader a_Reader, short[] a_Output, int a_OutputOffset)
		{
			var l_Current = _LogAreaIndex;
			_LogAreaRatios[l_Current, 0] = DecodeLogArea(a_Reader.Read(6), 13107, 32768);
			_LogAreaRatios[l_Current, 1] = DecodeLogArea(a_Reader.Read(6), 13107, 32768);
			_LogAreaRatios[l_Current, 2] = DecodeLogArea(a_Reader.Read(5), 13107, 20480);
			_LogAreaRatios[l_Current, 3] = DecodeLogArea(a_Reader.Read(5), 13107, 11264);
			_LogAreaRatios[l_Current, 4] = DecodeLogArea(a_Reader.Read(4), 19223, 8380);
			_LogAreaRatios[l_Current, 5] = DecodeLogArea(a_Reader.Read(4), 17476, 4608);
			_LogAreaRatios[l_Current, 6] = DecodeLogArea(a_Reader.Read(3), 31454, 3414);
			_LogAreaRatios[l_Current, 7] = DecodeLogArea(a_Reader.Read(3), 29708, 1808);
			var l_ReferenceOffset = 120;
			for (var l_SubFrame = 0; l_SubFrame < 4; l_SubFrame++)
			{
				var l_Lag = Math.Clamp(a_Reader.Read(7), 40, 120);
				var l_Gain = s_LongTermGain[a_Reader.Read(2)];
				var l_Offset = a_Reader.Read(2);
				for (var l_Index = 0; l_Index < 40; l_Index++) _Reference[l_ReferenceOffset + l_Index] = (short)GsmMultiply(l_Gain, _Reference[l_ReferenceOffset + l_Index - l_Lag]);
				var l_MaximumIndex = a_Reader.Read(6);
				for (var l_Index = 0; l_Index < 13; l_Index++) _Reference[l_ReferenceOffset + l_Offset + 3 * l_Index] += s_Dequant[l_MaximumIndex * 8 + a_Reader.Read(3)];
				l_ReferenceOffset += 40;
			}
			Array.Copy(_Reference, 160, _Reference, 0, 120);
			ShortTermSynthesis(a_Output, a_OutputOffset, _Reference, 120);
			_PostprocessMemory = Postprocess(a_Output, a_OutputOffset, _PostprocessMemory);
		}

		private void ShortTermSynthesis(short[] a_Output, int a_OutputOffset, short[] a_Source, int a_SourceOffset)
		{
			var l_Current = _LogAreaIndex; var l_Previous = l_Current ^ 1;
			for (var l_Index = 0; l_Index < 8; l_Index++) _ReflectionCoefficients[l_Index] = GetReflection((_LogAreaRatios[l_Previous, l_Index] >> 2) + (_LogAreaRatios[l_Previous, l_Index] >> 1) + (_LogAreaRatios[l_Current, l_Index] >> 2));
			FilterRange(a_Output, a_OutputOffset, a_Source, a_SourceOffset, 0, 13, _ReflectionCoefficients);
			for (var l_Index = 0; l_Index < 8; l_Index++) _ReflectionCoefficients[l_Index] = GetReflection((_LogAreaRatios[l_Previous, l_Index] >> 1) + (_LogAreaRatios[l_Current, l_Index] >> 1));
			FilterRange(a_Output, a_OutputOffset, a_Source, a_SourceOffset, 13, 27, _ReflectionCoefficients);
			for (var l_Index = 0; l_Index < 8; l_Index++) _ReflectionCoefficients[l_Index] = GetReflection((_LogAreaRatios[l_Previous, l_Index] >> 2) + (_LogAreaRatios[l_Current, l_Index] >> 1) + (_LogAreaRatios[l_Current, l_Index] >> 2));
			FilterRange(a_Output, a_OutputOffset, a_Source, a_SourceOffset, 27, 40, _ReflectionCoefficients);
			for (var l_Index = 0; l_Index < 8; l_Index++) _ReflectionCoefficients[l_Index] = GetReflection(_LogAreaRatios[l_Current, l_Index]);
			FilterRange(a_Output, a_OutputOffset, a_Source, a_SourceOffset, 40, 160, _ReflectionCoefficients);
			_LogAreaIndex ^= 1;
		}

		private void FilterRange(short[] a_Output, int a_OutputOffset, short[] a_Source, int a_SourceOffset, int a_Start, int a_End, int[] a_Filter)
		{
			for (var l_Sample = a_Start; l_Sample < a_End; l_Sample++)
			{
				var l_Value = (int)a_Source[a_SourceOffset + l_Sample];
				for (var l_Index = 7; l_Index >= 0; l_Index--)
				{
					l_Value -= GsmMultiply(a_Filter[l_Index], _FilterMemory[l_Index]);
					_FilterMemory[l_Index + 1] = _FilterMemory[l_Index] + GsmMultiply(a_Filter[l_Index], l_Value);
				}
				_FilterMemory[0] = l_Value;
				a_Output[a_OutputOffset + l_Sample] = unchecked((short)l_Value);
			}
		}

		private static int Postprocess(short[] a_Data, int a_Offset, int a_Memory)
		{
			for (var l_Index = 0; l_Index < 160; l_Index++)
			{
				a_Memory = Math.Clamp(a_Data[a_Offset + l_Index] + GsmMultiply(a_Memory, 28180), short.MinValue, short.MaxValue);
				a_Data[a_Offset + l_Index] = (short)(Math.Clamp(a_Memory * 2, short.MinValue, short.MaxValue) & ~7);
			}
			return a_Memory;
		}

		private static int DecodeLogArea(int a_Coded, int a_Factor, int a_Offset) => GsmMultiply((a_Coded << 10) - a_Offset, a_Factor) * 2;
		private static int GetReflection(int a_Filtered) { var l_Value = Math.Abs(a_Filtered); if (l_Value < 11059) l_Value <<= 1; else if (l_Value < 20070) l_Value += 11059; else l_Value = (l_Value >> 2) + 26112; return a_Filtered < 0 ? -l_Value : l_Value; }
		private static int GsmMultiply(int a_Left, int a_Right) => unchecked(a_Left * a_Right + 16384) >> 15;

		/// <summary>Reads Microsoft GSM fields least-significant bit first across byte boundaries.</summary>
		private sealed class LittleBitReader
		{
			private byte[] _Data; private int _Start; private int _BitLength; private int _BitPosition;
			public void Initialize(byte[] a_Data, int a_Start, int a_Length) { _Data = a_Data; _Start = a_Start; _BitLength = a_Length * 8; _BitPosition = 0; }
			public int Read(int a_Count)
			{
				if (a_Count < 0 || _BitPosition > _BitLength - a_Count) return 0;
				var l_Result = 0;
				for (var l_Bit = 0; l_Bit < a_Count; l_Bit++)
				{
					var l_SourceBit = _BitPosition + l_Bit;
					l_Result |= ((_Data[_Start + (l_SourceBit >> 3)] >> (l_SourceBit & 7)) & 1) << l_Bit;
				}
				_BitPosition += a_Count; return l_Result;
			}
		}
	}
}
