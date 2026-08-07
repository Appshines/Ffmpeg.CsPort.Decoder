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

namespace Ffmpeg.CsPort.Decoder.Codecs.Adpcm
{
	/// <summary>
	/// Ports the sample expansion primitives shared by multiple ADPCM packet layouts.
	/// </summary>
	internal static class AdpcmSampleExpansion
	{
		public static short Ima(AdpcmChannelStatus status, int nibble, int shift)
		{
			var step = AdpcmTables.Step[status.StepIndex];
			var stepIndex = status.StepIndex + AdpcmTables.Index[(uint)nibble];
			stepIndex = Math.Clamp(stepIndex, 0, 88);
			var sign = nibble & 8;
			var delta = nibble & 7;
			var difference = ((2 * delta + 1) * step) >> shift;
			var predictor = status.Predictor;
			if (sign != 0)
				predictor -= difference;
			else
				predictor += difference;
			status.Predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
			status.StepIndex = (short)stepIndex;
			return (short)status.Predictor;
		}

		public static short ImaAlp(AdpcmChannelStatus status, int nibble, int shift)
		{
			var step = AdpcmTables.Step[status.StepIndex];
			var stepIndex = Math.Clamp(status.StepIndex + AdpcmTables.Index[(uint)nibble], 0, 88);
			var difference = ((nibble & 7) * step) >> shift;
			var predictor = (nibble & 8) != 0 ? status.Predictor - difference : status.Predictor + difference;
			status.Predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
			status.StepIndex = (short)stepIndex;
			return (short)status.Predictor;
		}

		public static short ImaQuickTime(AdpcmChannelStatus status, int nibble)
		{
			var step = AdpcmTables.Step[status.StepIndex];
			var stepIndex = Math.Clamp(status.StepIndex + AdpcmTables.Index[nibble], 0, 88);
			var difference = step >> 3;
			if ((nibble & 4) != 0) difference += step;
			if ((nibble & 2) != 0) difference += step >> 1;
			if ((nibble & 1) != 0) difference += step >> 2;
			var predictor = (nibble & 8) != 0 ? status.Predictor - difference : status.Predictor + difference;
			status.Predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
			status.StepIndex = (short)stepIndex;
			return (short)status.Predictor;
		}

		public static short ImaWave(AdpcmChannelStatus status, int nibble, int bitsPerSample)
		{
			var shift = bitsPerSample - 1;
			var step = AdpcmTables.Step[status.StepIndex];
			var stepIndex = Math.Clamp(status.StepIndex + AdpcmTables.ImaIndexTables[bitsPerSample - 2][nibble], 0, 88);
			var sign = nibble & (1 << shift);
			var delta = nibble & ((1 << shift) - 1);
			var difference = step >> shift;
			for (var index = 0; index < shift; index++)
				difference += (step >> (shift - 1 - index)) * ((delta & (1 << index)) != 0 ? 1 : 0);
			var predictor = sign != 0 ? status.Predictor - difference : status.Predictor + difference;
			status.Predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
			status.StepIndex = (short)stepIndex;
			return (short)status.Predictor;
		}

		public static short Microsoft(AdpcmChannelStatus status, int nibble)
		{
			var predictor = (status.Sample1 * status.Coefficient1 + status.Sample2 * status.Coefficient2) / 64;
			predictor += ((nibble & 8) != 0 ? nibble - 16 : nibble) * status.Delta;
			status.Sample2 = status.Sample1;
			status.Sample1 = Math.Clamp(predictor, short.MinValue, short.MaxValue);
			status.Delta = AdpcmTables.Adaptation[nibble] * status.Delta >> 8;
			if (status.Delta < 16)
				status.Delta = 16;
			if (status.Delta > int.MaxValue / 768)
				status.Delta = int.MaxValue / 768;
			return (short)status.Sample1;
		}

		public static short Yamaha(AdpcmChannelStatus status, int nibble)
		{
			if (status.Step == 0)
			{
				status.Predictor = 0;
				status.Step = 127;
			}
			status.Predictor += status.Step * AdpcmTables.YamahaDifference[nibble] / 8;
			status.Predictor = Math.Clamp(status.Predictor, short.MinValue, short.MaxValue);
			status.Step = status.Step * AdpcmTables.YamahaIndexScale[nibble] >> 8;
			status.Step = Math.Clamp(status.Step, 127, 24576);
			return (short)status.Predictor;
		}

		public static short ImaEscape(AdpcmChannelStatus status, int nibble)
		{
			var step = AdpcmTables.Step[status.StepIndex];
			var stepIndex = Math.Clamp(status.StepIndex + AdpcmTables.Index[(uint)nibble], 0, 88);
			var difference = (nibble & 7) * step >> 2;
			var predictor = (nibble & 8) != 0 ? status.Predictor - difference : status.Predictor + difference;
			status.Predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
			status.StepIndex = (short)stepIndex;
			return (short)status.Predictor;
		}

