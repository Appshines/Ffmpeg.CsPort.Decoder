# Ffmpeg.CsPort.Decoder

`Ffmpeg.CsPort.Decoder` is an independent, fully managed C# port of selected FFmpeg audio-decoding, demuxing, transform, channel-layout, mathematics, bitstream, and resampling code. It does not load native FFmpeg libraries at runtime.

The port is based on FFmpeg commit [`9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`](https://github.com/FFmpeg/FFmpeg/tree/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b), identified by the reference build as `n8.1.2-34-g9b6c8969e0-20260731`.

This repository contains the performance-optimized implementation. It preserves the assembly identity and public contract of the original managed port and remains a bit-exact replacement for it.

## Performance and validation

The optimized implementation was benchmarked on Windows x64 with alternating isolated processes and stable-run medians. The additional v2 campaign improved representative steady-state throughput over the preceding optimized state as follows:

| Codec | Additional v2 throughput | Approximate total throughput over the original baseline |
|---|---:|---:|
| FLAC | +15.63% | +19.75% |
| AAC-LC | +8.18% | +20.65% |
| MP3 | +2.88% to +4.95% | +13.22% to +13.50% |
| Vorbis | +2.05% | +28.30% |
| HE-AAC/PS | +0.83% | +26.83% |
| Opus | +0.03% | +7.66% |
| HE-AAC | -0.59% | +44.04% |

The total figures combine the sequentially measured original-to-optimized and v2 throughput factors; they are not a weighted real-world workload average. Results depend on CPU, runtime, input and warm-up state. AVX2 acceleration is used only where supported, with the complete scalar path retained as a fallback.

Correctness gates passed with 277/277 identical production-path PCM fingerprints and complete standard/optimized FFmpeg 8.1.2 conformance runs over 271 files. Both variants completed with zero deviations and the same 62 expected nonzero reference exits.

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
