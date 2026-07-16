param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "examples\org.example.sunny-demo\assets")
)

$ErrorActionPreference = "Stop"
$voiceDir = Join-Path $OutputRoot "voice"
$cgDir = Join-Path $OutputRoot "cg"
$musicDir = Join-Path $OutputRoot "music"
$spriteDir = Join-Path $OutputRoot "sprites"
$voicePath = Join-Path $voiceDir "voice.wav"
$imagePath = Join-Path $cgDir "pic.png"
$musicPath = Join-Path $musicDir "route.wav"
$spriteAPath = Join-Path $spriteDir "guest-a.png"
$spriteBPath = Join-Path $spriteDir "guest-b.png"
New-Item -ItemType Directory -Force -Path $voiceDir, $cgDir, $musicDir, $spriteDir | Out-Null

Add-Type -AssemblyName System.Drawing

function New-DemoBackground {
    param([string]$Path)

    $bitmap = [System.Drawing.Bitmap]::new(1280, 720, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $rect = [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
        $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 31, 62, 85),
            [System.Drawing.Color]::FromArgb(255, 233, 169, 91),
            18.0)
        $horizon = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(185, 238, 244, 239))
        $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(220, 52, 132, 135))
        $sun = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 255, 235, 176))
        $line = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(170, 255, 255, 255), 3)
        try {
            $graphics.FillRectangle($background, $rect)
            $graphics.FillEllipse($sun, 930, 90, 170, 170)
            $graphics.FillPolygon($horizon, [System.Drawing.Point[]]@(
                [System.Drawing.Point]::new(0, 535),
                [System.Drawing.Point]::new(260, 365),
                [System.Drawing.Point]::new(520, 525),
                [System.Drawing.Point]::new(790, 315),
                [System.Drawing.Point]::new(1050, 500),
                [System.Drawing.Point]::new(1280, 390),
                [System.Drawing.Point]::new(1280, 720),
                [System.Drawing.Point]::new(0, 720)
            ))
            $graphics.FillRectangle($accent, 0, 630, 1280, 90)
            for ($x = 80; $x -lt 1280; $x += 160) {
                $graphics.DrawLine($line, $x, 650, $x + 80, 700)
            }
        }
        finally {
            $background.Dispose()
            $horizon.Dispose()
            $accent.Dispose()
            $sun.Dispose()
            $line.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-DemoWave {
    param(
        [string]$Path,
        [double]$DurationSeconds,
        [double[]]$Frequencies,
        [double]$Gain = 0.22
    )

    $sampleRate = 44100
    $sampleCount = [int]($sampleRate * $DurationSeconds)
    $dataLength = $sampleCount * 2
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = [System.IO.BinaryWriter]::new($stream, [System.Text.Encoding]::ASCII, $false)
    try {
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
        $writer.Write([int](36 + $dataLength))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
        $writer.Write([int]16)
        $writer.Write([int16]1)
        $writer.Write([int16]1)
        $writer.Write([int]$sampleRate)
        $writer.Write([int]($sampleRate * 2))
        $writer.Write([int16]2)
        $writer.Write([int16]16)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
        $writer.Write([int]$dataLength)

        for ($index = 0; $index -lt $sampleCount; $index++) {
            $time = $index / [double]$sampleRate
            $progress = $index / [double][Math]::Max(1, $sampleCount - 1)
            $envelope = [Math]::Min(1.0, $progress * 12.0) * [Math]::Min(1.0, (1.0 - $progress) * 12.0)
            $value = 0.0
            foreach ($frequency in $Frequencies) {
                $value += [Math]::Sin(2.0 * [Math]::PI * $frequency * $time)
            }
            $value = ($value / $Frequencies.Count) * $Gain * $envelope
            $sample = [int][Math]::Round([Math]::Max(-1.0, [Math]::Min(1.0, $value)) * 32767.0)
            $writer.Write([int16]$sample)
        }
    }
    finally {
        $writer.Dispose()
    }
}

function New-DemoSprite {
    param(
        [string]$Path,
        [System.Drawing.Color]$CoatColor,
        [System.Drawing.Color]$HairColor
    )

    $bitmap = [System.Drawing.Bitmap]::new(600, 900, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $coat = [System.Drawing.SolidBrush]::new($CoatColor)
        $skin = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 246, 216, 198))
        $hair = [System.Drawing.SolidBrush]::new($HairColor)
        $shirt = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 245, 242))
        try {
            $body = [System.Drawing.Point[]]@(
                [System.Drawing.Point]::new(105, 900),
                [System.Drawing.Point]::new(155, 470),
                [System.Drawing.Point]::new(300, 410),
                [System.Drawing.Point]::new(445, 470),
                [System.Drawing.Point]::new(495, 900)
            )
            $graphics.FillPolygon($coat, $body)
            $graphics.FillPolygon($shirt, [System.Drawing.Point[]]@(
                [System.Drawing.Point]::new(235, 445),
                [System.Drawing.Point]::new(300, 560),
                [System.Drawing.Point]::new(365, 445)
            ))
            $graphics.FillEllipse($skin, 190, 120, 220, 300)
            $graphics.FillPie($hair, 165, 80, 270, 245, 180, 180)
            $graphics.FillPolygon($hair, [System.Drawing.Point[]]@(
                [System.Drawing.Point]::new(180, 185),
                [System.Drawing.Point]::new(235, 80),
                [System.Drawing.Point]::new(300, 170),
                [System.Drawing.Point]::new(350, 75),
                [System.Drawing.Point]::new(420, 205),
                [System.Drawing.Point]::new(405, 310),
                [System.Drawing.Point]::new(190, 310)
            ))
        }
        finally {
            $coat.Dispose()
            $skin.Dispose()
            $hair.Dispose()
            $shirt.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-DemoBackground -Path $imagePath
New-DemoWave -Path $voicePath -DurationSeconds 1.2 -Frequencies @(660.0, 880.0) -Gain 0.18
New-DemoWave -Path $musicPath -DurationSeconds 4.0 -Frequencies @(220.0, 277.18, 329.63) -Gain 0.14
New-DemoSprite -Path $spriteAPath `
    -CoatColor ([System.Drawing.Color]::FromArgb(255, 40, 106, 138)) `
    -HairColor ([System.Drawing.Color]::FromArgb(255, 45, 38, 46))
New-DemoSprite -Path $spriteBPath `
    -CoatColor ([System.Drawing.Color]::FromArgb(255, 164, 72, 84)) `
    -HairColor ([System.Drawing.Color]::FromArgb(255, 93, 58, 38))

foreach ($legacyPath in @(
    (Join-Path $musicDir "route.ogg"),
    (Join-Path $voiceDir "voice.ogg")
)) {
    if ([System.IO.File]::Exists($legacyPath)) {
        [System.IO.File]::Delete($legacyPath)
    }
}

Write-Host "Generated self-contained demo assets in $OutputRoot"
