# Offline reports

Choose **Maintenance → Export review report** after the scan finishes or its cancellation completes. Reports include every current Non-compliant group and its active recorded locations, independently of gallery filters or selection. A group without an active location receives an explicit empty-location row.

Choose **CSV file** or **Offline HTML folder + complete CSV**, then enter a new destination. Existing files and folders are never overwritten. For HTML, Browse chooses a parent folder and suggests a new report folder. Open `index.html` from the completed folder and follow its report-page links. Keep the folder together when moving it.

HTML pages contain at most 128 location cards each, with saved raster previews when available. They use no scripts, remote fonts or external resource URLs. HTML shows bounded text with a continuation notice; `report.csv` contains complete ordinary reviewer, notes and path fields, including streamed long imported text. Saved previews can be excluded. Unsupported or unavailable previews keep their report records.

CSV retains these columns from the final HTML build: Status, Hash method, Hash value, Full SHA-256, Root, Relative path, Display path, Media kind, Extension, Duration seconds, Size, Last modified, Archive path, Archive member, Original connected, Reviewed UTC, Reviewer, Notes and Last scan ID. Duration and archive fields are populated when preserved metadata is available; native ZIP scanning is not available. Unsupported oversized projected archive metadata stops export with an error rather than publishing truncated fields.

**Original connected** reads **Not checked**. Reports do not probe network shares or open source media. Recorded paths and timestamps describe saved data, not live availability. Reconnected ordinary media uses its current source path. Full SHA-256 remains distinct from a quick candidate fingerprint; sampled equality does not prove identical files.

Leave spreadsheet protection enabled unless you need raw CSV text. It prefixes an apostrophe when a value could be treated as a spreadsheet formula, including dangerous leading whitespace. This changes report presentation only. CSV quotes preserve commas, quotation marks, Unicode and newlines.

## Evidence and privacy

Reports are unencrypted. Ordinary review fields and saved previews are already ordinary database metadata. **Also include Evidence details** is an explicit additional choice: unlock the originals vault, then export. The application authenticates each saved original and its full hash before using its protected capture metadata. A missing source does not prevent this verification. Incorrect authorization or corrupted protected data prevents report publication.

Included Evidence details and optional protected thumbnails become plaintext in the report. Original media bytes and encrypted-original packages are not included. Evidence uses its authenticated capture snapshot, which can differ from later ordinary notes or source connections. Protected metadata exceeding the 1 MiB report display limit, or lacking readable saved locations, is refused; the encrypted records remain preserved.

## Progress, cancellation and interrupted output

Reports use one committed database revision. Review saves, note saves and scan starts pause while export is active. Current cached images and ordinary inspection remain usable. Activity shows actual locations written or original bytes authenticated. Choose **Cancel task** to stop further work; an already blocked operating-system read or write must return before its owned worker can finish.

Output is written to a uniquely named `.partial-…` destination beside the chosen destination and published after successful completion. Cancellation or a write failure removes only files whose recorded identities still match. Cleanup never recursively deletes a directory or removes unrelated files. If the process is forcibly stopped, its clearly named partial output can remain. Consult **Log** for the destination, partial path and stopped/completed outcome. If the final log or database status cannot be saved after publication, the message identifies the exported destination and the logging failure. Inspect an abandoned partial after closing the application before removing it; a running instance may still own its files.

Generated checks cover saved data, escaping, multiple location pages, cancellation after writes, output-failure injection, WPF interaction and authenticated Evidence. Browser visual verification and realistic large/network report measurements remain outstanding. Output-failure injection is not a measurement of an actual full disk.
