param([switch]$SkipBuild, [switch]$LiveReadOnly, [string[]]$Entries=@('VerifyScenes','VerifyLibraryUI','VerifyReplies'))
$ErrorActionPreference = 'Stop'
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -TestGpu }
$source = Join-Path $PSScriptRoot 'src'
$tests = Join-Path $PSScriptRoot 'tests'
$run = Join-Path $PSScriptRoot 'build\test-run'
New-Item -ItemType Directory -Path $run, (Join-Path $run 'work') -Force | Out-Null
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes','UIAutomationProvider') | ForEach-Object { '/r:' + (Join-Path (Join-Path $fx 'WPF') ($_ + '.dll')) }
$files = @((Get-ChildItem -LiteralPath $source -Filter '*.cs').FullName) + @((Get-ChildItem -LiteralPath $tests -Filter '*.cs').FullName)
Push-Location $run
try {
    foreach ($entry in $Entries) {
        $exe = Join-Path $run ($entry + '.exe')
        & (Join-Path $fx 'csc.exe') /nologo /optimize+ /warnaserror+ /platform:x64 "/main:$entry" "/out:$exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.Web.Extensions.dll /r:System.Xaml.dll "/resource:$source\LightsWindow.xaml,LightsWindow.xaml" "/resource:$source\LightModels.tsv,LightModels.tsv" @refs @files
        if ($LASTEXITCODE -ne 0) { throw "Test build failed for $entry" }
        if ($entry -eq 'VerifyReplies' -and $LiveReadOnly) { & $exe --live } else { & $exe }
        if ($LASTEXITCODE -ne 0) { throw "Failed $entry" }
    }
} finally { Pop-Location }
