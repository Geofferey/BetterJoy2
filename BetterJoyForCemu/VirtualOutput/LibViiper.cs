using System;
using System.Runtime.InteropServices;

namespace BetterJoyForCemu.VirtualOutput {
    // Minimal P/Invoke binding for libVIIPER (https://github.com/Alia5/VIIPER, GPL-3.0), a
    // cross-platform virtual USB input device framework. Scoped to only what this project's Xbox
    // 360 output backend actually needs - server lifecycle plus the Xbox 360 device functions.
    // libVIIPER.dll itself stays GPL-3.0 even though the rest of this project is MIT - see
    // README's credits section. Trimmed from the full header (DualSense/DS4/keyboard/mouse/NS2Pro
    // device types exist too, and aren't used here) to match this project's HIDapi.cs/
    // SbcEncoder.cs convention of only binding what's actually called.
    internal static class LibViiper {
        private const string Dll = "libVIIPER.dll";

        public enum LogLevel : int {
            Debug = -4,
            Info = 0,
            Warn = 4,
            Error = 8,
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallback(LogLevel level,
            [MarshalAs(UnmanagedType.LPStr)] string message);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Xbox360RumbleCallback(UIntPtr deviceHandle, byte leftMotor,
            byte rightMotor);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct ServerConfig {
            [MarshalAs(UnmanagedType.LPStr)]
            public string Addr;
            public ulong ConnectionTimeoutMs;
            public ulong DeviceHandlerConnectTimeoutMs;
            public uint WriteBatchFlushIntervalMs;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Xbox360DeviceState {
            public uint Buttons;
            public byte LT;
            public byte RT;
            public short LX;
            public short LY;
            public short RX;
            public short RY;
            public byte Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
        }

        [Flags]
        public enum Xbox360Buttons : uint {
            DPadUp = 0x0001,
            DPadDown = 0x0002,
            DPadLeft = 0x0004,
            DPadRight = 0x0008,
            Start = 0x0010,
            Back = 0x0020,
            LThumb = 0x0040,
            RThumb = 0x0080,
            LShoulder = 0x0100,
            RShoulder = 0x0200,
            Guide = 0x0400,
            A = 0x1000,
            B = 0x2000,
            X = 0x4000,
            Y = 0x8000,
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NewUSBServer([In] ref ServerConfig config,
            out UIntPtr outHandle, LogCallback logCallback);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CloseUSBServer(UIntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CreateUSBBus(UIntPtr handle, ref uint busId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool RemoveUSBBus(UIntPtr handle, uint busId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CreateXbox360Device(UIntPtr serverHandle,
            out UIntPtr outDeviceHandle, uint busId,
            [MarshalAs(UnmanagedType.I1)] bool autoAttachLocalhost, ushort idVendor,
            ushort idProduct, byte xinputSubType);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SetXbox360DeviceState(UIntPtr deviceHandle,
            Xbox360DeviceState state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool RemoveXbox360Device(UIntPtr deviceHandle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SetXbox360RumbleCallback(UIntPtr deviceHandle,
            Xbox360RumbleCallback callback);
    }
}
