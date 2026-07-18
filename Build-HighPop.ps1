[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$packageRoot = Join-Path $artifacts "package\HighPop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0"
}

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item $publish -ItemType Directory -Force | Out-Null
New-Item (Join-Path $packageRoot "assets\presets") -ItemType Directory -Force | Out-Null

dotnet restore (Join-Path $root "HighPop.sln")
dotnet build (Join-Path $root "HighPop.sln") -c $Configuration --no-restore
dotnet run --project (Join-Path $root "HighPop.SmokeTests\HighPop.SmokeTests.csproj") -c $Configuration --no-build
dotnet publish (Join-Path $root "HighPop\HighPop.csproj") `
    -c $Configuration -r $Runtime --self-contained true -o $publish

Copy-Item (Join-Path $publish "HighPop.exe") $packageRoot
Copy-Item (Join-Path $root "HighPop\assets\README.txt") (Join-Path $packageRoot "assets")
Copy-Item (Join-Path $root "HighPop\assets\presets\*.json") (Join-Path $packageRoot "assets\presets")
Copy-Item -Path @(
    (Join-Path $root "LICENSE"),
    (Join-Path $root "NOTICE.md"),
    (Join-Path $root "README.md"),
    (Join-Path $root "CHANGELOG.md")
) -Destination $packageRoot

$version = (Select-String -Path (Join-Path $root "HighPop\HighPop.csproj") -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value
$zip = Join-Path $artifacts "HighPop-v$version-$Runtime.zip"
Compress-Archive -Path $packageRoot -DestinationPath $zip -CompressionLevel Optimal

Write-Host "Created $zip"
