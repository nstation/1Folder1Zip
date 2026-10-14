$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot '..\src\OneFolderOneZip.App\Assets\App.ico'
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($iconPath)) | Out-Null
$images = @()
foreach ($size in @(16, 32, 48, 64, 128, 256)) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $folderBack = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#EFB84D'))
    $folderFront = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#FFCF69'))
    $zipBrush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#2468D8'))
    $zipPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2.8)
    $points = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF(4,18)),(New-Object System.Drawing.PointF(23,18)),(New-Object System.Drawing.PointF(29,24)),(New-Object System.Drawing.PointF(60,24)),(New-Object System.Drawing.PointF(60,56)),(New-Object System.Drawing.PointF(4,56)))
    $graphics.FillPolygon($folderBack, $points)
    $graphics.FillRectangle($folderFront, 4, 30, 56, 26)
    $graphics.FillRectangle($zipBrush, 36, 7, 21, 40)
    $graphics.DrawLine($zipPen, 46, 10, 46, 32)
    foreach ($y in @(15, 21, 27)) { $graphics.DrawLine($zipPen, 42, $y, 50, $y) }
    $graphics.DrawRectangle($zipPen, 43, 34, 6, 7)
    $memory = New-Object System.IO.MemoryStream
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@{ Size=$size; Bytes=$memory.ToArray() }
    $memory.Dispose(); $zipPen.Dispose(); $zipBrush.Dispose(); $folderFront.Dispose(); $folderBack.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$stream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($entry in $images) {
        $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
        $writer.Write([Byte]$dimension); $writer.Write([Byte]$dimension)
        $writer.Write([Byte]0); $writer.Write([Byte]0); $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$entry.Bytes.Length); $writer.Write([UInt32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $images) { $writer.Write([Byte[]]$entry.Bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
