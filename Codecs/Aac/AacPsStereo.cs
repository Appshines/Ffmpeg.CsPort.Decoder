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
namespace Ffmpeg.CsPort.Decoder.Codecs.Aac
{
	/// <summary>Maps AAC PS parameter resolutions and applies FFmpeg's IID/ICC/IPD/OPD stereo mixing matrices.</summary>
	internal static class AacPsStereo
	{
		private static readonly int[] ParameterBands = { 20, 34 };
		private static readonly int[] PhaseBands = { 11, 17 };
		private static readonly int[] HybridBands = { 71, 91 };

		/// <summary>Creates each envelope's target mixing matrix and interpolates it across all PS hybrid time slots.</summary>
		public static void Process(AacParametricStereo ps, bool is34Bands)
		{
			var common = ps.Common;
			var mode = is34Bands ? 1 : 0;
			var mapping = is34Bands ? AacPsTables.KToI34 : AacPsTables.KToI20;
			CopyPreviousMatrix(ps, common.PreviousNumberOfEnvelopes);
			MapParameters(ps, is34Bands);
			if (is34Bands && !common.Was34Bands)
			{
				MapMatrix20To34(ps);
				ResetPhaseHistory(ps);
			} else if (!is34Bands && common.Was34Bands)
			{
				MapMatrix34To20(ps);
				ResetPhaseHistory(ps);
			}

			var mixingTable = common.IccMode < 3 ? AacPsTables.MixingTableA : AacPsTables.MixingTableB;
			for (var envelope = 0; envelope < common.NumberOfEnvelopes; envelope++)
			{
				for (var parameterBand = 0; parameterBand < ParameterBands[mode]; parameterBand++)
				{
					var iid = ps.IidMapped[envelope, parameterBand] + 7 + 23 * common.IidQuantization;
					var icc = ps.IccMapped[envelope, parameterBand];
					var tableOffset = (iid * 8 + icc) * 4;
					var h11 = mixingTable[tableOffset];
					var h12 = mixingTable[tableOffset + 1];
					var h21 = mixingTable[tableOffset + 2];
					var h22 = mixingTable[tableOffset + 3];
					if (common.EnableIpdOpd && parameterBand < PhaseBands[mode])
					{
						var opdIndex = ps.OpdHistory[parameterBand] * 8 + ps.OpdMapped[envelope, parameterBand];
						var ipdIndex = ps.IpdHistory[parameterBand] * 8 + ps.IpdMapped[envelope, parameterBand];
						var opdReal = AacPsTables.PhaseRealSmooth[opdIndex];
						var opdImaginary = AacPsTables.PhaseImaginarySmooth[opdIndex];
						var ipdReal = AacPsTables.PhaseRealSmooth[ipdIndex];
						var ipdImaginary = AacPsTables.PhaseImaginarySmooth[ipdIndex];
						ps.OpdHistory[parameterBand] = (sbyte)(opdIndex & 0x3f);
						ps.IpdHistory[parameterBand] = (sbyte)(ipdIndex & 0x3f);
						var adjustedReal = opdReal * ipdReal + opdImaginary * ipdImaginary;
						var adjustedImaginary = opdImaginary * ipdReal - opdReal * ipdImaginary;
						ps.H11[1, envelope + 1, parameterBand] = h11 * opdImaginary;
						h11 *= opdReal;
						ps.H12[1, envelope + 1, parameterBand] = h12 * adjustedImaginary;
						h12 *= adjustedReal;
						ps.H21[1, envelope + 1, parameterBand] = h21 * opdImaginary;
						h21 *= opdReal;
						ps.H22[1, envelope + 1, parameterBand] = h22 * adjustedImaginary;
						h22 *= adjustedReal;
					}
					ps.H11[0, envelope + 1, parameterBand] = h11;
					ps.H12[0, envelope + 1, parameterBand] = h12;
					ps.H21[0, envelope + 1, parameterBand] = h21;
					ps.H22[0, envelope + 1, parameterBand] = h22;
				}

				for (var hybridBand = 0; hybridBand < HybridBands[mode]; hybridBand++)
				{
					var start = common.BorderPosition[envelope];
					var stop = common.BorderPosition[envelope + 1];
					var width = 1.0f / (stop - start != 0 ? stop - start : 1);
					var parameterBand = mapping[hybridBand];
					ps.Matrix[0, 0] = ps.H11[0, envelope, parameterBand];
					ps.Matrix[0, 1] = ps.H12[0, envelope, parameterBand];
					ps.Matrix[0, 2] = ps.H21[0, envelope, parameterBand];
					ps.Matrix[0, 3] = ps.H22[0, envelope, parameterBand];
					if (common.EnableIpdOpd)
					{
						var negate = is34Bands && hybridBand >= 9 && hybridBand <= 13 || !is34Bands && hybridBand <= 1;
						var sign = negate ? -1.0f : 1.0f;
						ps.Matrix[1, 0] = sign * ps.H11[1, envelope, parameterBand];
						ps.Matrix[1, 1] = sign * ps.H12[1, envelope, parameterBand];
						ps.Matrix[1, 2] = sign * ps.H21[1, envelope, parameterBand];
						ps.Matrix[1, 3] = sign * ps.H22[1, envelope, parameterBand];
					}
					ps.MatrixStep[0, 0] = (ps.H11[0, envelope + 1, parameterBand] - ps.Matrix[0, 0]) * width;
					ps.MatrixStep[0, 1] = (ps.H12[0, envelope + 1, parameterBand] - ps.Matrix[0, 1]) * width;
					ps.MatrixStep[0, 2] = (ps.H21[0, envelope + 1, parameterBand] - ps.Matrix[0, 2]) * width;
					ps.MatrixStep[0, 3] = (ps.H22[0, envelope + 1, parameterBand] - ps.Matrix[0, 3]) * width;
					if (common.EnableIpdOpd)
					{
						ps.MatrixStep[1, 0] = (ps.H11[1, envelope + 1, parameterBand] - ps.Matrix[1, 0]) * width;
						ps.MatrixStep[1, 1] = (ps.H12[1, envelope + 1, parameterBand] - ps.Matrix[1, 1]) * width;
						ps.MatrixStep[1, 2] = (ps.H21[1, envelope + 1, parameterBand] - ps.Matrix[1, 2]) * width;
						ps.MatrixStep[1, 3] = (ps.H22[1, envelope + 1, parameterBand] - ps.Matrix[1, 3]) * width;
					}
					if (stop - start != 0)
						AacPsDsp.StereoInterpolate(ps, hybridBand, start + 1, stop - start, common.EnableIpdOpd);
				}
			}
		}

