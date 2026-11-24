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
| `Auth/` | Single sign-on via Playwright (Chrome, falling back to Edge) to obtain a Moodle token; `TokenStore` keeps it DPAPI-encrypted and the u:account password in Credential Manager |
| `Api/` | `MoodleClient`: courses, assignments, submission status |
| `Uspace/`, `Ufind/` | Enrolments, sessions and exams from u:space; static course info from u:find |
| `Sync/` | `SyncService` (assignments), `ChangeDetector` (normalise, hash, classify changes), `NotificationRules` |
| `Univie/` | Course/session/exam models, `SessionDiffer` and `ExamDiffer` (moved, changed, cancelled…), `UnivieSyncService` |
| `Calendar/` | The unified calendar model shown by the app |
| `Planning/` | `ExamPlanner`: picks exam dates under clash/registration constraints |
| `Export/` | `ExportBuilder`, `IcsWriter` (stable UIDs), `SyncPlanner` (pure decision logic), `ImportedEventMatcher` |
| `Google/` | OAuth (loopback + PKCE), REST client, `GoogleSyncService` (preview/apply) |
| `Docx/` | Template filling for Word documents (merges split runs before replacing placeholders) |
| `Database.cs` | SQLite in WAL mode, short transactions; migrations by `CREATE TABLE IF NOT EXISTS` |
| `AgentScheduler.cs` | Registers the hidden Task Scheduler task (logon + every N minutes, only while logged on) |

## Design principles

- **Pure planning, thin I/O.** `SyncPlanner`, `ExamPlanner`, differs and `ChangeDetector` take data and return decisions; network and database code is a thin shell around them. That is what makes the 180+ tests fast and network-free (the Google client is replaced by a fake that mimics Google's behaviour).
- **Never destroy user data.** Google sync only touches tagged events, skips anything the user edited or deleted, and has guards against runaway deletions. See [Google Calendar setup](google-calendar-setup.md).
- **Stable identity.** Sessions, exams and calendar events are identified by derived stable keys, so moving a session updates the same calendar entry rather than creating a new one.
- **Crash-safe.** The Google sync persists its state after every single event; a crash leaves a consistent map.

## Security

- Moodle token and Google credentials: DPAPI (`CurrentUser`).
- u:account password: Windows Credential Manager only.
- No telemetry. Network access only to Moodle, u:space, u:find, and Google when enabled.
- The background agent never opens a browser; if a silent login fails it raises a "login required" toast instead.

## Tests

```powershell
dotnet test UniDashboard.Core.Tests
```

Covers change detection fixtures, session/exam differs, the exam planner, `.ics` output, the sync planner's safety properties, and the Google client against a fake HTTP layer.

## Demo data and screenshots

`UniDashboard.Smoke --seed-demo <folder>` fills a database with fake data. Run the app with `UNIDASHBOARD_DATA_DIR=<folder>` to see it. `docs/capture-screenshots.ps1` automates this to regenerate everything in `docs/screenshots/`.
