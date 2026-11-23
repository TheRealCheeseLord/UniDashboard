using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoodleDashboard.Core.Export;

namespace MoodleDashboard.Core.Google;

/// <summary>
/// Minimal Google Calendar v3 REST client. Every write is sendUpdates=none; patch/delete may carry If-Match.
/// Retries 429 / 5xx / rate-limit 403 with exponential backoff (honouring Retry-After); a 401 triggers one token refresh.
/// </summary>
public class GoogleCalendarClient(HttpClient http, Func<bool, CancellationToken, Task<string>> accessToken, Func<TimeSpan, CancellationToken, Task>? delay = null, int maxRetries = 5) : IGoogleCalendar
{
    const string Base = "https://www.googleapis.com/calendar/v3";
    const string EventFields = "id,iCalUID,summary,location,description,etag,updated,status,start,end,extendedProperties/private";
    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((t, ct) => Task.Delay(t, ct));

    // ---------- reads

    public async Task<List<GoogleCalendarInfo>> ListCalendarsAsync(CancellationToken ct = default)
    {
        var list = new List<GoogleCalendarInfo>();
        string? page = null;
        do
        {
            var url = $"{Base}/users/me/calendarList?minAccessRole=reader&maxResults=250&fields=items(id,summary,summaryOverride,accessRole,primary),nextPageToken" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var doc = await GetJsonAsync(url, ct);
            foreach (var c in doc.RootElement.GetProperty("items").EnumerateArray())
                list.Add(new(Str(c, "id") ?? "", Str(c, "summaryOverride") ?? Str(c, "summary") ?? "", Str(c, "accessRole") ?? "reader",
                    c.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True));
            page = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        } while (page is not null);
        return list;
    }

    public Task<List<RemoteEvent>> ListEventsAsync(string calendarId, string calendarName, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
        ListAsync(calendarId, calendarName,
            $"timeMin={Uri.EscapeDataString(timeMin.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}" +
            $"&timeMax={Uri.EscapeDataString(timeMax.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}&singleEvents=true", ct);

    public Task<List<RemoteEvent>> ListManagedEventsAsync(string calendarId, string calendarName, CancellationToken ct = default) =>
        ListAsync(calendarId, calendarName, "privateExtendedProperty=" + Uri.EscapeDataString($"{GoogleEventData.MarkerManaged}=1"), ct);

    async Task<List<RemoteEvent>> ListAsync(string calendarId, string calendarName, string query, CancellationToken ct)
    {
        var list = new List<RemoteEvent>();
        string? page = null;
        do
        {
            var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events?{query}&showDeleted=false&maxResults=2500&fields=items({EventFields}),nextPageToken" +
                      (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var doc = await GetJsonAsync(url, ct);
            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var e in items.EnumerateArray()) list.Add(ParseEvent(e, calendarId, calendarName));
            page = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        } while (page is not null);
        return list;
    }

    public async Task<RemoteEvent?> GetAsync(string calendarId, string calendarName, string eventId, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync($"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}?fields={EventFields}", ct);
            return ParseEvent(doc.RootElement, calendarId, calendarName);
        }
        catch (GoogleNotFoundException) { return null; }
    }

    // ---------- writes

    public async Task<RemoteEvent> InsertAsync(string calendarId, string calendarName, GoogleEventData e, CancellationToken ct = default)
    {
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events?sendUpdates=none&fields={EventFields}";
        using var doc = await SendJsonAsync(() => Json(HttpMethod.Post, url, Body(e, includeId: true)), ct);
        return ParseEvent(doc.RootElement, calendarId, calendarName);
    }