		private static void MapParameters(AacParametricStereo ps, bool is34)
		{
			var common = ps.Common;
			if (is34)
			{
				MapRowsTo34(ps.IidMapped, common.IidParameters, common.NumberOfIidParameters, common.NumberOfEnvelopes, true);
				MapRowsTo34(ps.IccMapped, common.IccParameters, common.NumberOfIccParameters, common.NumberOfEnvelopes, true);
				if (common.EnableIpdOpd)
				{
					MapRowsTo34(ps.IpdMapped, common.IpdParameters, common.NumberOfIpdOpdParameters, common.NumberOfEnvelopes, false);
					MapRowsTo34(ps.OpdMapped, common.OpdParameters, common.NumberOfIpdOpdParameters, common.NumberOfEnvelopes, false);
				}
			} else
			{
				MapRowsTo20(ps.IidMapped, common.IidParameters, common.NumberOfIidParameters, common.NumberOfEnvelopes, true);
				MapRowsTo20(ps.IccMapped, common.IccParameters, common.NumberOfIccParameters, common.NumberOfEnvelopes, true);
				if (common.EnableIpdOpd)
				{
					MapRowsTo20(ps.IpdMapped, common.IpdParameters, common.NumberOfIpdOpdParameters, common.NumberOfEnvelopes, false);
					MapRowsTo20(ps.OpdMapped, common.OpdParameters, common.NumberOfIpdOpdParameters, common.NumberOfEnvelopes, false);
				}
			}
		}

