@echo off
setlocal
REM ===========================================================================================
REM Register NotepadRedo as a selectable handler for .txt files (per-user; no admin required).
REM
REM Why this is needed: Windows only offers "Always use this app to open .txt files" when the
REM application DECLARES that it can open the type, via a registered Applications entry with a
REM SupportedTypes list (and, for the Settings > Default apps UI, a Capabilities block). A bare
REM exe you merely browse to in "Open with > Choose another app" is treated as a one-off, so
REM Windows shows only "Just once".
REM
REM This script does NOT (and cannot) silently force NotepadRedo to be THE default: Windows
REM protects the final .txt default with a hashed UserChoice key. After running this, set the
REM default yourself the normal way -- right-click a .txt > Open with > Choose another app >
REM NotepadRedo > "Always", or Settings > Apps > Default apps.
REM
REM Usage:  associate-txt.bat  [full-path-to-NotepadRedo.exe]
REM         (defaults to D:\utils\NotepadRedo.exe)
REM ===========================================================================================

set "EXE=D:\utils\NotepadRedo.exe"
if not "%~1"=="" set "EXE=%~1"

if not exist "%EXE%" (
    echo ERROR: "%EXE%" does not exist. Pass the correct path as the first argument.
    exit /b 1
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

REM --- Capabilities: makes NotepadRedo show up in Settings > Apps > Default apps ---
reg add "HKCU\Software\NotepadRedo\Capabilities" /v ApplicationName /d "NotepadRedo" /f >nul
reg add "HKCU\Software\NotepadRedo\Capabilities" /v ApplicationDescription /d "Branching-undo text editor" /f >nul
reg add "HKCU\Software\NotepadRedo\Capabilities\FileAssociations" /v ".txt" /d "NotepadRedo.txt" /f >nul
reg add "HKCU\Software\RegisteredApplications" /v "NotepadRedo" /d "Software\NotepadRedo\Capabilities" /f >nul

echo.
echo Done. NotepadRedo is now a registered .txt handler for "%EXE%".
echo.
echo Set it as the default one of these ways:
echo   - Right-click any .txt ^> Open with ^> Choose another app ^> NotepadRedo ^> "Always".
echo   - Settings ^> Apps ^> Default apps ^> search .txt (or NotepadRedo) ^> set to NotepadRedo.
echo.
echo The "Always" option will now be available (it was missing because the exe wasn't registered).
endlocal
