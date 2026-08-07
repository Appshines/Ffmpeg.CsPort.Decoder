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
using System.Numerics;
using Ffmpeg.CsPort.Decoder.Bitstream;
using Ffmpeg.CsPort.Decoder.Infrastructure;

namespace Ffmpeg.CsPort.Decoder.Codecs.Dca
{
	/// <summary>
	/// Ports FFmpeg's DTS extension-substream header, asset descriptor, and component-offset parser.
	/// </summary>
	internal sealed class DcaExssParser
	{
		private readonly BitReader _Bits = new BitReader();
		private int _SizeBitCount;
		private int _StaticFieldsPresent;
		private int _MixMetadataEnabled;
		private int _MixOutputConfigurationCount;
		private readonly int[] _MixOutputChannels = new int[4];

		public int Index { get; private set; }
		public int Size { get; private set; }
		public int Presentations { get; private set; }
		public int Assets { get; private set; }
		public DcaExssAsset Asset { get; } = new DcaExssAsset();

		/// <summary>
		/// Parses one complete EXSS frame and resolves the byte range of each supported coding extension inside its asset.
		/// </summary>
		public int Parse(byte[] data, int offset, int size)
		{
			var result = _Bits.Initialize(data, offset, size * 8);
			if (result < 0) return result;
			if (_Bits.ReadBitsLong(32) != DcaBitstream.ExtensionSubstreamSyncWord) return FfmpegError.InvalidData;
			_Bits.SkipBits(8);
			Index = (int)_Bits.ReadBits(2);
			var wideHeader = (int)_Bits.ReadBit();
			var headerSize = (int)_Bits.ReadBits(8 + 4 * wideHeader) + 1;
			_SizeBitCount = 16 + 4 * wideHeader;
			Size = (int)_Bits.ReadBits(_SizeBitCount) + 1;
			if (Size > size) return FfmpegError.InvalidData;
			_StaticFieldsPresent = (int)_Bits.ReadBit();
			if (_StaticFieldsPresent != 0)
			{
				_Bits.SkipBits(5);
				if (_Bits.ReadBit() != 0) SkipLong(36);
				Presentations = (int)_Bits.ReadBits(3) + 1;
				if (Presentations > 1) return FfmpegError.PatchWelcome;
				Assets = (int)_Bits.ReadBits(3) + 1;
				if (Assets > 1) return FfmpegError.PatchWelcome;
				Span<int> activeMasks = stackalloc int[8];
				for (var index = 0; index < Presentations; index++) activeMasks[index] = (int)_Bits.ReadBits(Index + 1);
				for (var index = 0; index < Presentations; index++) SkipLong(BitOperations.PopCount((uint)activeMasks[index]) * 8);
				_MixMetadataEnabled = (int)_Bits.ReadBit();
				if (_MixMetadataEnabled != 0)
				{
					_Bits.SkipBits(2);
					var speakerMaskBits = ((int)_Bits.ReadBits(2) + 1) << 2;
					_MixOutputConfigurationCount = (int)_Bits.ReadBits(2) + 1;
					for (var index = 0; index < _MixOutputConfigurationCount; index++) _MixOutputChannels[index] = CountChannelsForMask((int)_Bits.ReadBits(speakerMaskBits));
				}
			} else
			{
				Presentations = 1;
				Assets = 1;
			}

			var assetOffset = headerSize;
			Asset.AssetOffset = assetOffset;
			Asset.AssetSize = (int)_Bits.ReadBits(_SizeBitCount) + 1;
			if (assetOffset + Asset.AssetSize > Size) return FfmpegError.InvalidData;
			result = ParseDescriptor(Asset);
			if (result < 0) return result;
			result = SetOffsets(Asset);
			if (result < 0) return result;
			return Seek(headerSize * 8);
		}

