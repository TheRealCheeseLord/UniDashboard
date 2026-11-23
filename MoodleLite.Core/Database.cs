using Microsoft.Data.Sqlite;

namespace MoodleLite.Core;

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
