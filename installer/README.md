# ParalivesAccess Installer

A separate, small Windows Forms application that downloads and installs the latest
stable release from `zersiax/ParalivesAccess`. The same EXE works for first installs,
updates and repairs. It is an extra release asset; the full ZIP remains available.
Installer versioning is independent of the mod. Version 1.0.0 targets .NET Framework
4.8 and uses only Windows/.NET libraries; no SDK, command prompt, GitHub login or
additional installer runtime is needed to use it.

## Build

From the repository root:

```powershell
.\scripts\Build-Installer.ps1
```

Distribute `artifacts/installer/ParalivesAccess-Installer.exe` and its adjacent
`.sha256` file. The EXE is standalone on a machine with .NET Framework 4.8; the
build's PDB, test runner, configuration and project files are not release assets.
There is no signing certificate configured in this repository.

## Behavior

- Detect Steam libraries from the Windows registry and `libraryfolders.vdf`; use
  app manifest 1118520 to locate Paralives. Allow manual folder selection.
- Check GitHub's latest stable release API. Select the full ZIP matching its tag,
  not a source archive, installer EXE or partial update bundle.
- Check the downloaded ZIP against GitHub's SHA-256 asset digest, then verify every
  unpacked file against `SHA256SUMS.txt` before writing to the game folder.
- Refuse unsafe/duplicate archive paths, links, unlisted files, unexpected package
  locations, incomplete loader bundles and junctions in destination paths. There
  is no package permission to replace the game executable, saves, BepInEx settings
  or other plugins.
- Update only missing/changed package files, or explicitly reinstall all of them.
  Retain backups of replaced files and an installation record under
  `%LOCALAPPDATA%\ParalivesAccess\Installer\Backups`. If an operation fails or is
  canceled, restore the files already touched. If restoration itself fails, report
  that explicitly and keep the backup location in the error.
- Refuse installation while Paralives is running, including if it starts during
  file application. Multiple installer instances are serialized with a mutex.
- Run without elevation by default. Access-denied errors offer an explicit
  administrator restart; the user handles Windows' permission prompt.

The UI uses native controls, labels, access keys, a read-only status field with a
polite UI Automation live-region notification, a reviewable progress list, and
ordinary completion/error dialogs. Notification content and actual audible output
should also be checked with the user's screen reader.

## Tests

```powershell
.\scripts\Test-Installer.ps1 -Package C:\path\to\ParalivesAccess-v0.52.18.zip -LiveGitHub
```

The console runner calls the built installer assembly against fresh disposable
game-marker directories. It checks fresh install, no-op update, repair, reinstall,
preservation of unrelated files, rollback, cancellation, locked files, a game
starting during application, malformed packages and download identities. The
PowerShell launcher creates a disposable junction to check destination isolation.
`-LiveGitHub` additionally checks the actual current API/download/install path,
digest rejection and network cancellation. The ordinary game is not an install
target in these tests.

Keyboard focus/order and native accessibility-tree inspection are separate UI
checks. A UAC prompt and another person's audible screen-reader experience are
not established by these engine tests.

## Publishing

Commit the source and tests, build and test the exact final EXE, then upload the
EXE and checksum alongside the existing full ZIP in both unified GitHub releases.
Do not replace or retag the mod merely to add an installer. Its stable download is:

https://github.com/zersiax/ParalivesAccess/releases/latest/download/ParalivesAccess-Installer.exe

The installer continues to use the public release source even when downloaded
from Patreon. Future mod releases should attach the installer asset too so that
the stable link continues to work.

## Platform references

- [Windows Forms accessibility properties](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/properties-on-windows-forms-controls-that-support-accessibility-guidelines)
- [GitHub release API and asset digests](https://docs.github.com/en/rest/releases/releases)
