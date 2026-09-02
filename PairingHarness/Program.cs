using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterJoyForCemu {
    // Standalone diagnostic: runs the automatic-pairing write sequence in isolation - no Poll
    // thread, no scanning, no virtual controller, no HidHide - so a drop can be attributed to
    // Windows/firmware timing instead of something BetterJoy's own runtime is doing concurrently.
    //
    // The default sequence and byte layouts below match DualSense.cs/BluetoothRadio.cs as
    // committed at b7d0efa exactly (see BluetoothRadioSnapshot.cs's header for why this keeps a
    // frozen copy instead of referencing the live BetterJoyForCemu assembly).
    //
    // Built as a named, reorderable step pipeline on purpose: Sequence below is the only place
    // that needs editing to try a different order, add a delay (Wait(ms)), or splice in an
    // optional step like SendPowerOffToController - no other code changes needed for that kind of
    // experiment.
    static class Program {
        const ushort SonyVendorId = 0x054C;
        const ushort DualSenseProductId = 0x0CE6;
        // Confirmed on real hardware: a DualSense Edge enumerates under this different product ID
        // instead - same constant BetterJoyForCemu's own CheckForNewControllers checks
        // (product_dualsense_edge). Both are otherwise identical for this harness's purposes.
        const ushort DualSenseEdgeProductId = 0x0DF2;

        const byte PairingInfoReportId = 0x09;
        const int PairingInfoReportLen = 20;
        const int PairingHostAddressOffset = 10;

        const byte SetPairingReportId = 0x0A;
        const int SetPairingReportLen = 27;

        const byte BluetoothControlReportId = 0x08;
        const int BluetoothControlReportLen = 47;
        const byte BluetoothControlOn = 0x01;
        const byte BluetoothControlOff = 0x02;

        const int PairingRecordCommitTimeoutMs = 3000;
        const int PairingRecordPollIntervalMs = 50;
        const int FinalizationTimeoutMs = 10000;

        static StreamWriter logFile;

        // Shared state steps read/write as the pipeline runs. Every field here is set by some
        // step and consumed by a later one - see each step's own comment for which.
        class Ctx {
            public string UsbPath;
            public IntPtr Handle = IntPtr.Zero;
            public byte[] ControllerMac;
            public byte[] HostMacLittleEndian;
            public byte[] LinkKey;
            public bool BondCreated;
            public bool ConnectRequested;
            public bool FinalizeCompleted;
            // Set false by any step that hits an unrecoverable failure - checked between every
            // step in the pipeline, same early-return-on-failure behavior the real app's
            // PerformAutomaticBluetoothPairing has, just expressed as a flag instead of nested
            // returns since steps are no longer one long method.
            public bool Ok = true;
        }

        // (string, Action<Ctx>) tuples would need the System.ValueTuple package this small
        // project deliberately doesn't reference (see PairingHarness.csproj) - a plain struct
        // does the same job without adding a dependency.
        struct Step {
            public readonly string Name;
            public readonly Action<Ctx> Run;
            public Step(string name, Action<Ctx> run) { Name = name; Run = run; }
        }

        // The default sequence, matching b7d0efa exactly (bond create/reuse, clear/write/reassert
        // with readback verification, connect trigger, USB step-off, BluetoothSetServiceState
        // finalization, then the post-finalization USB reassert of the FINAL key) followed by
        // active-read Bluetooth monitoring (added after the isolated-harness test showed a
        // read-nothing monitor drops faster than the real app does - see MonitorBluetoothConnection's
        // own comment). Reorder, delete, or insert Wait(ms)/SendPowerOffToController/etc. here to
        // try something different - nothing else needs to change for that.
        static readonly Step[] Sequence = {
            new Step("WaitForUsbController", WaitForUsbController),
            new Step("ReadControllerMac", ReadControllerMac),
            new Step("GetOrCreateBond", GetOrCreateBond),
            new Step("ClearPreviousBonds", ClearPreviousBond),
            new Step("WriteBond", WriteBond),
            //new Step("ReassertBond", ReassertBond),
            new Step("SendConnectTrigger", SendConnectTrigger),
			//new Step("ReassertBond", ReassertBond),
			//new Step("PressPSButton", PowerOffController),
			new Step("PowerOffController", PowerOffController),
            new Step("FinalizeHidService", FinalizeHidService),

        };

        static void Main() {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "pairing_harness_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            using (logFile = new StreamWriter(logPath, false) { AutoFlush = true }) {
                Log("=== DualSense Bluetooth pairing test harness (based on b7d0efa) ===");
                Log("Log: " + logPath);
                Log("Note: if the wired DualSense is never found, check it isn't HidHide-hidden - " +
                    "PairingHarness.exe needs to be on HidHide's whitelist (or BetterJoy fully quit).");

                var ctx = new Ctx();
                try {
                    foreach (Step step in Sequence) {
                        if (!ctx.Ok) {
                            Log("Stopping before step '" + step.Name + "' - a previous step failed.");
                            break;
                        }
                        Log("-- Step: " + step.Name + " --");
                        step.Run(ctx);
                    }
                } finally {
                    if (ctx.Handle != IntPtr.Zero)
                        HIDapi.hid_close(ctx.Handle);
                    if (ctx.LinkKey != null)
                        Array.Clear(ctx.LinkKey, 0, ctx.LinkKey.Length);
                }
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

        // === Pipeline steps - each is a self-contained, independently named/reorderable unit ===

        // Polls for the wired DualSense instead of requiring it already be plugged in before
        // launch. Ctrl+C to give up (no cap - see MonitorBluetoothConnection's own comment on why
        // capped waits are exactly the thing this harness exists to avoid).
        static void WaitForUsbController(Ctx ctx) {
            bool printedWaitingMessage = false;
            while (true) {
                string path = FindUsbDualSensePath();
                if (path != null) {
                    ctx.UsbPath = path;
                    break;
                }
                if (!printedWaitingMessage) {
                    Log("No wired DualSense found yet - waiting for one (plug it in now, Ctrl+C to give up)...");
                    printedWaitingMessage = true;
                }
                Thread.Sleep(500);
            }
            Log("Found wired DualSense: " + ctx.UsbPath);

            ctx.Handle = HIDapi.hid_open_path(ctx.UsbPath);
            if (ctx.Handle == IntPtr.Zero) {
                Log("Could not open the wired HID interface.");
                ctx.Ok = false;
            }
        }

        // Bytes 1-6 of feature report 0x09 are the controller's own MAC, stored in reverse byte
        // order relative to normal MAC notation - matches DualSense.cs's
        // ReassertBluetoothPairingStateOverUsb, which compares this same field against
        // PadMacAddress reversed.
        static void ReadControllerMac(Ctx ctx) {
            byte[] report = new byte[PairingInfoReportLen];
            report[0] = PairingInfoReportId;
            int received = HIDapi.hid_get_feature_report(ctx.Handle, report, new UIntPtr((uint)report.Length));
            if (received < 7) {
                Log("Could not read the controller's own MAC from feature report 0x09.");
                ctx.Ok = false;
                return;
            }

            ctx.ControllerMac = new byte[6];
            for (int i = 0; i < 6; i++)
                ctx.ControllerMac[i] = report[1 + (5 - i)];
            Log("Controller MAC: " + BitConverter.ToString(ctx.ControllerMac).Replace("-", ""));
        }

        static void GetOrCreateBond(Ctx ctx) {
            if (!BluetoothRadio.TryGetOrCreateClassicPairing(ctx.ControllerMac,
                    out byte[] hostMacLittleEndian, out byte[] linkKey, out bool created)) {
                Log("Could not access a local Bluetooth radio or its Windows bond store.");
                ctx.Ok = false;
                return;
            }
            ctx.HostMacLittleEndian = hostMacLittleEndian;
            ctx.LinkKey = linkKey;
            ctx.BondCreated = created;
            Log("Windows classic bond: " + (created ? "created new" : "reused existing") +
                ", host=" + BitConverter.ToString(hostMacLittleEndian).Replace("-", ""));
        }

        static void ClearPreviousBond(Ctx ctx) {
            byte[] emptyHost = new byte[6];
            byte[] emptyKey = new byte[16];
            bool cleared = SendPairingReport(ctx.Handle, emptyHost, emptyKey);
            if (cleared)
                cleared = WaitForPairingHost(ctx.Handle, emptyHost, PairingRecordCommitTimeoutMs);
            Log("Clear previous bond: " + (cleared ? "OK" : "FAILED"));
            if (!cleared) {
                if (ctx.BondCreated)
                    BluetoothRadio.RemoveClassicLinkKeyIfMatches(ctx.HostMacLittleEndian, ctx.ControllerMac, ctx.LinkKey);
                ctx.Ok = false;
            }
        }

        // Not wired into Sequence - place it yourself, in place of ClearPreviousBond above.
        // "Seek and destroy" version: ClearPreviousBond only clears the CONTROLLER's own onboard
        // pairing record (report 0x0A). This also clears WINDOWS' side first - every trace of a
        // bond to this device, not just whatever TryGetOrCreateClassicPairing's single
        // GetOrCreateBond call happened to find. Motivated by real hardware testing: Windows
        // Bluetooth Settings sometimes shows only one bonded entry for a device that actually has
        // two underlying records - delete the visible one, restart the radio (Settings > toggle
        // Bluetooth off/on), and a second one appears to delete too. Calls the real
        // BluetoothRemoveDevice API (goes through the actual Bluetooth stack, not just registry
        // bytes - the likely reason a manual radio toggle was needed before) and sweeps every
        // adapter subkey under the registry's Keys tree for anything left over, not just one.
        // Runs before GetOrCreateBond has resolved a host/key for this attempt, so it needs
        // ctx.ControllerMac only - nothing else from later steps.
        static void ClearPreviousBonds(Ctx ctx) {
            BluetoothRadio.RemoveAllBondsForDevice(ctx.ControllerMac,
                out bool removedViaApi, out uint apiResult, out int registryRemovals);
            Log(string.Format(
                "ClearPreviousBonds: BluetoothRemoveDevice={0} (0x{1:X8}), registry entries removed={2}",
                removedViaApi, apiResult, registryRemovals));

            byte[] emptyHost = new byte[6];
            byte[] emptyKey = new byte[16];
            bool cleared = SendPairingReport(ctx.Handle, emptyHost, emptyKey);
            if (cleared)
                cleared = WaitForPairingHost(ctx.Handle, emptyHost, PairingRecordCommitTimeoutMs);
            Log("Clear controller's own record: " + (cleared ? "OK" : "FAILED"));
            if (!cleared)
                ctx.Ok = false;
        }

        static void WriteBond(Ctx ctx) {
            bool wrote = SendPairingReport(ctx.Handle, ctx.HostMacLittleEndian, ctx.LinkKey);
            if (wrote)
                wrote = WaitForPairingHost(ctx.Handle, ctx.HostMacLittleEndian, PairingRecordCommitTimeoutMs);
            Log("Write new bond: " + (wrote ? "OK" : "FAILED"));
            if (!wrote) {
                if (ctx.BondCreated)
                    BluetoothRadio.RemoveClassicLinkKeyIfMatches(ctx.HostMacLittleEndian, ctx.ControllerMac, ctx.LinkKey);
                ctx.Ok = false;
            }
        }

        static void ReassertBond(Ctx ctx) {
            bool reasserted = SendPairingReport(ctx.Handle, ctx.HostMacLittleEndian, ctx.LinkKey);
            if (reasserted)
                reasserted = WaitForPairingHost(ctx.Handle, ctx.HostMacLittleEndian, PairingRecordCommitTimeoutMs);
            Log("Reassert bond: " + (reasserted ? "OK" : "FAILED"));
            if (!reasserted)
                ctx.Ok = false;
        }

        static void SendConnectTrigger(Ctx ctx) {
            ctx.ConnectRequested = SendBluetoothControlReport(ctx.Handle, false, BluetoothControlOn);
            Log("Connect trigger (0x08/ON): " + (ctx.ConnectRequested ? "sent" : "FAILED to send"));
            if (!ctx.ConnectRequested)
                ctx.Ok = false;
        }

        // Not wired into Sequence - place it yourself. Theory (confirmed against official PS5
        // pairing behavior): once the bond is written, the controller goes to low power and
        // correctly STAYS there - not a bug - until a PS press wakes it. Waits for a
        // release-to-press edge over ctx.Handle (or, if the interface was already idle when this
        // step started, treats the first report after that idle stretch as the wake itself), then
        // sends the connect trigger only once woken - same detection shape as DualSense.cs's own
        // MonitorChargeOnlyUsbWake. No cap - Ctrl+C to give up.
        static void PressPSButton(Ctx ctx) {
            if (ctx.Handle == IntPtr.Zero) {
                Log("PressPSButton: no open handle.");
                return;
            }

            Log("Waiting for a PS button press to wake the controller (Ctrl+C to give up)...");
            bool sawPsReleased = false;
            bool dormant = false;
            long quietSinceTicks = Environment.TickCount;
            long lastHeartbeatTicks = quietSinceTicks;
            byte[] report = new byte[64];

            while (true) {
                int received = HIDapi.hid_read_timeout(ctx.Handle, report,
                    new UIntPtr((uint)report.Length), 100);
                if (received < 0) {
                    Log("PressPSButton: read failed, handle went invalid.");
                    return;
                }

                long now = Environment.TickCount;
                if (received == 0) {
                    if ((now - quietSinceTicks) >= 750)
                        dormant = true;
                } else if (received != 64 || report[0] != 0x01) {
                    quietSinceTicks = now;
                } else {
                    bool psPressed = (report[10] & 0x01) != 0;
                    if (!psPressed)
                        sawPsReleased = true;

                    bool wakeRequested = dormant || (sawPsReleased && psPressed);
                    quietSinceTicks = now;
                    if (wakeRequested) {
                        Log("PS wake edge detected - sending connect trigger.");
                        ctx.ConnectRequested = SendBluetoothControlReport(ctx.Handle, false, BluetoothControlOn);
                        Log("Connect trigger (0x08/ON): " + (ctx.ConnectRequested ? "sent" : "FAILED to send"));
                        return;
                    }
                }

                if ((now - lastHeartbeatTicks) >= 30000) {
                    lastHeartbeatTicks = now;
                    Log("Still waiting for PS press...");
                }
            }
        }

        // The real app's synchronous USB step-off: nothing further touches this handle after the
        // connect trigger, matching PerformAutomaticBluetoothPairing exactly.
        static void CloseUsbHandle(Ctx ctx) {
            if (ctx.Handle != IntPtr.Zero) {
                HIDapi.hid_close(ctx.Handle);
                ctx.Handle = IntPtr.Zero;
            }
            Log("USB handle closed.");
        }

        static void FinalizeHidService(Ctx ctx) {
            Log("Finalizing (BluetoothSetServiceState), up to " + FinalizationTimeoutMs + "ms...");
            long started = Environment.TickCount;
            ctx.FinalizeCompleted = BluetoothRadio.TryFinalizeClassicHidPairing(
                ctx.HostMacLittleEndian, ctx.ControllerMac, "DualSense Wireless Controller",
                FinalizationTimeoutMs);
            Log("Finalization: authenticatedHidRegistered=" + ctx.FinalizeCompleted +
                " (" + (Environment.TickCount - started) + "ms)");
        }

        // b7d0efa's QueueAutomaticBluetoothPairingFinalization: once Windows has authenticated and
        // registered the HID service, reopen a fresh USB handle on the same path (the earlier one
        // is already closed by CloseUsbHandle) and rewrite the pairing-info report once more with
        // whatever Windows settled on as the FINAL link key, in case it rotated during
        // authentication. Does nothing if finalization didn't complete.
        static void ReassertFinalKeyOverUsb(Ctx ctx) {
            if (!ctx.FinalizeCompleted) {
                Log("Skipping final key reassert (finalization did not complete).");
                return;
            }
            if (!BluetoothRadio.TryGetClassicLinkKey(ctx.HostMacLittleEndian, ctx.ControllerMac,
                    out byte[] finalLinkKey) || string.IsNullOrEmpty(ctx.UsbPath)) {
                Log("Final key reassert: could not read the final link key.");
                return;
            }

            try {
                IntPtr finalizeHandle = HIDapi.hid_open_path(ctx.UsbPath);
                if (finalizeHandle == IntPtr.Zero) {
                    Log("Final key reassert: could not reopen the USB handle.");
                    return;
                }
                try {
                    bool reasserted = SendPairingReport(finalizeHandle, ctx.HostMacLittleEndian, finalLinkKey);
                    Log("Final key reassert over USB: " + (reasserted ? "OK" : "FAILED"));
                } finally {
                    HIDapi.hid_close(finalizeHandle);
                }
            } finally {
                Array.Clear(finalLinkKey, 0, finalLinkKey.Length);
            }
        }

        // Optional step, not in the default Sequence - insert it wherever you want to try sending
        // the DualSense's own low-power/off command (report 0x08, command 0x02 - PowerOff()'s own
        // command byte) mid-experiment. Self-contained: finds whichever interface (Bluetooth
        // preferred, falls back to USB) is currently reachable and opens its own handle rather
        // than depending on another step's ctx.Handle, so it's safe to splice in anywhere.
        // bluetoothTransport (CRC32 seed 0x53) is required over an actual Bluetooth handle,
        // matching PowerOff()'s own SendBluetoothControlFeatureReport(handle, true, ...) call -
        // left off (no CRC) for a USB handle, matching every other USB report in this harness.
        static void SendPowerOffToController(Ctx ctx) {
            string btPath = FindBluetoothInterfacePath();
            string path = btPath ?? FindUsbDualSensePath();
            if (path == null) {
                Log("SendPowerOffToController: no reachable interface found.");
                return;
            }

            IntPtr handle = HIDapi.hid_open_path(path);
            if (handle == IntPtr.Zero) {
                Log("SendPowerOffToController: could not open " + path);
                return;
            }
            try {
                bool sent = SendBluetoothControlReport(handle, btPath != null, BluetoothControlOff);
                Log("SendPowerOffToController (" + (btPath != null ? "Bluetooth" : "USB") + "): " +
                    (sent ? "sent" : "FAILED to send"));
            } finally {
                HIDapi.hid_close(handle);
            }
        }

        // Same job as SendPowerOffToController above, kept as its own separately-named step (not
        // wired into Sequence - place it wherever you want to test). Self-contained: finds
        // whichever interface (Bluetooth preferred, falls back to USB) is currently reachable and
        // opens its own handle, safe to splice in anywhere.
        static void PowerOffController(Ctx ctx) {
            string btPath = FindBluetoothInterfacePath();
            string path = btPath ?? FindUsbDualSensePath();
            if (path == null) {
                Log("PowerOffController: no reachable interface found.");
                return;
            }

            IntPtr handle = HIDapi.hid_open_path(path);
            if (handle == IntPtr.Zero) {
                Log("PowerOffController: could not open " + path);
                return;
            }
            try {
                bool sent = SendBluetoothControlReport(handle, btPath != null, BluetoothControlOff);
                Log("PowerOffController (" + (btPath != null ? "Bluetooth" : "USB") + "): " +
                    (sent ? "sent" : "FAILED to send"));
            } finally {
                HIDapi.hid_close(handle);
            }
        }

        // Named so it can be inserted between any two steps in Sequence to try a specific delay -
        // e.g. ("Wait", ctx => Wait(1000)(ctx)), or just call Thread.Sleep directly inline if a
        // one-off delay doesn't need its own named entry.
        static Action<Ctx> Wait(int milliseconds) {
            return ctx => {
                Log("Waiting " + milliseconds + "ms...");
                Thread.Sleep(milliseconds);
            };
        }

        // First waits for the Bluetooth HID interface to be enumerable at all, but once it
        // appears, opens it and actively reads input reports from it - matching what
        // Controller.Poll() actually does in the real app - instead of only checking existence via
        // hid_enumerate. That distinction turned out to matter: an isolated run that never
        // generates real HID traffic dropped consistently around 8-10s, while the full app (whose
        // Poll loop reads continuously once it adopts a controller) has seen connections hold far
        // longer. Bluetooth HID commonly has an idle/sniff-mode disconnect if nothing's actually
        // exchanging data - this tests whether active reads are what's keeping the full app's
        // connections alive past where this harness used to die.
        //
        // No cap - keeps waiting/monitoring indefinitely. A capped wait was the exact bug this
        // harness exists to avoid one level up (see WaitForUsbController's own history): a soak
        // test that gives up after some arbitrary duration can't tell "still fine" apart from "we
        // stopped watching too early." Only an actual drop or Ctrl+C ends this.
        static void MonitorBluetoothConnection(Ctx ctx) {
            const int enumeratePollIntervalMs = 500;
            const int readTimeoutMs = 100;
            const int heartbeatSeconds = 30;
            const int reportBufferLen = 128;

            Log("Waiting for the Bluetooth HID interface to appear...");
            string btPath = null;
            while (btPath == null) {
                btPath = FindBluetoothInterfacePath();
                if (btPath == null)
                    Thread.Sleep(enumeratePollIntervalMs);
            }
            Log("Bluetooth HID interface appeared: " + btPath);

            IntPtr handle = HIDapi.hid_open_path(btPath);
            if (handle == IntPtr.Zero) {
                Log("Appeared, but could not open it for reading - falling back to " +
                    "existence-only monitoring.");
            }

            long startTicks = Environment.TickCount;
            long lastHeartbeatTicks = startTicks;
            long lastEnumerateCheckTicks = startTicks;
            byte[] reportBuffer = new byte[reportBufferLen];
            int reportsRead = 0;

            try {
                while (true) {
                    if (handle != IntPtr.Zero) {
                        // Blocks up to readTimeoutMs waiting for the controller's own next input
                        // report - continuous real HID traffic, the same shape of activity
                        // Controller.Poll()'s ReceiveRaw loop generates for every attached
                        // controller. A negative return here (as opposed to 0, meaning "no report
                        // within the timeout, still fine") is hidapi's own signal that the
                        // underlying device handle has gone bad, i.e. the connection dropped.
                        int received = HIDapi.hid_read_timeout(handle, reportBuffer,
                            new UIntPtr((uint)reportBuffer.Length), readTimeoutMs);
                        if (received > 0)
                            reportsRead++;
                        else if (received < 0) {
                            double heldSeconds = (Environment.TickCount - startTicks) / 1000.0;
                            Log(string.Format("Read failed (handle invalid) - DROPPED after " +
                                "{0:0.00}s, {1} reports read.", heldSeconds, reportsRead));
                            return;
                        }
                    } else {
                        Thread.Sleep(readTimeoutMs);
                    }

                    // Cross-check against enumeration periodically too, independent of what reads
                    // report - the same authoritative signal every earlier measurement tonight
                    // (including the app's own debug.log) used, so results stay comparable.
                    if ((Environment.TickCount - lastEnumerateCheckTicks) >= enumeratePollIntervalMs) {
                        lastEnumerateCheckTicks = Environment.TickCount;
                        if (FindBluetoothInterfacePath() == null) {
                            double heldSeconds = (Environment.TickCount - startTicks) / 1000.0;
                            Log(string.Format("No longer enumerable - DROPPED after {0:0.00}s, " +
                                "{1} reports read.", heldSeconds, reportsRead));
                            return;
                        }
                    }

                    if ((Environment.TickCount - lastHeartbeatTicks) >= heartbeatSeconds * 1000) {
                        lastHeartbeatTicks = Environment.TickCount;
                        double heldSeconds = (Environment.TickCount - startTicks) / 1000.0;
                        Log(string.Format("Still holding after {0:0}s, {1} reports read.",
                            heldSeconds, reportsRead));
                    }
                }
            } finally {
                if (handle != IntPtr.Zero)
                    HIDapi.hid_close(handle);
            }
        }

        // === Shared helpers ===

        static bool IsDualSenseProductId(ushort productId) {
            return productId == DualSenseProductId || productId == DualSenseEdgeProductId;
        }

        static string FindUsbDualSensePath() {
            IntPtr ptr = HIDapi.hid_enumerate(SonyVendorId, 0);
            IntPtr top = ptr;
            string result = null;
            try {
                while (ptr != IntPtr.Zero) {
                    var info = (HIDapi.hid_device_info)Marshal.PtrToStructure(ptr, typeof(HIDapi.hid_device_info));
                    // The wired composite HID interface; Bluetooth's carries the 00001124 GATT/
                    // BR-EDR HID service GUID instead - same distinction used throughout
                    // BetterJoy's own CheckForNewControllers.
                    if (IsDualSenseProductId(info.product_id) && !string.IsNullOrEmpty(info.path) &&
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

        static string FindBluetoothInterfacePath() {
            IntPtr ptr = HIDapi.hid_enumerate(SonyVendorId, 0);
            IntPtr top = ptr;
            string result = null;
            try {
                while (ptr != IntPtr.Zero) {
                    var info = (HIDapi.hid_device_info)Marshal.PtrToStructure(ptr, typeof(HIDapi.hid_device_info));
                    if (IsDualSenseProductId(info.product_id) && !string.IsNullOrEmpty(info.path) &&
                            info.path.IndexOf("00001124", StringComparison.OrdinalIgnoreCase) >= 0) {
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

        // bluetoothTransport adds a CRC32 (seed 0x53) over the report, required when this actually
        // goes out over a live Bluetooth handle (matches PowerOff()'s own
        // SendBluetoothControlFeatureReport(handle, true, ...) call) - left off for USB, matching
        // the automatic-pairing connect trigger's own SendBluetoothControlFeatureReport(handle,
        // false, ...) call.
        static bool SendBluetoothControlReport(IntPtr handle, bool bluetoothTransport, byte command) {
            byte[] report = new byte[BluetoothControlReportLen];
            report[0] = BluetoothControlReportId;
            report[1] = command;
            if (bluetoothTransport) {
                uint crc = Crc32(0x53, report, report.Length - 4);
                report[report.Length - 4] = (byte)crc;
                report[report.Length - 3] = (byte)(crc >> 8);
                report[report.Length - 2] = (byte)(crc >> 16);
                report[report.Length - 1] = (byte)(crc >> 24);
            }
            return HIDapi.hid_send_feature_report(handle, report, new UIntPtr((uint)report.Length)) == report.Length;
        }

        // Exact copy of Controller.cs's own Crc32/BuildCrc32Table as committed at b7d0efa - the
        // seed byte is fed through the table as the first data byte (not XORed directly into the
        // initial CRC), which matters: getting this wrong produces a CRC the controller silently
        // rejects.
        static readonly uint[] crc32Table = BuildCrc32Table();

        static uint[] BuildCrc32Table() {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        static uint Crc32(byte seed, byte[] data, int length) {
            uint crc = 0xFFFFFFFF;
            crc = crc32Table[(crc ^ seed) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < length; i++)
                crc = crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }
}
