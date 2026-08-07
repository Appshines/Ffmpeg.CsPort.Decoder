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
using System.Globalization;
using System.Text;
using Ffmpeg.CsPort.Decoder.Infrastructure;
using Ffmpeg.CsPort.Decoder.Mathematics;

namespace Ffmpeg.CsPort.Decoder.Channels
{
	/// <summary>
	/// Ports FFmpeg's channel masks, standard layouts, parsing, indexing, validation, and retyping behavior.
	/// </summary>
	internal static class ChannelLayouts
	{
		public const ulong FrontLeft = 1UL << (int)AvChannel.FrontLeft;
		public const ulong FrontRight = 1UL << (int)AvChannel.FrontRight;
		public const ulong FrontCenter = 1UL << (int)AvChannel.FrontCenter;
		public const ulong LowFrequency = 1UL << (int)AvChannel.LowFrequency;
		public const ulong BackLeft = 1UL << (int)AvChannel.BackLeft;
		public const ulong BackRight = 1UL << (int)AvChannel.BackRight;
		public const ulong FrontLeftOfCenter = 1UL << (int)AvChannel.FrontLeftOfCenter;
		public const ulong FrontRightOfCenter = 1UL << (int)AvChannel.FrontRightOfCenter;
		public const ulong BackCenter = 1UL << (int)AvChannel.BackCenter;
		public const ulong SideLeft = 1UL << (int)AvChannel.SideLeft;
		public const ulong SideRight = 1UL << (int)AvChannel.SideRight;
		public const ulong TopCenter = 1UL << (int)AvChannel.TopCenter;
		public const ulong TopFrontLeft = 1UL << (int)AvChannel.TopFrontLeft;
		public const ulong TopFrontCenter = 1UL << (int)AvChannel.TopFrontCenter;
		public const ulong TopFrontRight = 1UL << (int)AvChannel.TopFrontRight;
		public const ulong TopBackLeft = 1UL << (int)AvChannel.TopBackLeft;
		public const ulong TopBackCenter = 1UL << (int)AvChannel.TopBackCenter;
		public const ulong TopBackRight = 1UL << (int)AvChannel.TopBackRight;
		public const ulong StereoLeft = 1UL << (int)AvChannel.StereoLeft;
		public const ulong StereoRight = 1UL << (int)AvChannel.StereoRight;
		public const ulong WideLeft = 1UL << (int)AvChannel.WideLeft;
		public const ulong WideRight = 1UL << (int)AvChannel.WideRight;
		public const ulong SurroundDirectLeft = 1UL << (int)AvChannel.SurroundDirectLeft;
		public const ulong SurroundDirectRight = 1UL << (int)AvChannel.SurroundDirectRight;
		public const ulong LowFrequency2 = 1UL << (int)AvChannel.LowFrequency2;
		public const ulong TopSideLeft = 1UL << (int)AvChannel.TopSideLeft;
		public const ulong TopSideRight = 1UL << (int)AvChannel.TopSideRight;
		public const ulong BottomFrontCenter = 1UL << (int)AvChannel.BottomFrontCenter;
		public const ulong BottomFrontLeft = 1UL << (int)AvChannel.BottomFrontLeft;
		public const ulong BottomFrontRight = 1UL << (int)AvChannel.BottomFrontRight;
		public const ulong SideSurroundLeft = 1UL << (int)AvChannel.SideSurroundLeft;
		public const ulong SideSurroundRight = 1UL << (int)AvChannel.SideSurroundRight;
		public const ulong TopSurroundLeft = 1UL << (int)AvChannel.TopSurroundLeft;
		public const ulong TopSurroundRight = 1UL << (int)AvChannel.TopSurroundRight;
		public const ulong BinauralLeft = 1UL << (int)AvChannel.BinauralLeft;
		public const ulong BinauralRight = 1UL << (int)AvChannel.BinauralRight;

