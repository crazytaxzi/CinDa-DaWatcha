[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',
    [string]$GeckoDriverArchive = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$stagingPath = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "CinDa-DaWatcha-v$Version-win-x64"))
$archivePath = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "CinDa-DaWatcha-v$Version-win-x64.zip"))
$checksumPath = "$archivePath.sha256"
$cachePath = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'cache'))
$downloadPath = Join-Path $cachePath 'geckodriver-v0.37.1-win64.zip'
$expectedDriverArchiveSha256 = 'dfed9315abe8d2fbc1b6161a2ee8002452e79cf05ee92fdc653a4e26bc35edd8'
$driverUrl = 'https://github.com/mozilla/geckodriver/releases/download/v0.37.1/geckodriver-v0.37.1-win64.zip'

foreach ($target in @($stagingPath, $archivePath, $checksumPath)) {
    if (-not $target.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository artifacts directory: $target"
    }
}

New-Item -ItemType Directory -Force -Path $artifactsRoot, $cachePath | Out-Null
foreach ($target in @($stagingPath, $archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

if ([string]::IsNullOrWhiteSpace($GeckoDriverArchive)) {
    if (-not (Test-Path -LiteralPath $downloadPath)) {
        Write-Host 'Downloading pinned GeckoDriver v0.37.1 from Mozilla GitHub...'
        Invoke-WebRequest -Uri $driverUrl -OutFile $downloadPath
    }
    $GeckoDriverArchive = $downloadPath
}
$GeckoDriverArchive = [IO.Path]::GetFullPath($GeckoDriverArchive)
$driverArchiveSha256 = (Get-FileHash -LiteralPath $GeckoDriverArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($driverArchiveSha256 -ne $expectedDriverArchiveSha256) {
    throw "GeckoDriver archive checksum mismatch. Expected $expectedDriverArchiveSha256; received $driverArchiveSha256."
}
Write-Host "Verified GeckoDriver archive SHA-256: $driverArchiveSha256"

dotnet publish (Join-Path $repositoryRoot 'src\CinDa.DaWatcha.App\CinDa.DaWatcha.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:ContinuousIntegrationBuild=true `
    -o $stagingPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$driverExtractPath = Join-Path $artifactsRoot 'geckodriver-extract'
if (-not $driverExtractPath.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Invalid GeckoDriver extraction path.'
}
try {
    if (Test-Path -LiteralPath $driverExtractPath) {
        Remove-Item -LiteralPath $driverExtractPath -Recurse -Force
    }
    Expand-Archive -LiteralPath $GeckoDriverArchive -DestinationPath $driverExtractPath
    Copy-Item -LiteralPath (Join-Path $driverExtractPath 'geckodriver.exe') `
        -Destination (Join-Path $stagingPath 'geckodriver.exe')
}
finally {
    if (Test-Path -LiteralPath $driverExtractPath) {
        Remove-Item -LiteralPath $driverExtractPath -Recurse -Force
    }
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $stagingPath
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'watch-config.example.json') -Destination $stagingPath
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') `
    -Destination (Join-Path $stagingPath 'docs') -Recurse

$notice = @"
CinDa-DaWatcha third-party notices

GeckoDriver v0.37.1
Copyright Mozilla contributors
License: Mozilla Public License 2.0
Source and release: https://github.com/mozilla/geckodriver/releases/tag/v0.37.1
Packaged archive SHA-256: $expectedDriverArchiveSha256

Other managed dependencies and their license metadata are recorded in the
application .deps.json file and their respective NuGet packages.
"@
Set-Content -LiteralPath (Join-Path $stagingPath 'THIRD-PARTY-NOTICES.txt') `
    -Value $notice -Encoding utf8

Get-ChildItem -LiteralPath $stagingPath -Filter '*.pdb' -File |
    Remove-Item -Force
$createdumpPath = Join-Path $stagingPath 'createdump.exe'
if (Test-Path -LiteralPath $createdumpPath) {
    Remove-Item -LiteralPath $createdumpPath -Force
}
$seleniumManagers = Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
    Where-Object { $_.Name -like 'selenium-manager*' }
foreach ($manager in $seleniumManagers) {
    if (-not $manager.FullName.StartsWith(
            $stagingPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected Selenium Manager path: $($manager.FullName)"
    }
    Remove-Item -LiteralPath $manager.FullName -Force
}
if (Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        Where-Object { $_.Name -like 'selenium-manager*' }) {
    throw 'Selenium Manager remained in the package.'
}

Write-Host 'Running browser DOM smoke test with the exact packaged GeckoDriver...'
dotnet run --project (Join-Path $repositoryRoot 'tools\CinDa.DaWatcha.BrowserSmoke') `
    -c Release -- --driver (Join-Path $stagingPath 'geckodriver.exe')
if ($LASTEXITCODE -ne 0) { throw 'Packaged browser smoke test failed.' }

Compress-Archive -LiteralPath $stagingPath -DestinationPath $archivePath `
    -CompressionLevel Optimal
$archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath `
    -Value "$archiveSha256  $([IO.Path]::GetFileName($archivePath))" -Encoding ascii

Write-Host "PACKAGE: $archivePath"
Write-Host "SHA256:  $archiveSha256"
