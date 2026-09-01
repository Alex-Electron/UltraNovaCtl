using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using UltraNovaCtl.Core;

namespace UltraNovaCtl.Gui;

internal static class Program
{
    const string MutexName = @"Local\UltraNovaCtl.single";
    const string ShowEventName = @"Local\UltraNovaCtl.show";

    static Mutex _mutex;
    static EventWaitHandle _show;

    /// <summary>
    /// Started by the Run key rather than by a person: come up in the tray with no window,
    /// because a synth control surface appearing over whatever you were doing at login is
    /// not a welcome.
    /// </summary>
    public static bool StartHidden { get; private set; }

    /// <summary>Show the panel-lamp walker and the rest of the hardware bench.</summary>
    public static bool StartWithDebugTools { get; private set; }

    /// <summary>The Version from Directory.Build.props, without a git suffix.</summary>
    public static string AppVersion
    {
        get
        {
            string raw = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0";
            int plus = raw.IndexOf('+');
            return plus >= 0 ? raw[..plus] : raw;
        }
    }

    /// <summary>
    /// Named event the second process pulses so the first can raise its window.
    /// Null when this process is not the owner (or named events are unavailable).
    /// </summary>
    public static EventWaitHandle ShowSignal => _show;

    /// <summary>
    /// Places the running GUI watches for a one-line lamp probe. A second process
    /// that cannot take the USB device writes here instead of opening it.
    /// </summary>
    public static IEnumerable<string> ProbeFilePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "probe.txt");
        yield return Path.Combine(@"C:\Yandex.Disk\DIY\UltraNova\UltraNovaCtl", "probe.txt");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UltraNovaCtl", "probe.txt");
    }

    [STAThread]
    public static void Main(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "--tray" or "-tray" or "/tray" or "--minimized") StartHidden = true;
            if (a is "--debug" or "-debug" or "/debug") StartWithDebugTools = true;
        }

        // Set the Run key and leave again, without starting a window. Handy for scripting,
        // and it is what an installer would call. Exit code 0 means it took. This is
        // before the mutex so an installer can flip the key while the GUI is running.
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is not ("--autostart" or "/autostart")) continue;
            bool on = args[i + 1] is "on" or "1" or "true" or "yes";
            Environment.Exit(Core.Startup.Set(on, out _) ? 0 : 1);
        }

        string probe = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is not ("--probe" or "-probe")) continue;
            probe = args[i + 1];
            break;
        }

        // One Automap host at a time: the USB interrupt pin and the WinMM ports
        // cannot be shared. A second copy either asks the first to come forward,
        // or (for --probe) drops a file the first already watches.
        if (!TakeSingleInstance())
        {
            if (probe != null) HandOffProbe(probe);
            else AskExistingToShow();
            return;
        }

        Shutdown.Register(ReleaseSingleInstance);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown.Run();
        Console.CancelKeyPress += (_, _) => Shutdown.Run();

        if (probe != null)
        {
            AllocConsole();
            Environment.Exit(RunProbeCli(probe));
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Shutdown.Run();
    }

    /// <summary>Cleanup that must happen once, however the program ends.</summary>
    public static class Shutdown
    {
        static readonly List<Action> Actions = new();
        static bool _done;

        public static void Register(Action a) { lock (Actions) Actions.Add(a); }

        public static void Run()
        {
            lock (Actions)
            {
                if (_done) return;
                _done = true;
                foreach (var a in Actions) { try { a(); } catch { } }
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [DllImport("kernel32.dll")] static extern bool AllocConsole();

    static bool TakeSingleInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous process died without releasing; this one now owns it.
        }
        catch
        {
            return true;
        }

        try
        {
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch { }

        return true;
    }

    static void AskExistingToShow()
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    static void HandOffProbe(string spec)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0) return;
        foreach (string path in ProbeFilePaths())
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, spec);
            }
            catch { }
        }
    }

    static void ReleaseSingleInstance()
    {
        try { _show?.Dispose(); } catch { }
        _show = null;
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    static int RunProbeCli(string spec)
    {
        var eng = new AutomapEngine();
        eng.Log += (_, s) => Console.WriteLine(s);
        if (!eng.Start())
        {
            Console.WriteLine("no synth");
            return 1;
        }
        Console.WriteLine("waiting for AUTOMAP…");
        for (int i = 0; i < 80 && !eng.AutomapActive; i++) Thread.Sleep(100);
        if (!eng.AutomapActive)
        {
            Console.WriteLine("not in AUTOMAP");
            eng.Stop();
            return 1;
        }
        eng.ProbeBlocking(spec);
        eng.Stop();
        return 0;
    }
}
