using System.Runtime.InteropServices;

namespace UltraNovaCtl.Core;

public sealed class MidiInEventArgs : EventArgs
{
    public byte Status;
    public byte Data1;
    public byte Data2;

    public int Channel => (Status & 0x0F) + 1;
    public int Kind => Status & 0xF0;
    public byte[] SysEx;

    public bool IsCc => Kind == 0xB0;
    public bool IsNote => Kind is 0x90 or 0x80;
    public bool IsPitchBend => Kind == 0xE0;
    public bool IsSysEx => SysEx != null && SysEx.Length > 0;

    /// <summary>Value as a DAW would show it; pitch bend collapses its two bytes.</summary>
    public int Value => IsPitchBend ? (Data2 << 7 | Data1) : Data2;

    public string Describe()
    {
        if (IsSysEx) return MidiNames.SysEx(SysEx);
        if (Status >= 0xF8 || Status is 0xF6) return MidiNames.Realtime(Status);
        return Kind switch
        {
            0xB0 => $"{MidiNames.CcShort(Data1)} · ch {Channel} · {Data2}",
            0x90 => $"{MidiNames.NoteShort(Data1)} · ch {Channel} · vel {Data2}",
            0x80 => $"{MidiNames.NoteShort(Data1)} off · ch {Channel}",
            0xE0 => $"Pitch Bend · ch {Channel} · {Value - 8192:+#;-#;0}",
            0xD0 => $"Aftertouch · ch {Channel} · {Data1}",
            0xA0 => $"Poly AT {MidiNames.NoteShort(Data1)} · ch {Channel} · {Data2}",
            0xC0 => $"Program {Data1} · ch {Channel}",
            _ => $"{Status:X2} {Data1:X2} {Data2:X2}",
        };
    }
}

