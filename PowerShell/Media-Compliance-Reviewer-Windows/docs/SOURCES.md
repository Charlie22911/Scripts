# Reconnect a source

Use **Maintenance → Sources / reconnect** when recorded media has moved to another local folder, mapped drive or UNC share. The active SQLite database must stay on suitable local storage.

1. Select the recorded source. **Next roots** and **First roots** browse pages of ten sources. The window shows both the current location and the original recorded location.
2. Enter or browse to the replacement root. Keep the same relative user-folder layout beneath it.
3. Choose **Check folder**. The application reads recorded current locations in the background, with bounded memory. Full SHA-256 records require a full-file match. Quick records use their saved sampling method and remain candidate matches.
4. Review the counts and latest checked paths. Choose **Refresh** for current results. Log includes failed paths and transient read retries. A mismatched fingerprint, permission failure or unsupported fingerprint method prevents applying the connection. There must be at least one matching file.
5. If the check includes quick candidates or missing files, select the acknowledgement before choosing **Apply connection**. Quick equality does not prove identical complete contents. Applying a connection does not promote a candidate to a verified identity.

If an identity is promoted, split or otherwise changed after checking, discard that check and check again before applying. Ordinary reviewer/notes edits do not invalidate the identity check.

The new location becomes active only after the database commits the connection. Saved decisions, content identities, display order, authenticated Evidence and recorded source provenance remain intact. Old source inspection closes so it cannot keep presenting the previous location as current; cached gallery images remain available. An open Details window refreshes its source summary and recorded locations while preserving unsaved reviewer and notes text.

## Interrupted checks

Use **Cancel task** on the main window to stop unfinished work. Reopen Sources and choose **Resume check** after cancellation or reopening the database. Completed file checks are retained. An interrupted file is reopened and checked again. Blocking operating-system I/O may need to return before cancellation finishes; the application keeps ownership of that worker instead of starting duplicate reads.

**Discard check** removes an unapplied check in small database batches. It does not delete media, previews or reviews. Resume a cancelled discard by choosing Discard again. At most twenty unfinished checks are retained, with one per root. An active connection cannot be discarded; check and apply a replacement location instead. Older superseded check rows are removed in bounded batches when checking that root again.

## Resume a scan

Cancel scanning and wait for its workers before checking or applying a connection. Afterwards, **Resume scan** uses the new location with the saved manifest and pending jobs. It does not revisit completed users or rewrite the original scan settings. Files newly read during that resumed scan receive their own source observations, keeping them separate from untouched earlier locations.

A fresh scan at the connected location reuses the recorded root and can find additions. An old path already connected elsewhere is refused as a new scan destination. Another recorded root cannot silently be merged into the same current location.

## Limits

Checks cover recorded current ordinary media locations. They do not enumerate new files or authenticate stored Evidence. Evidence-only roots and roots with no matching ordinary files cannot establish a checked connection. Stored Evidence remains usable without its source.

A missing file remains unavailable through that connection even if a different file later appears at its path. Use a fresh scan or a new connection check before using it. Relevant changes after checking also prevent inspection, preview recovery or original retention from accepting a stale file identity. Opened-file root checks refuse junctions that redirect media outside the selected root.

The window displays the latest twenty file outcomes, with totals for the complete check. It does not retain all paths or file contents in memory. Actual large-share performance and long blocked-network behavior still need field measurements; generated local checks do not establish that scale.

Native schema 8 adds source connections and per-file observations. Opening a supported older native database first makes a validated pre-upgrade backup. Keep that backup for use with an earlier application version.
