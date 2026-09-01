using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using UltraNovaCtl.Core;

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

            // Avalonia shows the main window itself, so the only place to stay out of
            // sight is the moment it opens.
            if (Program.StartHidden)
                _window.Opened += (_, _) => _window.Hide();

            // The window is a front end for something that keeps running: closing it
            // should put the program away, not stop the server mid-performance.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            BuildTray(desktop);
            WatchForSecondInstance();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A second process holds the mutex for a moment, pulses this event, and exits.
    /// Raise the existing window instead of opening another USB session.
    /// </summary>
    void WatchForSecondInstance()
    {
        var ev = Program.ShowSignal;
        if (ev == null) return;
        new Thread(() =>
        {
            while (true)
            {
                try { ev.WaitOne(); }
                catch { break; }
                Dispatcher.UIThread.Post(() => _window?.ShowFromTray());
            }
        })
        {
            IsBackground = true,
            Name = "single-instance",
        }.Start();
    }

    void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var show = new NativeMenuItem("Show window");
        show.Click += (_, _) => _window?.ShowFromTray();

        var reinit = new NativeMenuItem("Reinitialise MIDI");
        reinit.Click += (_, _) => _window?.ReinitialiseFromTray();

        var debug = new NativeMenuItem("Debug tools")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _window?.DebugToolsVisible == true,
        };
        debug.Click += (_, _) =>
        {
            if (_window == null) return;
            _window.DebugToolsVisible = debug.IsChecked;
        };

        var autostart = new NativeMenuItem("Start with Windows")
        {
            ToggleType = MenuItemToggleType.CheckBox,   // Avalonia 12 renamed this enum
            IsChecked = Startup.IsEnabled(),
            IsEnabled = Startup.IsSupported,
        };
        autostart.Click += (_, _) =>
        {
            // The menu item has already flipped itself; make the registry agree, and put
            // the tick back if it would not.
            bool want = autostart.IsChecked;
            if (Startup.Set(want, out string error))
            {
                _window?.Say(want
                    ? "will start with Windows, into the tray"
                    : "will no longer start with Windows");
            }
            else
            {
                autostart.IsChecked = !want;
                _window?.Say("could not change the startup entry: " + error);
            }
        };

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
        menu.Add(autostart);
        menu.Add(debug);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            ToolTipText = "UltraNovaCtl " + Program.AppVersion,
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
