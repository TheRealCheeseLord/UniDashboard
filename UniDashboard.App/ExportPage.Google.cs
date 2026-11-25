using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UniDashboard.Core.Export;
using UniDashboard.Core.Google;

namespace UniDashboard.App;

public sealed partial class ExportPage
{
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");

    GooglePreview? _gPreview;
    List<GoogleCalendarInfo> _cals = [];
    readonly ProgressRing _ring = new() { Width = 20, Height = 20, IsActive = false };
    readonly TextBlock _gStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _gLast = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly TextBox _clientId = new() { Header = "Client ID", PlaceholderText = "xxxxxxxx.apps.googleusercontent.com" };
    readonly PasswordBox _clientSecret = new() { Header = "Client secret" };
    readonly Button _connect = new() { Content = "Connect to Google…", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _disconnect = new() { Content = "Disconnect" };
    readonly ComboBox _target = new() { Header = "Sync into this calendar", MinWidth = 320, PlaceholderText = "Connect first" };
    readonly StackPanel _scan = new() { Spacing = 2 };
    readonly Button _previewBtn = new() { Content = "Preview changes" };
    readonly Button _applyBtn = new() { Content = "Apply changes", IsEnabled = false };
    readonly Button _syncBtn = new() { Content = "Sync now" };
    readonly ToggleSwitch _auto = new() { Header = "Sync automatically in the background (needs the background agent)" };
    readonly ToggleSwitch _overwrite = new() { Header = "Overwrite events I edited in Google (off = never touch my edits)" };
    readonly StackPanel _review = new() { Spacing = 8 };
    readonly CheckBox _confirmMass = new() { Visibility = Visibility.Collapsed };

    readonly Border _statusPill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>A titled card with an optional hint line, matching the other pages.</summary>
    Border GCard(string title, string? hint, params UIElement[] content)
    {
        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { Palette.Chip("\uE787", Palette.Exam, 30, 16), new TextBlock { Text = title, Style = (Style)Resources["CardTitle"] } },
        });
        if (hint is not null) sp.Children.Add(new TextBlock { Text = hint, Style = (Style)Resources["Hint"] });
        foreach (var c in content) sp.Children.Add(c);
        return new Border { Style = (Style)Resources["CardStyle"], Child = sp };
    }

    void BuildGoogleSection()
    {
        GooglePanel.Children.Clear();
        _loading = true;

        // ---- setup guide (inside the connection card)
        var guide = new StackPanel { Spacing = 8, Padding = new Thickness(0, 6, 0, 6) };
        void Step(string n, string text, string? linkText = null, string? url = null)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Top,
                Background = Res("AccentFillColorDefaultBrush"),
                Child = new TextBlock { Text = n, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextOnAccentFillColorPrimaryBrush") },
            });
            var body = new StackPanel { Spacing = 2 };
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            if (url is not null)
            {
                var link = new HyperlinkButton { Content = linkText, Padding = new Thickness(0) };
                link.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
                body.Children.Add(link);
            }
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            guide.Children.Add(row);
        }
        Step("1", "Create a Google Cloud project (any name).", "Open Google Cloud", "https://console.cloud.google.com/projectcreate");
        Step("2", "Enable the “Google Calendar API” for that project.", "Open the API page", "https://console.cloud.google.com/apis/library/calendar-json.googleapis.com");
        Step("3", "Set up the OAuth consent screen (type “External”; an app name and your e-mail are enough), then press “Publish app” so it is “In production” — otherwise Google expires the login every 7 days. The “unverified app” warning at sign-in is normal for your own app (Advanced → continue).", "Open the consent screen", "https://console.cloud.google.com/apis/credentials/consent");
        Step("4", "Create credentials → OAuth client ID → application type “Desktop app”.", "Open credentials", "https://console.cloud.google.com/apis/credentials");
        Step("5", "Paste the Client ID and Client secret here and save.");
        var saveClient = new Button { Content = "Save client" };
        saveClient.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_clientId.Text) || string.IsNullOrWhiteSpace(_clientSecret.Password)) { Show("Enter both the client ID and the client secret.", InfoBarSeverity.Warning); return; }
            A.GoogleCreds.SaveClient(_clientId.Text, _clientSecret.Password);
            _clientSecret.Password = "";
            Show("Client saved. You can connect now.", InfoBarSeverity.Success);
            RefreshGoogleState();
        };
        guide.Children.Add(_clientId);
        guide.Children.Add(_clientSecret);
        guide.Children.Add(saveClient);
        var expander = new Expander
        {
            Header = "Setup guide (once, about 5 minutes)", Content = guide, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = !A.GoogleAuth.HasClient,
        };
        _clientId.Text = A.GoogleCreds.Load().ClientId;

        // ---- card 1: connection
        _gStatus.FontSize = 12;
        _statusPill.Child = _gStatus;
        var conn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        conn.Children.Add(_statusPill);
        conn.Children.Add(_connect);
        conn.Children.Add(_disconnect);
        conn.Children.Add(_ring);
        _connect.Click += async (_, _) => await ConnectAsync();
        _disconnect.Click += async (_, _) => { await A.GoogleAuth.DisconnectAsync(); _cals = []; Show("Disconnected from Google. Events already created in Google stay where they are.", InfoBarSeverity.Informational); RefreshGoogleState(); FillCalendars(); };
        GooglePanel.Children.Add(GCard("Connect your Google account",
            "The app signs in through your browser. Nothing is sent anywhere else; the access token is stored encrypted for your Windows account.", conn, expander));

        // ---- card 2: calendar
        var refresh = new Button { Content = "Refresh list", VerticalAlignment = VerticalAlignment.Bottom };
        refresh.Click += async (_, _) => await LoadCalendarsAsync();
        var tgt = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        tgt.Children.Add(_target);
        tgt.Children.Add(refresh);
        _target.SelectionChanged += (_, _) =>
        {
            if (_loading || _target.SelectedItem is not ComboBoxItem { Tag: GoogleCalendarInfo c }) return;
            A.Settings.GoogleTargetCalendarId = c.Id;
            A.Settings.GoogleTargetCalendarName = c.Name;
            ClearPreview();
        };
        var scanBox = new Expander
        {
            Header = "Look for events I imported myself (e.g. from u:find's iCal) in these calendars", Content = _scan,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        GooglePanel.Children.Add(GCard("Choose the calendar",
            "Only events this app creates (marked with a hidden tag) are ever changed there. Everything else in the calendar is left alone.", tgt, scanBox));

        // ---- card 3: sync
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_previewBtn);
        actions.Children.Add(_applyBtn);
        actions.Children.Add(_syncBtn);
        _previewBtn.Click += async (_, _) => await PreviewAsync();
        _applyBtn.Click += async (_, _) => await ApplyAsync();
        _syncBtn.Click += async (_, _) => await SyncNowAsync();
        _previewBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

        _auto.IsOn = A.Settings.GoogleAutoSync;
        _overwrite.IsOn = A.Settings.GoogleOverwriteEdits;
        _auto.Toggled += (_, _) => { if (!_loading) A.Settings.GoogleAutoSync = _auto.IsOn; };
        _overwrite.Toggled += (_, _) => { if (!_loading) { A.Settings.GoogleOverwriteEdits = _overwrite.IsOn; ClearPreview(); } };
        GooglePanel.Children.Add(GCard("Preview, then sync",
            "“Preview changes” shows exactly what would happen. Nothing is written until you apply it.", actions, _auto, _overwrite, _gLast));

        GooglePanel.Children.Add(_review);

        // ---- card 4: undo
        var removeAll = new Button { Content = "Remove everything this app created in this calendar…" };
        removeAll.Click += async (_, _) => await RemoveAllAsync();
        GooglePanel.Children.Add(GCard("Undo", "Deletes only the events carrying this app's hidden tag. Your own events and old imports are not touched; Google keeps deleted events in the trash for about 30 days.", removeAll));

        _loading = false;
        RefreshGoogleState();
        if (A.GoogleAuth.IsConnected) _ = LoadCalendarsAsync();
    }

    // ---------- state

    void RefreshGoogleState()
    {
        var connected = A.GoogleAuth.IsConnected;
        _gStatus.Text = connected ? "● Connected" : A.GoogleAuth.HasClient ? "Not connected" : "Set up the client first";
        _statusPill.Background = connected ? Res("SystemFillColorSuccessBackgroundBrush") : Res("SubtleFillColorSecondaryBrush");
        _connect.IsEnabled = A.GoogleAuth.HasClient && !connected;
        _disconnect.IsEnabled = connected;
        var ready = connected && !string.IsNullOrEmpty(A.Settings.GoogleTargetCalendarId);
        _previewBtn.IsEnabled = ready;
        _syncBtn.IsEnabled = ready && A.Settings.GoogleApplied;
        _auto.IsEnabled = ready && A.Settings.GoogleApplied;
        _auto.Header = A.Settings.GoogleApplied ? "Sync automatically in the background (needs the background agent)" : "Sync automatically in the background (available after your first Apply)";
        var last = A.Db.GetLastGoogleLog();
        _gLast.Text = last is { } l ? $"Last run {l.At.ToLocalTime():g}: {l.Summary}" : "No sync yet. Start with “Preview changes” — nothing is written until you apply.";
    }

    void ClearPreview()
    {
        _gPreview = null;
        _applyBtn.IsEnabled = false;
        _review.Children.Clear();
        _confirmMass.IsChecked = false;
        RefreshGoogleState();
    }

    void Busy(bool on)
    {
        _ring.IsActive = on;
        foreach (var b in new[] { _previewBtn, _applyBtn, _syncBtn, _connect, _disconnect }) if (on) b.IsEnabled = false;
        if (!on) { RefreshGoogleState(); _applyBtn.IsEnabled = _gPreview?.Plan.HasChanges == true; }
    }

    async Task ConnectAsync()
    {
        Busy(true);
        try
        {
            Show("Your browser opens for the Google sign-in…", InfoBarSeverity.Informational);
            await A.GoogleAuth.AuthorizeInteractiveAsync();
            Show("Connected to Google Calendar.", InfoBarSeverity.Success);
            await LoadCalendarsAsync();
        }
        catch (Exception ex) { Show("Could not connect: " + ex.Message, InfoBarSeverity.Error); }
        finally { Busy(false); }
    }

    async Task LoadCalendarsAsync()
    {
        try
        {
            _cals = await A.GoogleApi.ListCalendarsAsync();
            FillCalendars();
        }
        catch (GoogleAuthRequiredException ex) { Show(ex.Message, InfoBarSeverity.Warning); RefreshGoogleState(); }
        catch (Exception ex) { Show("Could not load your calendars: " + ex.Message, InfoBarSeverity.Error); }
    }

    void FillCalendars()
    {
        _loading = true;
        _target.Items.Clear();
        foreach (var c in _cals.Where(c => c.CanWrite).OrderByDescending(c => c.Primary).ThenBy(c => c.Name))
        {
            var item = new ComboBoxItem { Content = c.Name + (c.Primary ? "  (main)" : ""), Tag = c };
            _target.Items.Add(item);
            if (c.Id == A.Settings.GoogleTargetCalendarId) _target.SelectedItem = item;
        }

        _scan.Children.Clear();
        var scan = A.Settings.GoogleScanCalendarIds;
        foreach (var c in _cals.OrderByDescending(c => c.Primary).ThenBy(c => c.Name))
        {
            var cb = new CheckBox { Content = c.Name + (c.CanWrite ? "" : "  (read-only)"), IsChecked = scan.Contains(c.Id), MinWidth = 0 };
            var id = c.Id;
            cb.Click += (_, _) =>
            {
                var ids = A.Settings.GoogleScanCalendarIds;
                if (cb.IsChecked == true) { if (!ids.Contains(id)) ids.Add(id); } else ids.Remove(id);
                A.Settings.GoogleScanCalendarIds = ids;
                ClearPreview();
            };
            _scan.Children.Add(cb);
        }
        _loading = false;
        RefreshGoogleState();
    }

    // ---------- preview / apply

    async Task PreviewAsync()
    {
        Busy(true);
        try
        {
            _gPreview = await Task.Run(() => A.GoogleSync.PreviewAsync(sourceHealthy: true));
            ShowReview(_gPreview);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Fail(ex); _gPreview = null; }
        finally { Busy(false); }
    }

    async Task ApplyAsync()
    {
        if (_gPreview is null) return;
        var p = _gPreview;
        if (p.Plan.DeletesBlocked && _confirmMass.IsChecked != true) { Show("Tick the confirmation box for the many deletions first.", InfoBarSeverity.Warning); return; }
        var allowMass = _confirmMass.IsChecked == true;     // read UI state here, on the UI thread — never inside Task.Run
        Busy(true);
        try
        {
            var r = await Task.Run(() => A.GoogleSync.ApplyAsync(p, allowMass));
            ClearPreview();
            Show(r.Summary + (r.Errors.Count > 0 ? "\n" + string.Join("\n", r.Errors.Take(5)) : ""), r.Errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Fail(ex); }
        finally { Busy(false); }
    }

    async Task SyncNowAsync()
    {
        Busy(true);
        try
        {
            var r = await Task.Run(() => A.GoogleSync.SyncAsync(sourceHealthy: true));
            ClearPreview();
            Show(r.Summary + (r.Errors.Count > 0 ? "\n" + string.Join("\n", r.Errors.Take(5)) : ""), r.Errors.Count > 0 || r.DeletesBlocked ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            if (r.DeletesBlocked) await PreviewAsync();     // show what was held back
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Fail(ex); }
        finally { Busy(false); }
    }

    async Task RemoveAllAsync()
    {
        if (!A.GoogleAuth.IsConnected || string.IsNullOrEmpty(A.Settings.GoogleTargetCalendarId)) { Show("Connect and choose a calendar first.", InfoBarSeverity.Warning); return; }
        if (!await ConfirmAsync("Remove all events created by this app?",
                $"This deletes every event in “{A.Settings.GoogleTargetCalendarName}” that carries this app's hidden marker. Your own events and old imports are not touched. Google keeps deleted events in the trash for about 30 days.",
                "Remove them")) return;
        Busy(true);
        try
        {
            var r = await Task.Run(() => A.GoogleSync.RemoveAllManagedAsync());
            ClearPreview();
            Show(r.Summary, r.Errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Fail(ex); }
        finally { Busy(false); }
    }

    // ---------- review panel

    static string When(DateTimeOffset? t, bool allDay = false) => t is null ? "" : allDay ? t.Value.ToLocalTime().ToString("ddd d.M.yyyy", De) : t.Value.ToLocalTime().ToString("ddd d.M. HH:mm", De);

    void ShowReview(GooglePreview p)
    {
        _review.Children.Clear();
        _confirmMass.IsChecked = false;
        var plan = p.Plan;
        var skippedDup = plan.Count(SyncActionKind.SkipForeignDuplicate);
        var skippedEdit = plan.Count(SyncActionKind.SkipUserEdited) + plan.Count(SyncActionKind.KeepOrphanEdited);
        var skippedDel = plan.Count(SyncActionKind.SkipUserDeleted);

        _review.Children.Add(new TextBlock
        {
            FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Text = $"Preview: {plan.Count(SyncActionKind.Create)} to create · {plan.Count(SyncActionKind.Update)} to update · {plan.Count(SyncActionKind.Delete)} to delete" +
                   (skippedDup > 0 ? $" · {skippedDup} already in your calendar" : "") + (skippedEdit > 0 ? $" · {skippedEdit} edited by you (kept)" : "") +
                   (skippedDel > 0 ? $" · {skippedDel} deleted by you (not recreated)" : "") + $" · {plan.Count(SyncActionKind.Unchanged)} unchanged",
        });
        _review.Children.Add(new TextBlock { FontSize = 12, Opacity = 0.7, Text = $"Looked at {p.ScannedEvents} existing events in your selected calendars." });
        foreach (var n in plan.Notes.Concat(p.Warnings)) _review.Children.Add(new TextBlock { Text = "ℹ " + n, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        if (plan.DeletesBlocked)
        {
            _review.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Safety stop", Message = plan.GuardReason });
            _confirmMass.Content = $"Yes, also delete these {plan.Count(SyncActionKind.Delete)} events";
            _confirmMass.Visibility = Visibility.Visible;
            _review.Children.Add(_confirmMass);
        }
        else _confirmMass.Visibility = Visibility.Collapsed;

        void Section(string title, IEnumerable<SyncAction> actions, Func<SyncAction, string> line)
        {
            var list = actions.ToList();
            if (list.Count == 0) return;
            var sp = new StackPanel { Spacing = 2 };
            foreach (var a in list.Take(300)) sp.Children.Add(new TextBlock { Text = line(a), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (list.Count > 300) sp.Children.Add(new TextBlock { Text = $"… and {list.Count - 300} more", FontSize = 12, Opacity = 0.7 });
            _review.Children.Add(new Expander { Header = $"{title} ({list.Count})", Content = sp, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        string Item(SyncAction a) => $"{When(a.Item!.Start, a.Item.AllDay)}  {a.Item.Title}";
        Section("Will create", plan.Of(SyncActionKind.Create), Item);
        Section("Will update", plan.Of(SyncActionKind.Update), a => Item(a) + (a.Note == "moved" ? "   (moved)" : ""));
        Section("Will delete", plan.Of(SyncActionKind.Delete), a => $"{When(a.Remote?.Start, a.Remote?.AllDay ?? false)}  {a.Remote?.Summary}   — {a.Note}");
        Section("Skipped — already in your calendar", plan.Of(SyncActionKind.SkipForeignDuplicate), a => $"{Item(a)}   — {a.Note}");
        Section("Kept — you changed them in Google", plan.Of(SyncActionKind.SkipUserEdited).Concat(plan.Of(SyncActionKind.KeepOrphanEdited)), a => a.Item is not null ? Item(a) : $"{a.Remote?.Summary}   — {a.Note}");
        Section("Not recreated — you deleted them in Google", plan.Of(SyncActionKind.SkipUserDeleted), Item);

        if (p.Foreign.Count > 0) _review.Children.Add(ForeignList(p));

        _applyBtn.IsEnabled = plan.HasChanges;
        if (!plan.HasChanges) Show("Everything is already in sync.", InfoBarSeverity.Success);
    }

    FrameworkElement ForeignList(GooglePreview p)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Text = "These events look like earlier imports from u:find / u:space (same title pattern, start and place). They are not touched by the sync. If you would like to remove the old copies, tick them and delete — only after you confirm.",
        });

        var boxes = new List<(CheckBox Box, ForeignMatch Match)>();
        foreach (var m in p.Foreign.OrderBy(m => m.Remote.Start))
        {
            var cb = new CheckBox
            {
                IsChecked = false, MinWidth = 0,
                Content = $"{When(m.Remote.Start)}  {m.Remote.Summary}   [{m.Remote.CalendarName}]  {(m.Confidence == MatchConfidence.Strong ? "sure" : "probable")}",
            };
            boxes.Add((cb, m));
            panel.Children.Add(cb);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var selStrong = new Button { Content = "Select the sure ones" };
        selStrong.Click += (_, _) => { foreach (var (b, m) in boxes) b.IsChecked = m.Confidence == MatchConfidence.Strong && m.Remote.CalendarName is not null && _cals.FirstOrDefault(c => c.Id == m.Remote.CalendarId)?.CanWrite == true; };
        var selNone = new Button { Content = "Clear selection" };
        selNone.Click += (_, _) => { foreach (var (b, _) in boxes) b.IsChecked = false; };
        var delete = new Button { Content = "Delete selected from Google…" };
        delete.Click += async (_, _) =>
        {
            var chosen = boxes.Where(x => x.Box.IsChecked == true).Select(x => x.Match).ToList();
            if (chosen.Count == 0) { Show("Nothing selected.", InfoBarSeverity.Informational); return; }
            var text = string.Join("\n", chosen.Select(m => $"• {When(m.Remote.Start)}  {m.Remote.Summary}  [{m.Remote.CalendarName}]"));
            if (!await ConfirmAsync($"Delete {chosen.Count} event(s) from Google Calendar?",
                    text + "\n\nOnly these events are deleted, each one re-checked right before. Google keeps deleted events in the trash for about 30 days.", "Delete")) return;
            Busy(true);
            try
            {
                var r = await Task.Run(() => A.GoogleSync.DeleteForeignAsync(chosen, _cals));
                Show($"{r.Deleted} deleted, {r.AlreadyGone} already gone" + (r.Skipped.Count > 0 ? $", {r.Skipped.Count} skipped:\n" + string.Join("\n", r.Skipped.Take(5)) : "."),
                    r.Skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
                ClearPreview();
                await PreviewAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Fail(ex); }
            finally { Busy(false); }
        };
        row.Children.Add(selStrong); row.Children.Add(selNone); row.Children.Add(delete);
        panel.Children.Add(row);

        return new Expander
        {
            Header = $"Already in your calendars — old imports ({p.Foreign.Count})", Content = panel, IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    /// <summary>Shows an error in the banner and writes the full exception to the log (handlers are async void: nothing may escape).</summary>
    void Fail(Exception ex)
    {
        UniDashboard.Core.FileLog.Write("app", "export page: " + ex);
        Show(ex is GoogleAuthRequiredException ? ex.Message + " Press “Connect to Google…” again." : ex.Message, ex is GoogleAuthRequiredException ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        RefreshGoogleState();
    }

    async Task<bool> ConfirmAsync(string title, string text, string primary)
    {
        var dlg = new ContentDialog
        {
            Title = title, PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
            Content = new ScrollViewer { MaxHeight = 380, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }
}
