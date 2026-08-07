# Ported from FFmpeg

This document maps the managed source families in `Ffmpeg.CsPort.Decoder` to the FFmpeg C baseline used for the translation.

## Pinned baseline

- Version reported by the reference build: `n8.1.2-34-g9b6c8969e0-20260731`
- Full commit: `9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`
- Source tree: <https://github.com/FFmpeg/FFmpeg/tree/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b>
- Translation/adaptation notice date: 2026-08-06
- License: `LGPL-2.1-or-later`

Paths below are relative to that exact FFmpeg source tree. Architecture-specific assembly/SIMD, encoder, GPL, and nonfree paths are not part of the managed port.

## Foundation and infrastructure

### `Audio/**`, `Infrastructure/**`, and root port metadata

The public managed types mirror the portions of FFmpeg's codec identifiers, frame/sample format, and error model used by the port. `PortInformation.cs` and `Properties/AssemblyInfo.cs` are port-support metadata without a direct C-file counterpart.

- `libavcodec/codec_id.h`
- `libavutil/error.h`
- `libavutil/frame.h`
- `libavutil/samplefmt.h`

### `Bitstream/**`

- `libavcodec/bitstream.c`
- `libavcodec/bitstream.h`
- `libavcodec/get_bits.h`
- `libavcodec/golomb.c`
- `libavcodec/golomb.h`
- `libavcodec/put_bits.h`
- `libavcodec/vlc.c`
- `libavcodec/vlc.h`

### `Channels/**`

- `libavutil/channel_layout.c`
- `libavutil/channel_layout.h`

### `Mathematics/**`

- `libavutil/common.h`
- `libavutil/eval.c`
- `libavutil/intmath.h`
- `libavutil/mathematics.c`
- `libavutil/mathematics.h`
- `libavutil/rational.c`
- `libavutil/rational.h`

### `Transforms/**`

- `libavutil/tx.c`
- `libavutil/tx.h`
- `libavutil/tx_float.c`
- `libavutil/tx_priv.h`
- `libavutil/tx_template.c`

### `Windows/**`

- `libavcodec/kbdwin.c`
- `libavcodec/kbdwin.h`
- `libavcodec/sinewin.c`
- `libavcodec/sinewin.h`

## Codecs

### `Codecs/Aac/**`

- `libavcodec/aac/aacdec.c`
- `libavcodec/aac/aacdec.h`
- `libavcodec/aac/aacdec_dsp_template.c`
- `libavcodec/aac/aacdec_float.c`
- `libavcodec/aac/aacdec_float_coupling.h`
- `libavcodec/aac/aacdec_float_prediction.h`
- `libavcodec/aac/aacdec_latm.h`
- `libavcodec/aac/aacdec_proc_template.c`
- `libavcodec/aac/aacdec_tab.c`
- `libavcodec/aac/aacdec_tab.h`
- `libavcodec/aacps.c`
- `libavcodec/aacps.h`
- `libavcodec/aacps_common.c`
- `libavcodec/aacps_float.c`
- `libavcodec/aacps_tablegen.h`
- `libavcodec/aacps_tablegen_template.c`
- `libavcodec/aacpsdata.c`
- `libavcodec/aacpsdsp_float.c`
- `libavcodec/aacpsdsp_template.c`
- `libavcodec/aacpsdsp.h`
- `libavcodec/aacsbr.c`
- `libavcodec/aacsbr.h`
- `libavcodec/aacsbr_template.c`
- `libavcodec/aacsbrdata.h`
- `libavcodec/aactab.c`
- `libavcodec/aactab.h`
- `libavcodec/adts_header.c`
- `libavcodec/adts_header.h`
- `libavcodec/mpeg4audio.c`
- `libavcodec/mpeg4audio.h`
- `libavcodec/sbrdsp.c`
- `libavcodec/sbrdsp.h`

### `Codecs/Ac3/**`

- `libavcodec/ac3.c`
- `libavcodec/ac3.h`
- `libavcodec/ac3_parser.c`
- `libavcodec/ac3_parser.h`
- `libavcodec/ac3_parser_internal.h`
- `libavcodec/ac3dec.c`
- `libavcodec/ac3dec.h`
- `libavcodec/ac3dec_data.c`
- `libavcodec/ac3dec_data.h`
- `libavcodec/ac3dec_float.c`
- `libavcodec/ac3defs.h`
- `libavcodec/ac3dsp.c`
- `libavcodec/ac3dsp.h`
- `libavcodec/ac3tab.c`
- `libavcodec/ac3tab.h`
- `libavcodec/eac3_data.c`
- `libavcodec/eac3_data.h`
- `libavcodec/eac3dec.c`

### `Codecs/Adpcm/**`

- `libavcodec/adpcm.c`
- `libavcodec/adpcm.h`
- `libavcodec/adpcm_data.c`
- `libavcodec/adpcm_data.h`
- `libavcodec/g726.c`
- `libavcodec/bytestream.h`

### `Codecs/Alac/**`

