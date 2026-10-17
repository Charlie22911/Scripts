[CmdletBinding()]
param([switch]$Development)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) { throw 'Build with 64-bit Windows PowerShell 5.1.' }
$project=Split-Path -Parent $PSScriptRoot
$releaseVersion=[regex]::Match([IO.File]::ReadAllText("$project\src\Product.cs"),'Version="([0-9]+\.[0-9]+\.[0-9]+)"').Groups[1].Value
if(-not $releaseVersion){throw 'Missing product version'}
$output=Join-Path $project '.build\runtime'
[IO.Directory]::CreateDirectory($output) | Out-Null
foreach ($name in @('sqlite3.dll')) { Copy-Item -LiteralPath "$project\vendor\sqlite\$name" -Destination (Join-Path $output $name) -Force }
foreach ($name in @('libglib-2.0-0.dll','libgobject-2.0-0.dll','libvips-42.dll')) { Copy-Item -LiteralPath "$project\vendor\libvips\vips-dev-8.18\bin\$name" -Destination (Join-Path $output $name) -Force }
$dependencyManifest=Get-Content -LiteralPath "$project\vendor\dependencies.json" -Raw | ConvertFrom-Json
foreach ($property in $dependencyManifest.files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $output $property.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value) { throw "Dependency integrity mismatch: $($property.Name)" }
}
$ffmpegLock=Get-Content -LiteralPath "$project\vendor\ffmpeg-lock.json" -Raw | ConvertFrom-Json
$ffmpegBin=(Get-ChildItem -LiteralPath "$project\vendor\ffmpeg" -Recurse -Filter ffmpeg.exe -File | Select-Object -First 1).Directory.FullName
foreach($spec in $ffmpegLock.files){$from=Join-Path $ffmpegBin $spec.Name;if((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash.ToLowerInvariant() -ne $spec.Sha256){throw ('FFmpeg integrity mismatch: '+$spec.Name)};Copy-Item -LiteralPath $from -Destination (Join-Path $output $spec.Name) -Force}
$sources=[string[]]@(Get-ChildItem -LiteralPath "$project\src" -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$assembly=Join-Path $output 'MediaReviewer.Core.dll'
if (Test-Path -LiteralPath $assembly) { Remove-Item -LiteralPath $assembly -Force }
$parameters=[CodeDom.Compiler.CompilerParameters]::new()
$parameters.GenerateExecutable=$false; $parameters.OutputAssembly=$assembly
$parameters.CompilerOptions='/target:library /platform:x64 /optimize+'
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.Xml.dll','System.Runtime.Serialization.dll')) { [void]$parameters.ReferencedAssemblies.Add($reference) }
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
foreach ($reference in @([Windows.Window].Assembly.Location,[Windows.Media.Imaging.BitmapSource].Assembly.Location,[Windows.DependencyObject].Assembly.Location,[System.Xaml.XamlReader].Assembly.Location)) { [void]$parameters.ReferencedAssemblies.Add($reference) }
$provider=[Microsoft.CSharp.CSharpCodeProvider]::new()
try {
    $result=$provider.CompileAssemblyFromFile($parameters,$sources)
    $errors=@($result.Errors | Where-Object { -not $_.IsWarning })
    if ($errors.Count) { throw ($errors | ForEach-Object { $_.ToString() } | Out-String) }
} finally { $provider.Dispose() }
$decoder=Join-Path $output 'MediaReviewer.Decoder.exe'
if(Test-Path -LiteralPath $decoder){Remove-Item -LiteralPath $decoder -Force}
$decoderParameters=[CodeDom.Compiler.CompilerParameters]::new();$decoderParameters.GenerateExecutable=$true;$decoderParameters.OutputAssembly=$decoder;$decoderParameters.CompilerOptions='/target:exe /platform:x64 /optimize+'
foreach($reference in @('System.dll','System.Core.dll',$assembly)){[void]$decoderParameters.ReferencedAssemblies.Add($reference)}
$provider=[Microsoft.CSharp.CSharpCodeProvider]::new()
try{$result=$provider.CompileAssemblyFromFile($decoderParameters,[string[]]@("$project\decoder\Program.cs"));$errors=@($result.Errors | Where-Object {-not $_.IsWarning});if($errors.Count){throw ($errors | ForEach-Object {$_.ToString()} | Out-String)}}finally{$provider.Dispose()}
Write-Output "Development helper built: $assembly"
$extraNotices=[IO.File]::ReadAllText("$project\vendor\THIRD-PARTY-LICENSES.txt",[Text.Encoding]::UTF8)
$gplAt=$extraNotices.IndexOf('GNU General Public License version 3 (incorporated by LGPLv3)');$manifestAt=$extraNotices.IndexOf('Pinned Windows build dependency/license/source manifest')
if($gplAt -lt 1 -or $manifestAt -le $gplAt){throw 'License section boundaries are missing; notices were not repackaged'}
$licenseSections=@(
    @{Title='SQLite — public domain';Text='SQLite 3.54.0 is public domain. Source: https://www.sqlite.org/2026/sqlite-src-3540000.zip'},
    @{Title='libvips — LGPL 2.1 or later';Text=[IO.File]::ReadAllText("$project\vendor\libvips\vips-dev-8.18\LICENSE",[Text.Encoding]::UTF8)},
    @{Title='Combined decoder build — LGPL 3 or later';Text=$extraNotices.Substring(0,$gplAt)},
    @{Title='GPL 3 terms incorporated by LGPL 3';Text=$extraNotices.Substring($gplAt,$manifestAt-$gplAt)},
    @{Title='FFmpeg — LGPL shared build';Text=[IO.File]::ReadAllText("$project\vendor\FFMPEG-LICENSE.txt",[Text.Encoding]::UTF8)},
    @{Title='Windows decoder build — sources, components and notices';Text=$extraNotices.Substring($manifestAt)},
    @{Title='Bundled component versions';Text=[IO.File]::ReadAllText("$project\vendor\libvips\vips-dev-8.18\versions.json",[Text.Encoding]::UTF8)},
    @{Title='FFmpeg binary and source identifiers';Text=[IO.File]::ReadAllText("$project\vendor\ffmpeg-lock.json",[Text.Encoding]::UTF8)}
)
[IO.File]::WriteAllText("$output\LicenseSections.json",($licenseSections | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
if ($Development) { return }
Copy-Item -LiteralPath "$project\src\MainWindow.xaml" -Destination "$output\MainWindow.xaml" -Force
$notices="Media Compliance Reviewer for Windows - embedded dependency notices`r`n`r`nSQLite 3.54.0: public domain. Source: https://www.sqlite.org/2026/sqlite-src-3540000.zip`r`n`r`nlibvips 8.18.7 x64 static-ffi web build. This combined build uses LGPL-3.0-or-later and separately licensed components. Exact dependency/source/license manifest: https://github.com/libvips/build-win64-mxe/tree/v8.18.7`r`nImage decoding uses the pinned libvips web bundle. Private FFmpeg supplies video/HEVC decoding; RAW uses embedded JPEG extraction.`r`n`r`nThe native libraries are separate replaceable files; rebuild the script with modified library assets and updated integrity hashes when exercising license rights. Application source/build scripts are retained beside this distribution. Reverse engineering for debugging modifications to LGPL libraries is not restricted by this project.`r`n`r`nDependency versions:`r`n"
$notices += [IO.File]::ReadAllText("$project\vendor\libvips\vips-dev-8.18\versions.json",[Text.Encoding]::UTF8)
$notices += "`r`n`r`nUpstream libvips notice:`r`n"+[IO.File]::ReadAllText("$project\vendor\libvips\vips-dev-8.18\README.md",[Text.Encoding]::UTF8)
$notices += "`r`n`r`nUpstream libvips license text (LGPL-2.1-or-later; combined build terms described above):`r`n"+[IO.File]::ReadAllText("$project\vendor\libvips\vips-dev-8.18\LICENSE",[Text.Encoding]::UTF8)
if(Test-Path -LiteralPath "$project\vendor\THIRD-PARTY-LICENSES.txt"){$notices += "`r`n`r`n"+[IO.File]::ReadAllText("$project\vendor\THIRD-PARTY-LICENSES.txt",[Text.Encoding]::UTF8)}
$notices += "`r`n`r`nFFmpeg/ffprobe $($ffmpegLock.version), x64 LGPL shared build. Exact binary/source/build identifiers and SHA-256 hashes:`r`n"+[IO.File]::ReadAllText("$project\vendor\ffmpeg-lock.json",[Text.Encoding]::UTF8)
$notices += "`r`n`r`nFFmpeg LGPL license:`r`n"+[IO.File]::ReadAllText("$project\vendor\FFMPEG-LICENSE.txt",[Text.Encoding]::UTF8)
[IO.File]::WriteAllText("$output\THIRD-PARTY-NOTICES.txt",$notices,[Text.UTF8Encoding]::new($false))
$names=@('MediaReviewer.Core.dll','MediaReviewer.Decoder.exe','sqlite3.dll','libglib-2.0-0.dll','libgobject-2.0-0.dll','libvips-42.dll','MainWindow.xaml','THIRD-PARTY-NOTICES.txt','LicenseSections.json')+@($ffmpegLock.files | ForEach-Object {$_.Name})
$assets=@(foreach($name in $names){$file=Get-Item -LiteralPath (Join-Path $output $name);[pscustomobject]@{Name=$name;Length=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
$json=$assets | ConvertTo-Json -Compress
$startup=[IO.File]::ReadAllText("$project\src\Startup.ps1",[Text.Encoding]::UTF8)
$manifestCode='$assetManifestJson = @''' + "`r`n" + $json + "`r`n'@`r`n" + '$assetManifest = $assetManifestJson | ConvertFrom-Json'
$startup=$startup.Replace('# MCR-MANIFEST-PLACEHOLDER',$manifestCode)
$startup=$startup.Replace('# MCR-UI-PLACEHOLDER',([IO.File]::ReadAllText("$project\src\UI.ps1",[Text.Encoding]::UTF8)+"`r`n"+[IO.File]::ReadAllText("$project\src\EvidenceUI.ps1",[Text.Encoding]::UTF8)+"`r`n"+[IO.File]::ReadAllText("$project\src\InspectorUI.ps1",[Text.Encoding]::UTF8)+"`r`n"+[IO.File]::ReadAllText("$project\src\ReviewDetailsUI.ps1",[Text.Encoding]::UTF8)+"`r`n"+[IO.File]::ReadAllText("$project\src\SourcesUI.ps1",[Text.Encoding]::UTF8)+"`r`n"+[IO.File]::ReadAllText("$project\src\PreferencesUI.ps1",[Text.Encoding]::UTF8)+"`r`n"+[IO.File]::ReadAllText("$project\src\ReportsUI.ps1",[Text.Encoding]::UTF8)+"`r`n"+[IO.File]::ReadAllText("$project\src\PackageUI.ps1",[Text.Encoding]::UTF8)))
$distribution=Join-Path $project 'dist';[IO.Directory]::CreateDirectory($distribution) | Out-Null
$destination=Join-Path $distribution ("Media-Compliance-Reviewer-Windows-$releaseVersion.ps1")
$writer=[IO.StreamWriter]::new($destination,$false,[Text.UTF8Encoding]::new($true))
try {
    $writer.WriteLine($startup)
    foreach($name in $names){
        $compressed=Join-Path $project ('.build\'+$name+'.gzip');$sourceStream=[IO.File]::OpenRead((Join-Path $output $name));$targetStream=[IO.File]::Create($compressed);$gzipStream=[IO.Compression.GZipStream]::new($targetStream,[IO.Compression.CompressionMode]::Compress)
        try{$sourceStream.CopyTo($gzipStream,65536)}finally{$gzipStream.Dispose();$targetStream.Dispose();$sourceStream.Dispose()}
        $writer.WriteLine('#MCR-ASSET '+$name);$inputStream=[IO.File]::OpenRead($compressed)
        try{$buffer=[byte[]]::new(12288);while(($read=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0){$writer.WriteLine('#'+[Convert]::ToBase64String($buffer,0,$read))}}finally{$inputStream.Dispose()}
        $writer.WriteLine('#MCR-END')
    }
}finally{$writer.Dispose()}
$sha=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant();[IO.File]::WriteAllText($destination+'.sha256',$sha+'  '+[IO.Path]::GetFileName($destination)+"`r`n",[Text.UTF8Encoding]::new($false))
Write-Output ("Portable script built: $destination ($((Get-Item -LiteralPath $destination).Length) bytes); SHA-256 $sha")
