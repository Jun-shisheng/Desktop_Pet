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

if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Cannot find $projectFile"
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
$packageFiles = @($exeFile)
if (Test-Path -LiteralPath $readmeFile -PathType Leaf) {
    $publishedReadme = Join-Path $publishDir 'README.md'
    Copy-Item -LiteralPath $readmeFile -Destination $publishedReadme -Force
    $packageFiles += $publishedReadme
}

if (Test-Path -LiteralPath $zipFile -PathType Leaf) {
    Remove-Item -LiteralPath $zipFile -Force
}
Compress-Archive -LiteralPath $packageFiles -DestinationPath $zipFile -CompressionLevel Optimal
Write-Host "Portable app: $publishDir"
Write-Host "Portable ZIP: $zipFile"
