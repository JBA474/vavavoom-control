# Writes app.ico: the AuraBox Control icon (a 4x4 grid of LED colors on a dark tile), drawn separately at every
# size Windows asks for (16-256 px) so it stays crisp in the title bar, taskbar, tray, Task Manager and Explorer.
#   powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1 [out.ico]
# Sizes up to 64 are stored as 32-bit bitmaps (read everywhere); 128 and 256 as PNG (the Windows Vista+ format).
param([string]$Out = (Join-Path $PSScriptRoot '..\app.ico'))
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
# same colors as the app's 3-bit palette (red, green, yellow, blue, magenta, cyan, white)
$palette = @(
    [Drawing.Color]::FromArgb(255, 0, 0), [Drawing.Color]::FromArgb(0, 255, 0), [Drawing.Color]::FromArgb(255, 255, 0),
    [Drawing.Color]::FromArgb(0, 0, 255), [Drawing.Color]::FromArgb(255, 0, 255), [Drawing.Color]::FromArgb(0, 255, 255),
    [Drawing.Color]::White)

function Draw([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::Transparent)
    $g.SmoothingMode = 'AntiAlias'
    # dark rounded tile, so the colors read on light and dark taskbars alike
    $r = [Math]::Max(2, [int]($s / 6)); $d = 2 * $r
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(30, 30, 30))), $path)
    # 4x4 LED cells on whole pixels: no blur at small sizes
    $g.SmoothingMode = 'None'
    $margin = if ($s -le 24) { 1 } else { [int][Math]::Round($s / 10) }
    $gap = [Math]::Max(1, [int][Math]::Round($s / 32))
    $cell = [Math]::Floor(($s - 2 * $margin - 3 * $gap) / 4)
    $start = [Math]::Floor(($s - (4 * $cell + 3 * $gap)) / 2)
    for ($k = 0; $k -lt 16; $k++) {
        $c = $palette[($k * 3 + [Math]::Floor($k / 4)) % 7]
        $x = $start + ($k % 4) * ($cell + $gap); $y = $start + [Math]::Floor($k / 4) * ($cell + $gap)
        $g.FillRectangle((New-Object Drawing.SolidBrush $c), $x, $y, $cell, $cell)
    }
    $g.Dispose()
    return $bmp
}

# one ICO image entry: PNG bytes, or a 32-bit bottom-up DIB plus its 1-bit AND mask
function EntryBytes($bmp) {
    $s = $bmp.Width
    if ($s -ge 128) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); return $ms.ToArray() }
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) { $p = $bmp.GetPixel($x, $y); $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A) }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32) * 4)
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskRow
        for ($x = 0; $x -lt $s; $x++) { if ($bmp.GetPixel($x, $y).A -lt 128) { $row[[Math]::Floor($x / 8)] = $row[[Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) } }
        $w.Write($row)
    }
    $w.Flush(); return $ms.ToArray()
}

$images = foreach ($s in $sizes) { $b = Draw $s; , @($s, (EntryBytes $b)); $b.Dispose() }
$ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($im in $images) {
    $s = $im[0]; $data = $im[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$data.Length); $w.Write([int]$offset)
    $offset += $data.Length
}
foreach ($im in $images) { $w.Write([byte[]]$im[1]) }
$w.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ms.ToArray())
"wrote $([IO.Path]::GetFullPath($Out)) ($($images.Count) sizes: $($sizes -join ', ') px)"
