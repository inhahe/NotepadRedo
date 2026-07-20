# NotepadRedo

WPF .NET 8 text editor (namespace / AssemblyName `NotepadRedo`). `README.md` is user-facing
documentation — keep it current whenever an observable feature changes.

## Versioning — bump `VERSION` on every build

The app version lives in **one place: the `VERSION` file** at the project root (e.g. `1.0.0`). The
csproj reads it into `<Version>`, so it's stamped into the built exe, and `release-github.bat` reads
it to tag the GitHub release.

**On every build/deploy of NotepadRedo, bump the number in `VERSION` first.** Increment the patch
component for ordinary changes (`1.0.0` → `1.0.1`); bump minor/major for larger ones. Do this as part
of the same change so the built binary and any release carry the new version. (Commit the bumped
`VERSION` with the build.)

## Publishing a release

`release-github.bat` (no argument) publishes a GitHub release of **whatever version `VERSION`
currently holds** — it does **not** bump anything. It aborts if a release for that version already
exists on `inhahe/NotepadRedo`, so it only publishes after `VERSION` has been bumped past the last
release. It builds a fresh self-contained single-file exe into a throwaway `release\` folder (separate
from the deployed/running copies, so no exe file-lock conflict and no need to close the app) and
attaches the exe plus `associate-txt.bat`. It does **not** push source (local `master` and remote
`main` have diverged; there is no configured git remote).
