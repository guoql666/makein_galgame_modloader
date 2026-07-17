param(
    [string]$Configuration = "Release",

    [string]$GameRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = $env:SUNNY_GAME_ROOT
}
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = Split-Path $PSScriptRoot -Parent
}
$gameRoot = [IO.Path]::GetFullPath($GameRoot)
$projectPath = Join-Path $PSScriptRoot "src\SunnyModLoader\SunnyModLoader.csproj"
[xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "The Loader version could not be read from $projectPath"
}
$distRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "dist"))
$packageName = "SunnyModLoader-R$version"
$dist = [IO.Path]::GetFullPath((Join-Path $distRoot $packageName))
$patchArchive = [IO.Path]::GetFullPath((Join-Path $distRoot ($packageName + ".zip")))
$scriptExportRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot "output_text"))
$scriptSource = Join-Path $scriptExportRoot "normal-flow"
$dialogueIndexSource = Join-Path $scriptExportRoot "dialogue-index.tsv"
$resourceIndexSource = Join-Path $scriptExportRoot "normal-flow-index.tsv"
$scriptReference = Join-Path $PSScriptRoot "SCRIPT_REFERENCE.md"
$distPrefix = $distRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $dist.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $patchArchive.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output escaped the repository dist directory."
}

$requiredScriptFiles = @("Entry.txt", "vol1.txt", "vol2.txt", "vol3.txt")
if (-not (Test-Path -LiteralPath $scriptSource -PathType Container)) {
    throw "The exported normal flow directory was not found: $scriptSource"
}
foreach ($scriptFile in $requiredScriptFiles) {
    $scriptPath = Join-Path $scriptSource $scriptFile
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "The exported normal flow file was not found: $scriptPath"
    }
}
foreach ($requiredReference in @($dialogueIndexSource, $resourceIndexSource, $scriptReference)) {
    if (-not (Test-Path -LiteralPath $requiredReference -PathType Leaf)) {
        throw "The Script reference input was not found: $requiredReference"
    }
}

& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration -GameRoot $gameRoot
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& (Join-Path $PSScriptRoot "generate-demo-assets.ps1")

if (Test-Path $dist) {
    Remove-Item -LiteralPath $dist -Recurse -Force
}
if (Test-Path $patchArchive) {
    Remove-Item -LiteralPath $patchArchive -Force
}

New-Item -ItemType Directory -Force -Path `
    (Join-Path $dist "BepInEx\plugins"), `
    (Join-Path $dist "Mods\Inbox"), `
    (Join-Path $dist "ModSDK\examples"), `
    (Join-Path $dist "Script") | Out-Null
Copy-Item -LiteralPath (Join-Path $gameRoot "BepInEx\plugins\SunnyModLoader.dll") -Destination (Join-Path $dist "BepInEx\plugins")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "MODS_README.md") -Destination (Join-Path $dist "Mods\README.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "INBOX.md") -Destination (Join-Path $dist "Mods\Inbox\README.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "LICENSE") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "THIRD_PARTY_NOTICES.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "CHANGELOG.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "SECURITY.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "FLOW.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "ANALYSIS.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "VERIFICATION.md") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "manifest.schema.json") -Destination $dist
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "MOD_AUTHORING.md") -Destination $dist
foreach ($scriptFile in $requiredScriptFiles) {
    Copy-Item -LiteralPath (Join-Path $scriptSource $scriptFile) -Destination (Join-Path $dist "Script")
}
Copy-Item -LiteralPath $dialogueIndexSource -Destination (Join-Path $dist "Script\dialogue-index.tsv")
Copy-Item -LiteralPath $resourceIndexSource -Destination (Join-Path $dist "Script\resource-index.tsv")
Copy-Item -LiteralPath $scriptReference -Destination (Join-Path $dist "Script\README.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "ANALYSIS.md") -Destination (Join-Path $dist "ModSDK")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "FLOW.md") -Destination (Join-Path $dist "ModSDK")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "manifest.schema.json") -Destination (Join-Path $dist "ModSDK")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "MOD_AUTHORING.md") -Destination (Join-Path $dist "ModSDK")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "new-mod.ps1") -Destination (Join-Path $dist "ModSDK")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "pack-mod.ps1") -Destination (Join-Path $dist "ModSDK")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "templates") -Destination (Join-Path $dist "ModSDK") -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-example.ps1") -Destination (Join-Path $dist "ModSDK")
$examplesRoot = Join-Path $PSScriptRoot "examples"
foreach ($example in Get-ChildItem -LiteralPath $examplesRoot -Directory) {
    Copy-Item -LiteralPath $example.FullName -Destination (Join-Path $dist "ModSDK\examples") -Recurse
    $exampleArchive = Join-Path $dist ("ModSDK\examples\" + $example.Name + ".zip")
    Compress-Archive -Path (Join-Path $example.FullName "*") `
        -DestinationPath $exampleArchive -CompressionLevel Optimal
    Move-Item -LiteralPath $exampleArchive `
        -Destination (Join-Path $dist ("ModSDK\examples\" + $example.Name + ".sunmod"))
}

Compress-Archive -Path (Join-Path $dist "*") -DestinationPath $patchArchive -CompressionLevel Optimal
Write-Host "Patch package created at $dist"
Write-Host "Patch archive created at $patchArchive"
