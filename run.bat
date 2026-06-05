@echo off
REM VulcanScope — pobierz dane i otwórz dashboard
cd /d "%~dp0"
py -3.12 export.py
if exist dashboard.html start "" "dashboard.html"
