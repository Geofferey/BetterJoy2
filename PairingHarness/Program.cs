using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // Standalone diagnostic: runs ONLY the automatic-pairing write sequence and connect trigger,
    // then closes the USB handle and does nothing else - no Poll thread, no scanning, no virtual
    // controller, no HidHide. The pairing/finalization logic itself is a frozen copy of
    // BluetoothRadio.cs as committed at d49e317 (see BluetoothRadioSnapshot.cs's header) and the
    // report byte layouts below match DualSense.cs's own SendBluetoothPairingFeatureReport/
    // SendBluetoothControlFeatureReport/WaitForBluetoothPairingHost at that same commit exactly.
    //
    // Purpose: BetterJoy's real runtime always has scanning, per-controller Poll loops, and
    // virtual-controller creation running concurrently with automatic pairing, so every real
    // hardware test tonight was confounded - a drop could be Windows/firmware timing, or it could
    // be something BetterJoy itself is doing. This tool answers that: if a Bluetooth connection
    // still dies at the same ~14-18s mark with absolutely nothing else running, that's proof it's
    // not us. If it holds here where it didn't in the full app, something in BetterJoy's normal
    // operation is the actual cause.
    static class Program {
        const ushort SonyVendorId = 0x054C;
        const ushort DualSenseProductId = 0x0CE6;

        const byte PairingInfoReportId = 0x09;
        const int PairingInfoReportLen = 20;
        const int PairingHostAddressOffset = 10;

        const byte SetPairingReportId = 0x0A;
        const int SetPairingReportLen = 27;

        const byte BluetoothControlReportId = 0x08;
        const int BluetoothControlReportLen = 47;
        const byte BluetoothControlOn = 0x01;

        const int PairingRecordCommitTimeoutMs = 3000;
        const int PairingRecordPollIntervalMs = 50;
        const int FinalizationTimeoutMs = 10000;

        static StreamWriter logFile;

        static void Main() {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "pairing_harness_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            using (logFile = new StreamWriter(logPath, false) { AutoFlush = true }) {
                Log("=== DualSense Bluetooth pairing test harness ===");
                Log("Log: " + logPath);
                RunOnce();
            }
            Console.WriteLine();
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey(true);
        }

        static void Log(string message) {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            Console.WriteLine(line);
            logFile.WriteLine(line);
        }

        static void RunOnce() {
            string usbPath = FindUsbDualSensePath();
            if (usbPath == null) {
                Log("No wired DualSense found. Plug it in over USB and re-run.");
                return;
            }
            Log("Found wired DualSense: " + usbPath);

            IntPtr handle = HIDapi.hid_open_path(usbPath);
            if (handle == IntPtr.Zero) {
                Log("Could not open the wired HID interface.");
                return;
            }

            byte[] controllerMac;
            try {
                if (!TryReadControllerMac(handle, out controllerMac)) {
                    Log("Could not read the controller's own MAC from feature report 0x09.");
                    return;
                }
                Log("Controller MAC: " + BitConverter.ToString(controllerMac).Replace("-", ""));

                if (!BluetoothRadio.TryGetOrCreateClassicPairing(controllerMac,
                        out byte[] hostMacLittleEndian, out byte[] linkKey, out bool created)) {
                    Log("Could not access a local Bluetooth radio or its Windows bond store.");
                    return;
                }
                Log("Windows classic bond: " + (created ? "created new" : "reused existing") +
                    ", host=" + BitConverter.ToString(hostMacLittleEndian).Replace("-", ""));

                try {
                    byte[] emptyHost = new byte[6];
                    byte[] emptyKey = new byte[16];

                    bool cleared = SendPairingReport(handle, emptyHost, emptyKey);
                    if (cleared)
                        cleared = WaitForPairingHost(handle, emptyHost, PairingRecordCommitTimeoutMs);
                    Log("Step 1/3 - clear previous bond: " + (cleared ? "OK" : "FAILED"));
                    if (!cleared) {
                        if (created)
                            BluetoothRadio.RemoveClassicLinkKeyIfMatches(hostMacLittleEndian, controllerMac, linkKey);
                        return;
                    }

                    bool wrote = SendPairingReport(handle, hostMacLittleEndian, linkKey);
                    if (wrote)
                        wrote = WaitForPairingHost(handle, hostMacLittleEndian, PairingRecordCommitTimeoutMs);
                    Log("Step 2/3 - write new bond: " + (wrote ? "OK" : "FAILED"));
                    if (!wrote) {
                        if (created)
                            BluetoothRadio.RemoveClassicLinkKeyIfMatches(hostMacLittleEndian, controllerMac, linkKey);
                        return;
                    }

                    bool reasserted = SendPairingReport(handle, hostMacLittleEndian, linkKey);
                    if (reasserted)
                        reasserted = WaitForPairingHost(handle, hostMacLittleEndian, PairingRecordCommitTimeoutMs);
                    Log("Step 3/3 - reassert bond: " + (reasserted ? "OK" : "FAILED"));
                    if (!reasserted)
                        return;

                    bool connectSent = SendBluetoothControlReport(handle, BluetoothControlOn);
                    Log("Connect trigger (0x08/ON): " + (connectSent ? "sent" : "FAILED to send"));
                    if (!connectSent)
                        return;

                    // Deliberately no polling loop, no Poll thread, nothing else touching this
                    // handle from here on - close it now, exactly like the real app's
                    // synchronous USB step-off, and never open it again this run.
                    HIDapi.hid_close(handle);
                    handle = IntPtr.Zero;
                    Log("USB handle closed. Nothing further touches USB this run.");

                    Log("Finalizing (BluetoothSetServiceState), up to " + FinalizationTimeoutMs + "ms...");
                    long finalizeStarted = Environment.TickCount;
                    bool completed = BluetoothRadio.TryFinalizeClassicHidPairing(
                        hostMacLittleEndian, controllerMac, "DualSense Wireless Controller",
                        FinalizationTimeoutMs);
                    Log("Finalization: " + (completed ? "authenticatedHidRegistered=True" : "authenticatedHidRegistered=False") +
                        " (" + (Environment.TickCount - finalizeStarted) + "ms)");

                    MonitorBluetoothConnection();
                } finally {
                    if (handle != IntPtr.Zero)
                        HIDapi.hid_close(handle);
                }
            } finally {
                // controllerMac has no secret material; only linkKey/emptyKey ever needed
                // clearing, already handled by the try block's own locals going out of scope -
                // nothing further to zero here.
            }
        }

        // Nothing from here on touches USB or does anything BetterJoy's real Poll/scan loops do -
        // this only asks Windows, via hid_enumerate, whether the Bluetooth HID interface is still
        // present. Runs until it disappears or a generous cap elapses.
        static void MonitorBluetoothConnection() {
            const int pollIntervalMs = 500;
            const int maxMonitorSeconds = 600;

            Log("Monitoring for the Bluetooth HID interface (no other BetterJoy code running)...");
            long? firstSeenTicks = null;
            long startTicks = Environment.TickCount;

            while ((Environment.TickCount - startTicks) < maxMonitorSeconds * 1000) {
                bool present = BluetoothInterfacePresent();
                if (present && firstSeenTicks == null) {
                    firstSeenTicks = Environment.TickCount;
                    Log("Bluetooth HID interface appeared.");
                } else if (!present && firstSeenTicks != null) {
                    double heldSeconds = (Environment.TickCount - firstSeenTicks.Value) / 1000.0;
                    Log(string.Format("Bluetooth HID interface DROPPED after {0:0.00}s.", heldSeconds));
                    return;
                }
                Thread.Sleep(pollIntervalMs);
            }

            if (firstSeenTicks == null)
                Log("Bluetooth HID interface never appeared within " + maxMonitorSeconds + "s.");
            else
                Log(string.Format("Still holding after {0}s (monitor cap reached) - looks stable.",
                    maxMonitorSeconds));
        }

        static bool BluetoothInterfacePresent() {
            IntPtr ptr = HIDapi.hid_enumerate(SonyVendorId, DualSenseProductId);
            IntPtr top = ptr;
            bool found = false;
            try {
                while (ptr != IntPtr.Zero) {
                    var info = (HIDapi.hid_device_info)Marshal.PtrToStructure(ptr, typeof(HIDapi.hid_device_info));
                    if (!string.IsNullOrEmpty(info.path) &&
                            info.path.IndexOf("00001124", StringComparison.OrdinalIgnoreCase) >= 0) {
                        found = true;
                        break;
                    }
                    ptr = info.next;
                }
            } finally {
                HIDapi.hid_free_enumeration(top);
            }
            return found;
        }

        static string FindUsbDualSensePath() {
            IntPtr ptr = HIDapi.hid_enumerate(SonyVendorId, DualSenseProductId);
            IntPtr top = ptr;
            string result = null;
            try {
                while (ptr != IntPtr.Zero) {
                    var info = (HIDapi.hid_device_info)Marshal.PtrToStructure(ptr, typeof(HIDapi.hid_device_info));
                    // The wired composite HID interface; Bluetooth's carries the 00001124 GATT/
                    // BR-EDR HID service GUID instead - same distinction used throughout
                    // BetterJoy's own CheckForNewControllers.
                    if (!string.IsNullOrEmpty(info.path) &&
                            info.path.IndexOf("MI_03", StringComparison.OrdinalIgnoreCase) >= 0) {
                        result = info.path;
                        break;
                    }
                    ptr = info.next;
                }
            } finally {
                HIDapi.hid_free_enumeration(top);
            }
            return result;
        }

        // Bytes 1-6 of feature report 0x09 are the controller's own MAC, stored in reverse byte
        // order relative to normal MAC notation - matches DualSense.cs's
        // ReassertBluetoothPairingStateOverUsb, which compares this same field against
        // PadMacAddress reversed.
        static bool TryReadControllerMac(IntPtr handle, out byte[] controllerMac) {
            controllerMac = null;
            byte[] report = new byte[PairingInfoReportLen];
            report[0] = PairingInfoReportId;
            int received = HIDapi.hid_get_feature_report(handle, report, new UIntPtr((uint)report.Length));
            if (received < 7)
                return false;

            controllerMac = new byte[6];
            for (int i = 0; i < 6; i++)
                controllerMac[i] = report[1 + (5 - i)];
            return true;
        }

        static bool SendPairingReport(IntPtr handle, byte[] hostMacLittleEndian, byte[] linkKey) {
            if (handle == IntPtr.Zero || hostMacLittleEndian == null || hostMacLittleEndian.Length != 6 ||
                    linkKey == null || linkKey.Length != 16)
                return false;

            byte[] report = new byte[SetPairingReportLen];
            report[0] = SetPairingReportId;
            Buffer.BlockCopy(hostMacLittleEndian, 0, report, 1, 6);
            Buffer.BlockCopy(linkKey, 0, report, 7, 16);
            // Bytes 23..26 are the optional CRC field - zero over USB, matching every other USB
            // feature report in the real app.
            return HIDapi.hid_send_feature_report(handle, report, new UIntPtr((uint)report.Length)) == report.Length;
        }

        static bool WaitForPairingHost(IntPtr handle, byte[] expectedHostMacLittleEndian, int timeoutMs) {
            byte[] report = new byte[PairingInfoReportLen];
            long started = Environment.TickCount;
            while (true) {
                Array.Clear(report, 0, report.Length);
                report[0] = PairingInfoReportId;
                int received = HIDapi.hid_get_feature_report(handle, report, new UIntPtr((uint)report.Length));
                if (received >= PairingHostAddressOffset + 6) {
                    bool matches = true;
                    for (int i = 0; i < 6; i++) {
                        if (report[PairingHostAddressOffset + i] != expectedHostMacLittleEndian[i]) {
                            matches = false;
                            break;
                        }
                    }
                    if (matches)
                        return true;
                }
                if ((Environment.TickCount - started) >= timeoutMs)
                    return false;
                Thread.Sleep(PairingRecordPollIntervalMs);
            }
        }

        static bool SendBluetoothControlReport(IntPtr handle, byte command) {
            byte[] report = new byte[BluetoothControlReportLen];
            report[0] = BluetoothControlReportId;
            report[1] = command;
            // bluetoothTransport=false (this always runs over USB) - no CRC, matching
            // SendBluetoothControlFeatureReport.
            return HIDapi.hid_send_feature_report(handle, report, new UIntPtr((uint)report.Length)) == report.Length;
        }
    }
}
