$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path $PSScriptRoot '..\src\AndroidOnDesktop.Desktop\Assets'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect x="12" y="12" width="232" height="232" rx="52" fill="#146B56"/>
  <rect x="40" y="48" width="176" height="150" rx="18" fill="#F3FFF9"/>
  <path d="M40 84H216" stroke="#146B56" stroke-width="8"/>
  <circle cx="57" cy="66" r="5" fill="#146B56"/>
  <circle cx="74" cy="66" r="5" fill="#36BE83"/>
  <path d="M86 115L77 101M170 115L179 101" stroke="#146B56" stroke-width="8" stroke-linecap="round"/>
  <path d="M72 164V153A56 48 0 0 1 184 153V164Z" fill="#36BE83"/>
  <circle cx="103" cy="143" r="7" fill="#146B56"/>
  <circle cx="153" cy="143" r="7" fill="#146B56"/>
  <path d="M104 218H152" stroke="#9CE7C0" stroke-width="8" stroke-linecap="round"/>
</svg>
'@
[IO.File]::WriteAllText((Join-Path $assetDir 'AndroidOnDesktop.svg'), $svg)
function RoundRect($g,$brush,$x,$y,$w,$h,$r) {
    $p=[Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddArc($x,$y,$r*2,$r*2,180,90); $p.AddArc($x+$w-$r*2,$y,$r*2,$r*2,270,90)
    $p.AddArc($x+$w-$r*2,$y+$h-$r*2,$r*2,$r*2,0,90); $p.AddArc($x,$y+$h-$r*2,$r*2,$r*2,90,90)
    $p.CloseFigure(); $g.FillPath($brush,$p); $p.Dispose()
}
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $g=[Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode='AntiAlias'; $g.ScaleTransform($size/256.0,$size/256.0)
    $dark=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#146B56'))
    $light=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#F3FFF9'))
    $green=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#36BE83'))
    $pen=[Drawing.Pen]::new($dark,8); $pen.StartCap='Round'; $pen.EndCap='Round'
    RoundRect $g $dark 12 12 232 232 52
    RoundRect $g $light 40 48 176 150 18
    $g.DrawLine($pen,40,84,216,84)
    $g.FillEllipse($dark,52,61,10,10); $g.FillEllipse($green,69,61,10,10)
    $g.DrawLine($pen,86,115,77,101); $g.DrawLine($pen,170,115,179,101)
    $head=[Drawing.Drawing2D.GraphicsPath]::new()
    $head.AddArc(72,105,112,96,180,180); $head.AddLine(184,153,184,164); $head.AddLine(184,164,72,164); $head.CloseFigure()
    $g.FillPath($green,$head)
    $g.FillEllipse($dark,96,136,14,14); $g.FillEllipse($dark,146,136,14,14)
    $pen.Color=[Drawing.ColorTranslator]::FromHtml('#9CE7C0'); $g.DrawLine($pen,104,218,152,218)
    $stream=[IO.MemoryStream]::new(); $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDir 'AndroidOnDesktop.png'),[Drawing.Imaging.ImageFormat]::Png) }
    $head.Dispose(); $pen.Dispose(); $dark.Dispose(); $light.Dispose(); $green.Dispose(); $g.Dispose(); $bitmap.Dispose(); $stream.Dispose()
}
$output=[IO.File]::Create((Join-Path $assetDir 'AndroidOnDesktop.ico'))
$writer=[IO.BinaryWriter]::new($output)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset=6+16*$frames.Count
$sizes=@(16,24,32,48,64,128,256)
for ($i=0;$i -lt $frames.Count;$i++) {
    $edge=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
    $writer.Write([byte]$edge); $writer.Write([byte]$edge); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()