		public const ulong Mono = FrontCenter;
		public const ulong Stereo = FrontLeft | FrontRight;
		public const ulong TwoPointOne = Stereo | LowFrequency;
		public const ulong TwoOne = Stereo | BackCenter;
		public const ulong Surround = Stereo | FrontCenter;
		public const ulong ThreePointOne = Surround | LowFrequency;
		public const ulong FourPointZero = Surround | BackCenter;
		public const ulong FourPointOne = FourPointZero | LowFrequency;
		public const ulong TwoTwo = Stereo | SideLeft | SideRight;
		public const ulong Quad = Stereo | BackLeft | BackRight;
		public const ulong FivePointZero = Surround | SideLeft | SideRight;
		public const ulong FivePointOne = FivePointZero | LowFrequency;
		public const ulong FivePointZeroBack = Surround | BackLeft | BackRight;
		public const ulong FivePointOneBack = FivePointZeroBack | LowFrequency;
		public const ulong SixPointZero = FivePointZero | BackCenter;
		public const ulong SixPointZeroFront = TwoTwo | FrontLeftOfCenter | FrontRightOfCenter;
		public const ulong Hexagonal = FivePointZeroBack | BackCenter;
		public const ulong ThreePointOnePointTwo = ThreePointOne | TopFrontLeft | TopFrontRight;
		public const ulong SixPointOne = FivePointOne | BackCenter;
		public const ulong SixPointOneBack = FivePointOneBack | BackCenter;
		public const ulong SixPointOneFront = SixPointZeroFront | LowFrequency;
		public const ulong SevenPointZero = FivePointZero | BackLeft | BackRight;
		public const ulong SevenPointZeroFront = FivePointZero | FrontLeftOfCenter | FrontRightOfCenter;
		public const ulong SevenPointOne = FivePointOne | BackLeft | BackRight;
		public const ulong SevenPointOneWide = FivePointOne | FrontLeftOfCenter | FrontRightOfCenter;
		public const ulong SevenPointOneWideBack = FivePointOneBack | FrontLeftOfCenter | FrontRightOfCenter;
		public const ulong FivePointOnePointTwo = FivePointOne | TopFrontLeft | TopFrontRight;
		public const ulong FivePointOnePointTwoBack = FivePointOneBack | TopFrontLeft | TopFrontRight;
		public const ulong Octagonal = FivePointZero | BackLeft | BackCenter | BackRight;
		public const ulong Cube = Quad | TopFrontLeft | TopFrontRight | TopBackLeft | TopBackRight;
		public const ulong FivePointOnePointFourBack = FivePointOnePointTwo | TopBackLeft | TopBackRight;
		public const ulong SevenPointOnePointTwo = SevenPointOne | TopFrontLeft | TopFrontRight;
		public const ulong SevenPointOnePointFourBack = SevenPointOnePointTwo | TopBackLeft | TopBackRight;
		public const ulong SevenPointTwoPointThree = SevenPointOnePointTwo | TopBackCenter | LowFrequency2;
		public const ulong NinePointOnePointFourBack = SevenPointOnePointFourBack | FrontLeftOfCenter | FrontRightOfCenter;
		public const ulong NinePointOnePointSix = NinePointOnePointFourBack | TopSideLeft | TopSideRight;
		public const ulong Hexadecagonal = Octagonal | WideLeft | WideRight | TopBackLeft | TopBackRight | TopBackCenter | TopFrontCenter | TopFrontLeft | TopFrontRight;
		public const ulong Binaural = BinauralLeft | BinauralRight;
		public const ulong StereoDownmix = StereoLeft | StereoRight;
		public const ulong TwentyTwoPointTwo = NinePointOnePointSix | BackCenter | LowFrequency2 | TopFrontCenter | TopCenter | TopBackCenter |
			BottomFrontCenter | BottomFrontLeft | BottomFrontRight;

		private static readonly NamedLayout[] s_StandardLayouts =
		{
			new NamedLayout("mono", Native(1, Mono)),
			new NamedLayout("stereo", Native(2, Stereo)),
			new NamedLayout("2.1", Native(3, TwoPointOne)),
			new NamedLayout("3.0", Native(3, Surround)),
			new NamedLayout("3.0(back)", Native(3, TwoOne)),
			new NamedLayout("4.0", Native(4, FourPointZero)),
			new NamedLayout("quad", Native(4, Quad)),
			new NamedLayout("quad(side)", Native(4, TwoTwo)),
			new NamedLayout("3.1", Native(4, ThreePointOne)),
			new NamedLayout("5.0", Native(5, FivePointZeroBack)),
			new NamedLayout("5.0(side)", Native(5, FivePointZero)),
			new NamedLayout("4.1", Native(5, FourPointOne)),
			new NamedLayout("5.1", Native(6, FivePointOneBack)),
			new NamedLayout("5.1(side)", Native(6, FivePointOne)),
			new NamedLayout("6.0", Native(6, SixPointZero)),
			new NamedLayout("6.0(front)", Native(6, SixPointZeroFront)),
			new NamedLayout("3.1.2", Native(6, ThreePointOnePointTwo)),
			new NamedLayout("hexagonal", Native(6, Hexagonal)),
			new NamedLayout("6.1", Native(7, SixPointOne)),
			new NamedLayout("6.1(back)", Native(7, SixPointOneBack)),
			new NamedLayout("6.1(front)", Native(7, SixPointOneFront)),
			new NamedLayout("7.0", Native(7, SevenPointZero)),
			new NamedLayout("7.0(front)", Native(7, SevenPointZeroFront)),
			new NamedLayout("7.1", Native(8, SevenPointOne)),
			new NamedLayout("7.1(wide)", Native(8, SevenPointOneWideBack)),
			new NamedLayout("7.1(wide-side)", Native(8, SevenPointOneWide)),
			new NamedLayout("5.1.2", Native(8, FivePointOnePointTwo)),
			new NamedLayout("5.1.2(back)", Native(8, FivePointOnePointTwoBack)),
			new NamedLayout("octagonal", Native(8, Octagonal)),
			new NamedLayout("cube", Native(8, Cube)),
			new NamedLayout("5.1.4", Native(10, FivePointOnePointFourBack)),
			new NamedLayout("7.1.2", Native(10, SevenPointOnePointTwo)),
			new NamedLayout("7.1.4", Native(12, SevenPointOnePointFourBack)),
			new NamedLayout("7.2.3", Native(12, SevenPointTwoPointThree)),
			new NamedLayout("9.1.4", Native(14, NinePointOnePointFourBack)),
			new NamedLayout("9.1.6", Native(16, NinePointOnePointSix)),
			new NamedLayout("hexadecagonal", Native(16, Hexadecagonal)),
			new NamedLayout("binaural", Native(2, Binaural)),
			new NamedLayout("downmix", Native(2, StereoDownmix)),
			new NamedLayout("22.2", Native(24, TwentyTwoPointTwo))
		};

