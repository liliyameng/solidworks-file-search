[CmdletBinding()]
param(
    [string]$Source = 'C:\Users\liliy\AppData\Local\Temp\codex-clipboard-80223c5c-4dee-4856-b2f7-6845635605b8.png'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Save-TaskpanePalettePng {
    param(
        [Parameter(Mandatory = $true)][Drawing.Bitmap]$Source,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $indexed = [Drawing.Bitmap]::new($Source.Width, $Source.Height, [Drawing.Imaging.PixelFormat]::Format8bppIndexed)
    try {
        $palette = $indexed.Palette
        for ($red = 0; $red -lt 8; $red++) {
            for ($green = 0; $green -lt 8; $green++) {
                for ($blue = 0; $blue -lt 4; $blue++) {
                    $index = ($red -shl 5) -bor ($green -shl 2) -bor $blue
                    $palette.Entries[$index] = [Drawing.Color]::FromArgb(
                        255,
                        [int][Math]::Round($red * 255.0 / 7.0),
                        [int][Math]::Round($green * 255.0 / 7.0),
                        [int][Math]::Round($blue * 255.0 / 3.0))
                }
            }
        }
        $indexed.Palette = $palette
        $rectangle = [Drawing.Rectangle]::new(0, 0, $Source.Width, $Source.Height)
        $data = $indexed.LockBits($rectangle, [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format8bppIndexed)
        try {
            $row = [byte[]]::new($data.Stride)
            for ($y = 0; $y -lt $Source.Height; $y++) {
                [Array]::Clear($row, 0, $row.Length)
                for ($x = 0; $x -lt $Source.Width; $x++) {
                    $pixel = $Source.GetPixel($x, $y)
                    $red = [Math]::Min(7, [int][Math]::Round($pixel.R * 7.0 / 255.0))
                    $green = [Math]::Min(7, [int][Math]::Round($pixel.G * 7.0 / 255.0))
                    $blue = [Math]::Min(3, [int][Math]::Round($pixel.B * 3.0 / 255.0))
                    $row[$x] = [byte](($red -shl 5) -bor ($green -shl 2) -bor $blue)
                }
                [Runtime.InteropServices.Marshal]::Copy($row, 0, [IntPtr]::Add($data.Scan0, $y * $data.Stride), $row.Length)
            }
        }
        finally { $indexed.UnlockBits($data) }
        $indexed.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $indexed.Dispose() }
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$brand = Join-Path $root 'src\Assets\Brand'
$taskpane = Join-Path $root 'src\Assets\Taskpane'
New-Item -ItemType Directory -Force -Path $brand,$taskpane | Out-Null
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Brand source not found: $Source" }
$master = Join-Path $brand 'ProductIcon-source.png'
$sourceFull = [IO.Path]::GetFullPath($Source)
if (-not $sourceFull.Equals([IO.Path]::GetFullPath($master), [StringComparison]::OrdinalIgnoreCase)) {
    [IO.File]::Copy($sourceFull, $master, $true)
}

$sourceImage = [Drawing.Bitmap]::new($master)
try {
    $side = [Math]::Max($sourceImage.Width, $sourceImage.Height)
    $corners = @(
        $sourceImage.GetPixel(0,0),
        $sourceImage.GetPixel($sourceImage.Width-1,0),
        $sourceImage.GetPixel(0,$sourceImage.Height-1),
        $sourceImage.GetPixel($sourceImage.Width-1,$sourceImage.Height-1)
    )
    $background = [Drawing.Color]::FromArgb(
        255,
        [int](($corners | Measure-Object R -Average).Average),
        [int](($corners | Measure-Object G -Average).Average),
        [int](($corners | Measure-Object B -Average).Average))
    $square = [Drawing.Bitmap]::new($side,$side,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($square)
        try {
            $graphics.Clear($background)
            $offsetX = [int](($side - $sourceImage.Width) / 2)
            $offsetY = [int](($side - $sourceImage.Height) / 2)
            $graphics.DrawImageUnscaled($sourceImage,$offsetX,$offsetY)
        } finally { $graphics.Dispose() }
        $square.Save((Join-Path $brand 'SwSearch-1115.png'),[Drawing.Imaging.ImageFormat]::Png)
        $sizes = @(16,20,24,32,40,48,64,96,128,256,512)
        foreach($size in $sizes) {
            $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $g = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $g.Clear($background)
                    $g.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $g.DrawImage($square,[Drawing.Rectangle]::new(0,0,$size,$size),0,0,$side,$side,[Drawing.GraphicsUnit]::Pixel)
                } finally { $g.Dispose() }
                $png = Join-Path $brand "SwSearch-$size.png"
                $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
                if($size -in @(20,32,40,64,96,128)) {
                    Save-TaskpanePalettePng -Source $bitmap -Path (Join-Path $taskpane "SwSearch-$size.png")
                }
            } finally { $bitmap.Dispose() }
        }
    } finally { $square.Dispose() }
} finally { $sourceImage.Dispose() }

$icoSizes = @(16,20,24,32,40,48,64,128,256)
$frames = foreach($size in $icoSizes) {
    [pscustomobject]@{ Size=$size; Bytes=[IO.File]::ReadAllBytes((Join-Path $brand "SwSearch-$size.png")) }
}
$icoPath = Join-Path $brand 'SwSearch.ico'
$stream = [IO.File]::Create($icoPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach($frame in $frames) {
        $writer.Write([byte]$(if($frame.Size -eq 256){0}else{$frame.Size}))
        $writer.Write([byte]$(if($frame.Size -eq 256){0}else{$frame.Size}))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally {
    $writer.Dispose()
    $stream.Dispose()
}

[pscustomobject]@{
    SourceSha256=(Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    MasterSha256=(Get-FileHash -LiteralPath $master -Algorithm SHA256).Hash
    IconSha256=(Get-FileHash -LiteralPath $icoPath -Algorithm SHA256).Hash
    Background=$background.Name
    IcoFrames=($icoSizes -join ',')
}
