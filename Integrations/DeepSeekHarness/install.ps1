#Requires -Version 5.1
<#
.SYNOPSIS
  Manually install or uninstall the DeepSeek Fish Pet plugin for one DeepSeek Harness profile.
.EXAMPLE
  .\install.ps1 -ProfileDirectory "$HOME\.dsh\profiles\web"
.EXAMPLE
  .\install.ps1 -ProfileDirectory "$HOME\.dsh\profiles\web" -Uninstall
.NOTES
  Run only while Harness is closed. Each changed profile file receives a timestamped backup.
  Uninstall leaves the shared copy in %LOCALAPPDATA%\DeepSeekFishPet\harness-plugin,
  because another profile may still use it. Delete it manually after uninstalling all profiles.
#>
[CmdletBinding()]
param(
    [string]$ProfileDirectory,
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pluginName = '@local/dsh-plugin-deepseek-fish-pet'
$cordisId = 'deepseek-fish-pet'
$beginMarker = '# DeepSeekFishPet begin'
$endMarker = '# DeepSeekFishPet end'
$sourceDirectory = $PSScriptRoot
$stableDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'DeepSeekFishPet\harness-plugin'))
$linkValue = 'link:' + $stableDirectory.Replace('\', '/')
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Resolve-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
}

function Same-Path([string]$First, [string]$Second) {
    return [string]::Equals((Resolve-FullPath $First), (Resolve-FullPath $Second), [StringComparison]::OrdinalIgnoreCase)
}

function Select-Profile {
    if ($ProfileDirectory) {
        $selected = Resolve-FullPath $ProfileDirectory
        if (-not [IO.Directory]::Exists($selected)) { throw "Profile directory does not exist: $selected" }
        if (-not [IO.File]::Exists((Join-Path $selected 'package.json'))) {
            throw "Profile has no package.json: $selected"
        }
        return $selected
    }

    $roots = @()
    if ($env:USERPROFILE) { $roots += (Join-Path $env:USERPROFILE '.dsh\profiles') }
    if ($env:APPDATA) { $roots += (Join-Path $env:APPDATA 'dsh-desktop\harness\profiles') }
    $candidates = @()
    foreach ($root in $roots) {
        if (-not [IO.Directory]::Exists($root)) { continue }
        if ([IO.File]::Exists((Join-Path $root 'package.json'))) {
            $candidates += (Resolve-FullPath $root)
        }
        foreach ($name in @('desktop', 'web')) {
            $candidate = Join-Path $root $name
            if ([IO.File]::Exists((Join-Path $candidate 'package.json'))) {
                $candidates += (Resolve-FullPath $candidate)
            }
        }
    }
    $candidates = @($candidates | Select-Object -Unique)
    if ($candidates.Count -eq 0) {
        throw 'No DeepSeek Harness profile found. Specify -ProfileDirectory explicitly.'
    }
    $ranked = @($candidates | ForEach-Object {
        $pkg = [IO.File]::ReadAllText((Join-Path $_ 'package.json'))
        $rank = if ($pkg.Contains($pluginName)) { 0 }
            elseif ([IO.Path]::GetFileName($_) -eq 'desktop') { 1 }
            elseif ([IO.Path]::GetFileName($_) -eq 'web') { 2 }
            else { 3 }
        [pscustomobject]@{ Path = $_; Rank = $rank }
    } | Sort-Object Rank, Path)
    Write-Host "Selected Harness profile: $($ranked[0].Path) (override with -ProfileDirectory)"
    return $ranked[0].Path
}

