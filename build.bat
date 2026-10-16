@echo off
cd /d "%~dp0"
rem Builds KinetikWeather.exe with the .NET Framework compiler that ships with Windows. No SDK needed.
rem "build.bat package" also makes KinetikWeather.zip (the exe, README and licence) for a release.
if not exist icon.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 || exit /b 1
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /utf8output /codepage:65001 /platform:x64 /target:winexe /out:KinetikWeather.new.exe /win32icon:icon.ico /resource:icon.ico,icon.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
  KinetikWeather.cs || exit /b 1
rem Built under a new name first, so this works while the app is running. The exe it replaces goes to ..\Old Versions
rem (when that folder is there), stamped with the date and time, to go back to if a build misbehaves.
if exist KinetikWeather.exe if exist "..\Old Versions\" (
  for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd-HHmmss"') do move /y KinetikWeather.exe "..\Old Versions\KinetikWeather-%%t.exe" >nul
)
if exist KinetikWeather.exe move /y KinetikWeather.exe KinetikWeather.old.exe >nul
move /y KinetikWeather.new.exe KinetikWeather.exe >nul || exit /b 1
if exist KinetikWeather.old.exe del KinetikWeather.old.exe 2>nul
echo Built KinetikWeather.exe
if /i not "%~1"=="package" exit /b 0
if exist dist rmdir /s /q dist
mkdir dist\KinetikWeather
copy /y KinetikWeather.exe dist\KinetikWeather\ >nul
copy /y README.md dist\KinetikWeather\ >nul
copy /y LICENSE dist\KinetikWeather\ >nul
if exist KinetikWeather.zip del KinetikWeather.zip
powershell -NoProfile -Command "Compress-Archive -Path dist\KinetikWeather\* -DestinationPath KinetikWeather.zip"
rmdir /s /q dist
echo Made KinetikWeather.zip
