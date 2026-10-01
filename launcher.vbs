' Mette in coda i file ricevuti e avvia SendToUnity.ps1 senza finestra di PowerShell.
' Dal tasto destro Explorer avvia questo script una volta per ogni file selezionato:
' ogni copia scrive il suo file in coda (e' veloce), ma solo la prima avvia PowerShell.
Option Explicit
Dim sh, fso, appDir, queue, lock, f, name, a, lf

If WScript.Arguments.Count = 0 Then WScript.Quit

Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
appDir = fso.GetParentFolderName(WScript.ScriptFullName)
queue = sh.ExpandEnvironmentStrings("%TEMP%") & "\SendToUnity-queue"

On Error Resume Next
If Not fso.FolderExists(queue) Then fso.CreateFolder queue
On Error GoTo 0

' Scrive prima in .tmp e poi rinomina, cosi' PowerShell non legge mai un file a meta'
Randomize
name = queue & "\" & Replace(CStr(Timer), ".", "") & "_" & CStr(Int(Rnd * 1000000000)) & "_" & fso.GetBaseName(fso.GetTempName())
Set f = fso.CreateTextFile(name & ".tmp", True, True)   ' Unicode: nomi con qualsiasi carattere
For Each a In WScript.Arguments
    f.WriteLine a
Next
f.Close
fso.MoveFile name & ".tmp", name & ".txt"

' Lock: se esiste gia', un PowerShell sta per raccogliere la coda (anche questo file)
lock = queue & "\leader.lock"
On Error Resume Next
If fso.FileExists(lock) Then
    If DateDiff("s", fso.GetFile(lock).DateLastModified, Now) > 60 Then fso.DeleteFile lock, True
End If
Err.Clear
Set lf = fso.CreateTextFile(lock, False)
If Err.Number <> 0 Then WScript.Quit
On Error GoTo 0
lf.Close

sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & appDir & "\SendToUnity.ps1""", 0, False
