# UniDashboard — Spec

A lean, Windows-native dashboard for Moodle (University of Vienna) assignments. It is the "lite" version of a bigger Go/Tauri project, but a **fully standalone codebase** with no shared code.
It runs on one personal Windows 11 machine only.

## Goals

- Fetch all assignments and show them sorted and grouped by due date
- Link each assignment to its Moodle page
- Show submission status (handed in or not)
- Track changes to each assignment over time (version history with diffs)
- Generate a `.docx` from a template for an assignment
- Run a background agent that syncs regularly and sends Windows toast notifications

## Non-goals

- No downloading of attachments: show only the count and link to Moodle
- No submitting to Moodle
- No cross-platform support, no MSIX/Store packaging
- No overdue notifications

## Stack

- .NET 10 (LTS), C#
- WinUI 3 (Windows App SDK), **unpackaged**, self-contained, installed to `%LOCALAPPDATA%\UniDashboard`
- `Microsoft.Playwright` (use `Channel = "msedge"`, so no browser download is needed)
- `Microsoft.Data.Sqlite` (WAL mode)
- `DocumentFormat.OpenXml` for .docx generation
- `DiffPlex` for text diffs
- `TaskScheduler` (NuGet `Microsoft.Win32.TaskScheduler`) to register the agent
- `Microsoft.Windows.AppNotifications` for toasts (`AppNotificationManager.Default.Register()`)
- CommunityToolkit.Mvvm for MVVM

## Solution layout

```
UniDashboard.sln
├─ UniDashboard.Core    class library: all logic (auth, API, storage, diff, docx, notification rules)
├─ UniDashboard.App     WinUI 3 dashboard
└─ UniDashboard.Agent   headless console exe: one sync pass + toasts, then exit
```

The App and the Agent both reference Core. Both read from and write to the same SQLite DB at `%LOCALAPPDATA%\UniDashboard\data.db`.

## Core

### Auth (`AuthService`)
- Port of the existing Python Playwright univie SAML login:
  1. Open `{moodle}/admin/tool/mobile/launch.php?service=moodle_mobile_app&passport={random}&urlscheme=moodlemobile`
  2. Complete the univie SSO login
  3. Catch the `moodlemobile://token=...` redirect (intercept the navigation/request)
  4. Base64-decode it → `signature:::token[:::privatetoken]`
- Store the token with DPAPI (`ProtectedData`, CurrentUser scope) in `%LOCALAPPDATA%\UniDashboard\token.bin`
- Credentials: Windows Credential Manager (or interactive login only)
- Re-authenticate only when a call returns `errorcode: invalidtoken`
- **The Agent never shows a browser.** If headless login fails (e.g. MFA), the Agent sends a toast "Moodle login required" → opens the App, which runs Playwright headed.

### API (`MoodleClient`)
REST calls to `{moodle}/webservice/rest/server.php?wstoken=...&moodlewsrestformat=json&wsfunction=...`

| Function | Purpose |
|---|---|
| `core_webservice_get_site_info` | userid, token check |
| `core_course_get_enrolled_courses_by_timeline_classification` (`classification=inprogress`) | courses |
| `mod_assign_get_assignments` (`courseids[]`) | title, intro, duedate, cmid, introattachments |
| `mod_assign_get_submission_status` (`assignid`) | submitted?, grade/feedback |

- Assignment link: `{moodle}/mod/assign/view.php?id={cmid}`
- The Moodle base URL is a setting

### Storage (SQLite, WAL, short transactions)

```
courses(id PK, name, shortname, enabled BOOL DEFAULT 1)
assignments(id PK, course_id, cmid, name, intro_html, due_at, attachment_count,
            attachments_json, submitted BOOL, grade TEXT NULL, feedback TEXT NULL,
            content_hash, last_seen_at, unseen_change BOOL)
assignment_snapshots(id PK, assignment_id, taken_at, hash, json, change_kinds)
notifications_sent(assignment_id, kind, key, sent_at, PK(assignment_id, kind, key))
settings(key PK, value)
```

