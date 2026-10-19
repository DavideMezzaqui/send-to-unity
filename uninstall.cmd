@echo off
setlocal
rem Removes "Send to Unity": menu items, "Send to" shortcut and the program.
set "DEST=%LOCALAPPDATA%\SendToUnity"

taskkill /im SendToUnity.exe /f >nul 2>&1
if exist "%DEST%\SendToUnity.exe" start "" /wait "%DEST%\SendToUnity.exe" --uninstall

rem direct cleanup, in case the program is no longer there
reg delete "HKCU\Software\Classes\*\shell\SendToUnity" /f >nul 2>&1
reg delete "HKCU\Software\Classes\Directory\shell\SendToUnity" /f >nul 2>&1
reg delete "HKCU\Software\Classes\CLSID\{6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23}" /f >nul 2>&1
reg delete "HKCU\Software\Classes\*\shell\SendToUnityQuick" /f >nul 2>&1
reg delete "HKCU\Software\Classes\Directory\shell\SendToUnityQuick" /f >nul 2>&1
reg delete "HKCU\Software\Classes\CLSID\{8B2F4C6D-1E3A-4F5B-9C7D-0A1B2C3D4E5F}" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v SendToUnity /f >nul 2>&1
del "%APPDATA%\Microsoft\Windows\SendTo\Unity project.lnk" >nul 2>&1
del "%APPDATA%\Microsoft\Windows\SendTo\Progetto Unity.lnk" >nul 2>&1
powershell -NoProfile -Command "Get-AppxPackage -Name 'DAVFX.InviaAUnity*' | Remove-AppxPackage" >nul 2>&1
rmdir /s /q "%DEST%" >nul 2>&1

echo Uninstalled.
if /i not "%~1"=="/quiet" pause
exit /b 0
