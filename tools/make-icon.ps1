# Generates assets\app.ico (and app-paused.ico) from code so the repo needs no binary art tooling.
# PNG-compressed ICO container (supported on Vista+). Run once; re-run only to change the art.
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

function New-Glyph {
    param([int]$Size, [switch]$Paused)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $Size / 32.0   # all coordinates authored on a 32x32 grid

    # --- folder body ---
    $bodyColor = if ($Paused) { [System.Drawing.Color]::FromArgb(255, 130, 130, 138) } else { [System.Drawing.Color]::FromArgb(255, 60, 130, 220) }
    $tabColor  = if ($Paused) { [System.Drawing.Color]::FromArgb(255, 160, 160, 168) } else { [System.Drawing.Color]::FromArgb(255, 96, 165, 250) }

    $tabBrush = New-Object System.Drawing.SolidBrush($tabColor)
    $bodyBrush = New-Object System.Drawing.SolidBrush($bodyColor)

    # tab across the top-left
    $g.FillRectangle($tabBrush, (2 * $s), (7 * $s), (12 * $s), (5 * $s))
    # main body
    $g.FillRectangle($bodyBrush, (2 * $s), (10 * $s), (28 * $s - 2 * $s), (24 * $s - 10 * $s))

    if (-not $Paused) {
        # --- green "new item" badge, bottom-right ---
        $badge = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 34, 180, 90))
        $d = 15 * $s
        $g.FillEllipse($badge, (16 * $s), (15 * $s), $d, $d)

        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float](2.6 * $s))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $cx = 16 * $s + $d / 2
        $cy = 15 * $s + $d / 2
        $arm = 4 * $s
        $g.DrawLine($pen, [float]($cx - $arm), [float]$cy, [float]($cx + $arm), [float]$cy)
        $g.DrawLine($pen, [float]$cx, [float]($cy - $arm), [float]$cx, [float]($cy + $arm))
        $pen.Dispose(); $badge.Dispose()
    } else {
        # --- pause bars badge ---
        $badge = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 60, 60, 66))
        $d = 15 * $s
        $g.FillEllipse($badge, (16 * $s), (15 * $s), $d, $d)
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $g.FillRectangle($white, (20 * $s), (18.5 * $s), (2.2 * $s), (8 * $s))
        $g.FillRectangle($white, (24.5 * $s), (18.5 * $s), (2.2 * $s), (8 * $s))
        $white.Dispose(); $badge.Dispose()
    }

    $tabBrush.Dispose(); $bodyBrush.Dispose(); $g.Dispose()
    return $bmp
}

function Save-Ico {
    param([string]$Path, [switch]$Paused)

    $sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
    $pngs = @()
    foreach ($size in $sizes) {
        $bmp = New-Glyph -Size $size -Paused:$Paused
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , @{ Size = $size; Bytes = $ms.ToArray() }
        $ms.Dispose(); $bmp.Dispose()
    }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    # ICONDIR
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)

    $offset = 6 + (16 * $pngs.Count)
    foreach ($p in $pngs) {
        $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)   # width, height
        $bw.Write([byte]0)                              # palette count
        $bw.Write([byte]0)                              # reserved
        $bw.Write([uint16]1)                            # colour planes
        $bw.Write([uint16]32)                           # bits per pixel
        $bw.Write([uint32]$p.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $p.Bytes.Length
    }
    foreach ($p in $pngs) { $bw.Write($p.Bytes) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    Write-Output "wrote $Path"
}

Save-Ico -Path (Join-Path $outDir 'app.ico')
Save-Ico -Path (Join-Path $outDir 'app-paused.ico') -Paused