function Get-PatchEdit([string]$Original, [bool]$Removing) {
    $newline = if ($Original.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in [regex]::Split($Original, '\r\n|\n|\r')) { $lines.Add($line) }

    $idPattern = '^(?<indent>[ \t]*)-[ \t]+id:[ \t]*["'']?deepseek-fish-pet["'']?[ \t]*(?:#.*)?$'
    $namePattern = '^name:[ \t]*["'']?@local/dsh-plugin-deepseek-fish-pet["'']?[ \t]*(?:#.*)?$'
    $entries = [Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch $idPattern) { continue }
        $indent = $Matches.indent.Length
        $end = $i + 1
        $validName = $false
        while ($end -lt $lines.Count) {
            $next = $lines[$end]
            if ($next -eq $endMarker) { break }
            if ($next.Trim() -eq '') { $end++; continue }
            if ($next -match '^([ \t]*)') { $nextIndent = $Matches[1].Length } else { $nextIndent = 0 }
            if ($nextIndent -le $indent) { break }
            if ($next.Trim() -match $namePattern) { $validName = $true }
            $end++
        }
        $entries.Add([pscustomobject]@{ Start = $i; End = $end; ValidName = $validName })
    }
    if ($entries.Count -gt 1) { throw "Multiple Cordis entries use id '$cordisId'; edit cordis.patch.yml manually." }
    if ($entries.Count -eq 1 -and -not $entries[0].ValidName) {
        throw "Cordis id '$cordisId' belongs to a different package; refusing to overwrite it."
    }

    if (-not $Removing) {
        if ($entries.Count -eq 1) { return $Original }
        $prefix = if ($Original.Length -eq 0) { '' }
            elseif ($Original.EndsWith("`n")) { '' }
            else { $newline }
        $block = @(
            $beginMarker,
            '- insert:',
            "    - id: $cordisId",
            "      name: '$pluginName'",
            $endMarker,
            ''
        ) -join $newline
        return ($Original + $prefix + $block)
    }

    if ($entries.Count -eq 0) { return $Original }
    $entry = $entries[0]
    # Remove only this item and its children. Harness may have added another item
    # beneath our - insert: line after the original installation.
    for ($j = $entry.End - 1; $j -ge $entry.Start; $j--) { $lines.RemoveAt($j) }

    $beginIndex = $lines.IndexOf($beginMarker)
    $endIndex = $lines.IndexOf($endMarker)
    if ($beginIndex -ge 0 -and $endIndex -gt $beginIndex) {
        $headerIndex = -1
        for ($j = $beginIndex + 1; $j -lt $endIndex; $j++) {
            if ($lines[$j] -match '^[ \t]*-[ \t]+insert:[ \t]*$') { $headerIndex = $j; break }
        }
        if ($headerIndex -ge 0) {
            $headerIndent = ([regex]::Match($lines[$headerIndex], '^[ \t]*')).Length
            $hasOtherChildren = $false
            for ($j = $headerIndex + 1; $j -lt $lines.Count; $j++) {
                $trimmed = $lines[$j].Trim()
                if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
                $lineIndent = ([regex]::Match($lines[$j], '^[ \t]*')).Length
                if ($lineIndent -le $headerIndent) { break }
                $hasOtherChildren = $true
                break
            }
            if (-not $hasOtherChildren) { $lines.RemoveAt($headerIndex) }
        }
        $lines.Remove($beginMarker) | Out-Null
        $lines.Remove($endMarker) | Out-Null
    }
    $result = [string]::Join($newline, $lines)
    return $result
}

