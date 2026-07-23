param(
    [Parameter(Mandatory = $true)]
    [string]$Id,

    [Parameter(Mandatory = $true)]
    [string]$Name,

    [string]$DestinationRoot = ""
)

$ErrorActionPreference = "Stop"
$PackageIdPattern = '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$'
$SemVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
if ($Id.Length -gt 128 -or $Id -cnotmatch $PackageIdPattern) {
    throw "Id must be a lowercase ASCII reverse-domain identifier such as com.yourname.first-mod."
}
if ([string]::IsNullOrWhiteSpace($Name)) {
    throw "Name must not be empty."
}
if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "Mods"
}
$destinationRootPath = [IO.Path]::GetFullPath($DestinationRoot)
$target = Join-Path $destinationRootPath $Id
if (Test-Path -LiteralPath $target) {
    throw "Target Mod directory already exists: $target"
}

$template = Join-Path $PSScriptRoot "templates\minimal-story-mod"
$templateManifestPath = Join-Path $template "manifest.json"
if (-not (Test-Path -LiteralPath $template -PathType Container) -or
    -not (Test-Path -LiteralPath $templateManifestPath -PathType Leaf)) {
    throw "The minimal Mod template or its root manifest.json is missing."
}
$templateManifests = @(Get-ChildItem -LiteralPath $template -Recurse -Filter "manifest.json" -File)
if ($templateManifests.Count -ne 1 -or
    -not [IO.Path]::GetFullPath($templateManifests[0].FullName).Equals(
        [IO.Path]::GetFullPath($templateManifestPath),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The minimal Mod template must contain exactly one manifest.json at its root."
}
$templateManifest = Get-Content -LiteralPath $templateManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($templateManifest.schemaVersion -ne 2) {
    throw "The minimal Mod template must use schemaVersion 2."
}
if ($null -eq $templateManifest.compatibility -or $templateManifest.compatibility.loaderApi -ne 2) {
    throw "The minimal Mod template must target loaderApi 2."
}
$templateVersion = [string]$templateManifest.version
if ($templateVersion.Length -lt 5 -or $templateVersion.Length -gt 256 -or
    $templateVersion -cnotmatch $SemVerPattern) {
    throw "The minimal Mod template must use a valid SemVer 2.0 version."
}

New-Item -ItemType Directory -Force -Path $destinationRootPath | Out-Null
Copy-Item -LiteralPath $template -Destination $target -Recurse
New-Item -ItemType Directory -Force -Path `
    (Join-Path $target "assets\voice"), `
    (Join-Path $target "assets\music"), `
    (Join-Path $target "assets\images"), `
    (Join-Path $target "assets\sprites"), `
    (Join-Path $target "assets\spine") | Out-Null

$manifestPath = Join-Path $target "manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest.id = $Id
$manifest.name = $Name
$json = $manifest | ConvertTo-Json -Depth 16
[IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

Write-Host "Created data Mod at $target"
Write-Host "Edit story\main.sunny, then enable the Mod from F8. No build step is required."
