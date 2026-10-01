' Avvia SendToUnity.ps1 senza far comparire la finestra di PowerShell.
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
args = ""
For Each a In WScript.Arguments
    args = args & " """ & a & """"
Next
sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\SendToUnity.ps1""" & args, 0, False
