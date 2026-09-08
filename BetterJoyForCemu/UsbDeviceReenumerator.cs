using System;
using System.Runtime.InteropServices;

namespace BetterJoyForCemu {
    // Best-effort PnP nudge used only after BetterJoy has deliberately stepped off a USB
    // DualSense/Edge for charge-only pseudo-sleep. The normal HID handle is already closed before
    // this runs; this simply asks Windows to re-enumerate the HID devnode/parent so the controller
    // can settle into its firmware-owned charging LED state while our wake monitor waits.
    internal static class UsbDeviceReenumerator {
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const uint CrSuccess = 0x00000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData {
            public int Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, ref SpDevinfoData deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parentDevInst,
            uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Reenumerate_DevNode(uint devInst, uint flags);

        internal static bool TryReenumerateHidInterface(string devicePath,
                out string detail) {
            detail = "no path";
            if (String.IsNullOrWhiteSpace(devicePath))
                return false;

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid,
                IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (deviceInfoSet == new IntPtr(-1)) {
                detail = "SetupDiGetClassDevs failed";
                return false;
            }

            try {
                for (uint index = 0; ; index++) {
                    var interfaceData = new SpDeviceInterfaceData {
                        Size = Marshal.SizeOf(typeof(SpDeviceInterfaceData))
                    };
                    if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero,
                            ref hidGuid, index, ref interfaceData))
                        break;

                    uint requiredSize = 0;
                    SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData,
                        IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                    if (requiredSize == 0)
                        continue;

                    IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
                    try {
                        Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                        var devInfo = new SpDevinfoData {
                            Size = Marshal.SizeOf(typeof(SpDevinfoData))
                        };
                        if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet,
                                ref interfaceData, detailBuffer, requiredSize,
                                out requiredSize, ref devInfo))
                            continue;

                        string candidatePath =
                            Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                        if (!String.Equals(candidatePath, devicePath,
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        uint direct = CM_Reenumerate_DevNode(devInfo.DevInst, 0);
                        uint parentResult = UInt32.MaxValue;
                        if (CM_Get_Parent(out uint parentDevInst, devInfo.DevInst, 0) ==
                                CrSuccess)
                            parentResult = CM_Reenumerate_DevNode(parentDevInst, 0);

                        detail = "direct=0x" + direct.ToString("X8") +
                            " parent=0x" + parentResult.ToString("X8");
                        return direct == CrSuccess || parentResult == CrSuccess;
                    } finally {
                        Marshal.FreeHGlobal(detailBuffer);
                    }
                }
            } finally {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            detail = "HID path not found";
            return false;
        }
    }
}