		public static int CustomInitialize(out AvChannelLayout layout, int channelCount)
		{
			layout = default;
			if (channelCount <= 0)
			{
				return FfmpegError.InvalidArgument;
			}

			var map = new AvChannelCustom[channelCount];
			for (var index = 0; index < channelCount; index++)
			{
				map[index].Id = AvChannel.Unknown;
			}
			layout.Order = AvChannelOrder.Custom;
			layout.ChannelCount = channelCount;
			layout.Map = map;
			return 0;
		}

		public static int FromMask(out AvChannelLayout layout, ulong mask)
		{
			layout = default;
			if (mask == 0)
			{
				return FfmpegError.InvalidArgument;
			}
			layout = Native(FfmpegMath.PopCount(mask), mask);
			return 0;
		}

		/// <summary>
		/// Parses FFmpeg standard names, ambisonic forms, channel lists, masks, and explicit channel-count forms in source order.
		/// </summary>
		public static int FromString(out AvChannelLayout layout, string value)
		{
			layout = default;
			if (value == null)
			{
				return FfmpegError.InvalidArgument;
			}
			for (var index = 0; index < s_StandardLayouts.Length; index++)
			{
				if (value == s_StandardLayouts[index].Name)
				{
					layout = s_StandardLayouts[index].Layout;
					return 0;
				}
			}

			if (value.StartsWith("ambisonic ", StringComparison.Ordinal))
			{
				var separator = value.IndexOf('+', 10);
				var orderText = separator >= 0 ? value.Substring(10, separator - 10) : value.Substring(10);
				if (!int.TryParse(orderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ||
					order < 0 || order + 1 > int.MaxValue / (order + 1))
				{
					return FfmpegError.InvalidArgument;
				}

				layout.Order = AvChannelOrder.Ambisonic;
				layout.ChannelCount = (order + 1) * (order + 1);
				if (separator >= 0)
				{
					var result = FromString(out var extra, value.Substring(separator + 1));
					if (result < 0 || extra.ChannelCount >= int.MaxValue - layout.ChannelCount)
					{
						layout = default;
						return result < 0 ? result : FfmpegError.InvalidArgument;
					}
					if (extra.Order == AvChannelOrder.Native)
					{
						layout.Mask = extra.Mask;
					} else
					{
						var ambisonicCount = layout.ChannelCount;
						layout.Order = AvChannelOrder.Custom;
						layout.Map = new AvChannelCustom[ambisonicCount + extra.ChannelCount];
						for (var index = 0; index < ambisonicCount; index++)
						{
							layout.Map[index].Id = AvChannel.AmbisonicBase + index;
						}
						for (var index = 0; index < extra.ChannelCount; index++)
						{
							var channel = ChannelFromIndex(extra, index);
							if (IsAmbisonic(channel))
							{
								layout = default;
								return FfmpegError.InvalidArgument;
							}
							layout.Map[ambisonicCount + index].Id = channel;
							if (extra.Order == AvChannelOrder.Custom)
							{
								layout.Map[ambisonicCount + index].Name = extra.Map[index].Name;
							}
						}
					}
					layout.ChannelCount += extra.ChannelCount;
				}

				return 0;
			}

			var listResult = ParseChannelList(out layout, value);
			if (listResult >= 0)
			{
				return 0;
			}

			if (!value.Contains('-', StringComparison.Ordinal) && TryParseUnsigned(value, out var mask) && mask != 0)
			{
				return FromMask(out layout, mask);
			}

			if (value.EndsWith("c", StringComparison.Ordinal) &&
				int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var defaultCount) &&
				defaultCount > 0)
			{
				layout = Default(defaultCount);
				return layout.Order == AvChannelOrder.Native ? 0 : FfmpegError.InvalidArgument;
			}

			var suffixLength = value.EndsWith("C", StringComparison.Ordinal) ? 1 :
				value.EndsWith(" channels", StringComparison.Ordinal) ? " channels".Length : 0;
			if (suffixLength != 0 &&
				int.TryParse(value.AsSpan(0, value.Length - suffixLength), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelCount) &&
				channelCount > 0)
			{
				layout.Order = AvChannelOrder.Unspecified;
				layout.ChannelCount = channelCount;
				return 0;
			}

			layout = default;
			return FfmpegError.InvalidArgument;
		}

