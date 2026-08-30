using System;
using System.Runtime.InteropServices;

namespace KsMidiMon;

/// <summary>
/// Sends MIDI to an existing WinMM output port.
///
/// This is the short road to a usable result: the Novation driver already publishes a
/// virtual port called "Automap MIDI", and anything written to its output shows up as an
/// input in a DAW. The proper solution is Windows MIDI Services creating our own port, but
/// that needs the .NET 10 SDK, and this works today with no dependencies at all.
/// </summary>
internal sealed class MidiOut : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MIDIOUTCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public ushort wTechnology, wVoices, wNotes, wChannelMask;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")] static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern uint midiOutGetDevCapsW(IntPtr id, ref MIDIOUTCAPS caps, uint size);
    [DllImport("winmm.dll")]
    static extern uint midiOutOpen(out IntPtr handle, uint deviceId, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern uint midiOutShortMsg(IntPtr handle, uint msg);
    [DllImport("winmm.dll")] static extern uint midiOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiOutClose(IntPtr handle);

    IntPtr _handle = IntPtr.Zero;
    public string PortName { get; private set; } = "";
    public long Sent { get; private set; }

    public static void ListPorts()
    {
        uint n = midiOutGetNumDevs();
        Console.WriteLine($"выходные MIDI-порты ({n}):");
        for (uint i = 0; i < n; i++)
        {
            var caps = new MIDIOUTCAPS();
            if (midiOutGetDevCapsW(new IntPtr(i), ref caps, (uint)Marshal.SizeOf<MIDIOUTCAPS>()) == 0)
                Console.WriteLine($"  [{i}] {caps.szPname}");
        }
    }

    /// <summary>Open the first output port whose name contains the fragment.</summary>
    public bool Open(string nameFragment)
    {
        uint n = midiOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new MIDIOUTCAPS();
            if (midiOutGetDevCapsW(new IntPtr(i), ref caps, (uint)Marshal.SizeOf<MIDIOUTCAPS>()) != 0)
                continue;
            if (caps.szPname.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;

            uint r = midiOutOpen(out _handle, i, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0)
            {
                Console.WriteLine($"порт '{caps.szPname}' не открылся, код {r}");
                _handle = IntPtr.Zero;
                return false;
            }
            PortName = caps.szPname;
            Console.WriteLine($"выход MIDI: '{PortName}' (устройство {i})");
            return true;
        }
        Console.WriteLine($"выходной порт с именем '{nameFragment}' не найден");
        return false;
    }

    /// <summary>Status, data1, data2 packed the way midiOutShortMsg wants them.</summary>
    public void Send(byte status, byte d1, byte d2)
    {
        if (_handle == IntPtr.Zero) return;
        midiOutShortMsg(_handle, (uint)(status | (d1 << 8) | (d2 << 16)));
        Sent++;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        midiOutReset(_handle);
        midiOutClose(_handle);
        _handle = IntPtr.Zero;
    }
}
