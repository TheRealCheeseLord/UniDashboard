using System.Globalization;
using UniDashboard.Core.Export;

namespace UniDashboard.Core.Google;

public class GoogleApiException(int status, string message, string? reason = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Reason { get; } = reason;
}
public class GoogleConflictException(string message) : GoogleApiException(409, message);              // id already exists
public class GoogleNotFoundException(string message) : GoogleApiException(404, message);             // 404 / 410: gone
public class GooglePreconditionException(string message) : GoogleApiException(412, message);         // etag changed (If-Match)
public class GoogleAuthRequiredException(string message) : Exception(message);                       // reconnect needed

public record GoogleCalendarInfo(string Id, string Name, string AccessRole, bool Primary)
{
    public bool CanWrite => AccessRole is "owner" or "writer";
}

/// <summary>What we send to Google for one managed event.</summary>
public record GoogleEventData(string Id, string Summary, string? Location, string Description, DateTimeOffset Start, DateTimeOffset End, bool AllDay,
    string? SourceUrl, string ItemKey, string ItemHash)
{
    public const string MarkerManaged = "uni_dashboard_managed", MarkerKey = "uni_dashboard_key", MarkerHash = "uni_dashboard_hash";

    public static GoogleEventData From(ExportItem item) => new(ExportIds.GoogleEventId(item.Key), item.Title, item.Location, item.Description,
        item.Start, item.End, item.AllDay, item.Url, item.Key, item.Hash);
}

public interface IGoogleCalendar
{
    Task<List<GoogleCalendarInfo>> ListCalendarsAsync(CancellationToken ct = default);
    /// <summary>All events (recurring ones expanded) of a calendar whose time range touches [timeMin,timeMax].</summary>
    Task<List<RemoteEvent>> ListEventsAsync(string calendarId, string calendarName, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);
    /// <summary>Events carrying our marker (any time).</summary>
    Task<List<RemoteEvent>> ListManagedEventsAsync(string calendarId, string calendarName, CancellationToken ct = default);
    Task<RemoteEvent?> GetAsync(string calendarId, string calendarName, string eventId, CancellationToken ct = default);
    /// <exception cref="GoogleConflictException">The id already exists.</exception>
    Task<RemoteEvent> InsertAsync(string calendarId, string calendarName, GoogleEventData e, CancellationToken ct = default);
    /// <exception cref="GooglePreconditionException">The event changed since <paramref name="ifMatchEtag"/>.</exception>
    /// <exception cref="GoogleNotFoundException"/>
    Task<RemoteEvent> PatchAsync(string calendarId, string calendarName, string eventId, GoogleEventData e, string? ifMatchEtag, CancellationToken ct = default, bool restore = false);
    Task DeleteAsync(string calendarId, string eventId, string? ifMatchEtag, CancellationToken ct = default);
}

/// <summary>Europe/Vienna conversions (the university's time zone), independent of the machine's zone.</summary>
public static class ViennaTime
{
    static readonly TimeZoneInfo Zone = Find();
    static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }

    public static string DateTimeString(DateTimeOffset t)
    {
        var v = TimeZoneInfo.ConvertTime(t, Zone);
        return v.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    public static string DateString(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, Zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTimeOffset FromDate(string yyyyMmDd)
    {
        var d = DateTime.ParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateTimeOffset(d, Zone.GetUtcOffset(d));
    }
}
