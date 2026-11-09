using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ProjZomb.App;

internal static class AppearanceService
{
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "data", "appearance.json");
    public static string Preference { get; private set; } = "Dark";

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath)) Preference = JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsPath)) ?? "Dark";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Preference = "Dark"; }
        if (Preference is not ("Dark" or "Light" or "System")) Preference = "Dark";
        Apply();
    }

    public static void SetPreference(string preference)
    {
        if (preference is not ("Dark" or "Light" or "System")) throw new ArgumentException("Unknown appearance preference.");
        // Persist before changing the displayed preference so disk failures are visible.
        ProjZomb.Core.AtomicFile.Write(SettingsPath, JsonSerializer.Serialize(preference));
        Preference = preference;
        Apply();
    }

    public static void Apply()
    {
        if (SystemParameters.HighContrast)
        {
            ApplicationThemeManager.Apply(ApplicationTheme.HighContrast, WindowBackdropType.None);
            return;
        }
        if (Preference == "System")
        {
            ApplicationThemeManager.ApplySystemTheme();
            // Use the resolved library theme, but explicitly keep the window opaque.
            ApplicationThemeManager.Apply(ApplicationThemeManager.GetAppTheme(), WindowBackdropType.None);
            return;
        }
        ApplicationThemeManager.Apply(Preference == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.None);
    }
}
