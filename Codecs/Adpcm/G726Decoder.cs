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

namespace Ffmpeg.CsPort.Decoder.Codecs.Adpcm
{
	/// <summary>
	/// Ports FFmpeg's stateful G.726 little-endian ADPCM decoder for the 2-, 3-, 4-, and 5-bit AU coding modes.
	/// </summary>
	public sealed class G726Decoder
	{
		private static readonly short[][] s_InverseQuant =
		{
			new short[] { 116, 365, 365, 116 },
			new short[] { short.MinValue, 135, 273, 373, 373, 273, 135, short.MinValue },
			new short[] { short.MinValue, 4, 135, 213, 273, 323, 373, 425, 425, 373, 323, 273, 213, 135, 4, short.MinValue },
			new short[] { short.MinValue, -66, 28, 104, 169, 224, 274, 318, 358, 395, 429, 459, 488, 514, 539, 566,
				566, 539, 514, 488, 459, 429, 395, 358, 318, 274, 224, 169, 104, 28, -66, short.MinValue }
		};
		private static readonly short[][] s_Adaptation =
		{
			new short[] { -22, 439, 439, -22 }, new short[] { -4, 30, 137, 582, 582, 137, 30, -4 },
			new short[] { -12, 18, 41, 64, 112, 198, 355, 1122, 1122, 355, 198, 112, 64, 41, 18, -12 },
			new short[] { 14, 14, 24, 39, 40, 41, 58, 100, 141, 179, 219, 280, 358, 440, 529, 696,
				696, 529, 440, 358, 280, 219, 179, 141, 100, 58, 41, 40, 39, 24, 14, 14 }
		};
		private static readonly byte[][] s_SpeedControl =
		{
			new byte[] { 0, 7, 7, 0 }, new byte[] { 0, 1, 2, 7, 7, 2, 1, 0 },
			new byte[] { 0, 0, 0, 1, 1, 1, 3, 7, 7, 3, 1, 1, 1, 0, 0, 0 },
			new byte[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 6, 6, 6, 5, 4, 3, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 }
		};

		private readonly int _CodeSize;
		private readonly Float11[] _Reconstructed = { new Float11(), new Float11() };
		private readonly Float11[] _Differences = { new Float11(), new Float11(), new Float11(), new Float11(), new Float11(), new Float11() };
		private readonly int[] _PoleCoefficients = new int[2];
		private readonly int[] _ZeroCoefficients = new int[6];
		private readonly int[] _Signs = { 1, 1 };
		private readonly Float11 _TemporaryFloat = new Float11();
		private int _FastScale;
		private int _UnlockedScale = 544;
		private int _LockedScale = 34816;
		private int _StepSize = 544;
		private int _ShortAverage;
		private int _LongAverage;
		private int _ToneDetect;
		private int _SignalEstimate;
		private int _ZeroEstimate;

		private G726Decoder(int a_CodeSize)
		{
			_CodeSize = a_CodeSize;
			for (var l_Index = 0; l_Index < _Reconstructed.Length; l_Index++) _Reconstructed[l_Index].Mantissa = 32;
			for (var l_Index = 0; l_Index < _Differences.Length; l_Index++) _Differences[l_Index].Mantissa = 32;
		}

		public AudioSampleFormat SampleFormat => AudioSampleFormat.Signed16;

		public static int Initialize(int a_Channels, int a_BitsPerCodedSample, out G726Decoder a_Decoder)
		{
			a_Decoder = null;
			if (a_Channels != 1 || a_BitsPerCodedSample < 2 || a_BitsPerCodedSample > 5) return FfmpegError.InvalidArgument;
			a_Decoder = new G726Decoder(a_BitsPerCodedSample);
			return 0;
		}

