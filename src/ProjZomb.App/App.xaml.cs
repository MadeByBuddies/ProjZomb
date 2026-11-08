using Microsoft.Win32;
using System.Windows;

namespace ProjZomb.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppearanceService.Load();
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        base.OnStartup(e);
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows raises this event off the UI thread. Always re-evaluate accessibility,
        // including when a user overrides the normal light/dark preference.
        Dispatcher.BeginInvoke(() => AppearanceService.Apply());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        base.OnExit(e);
    }
}
