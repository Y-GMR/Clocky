@echo off
if exist "%~dp0..\dist\Clocky.exe" (
    cd /d "%~dp0..\dist"
) else (
    cd /d "%~dp0..\src\Clocky\bin\Release\net9.0-windows"
)
echo Starting Clocky with Administrator elevation...
powershell -Command "Start-Process 'Clocky.exe' -Verb RunAs"
