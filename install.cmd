@echo off
setlocal
rem Installa "Invia a Unity" per l'utente corrente (non servono permessi di amministratore).
rem Compila SendToUnity.cs con il compilatore C# che fa gia' parte di Windows.

set "SRC=%~dp0SendToUnity.cs"
set "DEST=%LOCALAPPDATA%\SendToUnity"
set "EXE=%DEST%\SendToUnity.exe"

if not exist "%SRC%" (
    echo Non trovo SendToUnity.cs accanto a questo file.
    echo Se hai aperto lo zip senza estrarlo: estrai prima tutta la cartella, poi riprova.
    goto fail
)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Non trovo il compilatore C# di Windows ^(.NET Framework 4^).
    goto fail
)

if not exist "%DEST%" mkdir "%DEST%"
rem chiude eventuali finestre del programma rimaste aperte prima di sostituirlo
taskkill /im SendToUnity.exe /f >nul 2>&1

set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll"
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%EXE%" %REFS% "%SRC%"
if errorlevel 1 goto fail

rem Lo stesso codice come DLL per il menu del tasto destro. Ha un nome nuovo a ogni installazione
rem perche' Esplora risorse tiene bloccata quella che ha gia' caricato.
set "MENU=%DEST%\SendToUnityMenu-%RANDOM%%RANDOM%%RANDOM%.dll"
"%CSC%" /nologo /codepage:65001 /target:library /optimize+ /out:"%MENU%" %REFS% "%SRC%"
if errorlevel 1 goto fail

start "" /wait "%EXE%" --install "%MENU%"
if errorlevel 1 goto fail

echo.
echo Installato.
echo Tasto destro su file o cartelle ^> Invia a Unity.
echo Su Windows 11 la voce e' in "Mostra altre opzioni" (oppure Maiusc + tasto destro).
echo Oppure: tasto destro ^> Invia a ^> Progetto Unity (apre la finestra di scelta).
if /i not "%~1"=="/quiet" pause
exit /b 0

:fail
echo.
echo Installazione NON riuscita.
if /i not "%~1"=="/quiet" pause
exit /b 1
