$ErrorActionPreference = 'Stop'

# Audits the Avalonia views: every command binding must name a command the view-models define, every
# event handler named in XAML must exist in the code-behind, and the server page keeps two controls
# that must stay wired up.
$repoRoot = Split-Path -Parent $PSScriptRoot
$xamlFiles = @(Get-ChildItem (Join-Path $repoRoot 'HighPop.Desktop\Views') -Filter '*.axaml' -Recurse |
    ForEach-Object FullName)
$viewModelSource = (Get-ChildItem (Join-Path $repoRoot 'HighPop.UI\ViewModels') -Filter '*.cs' -Recurse |
    Get-Content -Raw) -join "`n"
$issues = [System.Collections.Generic.List[string]]::new()
$bindingCount = 0

foreach ($xamlFile in $xamlFiles) {
    $xaml = Get-Content $xamlFile -Raw
    try { $null = [xml]$xaml }
    catch { $issues.Add("$xamlFile is not valid XML: $($_.Exception.Message)") }

    foreach ($match in [regex]::Matches($xaml, 'Command="\{(?:Reflection)?Binding\s+([^,}\s]+)')) {
        # Dotted paths (Backups.XCommand, $parent[UserControl].DataContext.XCommand) are checked by
        # their last segment, so commands on per-page view-models are covered too.
        $command = ($match.Groups[1].Value -split '\.')[-1]
        if ($command -notlike '*Command') { continue }
        $bindingCount++
        $stem = $command -replace 'Command$', ''
        if ($viewModelSource -notmatch "\b$([regex]::Escape($stem))(?:Async)?\s*\(") {
            $issues.Add("$xamlFile binds $command, but no matching RelayCommand method was found")
        }
    }

    $codeBehind = "$xamlFile.cs"
    $code = if (Test-Path $codeBehind) { Get-Content $codeBehind -Raw } else { '' }
    foreach ($match in [regex]::Matches(
        $xaml,
        '\b(?:Click|Tapped|DoubleTapped|PointerPressed|PointerReleased|Closing|Opened|SelectionChanged|TextChanged|KeyDown)="([A-Za-z_][A-Za-z0-9_]*)"')) {
        $handler = $match.Groups[1].Value
        if ($code -notmatch "\b$([regex]::Escape($handler))\s*\(") {
            $issues.Add("$xamlFile references missing code-behind handler $handler")
        }
    }
}

$mainWindow = Get-Content (Join-Path $repoRoot 'HighPop.Desktop\Views\MainWindow.axaml') -Raw
if ($mainWindow -notmatch 'Command="\{Binding ForceStopCommand\}"\s+IsEnabled="\{Binding CanForceStop\}"') {
    $issues.Add('The server page must keep Force Stop bound to CanForceStop so it remains available while Oxide is Stopping')
}
if ($mainWindow -notmatch 'Text="\{Binding InstallProgressText\}"') {
    $issues.Add('The server page must display the current SteamCMD progress stage')
}

if ($issues.Count -gt 0) {
    $issues | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "UI surface audit passed: $($xamlFiles.Count) XAML files and $bindingCount direct command bindings."
