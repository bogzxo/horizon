@echo off
rem The tui command for cmd, a batch file on the PATH of a cmd the terminal starts like dawn.cmd. tui on its own says
rem which programs with a screen of their own there are and how to start them, tui name starts one in a tab of its own,
rem with -split or -down in a split instead. It says so with a private OSC (1717) and the terminal does the rest.
setlocal
set "where=tab"
set "name="

:words
if "%~1"=="" goto said
if /i "%~1"=="-split" set "where=split" & shift & goto words
if /i "%~1"=="--split" set "where=split" & shift & goto words
if /i "%~1"=="-down" set "where=down" & shift & goto words
if /i "%~1"=="--down" set "where=down" & shift & goto words
set "name=%~1"
shift
goto words

:said
if defined name goto asked
echo The programs with a screen of their own, each in a tab unless told -split or -down:
echo   tui files       a file picker, Ctrl+Shift+O
echo   tui git         the status and the log of the repository, Ctrl+Shift+G
echo   tui processes   what is running, and ending it
echo   tui settings    the terminal's settings
echo   tui repl        HIDL with a list of names, in a split
echo Your own go in the tui folder of Dusk's home, Ctrl+Shift+P and "tui" lists the lot.
goto done

:asked
rem cmd can't write an escape by itself, its prompt can, $E
for /f %%e in ('echo prompt $E^| cmd') do set "ESC=%%e"
<nul set /p "=%ESC%]1717;tui;%where%;%name%%ESC%\"

:done
endlocal
