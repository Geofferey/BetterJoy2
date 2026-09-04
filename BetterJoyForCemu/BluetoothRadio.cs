using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_RADIO_INFO {
            public int dwSize;
            public ulong address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string name;
            public uint classOfDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEMTIME {
            public ushort year;
            public ushort month;
            public ushort dayOfWeek;
            public ushort day;
            public ushort hour;
            public ushort minute;
            public ushort second;
            public ushort milliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct BLUETOOTH_DEVICE_INFO {
            public int dwSize;
            public ulong address;
            public uint classOfDevice;
            [MarshalAs(UnmanagedType.Bool)]
            public bool connected;
            [MarshalAs(UnmanagedType.Bool)]
            public bool remembered;
            [MarshalAs(UnmanagedType.Bool)]
            public bool authenticated;
            public SYSTEMTIME lastSeen;
            public SYSTEMTIME lastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS {
            public int dwSize;
            [MarshalAs(UnmanagedType.Bool)]
            public bool returnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)]
            public bool returnRemembered;
            [MarshalAs(UnmanagedType.Bool)]
            public bool returnUnknown;
            [MarshalAs(UnmanagedType.Bool)]
            public bool returnConnected;
            [MarshalAs(UnmanagedType.Bool)]
            public bool issueInquiry;
            public byte timeoutMultiplier;
            public IntPtr radioHandle;
        }

        private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000c;
        private const uint ErrorSuccess = 0;
        private const uint ErrorInvalidArgumentHresult = 0x80070057;
        private const uint BluetoothServiceEnable = 1;
        private const string BluetoothKeysRegistryPath =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys";
        private const string BluetoothDevicesRegistryPath =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";
        private static readonly Guid HumanInterfaceDeviceServiceClass =
            new Guid("00001124-0000-1000-8000-00805F9B34FB");

        [DllImport("bthprops.cpl", CharSet = CharSet.Auto)]
        private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, ref IntPtr phRadio);

        [DllImport("bthprops.cpl", CharSet = CharSet.Auto)]
        private static extern bool BluetoothFindNextRadio(IntPtr hFind, ref IntPtr phRadio);

        [DllImport("bthprops.cpl", CharSet = CharSet.Auto)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
        private static extern uint BluetoothGetRadioInfo(IntPtr hRadio,
            ref BLUETOOTH_RADIO_INFO radioInfo);

        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
        private static extern IntPtr BluetoothFindFirstDevice(
            ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams,
            ref BLUETOOTH_DEVICE_INFO deviceInfo);

        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
        private static extern bool BluetoothFindNextDevice(IntPtr findHandle,
            ref BLUETOOTH_DEVICE_INFO deviceInfo);

        [DllImport("bthprops.cpl")]
        private static extern bool BluetoothFindDeviceClose(IntPtr findHandle);

        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
        private static extern uint BluetoothUpdateDeviceRecord(
            ref BLUETOOTH_DEVICE_INFO deviceInfo);

        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
        private static extern uint BluetoothSetServiceState(IntPtr radioHandle,
            ref BLUETOOTH_DEVICE_INFO deviceInfo, ref Guid serviceGuid,
            uint serviceFlags);

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

            byte[] hostMac = ReverseAddress(hostMacLittleEndian);
            string adapterName = MacRegistryName(hostMac);
            string deviceName = MacRegistryName(deviceMac);
            try {
                using (RegistryKey localMachine = OpenLocalMachine())
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

        // DIAGNOSTIC ONLY: describe the current BthPort registry state for this controller MAC so we
        // can watch the bond come into existence across pairing rounds - the link key under each
        // adapter's Keys value, plus the full Devices\<mac> record subtree (Name, COD, SSP/paired
        // flags, ServicesFor\<adapter>\{00001124...} enable, CachedServices, etc.). Logs the raw key
        // on purpose here (diagnostic; not for production). Never throws.
        internal static string DescribeClassicPairingRegistry(byte[] deviceMac) {
            if (deviceMac == null || deviceMac.Length != 6)
                return "(bad mac)";
            string deviceName = MacRegistryName(deviceMac);
            StringBuilder sb = new StringBuilder();
            try {
                using (RegistryKey localMachine = OpenLocalMachine()) {
                    foreach (byte[] radioLE in GetLocalRadioAddressesLittleEndian()) {
                        string adapterName = MacRegistryName(ReverseAddress(radioLE));
                        using (RegistryKey adapterKey = localMachine.OpenSubKey(
                                BluetoothKeysRegistryPath + "\\" + adapterName, false)) {
                            byte[] stored = adapterKey?.GetValue(deviceName, null,
                                RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                            sb.Append("Keys[").Append(adapterName).Append("]=")
                              .Append(stored == null ? "none"
                                  : BitConverter.ToString(stored).Replace("-", ""))
                              .Append("; ");
                        }
                    }
                    using (RegistryKey devKey = localMachine.OpenSubKey(
                            BluetoothDevicesRegistryPath + "\\" + deviceName, false)) {
                        if (devKey == null) {
                            sb.Append("Devices[").Append(deviceName).Append("]=none");
                        } else {
                            sb.Append("Devices[").Append(deviceName).Append("]{ ");
                            AppendRegistrySubtree(devKey, sb, 0, 4);
                            sb.Append('}');
                        }
                    }
                }
            } catch (Exception ex) {
                sb.Append("(registry read error: ").Append(ex.GetType().Name).Append(')');
            }
            return sb.ToString();
        }

        private static void AppendRegistrySubtree(RegistryKey key, StringBuilder sb,
                int depth, int maxDepth) {
            if (key == null)
                return;
            foreach (string valueName in key.GetValueNames()) {
                object val = key.GetValue(valueName, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                sb.Append(valueName.Length == 0 ? "(default)" : valueName)
                  .Append('=').Append(FormatRegistryValue(val)).Append(' ');
            }
            if (depth >= maxDepth)
                return;
            foreach (string subName in key.GetSubKeyNames()) {
                sb.Append('[').Append(subName).Append(": ");
                using (RegistryKey subKey = key.OpenSubKey(subName, false))
                    AppendRegistrySubtree(subKey, sb, depth + 1, maxDepth);
                sb.Append(']');
            }
        }

        private static string FormatRegistryValue(object val) {
            if (val == null)
                return "null";
            if (val is byte[] bytes)
                return "0x" + BitConverter.ToString(bytes).Replace("-", "");
            return val.ToString();
        }

        // Select the local adapter that already owns this controller's bond when possible. For a
        // controller Windows has never seen, create one cryptographically random classic link key
        // under the first available radio. The caller writes the same key to the controller with
        // feature report 0x0A, making both sides agree without a Windows pairing dialog.
        internal static bool TryGetOrCreateClassicPairing(byte[] deviceMac,
                out byte[] hostMacLittleEndian, out byte[] linkKey, out bool created) {
            hostMacLittleEndian = null;
            linkKey = null;
            created = false;
            if (deviceMac == null || deviceMac.Length != 6)
                return false;

            List<byte[]> localRadios = GetLocalRadioAddressesLittleEndian();
            foreach (byte[] radio in localRadios) {
                if (TryGetClassicLinkKey(radio, deviceMac, out byte[] existing)) {
                    hostMacLittleEndian = radio;
                    linkKey = existing;
                    return true;
                }
            }
            if (localRadios.Count == 0)
                return false;

            byte[] selectedRadio = localRadios[0];
            byte[] generated = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(generated);

            if (!TryStoreClassicLinkKey(selectedRadio, deviceMac, generated,
                    out byte[] effectiveKey, out created)) {
                Array.Clear(generated, 0, generated.Length);
                return false;
            }

            Array.Clear(generated, 0, generated.Length);
            hostMacLittleEndian = selectedRadio;
            linkKey = effectiveKey;
            return true;
        }

        // A matching link key is enough for the controller to open a baseband connection, but
        // Windows does not turn an unknown incoming radio into a named, remembered HID device
        // from that key alone. Wait for the controller to appear in the Bluetooth cache, give a
        // still-anonymous record a useful fallback name, then enable its standard HID service.
        // BluetoothSetServiceState is the supported Windows step that installs the profile
        // driver and completes the device entry shown by Bluetooth Settings.
        //
        // Reverted from an authentication-window/durability-gated version (active
        // BluetoothRegisterForAuthenticationEx callback, BluetoothAuthenticateDeviceEx retries,
        // requiring authenticated+remembered to hold for a continuous 2-second window) back to
        // this simpler shape. Real hardware evidence: every added verification/durability layer
        // this session correlated with worse outcomes, not better, and the authentication
        // callback never fired even once across many attempts - the simpler version is the one
        // that got furthest (real connections observed staying up on their own).
        internal static bool TryFinalizeClassicHidPairing(byte[] hostMacLittleEndian,
                byte[] deviceMac, string fallbackName, int timeoutMilliseconds) {
            if (hostMacLittleEndian == null || hostMacLittleEndian.Length != 6 ||
                    deviceMac == null || deviceMac.Length != 6)
                return false;

            timeoutMilliseconds = Math.Max(0, timeoutMilliseconds);
            long started = Stopwatch.GetTimestamp();
            while (true) {
                if (TryFinalizeClassicHidPairingOnce(hostMacLittleEndian,
                        deviceMac, fallbackName))
                    return true;

                long elapsedMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000L /
                    Stopwatch.Frequency;
                if (elapsedMilliseconds >= timeoutMilliseconds)
                    return false;
                Thread.Sleep(200);
            }
        }

        private static bool TryFinalizeClassicHidPairingOnce(
                byte[] hostMacLittleEndian, byte[] deviceMac, string fallbackName) {
            ulong targetDeviceAddress = AddressValue(deviceMac);
            var radioSearch = new BLUETOOTH_FIND_RADIO_PARAMS {
                dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS))
            };
            IntPtr radioHandle = IntPtr.Zero;
            IntPtr radioFindHandle = BluetoothFindFirstRadio(ref radioSearch,
                ref radioHandle);
            if (radioFindHandle == IntPtr.Zero)
                return false;

            try {
                while (radioHandle != IntPtr.Zero) {
                    var radioInfo = new BLUETOOTH_RADIO_INFO {
                        dwSize = Marshal.SizeOf(typeof(BLUETOOTH_RADIO_INFO))
                    };
                    bool matchingRadio = BluetoothGetRadioInfo(radioHandle,
                        ref radioInfo) == ErrorSuccess &&
                        AddressMatchesLittleEndian(radioInfo.address,
                            hostMacLittleEndian);
                    if (matchingRadio && TryEnableHidService(
                            radioHandle, targetDeviceAddress, fallbackName))
                        return true;

                    CloseHandle(radioHandle);
                    radioHandle = IntPtr.Zero;
                    if (!BluetoothFindNextRadio(radioFindHandle, ref radioHandle))
                        radioHandle = IntPtr.Zero;
                }
            } finally {
                if (radioHandle != IntPtr.Zero)
                    CloseHandle(radioHandle);
                BluetoothFindRadioClose(radioFindHandle);
            }
            return false;
        }

        private static bool TryEnableHidService(IntPtr radioHandle,
                ulong targetDeviceAddress, string fallbackName) {
            var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS {
                dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS)),
                returnAuthenticated = true,
                returnRemembered = true,
                returnUnknown = true,
                returnConnected = true,
                issueInquiry = false,
                timeoutMultiplier = 0,
                radioHandle = radioHandle,
            };
            var device = new BLUETOOTH_DEVICE_INFO {
                dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO))
            };
            IntPtr deviceFindHandle = BluetoothFindFirstDevice(ref search, ref device);
            if (deviceFindHandle == IntPtr.Zero)
                return false;

            try {
                do {
                    if (device.address != targetDeviceAddress)
                        continue;

                    if (String.IsNullOrWhiteSpace(device.name) &&
                            !String.IsNullOrWhiteSpace(fallbackName)) {
                        device.name = fallbackName;
                        BluetoothUpdateDeviceRecord(ref device);
                    }

                    Guid hidService = HumanInterfaceDeviceServiceClass;
                    uint result = BluetoothSetServiceState(radioHandle, ref device,
                        ref hidService, BluetoothServiceEnable);
                    // Windows reports E_INVALIDARG when enabling an already-enabled service.
                    // Accept that only for a record Windows already calls remembered/authenticated;
                    // the same code from an unknown record is still a real failure.
                    return result == ErrorSuccess ||
                        (result == ErrorInvalidArgumentHresult &&
                            (device.remembered || device.authenticated));
                } while (ResetDeviceInfoAndFindNext(deviceFindHandle, ref device));
            } finally {
                BluetoothFindDeviceClose(deviceFindHandle);
            }
            return false;
        }

        private static bool ResetDeviceInfoAndFindNext(IntPtr findHandle,
                ref BLUETOOTH_DEVICE_INFO device) {
            device = new BLUETOOTH_DEVICE_INFO {
                dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO))
            };
            return BluetoothFindNextDevice(findHandle, ref device);
        }

        private static ulong AddressValue(byte[] normalOrderAddress) {
            ulong value = 0;
            for (int i = 0; i < normalOrderAddress.Length; i++)
                value = (value << 8) | normalOrderAddress[i];
            return value;
        }

        private static bool AddressMatchesLittleEndian(ulong address,
                byte[] littleEndianAddress) {
            for (int i = 0; i < littleEndianAddress.Length; i++) {
                if ((byte)(address >> (8 * i)) != littleEndianAddress[i])
                    return false;
            }
            return true;
        }

        // Roll back only a key this pairing attempt created and only if it is still byte-for-byte
        // identical. That prevents a failed controller-side 0x0A write from leaving an unusable
        // Windows bond without ever deleting a pre-existing or concurrently replaced key.
        internal static void RemoveClassicLinkKeyIfMatches(byte[] hostMacLittleEndian,
                byte[] deviceMac, byte[] expectedKey) {
            if (hostMacLittleEndian == null || hostMacLittleEndian.Length != 6 ||
                    deviceMac == null || deviceMac.Length != 6 ||
                    expectedKey == null || expectedKey.Length != 16)
                return;

            byte[] hostMac = ReverseAddress(hostMacLittleEndian);
            string adapterName = MacRegistryName(hostMac);
            string deviceName = MacRegistryName(deviceMac);
            try {
                using (RegistryKey localMachine = OpenLocalMachine())
                using (RegistryKey adapterKey = localMachine.OpenSubKey(
                        BluetoothKeysRegistryPath + "\\" + adapterName, true)) {
                    byte[] stored = adapterKey?.GetValue(deviceName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                    if (!ByteArraysEqual(stored, expectedKey))
                        return;
                    adapterKey.DeleteValue(deviceName, false);
                    adapterKey.Flush();
                }
            } catch (UnauthorizedAccessException) {
            } catch (System.Security.SecurityException) {
            } catch (System.IO.IOException) {
            }
        }

        private static bool TryStoreClassicLinkKey(byte[] hostMacLittleEndian,
                byte[] deviceMac, byte[] proposedKey, out byte[] effectiveKey,
                out bool created) {
            effectiveKey = null;
            created = false;
            byte[] hostMac = ReverseAddress(hostMacLittleEndian);
            string adapterName = MacRegistryName(hostMac);
            string deviceName = MacRegistryName(deviceMac);
            try {
                using (RegistryKey localMachine = OpenLocalMachine())
                using (RegistryKey adapterKey = localMachine.CreateSubKey(
                        BluetoothKeysRegistryPath + "\\" + adapterName)) {
                    if (adapterKey == null)
                        return false;

                    byte[] existing = adapterKey.GetValue(deviceName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                    if (existing != null && existing.Length == 16) {
                        effectiveKey = (byte[])existing.Clone();
                        return true;
                    }

                    adapterKey.SetValue(deviceName, proposedKey, RegistryValueKind.Binary);
                    adapterKey.Flush();
                    effectiveKey = (byte[])proposedKey.Clone();
                    created = true;
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

        private static List<byte[]> GetLocalRadioAddressesLittleEndian() {
            var addresses = new List<byte[]>();
            var searchParams = new BLUETOOTH_FIND_RADIO_PARAMS {
                dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS))
            };
            IntPtr radioHandle = IntPtr.Zero;
            IntPtr searchHandle = BluetoothFindFirstRadio(ref searchParams, ref radioHandle);
            if (searchHandle == IntPtr.Zero)
                return addresses;

            try {
                while (radioHandle != IntPtr.Zero) {
                    var info = new BLUETOOTH_RADIO_INFO {
                        dwSize = Marshal.SizeOf(typeof(BLUETOOTH_RADIO_INFO))
                    };
                    if (BluetoothGetRadioInfo(radioHandle, ref info) == 0) {
                        byte[] address = new byte[6];
                        for (int i = 0; i < address.Length; i++)
                            address[i] = (byte)(info.address >> (8 * i));
                        addresses.Add(address);
                    }

                    CloseHandle(radioHandle);
                    if (!BluetoothFindNextRadio(searchHandle, ref radioHandle))
                        radioHandle = IntPtr.Zero;
                }
            } finally {
                if (radioHandle != IntPtr.Zero)
                    CloseHandle(radioHandle);
                BluetoothFindRadioClose(searchHandle);
            }
            return addresses;
        }

        private static RegistryKey OpenLocalMachine() {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem
                    ? RegistryView.Registry64 : RegistryView.Registry32);
        }

        private static byte[] ReverseAddress(byte[] littleEndian) {
            byte[] normal = new byte[littleEndian.Length];
            for (int i = 0; i < normal.Length; i++)
                normal[i] = littleEndian[normal.Length - 1 - i];
            return normal;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right) {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
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
