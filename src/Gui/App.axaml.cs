using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace UltraNovaCtl.Gui;

public partial class App : Application
{
    MainWindow _window;
    TrayIcon _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _window = new MainWindow();
            desktop.MainWindow = _window;

            // The window is a front end for something that keeps running: closing it
            // should put the program away, not stop the server mid-performance.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            BuildTray(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var show = new NativeMenuItem("Show window");
        show.Click += (_, _) => _window?.ShowFromTray();

        var reinit = new NativeMenuItem("Reinitialise MIDI");
        reinit.Click += (_, _) => _window?.ReinitialiseFromTray();

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            _tray?.Dispose();
            _window?.ShutdownEngine();
            desktop.Shutdown();
        };

        var menu = new NativeMenu();
        menu.Add(show);
        menu.Add(reinit);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            ToolTipText = "UltraNovaCtl",
            Menu = menu,
            IsVisible = true,
        };

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://UltraNovaCtl/icon.ico"));
            _tray.Icon = new WindowIcon(stream);
        }
        catch { /* the menu still works without a picture */ }

        // A plain click on the icon toggles the window, which is what people expect.
        _tray.Clicked += (_, _) => _window?.ToggleFromTray();
    }
}
