param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskBundledDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
$taskDotnet = if (Test-Path -LiteralPath $taskBundledDotnet) { $taskBundledDotnet } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$taskProject = Join-Path $taskRoot 'src\Freshet\Freshet.csproj'
$taskConfig = Join-Path $taskRoot 'src\Freshet\NuGet.Config'
$taskOutput = Join-Path $taskRoot 'artifacts\publish\win-x64'
& $taskDotnet restore $taskProject --configfile $taskConfig --nologo
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
& $taskDotnet publish $taskProject -c $Configuration -o $taskOutput --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Freshet publish failed.' }
$taskRuntimeConfig = Join-Path $taskRoot "src\Freshet\bin\$Configuration\net10.0-windows\win-x64\Freshet.runtimeconfig.json"
$taskFrameworks = (Get-Content -LiteralPath $taskRuntimeConfig -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
$taskLicenses = Join-Path $taskOutput 'licenses'
New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
foreach ($taskFramework in $taskFrameworks) {
    $taskPackage = Join-Path $env:NUGET_PACKAGES ($taskFramework.name.ToLowerInvariant() + '.runtime.win-x64\' + $taskFramework.version)
    $taskLicense = Get-ChildItem -LiteralPath $taskPackage -File | Where-Object { $_.Name -in @('LICENSE', 'LICENSE.TXT') } | Select-Object -First 1
    if (!$taskLicense) { throw ('Missing runtime license: ' + $taskFramework.name) }
    Copy-Item -LiteralPath $taskLicense.FullName -Destination (Join-Path $taskLicenses ($taskFramework.name + '-LICENSE.txt')) -Force
    $taskNotices = Join-Path $taskPackage 'THIRD-PARTY-NOTICES.TXT'
    if (Test-Path -LiteralPath $taskNotices) {
        Copy-Item -LiteralPath $taskNotices -Destination (Join-Path $taskLicenses ($taskFramework.name + '-THIRD-PARTY-NOTICES.txt')) -Force
    }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\Freshet\Assets\Fonts\OFL.txt') -Destination (Join-Path $taskLicenses 'Pretendard-OFL.txt') -Force
Get-FileHash -LiteralPath (Join-Path $taskOutput 'Freshet.exe') -Algorithm SHA256 | ForEach-Object { $_.Hash + '  Freshet.exe' } | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256.txt') -Encoding utf8
Get-Item -LiteralPath (Join-Path $taskOutput 'Freshet.exe') | Select-Object FullName, Length
