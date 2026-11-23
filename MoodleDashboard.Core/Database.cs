using Microsoft.Data.Sqlite;
using MoodleDashboard.Core.Univie;

namespace MoodleDashboard.Core;

/// <summary>SQLite access; every operation opens a short-lived connection (WAL, so App and Agent coexist).</summary>
public class Database
{
    readonly string _cs;

    public Database(string? path = null)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = path ?? Paths.Db, Pooling = false }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
        CREATE TABLE IF NOT EXISTS courses(id INTEGER PRIMARY KEY, name TEXT, shortname TEXT, enabled INTEGER DEFAULT 1);
        CREATE TABLE IF NOT EXISTS assignments(id INTEGER PRIMARY KEY, course_id INTEGER, cmid INTEGER, name TEXT, intro_html TEXT,
            due_at INTEGER, attachment_count INTEGER, attachments_json TEXT, submitted INTEGER, grade TEXT, feedback TEXT,
            content_hash TEXT, last_seen_at INTEGER, unseen_change INTEGER DEFAULT 0);
        CREATE TABLE IF NOT EXISTS assignment_snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT, assignment_id INTEGER, taken_at INTEGER,
            hash TEXT, json TEXT, change_kinds INTEGER);
        CREATE TABLE IF NOT EXISTS notifications_sent(assignment_id INTEGER, kind TEXT, key TEXT, sent_at INTEGER,
            PRIMARY KEY(assignment_id, kind, key));
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS enrolments(key TEXT PRIMARY KEY, lvnr TEXT, group_nr TEXT, semester TEXT, title TEXT, type TEXT, status TEXT,
            immanent INTEGER, study_name TEXT, moodle_url TEXT, moodle_course_id INTEGER, lecturers_json TEXT, waitlist_pos INTEGER,
            ufind_link TEXT, remark TEXT, ufind_version TEXT, ufind_json TEXT, ufind_fetched_at INTEGER, content_hash TEXT,
            last_seen_at INTEGER, active INTEGER DEFAULT 1, unseen_change INTEGER DEFAULT 0);
        CREATE TABLE IF NOT EXISTS univie_sessions(session_key TEXT PRIMARY KEY, enrolment_key TEXT, begin_at INTEGER, end_at INTEGER, room TEXT,
            hash TEXT, status INTEGER, change INTEGER, first_seen_at INTEGER, last_changed_at INTEGER, prev_begin INTEGER, prev_end INTEGER, prev_room TEXT);
        CREATE INDEX IF NOT EXISTS idx_univie_sessions_enr ON univie_sessions(enrolment_key);
        CREATE TABLE IF NOT EXISTS exams(exam_key TEXT PRIMARY KEY, enrolment_key TEXT, ext_id TEXT, title TEXT, begin_at INTEGER, end_at INTEGER,
            time_known INTEGER, when_text TEXT, room TEXT, enroll_from INTEGER, enroll_to INTEGER, disenroll_until INTEGER, registered INTEGER,
            hash TEXT, status INTEGER, change INTEGER, first_seen_at INTEGER, last_changed_at INTEGER, prev_begin INTEGER, prev_enroll_from INTEGER, prev_enroll_to INTEGER);
        CREATE INDEX IF NOT EXISTS idx_exams_enr ON exams(enrolment_key);
        CREATE TABLE IF NOT EXISTS exam_registrations_raw(shape TEXT PRIMARY KEY, first_seen_at INTEGER, sample_json TEXT);
        CREATE TABLE IF NOT EXISTS enrolment_snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT, enrolment_key TEXT, taken_at INTEGER, hash TEXT, json TEXT, change_kinds TEXT);
        CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY, title TEXT, start_at INTEGER, end_at INTEGER, all_day INTEGER,
            location TEXT, description TEXT, url TEXT, source TEXT, course_name TEXT);
        """);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    public static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---- settings
    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));
    }

    // ---- courses
    public void UpsertCourse(Course course)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO courses(id,name,shortname,enabled) VALUES($i,$n,$s,1)
            ON CONFLICT(id) DO UPDATE SET name=$n, shortname=$s
            """, ("$i", course.Id), ("$n", course.Name), ("$s", course.ShortName));
    }

    public void SetCourseEnabled(long id, bool enabled)
    {
        using var c = Open();
        Exec(c, "UPDATE courses SET enabled=$e WHERE id=$i", ("$e", enabled ? 1 : 0), ("$i", id));
    }

    public List<Course> GetCourses(bool enabledOnly = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,shortname,enabled FROM courses" + (enabledOnly ? " WHERE enabled=1" : "") + " ORDER BY name";
        using var r = cmd.ExecuteReader();
        var l = new List<Course>();
        while (r.Read()) l.Add(new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt32(3) != 0));
        return l;
    }

    // ---- assignments
    const string AssignCols = "a.id,a.course_id,a.cmid,a.name,a.intro_html,a.due_at,a.attachment_count,a.attachments_json,a.submitted,a.grade,a.feedback,a.content_hash,a.last_seen_at,a.unseen_change,c.name";

    static Assignment Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), CourseId = r.GetInt64(1), Cmid = r.GetInt64(2), Name = r.GetString(3),
        IntroHtml = r.IsDBNull(4) ? "" : r.GetString(4),
        DueAt = r.IsDBNull(5) || r.GetInt64(5) == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)),
        AttachmentCount = r.GetInt32(6), AttachmentsJson = r.IsDBNull(7) ? "[]" : r.GetString(7),
        Submitted = r.GetInt32(8) != 0,
        Grade = r.IsDBNull(9) ? null : r.GetString(9), Feedback = r.IsDBNull(10) ? null : r.GetString(10),
        ContentHash = r.IsDBNull(11) ? "" : r.GetString(11),
        LastSeenAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(12)),
        UnseenChange = r.GetInt32(13) != 0,
        CourseName = r.IsDBNull(14) ? "" : r.GetString(14),
    };

    public Assignment? GetAssignment(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {AssignCols} FROM assignments a LEFT JOIN courses c ON c.id=a.course_id WHERE a.id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>All assignments of enabled courses.</summary>
    public List<Assignment> GetAssignments()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {AssignCols} FROM assignments a JOIN courses c ON c.id=a.course_id WHERE c.enabled=1";
        using var r = cmd.ExecuteReader();
        var l = new List<Assignment>();
        while (r.Read()) l.Add(Read(r));
        return l;
    }

    /// <summary>Saves the new state and, if kinds != None, a snapshot, in one short transaction.</summary>
    public void SaveAssignment(Assignment a, ChangeKind kinds, string snapshotJson)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, """
            INSERT INTO assignments(id,course_id,cmid,name,intro_html,due_at,attachment_count,attachments_json,submitted,grade,feedback,content_hash,last_seen_at,unseen_change)
            VALUES($id,$co,$cm,$n,$in,$d,$ac,$aj,$s,$g,$f,$h,$ls,$u)
            ON CONFLICT(id) DO UPDATE SET course_id=$co,cmid=$cm,name=$n,intro_html=$in,due_at=$d,attachment_count=$ac,attachments_json=$aj,
              submitted=$s,grade=$g,feedback=$f,content_hash=$h,last_seen_at=$ls,unseen_change=$u
            """,
            ("$id", a.Id), ("$co", a.CourseId), ("$cm", a.Cmid), ("$n", a.Name), ("$in", a.IntroHtml),
            ("$d", a.DueAt?.ToUnixTimeSeconds() ?? 0), ("$ac", a.AttachmentCount), ("$aj", a.AttachmentsJson),
            ("$s", a.Submitted ? 1 : 0), ("$g", a.Grade), ("$f", a.Feedback), ("$h", a.ContentHash),
            ("$ls", a.LastSeenAt.ToUnixTimeSeconds()), ("$u", a.UnseenChange ? 1 : 0));
        if (kinds != ChangeKind.None)
            Exec(c, "INSERT INTO assignment_snapshots(assignment_id,taken_at,hash,json,change_kinds) VALUES($a,$t,$h,$j,$k)",
                ("$a", a.Id), ("$t", a.LastSeenAt.ToUnixTimeSeconds()), ("$h", a.ContentHash), ("$j", snapshotJson), ("$k", (int)kinds));
        tx.Commit();
    }

    public void TouchAssignment(long id, DateTimeOffset now)
    {
        using var c = Open();
        Exec(c, "UPDATE assignments SET last_seen_at=$t WHERE id=$i", ("$t", now.ToUnixTimeSeconds()), ("$i", id));
    }

    public void ClearUnseen(long id)
    {
        using var c = Open();
        Exec(c, "UPDATE assignments SET unseen_change=0 WHERE id=$i", ("$i", id));
    }

    public List<Snapshot> GetSnapshots(long assignmentId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,assignment_id,taken_at,hash,json,change_kinds FROM assignment_snapshots WHERE assignment_id=$a ORDER BY id DESC";
        cmd.Parameters.AddWithValue("$a", assignmentId);
        using var r = cmd.ExecuteReader();
        var l = new List<Snapshot>();
        while (r.Read())
            l.Add(new(r.GetInt64(0), r.GetInt64(1), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)), r.GetString(3), r.GetString(4), (ChangeKind)r.GetInt32(5)));
        return l;
    }

    // ---- calendar events (read by the UI; written by a future fetcher)
    public List<CalendarEvent> GetEvents(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,title,start_at,end_at,all_day,location,description,url,source,course_name FROM events WHERE start_at < $to AND end_at >= $from ORDER BY start_at";
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var l = new List<CalendarEvent>();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            l.Add(new(r.GetString(0), r.GetString(1), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)), r.GetInt32(4) != 0, S(5), S(6), S(7), S(8) ?? "", S(9)));
        return l;
    }

    public void UpsertEvents(IEnumerable<CalendarEvent> events)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var e in events)
            Exec(c, """
                INSERT INTO events(id,title,start_at,end_at,all_day,location,description,url,source,course_name)
                VALUES($id,$t,$s,$e,$a,$l,$d,$u,$src,$cn)
                ON CONFLICT(id) DO UPDATE SET title=$t,start_at=$s,end_at=$e,all_day=$a,location=$l,description=$d,url=$u,source=$src,course_name=$cn
                """,
                ("$id", e.Id), ("$t", e.Title), ("$s", e.Start.ToUnixTimeSeconds()), ("$e", e.End.ToUnixTimeSeconds()),
                ("$a", e.AllDay ? 1 : 0), ("$l", e.Location), ("$d", e.Description), ("$u", e.Url), ("$src", e.Source), ("$cn", e.CourseName));
        tx.Commit();
    }

    // ---- univie enrolments / sessions
    const string EnrCols = "key,lvnr,group_nr,semester,title,type,status,immanent,study_name,moodle_url,moodle_course_id,lecturers_json,waitlist_pos,ufind_link,remark,ufind_version,ufind_json,ufind_fetched_at,content_hash,last_seen_at,active,unseen_change";

    static EnrolmentRecord ReadEnr(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new()
        {
            Key = r.GetString(0), LvNr = S(1) ?? "", GroupNr = S(2) ?? "", Semester = S(3) ?? "", Title = S(4) ?? "", Type = S(5) ?? "", Status = S(6) ?? "",
            Immanent = r.GetInt32(7) != 0, StudyName = S(8), MoodleUrl = S(9), MoodleCourseId = r.IsDBNull(10) ? null : r.GetInt64(10),
            LecturersJson = S(11) ?? "[]", WaitlistPosition = r.IsDBNull(12) ? null : r.GetInt32(12), UfindLink = S(13), Remark = S(14),
            UfindVersion = S(15), UfindJson = S(16) ?? "", UfindFetchedAt = r.IsDBNull(17) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(17)),
            ContentHash = S(18) ?? "", LastSeenAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(19)), Active = r.GetInt32(20) != 0, UnseenChange = r.GetInt32(21) != 0,
        };
    }

    public List<EnrolmentRecord> GetEnrolments(bool activeOnly = true)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {EnrCols} FROM enrolments" + (activeOnly ? " WHERE active=1" : "") + " ORDER BY semester DESC, title";
        using var r = cmd.ExecuteReader();
        var l = new List<EnrolmentRecord>();
        while (r.Read()) l.Add(ReadEnr(r));
        return l;
    }

    public EnrolmentRecord? GetEnrolment(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {EnrCols} FROM enrolments WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEnr(r) : null;
    }

    /// <summary>Saves enrolment + its sessions (+ optional snapshot) in one short transaction.</summary>
    public void SaveEnrolment(EnrolmentRecord e, IReadOnlyList<SessionRecord>? sessions, string? snapshotJson, string? changeKinds, IReadOnlyList<ExamRecord>? exams = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, $"""
            INSERT INTO enrolments({EnrCols}) VALUES($key,$lv,$gn,$sem,$t,$ty,$st,$im,$sn,$mu,$mi,$lj,$wp,$ul,$rm,$uv,$uj,$uf,$h,$ls,$a,$u)
            ON CONFLICT(key) DO UPDATE SET lvnr=$lv,group_nr=$gn,semester=$sem,title=$t,type=$ty,status=$st,immanent=$im,study_name=$sn,moodle_url=$mu,
              moodle_course_id=$mi,lecturers_json=$lj,waitlist_pos=$wp,ufind_link=$ul,remark=$rm,ufind_version=$uv,ufind_json=$uj,ufind_fetched_at=$uf,
              content_hash=$h,last_seen_at=$ls,active=$a,unseen_change=$u
            """,
            ("$key", e.Key), ("$lv", e.LvNr), ("$gn", e.GroupNr), ("$sem", e.Semester), ("$t", e.Title), ("$ty", e.Type), ("$st", e.Status),
            ("$im", e.Immanent ? 1 : 0), ("$sn", e.StudyName), ("$mu", e.MoodleUrl), ("$mi", e.MoodleCourseId), ("$lj", e.LecturersJson),
            ("$wp", e.WaitlistPosition), ("$ul", e.UfindLink), ("$rm", e.Remark), ("$uv", e.UfindVersion), ("$uj", e.UfindJson),
            ("$uf", e.UfindFetchedAt?.ToUnixTimeSeconds()), ("$h", e.ContentHash), ("$ls", e.LastSeenAt.ToUnixTimeSeconds()),
            ("$a", e.Active ? 1 : 0), ("$u", e.UnseenChange ? 1 : 0));
        if (sessions is not null)
        {
            Exec(c, "DELETE FROM univie_sessions WHERE enrolment_key=$k", ("$k", e.Key));
            foreach (var s in sessions)
                Exec(c, """
                    INSERT OR REPLACE INTO univie_sessions(session_key,enrolment_key,begin_at,end_at,room,hash,status,change,first_seen_at,last_changed_at,prev_begin,prev_end,prev_room)
                    VALUES($k,$e,$b,$en,$r,$h,$st,$ch,$fs,$lc,$pb,$pe,$pr)
                    """,
                    ("$k", s.Key), ("$e", s.EnrolmentKey), ("$b", s.Begin.ToUnixTimeSeconds()), ("$en", s.End.ToUnixTimeSeconds()), ("$r", s.Room),
                    ("$h", s.Hash), ("$st", (int)s.Status), ("$ch", (int)s.Change), ("$fs", s.FirstSeenAt.ToUnixTimeSeconds()),
                    ("$lc", s.LastChangedAt.ToUnixTimeSeconds()), ("$pb", s.PrevBegin?.ToUnixTimeSeconds()), ("$pe", s.PrevEnd?.ToUnixTimeSeconds()), ("$pr", s.PrevRoom));
        }
        if (exams is not null)
        {
            Exec(c, "DELETE FROM exams WHERE enrolment_key=$k", ("$k", e.Key));
            foreach (var x in exams)
                Exec(c, """
                    INSERT OR REPLACE INTO exams(exam_key,enrolment_key,ext_id,title,begin_at,end_at,time_known,when_text,room,enroll_from,enroll_to,disenroll_until,registered,
                        hash,status,change,first_seen_at,last_changed_at,prev_begin,prev_enroll_from,prev_enroll_to)
                    VALUES($k,$e,$x,$t,$b,$en,$tk,$w,$r,$ef,$et,$du,$rg,$h,$st,$ch,$fs,$lc,$pb,$pf,$pt)
                    """,
                    ("$k", x.Key), ("$e", x.EnrolmentKey), ("$x", x.ExtId), ("$t", x.Title), ("$b", x.Begin.ToUnixTimeSeconds()), ("$en", x.End.ToUnixTimeSeconds()),
                    ("$tk", x.TimeKnown ? 1 : 0), ("$w", x.When), ("$r", x.Room), ("$ef", x.EnrollFrom?.ToUnixTimeSeconds()), ("$et", x.EnrollTo?.ToUnixTimeSeconds()),
                    ("$du", x.DisenrollUntil?.ToUnixTimeSeconds()), ("$rg", x.Registered ? 1 : 0), ("$h", x.Hash), ("$st", (int)x.Status), ("$ch", (int)x.Change),
                    ("$fs", x.FirstSeenAt.ToUnixTimeSeconds()), ("$lc", x.LastChangedAt.ToUnixTimeSeconds()), ("$pb", x.PrevBegin?.ToUnixTimeSeconds()),
                    ("$pf", x.PrevEnrollFrom?.ToUnixTimeSeconds()), ("$pt", x.PrevEnrollTo?.ToUnixTimeSeconds()));
        }
        if (snapshotJson is not null)
            Exec(c, "INSERT INTO enrolment_snapshots(enrolment_key,taken_at,hash,json,change_kinds) VALUES($k,$t,$h,$j,$ck)",
                ("$k", e.Key), ("$t", e.LastSeenAt.ToUnixTimeSeconds()), ("$h", e.ContentHash), ("$j", snapshotJson), ("$ck", changeKinds ?? ""));
        tx.Commit();
    }

    public void SetEnrolmentActive(string key, bool active)
    {
        using var c = Open();
        Exec(c, "UPDATE enrolments SET active=$a WHERE key=$k", ("$a", active ? 1 : 0), ("$k", key));
    }

    public void ClearEnrolmentUnseen(string key)
    {
        using var c = Open();
        Exec(c, "UPDATE enrolments SET unseen_change=0 WHERE key=$k", ("$k", key));
    }

    static SessionRecord ReadSession(SqliteDataReader r) => new()
    {
        Key = r.GetString(0), EnrolmentKey = r.GetString(1), Begin = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)),
        End = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)), Room = r.IsDBNull(4) ? null : r.GetString(4), Hash = r.GetString(5),
        Status = (SessionStatus)r.GetInt32(6), Change = (SessionChange)r.GetInt32(7),
        FirstSeenAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)), LastChangedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(9)),
        PrevBegin = r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(10)),
        PrevEnd = r.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(11)),
        PrevRoom = r.IsDBNull(12) ? null : r.GetString(12),
    };

    const string SessCols = "session_key,enrolment_key,begin_at,end_at,room,hash,status,change,first_seen_at,last_changed_at,prev_begin,prev_end,prev_room";

    public List<SessionRecord> GetSessions(string enrolmentKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {SessCols} FROM univie_sessions WHERE enrolment_key=$k ORDER BY begin_at";
        cmd.Parameters.AddWithValue("$k", enrolmentKey);
        using var r = cmd.ExecuteReader();
        var l = new List<SessionRecord>();
        while (r.Read()) l.Add(ReadSession(r));
        return l;
    }

    /// <summary>Sessions of active enrolments touching [from,to), by their current or previous (moved-from) slot.</summary>
    public List<(SessionRecord Session, EnrolmentRecord Enrolment)> GetSessionsBetween(DateTimeOffset from, DateTimeOffset to)
    {
        var enr = GetEnrolments().ToDictionary(e => e.Key);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SessCols} FROM univie_sessions
            WHERE (begin_at < $to AND end_at >= $from) OR (prev_begin IS NOT NULL AND prev_begin < $to AND prev_end >= $from)
            ORDER BY begin_at
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var l = new List<(SessionRecord, EnrolmentRecord)>();
        while (r.Read())
        {
            var s = ReadSession(r);
            if (enr.TryGetValue(s.EnrolmentKey, out var e)) l.Add((s, e));
        }
        return l;
    }

    const string ExamCols = "exam_key,enrolment_key,ext_id,title,begin_at,end_at,time_known,when_text,room,enroll_from,enroll_to,disenroll_until,registered,hash,status,change,first_seen_at,last_changed_at,prev_begin,prev_enroll_from,prev_enroll_to";

    static ExamRecord ReadExam(SqliteDataReader r)
    {
        DateTimeOffset? D(int i) => r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(i));
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new()
        {
            Key = r.GetString(0), EnrolmentKey = r.GetString(1), ExtId = S(2) ?? "", Title = S(3) ?? "", Begin = D(4)!.Value, End = D(5)!.Value,
            TimeKnown = r.GetInt32(6) != 0, When = S(7), Room = S(8), EnrollFrom = D(9), EnrollTo = D(10), DisenrollUntil = D(11),
            Registered = r.GetInt32(12) != 0, Hash = S(13) ?? "", Status = (ExamStatus)r.GetInt32(14), Change = (ExamChange)r.GetInt32(15),
            FirstSeenAt = D(16)!.Value, LastChangedAt = D(17)!.Value, PrevBegin = D(18), PrevEnrollFrom = D(19), PrevEnrollTo = D(20),
        };
    }

    public List<ExamRecord> GetExams(string enrolmentKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ExamCols} FROM exams WHERE enrolment_key=$k ORDER BY begin_at";
        cmd.Parameters.AddWithValue("$k", enrolmentKey);
        using var r = cmd.ExecuteReader();
        var l = new List<ExamRecord>();
        while (r.Read()) l.Add(ReadExam(r));
        return l;
    }

    /// <summary>Exams of active enrolments whose date or registration window touches [from,to).</summary>
    public List<(ExamRecord Exam, EnrolmentRecord Enrolment)> GetExamsBetween(DateTimeOffset from, DateTimeOffset to)
    {
        var enr = GetEnrolments().ToDictionary(e => e.Key);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ExamCols} FROM exams
            WHERE (begin_at < $to AND end_at >= $from) OR (enroll_from IS NOT NULL AND enroll_from < $to AND enroll_to >= $from)
            ORDER BY begin_at
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var l = new List<(ExamRecord, EnrolmentRecord)>();
        while (r.Read())
        {
            var x = ReadExam(r);
            if (enr.TryGetValue(x.EnrolmentKey, out var e)) l.Add((x, e));
        }
        return l;
    }

    /// <summary>All exams of all active enrolments (for reminders).</summary>
    public List<(ExamRecord Exam, EnrolmentRecord Enrolment)> GetAllExams() =>
        GetExamsBetween(DateTimeOffset.FromUnixTimeSeconds(0), DateTimeOffset.FromUnixTimeSeconds(4102444800));

    /// <summary>Remembers the shape of a u:space exam item (once per distinct shape) so the parser can be corrected.</summary>
    public bool RememberExamShape(string shape, string sampleJson, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO exam_registrations_raw(shape,first_seen_at,sample_json) VALUES($s,$t,$j)";
        cmd.Parameters.AddWithValue("$s", shape);
        cmd.Parameters.AddWithValue("$t", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$j", sampleJson);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<Snapshot> GetEnrolmentSnapshots(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,taken_at,hash,json,change_kinds FROM enrolment_snapshots WHERE enrolment_key=$k ORDER BY id DESC";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        var l = new List<Snapshot>();
        while (r.Read())
            l.Add(new(r.GetInt64(0), 0, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)), r.GetString(2), r.GetString(3), ChangeKind.None, r.IsDBNull(4) ? "" : r.GetString(4)));
        return l;
    }

    // ---- notifications
    public bool WasSent(long assignmentId, string kind, string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM notifications_sent WHERE assignment_id=$a AND kind=$k AND key=$key";
        cmd.Parameters.AddWithValue("$a", assignmentId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() != null;
    }

    public void MarkSent(long assignmentId, string kind, string key, DateTimeOffset now)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO notifications_sent(assignment_id,kind,key,sent_at) VALUES($a,$k,$key,$t)",
            ("$a", assignmentId), ("$k", kind), ("$key", key), ("$t", now.ToUnixTimeSeconds()));
    }
}
