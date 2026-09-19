@echo off
rem ParentCLT watchdog: reactiva el servicio si no esta en RUNNING.
sc query "ParentCLT Agent" | findstr /C:"RUNNING" >nul
if errorlevel 1 (
    sc start "ParentCLT Agent" >nul 2>&1
)