		/// <summary>
		/// Parses static audio metadata, optional mix maps, coding mode, component sizes, and XLL synchronization data.
		/// </summary>
		private int ParseDescriptor(DcaExssAsset asset)
		{
			var descriptorPosition = _Bits.Position;
			var descriptorSize = (int)_Bits.ReadBits(9) + 1;
			asset.AssetIndex = (int)_Bits.ReadBits(3);
			if (_StaticFieldsPresent != 0)
			{
				if (_Bits.ReadBit() != 0) _Bits.SkipBits(4);
				if (_Bits.ReadBit() != 0) _Bits.SkipBits(24);
				if (_Bits.ReadBit() != 0)
				{
					var textSize = (int)_Bits.ReadBits(10) + 1;
					if (_Bits.BitsLeft < textSize * 8) return FfmpegError.InvalidData;
					SkipLong(textSize * 8);
				}
				asset.PcmBitResolution = (int)_Bits.ReadBits(5) + 1;
				asset.MaximumSampleRate = DcaTables.SamplingFrequencies[_Bits.ReadBits(4)];
				asset.TotalChannels = (int)_Bits.ReadBits(8) + 1;
				asset.OneToOneChannelToSpeaker = (int)_Bits.ReadBit();
				if (asset.OneToOneChannelToSpeaker != 0)
				{
					asset.EmbeddedStereo = asset.TotalChannels > 2 && _Bits.ReadBit() != 0 ? 1 : 0;
					asset.EmbeddedSixChannels = asset.TotalChannels > 6 && _Bits.ReadBit() != 0 ? 1 : 0;
					asset.SpeakerMaskEnabled = (int)_Bits.ReadBit();
					var speakerMaskBits = 0;
					if (asset.SpeakerMaskEnabled != 0)
					{
						speakerMaskBits = ((int)_Bits.ReadBits(2) + 1) << 2;
						asset.SpeakerMask = (int)_Bits.ReadBits(speakerMaskBits);
					}
					var remappingSets = (int)_Bits.ReadBits(3);
					if (remappingSets != 0 && speakerMaskBits == 0) return FfmpegError.InvalidData;
					Span<int> speakers = stackalloc int[8];
					for (var index = 0; index < remappingSets; index++) speakers[index] = CountChannelsForMask((int)_Bits.ReadBits(speakerMaskBits));
					for (var index = 0; index < remappingSets; index++)
					{
						var remappingChannels = (int)_Bits.ReadBits(5) + 1;
						for (var speaker = 0; speaker < speakers[index]; speaker++)
						{
							var channelMask = _Bits.ReadBitsLong(remappingChannels);
							SkipLong(BitOperations.PopCount(channelMask) * 5);
						}
					}
				} else
				{
					asset.EmbeddedStereo = asset.EmbeddedSixChannels = asset.SpeakerMaskEnabled = asset.SpeakerMask = 0;
					asset.RepresentationType = (int)_Bits.ReadBits(3);
				}
			}

			var dynamicRangePresent = (int)_Bits.ReadBit();
			if (dynamicRangePresent != 0) _Bits.SkipBits(8);
			if (_Bits.ReadBit() != 0) _Bits.SkipBits(5);
			if (dynamicRangePresent != 0 && asset.EmbeddedStereo != 0) _Bits.SkipBits(8);
			if (_MixMetadataEnabled != 0 && _Bits.ReadBit() != 0)
			{
				_Bits.SkipBits(7);
				if (_Bits.ReadBits(2) == 3) _Bits.SkipBits(8); else _Bits.SkipBits(3);
				if (_Bits.ReadBit() != 0)
				{
					for (var index = 0; index < _MixOutputConfigurationCount; index++) SkipLong(6 * _MixOutputChannels[index]);
				} else SkipLong(6 * _MixOutputConfigurationCount);
				var downmixChannels = asset.TotalChannels + (asset.EmbeddedSixChannels != 0 ? 6 : 0) + (asset.EmbeddedStereo != 0 ? 2 : 0);
				for (var configuration = 0; configuration < _MixOutputConfigurationCount; configuration++)
				{
					if (_MixOutputChannels[configuration] == 0) return FfmpegError.InvalidData;
					for (var channel = 0; channel < downmixChannels; channel++)
					{
						var mask = _Bits.ReadBitsLong(_MixOutputChannels[configuration]);
						SkipLong(BitOperations.PopCount(mask) * 6);
					}
				}
			}

			asset.CodingMode = (int)_Bits.ReadBits(2);
			if (asset.CodingMode == 0)
			{
				asset.ExtensionMask = (int)_Bits.ReadBits(12);
				if ((asset.ExtensionMask & 0x010) != 0) { asset.CoreSize = (int)_Bits.ReadBits(14) + 1; if (_Bits.ReadBit() != 0) _Bits.SkipBits(2); }
				if ((asset.ExtensionMask & 0x020) != 0) asset.XbrSize = (int)_Bits.ReadBits(14) + 1;
				if ((asset.ExtensionMask & 0x040) != 0) asset.XxchSize = (int)_Bits.ReadBits(14) + 1;
				if ((asset.ExtensionMask & 0x080) != 0) asset.X96Size = (int)_Bits.ReadBits(12) + 1;
				if ((asset.ExtensionMask & 0x100) != 0) ParseLbrParameters(asset);
				if ((asset.ExtensionMask & 0x200) != 0) ParseXllParameters(asset);
				if ((asset.ExtensionMask & 0x400) != 0) _Bits.SkipBits(16);
				if ((asset.ExtensionMask & 0x800) != 0) _Bits.SkipBits(16);
			} else if (asset.CodingMode == 1)
			{
				asset.ExtensionMask = 0x200;
				ParseXllParameters(asset);
			} else if (asset.CodingMode == 2)
			{
				asset.ExtensionMask = 0x100;
				ParseLbrParameters(asset);
			} else
			{
				asset.ExtensionMask = 0;
				_Bits.SkipBits(22);
				if (_Bits.ReadBit() != 0) _Bits.SkipBits(3);
			}
			if ((asset.ExtensionMask & 0x200) != 0) asset.HighDefinitionStreamId = (int)_Bits.ReadBits(3);
			return Seek(descriptorPosition + descriptorSize * 8);
		}