		public static AvChannelLayout Default(int channelCount)
		{
			for (var index = 0; index < s_StandardLayouts.Length; index++)
			{
				if (s_StandardLayouts[index].Layout.ChannelCount == channelCount)
				{
					return s_StandardLayouts[index].Layout;
				}
			}

			return new AvChannelLayout { Order = AvChannelOrder.Unspecified, ChannelCount = channelCount };
		}

		public static AvChannelLayout Copy(AvChannelLayout source)
		{
			var destination = source;
			if (source.Order == AvChannelOrder.Custom && source.Map != null)
			{
				destination.Map = new AvChannelCustom[source.ChannelCount];
				Array.Copy(source.Map, destination.Map, source.ChannelCount);
			}

			return destination;
		}

		public static AvChannel ChannelFromIndex(AvChannelLayout layout, int index)
		{
			if ((uint)index >= (uint)layout.ChannelCount)
			{
				return AvChannel.None;
			}
			if (layout.Order == AvChannelOrder.Custom)
			{
				return layout.Map[index].Id;
			}

			var ambisonicChannels = 0;
			if (layout.Order == AvChannelOrder.Ambisonic)
			{
				ambisonicChannels = layout.ChannelCount - FfmpegMath.PopCount(layout.Mask);
				if (index < ambisonicChannels)
				{
					return AvChannel.AmbisonicBase + index;
				}
				index -= ambisonicChannels;
			} else if (layout.Order != AvChannelOrder.Native)
			{
				return AvChannel.None;
			}

			for (var channel = 0; channel < 64; channel++)
			{
				if (((1UL << channel) & layout.Mask) != 0 && index-- == 0)
				{
					return (AvChannel)channel;
				}
			}

			return AvChannel.None;
		}

		public static int IndexFromChannel(AvChannelLayout layout, AvChannel channel)
		{
			if (channel == AvChannel.None)
			{
				return FfmpegError.InvalidArgument;
			}
			if (layout.Order == AvChannelOrder.Custom)
			{
				for (var index = 0; index < layout.ChannelCount; index++)
				{
					if (layout.Map[index].Id == channel)
					{
						return index;
					}
				}
				return FfmpegError.InvalidArgument;
			}

			if (layout.Order == AvChannelOrder.Native || layout.Order == AvChannelOrder.Ambisonic)
			{
				var ambisonicChannels = layout.ChannelCount - FfmpegMath.PopCount(layout.Mask);
				if (layout.Order == AvChannelOrder.Ambisonic && channel >= AvChannel.AmbisonicBase)
				{
					var ambisonicIndex = channel - AvChannel.AmbisonicBase;
					return ambisonicIndex >= ambisonicChannels ? FfmpegError.InvalidArgument : ambisonicIndex;
				}
				if ((uint)channel > 63 || (layout.Mask & (1UL << (int)channel)) == 0)
				{
					return FfmpegError.InvalidArgument;
				}
				var precedingMask = layout.Mask & ((1UL << (int)channel) - 1);
				return FfmpegMath.PopCount(precedingMask) + ambisonicChannels;
			}

			return FfmpegError.InvalidArgument;
		}

		public static int IndexFromString(AvChannelLayout layout, string value)
		{
			if (value == null)
			{
				return FfmpegError.InvalidArgument;
			}

			var channel = AvChannel.None;
			if (layout.Order == AvChannelOrder.Custom)
			{
				var separator = value.IndexOf('@');
				string customName = null;
				if (separator >= 0)
				{
					customName = separator + 1 < value.Length ? value.Substring(separator + 1) : null;
					channel = ChannelFromString(value.Substring(0, separator));
					if (channel == AvChannel.None && separator != 0)
					{
						return FfmpegError.InvalidArgument;
					}
				}
				for (var index = 0; customName != null && index < layout.ChannelCount; index++)
				{
					if (customName == layout.Map[index].Name && (channel == AvChannel.None || channel == layout.Map[index].Id))
					{
						return index;
					}
				}
			}

			if (layout.Order == AvChannelOrder.Custom || layout.Order == AvChannelOrder.Native || layout.Order == AvChannelOrder.Ambisonic)
			{
				channel = ChannelFromString(value);
				return channel == AvChannel.None ? FfmpegError.InvalidArgument : IndexFromChannel(layout, channel);
			}

			return FfmpegError.InvalidArgument;
		}

