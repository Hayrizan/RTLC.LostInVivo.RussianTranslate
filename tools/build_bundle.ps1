param(
    [string]$UnityEditor = $env:RTLC_UNITY2018_EDITOR,
    [string]$AuthoringProject = (Join-Path $env:USERPROFILE '.cache\lost-in-vivo-ru\bundle-authoring-2018'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\build\bundle')
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($UnityEditor)) { throw 'Pass -UnityEditor or set RTLC_UNITY2018_EDITOR to Unity 2018.2.21f1.' }
if (-not (Test-Path -LiteralPath $UnityEditor)) { throw "Unity 2018.2.21f1 not found: $UnityEditor" }
& python (Join-Path $PSScriptRoot 'prepare_bundle.py') --project $AuthoringProject --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Bundle staging failed.' }
$log = Join-Path $OutputDirectory 'unity-build.log'
$arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $AuthoringProject + '"'),
    '-executeMethod', 'BuildLocalizationBundle.Build', '-logFile', ('"' + $log + '"'))
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Host "Unity bundle build PID=$($process.Id); log=$log"
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $log" }
if (-not (Select-String -LiteralPath $log -Pattern 'RTLC_BUNDLE_OK' -Quiet)) { throw "Unity did not confirm bundle completion; see $log" }
Get-Item -LiteralPath (Join-Path $OutputDirectory 'rtlc.bundle') | Select-Object FullName, Length
