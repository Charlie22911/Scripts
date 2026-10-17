# Third-party notices

The portable script embeds private native libraries. No codecs are registered with Windows. About displays the embedded notices offline.

| Component | Pinned build | Terms/source |
| --- | --- | --- |
| SQLite | 3.54.0, official x64 DLL | Public domain; [source archive](https://www.sqlite.org/2026/sqlite-src-3540000.zip) |
| libvips web/static-ffi | 8.18.7, x64 | Combined build uses LGPL-3.0-or-later with separately licensed dependencies; [exact build/source/license manifest](https://github.com/libvips/build-win64-mxe/tree/v8.18.7) |
| FFmpeg/ffprobe LGPL shared | n9.0.2-27-g7e93014c5b-20261014, x64 | LGPL-3.0-or-later with separately licensed dependencies; [pinned build](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-14-13-03), [FFmpeg source](https://github.com/FFmpeg/FFmpeg/tree/7e93014c5b) |

The native archive digests and extracted binary hashes are recorded in `vendor/dependencies.json` and `vendor/ffmpeg-lock.json`. The build embeds the upstream libvips version manifest/notices, FFmpeg lock data and LGPL license texts. The libvips web build omits its optional HEVC dependencies; the private FFmpeg runtime supplies HEVC decoding for supported HEIF/video files. No system codec is registered.

LGPL libraries remain separate DLLs after extraction. To use modified libraries, rebuild from the retained application source and update dependency hashes. This project does not restrict reverse engineering needed to debug modifications to those libraries. Preserve applicable notices and corresponding source/build access when redistributing a modified package.

The upstream static build includes AOM, Cairo, cgif, Expat, Fontconfig, FreeType, FriBidi, GLib, HarfBuzz, Highway, Little CMS, libarchive, libexif, libffi, libheif, the BSD libimagequant fork, libpng, librsvg, libtiff, libultrahdr, libwebp, libxml2, MozJPEG, Pango, Pixman, proxy-libintl, and zlib-ng. Refer to the pinned upstream manifest for each version, license, and source location.

This development package is for local evaluation. A complete transitive-notice and corresponding-source redistribution audit is still required before wider distribution; the single libvips license file alone is not the complete license set for its static dependency bundle.
