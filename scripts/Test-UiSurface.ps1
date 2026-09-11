$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$xamlFiles = @(
    Join-Path $repoRoot 'HighPop\MainWindow.xaml'
) + @(Get-ChildItem (Join-Path $repoRoot 'HighPop\Views') -Filter '*.xaml' | ForEach-Object FullName)
$viewModelSource = (Get-ChildItem (Join-Path $repoRoot 'HighPop\ViewModels') -Filter '*.cs' |
    Get-Content -Raw) -join "`n"
$issues = [System.Collections.Generic.List[string]]::new()
$bindingCount = 0

foreach ($xamlFile in $xamlFiles) {
    $xaml = Get-Content $xamlFile -Raw
    try { $null = [xml]$xaml }
    catch { $issues.Add("$xamlFile is not valid XML: $($_.Exception.Message)") }

    foreach ($match in [regex]::Matches($xaml, 'Command="\{Binding\s+([^,}\s]+)')) {
        $command = $match.Groups[1].Value
        if ($command.Contains('.') -or $command -in @('DataContext', 'FileBrowser')) { continue }
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
        '(?:Click|MouseLeftButtonDown|MouseLeftButtonUp|MouseDoubleClick)="([A-Za-z_][A-Za-z0-9_]*)"')) {
        $handler = $match.Groups[1].Value
        if ($code -notmatch "\b$([regex]::Escape($handler))\s*\(") {
            $issues.Add("$xamlFile references missing code-behind handler $handler")
        }
    }
}

if ($issues.Count -gt 0) {
    $issues | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "UI surface audit passed: $($xamlFiles.Count) XAML files and $bindingCount direct command bindings."
