# File formats and previews

Version 0.14.1 scans the following file types. A recognized extension means the file is eligible for discovery and a fingerprint. Quick and Deep scans sample files larger than 1 MiB; small files and previously full-hashed paths receive full SHA-256. A thumbnail or poster also requires a readable, valid file and a supported encoding within that container.

| Family | Extensions | Saved gallery preview |
| --- | --- | --- |
| JPEG | JPG, JPEG, JPE, JFIF | Image thumbnail |
| Common raster images | PNG, GIF, BMP, WebP | Image thumbnail; first frame for GIF |
| Other raster images | AVIF, TIF, TIFF, HEIC, HEIF, ICO | Image thumbnail; first page/frame where applicable |
| SVG | SVG | Static thumbnail after resource and XML checks |
| Camera RAW | DNG, CR2, CR3, NEF, NRW, ARW, RAF, ORF, RW2, PEF, X3F | Embedded JPEG when one is found; no full sensor-data decoding |
| Video | MP4, M4V, MOV, AVI, WMV, MKV, WebM, MPG, MPEG, MPE, MTS, M2TS, TS | First available video frame as a saved poster |

**Quick** selects all listed image families except camera RAW, together with videos. This includes HEIC/HEIF, AVIF and TIFF. **Deep** adds RAW and includes AppData/Application Data. **File types** opens a menu of families; **Options** controls AppData and root-level file inclusion. Editing these choices switches to **Custom**. The gallery's type filter uses the same families. Resuming a scan retains its original saved types, AppData choice and age cutoff.

The decoder libraries are bundled privately. Every preview uses the same reusable isolated helper, including JPEG/PNG. Content signatures determine the decoder, so a supported image can be previewed when its extension describes a different format. Discovery still applies the selected file extensions first. HEIC/HEVC and video previewing do not require a Windows codec pack. The application saves the resulting JPEG thumbnails/posters in the database, so they remain available if the source disappears. Full video playback and full RAW development are not included.

An unavailable preview retains the fingerprinted record. Its placeholder and Log explain the failure. Start a fresh scan to retry its source after correcting an access or file problem. A file can remain undecodable even when its extension is listed; encrypted media, unusual codecs, malformed containers, absent RAW previews and resource limits can prevent a preview.

## Resource limits

- Gallery previews: at most 256 pixels on the longest side and 32 KiB encoded. Source inspection adapts to the viewport and zoom, with limits of 8,388,608 decoded pixels, an 8192-pixel longest side and 2 MiB encoded data. See [inspection controls and limits](INSPECTION.md).
- Images: at most 128 MiB input. JPEG/PNG source images up to 200 megapixels and 65,535 pixels per axis are eligible for bounded downsampling; decoding must still fit the helper's memory and time limits. Other image decoding retains its 32 megapixel guardrail. SVG XML: at most 4 MiB with bounded structure.
- JPEG dimension discovery checks segments incrementally within a 16 MiB metadata search bound. Invalid/truncated headers and resource-limit refusals have distinct errors.
- RAW: search at most the first 128 MiB; retain at most an 8 MiB JPEG candidate. These are extraction limits, not a full RAW decoder.
- Video/HEIF: at most 64 GiB source size for preview attempts, 4 MiB probing allowance and a 30-second total decoder-request budget. A preview guardrail does not cap ordinary scanning or database size.
- One reusable media helper and its codec child share a Windows job with a 384 MiB committed-memory limit and a two-program process limit. Windows can also attach console-host processes. A stalled request or cancellation stops the entire owned job; a replacement starts only after the job has no active processes.

Image/RAW decoding uses the reusable isolated helper; video/HEIF probing and rendering run in private FFmpeg children. This includes ordinary JPEG/PNG and contains decoder crashes while permitting cancellation without terminating the database owner.

SVG decoding prohibits DTDs, entities, processing instructions, scripts, foreign content and external resource references. Its buffer loader has no source base URL. FFmpeg receives explicit container/protocol selections with external MOV references disabled. These restrictions can reject a document that depends on external files. See the upstream [librsvg resource rules](https://gnome.pages.gitlab.gnome.org/librsvg/Rsvg-2.0/class.Handle.html#security-and-locations-of-referenced-files) and [FFmpeg MOV options](https://ffmpeg.org/ffmpeg-formats.html#mov_002fmp4_002f3gp).

I have checked generated fixtures for these image families, a real generated HEVC HEIF container, the listed video containers, long Unicode image/video paths, and RAW-like containers with embedded JPEGs. The RAW fixtures verify extraction, not compatibility with every camera model. Broader real-camera, codec-variant, network and multi-day validation remains open.
