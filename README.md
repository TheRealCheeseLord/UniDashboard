# UniDashboard

**A native Windows dashboard for University of Vienna students.** It pulls your Moodle assignments, your u:space course registrations and your u:find timetables into one fast desktop app. It tells you when something changes, and it can mirror your timetable into Google Calendar or an `.ics` file without ever creating duplicates.

![Assignments with change history](docs/screenshots/assignment-history.png)

> **Unofficial.** UniDashboard is a personal, non-commercial tool. It is not provided by, affiliated with or endorsed by the Universität Wien, Moodle or Google. Course data comes from u:space and u:find and may be wrong or late; always check the official sources for anything that matters.

## Highlights

| | |
|---|---|
| **Assignments** | Every Moodle assignment, grouped *Overdue / Today / This week / Later / Submitted*, with relative due times, submission status and a link to the Moodle page. |
| **Change history** | Every change to an assignment (due date, description, grade, attachments) is stored as a snapshot and shown as a coloured diff. |
| **Calendar** | Week and month view combining lectures, exams, registration windows and assignment deadlines. Moved sessions show as ghosts at the old time, cancelled ones are struck through. |
| **Courses** | Your u:space enrolments with sessions, rooms, lecturers, waitlist position and all exam dates. |
| **Exam planner** | Suggests an exam date per course (as soon / as late / as spread out as possible) that avoids clashes with lectures and with each other. It only recommends; nothing is registered for you. |
| **Export** | Save an `.ics` file, or sync to a Google Calendar of your choice. Stable event ids mean re-importing updates entries instead of duplicating them. |
| **Notifications** | A hidden background task syncs every N minutes and raises Windows toasts for new assignments, moved sessions, exam registration windows and more. Every kind can be switched off. |
| **Word documents** | One click turns an assignment into a `.docx` (title page with your name and Matrikelnummer) from your own template. |
| **Private by design** | Everything is stored locally. Tokens are encrypted with Windows DPAPI, your password lives in the Windows Credential Manager. |

## Screenshots

<table>
<tr>
<td width="50%"><b>Assignments</b><br><img src="docs/screenshots/assignments.png"></td>
<td width="50%"><b>Calendar (week)</b><br><img src="docs/screenshots/calendar-week.png"></td>
</tr>
<tr>
<td><b>Calendar (month)</b><br><img src="docs/screenshots/calendar-month.png"></td>
<td><b>Courses</b><br><img src="docs/screenshots/courses.png"></td>
</tr>
<tr>
<td><b>Exam planner</b><br><img src="docs/screenshots/exam-planner.png"></td>
<td><b>Export to Google Calendar / .ics</b><br><img src="docs/screenshots/export-google.png"></td>
</tr>
<tr>
<td><b>Notification settings</b><br><img src="docs/screenshots/settings-notifications.png"></td>
<td><b>Background sync settings</b><br><img src="docs/screenshots/settings-sync.png"></td>
</tr>
</table>

*All screenshots use fake demo data* (see [Development](#development)).

## Requirements

- Windows 10 (1809) or Windows 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (only to build; the installed app is self-contained)
- Google Chrome or Microsoft Edge (Chrome is tried first; used for the university single sign-on)
- A University of Vienna u:account

## Install (build from source)

There is no installer yet; one script builds and installs the app for your user.

```powershell
git clone https://github.com/TheRealCheeseLord/UniDashboard.git
cd UniDashboard
.\publish.ps1
```

`publish.ps1` publishes the app and the background agent (self-contained, win-x64) to `%LOCALAPPDATA%\UniDashboard\bin`. It stops a running copy first, so you can run it again at any time to update. Start the app from `%LOCALAPPDATA%\UniDashboard\bin\UniDashboard.App.exe` (pin it to the taskbar or create a shortcut).

**Uninstall:** turn off *Settings → Sync → Sync in the background*, delete `%LOCALAPPDATA%\UniDashboard`, and remove the `UniDashboard/univie` entry from the Windows Credential Manager if you saved your password.

## First run

1. Open **Settings → Accounts**, enter your Moodle address and press **Log in**. Your browser opens for the university login; the app keeps the session.
2. Optionally save your u:account username and password under *University login* so the background agent can log in silently (stored in the Windows Credential Manager).
3. Open **Settings → Sync**, pick the Moodle courses to include and switch on **Sync in the background**.
4. Press **Sync now** on the Assignments page, and **Sync courses** on the Courses page.

More detail in [docs/getting-started.md](docs/getting-started.md).

## Documentation

- [Getting started](docs/getting-started.md): setup, login, background agent, troubleshooting
- [Features](docs/features.md): what every page does
- [Google Calendar setup](docs/google-calendar-setup.md): one-time OAuth client setup and the sync safety rules
- [Architecture](docs/architecture.md): projects, data flow, storage, security
- [Original design notes](docs/design-notes.md): the first (Moodle-only) spec, kept for history

## Privacy and security

- All data stays on your machine in `%LOCALAPPDATA%\UniDashboard` (SQLite database, logs). The app talks only to Moodle, u:space, u:find and, if you enable it, Google Calendar.
- Moodle token and Google credentials are encrypted with DPAPI for your Windows account (`token.bin`, `google.bin`).
- Your u:account password is kept in the Windows Credential Manager, never in the database.
- The Google integration asks only for permission to manage calendar events and to list your calendars, and it only ever changes events that carry its own hidden tag.

## Development

```powershell
dotnet build UniDashboard.slnx -p:Platform=x64
dotnet test UniDashboard.Core.Tests          # 180+ unit tests, no network needed
```

| Project | What it is |
|---|---|
| `UniDashboard.Core` | All logic: Moodle/u:space/u:find clients, change detection, storage, export, Google sync, exam planner |
| `UniDashboard.App` | The WinUI 3 desktop app |
| `UniDashboard.Agent` | Headless console app run by Task Scheduler: one sync pass, toasts, exit |
| `UniDashboard.Core.Tests` | xUnit tests (planner safety rules, differs, export, Google client with fakes) |
| `UniDashboard.Smoke` | Developer console tool: login/sync smoke tests and the demo-data seeder |

Setting the environment variable `UNIDASHBOARD_DATA_DIR` makes the app use a different data folder. `docs/capture-screenshots.ps1` uses it together with `UniDashboard.Smoke --seed-demo` to regenerate every screenshot from fake data without touching your real data.

## License

Not licensed yet: all rights reserved for now. Please ask before reusing the code.