		public static AvChannel ChannelFromString(AvChannelLayout layout, string value)
		{
			var index = IndexFromString(layout, value);
			return index < 0 ? AvChannel.None : ChannelFromIndex(layout, index);
		}

		public static bool Check(AvChannelLayout layout)
		{
			if (layout.ChannelCount <= 0)
			{
				return false;
			}
			switch (layout.Order)
			{
				case AvChannelOrder.Native:
					return FfmpegMath.PopCount(layout.Mask) == layout.ChannelCount;
				case AvChannelOrder.Custom:
					if (layout.Map == null)
					{
						return false;
					}
					for (var index = 0; index < layout.ChannelCount; index++)
					{
						if (layout.Map[index].Id == AvChannel.None)
						{
							return false;
						}
					}
					return true;
				case AvChannelOrder.Ambisonic:
					return FfmpegMath.PopCount(layout.Mask) < layout.ChannelCount;
				case AvChannelOrder.Unspecified:
					return true;
				default:
					return false;
			}
		}

		public static int Compare(AvChannelLayout first, AvChannelLayout second)
		{
			if (first.ChannelCount != second.ChannelCount ||
				(first.Order == AvChannelOrder.Unspecified) != (second.Order == AvChannelOrder.Unspecified))
			{
				return 1;
			}
			if (first.Order == AvChannelOrder.Unspecified)
			{
				return 0;
			}
			if ((first.Order == AvChannelOrder.Native || first.Order == AvChannelOrder.Ambisonic) && first.Order == second.Order)
			{
				return first.Mask != second.Mask ? 1 : 0;
			}
			for (var index = 0; index < first.ChannelCount; index++)
			{
				if (ChannelFromIndex(first, index) != ChannelFromIndex(second, index))
				{
					return 1;
				}
			}

			return 0;
		}

		public static ulong Subset(AvChannelLayout layout, ulong mask)
		{
			if (layout.Order == AvChannelOrder.Native || layout.Order == AvChannelOrder.Ambisonic)
			{
				return layout.Mask & mask;
			}
			ulong result = 0;
			if (layout.Order == AvChannelOrder.Custom)
			{
				for (var channel = 0; channel < 64; channel++)
				{
					if ((mask & (1UL << channel)) != 0 && IndexFromChannel(layout, (AvChannel)channel) >= 0)
					{
						result |= 1UL << channel;
					}
				}
			}

			return result;
		}

		public static int AmbisonicOrder(AvChannelLayout layout)
		{
			if (layout.Order != AvChannelOrder.Ambisonic && layout.Order != AvChannelOrder.Custom)
			{
				return FfmpegError.InvalidArgument;
			}

			var highest = -1;
			if (layout.Order == AvChannelOrder.Ambisonic)
			{
				highest = layout.ChannelCount - FfmpegMath.PopCount(layout.Mask) - 1;
			} else
			{
				for (var index = 0; index < layout.ChannelCount; index++)
				{
					var isAmbisonic = IsAmbisonic(layout.Map[index].Id);
					if (index > 0 && isAmbisonic && !IsAmbisonic(layout.Map[index - 1].Id) ||
						isAmbisonic && layout.Map[index].Id - AvChannel.AmbisonicBase != index)
					{
						return FfmpegError.InvalidArgument;
					}
					if (isAmbisonic)
					{
						highest = index;
					}
				}
			}
			if (highest < 0)
			{
				return FfmpegError.InvalidArgument;
			}

			var order = (int)Math.Floor(Math.Sqrt(highest));
			return (order + 1) * (order + 1) == highest + 1 ? order : FfmpegError.InvalidArgument;
		}

