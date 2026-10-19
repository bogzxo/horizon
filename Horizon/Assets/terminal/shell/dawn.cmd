@echo off
rem The dawn command for cmd, which has no functions, so it is a batch file on the PATH of a cmd the terminal starts
rem (see TerminalIntegration.Environment). dawn file.cs edits the file in place of the terminal until Ctrl+Q, dawn -open
rem path hands a file or a folder to the Dawn that's running, dawn -window path opens a new Dawn window on it, and dawn
rem on its own opens the folder cmd is in. It says so with a private OSC (1717) and the terminal does the rest.
setlocal
set "mode=inline"
set "target="

:words
if "%~1"=="" goto said
if /i "%~1"=="-w" set "mode=window" & shift & goto words
if /i "%~1"=="-window" set "mode=window" & shift & goto words
if /i "%~1"=="--window" set "mode=window" & shift & goto words
if /i "%~1"=="-o" set "mode=open" & shift & goto words
if /i "%~1"=="-open" set "mode=open" & shift & goto words
if /i "%~1"=="--open" set "mode=open" & shift & goto words
set "target=%~f1"
shift
goto words

:said
if defined target goto escaped
if "%mode%"=="inline" set "mode=open"
set "target=%CD%"

:escaped
rem cmd can't write an escape by itself, its prompt can, $E
for /f %%e in ('echo prompt $E^| cmd') do set "ESC=%%e"
<nul set /p "=%ESC%]1717;dawn;%mode%;%target%%ESC%\"
endlocal
