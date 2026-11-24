# Getting started

## 1. Build and install

Prerequisites: Windows 10 (1809) / 11 x64, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Git, and Chrome or Edge.

```powershell
git clone https://github.com/TheRealCheeseLord/UniDashboard.git
cd UniDashboard
.\publish.ps1
```

The script builds `UniDashboard.App` and `UniDashboard.Agent` as self-contained x64 apps into `%LOCALAPPDATA%\UniDashboard\bin`. Nothing is installed system-wide and no admin rights are needed. To update later: `git pull` and run `.\publish.ps1` again (it closes a running copy first).

Start `%LOCALAPPDATA%\UniDashboard\bin\UniDashboard.App.exe`.

## 2. Connect Moodle

1. **Settings → Accounts → Moodle address**: the address of your Moodle (for example the University of Vienna Moodle).
2. Press **Log in / log in again**. A browser window opens for the university single sign-on. When it finishes, the window closes and the app stores a Moodle token (encrypted with DPAPI). It only asks again when Moodle says the token expired.

## 3. Silent background login (optional, recommended)

Under *University login* you can store your u:account username and password. They go into the Windows Credential Manager (entry `UniDashboard/univie`), never into the app database. With them the background agent can refresh sessions without opening a browser. If the university rejects the password the app stops retrying, so it cannot lock your account; save new credentials to re-enable it.

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
| Login window never completes | Complete MFA in the opened browser; make sure Chrome or Edge is installed. Log files in `logs\` show details. |
| No toasts | Make sure Windows notifications / Focus Assist allow the app, and that the relevant kind is ticked in *Settings → Notifications*. |
| Agent does not run | Open Task Scheduler and look for "UniDashboard Agent"; toggle *Sync in the background* off and on to re-register it. |
| Something looks wrong after an update | Close the app, run `.\publish.ps1` again. |