		/// <summary>Reads right-justified codes least-significant bit first and preserves predictor state across AU packets.</summary>
		public int DecodeFrame(byte[] a_Packet, int a_Offset, int a_Length, Span<byte> a_Output, out AudioFrameInfo a_Frame)
		{
			a_Frame = default;
			if (a_Packet == null || a_Offset < 0 || a_Length < 0 || a_Length > a_Packet.Length - a_Offset) return FfmpegError.InvalidArgument;
			var l_SampleCount = a_Length * 8 / _CodeSize;
			if (a_Output.Length < l_SampleCount * 2) return FfmpegError.InvalidArgument;
			var l_BitOffset = 0;
			for (var l_Sample = 0; l_Sample < l_SampleCount; l_Sample++)
			{
				var l_ByteOffset = a_Offset + (l_BitOffset >> 3);
				var l_Shift = l_BitOffset & 7;
				var l_Value = a_Packet[l_ByteOffset] >> l_Shift;
				if (l_Shift + _CodeSize > 8) l_Value |= a_Packet[l_ByteOffset + 1] << (8 - l_Shift);
				var l_Code = l_Value & ((1 << _CodeSize) - 1);
				BinaryPrimitives.WriteInt16LittleEndian(a_Output.Slice(l_Sample * 2, 2), DecodeSample(l_Code));
				l_BitOffset += _CodeSize;
			}
			a_Frame = new AudioFrameInfo(l_SampleCount, 1, AudioSampleFormat.Signed16, 1, l_SampleCount * 2, l_SampleCount * 2);
			return a_Length;
		}

		/// <summary>Applies the G.726 inverse quantizer, adaptive pole/zero predictor, tone transition, and scale updates in FFmpeg order.</summary>
		private short DecodeSample(int a_Code)
		{
			var l_Table = _CodeSize - 2;
			var l_Difference = InverseQuantize(s_InverseQuant[l_Table][a_Code]);
			var l_LinearScale = _LockedScale >> 15;
			var l_Fraction = _LockedScale >> 10 & 31;
			var l_Threshold = l_LinearScale > 9 ? 31 << 10 : (32 + l_Fraction) << l_LinearScale;
			var l_Transition = _ToneDetect == 1 && l_Difference > 3 * l_Threshold >> 2;
			var l_SignBit = a_Code >> (_CodeSize - 1);
			if (l_SignBit != 0) l_Difference = -l_Difference;
			var l_Reconstructed = unchecked((short)(_SignalEstimate + l_Difference));
			var l_PoleSign = _ZeroEstimate + l_Difference != 0 ? Sign(_ZeroEstimate + l_Difference) : 0;
			var l_DifferenceSign = l_Difference != 0 ? Sign(l_Difference) : 0;
			if (l_Transition)
			{
				Array.Clear(_PoleCoefficients); Array.Clear(_ZeroCoefficients);
			} else
			{
				var l_FirstPoleAdjustment = Math.Clamp(-_PoleCoefficients[0] * _Signs[0] * l_PoleSign >> 5, -256, 255);
				_PoleCoefficients[1] += 128 * l_PoleSign * _Signs[1] + l_FirstPoleAdjustment - (_PoleCoefficients[1] >> 7);
				_PoleCoefficients[1] = Math.Clamp(_PoleCoefficients[1], -12288, 12288);
				_PoleCoefficients[0] += 192 * l_PoleSign * _Signs[0] - (_PoleCoefficients[0] >> 8);
				_PoleCoefficients[0] = Math.Clamp(_PoleCoefficients[0], -(15360 - _PoleCoefficients[1]), 15360 - _PoleCoefficients[1]);
				for (var l_Index = 0; l_Index < 6; l_Index++)
					_ZeroCoefficients[l_Index] += 128 * l_DifferenceSign * Sign(-_Differences[l_Index].Sign) - (_ZeroCoefficients[l_Index] >> 8);
			}
			_Signs[1] = _Signs[0]; _Signs[0] = l_PoleSign != 0 ? l_PoleSign : 1;
			_Reconstructed[1].CopyFrom(_Reconstructed[0]); IntegerToFloat(l_Reconstructed, _Reconstructed[0]);
			for (var l_Index = 5; l_Index > 0; l_Index--) _Differences[l_Index].CopyFrom(_Differences[l_Index - 1]);
			IntegerToFloat(l_Difference, _Differences[0]); _Differences[0].Sign = l_SignBit;
			_ToneDetect = _PoleCoefficients[1] < -11776 ? 1 : 0;
			_ShortAverage += (s_SpeedControl[l_Table][a_Code] << 4) + (-_ShortAverage >> 5);
			_LongAverage += (s_SpeedControl[l_Table][a_Code] << 4) + (-_LongAverage >> 7);
			if (l_Transition) _FastScale = 256;
			else
			{
				_FastScale += -_FastScale >> 4;
				if (_StepSize <= 1535 || _ToneDetect != 0 || Math.Abs((_ShortAverage << 2) - _LongAverage) >= (_LongAverage >> 3)) _FastScale += 32;
			}
			_UnlockedScale = Math.Clamp(_StepSize + s_Adaptation[l_Table][a_Code] + (-_StepSize >> 5), 544, 5120);
			_LockedScale += _UnlockedScale + (-_LockedScale >> 6);
			var l_Adaptation = _FastScale >= 256 ? 64 : _FastScale >> 2;
			_StepSize = (_LockedScale + (_UnlockedScale - (_LockedScale >> 6)) * l_Adaptation) >> 6;
			_SignalEstimate = 0;
			for (var l_Index = 0; l_Index < 6; l_Index++) _SignalEstimate += Multiply(IntegerToFloat(_ZeroCoefficients[l_Index] >> 2, _TemporaryFloat), _Differences[l_Index]);
			_ZeroEstimate = _SignalEstimate >> 1;
			for (var l_Index = 0; l_Index < 2; l_Index++) _SignalEstimate += Multiply(IntegerToFloat(_PoleCoefficients[l_Index] >> 2, _TemporaryFloat), _Reconstructed[l_Index]);
			_SignalEstimate >>= 1;
			return unchecked((short)Math.Clamp(l_Reconstructed * 4, -65535, 65535));
		}