function Save-ChangedFile([string]$Path, [string]$NewContent, [string]$BackupSuffix) {
    $exists = [IO.File]::Exists($Path)
    if ($exists -and [IO.File]::ReadAllText($Path) -ceq $NewContent) { return $null }
    $backup = $null
    if ($exists) {
        $backup = $Path + $BackupSuffix
        [IO.File]::Copy($Path, $backup, $false)
    }
    $temporary = $Path + '.tmp-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, $NewContent, $utf8)
        if ($exists) { [IO.File]::Replace($temporary, $Path, $backup) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
    return $backup
}

function Get-JunctionTarget([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Existing node_modules entry is not a link: $Path"
    }
    $target = @($item.Target)[0]
    if (-not $target) { throw "Cannot inspect existing link target: $Path" }
    if (-not (Same-Path $target $stableDirectory)) {
        throw "Existing node_modules link points elsewhere: $Path -> $target"
    }
    return $target
}

function Assert-NotLink([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to overwrite a linked plugin file or directory: $Path"
    }
}

$profile = Select-Profile
$packagePath = Join-Path $profile 'package.json'
$patchPath = Join-Path $profile 'cordis.patch.yml'
$junctionPath = Join-Path $profile 'node_modules\@local\dsh-plugin-deepseek-fish-pet'
$packageOriginal = [IO.File]::ReadAllText($packagePath)
$package = $packageOriginal | ConvertFrom-Json
if ($package -isnot [pscustomobject]) { throw 'Profile package.json must contain a JSON object.' }
$dependenciesProperty = $package.PSObject.Properties['dependencies']
$dependencies = if ($dependenciesProperty) { $dependenciesProperty.Value } else { $null }
if ($null -ne $dependencies -and $dependencies -isnot [pscustomobject]) {
    throw 'Profile package.json dependencies must be a JSON object.'
}
$installedDependency = if ($null -ne $dependencies) { $dependencies.PSObject.Properties[$pluginName] } else { $null }
if ($installedDependency -and $installedDependency.Value -cne $linkValue) {
    throw "Dependency '$pluginName' already points elsewhere: $($installedDependency.Value)"
}
$patchOriginal = if ([IO.File]::Exists($patchPath)) { [IO.File]::ReadAllText($patchPath) } else { '' }
$patchNew = Get-PatchEdit $patchOriginal $Uninstall.IsPresent
$junctionTarget = Get-JunctionTarget $junctionPath

if ($Uninstall) {
    if ($installedDependency) {
        $dependencies.PSObject.Properties.Remove($pluginName)
    }
} else {
    $sourcePackagePath = Join-Path $sourceDirectory 'package.json'
    $sourcePackage = [IO.File]::ReadAllText($sourcePackagePath) | ConvertFrom-Json
    if ($sourcePackage.name -cne $pluginName -or $sourcePackage.main -cne 'lib/index.js') {
        throw 'Plugin package.json name or main does not match the installer.'
    }
    foreach ($relative in @('package.json', 'lib\index.js', 'lib\balance.js', 'LICENSE-CrossPet.txt')) {
        if (-not [IO.File]::Exists((Join-Path $sourceDirectory $relative))) {
            throw "Plugin source file is missing: $relative"
        }
    }
    if ($null -eq $dependencies) {
        $dependencies = [pscustomobject]@{}
        $package | Add-Member -NotePropertyName 'dependencies' -NotePropertyValue $dependencies
    }
    if (-not $installedDependency) {
        $dependencies | Add-Member -NotePropertyName $pluginName -NotePropertyValue $linkValue
    }
}

$packageNew = if (($Uninstall -and $installedDependency) -or (-not $Uninstall -and -not $installedDependency)) {
    ($package | ConvertTo-Json -Depth 100) + "`n"
} else { $packageOriginal }

if (-not $Uninstall) {
    # Copy only files owned by this plugin. Never clear the stable directory.
    Assert-NotLink $stableDirectory
    Assert-NotLink (Join-Path $stableDirectory 'lib')
    [IO.Directory]::CreateDirectory($stableDirectory) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $stableDirectory 'lib')) | Out-Null
    foreach ($relative in @('package.json', 'lib\index.js', 'lib\balance.js', 'LICENSE-CrossPet.txt')) {
        $source = Join-Path $sourceDirectory $relative
        $target = Join-Path $stableDirectory $relative
        Assert-NotLink $target
        if (-not [IO.File]::Exists($target) -or
            -not ([Linq.Enumerable]::SequenceEqual([byte[]][IO.File]::ReadAllBytes($source), [byte[]][IO.File]::ReadAllBytes($target)))) {
            [IO.File]::Copy($source, $target, $true)
        }
    }
}

$suffix = '.bak-deepseek-fish-pet-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$packageExisted = [IO.File]::Exists($packagePath)
$patchExisted = [IO.File]::Exists($patchPath)
$packageBackup = $null
$patchBackup = $null
$createdJunction = $false
try {
    if ($packageNew -cne $packageOriginal) {
        $packageBackup = Save-ChangedFile $packagePath $packageNew $suffix
    }
    if ($patchNew -cne $patchOriginal) {
        $patchBackup = Save-ChangedFile $patchPath $patchNew $suffix
    }
    if ($Uninstall) {
        if ($junctionTarget) { [IO.Directory]::Delete($junctionPath, $false) }
    } elseif (-not $junctionTarget) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $junctionPath)) | Out-Null
        New-Item -ItemType Junction -Path $junctionPath -Target $stableDirectory | Out-Null
        $createdJunction = $true
    }
} catch {
    if ($createdJunction -and (Get-JunctionTarget $junctionPath)) {
        [IO.Directory]::Delete($junctionPath, $false)
    }
    if ($patchBackup) { [IO.File]::Copy($patchBackup, $patchPath, $true) }
    elseif (-not $patchExisted -and [IO.File]::Exists($patchPath)) { [IO.File]::Delete($patchPath) }
    if ($packageBackup) { [IO.File]::Copy($packageBackup, $packagePath, $true) }
    throw
}

$action = if ($Uninstall) { 'Uninstalled from' } else { 'Installed into' }
Write-Host "$action Harness profile: $profile"
if ($packageBackup) { Write-Host "Backed up package.json: $packageBackup" }
if ($patchBackup) { Write-Host "Backed up cordis.patch.yml: $patchBackup" }
if (-not $Uninstall) { Write-Host "Stable plugin copy: $stableDirectory" }
