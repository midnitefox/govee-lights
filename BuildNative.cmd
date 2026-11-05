@echo off
setlocal
set "REPO=%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo Install Visual Studio Build Tools with Desktop development with C++ and a Windows SDK.
  exit /b 1
)
for /f "usebackq delims=" %%v in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "TOOLS_VS=%%v"
if not defined TOOLS_VS exit /b 1
call "%TOOLS_VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
pushd "%REPO%build\native"
fxc /nologo /T cs_5_0 /E main /O3 /Fh DownsampleShader.h /Vn GoveeDownsampleShader "%REPO%src\Downsample.hlsl" || exit /b 1
cl /nologo /O2 /W4 /WX /MT /EHsc /std:c++17 /LD /I. "%REPO%src\GoveeCapture.cpp" /link /OUT:GoveeCapture.dll d3d11.lib dxgi.lib user32.lib || exit /b 1
if /i "%~1"=="test" (
  cl /nologo /O2 /W4 /WX /MT /EHsc /std:c++17 /I. /I"%REPO%src" "%REPO%tests\CheckGpu.cpp" /link /OUT:CheckGpu.exe d3d11.lib dxgi.lib user32.lib || exit /b 1
  CheckGpu.exe || exit /b 1
)
popd
exit /b 0
