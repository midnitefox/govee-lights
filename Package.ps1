param([string]$Version = '1.0.0', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a semantic version such as 1.0.0.' }
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
$app = Join-Path $PSScriptRoot 'build\app'
$info = (Get-Item -LiteralPath (Join-Path $app 'Govee Lights.exe')).VersionInfo
if ($info.FileVersion -ne ($Version + '.0')) { throw 'The package version must match src/AssemblyInfo.cs.' }
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$archive = Join-Path $dist ("Govee-Lights-v" + $Version + "-win-x64.zip")
Compress-Archive -LiteralPath (Join-Path $app 'Govee Lights.exe'), (Join-Path $app 'Govee Lights MCP.exe'), (Join-Path $app 'GoveeCapture.dll'), (Join-Path $app 'README-FIRST.txt'), (Join-Path $app 'LICENSE') -DestinationPath $archive -Force
$digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
($digest + ' *' + [IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
$pluginArchive = Join-Path $dist ("Govee-Lights-OpenAI-plugin-v" + $Version + ".zip")
Compress-Archive -Path (Join-Path $PSScriptRoot 'plugins\govee-lights\*') -DestinationPath $pluginArchive -Force
$pluginDigest = (Get-FileHash -Algorithm SHA256 -LiteralPath $pluginArchive).Hash.ToLowerInvariant()
($pluginDigest + ' *' + [IO.Path]::GetFileName($pluginArchive)) | Add-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Output $archive
