# Retry saved previews

Select up to 200 cards in an ordinary review bucket, then choose **Retry previews**. The application reads the recorded source again using the bundled offline decoder. You can keep reviewing cached images while the task runs. The main status reports progress and **Cancel task** stops further work.

Each saved thumbnail or poster is committed separately. Retry does not change fingerprints, buckets, notes, publication order, completed scan users or source files. If a retry fails, an existing good preview remains available. Card labels show the latest retry outcome; open **Log** for affected paths and retry details.

The source must still match its saved size, modification time and file identity when one was recorded. Changed contents require a rescan. Missing sources, permission errors and unsupported encodings have specific failures. Transient read failures receive an initial attempt and up to three retries with cancellable waits. A decoder failure does not discard the fingerprinted record.

## Resume interrupted work

Choose **Preview tasks** beside Maintenance. The window lists saved tasks and their recorded outcomes. **Refresh** loads current outcomes. **Resume unfinished** processes unfinished rows from an interrupted task; it does not repeat saved or failed rows. Select the affected gallery cards for a fresh attempt at a failed source.

**Discard pending** removes the intent to continue unfinished work. It keeps saved previews, original media and review decisions. You can select those cards for another retry later. Cancel a running task before discarding it.

At most 20 unfinished tasks are retained. Resume or discard earlier work before adding more. The application also keeps up to 20 completed tasks, with at most 200 selected items in each. It does not automatically queue the whole library when opening a database.

## Limits

Sources may be local folders, mapped drives or UNC shares. The active WAL database must remain on suitable local storage. Use [Sources / reconnect](SOURCES.md) for descriptive imported root labels and moved or copied sources. Retry does not guess a folder relative to the application directory. The isolated decoder checks the opened file's resolved location against the root and refuses a junction that redirects it outside that root.

Evidence originals use authenticated stored inspection and export. Ordinary preview retry does not decrypt them or send them to the source decoder. Retained originals with no recorded source location also require their stored-original controls.

Gallery previews retain their 256-pixel/32-KiB bounds. Retrying a preview uses one bounded decoder request at a time through the existing background worker. Source inspection keeps its separate window/zoom limits. A refreshed saved preview invalidates its old cached image; late image results cannot replace the newer version.

This build uses native schema 8. Opening a database from schemas 1 through 7 first makes a validated SQLite backup, then applies transactional upgrades. The backup remains beside the database with a `.pre-upgrade-v...sqlite` name. Keep it if you need to reopen the earlier database with an older application build.
