# Changelog

## 0.14.0 — development build

- Maintenance exports up to 200 selected retained Non-compliant/Evidence originals as an encrypted `.mcrevidence` package. Stored ciphertext and native/final-HTML authentication identities remain intact; capture details, thumbnails and record identifiers stay protected.
- Open packages read only through Open or `-OpenEvidencePackage`, using their export-time originals password. Background authentication and full-original verification keep the interface responsive; lock clears protected views immediately.
- Explicit original export preserves existing destinations, writes plaintext only to the chosen new file and verifies its full SHA-256. Package export uses owned encrypted partial output, authentication before publication and operation reconciliation.

## 0.13.0 — development build

- Maintenance offers CSV and offline HTML reports for Non-compliant items. CSV keeps the final HTML application's 19 columns; HTML includes linked pages, saved raster previews and a complete companion CSV.
- Reports stream saved records without opening source files. Review changes and scan starts pause for a consistent committed revision; cached inspection and ordinary reads remain available.
- Evidence details are excluded by default. Explicit inclusion requires an unlocked originals vault and authenticates the retained original before writing plaintext details. Original media bytes are not included.
- Output stays in an owned partial destination until completion. Cancellation and output failures clean up identity-verified files; existing destinations are preserved. Spreadsheet-safe CSV presentation leaves database values unchanged.
- Cancel opening also cancels a database switch waiting for preferences to save, keeping the chooser unavailable until the current database closes.

## 0.12.0 — development build

- Each Windows database saves page size, gallery Fit/Fill, reviewer label and scan controls. Preferences restore before review controls become available; pending, committed and failed saves have separate status.
- Normal close and database switching wait for the last preference commit in the background. Failed saves retain the edited controls and offer Retry preferences. Passwords, keys and note drafts are excluded.
- Applying a source connection refreshes an open Details summary and recorded locations while preserving unsaved reviewer/notes text and rejecting older replies.

## 0.11.0 — development build

- Sources / reconnect checks moved or copied media before applying a replacement folder. Full matches, quick candidates, missing files and failures have separate outcomes; quick candidates and missing files require explicit acknowledgement.
- Interrupted checks resume from committed file outcomes. Applying a connection preserves recorded locations, reviews, Evidence and scan manifests. Resumed scans use the connected folder without revisiting completed users.
- Source inspection, preview recovery, verification and original retention use checked file identities and opened-file root checks. Native schema 8 upgrades supported older databases after a validated backup.

## 0.10.0 — development build

- Selected preview retries save bounded work and outcomes in the database. Interrupted work can be resumed or discarded from Preview tasks; completed scan users and review decisions stay unchanged.
- Existing good thumbnails survive failed retries. Changed sources and protected Evidence are refused, and refreshed preview versions reject older cached-image replies.
- Native schema 7 adds recovery state and preview versions. Supported upgrades from schemas 1–6 make a validated backup before changing the database.

## 0.9.0 — development build

- Details / notes displays saved identity and review metadata, with recorded locations in bounded pages of up to 100.
- Reviewer labels and notes can be saved independently of the bucket. Revision checks preserve drafts on conflicts; operation receipts reconcile a repeated save without duplicating its audit entry. Extended imported text remains preserved read-only.
- Previous/next item controls follow the visible gallery page in the source inspector and details window. Database changes and obsolete replies cannot repopulate closed details.

## 0.8.0 — development build

- Source inspection sizes previews for the window and zoom instead of a fixed 1024-pixel cap. Fit, centered Fill, +/−, Ctrl+wheel, 100% source scale and drag/scroll panning are available.
- Source and preview dimensions identify bounded sampling. Cached thumbnails remain usable when a source is unavailable; higher-quality current images are reused when zooming out.
- Superseded source work is cancelled, pending inspection requests/results retain only the latest item, and the private decoder protocol validates source and encoded image dimensions.

## 0.7.0 — development build

- Move selected items out of Evidence after authenticating every saved original. Decisions and history commit together; encrypted originals and display order are retained.
- Change the originals password from Maintenance, including imported HTML vaults. An authenticated key envelope avoids rewriting the saved media; earlier database copies keep their previous password.
- Schema 6 upgrades preserve interrupted retention targets and add authenticated legacy-original links for offline reuse without copying imported ciphertext.

## 0.6.0 — development build

