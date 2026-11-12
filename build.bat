@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo No se encontro csc.exe de .NET Framework 4.
  exit /b 1
)

if not exist "src\app.ico" powershell -NoProfile -ExecutionPolicy Bypass -File "tools\make-icon.ps1"
if not exist dist mkdir dist

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"dist\MonitorIPs.exe" ^
  /win32icon:"src\app.ico" ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll ^
  /resource:"src\web\index.html",index.html ^
  /resource:"src\app.ico",app.ico ^
  src\*.cs
if errorlevel 1 (
  echo.
  echo *** ERROR de compilacion ***
  exit /b 1
)
echo.
echo Listo: dist\MonitorIPs.exe
endlocal
