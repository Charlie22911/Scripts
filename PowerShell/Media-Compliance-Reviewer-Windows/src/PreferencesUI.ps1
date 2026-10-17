function Initialize-UiPreferences {
    $u=$script:reviewUi
    $state=[pscustomobject]@{Loaded=$false;ReadOnly=$false;LoadFailed=$false;LoadRequest='';SaveRequest='';Saved='';Observed='';Current=$null;InFlight=$null;OperationId='';AttemptSignature='';Due=[DateTime]::UtcNow;ErrorSignature='';LastValidAge=0;Exit=$null}
    if($u.PSObject.Properties['Preferences']){$u.Preferences=$state}else{$u | Add-Member -NotePropertyName Preferences -NotePropertyValue $state}
}
function Get-UiPreferences {
    $u=$script:reviewUi;$p=[MediaReviewer.ReviewPreferences]::new()
    $p.PageSize=[int]$u.Controls.PageSizeBox.SelectedItem;$p.Fit=[string]$u.Controls.FitBox.SelectedItem.Tag;$p.WorkerCount=[int]$u.Controls.WorkerBox.SelectedItem
    $p.Mode=@('Quick','Deep','Custom')[$u.Controls.ModeBox.SelectedIndex];$p.Families=[string[]]@([MediaReviewer.Formats]::Families | Where-Object {$u.FormatChoices[$_.Key].IsChecked} | ForEach-Object {$_.Key})
    $age=0;if([int]::TryParse($u.Controls.AgeBox.Text,[ref]$age) -and $age -ge 0 -and $age -le 9999){$u.Preferences.LastValidAge=$age};$p.ModifiedAgeYears=$u.Preferences.LastValidAge
    $p.IncludeApplicationData=[bool]$u.Controls.AppDataOption.IsChecked;$p.IncludeRootFiles=[bool]$u.Controls.RootFilesOption.IsChecked;$p.Reviewer=$u.Controls.ReviewerBox.Text
    return $p
}
function Restore-UiPreferences([MediaReviewer.ReviewPreferences]$Preferences) {
    $u=$script:reviewUi;$p=$Preferences;$u.SuppressEvents=$true
    try{
        $u.Controls.PageSizeBox.SelectedItem=$p.PageSize;$u.PageSize=$p.PageSize;$u.Controls.FitBox.SelectedIndex=if($p.Fit -eq 'UniformToFill'){1}else{0};$u.Controls.WorkerBox.SelectedItem=$p.WorkerCount
        $u.Controls.ModeBox.SelectedIndex=[Array]::IndexOf(@('Quick','Deep','Custom'),$p.Mode)
        foreach($family in [MediaReviewer.Formats]::Families){$u.FormatChoices[$family.Key].IsChecked=$family.Key -in $p.Families};Update-UiFormatSummary
        $u.Controls.AppDataOption.IsChecked=$p.IncludeApplicationData;$u.Controls.RootFilesOption.IsChecked=$p.IncludeRootFiles;$u.Controls.AgeBox.Text=[string]$p.ModifiedAgeYears;$u.Preferences.LastValidAge=$p.ModifiedAgeYears;$u.Controls.ReviewerBox.Text=$p.Reviewer
        $u.Page=1;$u.Bucket='TO_REVIEW';$u.Controls.NotesBox.Clear();$u.Controls.SearchBox.Clear();$u.Controls.FolderBox.Clear();$u.Controls.TypeBox.SelectedIndex=0;$u.Controls.SortBox.SelectedIndex=0;$u.Controls.PageBox.Text='1'
    }finally{$u.SuppressEvents=$false}
    $s=$u.Preferences;$s.Loaded=$true;$s.ReadOnly=$p.ReadOnly;$s.Current=Get-UiPreferences;$s.Saved=$s.Current.Signature;$s.Observed=$s.Saved
    $u.Controls.PreferencesText.Text=if($p.Warning){$p.Warning}else{'Preferences loaded for this database.'}
    $u.Controls.RetryPreferencesButton.Visibility='Hidden';$u.Initialized=$true;$u.Opening=$false;$u.Controls.DatabaseLabel.Text=$u.Engine.DatabasePath;Request-UiPage -Reset
}
function Invoke-UiPreferencesTick([switch]$Flush) {
    $u=$script:reviewUi;$s=$u.Preferences;if(-not $s -or -not $s.Loaded -or $s.ReadOnly -or -not $u.Engine -or -not $u.Engine.Ready -or $u.Closing -or $u.Opening){return}
    try{$current=Get-UiPreferences;$signature=$current.Signature}catch{$u.Controls.PreferencesText.Text='Preferences not saved: '+$_.Exception.Message;return}
    if($signature -cne $s.Observed){$s.Observed=$signature;$s.Current=$current;$s.Due=[DateTime]::UtcNow.AddMilliseconds(500);$s.ErrorSignature='';$u.Controls.RetryPreferencesButton.Visibility='Hidden';$u.Controls.PreferencesText.Text='Preferences pending...'}
    if($signature -ceq $s.Saved -and -not $s.SaveRequest){if($u.Controls.PreferencesText.Text -eq 'Preferences pending...'){$u.Controls.PreferencesText.Text='Preferences unchanged.'};return}
    if(-not $s.SaveRequest -and $s.ErrorSignature -cne $signature -and ($Flush -or [DateTime]::UtcNow -ge $s.Due)){
        if($s.AttemptSignature -cne $signature){$s.OperationId=[Guid]::NewGuid().ToString('N');$s.AttemptSignature=$signature}
        try{$s.SaveRequest=$u.Engine.RequestSavePreferences($s.Current,$s.OperationId);$s.InFlight=$signature;$u.Controls.PreferencesText.Text='Preferences saving...'}
        catch{$s.ErrorSignature=$signature;$s.Exit=$null;$u.Controls.PreferencesText.Text='Preferences not queued: '+$_.Exception.Message;$u.Controls.RetryPreferencesButton.Visibility='Visible'}
    }
}
function Test-UiPreferencesExit($Next) {
    $u=$script:reviewUi;$s=$u.Preferences
    if(-not $s.Loaded -or $s.ReadOnly -or -not $u.Engine.Ready -or $u.Engine.Faulted){return $true}
    Invoke-UiPreferencesTick -Flush
    if($s.SaveRequest -or $s.Observed -cne $s.Saved){if(-not $s.ErrorSignature){$s.Exit=$Next};return $false}
    return $true
}
function Complete-UiPreferencesExit {
    $u=$script:reviewUi;$s=$u.Preferences;if(-not $s.Exit){return};Invoke-UiPreferencesTick -Flush
    if($u.Engine.Faulted -or (-not $s.SaveRequest -and $s.Observed -ceq $s.Saved)){$next=$s.Exit;$s.Exit=$null;if($next.Kind -eq 'Close'){$u.Window.Close()}else{Open-UiDatabase $next.Path $next.Create $next.ImportSource}}
}
function Apply-UiPreferencesResult($Result) {
    $u=$script:reviewUi;$s=$u.Preferences
    if($Result.Kind -eq 'Preferences'){
        if(-not $s.LoadRequest -or $Result.RequestId -ne $s.LoadRequest){return $false};$s.LoadRequest=''
        if($Result.Success){Restore-UiPreferences $Result.Preferences}else{$s.LoadFailed=$true;$u.Controls.PreferencesText.Text='Preferences unavailable: '+$Result.Error;$u.Controls.RetryPreferencesButton.Visibility='Visible'};return $true
    }
    if($Result.Kind -eq 'PreferencesSaved'){
        if(-not $s.SaveRequest -or $Result.RequestId -ne $s.SaveRequest){return $false};$s.SaveRequest=''
        if($Result.Success){$s.Saved=$s.InFlight;$u.Controls.PreferencesText.Text=if($s.Saved -ceq $s.Observed){'Preferences committed.'}else{'Preferences pending...'}}
        else{$s.ErrorSignature=$s.InFlight;$s.Exit=$null;$u.Controls.PreferencesText.Text='Preferences not saved: '+$Result.Error;$u.Controls.RetryPreferencesButton.Visibility='Visible'};return $true
    };return $false
}
function Add-UiPreferencesControls {
    $u=$script:reviewUi;foreach($name in @('PreferencesText','RetryPreferencesButton')){$u.Controls[$name]=$u.Window.FindName($name)};Initialize-UiPreferences
    $u.Controls.RetryPreferencesButton.Add_Click({$u=$script:reviewUi;$s=$u.Preferences;if(-not $s.Loaded){try{$s.LoadRequest=$u.Engine.RequestPreferences();$u.Controls.PreferencesText.Text='Loading saved preferences...';$u.Controls.RetryPreferencesButton.Visibility='Hidden'}catch{$u.Controls.PreferencesText.Text=$_.Exception.Message}}else{$s.ErrorSignature='';Invoke-UiPreferencesTick -Flush}})
}
