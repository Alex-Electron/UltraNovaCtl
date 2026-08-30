using System;
using System.Runtime.InteropServices;

namespace UltraNovaCtl.Core;

/// <summary>
/// Sends MIDI to an existing WinMM output port.
///
/// This is the short road to a usable result: the Novation driver already publishes a
/// virtual port called "Automap MIDI", and anything written to its output shows up as an
/// input in a DAW. The proper solution is Windows MIDI Services creating our own port, but
/// that needs the .NET 10 SDK, and this works today with no dependencies at all.
/// </summary>
public sealed class MidiOut : IDisposable
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
    public string LastError { get; private set; } = "";

    /// <summary>Names of every MIDI output the system offers.</summary>
    public static List<string> PortNames()
    {
        var list = new List<string>();
        uint n = midiOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new MIDIOUTCAPS();
            if (midiOutGetDevCapsW(new IntPtr(i), ref caps, (uint)Marshal.SizeOf<MIDIOUTCAPS>()) == 0)
                list.Add(caps.szPname);
        }
        return list;
    }

    public static void ListPorts()
    {
        uint n = midiOutGetNumDevs();
        Console.WriteLine($"MIDI output ports ({n}):");
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

            // MMSYSERR_ALLOCATED (4) can linger for a moment after a process that held
            // the port dies without closing it - the system releases it slightly later.
            // A few short retries turn "will not open" into "opened".
            uint r = 0;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                r = midiOutOpen(out _handle, i, IntPtr.Zero, IntPtr.Zero, 0);
                if (r == 0) break;
                if (r != 4) break;                       // a real failure, not a stale hold
                _handle = IntPtr.Zero;
                System.Threading.Thread.Sleep(120);
            }
            if (r != 0)
            {
                LastError = r == 4
                    ? $"port '{caps.szPname}' is still held by another application"
                    : $"port '{caps.szPname}' would not open (code {r})";
                Console.WriteLine(LastError);
                _handle = IntPtr.Zero;
                return false;
            }
            PortName = caps.szPname;
            Console.WriteLine($"MIDI out: '{PortName}' (device {i})");
            return true;
        }
        Console.WriteLine($"no output port matching '{nameFragment}'");
        return false;
    }

    [DllImport("winmm.dll")]
    static extern uint midiOutLongMsg(IntPtr handle, IntPtr header, uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct MIDIHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public IntPtr lpNext;
        public IntPtr reserved;
        public uint dwOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] dwReserved;
    }

    [DllImport("winmm.dll")] static extern uint midiOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern uint midiOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);

    /// <summary>
    /// Send bytes as they are. Short messages go the fast way; anything starting with
    /// F0 is a SysEx and has to travel as a prepared buffer.
    /// </summary>
    public void SendRaw(byte[] data)
    {
        if (_handle == IntPtr.Zero || data == null || data.Length == 0) return;

        if (data[0] != 0xF0)
        {
            uint msg = data[0];
            if (data.Length > 1) msg |= (uint)data[1] << 8;
            if (data.Length > 2) msg |= (uint)data[2] << 16;
            midiOutShortMsg(_handle, msg);
            Sent++;
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(data.Length);
        IntPtr header = Marshal.AllocHGlobal(Marshal.SizeOf<MIDIHDR>());
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            var hdr = new MIDIHDR
            {
                lpData = buffer,
                dwBufferLength = (uint)data.Length,
                dwBytesRecorded = (uint)data.Length,
                dwReserved = new IntPtr[8],
            };
            Marshal.StructureToPtr(hdr, header, false);

            uint size = (uint)Marshal.SizeOf<MIDIHDR>();
            if (midiOutPrepareHeader(_handle, header, size) == 0)
            {
                midiOutLongMsg(_handle, header, size);
                midiOutUnprepareHeader(_handle, header, size);
                Sent++;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(buffer);
        }
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
