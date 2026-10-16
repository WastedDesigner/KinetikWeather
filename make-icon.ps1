# Draws icon.ico: a dark rounded tile with a yellow sun behind a light cloud.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Png([int]$s) {
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(0.5, $s * 0.06); $w = $s - 2 * $m; $r = $s * 0.2
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 34, 34, 40))), (RoundRect $m $m $w $w $r))
    $sun = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 196, 64))
    $R = $s * 0.19; $cx = $s * 0.40; $cy = $s * 0.40
    $g.FillEllipse($sun, [float]($cx - $R), [float]($cy - $R), [float](2 * $R), [float](2 * $R))
    if ($s -ge 32) { $rp = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 196, 64)), ([float]($s * 0.045)); $rp.StartCap = 'Round'; $rp.EndCap = 'Round'
        for ($i = 0; $i -lt 8; $i++) { $a = $i * [Math]::PI / 4; $c = [Math]::Cos($a); $n = [Math]::Sin($a)
            $g.DrawLine($rp, [float]($cx + $c * $R * 1.4), [float]($cy + $n * $R * 1.4), [float]($cx + $c * $R * 1.75), [float]($cy + $n * $R * 1.75)) } }
    $cl = New-Object System.Drawing.Drawing2D.GraphicsPath; $cl.FillMode = 'Winding'
    $x = $s * 0.24; $y = $s * 0.44; $cw = $s * 0.58; $ch = $s * 0.34
    $cl.AddPath((RoundRect $x ($y + $ch * 0.42) $cw ($ch * 0.58) ($ch * 0.29)), $false)
    $cl.AddEllipse([float]($x + $cw * 0.12), [float]($y + $ch * 0.28), [float]($ch * 0.62), [float]($ch * 0.62))
    $cl.AddEllipse([float]($x + $cw * 0.32), [float]$y, [float]($ch * 0.86), [float]($ch * 0.86))
    $cl.AddEllipse([float]($x + $cw * 0.6), [float]($y + $ch * 0.3), [float]($ch * 0.58), [float]($ch * 0.58))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 226, 232, 242))), $cl)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$imgs = $sizes | ForEach-Object { , (Png $_) }
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $imgs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $imgs) { $bw.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon.ico'), $out.ToArray())
Write-Output "Wrote icon.ico"

