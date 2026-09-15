' run_hidden.vbs
' Запускает PlaytimeWatcher.exe полностью в фоне, без всплывающего окна
' консоли. Весь вывод (в том числе [debug:...] строки) идёт в watcher.log
' рядом с exe, так как в скрытом окне его иначе было бы не увидеть.

Dim fso, scriptDir, exePath, logPath, shell, cmd

Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
exePath = scriptDir & "\PlaytimeWatcher.exe"
logPath = scriptDir & "\watcher.log"

Set shell = CreateObject("WScript.Shell")

' 0 = окно скрыто, False = не ждать завершения процесса
cmd = "cmd /c """ & exePath & """ >> """ & logPath & """ 2>&1"
shell.Run cmd, 0, False
