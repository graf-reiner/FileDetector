<#
.SYNOPSIS
    Bumps the project version, opens a CHANGELOG section for it, and optionally commits and tags.

.DESCRIPTION
    The version lives in exactly one place: <VersionPrefix> in src\FileDetector\FileDetector.csproj.
    Everything else derives from it: AssemblyVersion, FileVersion, the AppVersion shown in the app,
    the zip names produced by build.ps1, and the git tag. This script is the only thing that edits it.

.EXAMPLE
    .\tools\bump-version.ps1 patch            # 1.0.0 -> 1.0.1, edit files only
    .\tools\bump-version.ps1 minor -Commit    # 1.0.1 -> 1.1.0, commit the change
    .\tools\bump-version.ps1 1.2.0 -Commit -Tag
    .\tools\bump-version.ps1 -Show            # print the current version and exit
#>
[CmdletBinding()]
param(
    # "major", "minor", "patch", or an explicit version such as "1.2.0".
    [Parameter(Position = 0)]
    [string]$Bump,

    [switch]$Commit,
    [switch]$Tag,
    [switch]$Show,

    # Bump on top of uncommitted changes. Off by default so a bump commit contains only the bump.
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$csproj = Join-Path $root 'src\FileDetector\FileDetector.csproj'
$changelog = Join-Path $root 'CHANGELOG.md'

$csprojText = Get-Content $csproj -Raw
if ($csprojText -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') {
    throw "no <VersionPrefix> element in $csproj"
}
$current = $Matches[1].Trim()

if ($Show -or -not $Bump) {
    Write-Host "current version: $current"
    if (-not $Bump) { Write-Host "usage: .\tools\bump-version.ps1 <major|minor|patch|x.y.z> [-Commit] [-Tag]" }
    return
}

if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "current version '$current' is not major.minor.patch" }
$major, $minor, $patch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]

$next = switch ($Bump.ToLowerInvariant()) {
    'major' { "$($major + 1).0.0" }
    'minor' { "$major.$($minor + 1).0" }
    'patch' { "$major.$minor.$($patch + 1)" }
    default {
        if ($Bump -notmatch '^\d+\.\d+\.\d+$') { throw "'$Bump' is not major|minor|patch or an x.y.z version" }
        $Bump
    }
}

if ([version]$next -le [version]$current) { throw "$next is not newer than the current $current" }

if (-not $AllowDirty) {
    $dirty = & git status --porcelain
    if ($dirty) { throw "working tree is not clean; commit or stash first, or pass -AllowDirty" }
}

$existingTag = & git tag -l "v$next"
if ($existingTag) { throw "tag v$next already exists" }

# 1. The single source of truth.
$csprojText = $csprojText -replace '<VersionPrefix>[^<]+</VersionPrefix>', "<VersionPrefix>$next</VersionPrefix>"
Set-Content $csproj -Value $csprojText -NoNewline -Encoding utf8

# 2. Open a CHANGELOG section. An existing "## Unreleased" is promoted to the new version; otherwise
#    a stub is inserted for the notes to be written into before releasing.
$today = (Get-Date).ToString('yyyy-MM-dd')
# Built from a code point rather than typed literally: this file is read as ANSI by Windows
# PowerShell 5.1, which mangles non-ASCII source and breaks the parser.
$dash = [char]0x2014
$heading = "## $next $dash $today"
$lines = [System.Collections.Generic.List[string]](Get-Content $changelog)
$unreleased = $lines | Select-String -Pattern '^##\s+Unreleased' | Select-Object -First 1

if ($unreleased) {
    $lines[$unreleased.LineNumber - 1] = $heading
    $promoted = $true
} else {
    $header = $lines | Select-String -Pattern '^#\s+Changelog' | Select-Object -First 1
    $insertAt = if ($header) { $header.LineNumber } else { 0 }
    $lines.InsertRange($insertAt, [string[]]@('', $heading, '', '- TODO: describe what changed in this release.'))
    $promoted = $false
}
Set-Content $changelog -Value $lines -Encoding utf8

Write-Host "version: $current -> $next" -ForegroundColor Green
if ($promoted) {
    Write-Host "CHANGELOG: promoted the Unreleased section to $next"
} else {
    Write-Host "CHANGELOG: inserted a stub for $next - fill it in before releasing" -ForegroundColor Yellow
}

if ($Commit) {
    & git add $csproj $changelog
    & git commit -m "Version $next"
    if ($LASTEXITCODE -ne 0) { throw "commit failed" }
}

if ($Tag) {
    if (-not $Commit) {
        $stillDirty = & git status --porcelain
        if ($stillDirty) { throw "-Tag needs the bump committed first; re-run with -Commit -Tag" }
    }
    & git tag -a "v$next" -m "FileDetector $next"
    if ($LASTEXITCODE -ne 0) { throw "tag failed" }
    Write-Host "tagged v$next" -ForegroundColor Green
}

Write-Host ""
Write-Host "next steps:" -ForegroundColor Cyan
if (-not $promoted) { Write-Host "  1. write the CHANGELOG entry for $next" }
Write-Host "  2. .\build.ps1 -Package"
Write-Host "  3. .\tools\publish-release.ps1        # pushes and creates the GitHub release"