- `libavcodec/alac.c`
- `libavcodec/alac_data.c`
- `libavcodec/alac_data.h`
- `libavcodec/alacdsp.c`
- `libavcodec/alacdsp.h`

### `Codecs/Als/**`

- `libavcodec/alsdec.c`

### `Codecs/Amr/**`

- `libavcodec/acelp_filters.c`
- `libavcodec/acelp_filters.h`
- `libavcodec/acelp_pitch_delay.c`
- `libavcodec/acelp_pitch_delay.h`
- `libavcodec/acelp_vectors.c`
- `libavcodec/acelp_vectors.h`
- `libavcodec/amr.h`
- `libavcodec/amrnbdata.h`
- `libavcodec/amrnbdec.c`
- `libavcodec/amrwbdata.h`
- `libavcodec/amrwbdec.c`
- `libavcodec/celp_filters.c`
- `libavcodec/celp_filters.h`
- `libavcodec/celp_math.c`
- `libavcodec/celp_math.h`
- `libavcodec/lsp.c`
- `libavcodec/lsp.h`

### `Codecs/Ape/**`

- `libavcodec/apedec.c`

### `Codecs/Dca/**`

- `libavcodec/dca.c`
- `libavcodec/dca.h`
- `libavcodec/dca_core.c`
- `libavcodec/dca_core.h`
- `libavcodec/dca_exss.c`
- `libavcodec/dca_exss.h`
- `libavcodec/dca_lbr.c`
- `libavcodec/dca_lbr.h`
- `libavcodec/dca_syncwords.h`
- `libavcodec/dca_xll.c`
- `libavcodec/dca_xll.h`
- `libavcodec/dcaadpcm.c`
- `libavcodec/dcaadpcm.h`
- `libavcodec/dcadec.c`
- `libavcodec/dcadec.h`
- `libavcodec/dcadata.c`
- `libavcodec/dcadata.h`
- `libavcodec/dcadct.c`
- `libavcodec/dcadsp.c`
- `libavcodec/dcadsp.h`
- `libavcodec/dcahuff.c`
- `libavcodec/dcahuff.h`
- `libavcodec/dcamath.h`
- `libavcodec/synth_filter.c`
- `libavcodec/synth_filter.h`

### `Codecs/Flac/**`

- `libavcodec/flac.c`
- `libavcodec/flac.h`
- `libavcodec/flacdata.c`
- `libavcodec/flacdata.h`
- `libavcodec/flacdec.c`
- `libavcodec/flacdsp.c`
- `libavcodec/flacdsp.h`
- `libavcodec/flacdsp_lpc_template.c`
- `libavcodec/flacdsp_template.c`
- `libavutil/crc.c`
- `libavutil/crc.h`

### `Codecs/GsmMicrosoftDecoder.cs`

- `libavcodec/msgsmdec.c`

### `Codecs/MpegAudio/**`

- `libavcodec/mpegaudio.c`
- `libavcodec/mpegaudio.h`
- `libavcodec/mpegaudiodata.c`
- `libavcodec/mpegaudiodata.h`
- `libavcodec/mpegaudiodec_common.c`
- `libavcodec/mpegaudiodec_common_tablegen.c`
- `libavcodec/mpegaudiodec_common_tablegen.h`
- `libavcodec/mpegaudiodec_float.c`
- `libavcodec/mpegaudiodec_template.c`
- `libavcodec/mpegaudiodecheader.c`
- `libavcodec/mpegaudiodecheader.h`
- `libavcodec/mpegaudiodsp.c`
- `libavcodec/mpegaudiodsp.h`
- `libavcodec/mpegaudiodsp_data.c`
- `libavcodec/mpegaudiodsp_float.c`
- `libavcodec/mpegaudiodsp_template.c`

### `Codecs/Opus/**`

- `libavcodec/opus/celt.c`
- `libavcodec/opus/celt.h`
- `libavcodec/opus/dec.c`
- `libavcodec/opus/dec_celt.c`
- `libavcodec/opus/dsp.c`
- `libavcodec/opus/dsp.h`
- `libavcodec/opus/opus.h`
- `libavcodec/opus/parse.c`
- `libavcodec/opus/parse.h`
- `libavcodec/opus/parser.c`
- `libavcodec/opus/pvq.c`
- `libavcodec/opus/pvq.h`
- `libavcodec/opus/rc.c`
- `libavcodec/opus/rc.h`
- `libavcodec/opus/silk.c`
- `libavcodec/opus/silk.h`
- `libavcodec/opus/tab.c`
- `libavcodec/opus/tab.h`

### `Codecs/PcmDecoder.cs`

- `libavcodec/pcm.c`

### `Codecs/Vorbis/**`

- `libavcodec/vorbis.c`
- `libavcodec/vorbis.h`
- `libavcodec/vorbis_data.c`
- `libavcodec/vorbis_data.h`
- `libavcodec/vorbisdec.c`
- `libavcodec/vorbisdsp.c`
- `libavcodec/vorbisdsp.h`

### `Codecs/WavPackDecoder.cs`

