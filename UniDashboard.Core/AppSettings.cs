using System.Text.Json;
using UniDashboard.Core.Planning;

namespace UniDashboard.Core;

/// <summary>Typed view over the settings table.</summary>
public class AppSettings(Database db)
{
    string Get(string k, string def) => db.GetSetting(k) ?? def;
    int GetInt(string k, int def) => int.TryParse(db.GetSetting(k), out var v) ? v : def;
    bool GetBool(string k, bool def) => db.GetSetting(k) is { } s ? s == "1" : def;
    void Set(string k, string v) => db.SetSetting(k, v);
    void SetBool(string k, bool v) => db.SetSetting(k, v ? "1" : "0");

    public string MoodleBaseUrl
    {
        get => Get("moodle_base_url", "https://moodle.univie.ac.at").TrimEnd('/');
        set => Set("moodle_base_url", value.Trim().TrimEnd('/'));
    }
    public int SyncIntervalMinutes { get => GetInt("sync_interval_min", 30); set => Set("sync_interval_min", value.ToString()); }
    public int ReminderHours { get => GetInt("reminder_hours", 24); set => Set("reminder_hours", value.ToString()); }
    public bool AgentEnabled { get => GetBool("agent_enabled", false); set => SetBool("agent_enabled", value); }
    public string TemplatePath { get => Get("template_path", Paths.DefaultTemplate); set => Set("template_path", value); }
    /// <summary>Folder the "Save as" dialog opens in when generating a .docx.</summary>
    public string DocxFolder { get => Get("docx_folder", @"C:\Users\baric\Documents\School\Uni"); set => Set("docx_folder", value.Trim()); }
    public string StudentName { get => Get("student_name", ""); set => Set("student_name", value); }
    public string Matrikelnummer { get => Get("matrikelnummer", ""); set => Set("matrikelnummer", value); }
    /// <summary>Hash of credentials the IdP rejected; headless login is not retried with them (avoids account lockout).</summary>
    public string RejectedCredentialHash { get => Get("rejected_cred_hash", ""); set => Set("rejected_cred_hash", value); }
    public bool ShowDemoEvents { get => GetBool("show_demo_events", false); set => SetBool("show_demo_events", value); }
    // Exam planner (persisted as JSON)
    public PlanStrategy PlannerStrategy
    {
        get => Enum.TryParse<PlanStrategy>(db.GetSetting("planner_strategy"), out var v) ? v : PlanStrategy.Spread;
        set => Set("planner_strategy", value.ToString());
    }
    public HashSet<string> PlannerExcluded
    {
        get => ReadJson<HashSet<string>>("planner_excluded") ?? [];
        set => Set("planner_excluded", JsonSerializer.Serialize(value));
    }
    public Dictionary<string, string> PlannerPins
    {
        get => ReadJson<Dictionary<string, string>>("planner_pins") ?? new();
        set => Set("planner_pins", JsonSerializer.Serialize(value));
    }
    public DateOnly? PlannerNotBefore
    {
        get => DateOnly.TryParse(db.GetSetting("planner_not_before"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        set => Set("planner_not_before", value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }
    public DateOnly? PlannerNotAfter
    {
        get => DateOnly.TryParse(db.GetSetting("planner_not_after"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        set => Set("planner_not_after", value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }
    /// <summary>Ideal gap between exams for the "spaced out" strategy; null = as large as possible.</summary>
    public int? PlannerTargetGap
    {
        get => int.TryParse(db.GetSetting("planner_target_gap"), out var v) && v > 0 ? v : null;
        set => Set("planner_target_gap", value?.ToString() ?? "");
    }
    T? ReadJson<T>(string key)
    {
        try { return db.GetSetting(key) is { Length: > 0 } s ? JsonSerializer.Deserialize<T>(s) : default; }
        catch (JsonException) { return default; }
    }

    // Calendar export
    public bool ExportSessions { get => GetBool("export_sessions", true); set => SetBool("export_sessions", value); }
    public bool ExportExams { get => GetBool("export_exams", true); set => SetBool("export_exams", value); }
    public bool ExportFutureOnly { get => GetBool("export_future_only", true); set => SetBool("export_future_only", value); }
    public int ExportAlarmMinutes { get => GetInt("export_alarm_min", 0); set => Set("export_alarm_min", value.ToString()); }
    public string ExportFolder { get => Get("export_folder", @"C:\Users\baric\Documents\School\Uni"); set => Set("export_folder", value.Trim()); }

    // Google Calendar
    public string GoogleTargetCalendarId { get => Get("google_target_cal", ""); set => Set("google_target_cal", value); }
    public string GoogleTargetCalendarName { get => Get("google_target_cal_name", ""); set => Set("google_target_cal_name", value); }
    public List<string> GoogleScanCalendarIds
    {
        get => ReadJson<List<string>>("google_scan_cals") ?? [];
        set => Set("google_scan_cals", JsonSerializer.Serialize(value));
    }
    public bool GoogleAutoSync { get => GetBool("google_auto_sync", false); set => SetBool("google_auto_sync", value); }
    public bool GoogleOverwriteEdits { get => GetBool("google_overwrite_edits", false); set => SetBool("google_overwrite_edits", value); }
    /// <summary>True after one successful manual apply (auto-sync can only be enabled then).</summary>
    public bool GoogleApplied { get => GetBool("google_applied", false); set => SetBool("google_applied", value); }

    public bool ShowExams { get => GetBool("show_exams", true); set => SetBool("show_exams", value); }
    public bool ShowMovedRemoved { get => GetBool("show_moved_removed", true); set => SetBool("show_moved_removed", value); }
    public bool NotifyCourse(string kind) => GetBool("notify_" + kind, Univie.CourseKinds.All.First(k => k.Kind == kind).Default);
    public void SetNotifyCourse(string kind, bool v) => SetBool("notify_" + kind, v);
    public long UserId { get => long.TryParse(db.GetSetting("user_id"), out var v) ? v : 0; set => Set("user_id", value.ToString()); }
    public DateTimeOffset? LastSync
    {
        get => long.TryParse(db.GetSetting("last_sync"), out var v) ? DateTimeOffset.FromUnixTimeSeconds(v) : null;
        set => Set("last_sync", (value ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString());
    }

    // Notification toggles
    public bool Notify(ChangeKind k) => GetBool("notify_" + k, k is ChangeKind.New or ChangeKind.DueDateChanged or ChangeKind.DescriptionChanged or ChangeKind.GradeOrFeedback);
    public void SetNotify(ChangeKind k, bool v) => SetBool("notify_" + k, v);
    public bool NotifyDueReminder { get => GetBool("notify_due_reminder", true); set => SetBool("notify_due_reminder", value); }
    public bool NotifyLoginRequired { get => GetBool("notify_login_required", true); set => SetBool("notify_login_required", value); }
}
