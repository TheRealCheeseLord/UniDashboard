using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace MoodleLite.App;

/// <summary>Custom entry point: single instance; later launches (toast click) are redirected to the running window.</summary>
public static class Program
{
    [STAThread]
    static int Main()
    {
        ComWrappersSupport.InitializeComWrappers();

        var main = AppInstance.FindOrRegisterForKey("MoodleLite.Main");
        if (!main.IsCurrent)
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            main.RedirectActivationToAsync(args).AsTask().Wait();
            return 0;
        }

        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
