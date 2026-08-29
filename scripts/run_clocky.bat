@echo off
cd /d "%~dp0src\Clocky\bin\Release\net9.0-windows"
echo Starting Clocky with Administrator elevation...
powershell -Command "Start-Process 'Clocky.exe' -Verb RunAs"
