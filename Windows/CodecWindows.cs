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

namespace Ffmpeg.CsPort.Decoder.Windows
{
	/// <summary>
	/// Generates FFmpeg sine and Kaiser-Bessel-derived windows used by audio transforms and synthesis filters.
	/// </summary>
	internal static class CodecWindows
	{
		public const int MaximumKaiserBesselWindowSize = 1024;
		private static readonly float[][] s_SineWindows = new float[14][];
		private static readonly object s_SineWindowLock = new object();

		public static void InitializeSineWindow(float[] window, int length)
		{
			if (window == null || length < 0 || window.Length < length)
			{
				throw new ArgumentException("Sine window buffer is smaller than the requested FFmpeg window.", nameof(window));
			}

			for (var index = 0; index < length; index++)
			{
				window[index] = MathF.Sin((float)((index + 0.5) * (Math.PI / (2.0 * length))));
			}
		}

		public static float[] GetSineWindow(int index)
		{
			if (index < 5 || index >= s_SineWindows.Length)
			{
				throw new ArgumentOutOfRangeException(nameof(index));
			}

			var window = s_SineWindows[index];
			if (window != null)
			{
				return window;
			}

			lock (s_SineWindowLock)
			{
				window = s_SineWindows[index];
				if (window == null)
				{
					window = new float[1 << index];
					InitializeSineWindow(window, window.Length);
					s_SineWindows[index] = window;
				}
			}

			return window;
		}

		public static void InitializeKaiserBesselWindow(float[] window, float alpha, int length)
		{
			InitializeKaiserBesselWindow(window, null, alpha, length);
		}

		public static void InitializeKaiserBesselWindow(int[] window, float alpha, int length)
		{
			InitializeKaiserBesselWindow(null, window, alpha, length);
		}

		/// <summary>
		/// Preserves FFmpeg's accumulation order, symmetric weighting, and final square-root conversion for both output formats.
		/// </summary>
		private static void InitializeKaiserBesselWindow(float[] floatWindow, int[] integerWindow, float alpha, int length)
		{
			if (length > MaximumKaiserBesselWindowSize || length < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(length));
			}
			if (floatWindow != null && floatWindow.Length < length || integerWindow != null && integerWindow.Length < length)
			{
				throw new ArgumentException("Kaiser-Bessel window buffer is smaller than the requested FFmpeg window.");
			}

			var sum = 0.0;
			var scale = 0.0;
			var temporary = new double[MaximumKaiserBesselWindowSize / 2 + 1];
			var alphaSquared = 4 * (alpha * Math.PI / length) * (alpha * Math.PI / length);
			var index = 0;
			for (; index <= length / 2; index++)
			{
				var value = index * (length - index) * alphaSquared;
				temporary[index] = FfmpegMath.BesselI0(Math.Sqrt(value));
				scale += temporary[index] * (1 + (index != 0 && index < length / 2 ? 1 : 0));
			}
			scale = 1.0 / (scale + 1);

			for (index = 0; index <= length / 2; index++)
			{
				sum += temporary[index];
				var value = Math.Sqrt(sum * scale);
				if (floatWindow != null)
				{
					floatWindow[index] = (float)value;
				} else
				{
					integerWindow[index] = unchecked((int)Math.Round(2147483647 * value, MidpointRounding.ToEven));
				}
			}
			for (; index < length; index++)
			{
				sum += temporary[length - index];
				var value = Math.Sqrt(sum * scale);
				if (floatWindow != null)
				{
					floatWindow[index] = (float)value;
				} else
				{
					integerWindow[index] = unchecked((int)Math.Round(2147483647 * value, MidpointRounding.ToEven));
				}
			}
		}
	}
}
