using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KsMidiMon;

/// <summary>
/// Pin enumeration on a KS filter.
///
/// The property ids below are what cost us the most: KSPROPERTY_PIN_CTYPES is 1,
/// not 0. Asking for id 0 (CINSTANCES) with a 4-byte buffer always fails with
/// ERROR_INSUFFICIENT_BUFFER, because that property returns an 8-byte
/// KSPIN_CINSTANCES - which is why every enumeration attempt died with error 122.
/// </summary>
internal static class Pins
{
    public static readonly Guid KSPROPSETID_Pin =
        new("8c134960-51ad-11cf-878a-94f801c10000");

    public const uint CINSTANCES = 0;
    public const uint CTYPES = 1;
    public const uint DATAFLOW = 2;
    public const uint DATARANGES = 3;
    public const uint INTERFACES = 5;
    public const uint MEDIUMS = 6;
    public const uint COMMUNICATION = 7;
    public const uint GLOBALCINSTANCES = 8;
    public const uint CATEGORY = 11;
    public const uint NAME = 12;

    public const uint DATAFLOW_IN = 1;   // host -> device
    public const uint DATAFLOW_OUT = 2;  // device -> host

    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_SET_NOT_FOUND = 1170;

    /// <summary>KSP_PIN: a KSIDENTIFIER followed by the pin index. 32 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KSP_PIN
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
        public uint PinId;
        public uint Reserved;
    }

    public sealed class PinInfo
    {
        public uint Id;
        public uint DataFlow;
        public uint Communication;
        public string Name = "";
        public List<Ks.KSDATAFORMAT> Ranges = new();

        public bool IsMusic
        {
            get
            {
                foreach (var r in Ranges)
                    if (r.MajorFormat == Ks.KSDATAFORMAT_TYPE_MUSIC) return true;
                return false;
            }
        }

        public string Flow => DataFlow == DATAFLOW_IN ? "IN  (в синт)"
                            : DataFlow == DATAFLOW_OUT ? "OUT (из синта)"
                            : "?" + DataFlow;

        public string Comm => Communication switch
        {
            0 => "none",
            1 => "sink",
            2 => "source",
            3 => "both",
            4 => "bridge",
            _ => Communication.ToString()
        };
    }

    /// <summary>
    /// One KS property request. Returns the raw bytes, or null with the error set.
    /// Variable-length properties are probed first with a null output buffer.
    /// </summary>
    static byte[] Query(IntPtr filter, uint propId, uint pinId, int fixedSize, out int error)
    {
        var p = new KSP_PIN
        {
            Set = KSPROPSETID_Pin,
            Id = propId,
            Flags = Ks.KSPROPERTY_TYPE_GET,
            PinId = pinId,
            Reserved = 0
        };
        int pSize = Marshal.SizeOf<KSP_PIN>();
        IntPtr pIn = Marshal.AllocHGlobal(pSize);
        Marshal.StructureToPtr(p, pIn, false);
        try
        {
            int size = fixedSize;
            if (size <= 0)
            {
                Ks.DeviceIoControl(filter, Ks.IOCTL_KS_PROPERTY, pIn, (uint)pSize,
                                   IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                error = Marshal.GetLastWin32Error();
                if (error == ERROR_SET_NOT_FOUND) return null;
                size = (int)needed;
                if (size <= 0)
                {
                    if (error == 0) error = ERROR_INSUFFICIENT_BUFFER;
                    return null;
                }
            }

            IntPtr pOut = Marshal.AllocHGlobal(size);
            try
            {
                bool ok = Ks.DeviceIoControl(filter, Ks.IOCTL_KS_PROPERTY, pIn, (uint)pSize,
                                             pOut, (uint)size, out uint got, IntPtr.Zero);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok) return null;
                int len = got > 0 ? (int)got : size;
                var buf = new byte[len];
                Marshal.Copy(pOut, buf, 0, len);
                return buf;
            }
            finally { Marshal.FreeHGlobal(pOut); }
        }
        finally { Marshal.FreeHGlobal(pIn); }
    }

    static uint QueryU32(IntPtr filter, uint propId, uint pinId)
    {
        var b = Query(filter, propId, pinId, sizeof(uint), out _);
        return b != null && b.Length >= 4 ? BitConverter.ToUInt32(b, 0) : uint.MaxValue;
    }

    public static List<PinInfo> Enumerate(IntPtr filter, out string diagnostic)
    {
        var pins = new List<PinInfo>();

        // CTYPES is a plain ULONG, so it needs no probe.
        var ct = Query(filter, CTYPES, 0, sizeof(uint), out int err);
        if (ct == null || ct.Length < 4)
        {
            diagnostic = "CTYPES не прочитался, ошибка " + err;
            return pins;
        }
        uint count = BitConverter.ToUInt32(ct, 0);
        diagnostic = "пинов на фильтре: " + count;

        int fmtSize = Marshal.SizeOf<Ks.KSDATAFORMAT>();
        for (uint i = 0; i < count; i++)
        {
            var info = new PinInfo
            {
                Id = i,
                DataFlow = QueryU32(filter, DATAFLOW, i),
                Communication = QueryU32(filter, COMMUNICATION, i)
            };

            var nameBuf = Query(filter, NAME, i, 0, out _);
            if (nameBuf != null && nameBuf.Length > 1)
                info.Name = System.Text.Encoding.Unicode.GetString(nameBuf).TrimEnd('\0');

            var ranges = Query(filter, DATARANGES, i, 0, out _);
            if (ranges != null && ranges.Length >= 8)
            {
                // KSMULTIPLE_ITEM: Size, Count, then the entries packed to 8 bytes.
                uint n = BitConverter.ToUInt32(ranges, 4);
                int off = 8;
                var handle = GCHandle.Alloc(ranges, GCHandleType.Pinned);
                try
                {
                    for (uint k = 0; k < n && off + fmtSize <= ranges.Length; k++)
                    {
                        var fmt = Marshal.PtrToStructure<Ks.KSDATAFORMAT>(
                            handle.AddrOfPinnedObject() + off);
                        info.Ranges.Add(fmt);
                        int step = (int)fmt.FormatSize;
                        if (step < fmtSize) step = fmtSize;
                        off += (step + 7) & ~7;
                    }
                }
                finally { handle.Free(); }
            }

            pins.Add(info);
        }
        return pins;
    }

    public static void Dump(string filterPath)
    {
        // Synchronous handle on purpose: with FILE_FLAG_OVERLAPPED and a null
        // OVERLAPPED the size probe never fills lpBytesReturned, so every buffer
        // comes back as zero length.
        IntPtr filter = Ks.CreateFileW(filterPath, Ks.GENERIC_READ | Ks.GENERIC_WRITE,
            Ks.FILE_SHARE_READ | Ks.FILE_SHARE_WRITE, IntPtr.Zero,
            Ks.OPEN_EXISTING, 0, IntPtr.Zero);
        if (filter == IntPtr.Zero || filter == new IntPtr(-1))
        {
            Console.WriteLine("фильтр не открылся, ошибка " + Marshal.GetLastWin32Error());
            return;
        }
        try
        {
            var pins = Enumerate(filter, out string diag);
            Console.WriteLine(diag);
            foreach (var p in pins)
            {
                string music = p.IsMusic ? "  MUSIC" : "";
                Console.WriteLine("  pin " + p.Id.ToString().PadLeft(2) + "  " +
                                  p.Flow.PadRight(15) + " comm=" + p.Comm.PadRight(7) +
                                  " ranges=" + p.Ranges.Count + music +
                                  (p.Name.Length > 0 ? "  " + p.Name : ""));
                foreach (var r in p.Ranges)
                    Console.WriteLine("        major=" + r.MajorFormat + "  sub=" + r.SubFormat);
            }
        }
        finally { Ks.CloseHandle(filter); }
    }
}
