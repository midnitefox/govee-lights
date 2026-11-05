param([switch]$TestGpu)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$source = Join-Path $root 'src'
$output = Join-Path $root 'build\app'
$native = Join-Path $root 'build\native'
New-Item -ItemType Directory -Path $output, $native -Force | Out-Null
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $fx 'csc.exe'
& (Join-Path $root 'BuildIcon.ps1')
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework 4.x compiler is required.' }
if ($TestGpu) { & (Join-Path $root 'BuildNative.cmd') test }
else { & (Join-Path $root 'BuildNative.cmd') }
if ($LASTEXITCODE -ne 0) { throw 'Native capture build failed.' }
$refs = @('System.Runtime.Serialization.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll') | ForEach-Object { '/r:' + $_ }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationTypes.dll','UIAutomationProvider.dll') | ForEach-Object { '/r:' + (Join-Path (Join-Path $fx 'WPF') $_) }
$files = Get-ChildItem -LiteralPath $source -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler /nologo /warnaserror+ /target:winexe /main:GoveeLights.Program /platform:x64 /optimize+ "/win32manifest:$source\Lights.manifest" "/win32icon:$source\lights.ico" "/resource:$source\LightsWindow.xaml,LightsWindow.xaml" "/resource:$source\LightModels.tsv,LightModels.tsv" "/out:$output\Govee Lights.exe" @refs @files
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
& $compiler /nologo /warnaserror+ /target:exe /main:GoveeLights.McpProgram /platform:x64 /optimize+ "/win32manifest:$source\Lights.manifest" "/win32icon:$source\lights.ico" "/out:$output\Govee Lights MCP.exe" /r:System.Web.Extensions.dll "$source\AiProtocol.cs" "$source\McpServer.cs" "$source\AssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw 'MCP server build failed.' }
Copy-Item -LiteralPath "$output\Govee Lights MCP.exe" -Destination (Join-Path $root 'plugins\govee-lights\server\Govee Lights MCP.exe')
Copy-Item -LiteralPath (Join-Path $native 'GoveeCapture.dll') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'README-FIRST.txt'), (Join-Path $root 'LICENSE') -Destination $output
Write-Output "Built $output"
