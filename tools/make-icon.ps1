# Generates src/Sugrob/app.ico: a white snowflake on a blue rounded square, in all standard sizes.
# Run: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\src\Sugrob\app.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square with a vertical night-blue gradient
    $r = [single]($s * 0.22); $pad = [single]($s * 0.04); $w = [single]($s - 2 * $pad)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $w, $w
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 58, 120, 230)), ([System.Drawing.Color]::FromArgb(255, 22, 40, 110)), 90
    $g.FillPath($bg, $path)

    # snowflake: 6 arms with side branches
    $c = $s / 2.0; $arm = $s * 0.34
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.2, $s * 0.07))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    for ($k = 0; $k -lt 6; $k++) {
        $a = $k * [Math]::PI / 3 - [Math]::PI / 2
        $x2 = $c + [Math]::Cos($a) * $arm; $y2 = $c + [Math]::Sin($a) * $arm
        $g.DrawLine($pen, [single]$c, [single]$c, [single]$x2, [single]$y2)
        if ($s -ge 24) {
            foreach ($t in 0.55) {
                $bx = $c + [Math]::Cos($a) * $arm * $t; $by = $c + [Math]::Sin($a) * $arm * $t
                foreach ($side in -1, 1) {
                    $ba = $a + $side * [Math]::PI / 3
                    $g.DrawLine($pen, [single]$bx, [single]$by, [single]($bx + [Math]::Cos($ba) * $arm * 0.35), [single]($by + [Math]::Sin($ba) * $arm * 0.35))
                }
            }
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

# ICO container with PNG-compressed images
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Written $out"
