# Packaging and releasing LogicControl

Same shape as NetControl, Redline PDF, File Manager and File Compare: a public GitHub repository, a
per-user installer built by Inno Setup, and a release cut by pushing a tag. GitHub builds, tests and
packages; this PC only needs git.

## One-time setup

**1. Create the repository.** Public, named `LogicControl`, under `Robbuie`, with nothing in it (no
README, no licence - both are already here). Those two names are in
`src/LogicControl.App/Diagnostics/BuildInfo.cs` (`RepositoryOwner` / `RepositoryName`) and nowhere
else that matters: the update check, the Help menu and the workflows all derive their URLs from
them.

**2. Push.**

```powershell
cd C:\Users\rjokr\Projects\LogicControl
git init
git add .
git commit -m "LogicControl 0.2.0"
git branch -M main
git remote add origin https://github.com/Robbuie/LogicControl.git
git push -u origin main
```

**3. Watch the first `verify` run** at https://github.com/Robbuie/LogicControl/actions. It is the
first time the WPF project has ever been compiled - the code so far was built and tested on Linux,
where the engine and the view models compile but WPF's XAML cannot. If it is red, the log names
the file and line; fix, commit, push. Once it is green, turn `TreatWarningsAsErrors` on in
`Directory.Build.targets` the way NetControl has it.

**4. Build tools, only if you want to build the installer locally.** Inno Setup 6 and the .NET 10
SDK. Python with Pillow only to redraw the icon (`python tools/icon.py`); its output is committed.

## Cutting a release

```powershell
cd C:\Users\rjokr\Projects\LogicControl

# 1. Bump VersionPrefix in Directory.Build.props and add a "## 0.3.0 - <what it is>" section to
#    the top of CHANGELOG.md.
# 2. Commit and push to main - no tag yet.
git add -A
git commit -m "0.3.0 - <what it is>"
git push

# 3. Wait for the "verify" run on that commit to go green. If it is red, fix it in another commit.
# 4. Then tag the green commit. This is what builds and publishes the release.
git tag v0.3.0
git push --tags
```

`.github/workflows/release.yml` runs the suite, publishes the single self-contained exe, wraps it
with Inno Setup, and publishes a GitHub release with `gh` - the installer, the portable exe, a
SHA256 file beside each, and `version.json`. The release text is that version's section of
`CHANGELOG.md`. It fails if the tag and `VersionPrefix` disagree.

## Building locally instead

```powershell
pwsh tools/publish.ps1              # the portable exe only
pwsh tools/publish.ps1 -Installer   # and dist_installer\LogicControl-Setup-<version>.exe
```

The script runs the tests first, stamps the short commit onto the version (`0.2.0+a1b2c3d`), and
marks a build from an uncommitted tree `-dirty`.

## Two artefacts, on purpose

| | |
|---|---|
| `LogicControl-Setup-<version>.exe` | Per-user install into `%LOCALAPPDATA%\Programs\LogicControl`. Start menu entry, Add/Remove Programs entry, optional `.lcdev` association. No administrator prompt. |
| `LogicControl.exe` | The same tool as one loose self-contained file. Copy it anywhere and run it. |

**Both are load-bearing for updating, and so are their `.sha256` files.** The app picks the asset
matching what it is and refuses to download one without a checksum beside it.

**Never change the `AppId` GUID in `installer/LogicControl.iss`.** It is how Windows - and the
in-app updater, which reads the same uninstall key in `Diagnostics/InstallLocation.cs` - recognise
an existing install.

## How the update check works

Ported unchanged from NetControl:

- On startup the app asks GitHub for the latest release and **says nothing unless there is a newer
  one** - a failed check on a network with no route out is logged, not shown.
- **Help > Check for updates** always answers.
- **Clicking the status-bar line installs it**: an installed copy runs the new setup silently; a
  portable copy swaps itself for the new exe. Both come back on the new version. Every download is
  checked against its published SHA256 first.
- It **refuses while the development set has unsaved changes**, and says so.
- A site with no route to github.com points `updateManifestUrl` in
  `%LOCALAPPDATA%\LogicControl\settings.json` at a mirrored `version.json`; a site that wants no
  outbound request sets `"checkForUpdates": false`.

## Worth knowing

**The exe is unsigned.** SmartScreen will warn the first people who download it until the download
builds reputation. *More info -> Run anyway*, or a code-signing certificate.

**The repository is public; the licence is not open source.** See [LICENSE](LICENSE).
