param([string]$Out)
Add-Type -AssemblyName System.Drawing
$sizes = 16,20,24,32,48,64,128,256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s,$s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,18,19,23))
  $r = [single]($s*0.22)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r*2; $w = $s-1
  $path.AddArc(0,0,$d,$d,180,90); $path.AddArc($w-$d,0,$d,$d,270,90)
  $path.AddArc($w-$d,$w-$d,$d,$d,0,90); $path.AddArc(0,$w-$d,$d,$d,90,90); $path.CloseFigure()
  $g.FillPath($bg,$path)
  $amber = [System.Drawing.Color]::FromArgb(255,245,165,36)
  $t = [single][Math]::Max(1.5, $s*0.085)
  $pen = New-Object System.Drawing.Pen $amber,$t
  $c = $s/2.0; $rad = $s*0.27
  $g.DrawEllipse($pen, [single]($c-$rad), [single]($c-$rad), [single]($rad*2), [single]($rad*2))
  $len = $s*0.17; $gap = $s*0.10
  $g.DrawLine($pen, [single]$c, [single]($c-$gap-$len), [single]$c, [single]($c-$gap))
  $g.DrawLine($pen, [single]$c, [single]($c+$gap), [single]$c, [single]($c+$gap+$len))
  $g.DrawLine($pen, [single]($c-$gap-$len), [single]$c, [single]($c-$gap), [single]$c)
  $g.DrawLine($pen, [single]($c+$gap), [single]$c, [single]($c+$gap+$len), [single]$c)
  $dot = [single][Math]::Max(1.5,$s*0.07)
  $g.FillEllipse([System.Drawing.Brushes]::White, [single]($c-$dot), [single]($c-$dot), [single]($dot*2), [single]($dot*2))
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,@($s, $ms.ToArray())
  $bmp.Dispose()
}
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16*$pngs.Count
foreach ($p in $pngs) {
  $s = $p[0]; $data = $p[1]
  $b = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Close()
