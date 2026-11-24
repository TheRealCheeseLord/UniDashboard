using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using UniDashboard.Core;
using UniDashboard.Core.Web;

namespace UniDashboard.App;

/// <summary>
/// Visible sign-in for the cases the scripted login cannot handle (first login, changed pages). A small window with an embedded
/// Edge (WebView2) that is locked to the university's https hosts. Uses its own browser profile under the app's data folder
/// (never the user's browser profile); when the target page has loaded, the cookies are handed to the HTTP session and the window closes.
/// </summary>
public sealed class WebView2Login(Func<DispatcherQueue?> ui) : IInteractiveLogin
{
    public const string RuntimeHint = "The Microsoft Edge WebView2 Runtime is not installed. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.";

    public Task<IReadOnlyList<StoredCookie>> RunAsync(Uri start, Regex done, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<StoredCookie>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = ui();
        if (queue is null || !queue.TryEnqueue(() =>
            {
                try { new LoginWindow(start, done, tcs, ct).Open(); }
                catch (Exception e) { tcs.TrySetException(e); }
            }))
            tcs.TrySetException(new LoginRequiredException("The login window could not be opened."));
        return tcs.Task;
    }

    sealed class LoginWindow : Window
    {
        const string AllowedHostSuffix = "univie.ac.at";
        readonly WebView2 _web = new();
        readonly Uri _start;
        readonly Regex _done;
        readonly TaskCompletionSource<IReadOnlyList<StoredCookie>> _tcs;

        public LoginWindow(Uri start, Regex done, TaskCompletionSource<IReadOnlyList<StoredCookie>> tcs, CancellationToken ct)
        {
            _start = start; _done = done; _tcs = tcs;
            Title = "UniDashboard — sign in to the university";
            Content = new Grid { Children = { _web } };
            AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 780));
            var icon = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
            Closed += (_, _) => _tcs.TrySetException(new LoginRequiredException("The login window was closed before the login finished."));
            ct.Register(() => DispatcherQueue.TryEnqueue(Close));
        }

        public async void Open()
        {
            Activate();
            try
            {
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Paths.Root, "webview2"), new CoreWebView2EnvironmentOptions());
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (Exception e)
            {
                // Most likely the WebView2 Runtime is missing (the projection reports that as a COM / file-not-found error).
                FileLog.Write("app", "WebView2 could not start: " + e);
                _tcs.TrySetException(new LoginRequiredException(RuntimeHint));
                Close();
                return;
            }

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // Locked to the university: nothing else can be navigated to, and no extra windows are opened.
            core.NavigationStarting += (_, e) => { if (!IsAllowed(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess && _done.IsMatch(core.Source)) await FinishAsync(core);
            };
            core.Navigate(_start.AbsoluteUri);
        }

        static bool IsAllowed(string uri) =>
            uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
            (u.IdnHost == AllowedHostSuffix || u.IdnHost.EndsWith("." + AllowedHostSuffix, StringComparison.OrdinalIgnoreCase));

        async Task FinishAsync(CoreWebView2 core)
        {
            try
            {
                var all = await core.CookieManager.GetCookiesAsync(null);
                var cookies = all
                    .Where(c => c.Domain.TrimStart('.').EndsWith(AllowedHostSuffix, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new StoredCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsHttpOnly,
                        c.IsSession ? null : DateTimeOffset.FromUnixTimeSeconds((long)c.Expires)))
                    .ToList();
                _tcs.TrySetResult(cookies);
            }
            catch (Exception e) { _tcs.TrySetException(e); }
            Close();
        }
    }
}