		/// <summary>
		/// Retypes layouts with FFmpeg's loss reporting, lossless guard, and canonical-order selection semantics.
		/// </summary>
		public static int Retype(ref AvChannelLayout layout, AvChannelOrder order, ChannelRetypeFlags flags)
		{
			var allowLossy = (flags & ChannelRetypeFlags.Lossless) == 0;
			if (!Check(layout))
			{
				return FfmpegError.InvalidArgument;
			}
			if ((flags & ChannelRetypeFlags.Canonical) != 0)
			{
				order = CanonicalOrder(layout);
			}
			if (layout.Order == order)
			{
				return 0;
			}

			switch (order)
			{
				case AvChannelOrder.Unspecified:
				{
					var lossy = layout.Order != AvChannelOrder.Custom;
					if (layout.Order == AvChannelOrder.Custom)
					{
						for (var index = 0; index < layout.ChannelCount; index++)
						{
							if (layout.Map[index].Id != AvChannel.Unknown || !string.IsNullOrEmpty(layout.Map[index].Name))
							{
								lossy = true;
								break;
							}
						}
					}
					if (!lossy || allowLossy)
					{
						layout.Order = AvChannelOrder.Unspecified;
						layout.Mask = 0;
						layout.Map = null;
						return lossy ? 1 : 0;
					}
					return FfmpegError.NotImplemented;
				}
				case AvChannelOrder.Native:
					if (layout.Order == AvChannelOrder.Custom)
					{
						var mask = MaskedDescription(layout, 0);
						if (mask < 0)
						{
							return FfmpegError.NotImplemented;
						}
						var lossy = HasChannelNames(layout);
						if (!lossy || allowLossy)
						{
							var opaque = layout.Opaque;
							FromMask(out layout, (ulong)mask);
							layout.Opaque = opaque;
							return lossy ? 1 : 0;
						}
					}
					return FfmpegError.NotImplemented;
				case AvChannelOrder.Custom:
				{
					var result = CustomInitialize(out var custom, layout.ChannelCount);
					if (result < 0)
					{
						return result;
					}
					if (layout.Order != AvChannelOrder.Unspecified)
					{
						for (var index = 0; index < layout.ChannelCount; index++)
						{
							custom.Map[index].Id = ChannelFromIndex(layout, index);
						}
					}
					custom.Opaque = layout.Opaque;
					layout = custom;
					return 0;
				}
				case AvChannelOrder.Ambisonic:
					if (layout.Order == AvChannelOrder.Custom)
					{
						var ambisonicOrder = AmbisonicOrder(layout);
						if (ambisonicOrder < 0)
						{
							return FfmpegError.NotImplemented;
						}
						var mask = MaskedDescription(layout, (ambisonicOrder + 1) * (ambisonicOrder + 1));
						if (mask < 0)
						{
							return FfmpegError.NotImplemented;
						}
						var lossy = HasChannelNames(layout);
						if (!lossy || allowLossy)
						{
							layout.Order = AvChannelOrder.Ambisonic;
							layout.Mask = (ulong)mask;
							layout.Map = null;
							return lossy ? 1 : 0;
						}
					}
					return FfmpegError.NotImplemented;
				default:
					return FfmpegError.InvalidArgument;
			}
		}

		/// <summary>
		/// Formats native, ambisonic, and custom channel layouts using FFmpeg's naming and fallback rules.
		/// </summary>
		public static string Describe(AvChannelLayout layout)
		{
			if (layout.Order == AvChannelOrder.Native)
			{
				for (var index = 0; index < s_StandardLayouts.Length; index++)
				{
					if (layout.Mask == s_StandardLayouts[index].Layout.Mask)
					{
						return s_StandardLayouts[index].Name;
					}
				}
			}
			if (layout.Order == AvChannelOrder.Ambisonic)
			{
				var order = AmbisonicOrder(layout);
				if (order >= 0)
				{
					var description = "ambisonic " + order.ToString(CultureInfo.InvariantCulture);
					var ambisonicChannels = (order + 1) * (order + 1);
					if (ambisonicChannels < layout.ChannelCount)
					{
						description += "+" + Describe(Native(FfmpegMath.PopCount(layout.Mask), layout.Mask));
					}
					return description;
				}
			}
			if (layout.Order == AvChannelOrder.Unspecified)
			{
				return layout.ChannelCount.ToString(CultureInfo.InvariantCulture) + " channels";
			}

			var builder = new StringBuilder();
			if (layout.ChannelCount != 0)
			{
				builder.Append(layout.ChannelCount.ToString(CultureInfo.InvariantCulture));
				builder.Append(" channels (");
			}
			for (var index = 0; index < layout.ChannelCount; index++)
			{
				if (index != 0)
				{
					builder.Append('+');
				}
				builder.Append(ChannelName(ChannelFromIndex(layout, index)));
				if (layout.Order == AvChannelOrder.Custom && !string.IsNullOrEmpty(layout.Map[index].Name))
				{
					builder.Append('@');
					builder.Append(layout.Map[index].Name);
				}
			}
			if (layout.ChannelCount != 0)
			{
				builder.Append(')');
			}

			return builder.ToString();
		}

