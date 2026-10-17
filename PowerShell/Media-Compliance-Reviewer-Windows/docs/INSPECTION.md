# Inspect images and video posters

Choose **Inspect** on a card. The saved thumbnail appears immediately when it is cached, while a background worker requests a source preview. Opening another item clears the previous image and starts in Fit mode. A late result from an older item, window size or zoom level cannot replace the current view.

## Viewing controls

- **Fit** shows the whole image within the available viewing area. Resize the window to use more space.
- **Fill** covers the viewing area and crops around the center without changing the aspect ratio.
- **+** and **−** change zoom. **Ctrl+mouse wheel** provides the same controls.
- **100%** uses source pixel scale. Scroll or drag the image to pan when it is larger than the viewing area.
- **Close** releases the inspector image and cancels its pending source work.

**Previous item** and **Next item** follow the current gallery page and stop at its boundaries. Use the gallery page controls to browse another page. **Details / notes** opens the saved metadata and recorded locations separately, so database work does not replace the image you are inspecting. Evidence and source-less retained originals keep their authenticated stored-inspection controls.

## Details, locations and notes

Choose **Details / notes** on any card to see its bucket, source root, size, fingerprint method, digest, sampling parameters, preview state and last saved review time. Quick fingerprints identify candidates; matching samples do not verify identical files. Ordinary metadata, reviewer labels and notes remain unencrypted.

Locations load in pages of at most 100 records, with a separate limit on retained page bytes. **Next 100 locations** replaces the displayed page; **Reload / discard edits** returns to the beginning. Paths are relative to the displayed root. Historical locations were superseded; recorded paths are not live availability checks. A retained original may have no recorded source path.

Edit the reviewer label and notes, then choose **Save notes**. This saves the current review text without moving the item to another bucket, changing its publication order, or changing an encrypted original. Saving is pending until the database confirms its commit. A conflicting review or changed identity refuses the save and retains your draft. Copy the draft if needed before reloading. Navigation waits for a save or an explicit reload; closing asks before discarding unsaved edits.

The editor accepts up to 128 reviewer characters and 16,384 note characters; some emoji count as two characters in these limits. Extended imported fields, longer existing text and text containing unsupported characters are read-only. A bounded display is shown and the complete original text is preserved; the application does not save a shortened copy over it. Stored null characters appear as a visible null symbol. New notes cannot contain null characters.

The size calculation accounts for the display scaling reported by WPF. Source inspection has no fixed 1024-pixel preview cap. Larger windows and closer zoom can request larger previews. A sharper image already loaded for the same item is reused when zooming out. Resize requests are combined before decoding, and superseded work is cancelled.

The dimensions beside the controls distinguish the source from the decoded preview. A 100% display scale does not promise full-resolution detail when the preview is sampled. The view labels sampling caused by preview limits.

## Preview limits and unavailable sources

Source previews have a limit of 8,388,608 decoded pixels, an 8192-pixel longest side and 2 MiB encoded data. Decoded pixel storage is approximately 32 MiB at four bytes per pixel. The displayed image and one pending result have separate bounded ownership; gallery thumbnails keep their existing 256-pixel/32-KiB limits. Source input limits and the private decoder's memory/process limits still apply. These limits do not restrict fingerprinting or retained-original storage.

Video inspection shows a poster, not playback. Its display dimensions account for the poster aspect ratio; zoom does not add moving video. Camera RAW inspection uses an embedded JPEG when available.

If a source is missing, changed or cannot be decoded, the saved thumbnail remains usable. The status explains the source failure. Transient reads receive an initial attempt and three bounded retries; cancellation interrupts retry waits. Inspect the card again to request a fresh attempt. If the source changed after fingerprinting, rescan it before generating a new source preview.

Evidence and retained originals with no source-location record open authenticated stored inspection. That inspector displays the saved thumbnail; **Export original...** retrieves the verified full original. It does not decrypt originals into temporary preview files. See [retention and Evidence](EVIDENCE.md).
