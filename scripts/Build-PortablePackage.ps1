param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Commit = "local"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publish = (Resolve-Path $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$baseName = "HighPop-v$Version-win-x64"
$stageRoot = Join-Path $output "package_stage"
$hprmRoot = Join-Path $stageRoot "HPRM"

if (-not (Test-Path (Join-Path $publish "HighPop.exe"))) {
    throw "HighPop.exe was not found in $publish"
}
if (-not (Test-Path (Join-Path $publish "assets"))) {
    throw "Published assets were not found in $publish"
}

New-Item $output -ItemType Directory -Force | Out-Null
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item $hprmRoot -ItemType Directory -Force | Out-Null

Copy-Item (Join-Path $publish "HighPop.exe") $hprmRoot
Copy-Item (Join-Path $publish "assets") $hprmRoot -Recurse
Copy-Item (Join-Path $repoRoot "CHANGELOG.md") $hprmRoot

$directExe = Join-Path $output "$baseName.exe"
$zip = Join-Path $output "$baseName.zip"
Copy-Item (Join-Path $publish "HighPop.exe") $directExe -Force
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $hprmRoot -DestinationPath $zip -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @(
        "HPRM/HighPop.exe",
        "HPRM/CHANGELOG.md",
        "HPRM/assets/README.txt",
        "HPRM/assets/presets/rust_highpop.json"
    )) {
        if ($entries -notcontains $required) { throw "Portable ZIP is missing $required" }
    }
    $unexpected = @($entries | Where-Object {
        -not $_.EndsWith('/') `
        -and $_ -ne "HPRM/HighPop.exe" `
        -and $_ -ne "HPRM/CHANGELOG.md" `
        -and -not $_.StartsWith("HPRM/assets/")
    })
    if ($unexpected.Count -gt 0) {
        throw "Portable ZIP contains unexpected files: $($unexpected -join ', ')"
    }
} finally {
    $archive.Dispose()
}

$releaseFiles = @($directExe, $zip)
foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))" |
        Set-Content "$file.sha256" -Encoding ascii
}

$manifest = [ordered]@{
    product = "HighPop Rust Manager"
    version = $Version
    runtime = "win-x64"
    commit = $Commit
    archiveRoot = "HPRM"
    files = @($releaseFiles | ForEach-Object {
        [ordered]@{
            name = [IO.Path]::GetFileName($_)
            sha256 = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = (Get-Item $_).Length
        }
    })
}
$manifest | ConvertTo-Json -Depth 4 |
    Set-Content (Join-Path $output "$baseName.manifest.json") -Encoding utf8

Remove-Item $stageRoot -Recurse -Force
Write-Host "Created $zip with only HPRM/HighPop.exe, HPRM/assets/, and HPRM/CHANGELOG.md."
