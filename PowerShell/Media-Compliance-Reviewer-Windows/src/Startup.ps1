#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$VerifyDependencies,
    [string]$DatabasePath,
    [string]$ImportHtmlPath,
    [string]$OpenEvidencePackage,
    [string]$ScanPath,
    [switch]$CreateDatabase,
    [string]$ResumeScanId,
    [ValidateRange(1,8)][int]$WorkerCount=4,
    [ValidateSet('Quick','Deep','Custom')][string]$ScanMode='Quick',
    [string[]]$Extensions
)
$ErrorActionPreference='Stop'
if($OpenEvidencePackage -and ($VerifyDependencies -or $DatabasePath -or $ImportHtmlPath -or $ScanPath -or $ResumeScanId -or $CreateDatabase)){throw 'Open a protected package separately from database, scan, import or dependency-check options.'}
if($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess){throw 'Use 64-bit Windows PowerShell 5.1 (powershell.exe), not PowerShell 7 or a 32-bit host.'}
if($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage'){throw ('Application control has restricted PowerShell to '+$ExecutionContext.SessionState.LanguageMode+'. This application needs FullLanguage and permitted private assemblies. No policy change or bypass is attempted.')}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Start with powershell.exe -NoProfile -STA -File <script.ps1>.'}
if('MediaReviewer.Native' -as [type]){throw 'Start the portable script in a fresh PowerShell process to avoid mixing loaded dependency versions.'}
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
$applicationTempRoot=Join-Path ([IO.Path]::GetTempPath()) 'MediaComplianceReviewer'
$instanceDirectory=Join-Path $applicationTempRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($instanceDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $instanceDirectory 'owner.marker'),'MediaComplianceReviewer-owned-v1')
$instanceLease=[IO.FileStream]::new((Join-Path $instanceDirectory 'instance.lease'),[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$startupDiagnosticPath=Join-Path $instanceDirectory 'startup.log'
function Write-StartupDiagnostic([string]$Text){
    try{if($Text.Length -gt 8192){$Text=$Text.Substring(0,8192)};$entry=[Text.Encoding]::UTF8.GetBytes(([DateTime]::UtcNow.ToString('o')+' '+$Text+[Environment]::NewLine));$log=[IO.FileStream]::new($startupDiagnosticPath,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$remaining=65536-$log.Length;if($remaining -gt 0){$log.Write($entry,0,[int][Math]::Min($entry.Length,$remaining))}}finally{$log.Dispose()}}catch{}
}
$splash=$null;$engine=$null
try {
    Write-StartupDiagnostic ('PowerShell '+$PSVersionTable.PSVersion+'; language mode='+$ExecutionContext.SessionState.LanguageMode+'; apartment='+[Threading.Thread]::CurrentThread.GetApartmentState()+'; extracting verified startup helper')
    # MCR-MANIFEST-PLACEHOLDER
    $helper=$assetManifest | Where-Object {$_.Name -eq 'MediaReviewer.Core.dll'}
    $helperCompressed=Join-Path $instanceDirectory 'bootstrap.gzip';$helperPath=Join-Path $instanceDirectory 'MediaReviewer.Core.dll'
    $reader=[IO.StreamReader]::new($PSCommandPath,[Text.Encoding]::UTF8,$true,65536)
    $compressed=[IO.File]::Create($helperCompressed)
    try {
        $found=$false;$ended=$false;$size=0L
        while(($line=$reader.ReadLine()) -ne $null){if($line -eq '#MCR-ASSET MediaReviewer.Core.dll'){$found=$true;break}}
        if(-not $found -or $helper.Length -gt 1MB){throw 'The embedded startup helper is missing or exceeds its size limit.'}
        while(($line=$reader.ReadLine()) -ne $null){if($line -eq '#MCR-END'){$ended=$true;break};if(-not $line.StartsWith('#') -or $line.Length -gt 32769){throw 'Malformed startup payload.'};$bytes=[Convert]::FromBase64String($line.Substring(1));$size+=$bytes.Length;if($size -gt 1MB){throw 'Startup payload exceeds its size limit.'};$compressed.Write($bytes,0,$bytes.Length)}
        if(-not $ended){throw 'Startup payload is truncated.'}
    } finally {$reader.Dispose();$compressed.Dispose()}
    $inputStream=[IO.File]::OpenRead($helperCompressed);$gzip=[IO.Compression.GZipStream]::new($inputStream,[IO.Compression.CompressionMode]::Decompress);$outputStream=[IO.File]::Create($helperPath)
    try {$block=[byte[]]::new(65536);$expanded=0L;while(($count=$gzip.Read($block,0,$block.Length)) -gt 0){$expanded+=$count;if($expanded -gt $helper.Length){throw 'Startup helper expanded beyond its declared length.'};$outputStream.Write($block,0,$count)}}finally{$outputStream.Dispose();$gzip.Dispose();$inputStream.Dispose()}
    if($expanded -ne $helper.Length -or (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $helper.Sha256){throw 'Startup helper integrity check failed.'}
    Add-Type -Path $helperPath
    $preparation=[MediaReviewer.Distribution]::Prepare($PSCommandPath,$instanceDirectory,$assetManifestJson)
    $headless=$VerifyDependencies -or $ScanPath -or $ResumeScanId -or $ImportHtmlPath
    if(-not $headless){$splash=[Windows.Markup.XamlReader]::Parse('<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Starting Media Compliance Reviewer" Width="530" Height="180" ResizeMode="NoResize" WindowStartupLocation="CenterScreen" FontFamily="Segoe UI"><StackPanel Margin="24"><TextBlock Text="Starting Media Compliance Reviewer" FontSize="19" FontWeight="SemiBold"/><TextBlock x:Name="Stage" Text="Checking embedded dependencies…" Margin="0,16"/><ProgressBar Height="12" IsIndeterminate="True"/></StackPanel></Window>');$splash.Show()}
    while(-not $preparation.Done){if($splash){$splash.FindName('Stage').Text=$preparation.Stage;[Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::Background)};Start-Sleep -Milliseconds 25}
    if($preparation.Error){throw $preparation.Error}
    $script:EmbeddedMainWindowXaml=[IO.File]::ReadAllText((Join-Path $instanceDirectory 'MainWindow.xaml'),[Text.Encoding]::UTF8)
    $script:RuntimeLicenseText=[IO.File]::ReadAllText((Join-Path $instanceDirectory 'THIRD-PARTY-NOTICES.txt'),[Text.Encoding]::UTF8)
    $script:RuntimeLicenseSections=[IO.File]::ReadAllText((Join-Path $instanceDirectory 'LicenseSections.json'),[Text.Encoding]::UTF8) | ConvertFrom-Json
    # MCR-UI-PLACEHOLDER
    [MediaReviewer.Distribution]::ScheduleCleanup($applicationTempRoot,$instanceDirectory)
    if($splash){$splash.Close();$splash=$null}
    if($VerifyDependencies){Write-Output ('Verified offline dependencies: SQLite '+[MediaReviewer.Native]::SqliteVersion+'; libvips '+[MediaReviewer.Native]::DecoderVersion+'; Windows CNG; PowerShell '+$PSVersionTable.PSVersion);return}
    if($ScanPath -or $ResumeScanId -or $ImportHtmlPath){
        if(-not $DatabasePath){throw 'A command-line scan or import requires -DatabasePath.'}
        if($ImportHtmlPath -and ($ScanPath -or $ResumeScanId -or $CreateDatabase)){throw 'Use -ImportHtmlPath with a new -DatabasePath; scanning and -CreateDatabase are separate operations.'}
        $engine=if($ImportHtmlPath){[MediaReviewer.ReviewerEngine]::ImportHtml($ImportHtmlPath,$DatabasePath)}else{[MediaReviewer.ReviewerEngine]::Open($DatabasePath,[bool]$CreateDatabase)}
        $openingLast=[DateTime]::MinValue
        while(-not $engine.Ready -and -not $engine.Faulted){if(([DateTime]::UtcNow-$openingLast).TotalSeconds -ge 1){Write-Output $engine.Status.Stage;$openingLast=[DateTime]::UtcNow};Start-Sleep -Milliseconds 25}
        if($engine.Faulted){throw $engine.Status.Error}
        if($ImportHtmlPath){Write-Output ('Imported final HTML database: '+$engine.DatabasePath);return}
        if($ResumeScanId){$engine.ResumeScan($ResumeScanId,$WorkerCount)}else{$options=[MediaReviewer.ScanOptions]::new();$options.RootPath=$ScanPath;$options.WorkerCount=$WorkerCount;if($Extensions){$options.Extensions=[string[]]@($Extensions|ForEach-Object{$_ -split '[,;\s]+'}|Where-Object{$_})}elseif($ScanMode -eq 'Deep'){$options.Extensions=[MediaReviewer.Formats]::Deep;$options.ExcludeApplicationData=$false}elseif($ScanMode -eq 'Custom'){throw 'Custom command-line scans require -Extensions.'};$engine.StartScan($options) | Out-Null}
        $last=[DateTime]::MinValue
        while($engine.Status.RunningScan){if(([DateTime]::UtcNow-$last).TotalSeconds -ge 1){$status=$engine.Status;Write-Output ($status.Stage+'; committed files '+$status.ProcessedFiles+'; pending '+$status.PendingWork);$last=[DateTime]::UtcNow};Start-Sleep -Milliseconds 50}
        $status=$engine.Status;Write-Output ($status | ConvertTo-Json -Compress)
        if($engine.Faulted -or $status.Error){throw $status.Error}
        return
    }
    if($OpenEvidencePackage){Start-PackageInterface $OpenEvidencePackage}else{Start-ReviewerInterface}
} catch {
    $failure=$_.Exception.Message;Write-StartupDiagnostic ($_.Exception.ToString());$diagnosticNotice=if([IO.File]::Exists($startupDiagnosticPath)){'Startup diagnostic: '+$startupDiagnosticPath}else{'Startup diagnostic could not be written in '+$instanceDirectory};Write-Output $diagnosticNotice
    if($splash){$splash.FindName('Stage').Text=$_.Exception.Message;$splash.Close()}
    if(-not $VerifyDependencies -and -not $ScanPath -and -not $ResumeScanId -and -not $ImportHtmlPath){[Windows.MessageBox]::Show(($failure+"`r`n`r`n"+$diagnosticNotice),'Media Compliance Reviewer could not start',[Windows.MessageBoxButton]::OK,[Windows.MessageBoxImage]::Error) | Out-Null}
    throw [InvalidOperationException]::new(($failure+"`r`n`r`n"+$diagnosticNotice),$_.Exception)
} finally {
    if($engine){$engine.Dispose()}
    if('MediaReviewer.Preview' -as [type]){[MediaReviewer.Preview]::Shutdown()}
    $instanceLease.Dispose()
}