		private int InverseQuantize(short a_Value)
		{
			var l_Quantized = a_Value + (_StepSize >> 2);
			var l_Exponent = l_Quantized >> 7 & 15;
			var l_Mantissa = 128 + (l_Quantized & 127);
			return l_Quantized < 0 ? 0 : l_Mantissa << l_Exponent >> 7;
		}

		private static Float11 IntegerToFloat(int a_Value, Float11 a_Result)
		{
			a_Result.Sign = a_Value < 0 ? 1 : 0;
			if (a_Result.Sign != 0) a_Value = -a_Value;
			var l_Log = 0; var l_Copy = a_Value;
			while ((l_Copy >>= 1) != 0) l_Log++;
			a_Result.Exponent = l_Log + (a_Value != 0 ? 1 : 0);
			a_Result.Mantissa = a_Value != 0 ? a_Value << 6 >> a_Result.Exponent : 32;
			return a_Result;
		}

		private static int Multiply(Float11 a_Left, Float11 a_Right)
		{
			var l_Exponent = a_Left.Exponent + a_Right.Exponent;
			var l_Result = (a_Left.Mantissa * a_Right.Mantissa + 48) >> 4;
			l_Result = l_Exponent > 19 ? l_Result << (l_Exponent - 19) : l_Result >> (19 - l_Exponent);
			return unchecked((short)((a_Left.Sign ^ a_Right.Sign) != 0 ? -l_Result : l_Result));
		}

		private static int Sign(int a_Value) => a_Value < 0 ? -1 : 1;

		private sealed class Float11
		{
			public int Sign; public int Exponent; public int Mantissa;
			public void CopyFrom(Float11 a_Source) { Sign = a_Source.Sign; Exponent = a_Source.Exponent; Mantissa = a_Source.Mantissa; }
		}
	}
}
