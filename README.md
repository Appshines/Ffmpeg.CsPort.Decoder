# Ffmpeg.CsPort.Decoder

`Ffmpeg.CsPort.Decoder` is an independent, fully managed C# port of selected FFmpeg audio-decoding, demuxing, transform, channel-layout, mathematics, bitstream, and resampling code. It does not load native FFmpeg libraries at runtime.

The port is based on FFmpeg commit [`9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`](https://github.com/FFmpeg/FFmpeg/tree/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b), identified by the reference build as `n8.1.2-34-g9b6c8969e0-20260731`.

## License

The combined library is distributed under the **GNU Lesser General Public License version 2.1 or later** (`LGPL-2.1-or-later`). It is not `LGPL-3.0-only`. Version 3 may be selected under the “or later” clause, but the upstream baseline and this port retain the more precise `LGPL-2.1-or-later` designation. Package metadata uses `LGPL-2.1-or-later AND MIT AND BSL-1.0` so the additional component licenses below remain visible.

Three mapped Ogg source files retain their MIT terms, and the mapped Bessel implementation retains the Boost Software License 1.0 in addition to LGPL. Their complete notices are preserved in the affected C# files and in `UPSTREAM-COPYRIGHTS.txt`.

- The complete LGPL 2.1 text is in [COPYING.LGPLv2.1](COPYING.LGPLv2.1).
- Attribution, modification, trademark, and independence notices are in [NOTICE.md](NOTICE.md).
- The managed-to-upstream source mapping is in [PORTED-FROM-FFMPEG.md](PORTED-FROM-FFMPEG.md).
- Original upstream copyright notices are reproduced in [UPSTREAM-COPYRIGHTS.txt](UPSTREAM-COPYRIGHTS.txt).

## Distribution requirements

Anyone distributing this library, including as part of another application, must independently comply with the LGPL. At minimum:

1. Keep the source headers, copyright notices, license text, and notices intact.
2. Make the complete corresponding source of this modified C# library available under `LGPL-2.1-or-later`, including build files and generated table sources.
3. Include `COPYING.LGPLv2.1`, `NOTICE.md`, `PORTED-FROM-FFMPEG.md`, and `UPSTREAM-COPYRIGHTS.txt` with binary distributions.
4. Keep the library replaceable or otherwise provide the materials and permissions required for recipients to modify the library and relink/recombine the application. In the InstantDj distribution this means retaining it as a separate assembly and not merging it into a single-file bundle, trimming away replaceability, or statically embedding it through NativeAOT without another compliant relinking mechanism.
5. Do not impose terms that prohibit reverse engineering for debugging modifications of this LGPL-covered library.
6. Mark later modifications with their nature and date, and update the upstream mapping when code is rebased or newly ported.

Publishing this complete project on GitHub is useful source availability, but a binary distributor must also ensure that the source offer/access and notices remain valid for that particular distribution channel. Codec patent rules and other local laws are separate from copyright-license compliance.

This checklist is technical compliance documentation, not legal advice. A qualified open-source licensing lawyer should review the final repository, application EULA, download pages, packaging, and binary distribution process before release.
