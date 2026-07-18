@echo off
setlocal
REM ===========================================================================================
REM Optional: make NotepadRedo your DEFAULT .txt handler (per-user; no admin required).
REM
REM You normally do NOT need this script. NotepadRedo already registers itself as *able* to open
REM .txt every time it launches, so it shows up in "Open with" with an "Always use this app"
REM option. This script is only a convenience for setting it as the default in one step.
REM
REM It ASKS before changing anything. On "Yes" it (re)writes the per-user capability registration
REM and opens Windows' Default Apps settings so you can pick NotepadRedo. Windows guards the final
REM .txt default with a hashed UserChoice key, so no script can silently force it -- the last click
REM is always yours.
REM
REM Usage:  associate-txt.bat  [full-path-to-NotepadRedo.exe]   (defaults to D:\utils\NotepadRedo.exe)
REM ===========================================================================================

set "EXE=D:\utils\NotepadRedo.exe"
if not "%~1"=="" set "EXE=%~1"

if not exist "%EXE%" (
    echo ERROR: "%EXE%" does not exist. Pass the correct path as the first argument.
    exit /b 1
)

echo.
echo This will register NotepadRedo as a selectable .txt handler and open Default Apps settings
echo so you can make it your default text editor.
echo   Exe: %EXE%
echo.
set "ANSWER=N"
set /p "ANSWER=Associate .txt files with NotepadRedo? [y/N] "
if /I not "%ANSWER%"=="Y" (
    echo Skipped. No changes made.
    exit /b 0
)

set "APP=HKCU\Software\Classes\Applications\NotepadRedo.exe"
set "PROGID=HKCU\Software\Classes\NotepadRedo.txt"

echo Registering NotepadRedo as a .txt handler for the current user...

REM --- ProgId: the concrete "how to open" definition ---
reg add "%PROGID%" /ve /d "Text Document" /f >nul
reg add "%PROGID%\DefaultIcon" /ve /d "%EXE%,0" /f >nul
reg add "%PROGID%\shell\open\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul

REM --- Applications entry: makes the app appear in "Open with" and enables the "Always" option ---
reg add "%APP%" /v FriendlyAppName /d "NotepadRedo" /f >nul
reg add "%APP%\shell\open\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul
reg add "%APP%\SupportedTypes" /v ".txt" /d "" /f >nul

REM --- Offer NotepadRedo in the .txt "Open with" list ---
reg add "HKCU\Software\Classes\.txt\OpenWithProgids" /v "NotepadRedo.txt" /d "" /f >nul

REM --- Capabilities: makes NotepadRedo show up in Settings > Default apps ---
reg add "HKCU\Software\NotepadRedo\Capabilities" /v ApplicationName /d "NotepadRedo" /f >nul
reg add "HKCU\Software\NotepadRedo\Capabilities" /v ApplicationDescription /d "Branching-undo text editor" /f >nul
reg add "HKCU\Software\NotepadRedo\Capabilities\FileAssociations" /v ".txt" /d "NotepadRedo.txt" /f >nul
reg add "HKCU\Software\RegisteredApplications" /v "NotepadRedo" /d "Software\NotepadRedo\Capabilities" /f >nul

echo.
echo Registered. Opening Default Apps settings -- set the .txt file type (or NotepadRedo) to
echo NotepadRedo to finish. You can also right-click any .txt ^> Open with ^> Choose another app
echo ^> NotepadRedo ^> "Always".
start "" "ms-settings:defaultapps"
endlocal
