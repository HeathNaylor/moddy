@echo off
setlocal enabledelayedexpansion
set "URL=%~1"
if "!URL!"=="" exit /b
set "QUEUE_DIR=%LOCALAPPDATA%\Moddy\nxm_queue"
if not exist "!QUEUE_DIR!" mkdir "!QUEUE_DIR!"
set "FNAME=%RANDOM%_%RANDOM%"
> "!QUEUE_DIR!\!FNAME!.nxmurl" echo(!URL!
exit /b