    public async Task<RemoteEvent> PatchAsync(string calendarId, string calendarName, string eventId, GoogleEventData e, string? ifMatchEtag, CancellationToken ct = default)
    {
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}?sendUpdates=none&fields={EventFields}";
        using var doc = await SendJsonAsync(() =>
        {
            var req = Json(new HttpMethod("PATCH"), url, Body(e, includeId: false));
            if (ifMatchEtag is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatchEtag);
            return req;
        }, ct);
        return ParseEvent(doc.RootElement, calendarId, calendarName);
    }

    public async Task DeleteAsync(string calendarId, string eventId, string? ifMatchEtag, CancellationToken ct = default)
    {
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}?sendUpdates=none";
        using var resp = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, url);
            if (ifMatchEtag is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatchEtag);
            return req;
        }, ct);
        await EnsureSuccessAsync(resp);
    }

    // ---------- JSON mapping

    static HttpRequestMessage Json(HttpMethod method, string url, JsonNode body) =>
        new(method, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>Request body for an event we manage (always marked, explicit Vienna time zone).</summary>
    public static JsonObject Body(GoogleEventData e, bool includeId)
    {
        var o = new JsonObject
        {
            ["summary"] = e.Summary,
            ["description"] = e.Description,
            ["location"] = e.Location ?? "",
            ["transparency"] = e.AllDay ? "transparent" : "opaque",
            ["extendedProperties"] = new JsonObject
            {
                ["private"] = new JsonObject
                {
                    [GoogleEventData.MarkerManaged] = "1", [GoogleEventData.MarkerKey] = e.ItemKey, [GoogleEventData.MarkerHash] = e.ItemHash,
                },
            },
        };
        if (includeId) o["id"] = e.Id;
        if (e.AllDay)
        {
            o["start"] = new JsonObject { ["date"] = ViennaTime.DateString(e.Start) };
            o["end"] = new JsonObject { ["date"] = ViennaTime.DateString(e.End) };
        }
        else
        {
            o["start"] = new JsonObject { ["dateTime"] = ViennaTime.DateTimeString(e.Start), ["timeZone"] = "Europe/Vienna" };
            o["end"] = new JsonObject { ["dateTime"] = ViennaTime.DateTimeString(e.End), ["timeZone"] = "Europe/Vienna" };
        }
        if (Uri.TryCreate(e.SourceUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            o["source"] = new JsonObject { ["title"] = "u:find", ["url"] = e.SourceUrl };
        return o;
    }

    public static RemoteEvent ParseEvent(JsonElement e, string calendarId, string calendarName)
    {
        (DateTimeOffset? Time, bool AllDay) Time(string name)
        {
            if (!e.TryGetProperty(name, out var t)) return (null, false);
            if (t.TryGetProperty("dateTime", out var dt) && DateTimeOffset.TryParse(dt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return (d, false);
            if (t.TryGetProperty("date", out var da) && da.GetString() is { } s) return (ViennaTime.FromDate(s), true);
            return (null, false);
        }
        var (start, allDay) = Time("start");
        var (end, _) = Time("end");

        string? key = null, hash = null;
        var managed = false;
        if (e.TryGetProperty("extendedProperties", out var ep) && ep.TryGetProperty("private", out var priv))
        {
            managed = Str(priv, GoogleEventData.MarkerManaged) == "1";
            key = Str(priv, GoogleEventData.MarkerKey);
            hash = Str(priv, GoogleEventData.MarkerHash);
        }
        return new(calendarId, calendarName, Str(e, "id") ?? "", Str(e, "iCalUID"), Str(e, "summary") ?? "", start, end, allDay,
            Str(e, "location"), Str(e, "description"), Str(e, "etag"), Str(e, "updated"), Str(e, "status") == "cancelled", managed, key, hash);
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------- transport

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct) => await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

    async Task<JsonDocument> SendJsonAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        using var resp = await SendAsync(make, ct);
        await EnsureSuccessAsync(resp);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var forced = false;
        for (var attempt = 0; ; attempt++)
        {
            var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(forced, ct));
            var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && !forced) { forced = true; resp.Dispose(); continue; }   // refresh the token once

            if (attempt < maxRetries && await IsRetryableAsync(resp))
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(32, Math.Pow(2, attempt)) + Random.Shared.NextDouble());
                resp.Dispose();
                await _delay(wait, ct);
                continue;
            }
            return resp;
        }
    }

    static async Task<bool> IsRetryableAsync(HttpResponseMessage r)
    {
        var code = (int)r.StatusCode;
        if (code == 429 || code >= 500) return true;
        if (code != 403) return false;
        await r.Content.LoadIntoBufferAsync();
        var body = await r.Content.ReadAsStringAsync();
        return body.Contains("rateLimitExceeded") || body.Contains("userRateLimitExceeded") || body.Contains("quotaExceeded");
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        string message = body, reason = "";
        try
        {
            using var d = JsonDocument.Parse(body);
            var err = d.RootElement.GetProperty("error");
            message = err.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
            if (err.TryGetProperty("errors", out var es) && es.GetArrayLength() > 0 && es[0].TryGetProperty("reason", out var r)) reason = r.GetString() ?? "";
        }
        catch (Exception) { /* not JSON */ }

        var code = (int)resp.StatusCode;
        throw code switch
        {
            404 or 410 => new GoogleNotFoundException(message),
            409 => new GoogleConflictException(message),
            412 => new GooglePreconditionException(message),
            _ => new GoogleApiException(code, $"Google Calendar API {code}: {message}", reason),
        };
    }
}