		private static void MapRowsTo34(sbyte[,] destination, sbyte[,] source, int numberOfParameters, int envelopes, bool full)
		{
			for (var envelope = 0; envelope < envelopes; envelope++)
			{
				if (numberOfParameters == 20 || numberOfParameters == 11)
					MapIndex20To34(destination, envelope, source, envelope, full);
				else if (numberOfParameters == 10 || numberOfParameters == 5)
					MapIndex10To34(destination, envelope, source, envelope, full);
				else
					CopyRow(destination, envelope, source, envelope);
			}
		}

		private static void MapRowsTo20(sbyte[,] destination, sbyte[,] source, int numberOfParameters, int envelopes, bool full)
		{
			for (var envelope = 0; envelope < envelopes; envelope++)
			{
				if (numberOfParameters == 34 || numberOfParameters == 17)
					MapIndex34To20(destination, envelope, source, envelope, full);
				else if (numberOfParameters == 10 || numberOfParameters == 5)
					MapIndex10To20(destination, envelope, source, envelope, full);
				else
					CopyRow(destination, envelope, source, envelope);
			}
		}

		private static void MapIndex10To20(sbyte[,] destination, int destinationRow, sbyte[,] source, int sourceRow, bool full)
		{
			var band = full ? 9 : 4;
			if (!full)
				destination[destinationRow, 10] = 0;
			for (; band >= 0; band--)
			{
				destination[destinationRow, 2 * band + 1] = source[sourceRow, band];
				destination[destinationRow, 2 * band] = source[sourceRow, band];
			}
		}

		private static void MapIndex34To20(sbyte[,] d, int dr, sbyte[,] s, int sr, bool full)
		{
			d[dr, 0] = (sbyte)((2 * s[sr, 0] + s[sr, 1]) / 3);
			d[dr, 1] = (sbyte)((s[sr, 1] + 2 * s[sr, 2]) / 3);
			d[dr, 2] = (sbyte)((2 * s[sr, 3] + s[sr, 4]) / 3);
			d[dr, 3] = (sbyte)((s[sr, 4] + 2 * s[sr, 5]) / 3);
			d[dr, 4] = (sbyte)((s[sr, 6] + s[sr, 7]) / 2);
			d[dr, 5] = (sbyte)((s[sr, 8] + s[sr, 9]) / 2);
			d[dr, 6] = s[sr, 10]; d[dr, 7] = s[sr, 11];
			d[dr, 8] = (sbyte)((s[sr, 12] + s[sr, 13]) / 2);
			d[dr, 9] = (sbyte)((s[sr, 14] + s[sr, 15]) / 2);
			d[dr, 10] = s[sr, 16];
			if (!full)
				return;
			d[dr, 11] = s[sr, 17]; d[dr, 12] = s[sr, 18]; d[dr, 13] = s[sr, 19];
			d[dr, 14] = (sbyte)((s[sr, 20] + s[sr, 21]) / 2);
			d[dr, 15] = (sbyte)((s[sr, 22] + s[sr, 23]) / 2);
			d[dr, 16] = (sbyte)((s[sr, 24] + s[sr, 25]) / 2);
			d[dr, 17] = (sbyte)((s[sr, 26] + s[sr, 27]) / 2);
			d[dr, 18] = (sbyte)((s[sr, 28] + s[sr, 29] + s[sr, 30] + s[sr, 31]) / 4);
			d[dr, 19] = (sbyte)((s[sr, 32] + s[sr, 33]) / 2);
		}

