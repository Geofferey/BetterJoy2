using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterJoyForCemu {
    // Forces the local Bluetooth radio to drop its connection to a specific paired device, via
    // the same low-level IOCTL_BTH_DISCONNECT_DEVICE approach DS4Windows uses (HidLibrary/
    // NativeMethods.cs + DS4Library/DS4Device.cs's DisconnectBT) to release a controller's
    // Bluetooth link once the same physical unit is also connected over USB - otherwise the
    // still-live BT HID device keeps getting rediscovered every scan, spawning a duplicate
    // virtual controller that RetireDuplicateConnections has to keep retiring.
    internal static class BluetoothRadio {
        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS {
            public int dwSize;
        }

        private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000c;
        private const string BluetoothKeysRegistryPath =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys";

        [DllImport("bthprops.cpl", CharSet = CharSet.Auto)]
        private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, ref IntPtr phRadio);

        [DllImport("bthprops.cpl", CharSet = CharSet.Auto)]
        private static extern bool BluetoothFindNextRadio(IntPtr hFind, ref IntPtr phRadio);

        [DllImport("bthprops.cpl", CharSet = CharSet.Auto)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, ref long lpInBuffer, int nInBufferSize,
            IntPtr lpOutBuffer, int nOutBufferSize, ref int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Auto)]
        private static extern bool CloseHandle(IntPtr hObject);

        // mac must be 6 bytes in normal display order (mac[0] is the first octet you'd show -
        // matches PhysicalAddress.GetAddressBytes()). Tries every local radio in turn, same as
        // DS4Windows does, since which radio "owns" the paired device isn't queried separately.
        internal static bool DisconnectDevice(byte[] mac) {
            if (mac == null || mac.Length != 6)
                return false;

            long btAddr = 0;
            for (int i = 0; i < 6; i++)
                btAddr |= ((long)mac[i]) << (8 * (5 - i));

            var searchParams = new BLUETOOTH_FIND_RADIO_PARAMS {
                dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS))
            };
            IntPtr radioHandle = IntPtr.Zero;
            IntPtr searchHandle = BluetoothFindFirstRadio(ref searchParams, ref radioHandle);
            if (searchHandle == IntPtr.Zero)
                return false;

            bool success = false;
            try {
                while (!success && radioHandle != IntPtr.Zero) {
                    int bytesReturned = 0;
                    success = DeviceIoControl(radioHandle, IOCTL_BTH_DISCONNECT_DEVICE, ref btAddr, 8,
                        IntPtr.Zero, 0, ref bytesReturned, IntPtr.Zero);
                    CloseHandle(radioHandle);
                    if (!success && !BluetoothFindNextRadio(searchHandle, ref radioHandle))
                        radioHandle = IntPtr.Zero;
                }
            } finally {
                BluetoothFindRadioClose(searchHandle);
            }
            return success;
        }

        // Classic Bluetooth link keys are owned by BthPort and protected so ordinary desktop
        // processes cannot read them. BetterJoy's controller owner normally runs as LocalSystem,
        // which can read the exact existing bond without changing it. hostMacLittleEndian comes
        // directly from DualSense feature report 0x09; the registry names both adapter and device
        // in normal display order. Never log the returned key.
        internal static bool TryGetClassicLinkKey(byte[] hostMacLittleEndian,
                byte[] deviceMac, out byte[] linkKey) {
            linkKey = null;
            if (hostMacLittleEndian == null || hostMacLittleEndian.Length != 6 ||
                    deviceMac == null || deviceMac.Length != 6)
                return false;

            byte[] hostMac = new byte[6];
            for (int i = 0; i < hostMac.Length; i++)
                hostMac[i] = hostMacLittleEndian[hostMac.Length - 1 - i];

            string adapterName = MacRegistryName(hostMac);
            string deviceName = MacRegistryName(deviceMac);
            try {
                using (RegistryKey localMachine = RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem
                            ? RegistryView.Registry64 : RegistryView.Registry32))
                using (RegistryKey adapterKey = localMachine.OpenSubKey(
                        BluetoothKeysRegistryPath + "\\" + adapterName, false)) {
                    byte[] stored = adapterKey?.GetValue(deviceName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                    if (stored == null || stored.Length != 16)
                        return false;

                    linkKey = (byte[])stored.Clone();
                    return true;
                }
            } catch (UnauthorizedAccessException) {
                return false;
            } catch (System.Security.SecurityException) {
                return false;
            } catch (System.IO.IOException) {
                return false;
            }
        }

        private static string MacRegistryName(byte[] mac) {
            char[] text = new char[mac.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < mac.Length; i++) {
                text[i * 2] = hex[mac[i] >> 4];
                text[i * 2 + 1] = hex[mac[i] & 0x0f];
            }
            return new string(text);
        }
    }
}
