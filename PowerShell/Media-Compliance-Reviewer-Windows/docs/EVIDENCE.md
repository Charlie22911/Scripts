# Retained originals and Evidence

Non-compliant and Evidence actions save an encrypted original in the local database. Both use the same password-protected vault. Scanning and capture read source files without moving, modifying or deleting them.

## Retain an original

1. Select cards and enter the reviewer label and notes.
2. Click **Non-compliant**, or **Evidence** for the protected bucket.
3. Create an originals password if this database has no vault, or enter its existing password. Keep it safe; it cannot be recovered.
4. Wait for the commit acknowledgement. Pending status appears immediately; the review changes after every selected original has been verified and saved.

New Non-compliant actions require successful retention. A quick candidate is fully verified first. If its locations contain different files or match an existing verified group, use **Verify full** to review and resolve the grouping before capture. A failed action preserves the previous decisions and selection.

Previously imported or older Non-compliant records may contain only a decision and thumbnail. Select them and click **Non-compliant** again to retain their originals. An already retained original can enter Evidence while its source is offline. Reclassifying an ordinary card does not automatically delete its saved original.

## Inspect, lock and export

Select one card and click **Stored original**. Inspection authenticates the encrypted data, checks chunk order and size, and verifies full SHA-256 before displaying capture details. The displayed image is the saved thumbnail. **Export original...** saves the full original, including its original resolution, to a new file you explicitly choose. Existing files are never overwritten.

Evidence uses protected placeholder cards, including when a source location is unavailable. Clear search/type/folder filters to see those cards. Unlock and inspect a card to read its authenticated details.

To move items out of Evidence, select them and choose **To be reviewed**, **Compliant** or **Non-compliant**. Enter the originals password when prompted. Every selected original must authenticate and pass its full SHA-256 check before the decisions and history commit together. A failed or cancelled action preserves the selected reviews. Reclassification keeps the encrypted originals, identifiers and display order. Imported groups keep their existing location grouping; authentication verifies the stored original and does not establish that every current source location contains the same bytes.

An original without a source-location record remains visible as a **Stored original** card after reclassification. Clear search/type/folder filters to see it. **Inspect** opens authenticated stored inspection for that card; no replacement source path is created.

Click **Lock originals** to close protected views, cancel protected work and clear returned password-authorized byte buffers. Opening another database or closing the application releases the vault key. Ordinary source inspection and cached gallery thumbnails are separate from protected original inspection.

Exports are decrypted files. Choose their destination deliberately. The application verifies the original before creating an output file, verifies the streamed output, and confirms success after recording completion. Cancellation removes only the newly created file whose identity still matches the export. If completion cannot be confirmed, the file is retained for inspection. Log reports interrupted exports on the next opening; inspect their chosen destinations before retrying.

## Limits and interrupted captures

**Maintenance** offers a projected-database guardrail of **2.5 GB** or **5 GB** for retaining originals. Free disk space and SQLite working space are also checked. These guardrails do not limit ordinary scanning, opening a larger database, or importing saved reviews.

Originals are encrypted in 4 MiB chunks and committed in bounded transactions. Encryption, file reading and hashing happen outside write transactions. Incomplete captures remain hidden. The final transaction publishes the originals and selected decisions together. If a selected review changes during capture, its newer decision is preserved and publication is refused.

Use **Cancel task** to stop capture. Committed reviews remain saved. **Maintenance → Discard unfinished original captures** removes hidden incomplete capture data in bounded batches; it preserves completed originals. Optional cleanup does not delay startup.

## Change the originals password

Choose **Maintenance → Change originals password...**. Enter the current password, then enter and confirm the new one. Wait for confirmation. A successful change leaves originals locked; use the new password to unlock them.

Password changes update a small authenticated content-key envelope and the password check in one transaction. Existing originals, including imported HTML Evidence, stay encrypted with the same content key and do not need a complete rewrite. A wrong current password, cancellation before commit or a failed key check does not publish a change. A retry of an already committed operation must authenticate with the same new password before success is acknowledged.

Earlier database copies and backups retain their previous password. Password change does not securely erase older database pages, WAL contents or backups, and does not rotate the content encryption key. Keep older copies protected. This operation changes access through the current database, not access to historical copies.

## Encryption and saved HTML databases

New originals use version 3 authenticated records, AES-256-GCM and a versioned PBKDF2-HMAC-SHA-256 password scheme. New vaults use 600,000 iterations and a random salt. Authentication binds the workspace, capture, decision, full hash, size, chunk count and chunk positions. Passwords and unencrypted keys are never written to logs or the database. No plaintext Evidence scratch files are used.

After a password change, the database stores a version 1 AES-256-GCM content-key envelope. The new password-derived key authenticates that envelope and its workspace/generation binding. [Windows CNG's supported symmetric-key export](https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/nf-bcrypt-bcryptexportkey) runs on the owned operation thread and uses short-lived buffers that are cleared afterward. Key material is never stored unencrypted.

The final HTML build's version 1 and version 2 Evidence records initially open with their original password and derivation parameters. After a password change, use the current originals password. Their content key, authenticated identifiers and original encrypted bytes remain unchanged. Large legacy records are authenticated through bounded streams before plaintext is released. Capture details have display limits; oversized authenticated metadata stays preserved even when the inspector shows a notice instead of its complete text.

Ordinary metadata, review notes, source paths and gallery thumbnails are **not automatically encrypted**. Capture adds encrypted original bytes and a capture-details snapshot; it does not erase ordinary data already present in the database, WAL or older backups. This is not whole-database encryption. Use **Portable backup** for a complete movable database snapshot, including its encrypted originals. Native-to-HTML export is not offered.