		public static short ImaCunning(AdpcmChannelStatus status, int nibble)
		{
			nibble = (nibble & 8) != 0 ? nibble - 16 : nibble;
			var step = AdpcmTables.CunningStep[status.StepIndex];
			var stepIndex = Math.Clamp(status.StepIndex + AdpcmTables.CunningIndex[Math.Abs(nibble)], 0, 60);
			status.Predictor = Math.Clamp(status.Predictor + step * nibble, short.MinValue, short.MaxValue);
			status.StepIndex = (short)stepIndex;
			return (short)status.Predictor;
		}

		public static short ImaOki(AdpcmChannelStatus status, int nibble)
		{
			var step = AdpcmTables.OkiStep[status.StepIndex];
			var stepIndex = Math.Clamp(status.StepIndex + AdpcmTables.Index[(uint)nibble], 0, 48);
			var difference = ((2 * (nibble & 7) + 1) * step) >> 3;
			var predictor = (nibble & 8) != 0 ? status.Predictor - difference : status.Predictor + difference;
			status.Predictor = Math.Clamp(predictor, -2048, 2047);
			status.StepIndex = (short)stepIndex;
			return (short)(status.Predictor * 16);
		}

		public static short ImaMtf(AdpcmChannelStatus status, int nibble)
		{
			var step = AdpcmTables.Step[status.StepIndex];
			var delta = step * (2 * nibble - 15);
			var predictor = status.Predictor + delta;
			var stepIndex = status.StepIndex + AdpcmTables.MtfIndex[nibble];
			status.Predictor = Math.Clamp(predictor >> 4, short.MinValue, short.MaxValue);
			status.StepIndex = (short)Math.Clamp(stepIndex, 0, 88);
			return (short)status.Predictor;
		}

		public static short Creative(AdpcmChannelStatus status, int nibble)
		{
			var difference = ((2 * (nibble & 7) + 1) * status.Step) >> 3;
			status.Predictor = (status.Predictor * 254 >> 8) + ((nibble & 8) != 0 ? -difference : difference);
			status.Predictor = Math.Clamp(status.Predictor, short.MinValue, short.MaxValue);
			var newStep = AdpcmTables.Adaptation[nibble & 7] * status.Step >> 8;
			status.Step = Math.Clamp(newStep, 511, 32767);
			return (short)status.Predictor;
		}

		public static short SoundBlasterPro(AdpcmChannelStatus status, int nibble, int size, int shift)
		{
			var sign = nibble & (1 << (size - 1));
			var delta = nibble & ((1 << (size - 1)) - 1);
			var difference = delta << (7 + status.Step + shift);
			status.Predictor = Math.Clamp(status.Predictor + (sign != 0 ? -difference : difference), -16384, 16256);
			if (delta >= 2 * size - 3 && status.Step < 3)
				status.Step++;
			else if (delta == 0 && status.Step > 0)
				status.Step--;
			return (short)status.Predictor;
		}

		public static short Argo(AdpcmChannelStatus status, int nibble, int shift, int flag)
		{
			var signedNibble = (nibble & 8) != 0 ? nibble - 16 : nibble;
			var sample = signedNibble * (1 << shift);
			if (flag != 0)
				sample += 8 * status.Sample1 - 4 * status.Sample2;
			else
				sample += 4 * status.Sample1;
			sample = Math.Clamp(sample >> 2, short.MinValue, short.MaxValue);
			status.Sample2 = status.Sample1;
			status.Sample1 = sample;
			return (short)sample;
		}

		public static short Circus(AdpcmChannelStatus status, int value)
		{
			var code = unchecked((sbyte)value);
			var sample = status.Predictor + code * (1 << status.Step);
			if (code == 0)
				status.Step--;
			else if (code == 127 || code == -128)
				status.Step++;
			status.Step = Math.Clamp(status.Step, 0, 8);
			status.Predictor = Math.Clamp(sample, short.MinValue, short.MaxValue);
			return (short)status.Predictor;
		}

		public static short Zork(AdpcmChannelStatus status, int value)
		{
			var lookup = (uint)AdpcmTables.Step[status.StepIndex];
			uint sample = 0;
			if ((value & 0x40) != 0) sample += lookup;
			if ((value & 0x20) != 0) sample += lookup >> 1;
			if ((value & 0x10) != 0) sample += lookup >> 2;
			if ((value & 0x08) != 0) sample += lookup >> 3;
			if ((value & 0x04) != 0) sample += lookup >> 4;
			if ((value & 0x02) != 0) sample += lookup >> 5;
			if ((value & 0x01) != 0) sample += lookup >> 6;
			var signedSample = (value & 0x80) != 0 ? -(int)sample : (int)sample;
			signedSample = Math.Clamp(signedSample + status.Predictor, short.MinValue, short.MaxValue);
			status.StepIndex = (short)Math.Clamp(status.StepIndex + AdpcmTables.ZorkIndex[(value >> 4) & 7], 0, 88);
			status.Predictor = signedSample;
			return (short)signedSample;
		}

