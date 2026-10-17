# Startup troubleshooting

Version 0.14.1 preserves decoder startup details that can otherwise be lost when a program exits without writing an error message. It does not replace dependencies, relax application-control policy or raise decoder resource limits.

## If the application cannot open

Keep the complete error message, especially the executable name and hexadecimal exit code. Once temporary initialization begins, the dialog and console identify a `startup.log` inside the current instance's directory:

```text
%TEMP%\MediaComplianceReviewer\<instance identifier>\startup.log
```

Copy that file before a later successful launch cleans up abandoned instance directories. Do not remove files belonging to a running instance. Very early PowerShell or application-control failures can prevent initialization and logging entirely.

The diagnostic is limited to 64 KiB. It records startup stages, the failed executable's available error output and exact status, Windows build, PowerShell/CLR/.NET release, CPU/manufacturer and memory context. It does not collect media or database contents, passwords or keys. Check paths and system details before sharing the file.

To check dependencies without opening a database or showing the review window, run from the script's folder:

```powershell
powershell.exe -NoProfile -STA -File .\Media-Compliance-Reviewer-Windows-0.14.1.ps1 -VerifyDependencies
```

## Interpreting the result

An error identifies the program that failed, such as `ffmpeg.exe`, `ffprobe.exe` or `MediaReviewer.Decoder.exe`. A helper's final exit and a failing media tool's exit are separate; keep both when present. If the helper must be stopped before a natural exit status is available, the diagnostic says so.

Known Windows statuses receive a brief explanation. For example, `0xC0000135` indicates a required DLL was not found and `0xC000001D` indicates an illegal instruction. These codes describe what Windows reported; they do not identify a missing filename or prove a particular CPU feature or endpoint-protection product caused the failure. The [Microsoft status-code reference](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55) documents these values.

Report the complete message and diagnostic rather than changing execution policy, adding exclusions, installing codecs or moving libraries into system folders. A confirmed policy restriction needs the appropriate policy decision; the application does not bypass it. Current dependency requirements remain Windows 10 22H2 or later, x64 Windows PowerShell 5.1 and .NET Framework 4.6.2 or later.