/// <summary>
/// Listens to a WinMM MIDI input. This is how Learn discovers what a foreign device
/// sends: point it at the interface the other synth is plugged into, move a control
/// there, and the controller number arrives here.
///
/// WinMM inputs are exclusive, so holding one keeps a DAW off that same port. The
/// caller is expected to open it only while listening.
/// </summary>
public sealed class MidiIn : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MIDIINCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwSupport;
    }

    delegate void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr instance, IntPtr p1, IntPtr p2);

    const uint MIM_DATA = 0x3C3;
    const uint MIM_LONGDATA = 0x3C4;
    const uint CALLBACK_FUNCTION = 0x30000;
    const int SysexBufSize = 4096;
    const int SysexBufCount = 2;

    [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern uint midiInGetDevCapsW(IntPtr id, ref MIDIINCAPS caps, uint size);
    [DllImport("winmm.dll")]
    static extern uint midiInOpen(out IntPtr handle, uint deviceId, MidiInProc proc, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint midiInStart(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiInStop(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiInReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiInClose(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiInPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern uint midiInUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern uint midiInAddBuffer(IntPtr h, IntPtr hdr, uint size);

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

    IntPtr _handle = IntPtr.Zero;
    MidiInProc _proc;                 // kept alive: the callback outlives the open call
    IntPtr[] _sxData;
    IntPtr[] _sxHdr;

    public string PortName { get; private set; } = "";
    public bool IsOpen => _handle != IntPtr.Zero;

    public event EventHandler<MidiInEventArgs> Received;

    public static List<string> PortNames()
    {
        var list = new List<string>();
        uint n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new MIDIINCAPS();
            if (midiInGetDevCapsW(new IntPtr(i), ref caps, (uint)Marshal.SizeOf<MIDIINCAPS>()) == 0)
                list.Add(caps.szPname);
        }
        return list;
    }

    /// <summary>Open the first input whose name contains the fragment.</summary>
    public bool Open(string nameFragment, out string error)
    {
        Close();
        error = null;
        uint n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new MIDIINCAPS();
            if (midiInGetDevCapsW(new IntPtr(i), ref caps, (uint)Marshal.SizeOf<MIDIINCAPS>()) != 0)
                continue;
            if (caps.szPname.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;

            _proc = Callback;
            uint r = midiInOpen(out _handle, i, _proc, IntPtr.Zero, CALLBACK_FUNCTION);
            if (r != 0)
            {
                _handle = IntPtr.Zero;
                error = r == 4
                    ? $"port '{caps.szPname}' is held by another application"
                    : $"port '{caps.szPname}' would not open (code {r})";
                return false;
            }
            PrepareSysexBuffers();
            midiInStart(_handle);
            PortName = caps.szPname;
            return true;
        }
        error = $"no input port matching '{nameFragment}'";
        return false;
    }

    void PrepareSysexBuffers()
    {
        ReleaseSysexBuffers();
        _sxData = new IntPtr[SysexBufCount];
        _sxHdr = new IntPtr[SysexBufCount];
        uint size = (uint)Marshal.SizeOf<MIDIHDR>();
        for (int i = 0; i < SysexBufCount; i++)
        {
            _sxData[i] = Marshal.AllocHGlobal(SysexBufSize);
            _sxHdr[i] = Marshal.AllocHGlobal((int)size);
            var hdr = new MIDIHDR
            {
                lpData = _sxData[i],
                dwBufferLength = SysexBufSize,
                dwReserved = new IntPtr[8],
            };
            Marshal.StructureToPtr(hdr, _sxHdr[i], false);
            if (midiInPrepareHeader(_handle, _sxHdr[i], size) == 0)
                midiInAddBuffer(_handle, _sxHdr[i], size);
        }
    }

    void ReleaseSysexBuffers()
    {
        if (_sxHdr == null) return;
        uint size = (uint)Marshal.SizeOf<MIDIHDR>();
        for (int i = 0; i < _sxHdr.Length; i++)
        {
            if (_sxHdr[i] != IntPtr.Zero)
            {
                if (_handle != IntPtr.Zero) midiInUnprepareHeader(_handle, _sxHdr[i], size);
                Marshal.FreeHGlobal(_sxHdr[i]);
            }
            if (_sxData != null && _sxData[i] != IntPtr.Zero) Marshal.FreeHGlobal(_sxData[i]);
        }
        _sxHdr = null;
        _sxData = null;
    }

    void Callback(IntPtr h, uint msg, IntPtr instance, IntPtr p1, IntPtr p2)
    {
        if (msg == MIM_LONGDATA)
        {
            if (p1 == IntPtr.Zero) return;
            var hdr = Marshal.PtrToStructure<MIDIHDR>(p1);
            int n = (int)hdr.dwBytesRecorded;
            if (n > 0 && hdr.lpData != IntPtr.Zero)
            {
                var data = new byte[n];
                Marshal.Copy(hdr.lpData, data, 0, n);
                Received?.Invoke(this, new MidiInEventArgs { Status = 0xF0, SysEx = data });
            }
            if (_handle != IntPtr.Zero)
                midiInAddBuffer(_handle, p1, (uint)Marshal.SizeOf<MIDIHDR>());
            return;
        }

        if (msg != MIM_DATA) return;
        int packed = p1.ToInt32();
        byte status = (byte)(packed & 0xFF);
        if (status < 0x80) return;
        // SysEx is MIM_LONGDATA. Real-time (clock, start/stop) arrives here as one byte.
        if (status is >= 0xF0 and < 0xF8 && status != 0xF6) return;
        Received?.Invoke(this, new MidiInEventArgs
        {
            Status = status,
            Data1 = (byte)((packed >> 8) & 0x7F),
            Data2 = (byte)((packed >> 16) & 0x7F),
        });
    }

    public void Close()
    {
        if (_handle == IntPtr.Zero) return;
        midiInStop(_handle);
        midiInReset(_handle);
        ReleaseSysexBuffers();
        midiInClose(_handle);
        _handle = IntPtr.Zero;
        PortName = "";
    }

    public void Dispose() => Close();
}
