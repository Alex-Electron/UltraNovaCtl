using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KsMidiMon;

internal static class Program
{
    // ---- SetupAPI, to find the device paths ---------------------------------

    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devInfo,
        ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize,
        out uint requiredSize, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    static List<string> EnumerateAudioFilters()
    {
        var paths = new List<string>();
        Guid category = Ks.KSCATEGORY_AUDIO;
        IntPtr set = SetupDiGetClassDevsW(ref category, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return paths;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA();
            data.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref category, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on x64 (4 + 2 chars padding)
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        if (!string.IsNullOrEmpty(path)) paths.Add(path);
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
                data.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    // ---- monitoring ---------------------------------------------------------

    static void Monitor(string path, uint pinId, int seconds)
    {
        Console.WriteLine($"filter : {path}");
        Console.WriteLine($"pin    : {pinId}");

        IntPtr filter = Ks.CreateFileW(path,
            Ks.GENERIC_READ | Ks.GENERIC_WRITE,
            Ks.FILE_SHARE_READ | Ks.FILE_SHARE_WRITE,
            IntPtr.Zero, Ks.OPEN_EXISTING, 0, IntPtr.Zero);
        if (filter == new IntPtr(-1))
        {
            Console.WriteLine($"CreateFile on filter failed, error {Marshal.GetLastWin32Error()}");
            return;
        }
        Console.WriteLine("filter opened");

        IntPtr pin = Ks.CreateMidiPin(filter, pinId, forWriting: false, out uint status);
        if (pin == IntPtr.Zero)
        {
            Console.WriteLine($"KsCreatePin failed, NTSTATUS 0x{status:X8}, win32 {Marshal.GetLastWin32Error()}");
            Ks.CloseHandle(filter);
            return;
        }
        Console.WriteLine("pin created");

        foreach (uint state in new[] { Ks.KSSTATE_ACQUIRE, Ks.KSSTATE_PAUSE, Ks.KSSTATE_RUN })
        {
            if (!Ks.SetPinState(pin, state, out int err))
                Console.WriteLine($"state {state} failed, error {err}");
        }
        Console.WriteLine("pin running, reading. Ctrl+C to stop.\n");

        const int bufSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        int headerSize = Marshal.SizeOf<Ks.KSSTREAM_HEADER>();
        IntPtr header = Marshal.AllocHGlobal(headerSize);

        DateTime end = DateTime.Now.AddSeconds(seconds);
        try
        {
            while (DateTime.Now < end)
            {
                var hdr = new Ks.KSSTREAM_HEADER
                {
                    Size = (uint)headerSize,
                    FrameExtent = bufSize,
                    DataUsed = 0,
                    Data = buffer
                };
                Marshal.StructureToPtr(hdr, header, false);

                bool ok = Ks.DeviceIoControl(pin, Ks.IOCTL_KS_READ_STREAM,
                    IntPtr.Zero, 0, header, (uint)headerSize, out _, IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 997) continue;          // ERROR_IO_PENDING
                    Console.WriteLine($"read failed, error {err}");
                    System.Threading.Thread.Sleep(50);
                    continue;
                }

                hdr = Marshal.PtrToStructure<Ks.KSSTREAM_HEADER>(header);
                int used = (int)hdr.DataUsed;
                if (used <= 0) continue;

                int offset = 0;
                while (offset + 8 <= used)
                {
                    uint delta = (uint)Marshal.ReadInt32(buffer, offset);
                    uint count = (uint)Marshal.ReadInt32(buffer, offset + 4);
                    offset += 8;
                    if (count == 0 || offset + count > used) break;

                    var bytes = new byte[count];
                    Marshal.Copy(buffer + offset, bytes, 0, (int)count);
                    Dump(delta, bytes);

                    offset += (int)((count + 3) & ~3u);   // blocks are 4-byte aligned
                }
            }
        }
        finally
        {
            Ks.SetPinState(pin, Ks.KSSTATE_STOP, out _);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(buffer);
            Ks.CloseHandle(pin);
            Ks.CloseHandle(filter);
        }
    }

    static void Dump(uint deltaMs, byte[] data)
    {
        var hex = new StringBuilder();
        var ascii = new StringBuilder();
        foreach (byte b in data)
        {
            hex.Append(b.ToString("X2")).Append(' ');
            ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
        }
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  +{deltaMs,-5}ms  {data.Length,3}B  {hex} | {ascii}");
    }

    // ---- entry point --------------------------------------------------------

    static int Main(string[] args)
    {
        // The console defaults to the OEM codepage, which turns Russian output into
        // question marks.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        if (args.Length == 0 || args[0] == "--list")
        {
            Console.WriteLine("KS audio/MIDI filters present:\n");
            var paths = EnumerateAudioFilters();
            for (int i = 0; i < paths.Count; i++)
                Console.WriteLine($"  [{i}] {paths[i]}");
            Console.WriteLine("\nUsage: KsMidiMon --match <substring> [--pin N] [--seconds N]");
            Console.WriteLine("       KsMidiMon --path <full path> [--pin N] [--seconds N]");
            return 0;
        }

        string? path = null, match = null, message = null, outPort = null;
        uint pin = 4;
        int seconds = 300;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--path") path = args[i + 1];
            else if (args[i] == "--match") match = args[i + 1];
            else if (args[i] == "--pin") pin = uint.Parse(args[i + 1]);
            else if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);
            else if (args[i] == "--server") message = args[i + 1];
            else if (args[i] == "--out") outPort = args[i + 1];
        }

        if (path is null && match is not null)
        {
            foreach (string p in EnumerateAudioFilters())
                if (p.Contains(match, StringComparison.OrdinalIgnoreCase)) { path = p; break; }
            if (path is null) { Console.WriteLine($"no filter matching '{match}'"); return 1; }
        }
        if (path is null) { Console.WriteLine("need --path or --match"); return 1; }

        foreach (string a in args)
        {
            if (a == "--pins") { Pins.Dump(path); return 0; }
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--leds") continue;
            int from = 0, to = 80, hold = 1200;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int f)) from = f;
            if (i + 2 < args.Length && int.TryParse(args[i + 2], out int t)) to = t;
            if (i + 3 < args.Length && int.TryParse(args[i + 3], out int h)) hold = h;
            return Automap.Scan(path, from, to, hold);
        }

        foreach (string a in args)
            if (a == "--ports") { MidiOut.ListPorts(); return 0; }

        if (message is not null)
            return Automap.Run(path, message, seconds, outPort);

        Monitor(path, pin, seconds);
        return 0;
    }
}
