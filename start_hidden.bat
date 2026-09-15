@echo off
REM start_hidden.bat
REM Точка входа для Bloxstrap "Пользовательские интеграции".
REM Сам .bat откроется на долю секунды (это неизбежно для cmd.exe), но сразу
REM передаёт запуск в run_hidden.vbs, который уже полностью прячет процесс.

wscript.exe "%~dp0run_hidden.vbs"
