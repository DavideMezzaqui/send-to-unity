@echo off
setlocal
rem Installs "Send to Unity" for the current user.
rem Compiles SendToUnity.cs with the C# compiler that already ships with Windows.

set "SRC=%~dp0SendToUnity.cs"
set "DEST=%LOCALAPPDATA%\SendToUnity"
set "EXE=%DEST%\SendToUnity.exe"

if not exist "%SRC%" (
    echo SendToUnity.cs not found next to this file.
    echo If you opened the zip without extracting it: extract the whole folder first, then try again.
    goto fail
)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo The Windows C# compiler ^(.NET Framework 4^) was not found.
    goto fail
)

if not exist "%DEST%" mkdir "%DEST%"
rem closes any program windows still open before replacing it
taskkill /im SendToUnity.exe /f >nul 2>&1

set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll"
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%EXE%" %REFS% "%SRC%"
if errorlevel 1 goto fail

rem The same code as a DLL for the right-click menu. It gets a new name on every install
rem because File Explorer keeps the one it already loaded locked.
set "MENU=%DEST%\SendToUnityMenu-%RANDOM%%RANDOM%%RANDOM%.dll"
"%CSC%" /nologo /codepage:65001 /target:library /optimize+ /out:"%MENU%" %REFS% "%SRC%"
if errorlevel 1 goto fail

rem On Windows 11, the first time, Windows asks for administrator permission for the short menu
start "" /wait "%EXE%" --install "%MENU%"
if errorlevel 1 goto fail

echo.
echo Installed.
echo Right-click files or folders ^> Send to Unity.
echo On Windows 11 the item is also in the short menu, if you gave the requested permission.
echo Or: right-click ^> Send to ^> Unity project (opens the window to choose).
echo Shortcut: select files in File Explorer and press Ctrl+Alt+U (sends to the last project).
echo Settings: run SendToUnity.exe again, or right-click ^> Send to Unity ^> Settings...
if /i not "%~1"=="/quiet" pause
exit /b 0

:fail
echo.
echo Installation FAILED.
if /i not "%~1"=="/quiet" pause
exit /b 1
