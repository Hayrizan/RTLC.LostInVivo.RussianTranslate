param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$LoaderDirectory = (Join-Path $env:USERPROFILE '.cache\lost-in-vivo-ru\loader'),
    [string]$UnityEditor = $env:RTLC_UNITY2018_EDITOR,
    [switch]$Deploy = $true
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($UnityEditor)) { throw 'Pass -UnityEditor or set RTLC_UNITY2018_EDITOR to Unity 2018.2.21f1.' }
& python (Join-Path $PSScriptRoot 'build_catalog.py')
if ($LASTEXITCODE -ne 0) { throw 'Catalog merge failed.' }
& (Join-Path $PSScriptRoot 'build_bundle.ps1') -UnityEditor $UnityEditor
& python (Join-Path $PSScriptRoot 'verify_bundle.py') (Join-Path $root 'build\bundle\rtlc.bundle')
if ($LASTEXITCODE -ne 0) { throw 'Bundle verification failed.' }
& dotnet build (Join-Path $root 'src\RTLC.LostInVivo.RussianTranslate.csproj') -c Release "-p:GameDirectory=$GameDirectory" "-p:LoaderDirectory=$LoaderDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$package = Join-Path $root 'dist\RTLC.LostInVivo.RussianTranslate'
New-Item -ItemType Directory -Force -Path $package | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src\bin\Release\net35\RTLC.LostInVivo.RussianTranslate.dll') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $root 'build\bundle\rtlc.bundle') -Destination $package -Force
if ($Deploy) { & (Join-Path $PSScriptRoot 'deploy_bundle.ps1') -GameDirectory $GameDirectory -PackageDirectory $package }
Get-ChildItem -LiteralPath $package | Select-Object Name, Length