		private static void MapIndex10To34(sbyte[,] d, int dr, sbyte[,] s, int sr, bool full)
		{
			if (full)
			{
				for (var band = 28; band <= 33; band++) d[dr, band] = s[sr, 9];
				for (var band = 24; band <= 27; band++) d[dr, band] = s[sr, 8];
				for (var band = 20; band <= 23; band++) d[dr, band] = s[sr, 7];
				d[dr, 18] = s[sr, 6]; d[dr, 19] = s[sr, 6];
				d[dr, 16] = s[sr, 5]; d[dr, 17] = s[sr, 5];
			} else d[dr, 16] = 0;
			for (var band = 12; band <= 15; band++) d[dr, band] = s[sr, 4];
			d[dr, 10] = s[sr, 3]; d[dr, 11] = s[sr, 3];
			for (var band = 6; band <= 9; band++) d[dr, band] = s[sr, 2];
			for (var band = 3; band <= 5; band++) d[dr, band] = s[sr, 1];
			for (var band = 0; band <= 2; band++) d[dr, band] = s[sr, 0];
		}

		private static void MapIndex20To34(sbyte[,] d, int dr, sbyte[,] s, int sr, bool full)
		{
			if (full)
			{
				d[dr, 33] = s[sr, 19]; d[dr, 32] = s[sr, 19];
				for (var band = 28; band <= 31; band++) d[dr, band] = s[sr, 18];
				d[dr, 27] = s[sr, 17]; d[dr, 26] = s[sr, 17];
				d[dr, 25] = s[sr, 16]; d[dr, 24] = s[sr, 16];
				d[dr, 23] = s[sr, 15]; d[dr, 22] = s[sr, 15];
				d[dr, 21] = s[sr, 14]; d[dr, 20] = s[sr, 14];
				d[dr, 19] = s[sr, 13]; d[dr, 18] = s[sr, 12]; d[dr, 17] = s[sr, 11];
			}
			d[dr, 16] = s[sr, 10]; d[dr, 15] = s[sr, 9]; d[dr, 14] = s[sr, 9];
			d[dr, 13] = s[sr, 8]; d[dr, 12] = s[sr, 8]; d[dr, 11] = s[sr, 7];
			d[dr, 10] = s[sr, 6]; d[dr, 9] = s[sr, 5]; d[dr, 8] = s[sr, 5];
			d[dr, 7] = s[sr, 4]; d[dr, 6] = s[sr, 4]; d[dr, 5] = s[sr, 3];
			d[dr, 4] = (sbyte)((s[sr, 2] + s[sr, 3]) / 2); d[dr, 3] = s[sr, 2];
			d[dr, 2] = s[sr, 1]; d[dr, 1] = (sbyte)((s[sr, 0] + s[sr, 1]) / 2); d[dr, 0] = s[sr, 0];
		}

		private static void CopyPreviousMatrix(AacParametricStereo ps, int previousEnvelope)
		{
			if (previousEnvelope == 0)
				return;
			for (var component = 0; component < 2; component++)
			{
				for (var band = 0; band < 34; band++)
				{
					ps.H11[component, 0, band] = ps.H11[component, previousEnvelope, band];
					ps.H12[component, 0, band] = ps.H12[component, previousEnvelope, band];
					ps.H21[component, 0, band] = ps.H21[component, previousEnvelope, band];
					ps.H22[component, 0, band] = ps.H22[component, previousEnvelope, band];
				}
			}
		}

		private static void MapMatrix20To34(AacParametricStereo ps)
		{
			for (var component = 0; component < 2; component++)
			{
				MapValue20To34(ps.H11, component); MapValue20To34(ps.H12, component);
				MapValue20To34(ps.H21, component); MapValue20To34(ps.H22, component);
			}
		}

		private static void MapMatrix34To20(AacParametricStereo ps)
		{
			for (var component = 0; component < 2; component++)
			{
				MapValue34To20(ps.H11, component); MapValue34To20(ps.H12, component);
				MapValue34To20(ps.H21, component); MapValue34To20(ps.H22, component);
			}
		}

