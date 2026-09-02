<#
.SYNOPSIS
    Pushes the current version's tag and publishes a GitHub release with the packaged artifacts.

.DESCRIPTION
    Reads the version from the csproj, checks that dist\ holds matching artifacts and that the
    CHANGELOG has a section for it, then pushes main + the tag and creates the release with the
    CHANGELOG section as its notes. Re-runnable: an existing release is updated rather than
    duplicated.

.EXAMPLE
    .\tools\publish-release.ps1              # publish the current version
    .\tools\publish-release.ps1 -DryRun      # show what would happen and stop
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$PreRelease
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI not found. Install it, then run: gh auth login"
}
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "not logged in to GitHub. Run: gh auth login" }

# The CHANGELOG is UTF-8 with em dashes; PowerShell 5.1's Get-Content/Set-Content would turn those
# into mojibake in the published release notes, so go through .NET with an explicit encoding.
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$csproj = Join-Path $root 'src\FileDetector\FileDetector.csproj'
if ([System.IO.File]::ReadAllText($csproj) -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') {
    throw "no <VersionPrefix> element in $csproj"
}
$version = $Matches[1].Trim()
$tag = "v$version"

$dirty = & git status --porcelain
if ($dirty) { throw "working tree is not clean; commit before releasing" }

# Artifacts must exist and match this version, or the release would ship the previous build.
$dist = Join-Path $root 'dist'
$assets = @(
    "FileDetector-$version-win-x64-self-contained.zip"
    "FileDetector-$version-win-x64-framework-dependent.zip"
    'SHA256SUMS.txt'
) | ForEach-Object { Join-Path $dist $_ }

$missing = $assets | Where-Object { -not (Test-Path $_) }
if ($missing) {
    throw "missing artifacts for $version (run .\build.ps1 -Package):`n  " + ($missing -join "`n  ")
}

# Release notes come from the CHANGELOG section for this version.
$lines = [System.IO.File]::ReadAllLines((Join-Path $root 'CHANGELOG.md'))
$start = ($lines | Select-String -Pattern ("^##\s+" + [regex]::Escape($version)) | Select-Object -First 1)
if (-not $start) { throw "CHANGELOG.md has no '## $version' section" }
$rest = $lines[$start.LineNumber..($lines.Count - 1)]
$nextHeading = ($rest | Select-String -Pattern '^##\s' | Select-Object -First 1)
$body = if ($nextHeading) { $rest[0..($nextHeading.LineNumber - 2)] } else { $rest }
if (($body -join '') -match 'TODO') { throw "CHANGELOG section for $version still contains a TODO" }

$notes = Join-Path $env:TEMP "FileDetector-release-notes-$version.md"
[System.IO.File]::WriteAllLines($notes, [string[]]$body, $Utf8NoBom)

if (-not (& git tag -l $tag)) { throw "tag $tag does not exist (run .\tools\bump-version.ps1 ... -Tag)" }

Write-Host "release $tag" -ForegroundColor Cyan
$assets | ForEach-Object { "  {0,-52} {1,6} MB" -f (Split-Path $_ -Leaf), [math]::Round((Get-Item $_).Length / 1MB, 1) }

if ($DryRun) {
    Write-Host "`n-DryRun: stopping before push. Notes preview:" -ForegroundColor Yellow
    Get-Content $notes | Select-Object -First 12
    return
}

& git push origin HEAD
if ($LASTEXITCODE -ne 0) { throw "push failed" }
& git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "tag push failed" }

$existing = & gh release view $tag --json tagName 2>$null
if ($LASTEXITCODE -eq 0 -and $existing) {
    Write-Host "release $tag already exists - updating notes and re-uploading assets"
    & gh release edit $tag --notes-file $notes
    & gh release upload $tag @assets --clobber
} else {
    $extra = @()
    if ($PreRelease) { $extra += '--prerelease' }
    & gh release create $tag @assets --title "FileDetector $version" --notes-file $notes @extra
}
if ($LASTEXITCODE -ne 0) { throw "release failed" }

Remove-Item $notes -Force
& gh release view $tag --json url,isDraft,assets | ConvertFrom-Json | Format-List url, isDraft
