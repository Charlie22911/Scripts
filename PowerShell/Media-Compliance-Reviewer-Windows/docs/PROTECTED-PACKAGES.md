# Protected Evidence packages

A `.mcrevidence` package carries encrypted stored originals, their saved capture details and available protected thumbnails. Open it with the same portable reviewer, without a database or access to the original media folders. The package opens read only.

## Export selected originals

1. Finish or cancel scanning and wait for pending review saves to commit.
2. Select up to 200 current **Non-compliant** or **Evidence** items that already have retained originals. Use **Select page** when appropriate. Retain older items first if they have no stored original.
3. Choose **Maintenance → Export selected protected package**. Unlock the originals vault if prompted, then choose a new `.mcrevidence` filename.
4. Wait for the exported confirmation. Keep the originals password that was current when you exported the package.

The application authenticates stored originals and their full SHA-256 hashes, streams the existing ciphertext into the package, verifies the completed package and publishes it after successful completion. It does not reread source media. Review changes and scan starts pause while export runs; cached gallery inspection remains available.

Existing destinations are preserved. Cancellation and failure remove only an owned incomplete output whose file identity still matches. A forcibly interrupted export may leave a clearly named `.partial-…` file beside the chosen destination. Inspect it after the application closes before removing it. An export that already completed is reported as published even if its final database status cannot be saved.

## Open and inspect a package

Choose **Open** and select the `.mcrevidence` file, or start the portable script directly:

```powershell
powershell.exe -NoProfile -STA -File .\Media-Compliance-Reviewer-Windows-0.14.0.ps1 -OpenEvidencePackage C:\Reviews\Evidence.mcrevidence
```

Enter the package's originals password. The application authenticates the complete package before showing its item list. Select an item to verify its original bytes and full SHA-256 before viewing its saved details and thumbnail. Larger or unavailable previews do not remove the verified original. Long saved details have a bounded display; the complete encrypted records remain in the package.

**Export original** writes a verified full-resolution plaintext copy to an explicitly chosen new destination. It preserves existing files. The saved thumbnail is a review aid; it is not the full-resolution media. **Lock** immediately clears the displayed details, thumbnails and inventory. Close waits for the package's owned worker without blocking the interface. One package window can be open or closing at a time.

Original export authenticates the stored media before creating the chosen plaintext file and verifies the written copy before confirming success. It creates no plaintext temporary siblings. Cancellation or failure removes only the incomplete chosen file if its recorded identity still matches. If safe removal fails, the application reports the affected destination and shows a warning before normal close. A forced process shutdown can leave an incomplete file at that destination; inspect it before use.

Changing the database password later does not change previously exported packages. Each package retains its export-time password envelope. Password recovery is not available.

## What is protected

Original media, capture details, protected thumbnails and record identities remain encrypted. Packages preserve native captures and the final HTML build's supported monolithic and chunked encrypted Evidence. Password derivation and authenticated encryption use the existing Windows CNG implementation. A package-wide authenticated seal binds its header, ordered frames and complete ciphertext, detecting changes, missing records, reordering, truncation and appended data.

The outer header is not secret: it includes format/version, creation time, the workspace identifier, password-derivation/envelope parameters and item count. Framing exposes original sizes, chunk sizes and format identifiers. This is not whole-file encryption or encryption of the ordinary review database. Ordinary records and gallery thumbnails remain subject to the database's existing privacy limits.

CSV and HTML reports are separate plaintext exports. They do not contain a protected package or original media bytes. A password-protected package requires the portable reviewer; no browser component or online service is needed.

## Limits

Packages contain at most 200 selected originals. Original-retention guardrails still apply when capturing media; they do not become scan limits. Unsupported package versions and malformed or inconsistent frames are refused. Stored details display within 1 MiB of authenticated metadata, with bounded visible text and thumbnails. Legacy oversized encrypted metadata is preserved and can accompany a verified original even when its details cannot be displayed in full.

Package verification and export stream their payloads. Generated local checks cover supported native/final-HTML encryption, source-less reading, password rotation, changed and incomplete packages, cancellation, background ownership and WPF interaction. These checks do not certify network behavior, every endpoint protection policy or multi-day collection performance.
