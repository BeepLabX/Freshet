$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = ([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'src\Freshet\Freshet.csproj') -Raw)).Project.PropertyGroup.Version
$taskExe = Join-Path $taskRoot 'artifacts\publish\win-x64\Freshet.exe'
if (!(Test-Path -LiteralPath $taskExe)) { throw 'Build Freshet first.' }
if ((Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion.Split('+')[0] -ne $taskVersion) { throw 'Build output version does not match the project.' }
$taskRelease = Join-Path $taskRoot ('artifacts\releases\' + $taskVersion)
New-Item -ItemType Directory -Path $taskRelease -Force | Out-Null
$taskApp = Join-Path $taskRoot ('.work\packaging\' + [guid]::NewGuid().ToString('N') + '\Freshet')
New-Item -ItemType Directory -Path $taskApp -Force | Out-Null
Copy-Item -LiteralPath $taskExe -Destination (Join-Path $taskApp 'Freshet.exe') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskApp 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\usage.txt') -Destination (Join-Path $taskApp '사용안내.txt') -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\Freshet\Assets\Fonts\OFL.txt') -Destination (Join-Path $taskApp 'Pretendard-OFL.txt') -Force
$taskLicenseSource = Join-Path $taskRoot 'artifacts\publish\win-x64\licenses'
foreach ($taskRequiredLicense in @('Microsoft.NETCore.App-LICENSE.txt', 'Microsoft.NETCore.App-THIRD-PARTY-NOTICES.txt', 'Microsoft.WindowsDesktop.App-LICENSE.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskLicenseSource $taskRequiredLicense))) { throw ('Missing runtime notice. Run scripts/build.ps1: ' + $taskRequiredLicense) }
}
Copy-Item -LiteralPath $taskLicenseSource -Destination (Join-Path $taskApp 'licenses') -Recurse -Force
$taskNoticeText = Get-Content -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Raw
$taskNoticeText.Replace('src/Freshet/Assets/Fonts/OFL.txt', 'Pretendard-OFL.txt') | Set-Content -LiteralPath (Join-Path $taskApp 'THIRD-PARTY-NOTICES.md') -Encoding utf8
$taskZipName = 'Freshet-' + $taskVersion + '-win-x64.zip'
$taskZip = Join-Path $taskRelease $taskZipName
Compress-Archive -LiteralPath $taskApp -DestinationPath $taskZip -Force
$taskFinalApp = Join-Path $taskRelease 'Freshet'
New-Item -ItemType Directory -Path $taskFinalApp -Force | Out-Null
Copy-Item -Path (Join-Path $taskApp '*') -Destination $taskFinalApp -Recurse -Force
$taskHashes = @(
    ((Get-FileHash -LiteralPath (Join-Path $taskApp 'Freshet.exe') -Algorithm SHA256).Hash + '  Freshet.exe'),
    ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash + '  ' + $taskZipName)
)
# No BOM, lowercase hashes: the format `sha256sum -c` expects.
$taskSums = ($taskHashes | ForEach-Object { $_.Substring(0, 64).ToLowerInvariant() + $_.Substring(64) + "`n" }) -join ''
[System.IO.File]::WriteAllText((Join-Path $taskRelease 'SHA256SUMS.txt'), $taskSums, (New-Object System.Text.UTF8Encoding $false))
Get-ChildItem -LiteralPath $taskRelease -Recurse -File | Select-Object FullName, Length
