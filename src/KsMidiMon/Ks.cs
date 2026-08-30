using System;
using System.Runtime.InteropServices;

namespace KsMidiMon;

/// <summary>
/// Kernel Streaming interop. This is the layer Automap talks to the synth on,
/// below WinMM, which is why nothing shows up in ordinary MIDI monitors.
/// </summary>
internal static class Ks
{
    // ---- GUIDs -------------------------------------------------------------

    public static readonly Guid KSDATAFORMAT_TYPE_MUSIC =
        new("e725d360-62cc-11cf-a5d6-28db04c10000");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_MIDI =
        new("1d262760-e957-11cf-a5d6-28db04c10000");
    public static readonly Guid KSDATAFORMAT_SPECIFIER_NONE =
        new("0f6417d6-c318-11d0-a43f-00a0c9223196");
    public static readonly Guid KSINTERFACESETID_Standard =
        new("1a8766a0-62ce-11cf-a5d6-28db04c10000");
    public static readonly Guid KSMEDIUMSETID_Standard =
        new("4747b320-62ce-11cf-a5d6-28db04c10000");
    public static readonly Guid KSPROPSETID_Connection =
        new("1d58c920-ac9b-11cf-a5d6-28db04c10000");
    public static readonly Guid KSCATEGORY_AUDIO =
        new("6994ad04-93ef-11d0-a3cc-00a0c9223196");

    public const uint KSINTERFACE_STANDARD_STREAMING = 0;
    public const uint KSMEDIUM_TYPE_ANYINSTANCE = 0;
    public const uint KSPROPERTY_CONNECTION_STATE = 0;

    public const uint KSSTATE_STOP = 0;
    public const uint KSSTATE_ACQUIRE = 1;
    public const uint KSSTATE_PAUSE = 2;
    public const uint KSSTATE_RUN = 3;

    public const uint KSPROPERTY_TYPE_GET = 0x00000001;
    public const uint KSPROPERTY_TYPE_SET = 0x00000002;

