param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$projectFile = Join-Path $projectRoot 'DeepSeekFishPet.csproj'
$outputRoot = Join-Path $projectRoot 'dist'
$publishDir = Join-Path $outputRoot "DeepSeekFishPet-$Runtime"
$zipFile = Join-Path $outputRoot "DeepSeekFishPet-$Runtime.zip"
$pluginSourceDir = Join-Path $projectRoot 'Integrations\DeepSeekHarness'
$pluginPublishDir = Join-Path $publishDir 'Integrations\DeepSeekHarness'
$pluginFiles = @(
    'README.md',
    'install.ps1',
    'package.json',
    'LICENSE-CrossPet.txt',
    'lib\index.js',
    'lib\balance.js'
)

if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Cannot find $projectFile"
}
foreach ($relative in $pluginFiles) {
    $source = Join-Path $pluginSourceDir $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Cannot find DeepSeek Harness plugin file: $source"
    }
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& dotnet publish $projectFile -c Release -r $Runtime --self-contained true `
    '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:PublishTrimmed=false' `
    '-p:DebugType=None' `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exeFile = Join-Path $publishDir 'DeepSeekFishPet.exe'
if (-not (Test-Path -LiteralPath $exeFile -PathType Leaf)) {
    throw "Published executable not found: $exeFile"
}

$readmeFile = Join-Path $projectRoot 'README.md'
$packageFiles = @(
    [pscustomobject]@{ Source = $exeFile; Entry = 'DeepSeekFishPet.exe' }
)
if (Test-Path -LiteralPath $readmeFile -PathType Leaf) {
    $publishedReadme = Join-Path $publishDir 'README.md'
    Copy-Item -LiteralPath $readmeFile -Destination $publishedReadme -Force
    $packageFiles += [pscustomobject]@{ Source = $publishedReadme; Entry = 'README.md' }
}

foreach ($relative in $pluginFiles) {
    $source = Join-Path $pluginSourceDir $relative
    $publishedFile = Join-Path $pluginPublishDir $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $publishedFile) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $publishedFile -Force
    $entry = 'Integrations/DeepSeekHarness/' + $relative.Replace('\', '/')
    $packageFiles += [pscustomobject]@{ Source = $publishedFile; Entry = $entry }
}

if (Test-Path -LiteralPath $zipFile -PathType Leaf) {
    Remove-Item -LiteralPath $zipFile -Force
}
# Add exact entries so test files and local Harness state can never enter the ZIP.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zipFile, [IO.Compression.ZipArchiveMode]::Create)
$complete = $false
try {
    foreach ($file in $packageFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.Source, $file.Entry,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $complete = $true
} finally {
    $archive.Dispose()
    if (-not $complete -and (Test-Path -LiteralPath $zipFile -PathType Leaf)) {
        Remove-Item -LiteralPath $zipFile -Force
    }
}
Write-Host "Portable app: $publishDir"
Write-Host "Portable ZIP: $zipFile"
