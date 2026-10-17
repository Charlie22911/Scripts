param([switch]$UseExisting)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$nativeDirectory=Join-Path $project '.build\runtime'
if (-not ('MediaReviewer.Native' -as [type])) {
    if (-not $UseExisting) { & "$PSScriptRoot\build.ps1" -Development }
    Add-Type -Path "$nativeDirectory\MediaReviewer.Core.dll"
    [MediaReviewer.Native]::Initialize($nativeDirectory)
}
