@echo off
setlocal
cd /d "%~dp0"

REM ===========================================================================
REM  release-github.bat  --  publish a GitHub release of the CURRENT version.
REM
REM  Usage:  release-github.bat            (use /?, -h or --help for this help)
REM
REM  The version is NOT bumped here. It is read from the VERSION file at the
REM  project root -- the single source of truth, also stamped into the exe by
REM  the csproj. Bump VERSION on every build (see CLAUDE.md); this script just
REM  releases whatever version VERSION currently holds.
REM
REM  If a release tag for that version already exists on GitHub, the script
REM  ABORTS (nothing to release until VERSION is bumped). Otherwise it builds a
REM  fresh, self-contained, single-file NotepadRedo.exe into a throwaway
REM  "release\" folder -- separate from the deployed/running copies, so it never
REM  fights an exe file lock -- and publishes the release with the exe and the
REM  associate-txt.bat helper attached.
REM
REM  It does NOT push source code: the local branch (master) and the remote
REM  (main) have diverged and there is no configured git remote, so pushing is
REM  left to you. The release is created against the remote's current default
REM  branch; the attached exe is built from your working tree.
REM ===========================================================================

set "REPO=inhahe/NotepadRedo"

if /i "%~1"=="/?"     goto :usage
if /i "%~1"=="-h"     goto :usage
if /i "%~1"=="--help" goto :usage

REM --- GitHub CLI present? (needed to check existing releases and to publish) ---
where gh >nul 2>&1
if errorlevel 1 (
    echo.
    echo GitHub CLI ^(gh^) was not found on PATH. Install it from https://cli.github.com/
    echo and run "gh auth login", then re-run this script.
    exit /b 1
)

REM --- Read the current version from the VERSION file (single source of truth). ---
if not exist "%~dp0VERSION" (
    echo.
    echo VERSION file not found next to this script. Cannot determine the version.
    exit /b 1
)
set "APPVER="
set /p APPVER=<"%~dp0VERSION"
REM Trim a stray trailing carriage return / spaces that set /p can leave behind.
for /f "tokens=* delims= " %%v in ("%APPVER%") do set "APPVER=%%v"
if "%APPVER%"=="" (
    echo.
    echo VERSION file is empty. Put a version like 1.0.1 in it and re-run.
    exit /b 1
)
set "TAG=v%APPVER%"

echo.
echo Current version is %APPVER% (release tag %TAG%).

REM --- Abort if this version is already released. ---
gh release view %TAG% --repo %REPO% >nul 2>&1
if not errorlevel 1 (
    echo.
    echo Release %TAG% already exists on %REPO% -- nothing to publish.
    echo Bump the version in the VERSION file and rebuild before releasing.
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
echo Building self-contained release exe for %TAG% ...
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
REM  The value keeps its surrounding quotes so the path (which contains spaces, e.g.
REM  "...\visual studio projects\...") reaches gh as a SINGLE argument. Passing it unquoted
REM  makes gh see split words like "studio" and fail with "no matches found for `studio`".
set "EXTRA="
if exist "%~dp0associate-txt.bat" set EXTRA="%~dp0associate-txt.bat"

echo.
echo Creating GitHub release %TAG% on %REPO% ...
gh release create %TAG% "%~dp0release\NotepadRedo.exe" %EXTRA% --repo %REPO% --title "NotepadRedo %TAG%" --notes "Self-contained single-file build for Windows x64 -- no .NET install required; just run NotepadRedo.exe. See the README for the feature list. associate-txt.bat is an optional helper that offers to register NotepadRedo as a handler for .txt files."
if errorlevel 1 (
    echo.
    echo Release FAILED. Current releases:
    gh release list --repo %REPO%
    exit /b 1
)

echo.
echo Done. Release %TAG% is live. Opening it in your browser...
gh release view %TAG% --repo %REPO% --web
endlocal
exit /b 0

:usage
echo Usage: release-github.bat
echo.
echo Publishes a GitHub release of the version currently in the VERSION file
echo (which is also stamped into the exe). The version is not bumped here --
echo bump VERSION on every build; this script releases whatever it holds and
echo aborts if that version is already released on %REPO%.
echo.
echo Current version in VERSION file:
type "%~dp0VERSION" 2>nul
echo.
echo Current releases:
gh release list --repo %REPO% 2>nul
exit /b 1
