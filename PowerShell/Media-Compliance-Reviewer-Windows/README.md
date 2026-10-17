# Media Compliance Reviewer for Windows

I am building a portable PowerShell desktop reviewer for images and videos stored in Windows user home folders. Version **0.14.0** is a development build with a native WPF interface, one local SQLite database, saved previews, resumable scanning, review buckets, encrypted original retention, offline reports and protected Evidence packages.

## Download and run

Download [Media-Compliance-Reviewer-Windows-0.14.0.ps1](../Media-Compliance-Reviewer-Windows-0.14.0.ps1?raw=true). Use GitHub's **Download raw file** button if you open the file page. You need only this one script to run the application. Its [SHA-256 checksum](../Media-Compliance-Reviewer-Windows-0.14.0.ps1.sha256) is available beside it.

Use 64-bit Windows PowerShell 5.1 on Windows 10 22H2 or later with the existing .NET Framework 4.6.2 or later. From the folder containing the downloaded script, run:

```powershell
powershell.exe -NoProfile -STA -File .\Media-Compliance-Reviewer-Windows-0.14.0.ps1
```

The application works offline. It includes SQLite, image/video decoders and its interface. It requires no installer, administrator rights, PowerShell 7, Python, Node.js, additional runtime or machine configuration changes. Embedded dependencies extract into an instance-owned folder under `%TEMP%\MediaComplianceReviewer`; their hashes and architecture are checked before use. Application-control restrictions can prevent execution; the application reports these restrictions without changing or bypassing policy.

## Start a review

1. Choose **Create** for a new local database or **Open** for an existing review. Wait for initialization to finish.
2. Select a source root. Its immediate subfolders are treated as user home folders. Sources may be local, mapped drives or UNC shares.
3. Choose **Quick**, **Deep** or a custom type selection. Quick includes common images such as HEIC/HEIF and video. Deep adds camera RAW and includes AppData. Both sample larger files; full verification distinguishes proven duplicates from candidates.
4. Start scanning. Review committed results while scanning continues. Select cards or **Select page**, enter a reviewer label and notes, then choose a bucket. The interface distinguishes pending actions from committed saves.
5. Cancel scanning when needed and use **Resume scan** to continue saved work. Start a fresh scan to find additions in previously completed user folders.

**Keep the active database on suitable local storage.** Media may remain on network shares. SQLite WAL databases are not placed on UNC or mapped network destinations. Use **Maintenance → Backup** for an open database; copying only the main file can omit committed WAL changes. Scanning and retention preserve source files.

## Originals, Evidence and reports

Non-compliant and Evidence items can retain encrypted full-resolution originals, verified with full SHA-256. Keep the originals password: password recovery is not available. Ordinary review records and gallery thumbnails are not automatically encrypted.

Maintenance exports readable CSV and paged offline HTML reports. To share protected originals, select up to 200 retained Non-compliant or Evidence items and choose **Maintenance → Export selected protected package**. Open the resulting `.mcrevidence` file with the same portable reviewer, using its export-time originals password. Source folders and a review database are not required for package inspection.

Package opening is read only. The application authenticates the package before listing items and verifies an original before showing its saved details and thumbnail. **Export original** writes a verified plaintext copy only to an explicitly chosen new file. **Lock** clears protected views; normal close waits for owned work. A forced shutdown can leave an incomplete chosen export, so inspect it before use.

See [retention and Evidence](docs/EVIDENCE.md), [protected packages](docs/PROTECTED-PACKAGES.md), [offline reports](docs/REPORTS.md), [file formats](docs/FORMATS.md), [inspection and zoom](docs/INSPECTION.md), [source connections](docs/SOURCES.md), [saved preferences](docs/PREFERENCES.md), and [current scope and measurements](docs/STATUS.md).

## Existing HTML databases

Open recognizes databases from the final HTML build, **3.3.12 / schema 4**, and creates a separately chosen Windows database while preserving the original. Imported encrypted Evidence initially uses its saved HTML password. Review decisions, saved locations, previews, scan work and supported encrypted originals are preserved. Older HTML schemas are not accepted directly.

## Source and dependency information

Application source is in `src/` and `decoder/`; build scripts are in `scripts/`. Pinned dependency URLs, versions and hashes are in `vendor/dependencies.json` and `vendor/ffmpeg-lock.json`. Obtain and extract those development assets into `vendor/sqlite`, `vendor/libvips` and `vendor/ffmpeg` before rebuilding. Runtime operation does not download them.

From this source folder, build with:

```powershell
powershell.exe -NoProfile -STA -File .\scripts\build.ps1
```

The output is written to this folder's `dist/` directory. To use modified LGPL libraries, retain their required notices, update the pinned binary hashes and rebuild. Reverse engineering needed to debug modifications to these libraries is not restricted. See [third-party notices](NOTICE.md), [embedded dependency notices](THIRD-PARTY-NOTICES.txt), the retained license texts in `vendor/`, and the exact upstream source/build links in the dependency manifests.

## Verification and current limits

I verified this build with generated fixtures on one Windows machine, including the isolated portable script, real WPF interactions, final-HTML database/Evidence compatibility, cancellation, resume, malformed dependencies and protected package integrity. A generated original larger than 2 GiB retained, packaged and exported with its independently verified full SHA-256.

Large network collections and multi-day operation remain unverified. ZIP scanning, broader maintenance and full playback remain outside this build's supported scope. Recognized extensions do not guarantee every encoding is decodable. See the [changelog](CHANGELOG.md) and [current scope](docs/STATUS.md) for details.

Made by **IT1 Charlie McPherson** · Version **0.14.0** · **October 16, 2026**.
