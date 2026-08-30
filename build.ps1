<#
.SYNOPSIS
    Builds, tests and publishes FileDetector.

.EXAMPLE
    .\build.ps1                # restore, build, test
    .\build.ps1 -Publish       # also produce publish\FileDetector.exe (self-contained, single file)
    .\build.ps1 -Publish -FrameworkDependent   # smaller exe, requires the .NET runtime on the target
    .\build.ps1 -Package       # build, test, and produce release zips + SHA256SUMS in dist\
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$FrameworkDependent,
    [switch]$Package,
    [switch]$SkipTests,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') {
        $env:Path += ';C:\Program Files\dotnet'
    } else {
        throw "dotnet SDK not found. Install it with: & ([scriptblock]::Create((Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -UseBasicParsing))) -Channel LTS"
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "== build ($Configuration) ==" -ForegroundColor Cyan
dotnet build FileDetector.slnx -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $SkipTests) {
    Write-Host "== test ==" -ForegroundColor Cyan
    dotnet test FileDetector.slnx -c $Configuration -v minimal --no-build
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

if ($Publish) {
    Write-Host "== publish ==" -ForegroundColor Cyan
    $selfContained = (-not $FrameworkDependent).ToString().ToLower()

    dotnet publish src\FileDetector\FileDetector.csproj `
        -c $Configuration `
        -r win-x64 `
        --self-contained $selfContained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o publish
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    $exe = Join-Path $PSScriptRoot 'publish\FileDetector.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "`npublished: $exe ($size MB)" -ForegroundColor Green
}

if ($Package) {
    Write-Host "== package ==" -ForegroundColor Cyan

    $csproj = Join-Path $PSScriptRoot 'src\FileDetector\FileDetector.csproj'
    $version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "could not read <Version> from $csproj" }

    $dist = Join-Path $PSScriptRoot 'dist'
    if (Test-Path $dist) { Get-ChildItem $dist -File | Remove-Item -Force }
    New-Item -ItemType Directory -Force -Path $dist | Out-Null

    # Two flavours: one that runs anywhere, one that is small if the runtime is already installed.
    $flavours = @(
        @{ Name = 'self-contained';      SelfContained = 'true';  Note = 'no .NET runtime needed' }
        @{ Name = 'framework-dependent'; SelfContained = 'false'; Note = 'requires the .NET 10 Desktop Runtime' }
    )

    foreach ($flavour in $flavours) {
        $stage = Join-Path $dist "stage-$($flavour.Name)"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

        # Single-file compression is only legal for self-contained bundles.
        $compression = if ($flavour.SelfContained -eq 'true') { 'true' } else { 'false' }

        dotnet publish $csproj `
            -c $Configuration `
            -r win-x64 `
            --self-contained $flavour.SelfContained `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=$compression `
            -o $stage
        if ($LASTEXITCODE -ne 0) { throw "publish ($($flavour.Name)) failed" }

        # Ship only the executable plus the docs; the .pdb is debug-only weight.
        Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force
        Copy-Item (Join-Path $PSScriptRoot 'README.md') $stage

        $zip = Join-Path $dist "FileDetector-$version-win-x64-$($flavour.Name).zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
        Remove-Item $stage -Recurse -Force

        $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
        Write-Host ("  {0,-46} {1,6} MB  ({2})" -f (Split-Path $zip -Leaf), $mb, $flavour.Note)
    }

    $sums = Join-Path $dist 'SHA256SUMS.txt'
    Get-ChildItem $dist -Filter *.zip | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
    } | Set-Content $sums -Encoding utf8

    Write-Host "`npackaged v$version into $dist" -ForegroundColor Green
    Get-Content $sums | ForEach-Object { Write-Host "  $_" }
}