### Change detection (`ChangeDetector`)
- Normalize each field (strip HTML whitespace noise, order attachments by filename+size+timemodified) and hash it.
- If anything differs from the stored state, save a new snapshot with `change_kinds` set (a set of the kinds below).
- Every change is saved as a snapshot. Only some kinds send a toast:

| Kind | Toast | Notes |
|---|---|---|
| `New` | ✅ | "New: {title} ({course}) — due {date}" |
| `DueDateChanged` | ✅ | show old → new |
| `DescriptionChanged` | ✅ | "Description updated" — diff is in the app |
| `GradeOrFeedback` | ✅ | include the grade if available |
| `AttachmentsChanged` | ❌ silent | set `unseen_change` (dot in the list) |
| `Submitted` | ❌ silent | status badge flips |

- **Due reminder:** when `due_at - now <= 24h`, not submitted, and not past due → one toast. Dedup key = `due_at` (so it fires again if the deadline moves). No reminder after the deadline.
- Each kind can be toggled in settings.
- Disabled courses are not synced and not shown.

### DOCX (`DocxGenerator`)
- Fills a `.dotx`/`.docx` template that contains placeholders: `{{Title}}`, `{{Course}}`, `{{DueDate}}`, `{{Description}}`, `{{MoodleUrl}}` (extend as needed)
- Word splits placeholder text across several runs (pieces of formatted text), so merge the runs inside each paragraph before replacing
- Description: convert the intro HTML to plain paragraphs
- **No caching or storage of output.** Generate on demand, show a `FileSavePicker` (default: Downloads, filename `{Course} - {Assignment}.docx`), save the file, done
- Template path is a setting. **TODO:** the template comes from the main project (location unknown — search that repo for `.docx/.dotx`, `docxtpl`, `python-docx`, `unioffice`, `go-docx`, `go:embed`). If that project builds the document in code, make a placeholder `.docx` from one generated output instead of porting the code.

## Agent (`UniDashboard.Agent.exe`)
- Started by Task Scheduler every N minutes (default 30), plus at logon
- The task must be "Run only when user is logged on" (otherwise toasts don't show)
- Single instance via a named mutex; does one sync pass → evaluates notification rules → sends toasts → exits
- Toast click → activates the App with the assignment id (deep link to the detail pane)
- Logs to `%LOCALAPPDATA%\UniDashboard\logs\` (rolling, small)

## App (WinUI 3)
Lean, modern Fluent look: Mica backdrop, `NavigationView` with **Assignments** and **Settings**. The app works on its own; it does not need the agent to run.

**Assignments**
- "Sync now" button plus last-sync time
- Grouped list: Overdue (not submitted only) / Today / This week / Later / Submitted (collapsed). Filter: all / open / submitted
- Each row shows: title, course, relative due time ("in 2 days") with a colour accent as it gets close, ✅ if submitted, and a dot if `unseen_change`
- Detail pane: title, course, absolute + relative due date, status, grade, description (rendered), "📎 N attachments", **Open in Moodle**, **Generate .docx**, and an expandable **History** (snapshot list → DiffPlex side-by-side or inline diff)
- Opening an assignment clears `unseen_change`

**Settings**
- Moodle base URL; Login / Re-login (headed Playwright)
- Course list with include/exclude toggles
- Sync interval; enable/disable background agent (creates/removes the scheduled task)
- Reminder threshold (default 24h); per-kind notification toggles
- Template path

## Build order
1. Core: Auth + MoodleClient, plus a console smoke test that lists assignments
2. Storage + ChangeDetector (unit tests with fixture JSON for each change kind)
3. App: list + detail + Open in Moodle + Sync now
4. Agent + toasts + Task Scheduler registration
5. History/diff view
6. DocxGenerator + template
7. Settings page polish
