$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$bin = Join-Path $root 'bin'

$csc = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio' -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Roslyn\\csc\.exe$' } |
    Sort-Object { [version]($_.VersionInfo.FileVersion -replace '[^0-9\.].*$', '') } -Descending |
    Select-Object -First 1

if (-not $csc) {
    throw 'Could not find a Roslyn csc.exe. Install .NET Framework targeting tools or Visual Studio Build Tools.'
}

$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    (Join-Path $framework 'mscorlib.dll'),
    (Join-Path $framework 'System.dll'),
    (Join-Path $framework 'System.Core.dll'),
    (Join-Path $framework 'System.Drawing.dll'),
    (Join-Path $framework 'System.Windows.Forms.dll')
)

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "Missing reference assembly: $reference"
    }
}

New-Item -ItemType Directory -Force -Path $bin | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root 'assets') | Out-Null

Add-Type -AssemblyName System.Drawing

$iconPath = Join-Path $root 'assets\icon.ico'

# Same geometry as assets\icon.svg. Each size is drawn directly so the
# tray and taskbar icons stay sharp instead of scaling one bitmap.
function New-DimIconBitmap([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = $size / 1024.0
    $plate = New-Object System.Drawing.RectangleF (26 * $scale), (26 * $scale), (972 * $scale), (972 * $scale)
    $radius = 220 * $scale
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = [Math]::Min($radius * 2, [Math]::Min($plate.Width, $plate.Height))
    $path.AddArc($plate.X, $plate.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($plate.Right - $diameter, $plate.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($plate.Right - $diameter, $plate.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($plate.X, $plate.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $black = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)
    $graphics.FillPath($black, $path)
    $black.Dispose()
    $path.Dispose()

    $center = 512 * $scale
    $discRadius = 288 * $scale
    $disc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disc.AddEllipse(($center - $discRadius), ($center - $discRadius), ($discRadius * 2), ($discRadius * 2))
    $graphics.SetClip($disc)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 129, 200, 242))
    $paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 237, 249, 247))
    $graphics.FillRectangle($blue, ($center - $discRadius), ($center - $discRadius), $discRadius, ($discRadius * 2))
    $graphics.FillRectangle($paper, $center, ($center - $discRadius), $discRadius, ($discRadius * 2))
    $blue.Dispose()
    $paper.Dispose()
    $disc.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Get-IconImageBytes($bitmap) {
    $width = $bitmap.Width
    $height = $bitmap.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $width, $height
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bitmap.UnlockBits($data)

    $xor = New-Object System.IO.MemoryStream
    $xorWriter = New-Object System.IO.BinaryWriter $xor
    for ($y = $height - 1; $y -ge 0; $y--) {
        $xorWriter.Write($raw, ($y * $stride), ($width * 4))
    }
    $xorWriter.Flush()

    $maskStride = [int](([Math]::Floor(($width + 31) / 32)) * 4)
    $mask = New-Object byte[] ($maskStride * $height)

    $image = New-Object System.IO.MemoryStream
    $imageWriter = New-Object System.IO.BinaryWriter $image
    $imageWriter.Write([UInt32]40)
    $imageWriter.Write([Int32]$width)
    $imageWriter.Write([Int32]($height * 2))
    $imageWriter.Write([UInt16]1)
    $imageWriter.Write([UInt16]32)
    $imageWriter.Write([UInt32]0)
    $imageWriter.Write([UInt32]($xor.Length + $mask.Length))
    $imageWriter.Write([Int32]0)
    $imageWriter.Write([Int32]0)
    $imageWriter.Write([UInt32]0)
    $imageWriter.Write([UInt32]0)
    $imageWriter.Write($xor.ToArray())
    $imageWriter.Write($mask)
    $imageWriter.Flush()
    Write-Output -NoEnumerate $image.ToArray()
}

$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$iconImages = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($iconSize in $iconSizes) {
    $drawn = New-DimIconBitmap $iconSize
    $iconImages.Add((Get-IconImageBytes $drawn))
    if ($iconSize -eq 64) {
        $drawn.Save((Join-Path $env:TEMP 'dim-icon-64.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $drawn.Dispose()
}

$iconStream = [System.IO.File]::Create($iconPath)
$iconWriter = New-Object System.IO.BinaryWriter $iconStream
$iconWriter.Write([UInt16]0)
$iconWriter.Write([UInt16]1)
$iconWriter.Write([UInt16]$iconImages.Count)
$offset = 6 + (16 * $iconImages.Count)
for ($i = 0; $i -lt $iconImages.Count; $i++) {
    $dimension = $iconSizes[$i]
    if ($dimension -ge 256) { $dimension = 0 }
    $iconWriter.Write([Byte]$dimension)
    $iconWriter.Write([Byte]$dimension)
    $iconWriter.Write([Byte]0)
    $iconWriter.Write([Byte]0)
    $iconWriter.Write([UInt16]1)
    $iconWriter.Write([UInt16]32)
    $iconWriter.Write([UInt32]$iconImages[$i].Length)
    $iconWriter.Write([UInt32]$offset)
    $offset += $iconImages[$i].Length
}
foreach ($imageBytes in $iconImages) {
    $iconWriter.Write([byte[]]$imageBytes)
}
$iconWriter.Flush()
$iconWriter.Dispose()
$iconStream.Dispose()

$sources = Get-ChildItem -Path $src -Filter *.cs | Sort-Object Name
$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:latest',
    ('/win32manifest:' + (Join-Path $root 'app.manifest')),
    ('/win32icon:' + $iconPath),
    ('/resource:' + $iconPath + ',DimWidget.icon.ico'),
    ('/out:' + (Join-Path $bin 'DimWidget.exe'))
)

foreach ($reference in $references) {
    $arguments += ('/r:' + $reference)
}

foreach ($source in $sources) {
    $arguments += $source.FullName
}

Write-Host ("Compiling with " + $csc.FullName)

& $csc.FullName @arguments

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $bin 'DimWidget.exe'
$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "Built $exe ($size KB)"
