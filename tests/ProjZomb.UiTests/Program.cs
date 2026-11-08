using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjZomb.Core;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using TextBox = System.Windows.Controls.TextBox;

internal static class Program
{
    // Runs on Windows only; no server is started, commands sent or profile files written.
    [STAThread]
    private static int Main()
    {
        var app = new ProjZomb.App.App();
        try
        {
            app.InitializeComponent();
            app.StartupUri = null;
            var window = new ProjZomb.App.MainWindow();
            app.MainWindow = window;
            window.Width = 940; window.Height = 650;
            window.Show();
            var navigation = (ListBox)window.FindName("Navigation");
            var grid = (DataGrid)window.FindName("SettingsGrid");
            grid.ItemsSource = new[] { new ConfigEntry { Key = "PublicName", Value = "Theme test", Category = "Identity" } };
            ((ListBox)window.FindName("PlayersList")).ItemsSource = new[] { "Test player" };
            ((ListBox)window.FindName("BackupsList")).ItemsSource = new[] { "Test backup.zip" };
            foreach (var theme in new[] { ApplicationTheme.Dark, ApplicationTheme.Light })
            {
                ApplicationThemeManager.Apply(theme, WindowBackdropType.None);
                Drain(window);
                foreach (string key in new[] { "DefaultUiButtonStyle", "DefaultTextBoxStyle", "DefaultPasswordBoxStyle", "DefaultComboBoxStyle", "DefaultDataGridStyle", "DefaultListBoxItemStyle", "NavigationItem", "PageHost" })
                    Require(window.TryFindResource(key) is Style, "Style resolves: " + key);
                var background = BrushColor(window, "ApplicationBackgroundBrush");
                foreach (string key in new[] { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush" })
                {
                    var color = Composite(BrushColor(window, key), background);
                    double ratio = Contrast(color, background);
                    Require(ratio >= 4.5, $"{theme} {key}: {ratio:F2}:1 text contrast");
                }
                for (int i = 0; i < navigation.Items.Count; i++)
                {
                    navigation.SelectedIndex = i; Drain(window);
                    var pages = (TabControl)window.FindName("Pages");
                    Require(pages.SelectedIndex == i && pages.SelectedContent is FrameworkElement, $"{theme}: navigation page {i} loads");
                }
                navigation.SelectedIndex = 2; Drain(window);
                grid.SelectedIndex = 0; grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[2]);
                grid.BeginEdit(); Drain(window); grid.CancelEdit();
                var editor = (TabControl)window.FindName("EditorTabs");
                editor.SelectedIndex = 1; Drain(window); editor.SelectedIndex = 0;
                navigation.SelectedIndex = 4; Drain(window);
                var access = (ComboBox)window.FindName("AccessLevel");
                access.IsDropDownOpen = true; Drain(window); access.IsDropDownOpen = false;
                var actions = (StackPanel)window.FindName("PlayerActions");
                actions.IsEnabled = false; Drain(window); actions.IsEnabled = true;
                Require(((TextBox)window.FindName("CommandInput")).Template is not null, "Command input template resolves");
                Require(((PasswordBox)window.FindName("SecretInput")).Template is not null, "Private input template resolves");
                Require(window.WindowBackdropType == WindowBackdropType.None && window.ExtendsContentIntoTitleBar, "Opaque custom title bar retained");
            }
            Console.WriteLine("Windows UI smoke checks passed. Manually inspect hover/focus, high contrast, DPI, snapping and tray behavior.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); }
    }
    private static void Drain(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
    private static Color BrushColor(FrameworkElement element, string key) =>
        (element.TryFindResource(key) as SolidColorBrush)?.Color ?? throw new InvalidOperationException("Missing brush: " + key);
    private static Color Composite(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        return Color.FromRgb((byte)Math.Round(fg.R * a + bg.R * (1 - a)), (byte)Math.Round(fg.G * a + bg.G * (1 - a)), (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
    }
    private static double Contrast(Color a, Color b)
    {
        double L(Color c)
        {
            double Linear(byte x) { double v = x / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        double x = L(a), y = L(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
