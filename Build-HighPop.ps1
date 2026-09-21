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

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0"
}

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item $publish -ItemType Directory -Force | Out-Null

dotnet restore (Join-Path $root "HighPop.sln")
dotnet build (Join-Path $root "HighPop.sln") -c $Configuration --no-restore
dotnet run --project (Join-Path $root "HighPop.SmokeTests\HighPop.SmokeTests.csproj") -c $Configuration --no-build
dotnet publish (Join-Path $root "HighPop\HighPop.csproj") `
    -c $Configuration -r $Runtime --self-contained true -o $publish

$version = (Select-String -Path (Join-Path $root "HighPop\HighPop.csproj") -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value
& (Join-Path $root "scripts\Build-PortablePackage.ps1") `
    -PublishDirectory $publish `
    -OutputDirectory $artifacts `
    -Version $version `
    -Commit "local"
