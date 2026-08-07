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

namespace Ffmpeg.CsPort.Decoder.Codecs.Dca
{
	/// <summary>
	/// Ports FFmpeg's DTS/DCA packet dispatcher and exposes managed planar output for core and extension substreams.
	/// </summary>
	public sealed class DcaDecoder
	{
		private const int MinimumPacketSize = 16;
		private const int MaximumPacketSize = 0x104000;
		private readonly DcaCoreDecoder _Core = new DcaCoreDecoder();
		private readonly DcaExssParser _Exss = new DcaExssParser();
		private readonly DcaXllDecoder _Xll = new DcaXllDecoder();
		private readonly DcaLbrDecoder _Lbr = new DcaLbrDecoder();
		private readonly byte[] _ConvertedPacket = new byte[MaximumPacketSize + 64];
		private bool _PreviousPacketHadXll;
		private bool _PreviousPacketHadResidual;

		/// <summary>
		/// Normalizes the four DTS core packing variants, decodes one complete packet, and writes planar sample bytes.
		/// </summary>
		public int DecodeFrame(byte[] packet, int packetOffset, int packetLength, Span<byte> output, out AudioFrameInfo frame)
		{
			frame = default;
			if (packet == null || packetOffset < 0 || packetLength < MinimumPacketSize || packetLength > MaximumPacketSize || packetOffset > packet.Length - packetLength)
				return FfmpegError.InvalidData;
			var marker = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(packetOffset, 4));
			byte[] input;
			var inputOffset = 0;
			var inputSize = packetLength;
			if (marker == DcaBitstream.CoreBigEndianSyncWord || marker == DcaBitstream.ExtensionSubstreamSyncWord)
			{
				input = packet;
				inputOffset = packetOffset;
			} else
			{
				input = _ConvertedPacket;
				inputSize = FfmpegError.InvalidData;
				for (var offset = 0; offset <= packetLength - MinimumPacketSize && inputSize < 0; offset++)
					inputSize = DcaBitstream.ConvertBitstream(packet, packetOffset + offset, packetLength - offset, _ConvertedPacket, _ConvertedPacket.Length);
				if (inputSize < 0) return inputSize;
			}
			var currentOffset = inputOffset;
			var currentSize = inputSize;
			var coreParsed = false;
			var exssParsed = false;
			var xllParsed = false;
			var lbrParsed = false;
			var recovery = false;
			if (BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(currentOffset, 4)) == DcaBitstream.CoreBigEndianSyncWord)
			{
				var result = _Core.Parse(input, currentOffset, currentSize);
				if (result < 0) return result;
				coreParsed = true;
				var coreSize = (_Core.FrameSize + 3) & ~3;
				if (currentSize - 4 > coreSize)
				{
					currentOffset += coreSize;
					currentSize -= coreSize;
				}
			}
			if (currentSize >= 4 && BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(currentOffset, 4)) == DcaBitstream.ExtensionSubstreamSyncWord)
			{
				var result = _Exss.Parse(input, currentOffset, currentSize);
				if (result >= 0)
				{
					exssParsed = true;
					if ((_Exss.Asset.ExtensionMask & 0x200) != 0)
					{
						result = _Xll.Parse(input, currentOffset, _Exss.Asset);
						xllParsed = result >= 0;
						if (result == FfmpegError.TryAgain && _PreviousPacketHadXll && coreParsed)
						{
							xllParsed = true;
							recovery = true;
						}
						if (result == FfmpegError.OutOfMemory) return result;
					}
					if ((_Exss.Asset.ExtensionMask & 0x100) != 0)
					{
						result = _Lbr.Parse(input, currentOffset, _Exss.Asset);
						lbrParsed = result >= 0;
						if (result == FfmpegError.OutOfMemory) return result;
					}
				}
			}
			if (coreParsed)
			{
				var extensionResult = _Core.ParseExtensionSubstream(input, currentOffset, exssParsed ? _Exss.Asset : null, xllParsed);
				if (extensionResult < 0) return extensionResult;
			}
			if (lbrParsed)
			{
				var result = _Lbr.Filter(output, out frame);
				_PreviousPacketHadXll = _PreviousPacketHadResidual = false;
				return result < 0 ? result : packetLength;
			}
			if (xllParsed)
			{
				if (coreParsed)
				{
					var fixedResult = _Core.FilterFixed(_Xll.BaseFrequency == 96000 && _Core.SampleRate == 48000 ? 1 : 0);
					if (fixedResult < 0) return fixedResult;
					if (!_PreviousPacketHadResidual && _Xll.ResidualChannelSets > 0 && _Xll.NumberOfChannelSets > 1) recovery = true;
				}
				var result = _Xll.Filter(output, coreParsed ? _Core : null, recovery, out frame);
				if (result >= 0)
				{
					_PreviousPacketHadXll = true;
					_PreviousPacketHadResidual = coreParsed;
					return packetLength;
				}
				if (!coreParsed || result != FfmpegError.InvalidData) return result;
			}
			if (!coreParsed) return FfmpegError.InvalidData;
			var coreResult = _Core.Filter(output, out frame);
			_PreviousPacketHadXll = _PreviousPacketHadResidual = false;
			return coreResult < 0 ? coreResult : packetLength;
		}
	}
}
