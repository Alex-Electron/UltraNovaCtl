using Avalonia;

namespace UltraNovaCtl.Gui;

internal static class Program
{
    /// <summary>
    /// Started by the Run key rather than by a person: come up in the tray with no window,
    /// because a synth control surface appearing over whatever you were doing at login is
    /// not a welcome.
    /// </summary>
    public static bool StartHidden { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        foreach (var a in args)
            if (a is "--tray" or "-tray" or "/tray" or "--minimized") StartHidden = true;

        // Set the Run key and leave again, without starting a window. Handy for scripting,
        // and it is what an installer would call. Exit code 0 means it took.
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is not ("--autostart" or "/autostart")) continue;
            bool on = args[i + 1] is "on" or "1" or "true" or "yes";
            Environment.Exit(Core.Startup.Set(on, out _) ? 0 : 1);
        }

        // Closing the window is the tidy path, but a process can end other ways. MIDI
        // ports left open by a dead process stay unavailable for a while, which looks
        // to the user like the DAW simply stopped receiving.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown.Run();
        Console.CancelKeyPress += (_, _) => Shutdown.Run();

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
}
