# Architecture

```
UniDashboard.slnx
├─ UniDashboard.Core         class library: all logic, no UI
├─ UniDashboard.App          WinUI 3 desktop app (unpackaged, self-contained)
├─ UniDashboard.Agent        headless console exe: one sync pass + toasts, then exit
├─ UniDashboard.Core.Tests   xUnit tests (no network)
└─ UniDashboard.Smoke        developer console tool (smoke tests, demo-data seeder)
```

The App and the Agent both reference Core and share one SQLite database. A named lock (`SyncLock`) ensures only one sync runs at a time, whichever process starts it.

## Data flow

```
 Moodle REST ──► MoodleClient ─┐
 u:space  ─────► UspaceClient ─┼─► *SyncService ──► ChangeDetector / SessionDiffer / ExamDiffer ──► SQLite
 u:find   ─────► UfindClient  ─┘                                   │
                                                                    ├─► NotificationRules ──► Windows toasts
                                                                    └─► ExportBuilder ──► IcsWriter (.ics)
                                                                                      └─► SyncPlanner ──► GoogleSyncService ──► Google Calendar API
```

## Core modules

| Folder | Responsibility |
|---|---|
| `Auth/` | `AuthService` obtains the Moodle token: saved web session, else scripted single sign-on with the stored u:account, else (app only) the sign-in window; `TokenStore` keeps the token DPAPI-encrypted and the u:account password in Credential Manager |
| `Web/` | The purpose-built HTTP layer that replaced a browser driver: `WebSession` (own cookie jar, https only, allow-listed hosts, bounded manual redirects, size-capped bodies), `HtmlForms` (tiny form/link scanner, no HTML-parser dependency), `UnivieLogin` (scripted Shibboleth / Keycloak sign-in), `IInteractiveLogin` (implemented by the app's WebView2 sign-in window) |
| `Api/` | `MoodleClient`: courses, assignments, submission status |
| `Uspace/`, `Ufind/` | Enrolments, sessions, exams and the study list from u:space; static course info and the curriculum tree (`CurriculumClient`, scraped from public u:find pages, versioned by `CurrentSchema`) from u:find |
| `Sync/` | `SyncService` (assignments), `ChangeDetector` (normalise, hash, classify changes), `NotificationRules` |
| `Univie/` | Course/session/exam models, `SessionDiffer` and `ExamDiffer` (moved, changed, cancelled…), `UnivieSyncService`, and `UnivieSyncService.Sections` (section exams) with `CurriculumMatcher` (picks the study's curriculum from u:space studies and the course chapter paths) |
| `Calendar/` | The unified calendar model shown by the app |
| `Planning/` | `ExamPlanner`: picks exam dates under clash/registration constraints |
| `Export/` | `ExportBuilder`, `IcsWriter` (stable UIDs), `SyncPlanner` (pure decision logic), `ImportedEventMatcher` |
| `Google/` | OAuth (loopback + PKCE), REST client, `GoogleSyncService` (preview/apply) |
| `Docx/` | Template filling for Word documents (merges split runs before replacing placeholders) |
| `Database.cs` | SQLite in WAL mode, short transactions; migrations by `CREATE TABLE IF NOT EXISTS` |
| `AgentScheduler.cs` | Registers the hidden Task Scheduler task (logon + every N minutes, only while logged on) |

## Section exams and the study plan

Exams of curriculum sections (STEOP I/II, modules) are stored as **synthetic enrolments** (`EnrolmentRecord.SectionKey`, `section:{semester}:{u:find path id}`) with their `ExamRecord`s in the normal tables, so calendar, planner, export, reminders and `ExamDiffer` work unchanged. `Database.GetEnrolments(includeSections: false)` hides them from course lists and sync bookkeeping. The u:find curriculum trees are cached in the `curriculum_trees` table (refreshed every 6 hours or when the parser version rises) and drive the Study plan page. The sync matches your u:space study to a curriculum by study code, version mark and start year.

## UI structure

Pages are code-light XAML plus code-built rows. `UniDashboard.App/Palette.cs` holds the colour language (one colour per kind of thing) and a helper for icon chips; pages must take colours from there. List/detail pages (Assignments, Courses, Study plan) use soft rows with hover and selection highlights; form pages (Settings, Export) use soft cards with an icon in each title.

## Design principles

- **Pure planning, thin I/O.** `SyncPlanner`, `ExamPlanner`, differs and `ChangeDetector` take data and return decisions; network and database code is a thin shell around them. That is what makes the 180+ tests fast and network-free (the Google client is replaced by a fake that mimics Google's behaviour).
- **Never destroy user data.** Google sync only touches tagged events, skips anything the user edited or deleted, and has guards against runaway deletions. See [Google Calendar setup](google-calendar-setup.md).
- **Stable identity.** Sessions, exams and calendar events are identified by derived stable keys, so moving a session updates the same calendar entry rather than creating a new one.
- **Crash-safe.** The Google sync persists its state after every single event; a crash leaves a consistent map.

## Security

- Moodle token and Google credentials: DPAPI (`CurrentUser`).
- u:account password: Windows Credential Manager only.
- No telemetry. Network access only to Moodle, u:space, u:find, and Google when enabled.
- No browser driver or Node runtime is shipped. Sign-in traffic goes only to https hosts under `univie.ac.at` (plus the configured Moodle host); every redirect hop is checked, and credentials are only ever posted to those hosts.
- The scripted login submits the credentials once; if the login form comes back it stops and remembers the rejected password (hash only), so an account can never be locked by retries.
- The manual sign-in window is an embedded WebView2 locked to the same hosts, with its own profile folder under the app's data folder (it has no access to your browser profile or cookies).
- The background agent never opens a window; if the silent login fails it raises a "login required" toast instead.

## Tests

```powershell
dotnet test UniDashboard.Core.Tests
```

Covers change detection fixtures, session/exam differs, the exam planner, `.ics` output, the sync planner's safety properties, and the Google client against a fake HTTP layer.

## Updates and releases

Releases are cut by pushing a version tag: GitHub Actions builds an Inno Setup installer (`installer/`) and attaches it, with its SHA-256, to a GitHub Release. The installed app asks the GitHub API for the latest release once a day (`Core/Update/UpdateService.cs`), shows a banner, and on request downloads, verifies and silently runs the installer, which upgrades in place and relaunches the app. Details in [releasing.md](releasing.md).

## Demo data and screenshots

`UniDashboard.Smoke --seed-demo <folder>` fills a database with fake data. Run the app with `UNIDASHBOARD_DATA_DIR=<folder>` to see it. `docs/capture-screenshots.ps1` automates this to regenerate everything in `docs/screenshots/`.