		private static void MapValue34To20(float[,,] p, int c)
		{
			p[c, 0, 0] = (2 * p[c, 0, 0] + p[c, 0, 1]) * 0.33333333f;
			p[c, 0, 1] = (p[c, 0, 1] + 2 * p[c, 0, 2]) * 0.33333333f;
			p[c, 0, 2] = (2 * p[c, 0, 3] + p[c, 0, 4]) * 0.33333333f;
			p[c, 0, 3] = (p[c, 0, 4] + 2 * p[c, 0, 5]) * 0.33333333f;
			p[c, 0, 4] = (p[c, 0, 6] + p[c, 0, 7]) * 0.5f;
			p[c, 0, 5] = (p[c, 0, 8] + p[c, 0, 9]) * 0.5f;
			p[c, 0, 6] = p[c, 0, 10]; p[c, 0, 7] = p[c, 0, 11];
			p[c, 0, 8] = (p[c, 0, 12] + p[c, 0, 13]) * 0.5f;
			p[c, 0, 9] = (p[c, 0, 14] + p[c, 0, 15]) * 0.5f;
			p[c, 0, 10] = p[c, 0, 16]; p[c, 0, 11] = p[c, 0, 17]; p[c, 0, 12] = p[c, 0, 18]; p[c, 0, 13] = p[c, 0, 19];
			p[c, 0, 14] = (p[c, 0, 20] + p[c, 0, 21]) * 0.5f;
			p[c, 0, 15] = (p[c, 0, 22] + p[c, 0, 23]) * 0.5f;
			p[c, 0, 16] = (p[c, 0, 24] + p[c, 0, 25]) * 0.5f;
			p[c, 0, 17] = (p[c, 0, 26] + p[c, 0, 27]) * 0.5f;
			p[c, 0, 18] = (p[c, 0, 28] + p[c, 0, 29] + p[c, 0, 30] + p[c, 0, 31]) * 0.25f;
			p[c, 0, 19] = (p[c, 0, 32] + p[c, 0, 33]) * 0.5f;
		}

		private static void MapValue20To34(float[,,] p, int c)
		{
			p[c, 0, 33] = p[c, 0, 19]; p[c, 0, 32] = p[c, 0, 19];
			p[c, 0, 31] = p[c, 0, 18]; p[c, 0, 30] = p[c, 0, 18]; p[c, 0, 29] = p[c, 0, 18]; p[c, 0, 28] = p[c, 0, 18];
			p[c, 0, 27] = p[c, 0, 17]; p[c, 0, 26] = p[c, 0, 17]; p[c, 0, 25] = p[c, 0, 16]; p[c, 0, 24] = p[c, 0, 16];
			p[c, 0, 23] = p[c, 0, 15]; p[c, 0, 22] = p[c, 0, 15]; p[c, 0, 21] = p[c, 0, 14]; p[c, 0, 20] = p[c, 0, 14];
			p[c, 0, 19] = p[c, 0, 13]; p[c, 0, 18] = p[c, 0, 12]; p[c, 0, 17] = p[c, 0, 11]; p[c, 0, 16] = p[c, 0, 10];
			p[c, 0, 15] = p[c, 0, 9]; p[c, 0, 14] = p[c, 0, 9]; p[c, 0, 13] = p[c, 0, 8]; p[c, 0, 12] = p[c, 0, 8];
			p[c, 0, 11] = p[c, 0, 7]; p[c, 0, 10] = p[c, 0, 6]; p[c, 0, 9] = p[c, 0, 5]; p[c, 0, 8] = p[c, 0, 5];
			p[c, 0, 7] = p[c, 0, 4]; p[c, 0, 6] = p[c, 0, 4]; p[c, 0, 5] = p[c, 0, 3];
			p[c, 0, 4] = (p[c, 0, 2] + p[c, 0, 3]) * 0.5f; p[c, 0, 3] = p[c, 0, 2]; p[c, 0, 2] = p[c, 0, 1];
			p[c, 0, 1] = (p[c, 0, 0] + p[c, 0, 1]) * 0.5f;
		}

		private static void ResetPhaseHistory(AacParametricStereo ps)
		{
			for (var band = 0; band < 17; band++)
			{
				ps.OpdHistory[band] = 0;
				ps.IpdHistory[band] = 0;
			}
		}

		private static void CopyRow(sbyte[,] destination, int destinationRow, sbyte[,] source, int sourceRow)
		{
			for (var band = 0; band < 34; band++)
				destination[destinationRow, band] = source[sourceRow, band];
		}
	}
}
