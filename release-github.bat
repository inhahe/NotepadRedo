@echo off
setlocal
cd /d "%~dp0"

REM ===========================================================================
REM  release-github.bat  --  publish a new GitHub release of NotepadRedo.
REM
REM  Usage:  release-github.bat vMAJOR.MINOR.PATCH   (e.g. release-github.bat v1.0.1)
REM
REM  Builds a fresh, self-contained, single-file NotepadRedo.exe (no .NET install
REM  needed by end users) into a throwaway "release\" folder -- separate from the
REM  deployed/running copies, so it never fights an exe file lock -- then creates
REM  the release on GitHub with the exe and the associate-txt.bat helper attached.
REM
REM  It does NOT push source code: the local branch (master) and the remote
REM  (main) have diverged and there is no configured git remote, so pushing is
REM  left to you. The release is created against the remote's current default
REM  branch; the attached exe is built from your working tree.
REM ===========================================================================

set "REPO=inhahe/NotepadRedo"
set "VERSION=%~1"

if "%VERSION%"=="" goto :usage

REM --- GitHub CLI present? ---
where gh >nul 2>&1
if errorlevel 1 (
    echo.
    echo GitHub CLI ^(gh^) was not found on PATH. Install it from https://cli.github.com/
    echo and run "gh auth login", then re-run this script.
    exit /b 1
)

REM --- Warn if the working tree has uncommitted changes (exe won't match a commit). ---
set "DIRTY="
for /f "delims=" %%i in ('git status --porcelain 2^>nul') do set "DIRTY=1"
if defined DIRTY (
    echo.
    echo WARNING: you have uncommitted changes. The released exe is built from your
    echo current working tree, so it may not correspond to any committed revision.
    echo Press Ctrl+C to abort, or
    pause
)

echo.
echo Building self-contained release exe for %VERSION% ...
dotnet publish NotepadRedo.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%~dp0release"
if errorlevel 1 (
    echo.
    echo Build FAILED. Nothing was released.
    exit /b 1
)

if not exist "%~dp0release\NotepadRedo.exe" (
    echo.
    echo Build reported success but release\NotepadRedo.exe is missing. Aborting.
    exit /b 1
)

REM --- Optional extra asset: the .txt-association helper, if present. ---
set "EXTRA="
if exist "%~dp0associate-txt.bat" set "EXTRA=%~dp0associate-txt.bat"

echo.
echo Creating GitHub release %VERSION% on %REPO% ...
gh release create %VERSION% "%~dp0release\NotepadRedo.exe" %EXTRA% --repo %REPO% --title "NotepadRedo %VERSION%" --notes "Self-contained single-file build for Windows x64 -- no .NET install required; just run NotepadRedo.exe. See the README for the feature list. associate-txt.bat is an optional helper that offers to register NotepadRedo as a handler for .txt files."
if errorlevel 1 (
    echo.
    echo Release FAILED. A release or tag named %VERSION% may already exist.
    echo Current releases:
    gh release list --repo %REPO%
    exit /b 1
)

echo.
echo Done. Release %VERSION% is live. Opening it in your browser...
gh release view %VERSION% --repo %REPO% --web
endlocal
exit /b 0

:usage
echo Usage: release-github.bat vMAJOR.MINOR.PATCH
echo    e.g. release-github.bat v1.0.1
echo.
echo Builds a self-contained NotepadRedo.exe and publishes it as a new GitHub
echo release on %REPO%.
echo.
echo Current releases:
gh release list --repo %REPO% 2>nul
exit /b 1
