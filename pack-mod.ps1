param(
    [Parameter(Mandatory = $true)]
    [string]$ModDirectory,

    [string]$OutputDirectory = "",

    [switch]$Force
)

$ErrorActionPreference = "Stop"
$PackageIdPattern = '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$'
$SemVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'

if ([string]::IsNullOrWhiteSpace($ModDirectory)) {
    throw "ModDirectory is required."
}
$modRootCandidate = [IO.Path]::GetFullPath($ModDirectory)
if (-not (Test-Path -LiteralPath $modRootCandidate -PathType Container)) {
    throw "ModDirectory is not a directory: $modRootCandidate"
}
$modRoot = (Resolve-Path -LiteralPath $modRootCandidate).Path
$manifestPath = Join-Path $modRoot "manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "manifest.json is missing from the Mod root."
}
$manifests = @(Get-ChildItem -LiteralPath $modRoot -Recurse -Filter "manifest.json" -File)
if ($manifests.Count -ne 1 -or
    -not [IO.Path]::GetFullPath($manifests[0].FullName).Equals(
        [IO.Path]::GetFullPath($manifestPath),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "A Mod package must contain exactly one manifest.json at its root."
}
if (Test-Path -LiteralPath (Join-Path $modRoot ".sunny-installed.json")) {
    throw "Remove .sunny-installed.json before packaging a development Mod."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2) {
    throw "Manifest schemaVersion must be 2."
}
if ($null -eq $manifest.compatibility -or $manifest.compatibility.loaderApi -ne 2) {
    throw "Manifest compatibility.loaderApi must be 2."
}
if ([string]::IsNullOrWhiteSpace($manifest.name)) {
    throw "Manifest name is required."
}
$manifestId = [string]$manifest.id
if ($manifestId.Length -gt 128 -or $manifestId -cnotmatch $PackageIdPattern) {
    throw "Manifest id must be a lowercase ASCII reverse-domain identifier."
}
$manifestVersion = [string]$manifest.version
if ($manifestVersion.Length -lt 5 -or $manifestVersion.Length -gt 256 -or
    $manifestVersion -cnotmatch $SemVerPattern) {
    throw "Manifest version must be a valid SemVer 2.0 version."
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Split-Path $modRoot -Parent
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$modPrefix = $modRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($outputRoot.Equals($modRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($modPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must not be inside the Mod directory."
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $outputRoot ($manifestId + "-" + $manifestVersion + ".sunmod")))
$outputPrefix = $outputRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The package output path escapes OutputDirectory."
}
if ((Test-Path -LiteralPath $outputPath) -and -not $Force) {
    throw "Output already exists. Pass -Force to replace it: $outputPath"
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$temporaryZip = Join-Path $env:TEMP ($manifestId + "-" + [Guid]::NewGuid().ToString("N") + ".zip")
try {
    Compress-Archive -Path (Join-Path $modRoot "*") -DestinationPath $temporaryZip -CompressionLevel Optimal
    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Force
    }
    Move-Item -LiteralPath $temporaryZip -Destination $outputPath
}
finally {
    if (Test-Path -LiteralPath $temporaryZip) {
        Remove-Item -LiteralPath $temporaryZip -Force
    }
}

Write-Host "Created $outputPath"
Write-Host "Data Mods do not require build.ps1."
