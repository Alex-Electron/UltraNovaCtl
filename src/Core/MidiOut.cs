using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UltraNovaCtl.Core;

public sealed class MidiOutMessageEventArgs : EventArgs
{
    public string PortName { get; init; } = "";
    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    public string Hex => string.Join(" ", Bytes.Select(b => b.ToString("X2")));

    public string Description
    {
        get
        {
            if (Bytes.Length == 0) return "empty MIDI message";
            if (Bytes[0] == 0xF0) return MidiNames.SysEx(Bytes);
            return new MidiInEventArgs
            {
                Status = Bytes[0],
                Data1 = Bytes.Length > 1 ? Bytes[1] : (byte)0,
                Data2 = Bytes.Length > 2 ? Bytes[2] : (byte)0,
            }.Describe();
        }
    }
}

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
    const uint NoError = 0;
    const uint MidiStillPlaying = 65;
    const uint MidiHeaderDone = 0x00000001;
    const int LongMessageTimeoutMs = 5000;

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
    bool _failed;
    readonly object _sync = new();
    readonly List<LongAllocation> _deferred = new();
    public string PortName { get; private set; } = "";
    public long Sent { get; private set; }
    public string LastError { get; private set; } = "";
    public event EventHandler<MidiOutMessageEventArgs> MessageSent;

    /// <summary>
    /// True while WinMM still has a live handle and no send has failed on it. A virtual
    /// cable can invalidate an already-open handle when one of its clients restarts; the
    /// owner uses this signal to discard and reopen that handle automatically.
    /// </summary>
    public bool IsUsable
    {
        get { lock (_sync) return _handle != IntPtr.Zero && !_failed; }
    }

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
        lock (_sync)
        {
            // An open handle that has not failed is already the answer. One that HAS
            // failed is not: the port went away under us, and a caller asking to open
            // again is asking to recover from that. Returning true here left IsUsable
            // false for the life of the object, so the automatic reopen never took.
            if (_handle != IntPtr.Zero)
            {
                if (!_failed) return true;
                CloseHandleLocked();
            }
            LastError = "";
            _failed = false;
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

            LastError = $"no output port matching '{nameFragment}'";
            Console.WriteLine(LastError);
            return false;
        }
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

    sealed class LongAllocation
    {
        public IntPtr Header;
        public IntPtr Buffer;

        public void Free()
        {
            if (Header != IntPtr.Zero) Marshal.FreeHGlobal(Header);
            if (Buffer != IntPtr.Zero) Marshal.FreeHGlobal(Buffer);
            Header = Buffer = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Send bytes as they are. Short messages go the fast way; anything starting with
    /// F0 is a SysEx and has to travel as a prepared buffer.
    /// </summary>
    public bool SendRaw(byte[] data)
    {
        if (data == null || data.Length == 0) return false;
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return false;
            ReleaseDeferred();

            if (data[0] != 0xF0)
            {
                uint msg = data[0];
                if (data.Length > 1) msg |= (uint)data[1] << 8;
                if (data.Length > 2) msg |= (uint)data[2] << 16;
                uint result = midiOutShortMsg(_handle, msg);
                if (result != NoError)
                {
                    Fail($"short MIDI message failed (code {result})");
                    return false;
                }
                Sent++;
                PublishSent(ShortBytes(data[0],
                    data.Length > 1 ? data[1] : (byte)0,
                    data.Length > 2 ? data[2] : (byte)0));
                return true;
            }

            var allocation = new LongAllocation();
            bool prepared = false;
            bool safeToFree = false;
            try
            {
                // Allocate one object at a time inside the protected region: even an
                // out-of-memory exception while allocating the header cannot orphan the buffer.
                allocation.Buffer = Marshal.AllocHGlobal(data.Length);
                allocation.Header = Marshal.AllocHGlobal(Marshal.SizeOf<MIDIHDR>());
                Marshal.Copy(data, 0, allocation.Buffer, data.Length);
                var hdr = new MIDIHDR
                {
                    lpData = allocation.Buffer,
                    dwBufferLength = (uint)data.Length,
                    dwBytesRecorded = (uint)data.Length,
                    dwReserved = new IntPtr[8],
                };
                Marshal.StructureToPtr(hdr, allocation.Header, false);

                uint size = (uint)Marshal.SizeOf<MIDIHDR>();
                uint result = midiOutPrepareHeader(_handle, allocation.Header, size);
                if (result != NoError)
                {
                    Fail($"SysEx buffer preparation failed (code {result})");
                    safeToFree = true;
                    return false;
                }
                prepared = true;

                result = midiOutLongMsg(_handle, allocation.Header, size);
                if (result != NoError)
                {
                    Fail($"SysEx send failed (code {result})");
                    safeToFree = UnprepareWhenReady(allocation.Header, size, 250) == NoError;
                    return false;
                }

                // A WinMM driver may complete midiOutLongMsg asynchronously. The backing
                // header and bytes remain alive until MHDR_DONE and a successful unprepare.
                if (!WaitUntilDone(allocation.Header, LongMessageTimeoutMs))
                {
                    midiOutReset(_handle);
                    WaitUntilDone(allocation.Header, 1000);
                    // Reclaim the buffer if the driver has let go of it, but leave here:
                    // the message never went out, and falling through to Sent++ counted
                    // a timeout as a successful send, which is how a dead virtual port
                    // kept looking healthy to everything upstream.
                    uint pending = UnprepareWhenReady(allocation.Header, size, 1000);
                    safeToFree = pending == NoError;
                    Fail($"SysEx send timed out after {LongMessageTimeoutMs} ms; MIDI output was reset"
                        + (safeToFree ? "" : $"; buffer still owned by the driver (code {pending})"));
                    return false;
                }

                result = UnprepareWhenReady(allocation.Header, size, 1000);
                safeToFree = result == NoError;
                if (!safeToFree)
                {
                    Fail($"SysEx buffer is still owned by the MIDI driver (code {result})");
                    return false;
                }

                Sent++;
                PublishSent(data);
                return true;
            }
            finally
            {
                if (!prepared || safeToFree)
                    allocation.Free();
                else
                    _deferred.Add(allocation); // leak-safe: retry after reset/next send
            }
        }
    }

    static bool WaitUntilDone(IntPtr header, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if ((Marshal.PtrToStructure<MIDIHDR>(header).dwFlags & MidiHeaderDone) != 0)
                return true;
            Thread.Sleep(1);
        } while (timer.ElapsedMilliseconds < timeoutMs);
        return false;
    }

    uint UnprepareWhenReady(IntPtr header, uint size, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        uint result;
        do
        {
            result = midiOutUnprepareHeader(_handle, header, size);
            if (result != MidiStillPlaying) return result;
            Thread.Sleep(1);
        } while (timer.ElapsedMilliseconds < timeoutMs);
        return result;
    }

    void ReleaseDeferred()
    {
        if (_handle == IntPtr.Zero || _deferred.Count == 0) return;
        uint size = (uint)Marshal.SizeOf<MIDIHDR>();
        for (int i = _deferred.Count - 1; i >= 0; i--)
        {
            var allocation = _deferred[i];
            if (midiOutUnprepareHeader(_handle, allocation.Header, size) != NoError) continue;
            allocation.Free();
            _deferred.RemoveAt(i);
        }
    }

    /// <summary>
    /// Status, data1, data2 packed the way midiOutShortMsg wants them. Returns whether
    /// the message actually reached the driver - the note bookkeeping upstream needs to
    /// know, because a Note On that never went out must not be remembered as owing a
    /// release.
    /// </summary>
    public bool Send(byte status, byte d1, byte d2)
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return false;
            uint result = midiOutShortMsg(_handle, (uint)(status | (d1 << 8) | (d2 << 16)));
            if (result != NoError)
            {
                Fail($"short MIDI message failed (code {result})");
                return false;
            }
            Sent++;
            PublishSent(ShortBytes(status, d1, d2));
            return true;
        }
    }

    void Fail(string message)
    {
        LastError = message;
        _failed = true;
    }

    static byte[] ShortBytes(byte status, byte d1, byte d2)
    {
        if (status >= 0xF8 || status == 0xF6) return new[] { status };
        int kind = status & 0xF0;
        return kind is 0xC0 or 0xD0 ? new[] { status, d1 } : new[] { status, d1, d2 };
    }

    void PublishSent(byte[] bytes)
    {
        var handler = MessageSent;
        if (handler == null) return;
        try
        {
            handler(this, new MidiOutMessageEventArgs
            {
                PortName = PortName,
                Bytes = (byte[])bytes.Clone(),
            });
        }
        catch
        {
            // Diagnostic subscribers must never turn a successful MIDI write into a failure.
        }
    }

    /// <summary>
    /// Close the handle, giving the driver a moment to hand back any SysEx buffer it
    /// still owns. Caller holds <see cref="_sync"/>.
    /// </summary>
    void CloseHandleLocked()
    {
        if (_handle == IntPtr.Zero) return;
        midiOutReset(_handle);

        var timer = Stopwatch.StartNew();
        do
        {
            ReleaseDeferred();
            if (_deferred.Count == 0) break;
            Thread.Sleep(2);
        } while (timer.ElapsedMilliseconds < 1000);

        // If a broken driver still owns a buffer, retaining a few unmanaged bytes
        // until process exit is safer than freeing memory it may still touch. Drop
        // them from the list all the same: a later Open would otherwise try to
        // unprepare them against a different handle, which is not defined.
        _deferred.Clear();

        midiOutClose(_handle);
        _handle = IntPtr.Zero;
        PortName = "";
    }

    public void Dispose()
    {
        lock (_sync) CloseHandleLocked();
    }
}
