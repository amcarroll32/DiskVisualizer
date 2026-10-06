using System.Windows;
using DiskVisualizer.Platform;

namespace DiskVisualizer;

public partial class App : Application
{
    public App()
    {
        Settings = AppSettings.Load();
        ApplyTheme(Settings.PrefersDark);
    }

    public static AppSettings Settings { get; private set; } = new();

    /// <summary>Switches both the Fluent control theme and the app's own palette.</summary>
    public static void ApplyTheme(bool dark)
    {
        Treemap.Theme.Apply(dark);
        Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
    }
}
