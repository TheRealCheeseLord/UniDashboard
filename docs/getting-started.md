# Getting started

## 1. Install

**Installer (recommended).** Download `UniDashboard-Setup-x.y.z.exe` from the [latest release](https://github.com/TheRealCheeseLord/UniDashboard/releases/latest) and run it. It installs per user into `%LOCALAPPDATA%\UniDashboard\bin` (no admin rights) and adds a Start Menu entry. Because the installer is not code-signed yet, Windows SmartScreen shows "Windows protected your PC": click **More info → Run anyway**. Verify the download against the `.sha256` file on the release page if you like.

Prerequisites: Windows 10 (1809) / 11 x64. The manual sign-in window needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 and Microsoft Edge (the installer does not need a separate browser).

**From source.** Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Git:

```powershell
git clone https://github.com/TheRealCheeseLord/UniDashboard.git
cd UniDashboard
.\publish.ps1
```

This installs to the same folder. Run it again after `git pull` to update.

Start UniDashboard from the Start Menu (or `%LOCALAPPDATA%\UniDashboard\bin\UniDashboard.App.exe`).

### Updating

The app looks for a newer release when it starts (at most once a day) and shows a banner. **Install and restart** downloads the new installer, checks its SHA-256, installs it over the old version and reopens the app; your data and settings are kept. Control this under **Settings → About & updates** (switch off, or *Check now*). Running a newer installer manually works too.

### Uninstalling

*Settings → Apps → Installed apps → UniDashboard*. It removes the background task and asks whether to also delete your data (database, saved logins, logs); answer *No* to keep it.
## 2. Connect Moodle

1. **Settings → Accounts → Moodle address**: the address of your Moodle (for example the University of Vienna Moodle).
2. Press **Log in / log in again**. A sign-in window opens for the university single sign-on (it is skipped when the saved u:account password from step 3 lets the app sign in on its own). When it finishes, the window closes and the app stores a Moodle token (encrypted with DPAPI). It only asks again when Moodle says the token expired.

## 3. Silent background login (optional, recommended)

Under *University login* you can store your u:account username and password. They go into the Windows Credential Manager (entry `UniDashboard/univie`), never into the app database. With them the background agent can refresh sessions on its own, without any window. If the university rejects the password the app stops retrying, so it cannot lock your account; save new credentials to re-enable it.

Without stored credentials everything still works, but when a session expires you have to log in again from the app (the agent sends a "login required" notification).

## 4. Choose what to sync

**Settings → Sync**

- Tick the Moodle courses that should be included. Unticked courses are not synced and their assignments are hidden.
- Turn on **Sync in the background**. This registers a hidden Windows scheduled task ("UniDashboard Agent", runs only while you are logged on) that starts at logon and then every *N* minutes (default 30).
- *Remind before a deadline* controls the deadline toast (default 24 hours).

**Settings → Notifications** lets you choose exactly which events create a Windows toast. Everything is recorded in the app regardless.

## 5. First sync

- **Assignments → Sync now** fetches assignments from Moodle.
- **Courses → Sync courses** fetches your u:space enrolments, sessions and exams, enriched with u:find data.

After that the background agent keeps both up to date.

## Data locations

| What | Where |
|---|---|
| Database (SQLite) | `%LOCALAPPDATA%\UniDashboard\data.db` |
| Moodle token (DPAPI) | `%LOCALAPPDATA%\UniDashboard\token.bin` |
| Google credentials (DPAPI) | `%LOCALAPPDATA%\UniDashboard\google.bin` |
| Logs (7 days, small) | `%LOCALAPPDATA%\UniDashboard\logs\` |
| Password | Windows Credential Manager, `UniDashboard/univie` |

Set the environment variable `UNIDASHBOARD_DATA_DIR` to use a different folder (handy for testing).

## Troubleshooting

| Problem | What to try |
|---|---|
| `publish.ps1` fails on restore/build | Check `dotnet --version` shows 10.x; run from a normal PowerShell, not as a different user. |
| Login window never completes | Complete the login (and any second factor) in the sign-in window. If no window opens, install the [Microsoft Edge WebView2 Runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703). Log files in `logs\` show details. |
| Background sync asks for a login again | The university may have changed its login pages, or the saved password was rejected. Press **Log in / log in again** once; see `logs\`. |
| No toasts | Make sure Windows notifications / Focus Assist allow the app, and that the relevant kind is ticked in *Settings → Notifications*. |
| Agent does not run | Open Task Scheduler and look for "UniDashboard Agent"; toggle *Sync in the background* off and on to re-register it. |
| Something looks wrong after an update | Close the app, run `.\publish.ps1` again. |