    // CTL_CODE(FILE_DEVICE_KS = 0x2f, function, METHOD_NEITHER = 3, access)
    public const uint IOCTL_KS_PROPERTY = 0x002F0003;
    public const uint IOCTL_KS_WRITE_STREAM = 0x002F8013;
    public const uint IOCTL_KS_READ_STREAM = 0x002F4017;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    // ---- structures --------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct KSIDENTIFIER
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KSPRIORITY
    {
        public uint PriorityClass;
        public uint PrioritySubClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KSPIN_CONNECT
    {
        public KSIDENTIFIER Interface;
        public KSIDENTIFIER Medium;
        public uint PinId;
        private uint _pad;
        public IntPtr PinToHandle;
        public KSPRIORITY Priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KSDATAFORMAT
    {
        public uint FormatSize;
        public uint Flags;
        public uint SampleSize;
        public uint Reserved;
        public Guid MajorFormat;
        public Guid SubFormat;
        public Guid Specifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KSTIME
    {
        public long Time;
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KSSTREAM_HEADER
    {
        public uint Size;
        public uint TypeSpecificFlags;
        public KSTIME PresentationTime;
        public long Duration;
        public uint FrameExtent;
        public uint DataUsed;
        public IntPtr Data;
        public uint OptionsFlags;
        public uint Reserved;
    }

    /// <summary>Each block in a MIDI stream buffer is prefixed with this.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KSMUSICFORMAT
    {
        public uint TimeDeltaMs;
        public uint ByteCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    // ---- imports -----------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("ksuser.dll", SetLastError = true)]
    public static extern uint KsCreatePin(
        IntPtr filterHandle, IntPtr connect, uint desiredAccess, out IntPtr connectionHandle);

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Instantiate a MIDI pin on an already opened KS filter.
    /// KsCreatePin wants KSPIN_CONNECT followed immediately by the data format.
    /// </summary>
    public static IntPtr CreateMidiPin(IntPtr filter, uint pinId, bool forWriting, out uint status)
        => CreateMidiPin(filter, pinId, forWriting, KSDATAFORMAT_SUBTYPE_MIDI, out status);

    /// <summary>
    /// Same, but with the subformat taken from the pin's own datarange. The Automap
    /// pins advertise a vendor subformat (39d30b88-...), and hardcoding the standard
    /// MIDI one makes KsCreatePin answer STATUS_NO_MATCH.
    /// </summary>
    public static IntPtr CreateMidiPin(IntPtr filter, uint pinId, bool forWriting,
                                       Guid subFormat, out uint status)
    {
        int connectSize = Marshal.SizeOf<KSPIN_CONNECT>();
        int formatSize = Marshal.SizeOf<KSDATAFORMAT>();
        IntPtr block = Marshal.AllocHGlobal(connectSize + formatSize);
        try
        {
            var connect = new KSPIN_CONNECT
            {
                Interface = new KSIDENTIFIER
                {
                    Set = KSINTERFACESETID_Standard,
                    Id = KSINTERFACE_STANDARD_STREAMING,
                    Flags = 0
                },
                Medium = new KSIDENTIFIER
                {
                    Set = KSMEDIUMSETID_Standard,
                    Id = KSMEDIUM_TYPE_ANYINSTANCE,
                    Flags = 0
                },
                PinId = pinId,
                PinToHandle = IntPtr.Zero,
                Priority = new KSPRIORITY { PriorityClass = 1, PrioritySubClass = 1 }
            };
            var format = new KSDATAFORMAT
            {
                FormatSize = (uint)formatSize,
                Flags = 0,
                SampleSize = 0,
                Reserved = 0,
                MajorFormat = KSDATAFORMAT_TYPE_MUSIC,
                SubFormat = subFormat,
                Specifier = KSDATAFORMAT_SPECIFIER_NONE
            };

            Marshal.StructureToPtr(connect, block, false);
            Marshal.StructureToPtr(format, block + connectSize, false);

            uint access = forWriting ? GENERIC_WRITE : GENERIC_READ;
            status = KsCreatePin(filter, block, access, out IntPtr pin);
            return status == 0 ? pin : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>Move a pin between STOP / ACQUIRE / PAUSE / RUN.</summary>
    public static bool SetPinState(IntPtr pin, uint state, out int error)
    {
        var prop = new KSPROPERTY
        {
            Set = KSPROPSETID_Connection,
            Id = KSPROPERTY_CONNECTION_STATE,
            Flags = KSPROPERTY_TYPE_SET
        };
        IntPtr pProp = Marshal.AllocHGlobal(Marshal.SizeOf<KSPROPERTY>());
        IntPtr pState = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.StructureToPtr(prop, pProp, false);
            Marshal.WriteInt32(pState, (int)state);
            bool ok = DeviceIoControl(pin, IOCTL_KS_PROPERTY,
                pProp, (uint)Marshal.SizeOf<KSPROPERTY>(),
                pState, sizeof(uint), out _, IntPtr.Zero);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(pProp);
            Marshal.FreeHGlobal(pState);
        }
    }

    /// <summary>
    /// Send raw MIDI bytes to a pin opened for writing.
    ///
    /// The payload is a KSMUSICFORMAT block header followed by the bytes, padded to a
    /// DWORD boundary. PresentationTime is set to 1/1 rather than left at zero: some
    /// drivers reject a write whose time base is 0/0, and the read path never needed it.
    /// </summary>
    public static bool WriteMidi(IntPtr pin, byte[] midi, out int error)
    {
        int musicSize = Marshal.SizeOf<KSMUSICFORMAT>();
        int payload = musicSize + midi.Length;
        int padded = (payload + 3) & ~3;

        IntPtr buffer = Marshal.AllocHGlobal(padded);
        int headerSize = Marshal.SizeOf<KSSTREAM_HEADER>();
        IntPtr header = Marshal.AllocHGlobal(headerSize);
        try
        {
            for (int i = 0; i < padded; i++) Marshal.WriteByte(buffer, i, 0);

            var music = new KSMUSICFORMAT { TimeDeltaMs = 0, ByteCount = (uint)midi.Length };
            Marshal.StructureToPtr(music, buffer, false);
            Marshal.Copy(midi, 0, buffer + musicSize, midi.Length);

            var hdr = new KSSTREAM_HEADER
            {
                Size = (uint)headerSize,
                TypeSpecificFlags = 0,
                PresentationTime = new KSTIME { Time = 0, Numerator = 1, Denominator = 1 },
                Duration = 0,
                FrameExtent = (uint)padded,
                DataUsed = (uint)payload,
                Data = buffer,
                OptionsFlags = 0,
                Reserved = 0
            };
            Marshal.StructureToPtr(hdr, header, false);

            bool ok = DeviceIoControl(pin, IOCTL_KS_WRITE_STREAM,
                IntPtr.Zero, 0, header, (uint)headerSize, out _, IntPtr.Zero);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(header);
        }
    }
}
