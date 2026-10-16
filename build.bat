@echo off
cd /d "%~dp0"
rem Builds KinetikWeather.exe with the .NET Framework compiler that ships with Windows. No SDK needed.
rem "build.bat package" also makes KinetikWeather.zip (the exe, README and licence) for a release.
if not exist icon.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 || exit /b 1
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /utf8output /codepage:65001 /platform:x64 /target:winexe /out:KinetikWeather.new.exe /win32icon:icon.ico /resource:icon.ico,icon.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
  KinetikWeather.cs SuiteSupport.cs || exit /b 1
rem Keep previous binaries. Never delete recovery material during a build.
if not exist "..\Old Versions\" mkdir "..\Old Versions" || exit /b 1
if exist KinetikWeather.exe (
  for /f %%t in ('powershell -NoProfile -Command "[Guid]::NewGuid().ToString('N')"') do move /y KinetikWeather.exe "..\Old Versions\KinetikWeather-%%t.exe" >nul
  if exist KinetikWeather.exe exit /b 1
)
move /y KinetikWeather.new.exe KinetikWeather.exe >nul || exit /b 1
echo Built KinetikWeather.exe
if /i not "%~1"=="package" exit /b 0
powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1 -App KinetikWeather
if errorlevel 1 exit /b 1
exit /b 0
