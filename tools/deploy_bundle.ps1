param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\dist\RTLC.LostInVivo.RussianTranslate')
)
$ErrorActionPreference = 'Stop'
$name = 'RTLC.LostInVivo.RussianTranslate'
$GameDirectory = [IO.Path]::GetFullPath($GameDirectory).TrimEnd('\')
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$expected = @(($name + '.dll'), 'rtlc.bundle')
$entries = @(Get-ChildItem -LiteralPath $PackageDirectory -Force)
if ($entries.Count -ne 2 -or @($entries | Where-Object { $_.PSIsContainer -or $_.Name -notin $expected }).Count) {
    throw 'The package must contain exactly the plugin DLL and rtlc.bundle.'
}
$mainExe = Join-Path $GameDirectory 'LIV.exe'
$running = @(Get-Process -Name LIV -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $mainExe })
if ($running.Count) { throw 'Close the main game before migrating its plugin directory. The prepared package is in dist.' }
$pluginParent = Join-Path $GameDirectory 'BepInEx\plugins'
$target = [IO.Path]::GetFullPath((Join-Path $pluginParent $name))
$backupParent = Join-Path $GameDirectory 'BepInEx\backups'
$stamp = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$backup = [IO.Path]::GetFullPath((Join-Path $backupParent ($name + '_loose_' + $stamp)))
$stage = [IO.Path]::GetFullPath((Join-Path $GameDirectory ('BepInEx\cache\rtlc-install-' + $stamp)))
# All directory moves below are literal, absolute and confined to this installation.
foreach ($path in @($target, $backup, $stage)) {
    if (-not $path.StartsWith($GameDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path outside game installation: $path" }
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($file in $expected) {
    $source = Join-Path $PackageDirectory $file
    $staged = Join-Path $stage $file
    Copy-Item -LiteralPath $source -Destination $staged
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $staged).Hash) { throw "Staged hash mismatch: $file" }
}
New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
$hadPrevious = Test-Path -LiteralPath $target
if ($hadPrevious) { Move-Item -LiteralPath $target -Destination $backup }
try { Move-Item -LiteralPath $stage -Destination $target }
catch {
    if ($hadPrevious -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }
    throw
}
foreach ($file in $expected) {
    if ((Get-FileHash -LiteralPath (Join-Path $PackageDirectory $file)).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $target $file)).Hash) { throw "Installed hash mismatch: $file" }
}
if (@(Get-ChildItem -LiteralPath $target -Force).Count -ne 2) { throw 'Unexpected installed directory entries.' }
Write-Host "Installed two-file package: $target"
if ($hadPrevious) { Write-Host "All previous editable files preserved: $backup" }
