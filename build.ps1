<#
.SYNOPSIS
    Builds, tests and publishes FileDetector.

.EXAMPLE
    .\build.ps1                # restore, build, test
    .\build.ps1 -Publish       # also produce publish\FileDetector.exe (self-contained, single file)
    .\build.ps1 -Publish -FrameworkDependent   # smaller exe, requires the .NET runtime on the target
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$FrameworkDependent,
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