- Non-compliant and Evidence actions retain full originals with verified SHA-256 in a password-protected vault. Quick candidates are fully verified first; collisions require explicit resolution.
- Chunked authenticated encryption, hidden staged captures, atomic review publication, cancellation, bounded incomplete-capture cleanup and committed-operation reconciliation.
- Originals password creation, unlock and lock controls; authenticated saved-original inspection and create-new full-resolution export. Lock closes protected views and clears returned secret buffers.
- Existing final-HTML v1/v2 Evidence passwords and encrypted records remain usable, including streamed large legacy payloads. Evidence cards remain available without a source location.
- Native schema 5, per-card review revisions, selected-review conflict protection, 2.5 GB/5 GB retention guardrails and interrupted-export ownership records.

## 0.5.0 — development build

- Background full-file verification, explicit candidate collision review, stable card promotion and new split cards. Existing verified decisions remain independent by source root.
- Verification reproduces the saved quick fingerprint through the same file handle as the full read before carrying a candidate decision forward. Unsupported historical fingerprint methods are refused.
- Bounded preparation with atomic publication, cancellation, stale-preview refusal and operation-ID recovery after a lost response.
- Identity history retains archived reviews and complete original notes. Previously reviewed content remains accessible when its last location becomes historical.
- Previews come from verified sources, with content-aware cache invalidation and protection against late thumbnails or inspector results.
- Native schema 4 upgrades preserve original backups, authenticated keys, row ownership, history and display sequence numbers. Large SQL migrations support cancellation and report their active stage.
- Protected Evidence aliases are indexed and checked before ordinary verification. Existing encrypted bytes and authenticated identifiers are preserved.

## 0.4.0 — development build

- Quick and Deep scans use sampled fingerprints above 1 MiB, with explicit candidate labels. Small files retain full SHA-256.
- Saved scans retain their hashing settings through cancellation and resume. Earlier native scans retain full hashing; imported final HTML scans retain their quick-hash choice and threshold.
- Rescanning previously full-hashed paths verifies their full contents again to preserve existing reviews and card order. Matching final HTML candidates preserve their original review and occurrence identities.
- Content identity includes file size, algorithm version and sampling parameters. Validated Windows Recycle Bin metadata is excluded while actual recycled media remains eligible.

## 0.3.0 — development build

- Open automatically recognizes final HTML databases and preserves the original while converting into a new local copy. Imported scans retain saved AppData and age filters with large metadata.
- Scan Options dropdown, Select page, Quick image coverage including HEIC/HEIF, Deep AppData inclusion, and About author/date/version with labeled expandable licenses.
- Unified isolated preview decoding, incremental JPEG metadata parsing, high-resolution JPEG/PNG downsampling, validated content routing and distinct malformed-header errors. Transient file-read codes survive helper responses for bounded retries.
- Native database upgrades with validated pre-upgrade snapshots, transactional version changes and preservation of committed reviews.
- Cancellable background backup and integrity checks, real progress, independent page readers and reliable shutdown when result queues are full.
- Streamed import of final HTML schema-4 metadata, long notes and legacy ciphertext. Validated completed imports are published separately; originals are preserved on success, cancellation and failure.
- Windows CNG authenticated-encryption foundations checked against the final HTML build's actual key/AAD functions, including large legacy records. Imported Evidence remains locked pending the native Evidence interface.
- Command-line HTML import, initialization cancellation and Unicode-correct PowerShell 5.1 interface labels.

## 0.2.0 — development build

- Image thumbnails for GIF, BMP, WebP, AVIF, TIFF, HEIC/HEIF, ICO and restricted static SVG, alongside JPEG/JFIF and PNG.
- Saved video posters for MP4/M4V, MOV, AVI, WMV, MKV, WebM, MPEG and transport-stream families; bounded embedded-JPEG extraction for camera RAW extensions.
- Quick/Deep presets, Custom family selection and gallery family filters. Imported scan configurations preserve image/video type selections.
- Reusable isolated complex-media decoder, private pinned LGPL FFmpeg binaries, memory/process/time limits, cancellation and crash recovery. Startup verifies the helper and codec versions offline.
- Single-file packaging includes the expanded decoder runtime. See [format coverage and limits](docs/FORMATS.md).

## 0.1.0 — development build

- Native WPF review interface for Windows PowerShell 5.1, packaged with private x64 SQLite and JPEG/PNG decoding dependencies in one offline script.
- Local authoritative WAL database, committed review feedback, stable pagination, and root-scoped duplicate decisions.
- Bounded per-user discovery/file processing, saved alphabetical manifest, cancellation, interrupted-work resume, and persistent thumbnails.
- Schema-4 HTML import/export foundation with preserved identifiers, history, previews, and opaque encrypted Evidence.
- Integrity checking, supported portable backup, bounded diagnostic log, and dependency notices.

Full HTML compatibility, native Evidence operations, ZIP scanning, and reports remain outside the current scope. See [current scope](docs/STATUS.md).
