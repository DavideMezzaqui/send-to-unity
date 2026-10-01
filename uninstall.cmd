@echo off
setlocal
rem Rimuove "Invia a Unity": voci del menu, collegamento in "Invia a" e programma.
set "DEST=%LOCALAPPDATA%\SendToUnity"

taskkill /im SendToUnity.exe /f >nul 2>&1
if exist "%DEST%\SendToUnity.exe" start "" /wait "%DEST%\SendToUnity.exe" --uninstall

rem pulizia diretta, nel caso il programma non ci sia piu'
reg delete "HKCU\Software\Classes\*\shell\SendToUnity" /f >nul 2>&1
reg delete "HKCU\Software\Classes\Directory\shell\SendToUnity" /f >nul 2>&1
del "%APPDATA%\Microsoft\Windows\SendTo\Progetto Unity.lnk" >nul 2>&1
rmdir /s /q "%DEST%" >nul 2>&1

echo Disinstallato.
if /i not "%~1"=="/quiet" pause
exit /b 0