		public static string ChannelName(AvChannel channel)
		{
			if (IsAmbisonic(channel))
			{
				return "AMBI" + (channel - AvChannel.AmbisonicBase).ToString(CultureInfo.InvariantCulture);
			}

			return channel switch
			{
				AvChannel.FrontLeft => "FL", AvChannel.FrontRight => "FR", AvChannel.FrontCenter => "FC",
				AvChannel.LowFrequency => "LFE", AvChannel.BackLeft => "BL", AvChannel.BackRight => "BR",
				AvChannel.FrontLeftOfCenter => "FLC", AvChannel.FrontRightOfCenter => "FRC", AvChannel.BackCenter => "BC",
				AvChannel.SideLeft => "SL", AvChannel.SideRight => "SR", AvChannel.TopCenter => "TC",
				AvChannel.TopFrontLeft => "TFL", AvChannel.TopFrontCenter => "TFC", AvChannel.TopFrontRight => "TFR",
				AvChannel.TopBackLeft => "TBL", AvChannel.TopBackCenter => "TBC", AvChannel.TopBackRight => "TBR",
				AvChannel.StereoLeft => "DL", AvChannel.StereoRight => "DR", AvChannel.WideLeft => "WL", AvChannel.WideRight => "WR",
				AvChannel.SurroundDirectLeft => "SDL", AvChannel.SurroundDirectRight => "SDR", AvChannel.LowFrequency2 => "LFE2",
				AvChannel.TopSideLeft => "TSL", AvChannel.TopSideRight => "TSR", AvChannel.BottomFrontCenter => "BFC",
				AvChannel.BottomFrontLeft => "BFL", AvChannel.BottomFrontRight => "BFR", AvChannel.SideSurroundLeft => "SSL",
				AvChannel.SideSurroundRight => "SSR", AvChannel.TopSurroundLeft => "TTL", AvChannel.TopSurroundRight => "TTR",
				AvChannel.BinauralLeft => "BIL", AvChannel.BinauralRight => "BIR", AvChannel.None => "NONE",
				AvChannel.Unused => "UNSD", AvChannel.Unknown => "UNK", _ => channel.ToString("D")
			};
		}

		public static string ChannelDescription(AvChannel channel)
		{
			if (IsAmbisonic(channel))
			{
				return "ambisonic ACN " + (channel - AvChannel.AmbisonicBase).ToString(CultureInfo.InvariantCulture);
			}

			return channel switch
			{
				AvChannel.FrontLeft => "front left", AvChannel.FrontRight => "front right", AvChannel.FrontCenter => "front center",
				AvChannel.LowFrequency => "low frequency", AvChannel.BackLeft => "back left", AvChannel.BackRight => "back right",
				AvChannel.FrontLeftOfCenter => "front left-of-center", AvChannel.FrontRightOfCenter => "front right-of-center",
				AvChannel.BackCenter => "back center", AvChannel.SideLeft => "side left", AvChannel.SideRight => "side right",
				AvChannel.TopCenter => "top center", AvChannel.TopFrontLeft => "top front left", AvChannel.TopFrontCenter => "top front center",
				AvChannel.TopFrontRight => "top front right", AvChannel.TopBackLeft => "top back left", AvChannel.TopBackCenter => "top back center",
				AvChannel.TopBackRight => "top back right", AvChannel.StereoLeft => "downmix left", AvChannel.StereoRight => "downmix right",
				AvChannel.WideLeft => "wide left", AvChannel.WideRight => "wide right", AvChannel.SurroundDirectLeft => "surround direct left",
				AvChannel.SurroundDirectRight => "surround direct right", AvChannel.LowFrequency2 => "low frequency 2",
				AvChannel.TopSideLeft => "top side left", AvChannel.TopSideRight => "top side right", AvChannel.BottomFrontCenter => "bottom front center",
				AvChannel.BottomFrontLeft => "bottom front left", AvChannel.BottomFrontRight => "bottom front right",
				AvChannel.SideSurroundLeft => "side surround left", AvChannel.SideSurroundRight => "side surround right",
				AvChannel.TopSurroundLeft => "top surround left", AvChannel.TopSurroundRight => "top surround right",
				AvChannel.BinauralLeft => "binaural left", AvChannel.BinauralRight => "binaural right",
				AvChannel.None => "none", AvChannel.Unused => "unused", AvChannel.Unknown => "unknown",
				_ => "user " + channel.ToString("D")
			};
		}

		public static AvChannel ChannelFromString(string value)
		{
			if (value == null)
			{
				return AvChannel.None;
			}
			for (var channel = -1; channel <= 62; channel++)
			{
				var candidate = (AvChannel)channel;
				if (value == ChannelName(candidate))
				{
					return candidate;
				}
			}
			if (value.StartsWith("AMBI", StringComparison.Ordinal) &&
				int.TryParse(value.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ambisonic) &&
				ambisonic >= 0 && ambisonic <= (int)AvChannel.AmbisonicEnd - (int)AvChannel.AmbisonicBase)
			{
				return AvChannel.AmbisonicBase + ambisonic;
			}
			if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric >= 0)
			{
				return (AvChannel)numeric;
			}

			return AvChannel.None;
		}

