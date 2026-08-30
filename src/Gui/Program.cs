using Avalonia;

namespace UltraNovaCtl.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
