using Microsoft.Win32;

namespace UltraNovaCtl.Core;

/// <summary>
/// Starting with Windows, through the per-user Run key.
///
/// The registry is the state, not a copy of it: a value the user deleted by hand should
/// read back as off rather than as whatever we last saved. That also means nothing here
/// belongs in the configuration file.
///
/// Per-user (HKCU) on purpose - it needs no administrator, and a MIDI control surface is
/// a per-person thing anyway.
/// </summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "UltraNovaCtl";

    /// <summary>The launcher path, quoted, asking for a start straight into the tray.</summary>
    static string Command
    {
        get
        {
            string exe = Environment.ProcessPath ?? "";
            return $"\"{exe}\" --tray";
        }
    }

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>True when the Run key points at this copy of the program.</summary>
    public static bool IsEnabled()
    {
        if (!IsSupported) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Where the Run key currently points, or null. Worth showing when it points at a copy
    /// that has since been moved or deleted, which otherwise fails silently at every login.
    /// </summary>
    public static string RegisteredCommand()
    {
        if (!IsSupported) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    /// <summary>Returns false and leaves things alone if the registry refuses.</summary>
    public static bool Set(bool enabled, out string error)
    {
        error = null;
        if (!IsSupported) { error = "only Windows has a Run key"; return false; }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) { error = "could not open the Run key"; return false; }

            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// True when the entry exists but names a different file than the one running - the
    /// program was moved or reinstalled elsewhere, and the old entry is now dead.
    /// </summary>
    public static bool IsStale()
    {
        string registered = RegisteredCommand();
        if (string.IsNullOrEmpty(registered)) return false;
        return !string.Equals(registered, Command, StringComparison.OrdinalIgnoreCase);
    }
}
