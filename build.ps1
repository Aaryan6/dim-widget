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
$iconSize = 256
$moonAccent = [System.Drawing.Color]::FromArgb(255, 255, 209, 102)

$iconBitmap = New-Object System.Drawing.Bitmap $iconSize, $iconSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
$iconGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$iconGraphics.Clear([System.Drawing.Color]::Transparent)

$moonBrush = New-Object System.Drawing.SolidBrush $moonAccent
$iconGraphics.FillEllipse($moonBrush, 16, 16, 224, 224)

$iconGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
$cutBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
$iconGraphics.FillEllipse($cutBrush, 82, -12, 226, 226)
$iconGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver

$moonBrush.Dispose()
$cutBrush.Dispose()
$iconGraphics.Dispose()

$pngStream = New-Object System.IO.MemoryStream
$iconBitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$pngStream.Dispose()
$iconBitmap.Dispose()

$fileStream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter $fileStream
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Flush()
$writer.Dispose()
$fileStream.Dispose()

$sources = Get-ChildItem -Path $src -Filter *.cs | Sort-Object Name
$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:latest',
    ('/win32manifest:' + (Join-Path $root 'app.manifest')),
    ('/win32icon:' + $iconPath),
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
