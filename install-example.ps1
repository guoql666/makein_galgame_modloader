param(
    [string]$GameRoot = "",

    [switch]$Enable
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = $env:SUNNY_GAME_ROOT
}
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = Split-Path $PSScriptRoot -Parent
}
$gameRoot = [IO.Path]::GetFullPath($GameRoot)
$managedCandidates = @(Get-ChildItem -LiteralPath $gameRoot -Directory -Filter "*_Data" | Where-Object {
    Test-Path -LiteralPath (Join-Path $_.FullName "Managed\Assembly-CSharp.dll") -PathType Leaf
})
if ($managedCandidates.Count -ne 1) {
    throw "GameRoot must contain exactly one *_Data directory with Managed\Assembly-CSharp.dll: $gameRoot"
}
$source = Join-Path $PSScriptRoot "examples\org.example.sunny-demo"
$target = Join-Path $gameRoot "Mods\org.example.sunny-demo"

& (Join-Path $PSScriptRoot "generate-demo-assets.ps1")
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $target -Recurse -Force

if ($Enable) {
    $configPath = Join-Path $gameRoot "BepInEx\config\qm.sunny.modloader.cfg"
    New-Item -ItemType Directory -Force -Path (Split-Path $configPath -Parent) | Out-Null
    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $configPath) {
        foreach ($line in [System.IO.File]::ReadAllLines($configPath)) {
            $lines.Add($line)
        }
    }

    $section = "[Mod:org.example.sunny-demo]"
    $sectionIndex = $lines.IndexOf($section)
    if ($sectionIndex -lt 0) {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -ne "") {
            $lines.Add("")
        }
        $lines.Add($section)
        $lines.Add("Enabled = true")
    }
    else {
        $enabledIndex = -1
        for ($i = $sectionIndex + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i].StartsWith("[")) {
                break
            }
            if ($lines[$i] -match '^Enabled\s*=') {
                $enabledIndex = $i
                break
            }
        }

        if ($enabledIndex -ge 0) {
            $lines[$enabledIndex] = "Enabled = true"
        }
        else {
            $lines.Insert($sectionIndex + 1, "Enabled = true")
        }
    }

    [System.IO.File]::WriteAllLines(
        $configPath,
        $lines,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Example installed and enabled in $configPath"
}
else {
    Write-Host "Example installed disabled by default."
}
