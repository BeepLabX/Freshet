param([switch]$LiveScan, [switch]$LiveMetadata, [switch]$UseBuildHost)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExe = Join-Path $taskRoot 'artifacts\publish\win-x64\Freshet.exe'
if (!(Test-Path -LiteralPath $taskExe)) { throw 'Run scripts/build.ps1 first.' }
$taskBaseArguments = @()
if ($UseBuildHost) {
    $taskDll = Join-Path $taskRoot 'src\Freshet\bin\Release\net10.0-windows\win-x64\Freshet.dll'
    if (!(Test-Path -LiteralPath $taskDll)) { throw 'Build the Release configuration first.' }
    $taskLocalHost = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
    $taskExe = if (Test-Path -LiteralPath $taskLocalHost) { $taskLocalHost } else { (Get-Command dotnet -ErrorAction Stop).Source }
    $taskBaseArguments = @('"' + $taskDll + '"')
}
$taskOutput = Join-Path $taskRoot ('artifacts\verification\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskTests = Join-Path $taskOutput 'self-test'
$taskProcess = Start-Process -FilePath $taskExe -ArgumentList ($taskBaseArguments + @('--self-test', ('"' + $taskTests + '"'))) -WindowStyle Hidden -Wait -PassThru
if ($taskProcess.ExitCode -ne 0) {
    if (Test-Path -LiteralPath ($taskTests + '.error.txt')) { Get-Content -LiteralPath ($taskTests + '.error.txt') }
    throw 'Freshet self-tests failed.'
}
Get-Content -LiteralPath (Join-Path $taskTests 'results.txt')
if ($LiveScan -or $LiveMetadata) {
    $taskScan = Join-Path $taskOutput 'live-scan.json'
    $taskMode = if ($LiveMetadata) { '--metadata-diagnose' } else { '--diagnose' }
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList ($taskBaseArguments + @($taskMode, ('"' + $taskScan + '"'))) -WindowStyle Hidden -Wait -PassThru
    if ($taskProcess.ExitCode -ne 0) { throw ('Live scan failed. Check ' + $taskOutput) }
    $taskResult = Get-Content -LiteralPath $taskScan -Raw | ConvertFrom-Json
    $taskResult | Select-Object ExitCode, Skipped, HadTable, CheckedPackage, CheckedVersion
    'Update candidates: ' + $taskResult.Packages.Count
    if ($LiveMetadata) {
        'Sizes available: ' + @($taskResult.Packages | Where-Object { $null -ne $_.EstimatedBytes }).Count
        'Local app icons: ' + @($taskResult.Packages | Where-Object { $_.IconOrigin -ne '기본 아이콘' }).Count
    }
}
'Verification artifacts: ' + $taskOutput
