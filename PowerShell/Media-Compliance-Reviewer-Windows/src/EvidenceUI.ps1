function Get-UiPasswordBytes([Security.SecureString]$Password) {
    $pointer=[IntPtr]::Zero;$characters=[char[]]::new($Password.Length)
    try{$pointer=[Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password);[Runtime.InteropServices.Marshal]::Copy($pointer,$characters,0,$characters.Length);return ,[Text.Encoding]::UTF8.GetBytes($characters)}
    finally{[Array]::Clear($characters,0,$characters.Length);if($pointer -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)};$Password.Dispose()}
}
function Clear-UiStoredView {
    $u=$script:reviewUi;if($u.StoredWindow){$u.StoredWindow.Close();$u.StoredWindow=$null};$u.StoredTask=$null
}
function Invoke-UiVaultLock {
    if($script:packageWindow){$script:packageWindow.Close()}
    $u=$script:reviewUi;Clear-UiStoredView;if($u.VaultContinuation -and $u.VaultContinuation.Kind -eq 'Capture'){foreach($card in $u.Cards){if($u.VaultContinuation.Request.GroupIds -contains $card.Record.GroupId){$card.IsPending=$false}}};$u.VaultContinuation=$null
    try{$u.VaultTask=$u.Engine.RequestVaultLock();$u.BackgroundOperationId=$u.VaultTask;$u.MaintenancePending=$true;Set-UiMessage 'Locking originals; protected views cleared.'}catch{Set-UiMessage $_.Exception.Message};Update-UiControls
}
function Show-UiVaultDialog($Continuation=$null) {
    $u=$script:reviewUi;if($u.MaintenancePending){Set-UiMessage 'Wait for the current task or cancel it first.';return}
    $create=-not $u.Engine.VaultExists
    $markup='<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Unlock stored originals" Width="520" Height="360" ResizeMode="NoResize" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI"><Grid Margin="20"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><TextBlock x:Name="Explanation" TextWrapping="Wrap" Margin="0,0,0,12"/><PasswordBox Grid.Row="1" x:Name="Password" Height="30" Margin="0,4" ToolTip="Originals password"/><StackPanel Grid.Row="2" x:Name="ConfirmPanel"><TextBlock Text="Confirm the new password" Margin="0,10,0,4"/><PasswordBox x:Name="Confirmation" Height="30"/></StackPanel><TextBlock Grid.Row="3" x:Name="Message" TextWrapping="Wrap" Margin="0,12,0,0"/><StackPanel Grid.Row="4" Orientation="Horizontal" HorizontalAlignment="Center"><Button x:Name="Unlock" Content="Unlock" IsDefault="True" Padding="24,7" Margin="6"/><Button x:Name="Cancel" Content="Cancel" IsCancel="True" Padding="24,7" Margin="6"/></StackPanel></Grid></Window>'
    $dialog=[Windows.Markup.XamlReader]::Parse($markup);$dialog.Owner=$u.Window;$dialog.Tag=[pscustomobject]@{Create=$create;Continuation=$Continuation}
    $dialog.FindName('Explanation').Text=if($create){'Create a password for encrypted retained originals and Evidence. Keep it safe; it cannot be recovered. Ordinary notes and thumbnails remain unencrypted.'}else{'Enter the current originals password. Newly imported HTML Evidence initially uses its saved HTML password.'}
    $dialog.FindName('ConfirmPanel').Visibility=if($create){'Visible'}else{'Collapsed'};$dialog.FindName('Unlock').Content=if($create){'Create vault'}else{'Unlock'}
    $dialog.FindName('Unlock').Add_Click({param($sender,$event)
        $window=[Windows.Window]::GetWindow($sender);$bytes=$null;$confirmation=$null
        try{$bytes=Get-UiPasswordBytes $window.FindName('Password').SecurePassword;if(-not $bytes.Length){throw 'Enter a password.'}
            if($window.Tag.Create){$confirmation=Get-UiPasswordBytes $window.FindName('Confirmation').SecurePassword;$same=$bytes.Length -eq $confirmation.Length;for($i=0;$i -lt [Math]::Min($bytes.Length,$confirmation.Length);$i++){if($bytes[$i] -ne $confirmation[$i]){$same=$false}};if(-not $same){throw 'The passwords do not match.'}}
            $u=$script:reviewUi;$u.VaultContinuation=$window.Tag.Continuation;$u.VaultTask=$u.Engine.RequestVaultUnlock($bytes,[bool]$window.Tag.Create);$bytes=$null;$u.BackgroundOperationId=$u.VaultTask;$u.MaintenancePending=$true;Set-UiMessage 'Checking the originals password...';$window.DialogResult=$true
        }catch{$window.FindName('Message').Text=$_.Exception.Message}finally{if($bytes){[Array]::Clear($bytes,0,$bytes.Length)};if($confirmation){[Array]::Clear($confirmation,0,$confirmation.Length)};$window.FindName('Password').Clear();$window.FindName('Confirmation').Clear()}
    })
    $u.VaultDialog=$dialog;try{$dialog.ShowDialog() | Out-Null}finally{$u.VaultDialog=$null};Update-UiControls
}
function Start-UiOriginalRetention([MediaReviewer.ReviewRequest]$Request,[string]$Bucket) {
    $u=$script:reviewUi;if($u.MaintenancePending){Set-UiMessage 'Wait for the current task or cancel it first.';return}
    try{if($Bucket -eq 'EVIDENCE'){[MediaReviewer.ReviewerEngine]::ValidateEvidenceReclassify($Request)}else{[MediaReviewer.ReviewerEngine]::ValidateOriginalRetention($Request)}}catch{foreach($card in $u.Cards){if($Request.GroupIds -contains $card.Record.GroupId){$card.IsPending=$false}};Set-UiMessage ('Retention failed; selection retained. Use at most 128 reviewer characters and 16,384 note characters. '+$_.Exception.Message);Update-UiControls;return}
    if(-not $Request.ExpectedRevisions){$Request.ExpectedRevisions=[Collections.Generic.Dictionary[long,long]]::new();foreach($card in $u.Cards){if($Request.GroupIds -contains $card.Record.GroupId){$Request.ExpectedRevisions[$card.Record.GroupId]=$card.Record.ReviewRevision}}}
    $retry=$u.RetentionRetry;if($retry -and $retry.Database -eq $u.Engine.DatabasePath){$old=$retry.Request;if($old.Status -ceq $Request.Status -and $old.Reviewer -ceq $Request.Reviewer -and $old.Notes -ceq $Request.Notes -and (($old.GroupIds | Sort-Object) -join ',') -ceq (($Request.GroupIds | Sort-Object) -join ',')){$Request.OperationId=$old.OperationId}}
    if(-not $u.Engine.VaultUnlocked){foreach($card in $u.Cards){if($Request.GroupIds -contains $card.Record.GroupId){$card.IsPending=$true}};Show-UiVaultDialog ([pscustomobject]@{Kind='Capture';Request=$Request;Bucket=$Bucket});if(-not $u.MaintenancePending){foreach($card in $u.Cards){if($Request.GroupIds -contains $card.Record.GroupId){$card.IsPending=$false}}};return}
    try{$id=if($Bucket -eq 'EVIDENCE'){$u.Engine.RequestEvidenceReclassify($Request)}else{$u.Engine.RequestOriginalRetention($Request)};$u.Pending[$id]=[pscustomobject]@{Ids=$Request.GroupIds;Bucket=$Bucket;Target=$Request.Status;OperationId=$Request.OperationId;Capture=$true;Request=$Request};$u.MaintenancePending=$true;$u.BackgroundOperationId=$id;foreach($card in $u.Cards){if($Request.GroupIds -contains $card.Record.GroupId){$card.IsPending=$true}};Set-UiMessage $(if($Bucket -eq 'EVIDENCE'){'Authenticating stored originals; Evidence reviews change after commit.'}else{'Retaining verified encrypted originals; the review changes after commit.'})}catch{foreach($card in $u.Cards){if($Request.GroupIds -contains $card.Record.GroupId){$card.IsPending=$false}};Set-UiMessage ('Retention failed; selection retained: '+$_.Exception.Message)};Update-UiControls
}
function Invoke-UiEvidenceCapture {
    $u=$script:reviewUi;$cards=@($u.Cards | Where-Object {$_.IsSelected -and -not $_.IsPending});if(-not $cards.Count){return}
    $r=[MediaReviewer.ReviewRequest]::new();$r.OperationId=[Guid]::NewGuid().ToString('N');$r.GroupIds=[long[]]@($cards | ForEach-Object {$_.Record.GroupId});$r.Status='EVIDENCE';$r.Reviewer=$u.Controls.ReviewerBox.Text;$r.Notes=$u.Controls.NotesBox.Text;Start-UiOriginalRetention $r $u.Bucket
}
function Invoke-UiStoredInspect($Card=$null) {
    $u=$script:reviewUi;if(-not $Card){$selected=@($u.Cards | Where-Object {$_.IsSelected});if($selected.Count -ne 1){return};$Card=$selected[0]}
    if($u.MaintenancePending){Set-UiMessage 'Wait for the current task or cancel it first.';return}
    if(-not $u.Engine.VaultUnlocked){Show-UiVaultDialog ([pscustomobject]@{Kind='Inspect';Card=$Card});return}
    Clear-UiStoredView
    try{$id=$u.Engine.RequestEvidenceInspect($Card.Record.GroupId);$u.StoredTask=[pscustomobject]@{Id=$id;Group=$Card.Record.GroupId;View=$u.View.Current;Selection=$u.SelectionGeneration;Epoch=$u.Engine.VaultGeneration};$u.BackgroundOperationId=$id;$u.MaintenancePending=$true;Set-UiMessage 'Authenticating the retained original...'}catch{Set-UiMessage $_.Exception.Message};Update-UiControls
}
function Show-UiStoredOriginal([MediaReviewer.EvidenceInspection]$Original) {
    $u=$script:reviewUi;Clear-UiStoredView
    $markup='<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Verified stored original" Width="850" Height="650" MinWidth="500" MinHeight="400" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI"><Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><TextBox x:Name="Details" IsReadOnly="True" TextWrapping="Wrap" MaxHeight="200" VerticalScrollBarVisibility="Auto" Margin="0,0,0,10"/><Border Grid.Row="1" Background="#EAF0F5"><Image x:Name="Picture" Stretch="Uniform"/></Border><Button Grid.Row="2" x:Name="Close" Content="Close" IsCancel="True" HorizontalAlignment="Center" Padding="24,7" Margin="0,12,0,0"/></Grid></Window>'
    $window=[Windows.Markup.XamlReader]::Parse($markup);$window.Owner=$u.Window;$window.Tag=$Original;$u.StoredWindow=$window
    $details='';$length=[Math]::Min(32768,$Original.Metadata.Length);$text=[Text.Encoding]::UTF8.GetString($Original.Metadata,0,$length)
    if($length -lt $Original.Metadata.Length){$details='Capture details exceed the display limit; their encrypted original is preserved.'}else{try{$meta=$text | ConvertFrom-Json;$details=@($meta.name,$meta.path,$meta.capturedAt,$meta.notes,$meta.displayNotice) -join "`r`n"}catch{$details='Authenticated capture details are preserved in the database.'}}
    $window.FindName('Details').Text="Verified original: $($Original.Size) bytes`r`nSHA-256: $($Original.Hash)`r`n$details`r`nThe image below is the saved thumbnail. Use Export original for full resolution."
    if($Original.PreviewImage){$window.FindName('Picture').Source=$Original.PreviewImage}elseif($Original.PreviewError){Set-UiMessage $Original.PreviewError}
    $window.FindName('Close').Add_Click({param($sender,$event) [Windows.Window]::GetWindow($sender).Close()})
    $window.Add_Closed({param($sender,$event) $sender.FindName('Picture').Source=$null;$sender.FindName('Details').Clear();if($sender.Tag){$sender.Tag.Dispose();$sender.Tag=$null};if($script:reviewUi.StoredWindow -eq $sender){$script:reviewUi.StoredWindow=$null}});$window.Show()
}
function Invoke-UiOriginalExport {
    $u=$script:reviewUi;$cards=@($u.Cards | Where-Object {$_.IsSelected});if($cards.Count -ne 1 -or $u.MaintenancePending){return}
    if(-not $u.Engine.VaultUnlocked){Show-UiVaultDialog ([pscustomobject]@{Kind='Export'});return}
    $picker=[Microsoft.Win32.SaveFileDialog]::new();$picker.Title='Export decrypted original to a new file';$picker.FileName=('Original-'+$cards[0].Record.GroupId+'.bin');$picker.Filter='Original file (*.*)|*.*'
    if($picker.ShowDialog($u.Window)){try{$operation=[Guid]::NewGuid().ToString('N');if($u.ExportRetry -and $u.ExportRetry.Database -eq $u.Engine.DatabasePath -and $u.ExportRetry.Group -eq $cards[0].Record.GroupId -and $u.ExportRetry.Path -ceq $picker.FileName){$operation=$u.ExportRetry.Operation};$u.ExportRetry=[pscustomobject]@{Database=$u.Engine.DatabasePath;Group=$cards[0].Record.GroupId;Path=$picker.FileName;Operation=$operation};$id=$u.Engine.RequestEvidenceOriginalExport($cards[0].Record.GroupId,$picker.FileName,$operation);$u.MaintenancePending=$true;$u.BackgroundOperationId=$id;Set-UiMessage 'Verifying the original before writing the explicitly chosen output...'}catch{Set-UiMessage $_.Exception.Message}};Update-UiControls
}
function Apply-UiEvidenceResult($Result) {
    $u=$script:reviewUi
    if($Result.Kind -in @('VaultUnlock','VaultLock','VaultPasswordChange')){
        if($Result.RequestId -ne $u.VaultTask){return $true};$u.VaultTask='';$u.MaintenancePending=$false;$u.BackgroundOperationId='';$continuation=$u.VaultContinuation;$u.VaultContinuation=$null;Set-UiMessage $(if($Result.Success){$Result.Value}else{'Originals remain locked: '+$Result.Error})
        if($Result.Kind -eq 'VaultPasswordChange' -and $Result.Success){$u.PasswordRetry=$null}
        if(-not $Result.Success -and $continuation -and $continuation.Kind -eq 'Capture'){foreach($card in $u.Cards){if($continuation.Request.GroupIds -contains $card.Record.GroupId){$card.IsPending=$false}}}
        if($Result.Success -and $u.Engine.VaultUnlocked -and $continuation){switch($continuation.Kind){'Capture'{Start-UiOriginalRetention $continuation.Request $continuation.Bucket};'Inspect'{Invoke-UiStoredInspect $continuation.Card};'Export'{Invoke-UiOriginalExport};'Package'{Invoke-UiPackageExport}}};Update-UiControls;return $true
    }
    if($Result.Kind -eq 'EvidenceInspect'){
        $task=$u.StoredTask;if(-not $task -or $task.Id -ne $Result.RequestId){if($Result.Evidence){$Result.Evidence.Dispose()};return $true};$u.StoredTask=$null;if($Result.RequestId -eq $u.BackgroundOperationId){$u.MaintenancePending=$false;$u.BackgroundOperationId=''}
        if($Result.Success -and $Result.Evidence -and $task -and $task.Id -eq $Result.RequestId -and $task.View -eq $u.View.Current -and $task.Selection -eq $u.SelectionGeneration -and $u.Engine.VaultUnlocked -and $Result.Evidence.Epoch -eq $u.Engine.VaultGeneration){Show-UiStoredOriginal $Result.Evidence;Set-UiMessage 'Stored original authenticated and verified.'}else{if($Result.Evidence){$Result.Evidence.Dispose()};Set-UiMessage $(if($Result.Success){'Protected result discarded because the selection changed.'}else{'Original inspection failed: '+$Result.Error})};Update-UiControls;return $true
    }
    if($Result.Kind -in @('OriginalExport','EvidenceLimit','OriginalCleanup')){if($Result.RequestId -eq $u.BackgroundOperationId){$u.MaintenancePending=$false;$u.BackgroundOperationId=''};if($Result.Kind -eq 'OriginalExport' -and $Result.Success){$u.ExportRetry=$null};Set-UiMessage $(if($Result.Success){$Result.Value}else{$Result.Error});Update-UiControls;return $true};return $false
}
function Add-UiEvidenceControls($Window) {
    $u=$script:reviewUi;foreach($name in @('CaptureEvidenceButton','VaultButton','StoredOriginalButton','ExportOriginalButton')){$u.Controls[$name]=$Window.FindName($name)}
    foreach($entry in @{StoredWindow=$null;StoredTask=$null;VaultTask='';VaultContinuation=$null;VaultDialog=$null;RetentionRetry=$null;ExportRetry=$null;PasswordRetry=$null}.GetEnumerator()){$u | Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force}
    $u.Controls.CaptureEvidenceButton.Add_Click({Invoke-UiEvidenceCapture});$u.Controls.StoredOriginalButton.Add_Click({Invoke-UiStoredInspect});$u.Controls.ExportOriginalButton.Add_Click({Invoke-UiOriginalExport});$u.Controls.VaultButton.Add_Click({if($script:reviewUi.Engine.VaultUnlocked){Invoke-UiVaultLock}else{Show-UiVaultDialog}})
    foreach($gb in @(2.5,5)){$item=[Windows.Controls.MenuItem]::new();$item.Header="Retained-original guardrail: $gb GB";$item.Tag=$gb;$item.Add_Click({param($sender,$event) try{$u=$script:reviewUi;$u.BackgroundOperationId=$u.Engine.RequestEvidenceLimit([double]$sender.Tag);$u.MaintenancePending=$true;Set-UiMessage 'Saving the retained-original guardrail...';Update-UiControls}catch{Set-UiMessage $_.Exception.Message}});[void]$u.Controls.MaintenanceButton.ContextMenu.Items.Add($item)}
    $cleanup=[Windows.Controls.MenuItem]::new();$cleanup.Header='Discard unfinished original captures';$cleanup.Add_Click({try{$u=$script:reviewUi;$u.BackgroundOperationId=$u.Engine.RequestOriginalCleanup();$u.MaintenancePending=$true;Set-UiMessage 'Discarding hidden incomplete captures in bounded batches...';Update-UiControls}catch{Set-UiMessage $_.Exception.Message}});[void]$u.Controls.MaintenanceButton.ContextMenu.Items.Add($cleanup)
    $change=[Windows.Controls.MenuItem]::new();$change.Header='Change originals password...';$change.Add_Click({Show-UiPasswordChangeDialog});[void]$u.Controls.MaintenanceButton.ContextMenu.Items.Add($change)
}
function Show-UiPasswordChangeDialog {
    $u=$script:reviewUi;if($u.MaintenancePending -or -not $u.Engine.VaultExists){Set-UiMessage 'Wait for the current task and create an originals vault before changing its password.';return}
    $markup='<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Change originals password" Width="520" Height="470" ResizeMode="NoResize" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI"><Grid Margin="20"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><TextBlock Text="Enter the current password and choose a new one. Saved originals stay intact. Earlier database copies keep their old password. Keep the new password safe; it cannot be recovered." TextWrapping="Wrap" Margin="0,0,0,12"/><StackPanel Grid.Row="1"><TextBlock Text="Current password"/><PasswordBox x:Name="Password" Height="30" Margin="0,4,0,10"/></StackPanel><StackPanel Grid.Row="2"><TextBlock Text="New password"/><PasswordBox x:Name="NewPassword" Height="30" Margin="0,4,0,10"/></StackPanel><StackPanel Grid.Row="3"><TextBlock Text="Confirm new password"/><PasswordBox x:Name="Confirmation" Height="30" Margin="0,4"/></StackPanel><TextBlock Grid.Row="4" x:Name="Message" TextWrapping="Wrap" Margin="0,10"/><StackPanel Grid.Row="5" Orientation="Horizontal" HorizontalAlignment="Center"><Button x:Name="Unlock" Content="Change password" IsDefault="True" Padding="18,7" Margin="6"/><Button Content="Cancel" IsCancel="True" Padding="24,7" Margin="6"/></StackPanel></Grid></Window>'
    $dialog=[Windows.Markup.XamlReader]::Parse($markup);$dialog.Owner=$u.Window
    $dialog.FindName('Unlock').Add_Click({param($sender,$event)
        $window=[Windows.Window]::GetWindow($sender);$current=$null;$next=$null;$confirmation=$null
        try{$current=Get-UiPasswordBytes $window.FindName('Password').SecurePassword;$next=Get-UiPasswordBytes $window.FindName('NewPassword').SecurePassword;$confirmation=Get-UiPasswordBytes $window.FindName('Confirmation').SecurePassword
            if(-not $current.Length -or -not $next.Length){throw 'Enter both the current and new password.'};$same=$next.Length -eq $confirmation.Length;for($i=0;$i -lt [Math]::Min($next.Length,$confirmation.Length);$i++){if($next[$i] -ne $confirmation[$i]){$same=$false}};if(-not $same){throw 'The new passwords do not match.'}
            $u=$script:reviewUi;$operation=[Guid]::NewGuid().ToString('N');if($u.PasswordRetry -and $u.PasswordRetry.Database -eq $u.Engine.DatabasePath){$operation=$u.PasswordRetry.Operation};Clear-UiStoredView;$u.VaultContinuation=$null;$u.PasswordRetry=[pscustomobject]@{Database=$u.Engine.DatabasePath;Operation=$operation};$u.VaultTask=$u.Engine.RequestVaultPasswordChange($current,$next,$operation);$current=$null;$next=$null;$u.BackgroundOperationId=$u.VaultTask;$u.MaintenancePending=$true;Set-UiMessage 'Checking the current password and updating the encrypted content-key record...';$window.DialogResult=$true
        }catch{$window.FindName('Message').Text=$_.Exception.Message}finally{foreach($bytes in @($current,$next,$confirmation)){if($bytes){[Array]::Clear($bytes,0,$bytes.Length)}};foreach($name in @('Password','NewPassword','Confirmation')){$window.FindName($name).Clear()}}
    })
    $u.VaultDialog=$dialog;try{$dialog.ShowDialog() | Out-Null}finally{$u.VaultDialog=$null};Update-UiControls
}
