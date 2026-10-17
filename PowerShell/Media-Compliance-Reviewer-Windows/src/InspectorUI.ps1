function Get-UiInspectorDpi($Window) {
    $presentation=[Windows.PresentationSource]::FromVisual($Window)
    if($presentation -and $presentation.CompositionTarget){return $presentation.CompositionTarget.TransformToDevice}
    return [Windows.Media.Matrix]::Identity
}
function Update-UiInspectorGeometry([switch]$Center) {
    $u=$script:reviewUi;$window=$u.Inspector;if(-not $window -or -not $window.Tag){return};$s=$window.Tag;$view=$window.FindName('Viewport');$picture=$window.FindName('Picture');$dpi=Get-UiInspectorDpi $window
    $width=[Math]::Max(1,$view.ActualWidth-2)*$dpi.M11;$height=[Math]::Max(1,$view.ActualHeight-2)*$dpi.M22
    $sourceWidth=$s.SourceWidth;$sourceHeight=$s.SourceHeight;if($sourceWidth -le 0 -and $s.Image){$sourceWidth=$s.Image.PixelWidth;$sourceHeight=$s.Image.PixelHeight}
    if($sourceWidth -gt 0){
        $oldWidth=[Math]::Max(1,$view.ExtentWidth);$oldHeight=[Math]::Max(1,$view.ExtentHeight);$cx=($view.HorizontalOffset+$view.ViewportWidth/2)/$oldWidth;$cy=($view.VerticalOffset+$view.ViewportHeight/2)/$oldHeight
        $s.Plan=[MediaReviewer.InspectionPlan]::Create($sourceWidth,$sourceHeight,$width,$height,$s.Mode,$s.Zoom);$picture.Width=$s.Plan.DisplayWidth/$dpi.M11;$picture.Height=$s.Plan.DisplayHeight/$dpi.M22;$picture.Source=$s.Image;$view.UpdateLayout()
        if($s.Mode -eq 'Fit'){$view.ScrollToHorizontalOffset(0);$view.ScrollToVerticalOffset(0)}elseif($Center -or $s.Mode -eq 'Fill'){$view.ScrollToHorizontalOffset([Math]::Max(0,($view.ExtentWidth-$view.ViewportWidth)/2));$view.ScrollToVerticalOffset([Math]::Max(0,($view.ExtentHeight-$view.ViewportHeight)/2))}else{$view.ScrollToHorizontalOffset([Math]::Max(0,$cx*$view.ExtentWidth-$view.ViewportWidth/2));$view.ScrollToVerticalOffset([Math]::Max(0,$cy*$view.ExtentHeight-$view.ViewportHeight/2))}
        $kind=if($s.SourceLoaded){'Source'}else{'Saved preview'};$details=if($s.Image){"$($s.Image.PixelWidth) × $($s.Image.PixelHeight) preview"}else{'Loading'};$limited=if($s.Plan.Limited -or ($s.SourceLoaded -and $s.Image -and [Math]::Max($s.Image.PixelWidth,$s.Image.PixelHeight)+1 -lt [Math]::Min([Math]::Max($s.SourceWidth,$s.SourceHeight),[Math]::Max($s.Plan.DisplayWidth,$s.Plan.DisplayHeight)))){' · sampled for preview limits'}else{''};$window.FindName('ZoomLabel').Text=('{0:0.##}% · {1} {2} × {3} · {4}{5}' -f ($s.Plan.Scale*100),$kind,$sourceWidth,$sourceHeight,$details,$limited)
    }
    foreach($name in @('ZoomInButton','ZoomOutButton','ActualButton')){$window.FindName($name).IsEnabled=$s.SourceLoaded -or $s.Failed};$window.FindName('ActualButton').Content=if($s.Failed){'100% preview'}else{'100%'}
}
function Schedule-UiInspectorPreview {
    $u=$script:reviewUi;if(-not $u.Inspector -or -not $u.Inspector.Tag){return};$s=$u.Inspector.Tag
    $s.Timer.Stop();[void]$u.InspectView.Advance();$u.Loader.CancelInspect($u.InspectView.Current);$s.Generation=$u.InspectView.Current;$s.Loading=$false;Update-UiInspectorGeometry
    if($s.Failed){return};$s.Loading=$true;$u.Inspector.FindName('Status').Text='Preparing source preview…';$s.Timer.Start()
}
function Request-UiInspectorPreview {
    $u=$script:reviewUi;$window=$u.Inspector;if(-not $window -or -not $window.Tag){return};$s=$window.Tag;$s.Timer.Stop();if($s.Failed){$s.Loading=$false;return};Update-UiInspectorGeometry
    if($s.SourceLoaded){$side=$s.Plan.DecodeSide}else{$view=$window.FindName('Viewport');$dpi=Get-UiInspectorDpi $window;$side=[int][Math]::Max(1,[Math]::Min([MediaReviewer.InspectionPlan]::MaxSide,[Math]::Ceiling([Math]::Max($view.ActualWidth*$dpi.M11,$view.ActualHeight*$dpi.M22))))}
    if($s.SourceLoaded -and $side -le $s.LoadedSide){$s.Loading=$false;$window.FindName('Status').Text='Source preview ready. Drag to pan when zoomed; Ctrl+wheel changes zoom.';return}
    $s.RequestedSide=$side;$s.Loading=$true;$window.FindName('Status').Text='Loading source preview…';try{if(-not $u.Loader.QueueInspect($s.Row,$s.Generation,$side)){$s.Loading=$false;$window.FindName('Status').Text='The inspector is closing. The cached preview remains available.'}}catch{$s.Loading=$false;$window.FindName('Status').Text=$_.Exception.Message}
}
function Set-UiInspectorMode([string]$Mode,[double]$Zoom=1) {
    $u=$script:reviewUi;if(-not $u.Inspector -or -not $u.Inspector.Tag){return};$s=$u.Inspector.Tag;$s.Mode=$Mode;$s.Zoom=$Zoom;Update-UiInspectorGeometry -Center;Schedule-UiInspectorPreview
}
function Step-UiInspectorZoom([double]$Factor) {
    $u=$script:reviewUi;if(-not $u.Inspector -or -not $u.Inspector.Tag -or -not $u.Inspector.Tag.Plan){return};$s=$u.Inspector.Tag;if(-not $s.SourceLoaded -and -not $s.Failed){return};$s.Mode='Zoom';$s.Zoom=$s.Plan.Scale*$Factor;Update-UiInspectorGeometry;Schedule-UiInspectorPreview
}
function Apply-UiInspectorImage($Image) {
    $u=$script:reviewUi;$window=$u.Inspector;if(-not $window -or -not $window.Tag){return $false};$s=$window.Tag
    if(-not $u.InspectView.Accept($Image.Generation) -or $Image.GroupId -ne $s.Row.GroupId -or $Image.ContentId -ne $s.Row.ContentId){return $false}
    $s.Loading=$false
    if($Image.Image){$s.Image=$Image.Image;$s.SourceWidth=$Image.SourceWidth;$s.SourceHeight=$Image.SourceHeight;$s.LoadedSide=$Image.RequestedSide;$s.SourceLoaded=$true;$s.Failed=$false;$window.FindName('Status').Text='Source preview ready. Drag to pan when zoomed; Ctrl+wheel changes zoom.'}
    else{$s.Failed=$true;$window.FindName('Status').Text='Source unavailable; saved preview retained. '+$Image.Error}
    Update-UiInspectorGeometry;return $true
}
function Update-UiInspectorThumbnail($Card) {
    $u=$script:reviewUi;if($u.Inspector -and $u.Inspector.Tag){$s=$u.Inspector.Tag;if(-not $s.SourceLoaded -and $s.Row.GroupId -eq $Card.Record.GroupId -and $s.Row.ContentId -eq $Card.Record.ContentId){$s.Image=$Card.Image;Update-UiInspectorGeometry}}
}
function Show-UiInspector($Card) {
    if($Card.Record.Status -eq 'EVIDENCE' -or ($Card.Record.OriginalRetained -and [string]::IsNullOrEmpty($Card.Record.RelativePath))){if($script:reviewUi.Inspector){$script:reviewUi.Inspector.Close()};Invoke-UiStoredInspect $Card;return}
    $u=$script:reviewUi
    if($script:reviewUi.SourceRefreshRequired){Set-UiMessage 'Refreshing source locations; cached gallery images remain available.';return}
    if(-not $u.Inspector){
        $markup=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Inspect media" Width="900" Height="650" MinWidth="500" MinHeight="400" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI">
  <Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="64"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <StackPanel><TextBlock x:Name="Name" FontSize="18" TextTrimming="CharacterEllipsis"/><TextBlock x:Name="Path" TextTrimming="CharacterEllipsis" Margin="0,4"/></StackPanel>
    <WrapPanel Grid.Row="1" Margin="0,6,0,10"><Button x:Name="PreviousItem" Content="Previous item" Padding="10,6" Margin="0,0,6,4"/><Button x:Name="NextItem" Content="Next item" Padding="10,6" Margin="0,0,6,4"/><Button x:Name="Details" Content="Details / notes" Padding="10,6" Margin="0,0,8,4"/><TextBlock x:Name="Position" VerticalAlignment="Center" Margin="0,0,10,4"/><Button x:Name="FitButton" Content="Fit" Padding="14,6" Margin="0,0,6,4" ToolTip="Show the whole image"/><Button x:Name="FillButton" Content="Fill" Padding="14,6" Margin="0,0,6,4" ToolTip="Fill the viewport and crop around the center"/><Button x:Name="ZoomOutButton" Content="−" Padding="14,6" Margin="0,0,6,4" ToolTip="Zoom out"/><Button x:Name="ZoomInButton" Content="+" Padding="14,6" Margin="0,0,6,4" ToolTip="Zoom in"/><Button x:Name="ActualButton" Content="100%" Padding="14,6" Margin="0,0,10,4" ToolTip="Display at source pixel scale; preview detail remains bounded"/><TextBlock x:Name="ZoomLabel" VerticalAlignment="Center" TextWrapping="Wrap"/></WrapPanel>
    <Border Grid.Row="2" Background="#EAF0F5" ClipToBounds="True"><ScrollViewer x:Name="Viewport" HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto" CanContentScroll="False" Focusable="True"><Image x:Name="Picture" Stretch="Uniform" HorizontalAlignment="Center" VerticalAlignment="Center"/></ScrollViewer></Border>
    <TextBlock Grid.Row="3" x:Name="Status" TextWrapping="Wrap" Margin="0,8,0,0"/>
    <Button Grid.Row="4" x:Name="Close" Content="Close" HorizontalAlignment="Center" Padding="22,6" Margin="0,8,0,0" IsCancel="True"/>
  </Grid>
</Window>
'@
        $u.Inspector=[Windows.Markup.XamlReader]::Parse($markup);$u.Inspector.Owner=$u.Window
        $u.Inspector.FindName('Close').Add_Click({$script:reviewUi.Inspector.Close()})
        $u.Inspector.FindName('FitButton').Add_Click({Set-UiInspectorMode 'Fit'});$u.Inspector.FindName('FillButton').Add_Click({Set-UiInspectorMode 'Fill'});$u.Inspector.FindName('ActualButton').Add_Click({Set-UiInspectorMode 'Zoom' 1});$u.Inspector.FindName('ZoomInButton').Add_Click({Step-UiInspectorZoom 1.25});$u.Inspector.FindName('ZoomOutButton').Add_Click({Step-UiInspectorZoom .8})
        $u.Inspector.FindName('PreviousItem').Add_Click({$card=Get-UiPageNeighbor $script:reviewUi.InspectGroup -1;if($card){Show-UiInspector $card}});$u.Inspector.FindName('NextItem').Add_Click({$card=Get-UiPageNeighbor $script:reviewUi.InspectGroup 1;if($card){Show-UiInspector $card}});$u.Inspector.FindName('Details').Add_Click({$card=Get-UiPageNeighbor $script:reviewUi.InspectGroup 0;if($card){Show-UiDetails $card}})
        $u.Inspector.FindName('Viewport').Add_SizeChanged({Schedule-UiInspectorPreview})
        $u.Inspector.FindName('Viewport').Add_PreviewMouseWheel({param($sender,$event) if(([Windows.Input.Keyboard]::Modifiers -band [Windows.Input.ModifierKeys]::Control) -ne 0){Step-UiInspectorZoom $(if($event.Delta -gt 0){1.25}else{.8});$event.Handled=$true}})
        $u.Inspector.FindName('Picture').Add_MouseLeftButtonDown({param($sender,$event) $w=[Windows.Window]::GetWindow($sender);$s=$w.Tag;$view=$w.FindName('Viewport');$s.Pan=$event.GetPosition($view);$s.PanX=$view.HorizontalOffset;$s.PanY=$view.VerticalOffset;[void]$sender.CaptureMouse();$event.Handled=$true})
        $u.Inspector.FindName('Picture').Add_MouseMove({param($sender,$event) $w=[Windows.Window]::GetWindow($sender);$s=$w.Tag;if($s -and $s.Pan -and $event.LeftButton -eq [Windows.Input.MouseButtonState]::Pressed){$view=$w.FindName('Viewport');$position=$event.GetPosition($view);$view.ScrollToHorizontalOffset($s.PanX+$s.Pan.X-$position.X);$view.ScrollToVerticalOffset($s.PanY+$s.Pan.Y-$position.Y);$event.Handled=$true}})
        $u.Inspector.FindName('Picture').Add_MouseLeftButtonUp({param($sender,$event) $w=[Windows.Window]::GetWindow($sender);if($w.Tag){$w.Tag.Pan=$null};$sender.ReleaseMouseCapture();$event.Handled=$true})
        $u.Inspector.Add_Closed({param($sender,$event) if($sender.Tag){$sender.Tag.Timer.Stop();$sender.Tag.Image=$null;$sender.Tag.Row=$null;$sender.Tag=$null};$sender.FindName('Picture').Source=$null;$sender.FindName('Name').Text='';$sender.FindName('Path').Text='';$u=$script:reviewUi;if($u.Inspector -eq $sender){[void]$u.InspectView.Advance();$u.Loader.CancelInspect($u.InspectView.Current);$u.Inspector=$null}})
    }
    $window=$u.Inspector;if($window.Tag){$window.Tag.Timer.Stop();$window.Tag.Image=$null};$window.FindName('Picture').Source=$null;[void]$u.InspectView.Advance();$u.Loader.CancelInspect($u.InspectView.Current);$u.InspectGroup=$Card.Record.GroupId
    $timer=[Windows.Threading.DispatcherTimer]::new();$timer.Interval=[TimeSpan]::FromMilliseconds(180);$timer.Add_Tick({Request-UiInspectorPreview})
    $window.Tag=[pscustomobject]@{Row=$Card.Record;Image=$Card.Image;SourceWidth=0;SourceHeight=0;SourceLoaded=$false;Failed=$false;Loading=$true;Mode='Fit';Zoom=1.0;Plan=$null;LoadedSide=0;RequestedSide=0;Generation=$u.InspectView.Current;Timer=$timer;Pan=$null;PanX=0.0;PanY=0.0}
    Update-UiItemNavigation $window $u.InspectGroup 'PreviousItem' 'NextItem' 'Position'
    $window.FindName('Name').Text=$Card.Name;$window.FindName('Path').Text=$Card.FullPath;$window.FindName('Path').ToolTip=$Card.FullPath;$window.FindName('ZoomLabel').Text='Loading source dimensions…';$window.FindName('Status').Text='Loading source preview…';$window.Show();$window.Activate() | Out-Null;Update-UiInspectorGeometry -Center;Schedule-UiInspectorPreview
}
