param(
    [string]$Configuration = "Release",

    [string]$GameRoot = "",

    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\SunnyModLoader\SunnyModLoader.csproj"
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = $env:SUNNY_GAME_ROOT
}
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = Split-Path $PSScriptRoot -Parent
}
$gameRootPath = [IO.Path]::GetFullPath($GameRoot)
$managedCandidates = @(Get-ChildItem -LiteralPath $gameRootPath -Directory -Filter "*_Data" | Where-Object {
    Test-Path -LiteralPath (Join-Path $_.FullName "Managed\Assembly-CSharp.dll") -PathType Leaf
})
if ($managedCandidates.Count -ne 1) {
    throw "GameRoot must contain exactly one *_Data directory with Managed\Assembly-CSharp.dll: $gameRootPath"
}
$managedPath = Join-Path $managedCandidates[0].FullName "Managed"
$requiredPaths = @(
    (Join-Path $gameRootPath "BepInEx\core\BepInEx.dll"),
    (Join-Path $gameRootPath "BepInEx\core\0Harmony.dll"),
    (Join-Path $managedPath "Assembly-CSharp.dll")
)
foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required game dependency was not found: $requiredPath"
    }
}

$candidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
    $candidates.Add((Join-Path $env:DOTNET_ROOT "dotnet.exe"))
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $dotnetCommand) {
    $candidates.Add($dotnetCommand.Source)
}

$candidates.Add((Join-Path $env:TEMP "dotnet-sdk\dotnet.exe"))
$dotnet = $null
foreach ($candidate in $candidates | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath $candidate)) {
        continue
    }

    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $null -ne $sdks -and @($sdks).Count -gt 0) {
        $dotnet = $candidate
        break
    }
}

if ($null -eq $dotnet) {
    throw "dotnet SDK not found. Install .NET 8 SDK or set dotnet on PATH."
}

& $dotnet build $project -c $Configuration `
    "-p:SunnyGameRoot=$gameRootPath" `
    "-p:SunnyManagedDir=$managedPath"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($NoInstall) {
    Write-Host "Built SunnyModLoader without installing it into the game directory."
    return
}

$source = Join-Path $PSScriptRoot "src\SunnyModLoader\bin\$Configuration\netstandard2.1\SunnyModLoader.dll"
$target = Join-Path $gameRootPath "BepInEx\plugins\SunnyModLoader.dll"
New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Force
Write-Host "Installed $target"
