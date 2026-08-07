# Notices for Ffmpeg.CsPort.Decoder

## FFmpeg-derived work

This project contains a C# translation and modification of source code from the FFmpeg project.

- Upstream project: <https://ffmpeg.org/>
- Upstream repository: <https://github.com/FFmpeg/FFmpeg>
- Pinned commit: [`9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`](https://github.com/FFmpeg/FFmpeg/tree/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b)
- Reference version: `n8.1.2-34-g9b6c8969e0-20260731`
- Combined-work license: `LGPL-2.1-or-later`
- Additional component licenses: `MIT` and `BSL-1.0`

The C-to-C# translation and managed-runtime adaptations were created during 2026. Every C# source file is marked as created or modified for this port on 2026-08-06. Later changes must retain the existing notices and add an accurate change description and date where required by the LGPL.

The exact source-family mapping is recorded in `PORTED-FROM-FFMPEG.md`. Copyright statements from the mapped upstream files are reproduced in `UPSTREAM-COPYRIGHTS.txt`; copyright remains with the respective upstream and port contributors.

## License classification

FFmpeg's `LICENSE.md` at the pinned commit states that most FFmpeg files are licensed under LGPL version 2.1 or later and that compatible MIT/X11/BSD-style files are included in the combined LGPL work. The per-file audit of the 263 mapped upstream files found:

- 259 files with an LGPL 2.1-or-later notice;
- 3 Ogg demuxer files under the MIT license (`libavformat/oggdec.c`, `libavformat/oggdec.h`, and `libavformat/oggparsevorbis.c`); and
- `libavutil/mathematics.c` under LGPL 2.1-or-later with its Bessel implementation additionally subject to the Boost Software License 1.0.

The full MIT notice is retained in `Formats/OggAudioDemuxer.cs`, and the full Boost notice is retained in `Mathematics/FfmpegMath.cs`. Both are also reproduced from upstream in `UPSTREAM-COPYRIGHTS.txt`. These permissive components are compatible with distribution of the combined port under `LGPL-2.1-or-later` but their own notices remain in force.

The port does not map any of FFmpeg's optional GPL production files, including `libavcodec/x86/flac_dsp_gpl.asm` and `libavcodec/x86/idct_mmx.c`, nor the GPL test program `libswresample/tests/swresample.c`.

If code is later ported from an FFmpeg file with GPL, nonfree, or different third-party terms, this project's license classification must be reassessed before that code is merged or distributed. Adding a source file to the mapping is not sufficient by itself.

## Independence and trademark

This project is independent and is not affiliated with, sponsored by, or endorsed by the FFmpeg project or its contributors. “FFmpeg” is a trademark of Fabrice Bellard. The namespace spelling `Ffmpeg` follows the requested C# identifier convention; prose and attribution use the official spelling `FFmpeg`.

## No warranty

This library is distributed in the hope that it will be useful, but without any warranty, including the implied warranties of merchantability or fitness for a particular purpose. See `COPYING.LGPLv2.1` for the controlling terms.
