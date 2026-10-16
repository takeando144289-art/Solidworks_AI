[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing,PresentationCore,WindowsBase
$folder = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\SolidWorksAgent.SwApi\Addin\Assets'
$source = [Drawing.Image]::FromFile((Join-Path $folder 'taskpane-source.png'))
try {
    foreach ($size in @(20,32,40,64,96,128)) {
        $bitmap = New-Object Drawing.Bitmap($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::White)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
            $width = [int][Math]::Round($source.Width * $scale)
            $height = [int][Math]::Round($source.Height * $scale)
            $graphics.DrawImage($source, [int](($size-$width)/2), [int](($size-$height)/2), $width, $height)
            # Match SOLIDWORKS's indexed PNG format with an artwork-specific palette.
            $stream = New-Object IO.MemoryStream
            try {
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                $stream.Position = 0
                $decoder = [Windows.Media.Imaging.PngBitmapDecoder]::new($stream,
                    [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
                    [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
                $frame = $decoder.Frames[0]
                $palette = [Windows.Media.Imaging.BitmapPalette]::new($frame, 256)
                $indexed = [Windows.Media.Imaging.FormatConvertedBitmap]::new($frame,
                    [Windows.Media.PixelFormats]::Indexed8, $palette, 0)
                $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
                $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($indexed))
                $output = [IO.File]::Create((Join-Path $folder "taskpane-$size.png"))
                try { $encoder.Save($output) } finally { $output.Dispose() }
            } finally { $stream.Dispose() }
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
Write-Output 'Generated six task pane icon sizes.'
