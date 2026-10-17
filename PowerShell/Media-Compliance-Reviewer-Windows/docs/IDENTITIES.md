# Fingerprints and full verification

Quick and Deep scans use the same hashing policy. Files larger than 1 MiB receive a quick fingerprint made from ten spaced windows, each at most 1 MiB. The fingerprint includes the file size and window positions. Small files receive a complete SHA-256 hash. A fresh scan fully reads paths that already have full hashes so their existing review decisions remain attached to verified contents.

A **Quick candidate match** means the sampled parts match. Files may differ elsewhere. Decisions on a candidate card apply to its grouped locations within that source root. Another root has independent decisions.

## Verify a candidate

1. Select one candidate card and click **Verify full**. Every current location is read completely in the background. You can continue browsing and inspecting saved previews.
2. Review the full hashes, example locations, and proposed decisions. Checking only one location through the programming interface cannot verify an entire group.
3. If an existing full group has a different decision, acknowledge keeping that group's decision. The candidate's original review is preserved in history.
4. Click **Apply verified identities**. Preparing batches does not change the visible grouping. Publication changes the groups together after the final transaction commits.

The first recorded location anchors the original card when its full identity has no existing group in that root. It retains the original review and display position. Other newly separated identities start **To be reviewed** and append to the gallery. Existing full groups retain their decisions and display positions; a redundant candidate card is archived after its locations join those groups.

Use **Identity history** on a surviving card to read related active and archived reviews. The window shows up to 32 groups and the first 2,048 characters of each note. Complete notes remain in the database.

## Interrupted work and changed sources

**Cancel task** stops the current verification or preparation without discarding committed review decisions. Prepared location bindings stay hidden until publication. Operation identifiers allow a committed result to be confirmed after its response is lost.

Sources must still match their recorded size, modification time and available file identity. Full verification also reproduces the saved quick fingerprint through the same open file handle as the full read. Changed sampled bytes cannot inherit the candidate's decision. Supported sampling includes the native version 1 parameters and the final HTML build's fixed q2 method; unknown historical methods or parameters are refused.

Each source's metadata is checked again during preparation. Publication is atomic in the database; the application does not lock an entire source tree simultaneously. If a source changed, scan its current contents before verifying it again. When a fresh scan supersedes the last current location of an older record, that record remains visible with a **Historical location** label and its saved preview.

If the database changes after the result preview was prepared, applying it is refused. Verify again after the scan or other review changes settle. An incomplete cached result can also be retried with **Verify full**.

## Protected originals and limits

Evidence records and candidates linked to protected originals are retained for the authenticated Evidence workflow. Ordinary verification cannot rewrite their identities. Joining an existing Evidence group requires a verified full hash and explicit acknowledgement; its authenticated key and ciphertext stay unchanged.

The current resolver accepts up to 200 distinct full identities in one result. This is an outcome limit, not a limit of 200 duplicate locations. Larger collision sets remain candidates with an explanation. Legacy protected-identity lookup is bounded to 1,000 reachable keys and 4,096 characters per key; oversized or streamed alias identifiers require the protected-record resolver.

Full verification saves hashes and bounded previews. Non-compliant and Evidence actions also [retain encrypted originals](EVIDENCE.md). A single-outcome candidate can be promoted during capture; collisions and matches to existing full groups need explicit resolution first. Ordinary metadata, notes and thumbnails are not automatically encrypted.