		private void ParseXllParameters(DcaExssAsset asset)
		{
			asset.XllSize = (int)_Bits.ReadBits(_SizeBitCount) + 1;
			asset.XllSyncPresent = (int)_Bits.ReadBit();
			if (asset.XllSyncPresent != 0)
			{
				_Bits.SkipBits(4);
				var delayBits = (int)_Bits.ReadBits(5) + 1;
				asset.XllDelayFrames = (int)_Bits.ReadBitsLong(delayBits);
				asset.XllSyncOffset = (int)_Bits.ReadBits(_SizeBitCount);
			} else asset.XllDelayFrames = asset.XllSyncOffset = 0;
		}

		private void ParseLbrParameters(DcaExssAsset asset)
		{
			asset.LbrSize = (int)_Bits.ReadBits(14) + 1;
			if (_Bits.ReadBit() != 0) _Bits.SkipBits(2);
		}

		private static int SetOffsets(DcaExssAsset asset)
		{
			var offset = asset.AssetOffset;
			var size = asset.AssetSize;
			if (!SetOffset(asset.ExtensionMask, 0x010, asset.CoreSize, ref offset, ref size, out asset.CoreOffset)) return FfmpegError.InvalidData;
			if (!SetOffset(asset.ExtensionMask, 0x020, asset.XbrSize, ref offset, ref size, out asset.XbrOffset)) return FfmpegError.InvalidData;
			if (!SetOffset(asset.ExtensionMask, 0x040, asset.XxchSize, ref offset, ref size, out asset.XxchOffset)) return FfmpegError.InvalidData;
			if (!SetOffset(asset.ExtensionMask, 0x080, asset.X96Size, ref offset, ref size, out asset.X96Offset)) return FfmpegError.InvalidData;
			if (!SetOffset(asset.ExtensionMask, 0x100, asset.LbrSize, ref offset, ref size, out asset.LbrOffset)) return FfmpegError.InvalidData;
			if (!SetOffset(asset.ExtensionMask, 0x200, asset.XllSize, ref offset, ref size, out asset.XllOffset)) return FfmpegError.InvalidData;
			return 0;
		}

		private static bool SetOffset(int mask, int flag, int componentSize, ref int offset, ref int remaining, out int componentOffset)
		{
			componentOffset = 0;
			if ((mask & flag) == 0) return true;
			componentOffset = offset;
			if (componentSize > remaining) return false;
			offset += componentSize;
			remaining -= componentSize;
			return true;
		}

		private int Seek(int position)
		{
			if (position < _Bits.Position || position > _Bits.SizeInBits) return FfmpegError.InvalidData;
			_Bits.SkipBits(position - _Bits.Position);
			return 0;
		}

		private void SkipLong(int count)
		{
			while (count > 0) { var step = Math.Min(25, count); _Bits.SkipBits(step); count -= step; }
		}

		private static int CountChannelsForMask(int mask)
		{
			return BitOperations.PopCount((uint)((mask & 0xffff) | ((mask & 0xae66) << 16)));
		}
	}

	/// <summary>
	/// Stores one FFmpeg DTS EXSS asset descriptor and the resolved byte ranges of its coding components.
	/// </summary>
	internal sealed class DcaExssAsset
	{
		public int AssetOffset, AssetSize, AssetIndex;
		public int PcmBitResolution, MaximumSampleRate, TotalChannels, OneToOneChannelToSpeaker;
		public int EmbeddedStereo, EmbeddedSixChannels, SpeakerMaskEnabled, SpeakerMask, RepresentationType;
		public int CodingMode, ExtensionMask;
		public int CoreOffset, CoreSize, XbrOffset, XbrSize, XxchOffset, XxchSize, X96Offset, X96Size;
		public int LbrOffset, LbrSize, XllOffset, XllSize, XllSyncPresent, XllDelayFrames, XllSyncOffset;
		public int HighDefinitionStreamId;
	}
}