		/// <summary>
		/// Parses FFmpeg's plus-separated custom channel-list syntax while preserving order and duplicate checks.
		/// </summary>
		private static int ParseChannelList(out AvChannelLayout layout, string value)
		{
			layout = default;
			if (value.Length == 0)
			{
				return FfmpegError.InvalidArgument;
			}

			var list = value;
			var prefixEnd = value.IndexOf(" channels (", StringComparison.Ordinal);
			var declaredCount = -1;
			if (prefixEnd > 0 && value.EndsWith(")", StringComparison.Ordinal) &&
				int.TryParse(value.AsSpan(0, prefixEnd), NumberStyles.Integer, CultureInfo.InvariantCulture, out declaredCount))
			{
				list = value.Substring(prefixEnd + " channels (".Length, value.Length - prefixEnd - " channels (".Length - 1);
			}

			var components = list.Split('+');
			var map = new AvChannelCustom[components.Length];
			for (var index = 0; index < components.Length; index++)
			{
				var separator = components[index].IndexOf('@');
				var channelText = separator >= 0 ? components[index].Substring(0, separator) : components[index];
				var name = separator >= 0 ? components[index].Substring(separator + 1) : string.Empty;
				var channel = ChannelFromString(channelText);
				if (channel == AvChannel.None)
				{
					return FfmpegError.InvalidArgument;
				}
				map[index].Id = channel;
				map[index].Name = name.Length <= 15 ? name : name.Substring(0, 15);
			}
			if (declaredCount >= 0 && declaredCount != map.Length)
			{
				return FfmpegError.InvalidArgument;
			}

			layout.Order = AvChannelOrder.Custom;
			layout.ChannelCount = map.Length;
			layout.Map = map;
			Canonicalize(ref layout);
			return 0;
		}

		private static void Canonicalize(ref AvChannelLayout layout)
		{
			var mask = MaskedDescription(layout, 0);
			if (mask > 0 && !HasChannelNames(layout))
			{
				var opaque = layout.Opaque;
				FromMask(out layout, (ulong)mask);
				layout.Opaque = opaque;
				return;
			}

			var order = AmbisonicOrder(layout);
			if (order >= 0 && !HasChannelNames(layout))
			{
				var extraMask = MaskedDescription(layout, (order + 1) * (order + 1));
				if (extraMask >= 0)
				{
					layout.Order = AvChannelOrder.Ambisonic;
					layout.Mask = (ulong)extraMask;
					layout.Map = null;
				}
			}
		}

		private static AvChannelOrder CanonicalOrder(AvChannelLayout layout)
		{
			if (layout.Order != AvChannelOrder.Custom || HasChannelNames(layout))
			{
				return layout.Order;
			}

			var hasKnownChannel = false;
			for (var index = 0; index < layout.ChannelCount && !hasKnownChannel; index++)
			{
				hasKnownChannel = layout.Map[index].Id != AvChannel.Unknown;
			}
			if (!hasKnownChannel)
			{
				return AvChannelOrder.Unspecified;
			}
			if (MaskedDescription(layout, 0) > 0)
			{
				return AvChannelOrder.Native;
			}

			var order = AmbisonicOrder(layout);
			return order >= 0 && MaskedDescription(layout, (order + 1) * (order + 1)) >= 0
				? AvChannelOrder.Ambisonic
				: AvChannelOrder.Custom;
		}

		private static long MaskedDescription(AvChannelLayout layout, int startChannel)
		{
			ulong mask = 0;
			for (var index = startChannel; index < layout.ChannelCount; index++)
			{
				var channel = layout.Map[index].Id;
				if (channel >= 0 && (int)channel < 63 && mask < (1UL << (int)channel))
				{
					mask |= 1UL << (int)channel;
				} else
				{
					return FfmpegError.InvalidArgument;
				}
			}

			return (long)mask;
		}

		private static bool HasChannelNames(AvChannelLayout layout)
		{
			if (layout.Order != AvChannelOrder.Custom)
			{
				return false;
			}
			for (var index = 0; index < layout.ChannelCount; index++)
			{
				if (!string.IsNullOrEmpty(layout.Map[index].Name))
				{
					return true;
				}
			}

			return false;
		}

		private static bool TryParseUnsigned(string value, out ulong result)
		{
			if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				return ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
			}

			return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
		}

		private static bool IsAmbisonic(AvChannel channel)
		{
			return channel >= AvChannel.AmbisonicBase && channel <= AvChannel.AmbisonicEnd;
		}

		private static AvChannelLayout Native(int channelCount, ulong mask)
		{
			return new AvChannelLayout { Order = AvChannelOrder.Native, ChannelCount = channelCount, Mask = mask };
		}

		/// <summary>
		/// Couples an FFmpeg standard-layout name to its immutable native mask value.
		/// </summary>
		private readonly struct NamedLayout
		{
			public string Name { get; }
			public AvChannelLayout Layout { get; }

			public NamedLayout(string name, AvChannelLayout layout)
			{
				Name = name;
				Layout = layout;
			}
		}
	}

	/// <summary>
	/// Matches FFmpeg's lossless and canonical AVChannelLayout retyping flags.
	/// </summary>
	[Flags]
	internal enum ChannelRetypeFlags
	{
		None = 0,
		Lossless = 1,
		Canonical = 2
	}
}