		public static short Agm(AdpcmChannelStatus status, int nibble)
		{
			var delta = nibble & 7;
			var step = status.Step;
			var add = (delta * 2 + 1) * step;
			if (add < 0)
				add += 7;
			var predictor = (nibble & 8) == 0
				? Math.Clamp(status.Predictor + (add >> 3), -32767, 32767)
				: Math.Clamp(status.Predictor - (add >> 3), -32767, 32767);
			switch (delta)
			{
				case 7: step *= 0x99; break;
				case 6:
					status.Step = Math.Clamp(status.Step * 2, 127, 24576);
					status.Predictor = predictor;
					return (short)predictor;
				case 5: step *= 0x66; break;
				case 4: step *= 0x4d; break;
				default: step *= 0x39; break;
			}
			if (step < 0)
				step += 0x3f;
			status.Step = Math.Clamp(step >> 6, 127, 24576);
			status.Predictor = predictor;
			return (short)predictor;
		}

		public static short Sanyo3(AdpcmChannelStatus status, int bits)
		{
			var sign = bits & 4;
			var delta = sign != 0 ? 4 - (bits & 3) : bits;
			int add;
			switch (delta)
			{
				case 0: add = 0; status.Step = 3 * status.Step >> 2; break;
				case 1: add = status.Step; status.Step = (4 * status.Step - (status.Step >> 1)) >> 2; break;
				case 2: add = 2 * status.Step; status.Step = ((status.Step >> 1) + add) >> 1; break;
				case 3: add = 4 * status.Step - (status.Step >> 1); status.Step *= 2; break;
				default: add = 11 * status.Step >> 1; status.Step *= 3; break;
			}
			if (sign != 0) add = -add;
			status.Predictor = Math.Clamp(status.Predictor + add, short.MinValue, short.MaxValue);
			status.Step = Math.Clamp(status.Step, 1, 7281);
			return (short)status.Predictor;
		}

		public static short Sanyo4(AdpcmChannelStatus status, int bits)
		{
			var sign = bits & 8;
			var delta = sign != 0 ? 8 - (bits & 7) : bits;
			int add;
			switch (delta)
			{
				case 0: add = 0; status.Step = 3 * status.Step >> 2; break;
				case 1: add = status.Step; status.Step = 3 * status.Step >> 2; break;
				case 2: add = 2 * status.Step; break;
				case 3: add = 3 * status.Step; break;
				case 4: add = 4 * status.Step; break;
				case 5: add = 11 * status.Step >> 1; status.Step += status.Step >> 2; break;
				case 6: add = 15 * status.Step >> 1; status.Step *= 2; break;
				case 7:
					add = (sign != 0 ? 19 : 21) * status.Step >> 1;
					status.Step = (status.Step >> 1) + 2 * status.Step;
					break;
				default: add = 25 * status.Step >> 1; status.Step *= 5; break;
			}
			if (sign != 0) add = -add;
			status.Predictor = Math.Clamp(status.Predictor + add, short.MinValue, short.MaxValue);
			status.Step = Math.Clamp(status.Step, 1, 2621);
			return (short)status.Predictor;
		}

		public static short Sanyo5(AdpcmChannelStatus status, int bits)
		{
			var sign = bits & 16;
			var delta = sign != 0 ? 16 - (bits & 15) : bits;
			var add = delta * status.Step;
			switch (delta)
			{
				case 0: status.Step += (status.Step >> 2) - (status.Step >> 1); break;
				case 1:
				case 2:
				case 3: status.Step += (status.Step >> 3) - (status.Step >> 2); break;
				case 4:
				case 5: status.Step += (status.Step >> 4) - (status.Step >> 3); break;
				case 6: break;
				case 7: status.Step += status.Step >> 3; break;
				case 8: status.Step += status.Step >> 2; break;
				case 9: status.Step += status.Step >> 1; break;
				case 10: status.Step = 2 * status.Step - (status.Step >> 3); break;
				case 11: status.Step = 2 * status.Step + (status.Step >> 3); break;
				case 12: status.Step = 2 * status.Step + (status.Step >> 1) - (status.Step >> 3); break;
				case 13: status.Step = 3 * status.Step - (status.Step >> 2); break;
				case 14: status.Step *= 3; break;
				default: status.Step = 7 * status.Step >> 1; break;
			}
			if (sign != 0) add = -add;
			status.Predictor = Math.Clamp(status.Predictor + add, short.MinValue, short.MaxValue);
			status.Step = Math.Clamp(status.Step, 1, 1024);
			return (short)status.Predictor;
		}

		public static short Mtaf(AdpcmChannelStatus status, int nibble)
		{
			status.Predictor += AdpcmTables.MtafStep[status.Step * 16 + nibble];
			status.Predictor = Math.Clamp(status.Predictor, short.MinValue, short.MaxValue);
			status.Step += AdpcmTables.Index[nibble];
			status.Step = Math.Clamp(status.Step, 0, 31);
			return (short)status.Predictor;
		}
	}
}
