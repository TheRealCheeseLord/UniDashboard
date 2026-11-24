# Releasing

How a new version gets from this repository to your friends' machines.

```
git tag v0.2.0 ─► GitHub Actions (release.yml) ─► tests ─► installer\build-installer.ps1 ─► GitHub Release
                                                                                              │
installed app (≤ once a day) ◄── asks the GitHub API for the latest release ◄─────────────────┘
        └─ banner → "Install and restart" → download, verify SHA-256, run the installer silently
```

## Publishing a release

1. Make sure `main` is green (the *CI* workflow builds and runs the tests on every push).
2. Tag and push:

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. The *Release* workflow (`.github/workflows/release.yml`) runs the tests, builds `UniDashboard-Setup-0.2.0.exe` plus its `.sha256`, and creates the GitHub Release with auto-generated notes. Edit the notes on GitHub afterwards if you want something friendlier: they are shown in the app's update banner.

After a release, bump the default in `Directory.Build.props` to that version so local builds do not look older than what is published.

The version number comes **from the tag** (`v0.2.0` → `0.2.0`) and overrides the default in `Directory.Build.props`. Tags with a suffix (`v0.3.0-rc1`) become *pre-releases*, which the in-app updater ignores, so you can test an installer by hand without updating anyone.

## Building the installer locally

```powershell
winget install JRSoftware.InnoSetup      # once
.\installer\build-installer.ps1 -Version 0.2.0
```

Output: `artifacts\UniDashboard-Setup-0.2.0.exe` and `.exe.sha256`. The installer script is `installer\UniDashboard.iss`:

- per-user, no admin; installs to `%LOCALAPPDATA%\UniDashboard\bin` (the same place `publish.ps1` uses); the program folder is replaced on every install so no stale files remain, user data one level up is never touched;
- stops the hidden background agent before replacing files and uses Windows' Restart Manager to close the app;
- `/LAUNCH` (used by the updater) starts the new version afterwards, through Explorer;
- the uninstaller removes the scheduled task "UniDashboard Agent" and asks before deleting user data and the saved `UniDashboard/univie` credential.

## How the updater decides

`UniDashboard.Core/Update/UpdateService.cs` calls `GET /repos/TheRealCheeseLord/UniDashboard/releases/latest` (unauthenticated; GitHub's limit of 60 requests an hour is no issue at one check per day). An update is offered only if:

- the release is neither a draft nor a pre-release,
- its version is higher than the running one,
- it contains `UniDashboard-Setup-*.exe` **and** a SHA-256 (the `.sha256` file, or GitHub's own asset digest).

The download is hashed before it is run; a mismatch deletes the file and shows an error. Network or API errors simply mean "no update". For testing, set `UNIDASHBOARD_UPDATE_URL` to another release JSON (an `https://` or `file://` URL) before starting the app.

## Testing an update end to end

1. Install version N (any build).
2. Build a higher version, e.g. `.\installer\build-installer.ps1 -Version 9.9.9`.
3. Write a small release JSON pointing at it, with the file's hash in `digest`:

   ```json
   {"tag_name":"v9.9.9","body":"test","assets":[{"name":"UniDashboard-Setup-9.9.9.exe",
     "browser_download_url":"file:///C:/path/to/artifacts/UniDashboard-Setup-9.9.9.exe","digest":"sha256:<hash>"}]}
   ```

4. Start the app with `UNIDASHBOARD_UPDATE_URL=file:///C:/path/to/latest.json` and press *Check now* under *Settings → About & updates*.

## Code signing (planned)

Releases are unsigned, so SmartScreen warns on first run. Since the project is public and GPL-licensed it should qualify for free signing through the [SignPath Foundation](https://signpath.org/); once approved, a signing step goes between *Build installer* and *Publish release* in `release.yml`. Reputation then builds with the certificate rather than with each new file.