- `libavcodec/wavpack.c`
- `libavcodec/wavpack.h`
- `libavcodec/wavpackdata.c`

### `Codecs/Wma/**`

- `libavcodec/wma.c`
- `libavcodec/wma.h`
- `libavcodec/wma_common.c`
- `libavcodec/wma_common.h`
- `libavcodec/wma_freqs.c`
- `libavcodec/wma_freqs.h`
- `libavcodec/wmadata.h`
- `libavcodec/wmadec.c`
- `libavcodec/wmalosslessdec.c`
- `libavcodec/wmaprodata.h`
- `libavcodec/wmaprodec.c`
- `libavcodec/wmavoice.c`
- `libavcodec/wmavoice_data.h`

## Demuxers and container support

### `Formats/Ac3RawDemuxer.cs`

- `libavformat/ac3dec.c`
- `libavformat/rawdec.c`

### `Formats/AiffDemuxer.cs`

- `libavformat/aiffdec.c`

### `Formats/AmrRawDemuxer.cs`

- `libavformat/amr.c`

### `Formats/ApeDemuxer.cs`

- `libavformat/ape.c`

### `Formats/AsfAudioDemuxer.cs`

- `libavformat/asf.c`
- `libavformat/asf.h`
- `libavformat/asfdec_f.c`

### `Formats/AuAudioDemuxer.cs`

- `libavformat/au.c`

### `Formats/CafAudioDemuxer.cs`

- `libavformat/caf.c`
- `libavformat/caf.h`
- `libavformat/cafdec.c`

### `Formats/DtsRawDemuxer.cs`

- `libavformat/dtsdec.c`
- `libavformat/rawdec.c`

### `Formats/FlacDemuxer.cs`

- `libavformat/flacdec.c`
- `libavformat/rawdec.c`

### `Formats/MatroskaAudioDemuxer.cs`

- `libavformat/matroska.c`
- `libavformat/matroska.h`
- `libavformat/matroskadec.c`

### `Formats/MovAudioDemuxer.cs`

- `libavformat/isom.c`
- `libavformat/isom.h`
- `libavformat/mov.c`
- `libavformat/mov_chan.c`
- `libavformat/mov_chan.h`
- `libavformat/mov_esds.c`

### `Formats/MpegAudioDemuxer.cs`

- `libavformat/mp3dec.c`

### `Formats/MpegTsAudioDemuxer.cs`

- `libavformat/mpegts.c`

### `Formats/OggAudioDemuxer.cs`

- `libavformat/oggdec.c`
- `libavformat/oggdec.h`
- `libavformat/oggparseflac.c`
- `libavformat/oggparseopus.c`
- `libavformat/oggparsevorbis.c`

### `Formats/PcmFormat.cs`

- `libavformat/pcm.c`
- `libavformat/pcm.h`

### `Formats/RawAacDemuxer.cs`

- `libavformat/aacdec.c`
- `libavformat/rawdec.c`

### `Formats/WaveDemuxer.cs` and `Formats/Wave64Demuxer.cs`

- `libavformat/riff.c`
- `libavformat/riff.h`
- `libavformat/riffdec.c`
- `libavformat/w64.c`
- `libavformat/w64.h`
- `libavformat/wavdec.c`

### `Formats/WavPackDemuxer.cs`

- `libavformat/wv.c`
- `libavformat/wv.h`
- `libavformat/wvdec.c`

### Other `Formats/**` support types

`AudioStreamInfo.cs`, `DemuxedAudioPacket.cs`, `FormatReader.cs`, and `ISeekableAudioDemuxer.cs` are managed support types that adapt the relevant FFmpeg `AVFormatContext`, `AVStream`, `AVPacket`, `AVIOContext`, and seeking semantics used by the mapped demuxers.

- `libavformat/avformat.h`
- `libavformat/avio.h`
- `libavformat/demux.h`

## Resampling

### `Resampling/**`

- `libswresample/audioconvert.c`
- `libswresample/dither.c`
- `libswresample/noise_shaping_data.c`
- `libswresample/options.c`
- `libswresample/rematrix.c`
- `libswresample/rematrix_template.c`
- `libswresample/resample.c`
- `libswresample/resample.h`
- `libswresample/resample_dsp.c`
- `libswresample/resample_template.c`
- `libswresample/swresample.c`
- `libswresample/swresample.h`
- `libswresample/swresample_internal.h`

## Change summary

The upstream C implementation was translated to C#, converted to managed arrays/spans and managed stream access, and organized behind managed decoder/demuxer APIs. Architecture-specific assembly and external-library paths were omitted. Arithmetic order, tables, bitstream semantics, return codes, and state transitions are intentionally kept close to the scalar FFmpeg reference and are covered by the conformance test project.

When a file is rebased or new FFmpeg code is ported, update the pinned baseline or add the precise additional commit, extend this mapping, reproduce the new original copyright notice in `UPSTREAM-COPYRIGHTS.txt`, retain the LGPL headers, and rerun the full conformance suite.
