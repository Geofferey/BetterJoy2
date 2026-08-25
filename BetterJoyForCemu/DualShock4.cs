using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // DualShock4Controller : Controller - a real sibling class, built the same way
    // DualSenseController was (see DOCS/CONTROLLERS-REFACTOR.md and DOCS/DUALSENSE.md). Baseline
    // scope only, matching how DualSense itself started (5e7355b, "Add baseline DualSense
    // support: buttons, sticks, and triggers"): buttons, sticks, analog triggers, and battery.
    // Rumble, lightbar, touchpad, and gyro/accel are deliberately out of scope for this pass -
    // add them the same incremental way DualSense's later phases did, once this baseline is
    // confirmed correct against real hardware.
    //
    // Byte offsets below follow the classic DualShock 4 HID report layout - the single most
    // independently re-verified third-party controller format in existence (DS4Windows, the
    // Linux kernel's hid-sony driver, and a decade of other open-source implementations all
    // agree on it), unlike DualSense's report, which this codebase's own DualSense.cs found to
    // genuinely diverge from every secondhand reference on real hardware. That agreement is
    // still a starting point, not a guarantee for this specific unit/firmware - see
    // LogDualShock4RawDump below, the same raw-hex-dump diagnostic pattern DualSense.cs used to
    // find and fix its own real offset mistakes.
    public class DualShock4Controller : Controller {
        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;
        public override bool HasGyro => false; // deferred - see class comment
        public override bool HasAnalogTriggers => true;
        public override bool UsesNintendoProtocol => false;
        public override ControllerKind Kind => ControllerKind.DualShock4;

        public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2
        protected override byte[] TriggerVal => triggerVal;

        // USB report is 64 bytes (report ID 0x01). Bluetooth's extended/"full" report (report ID
        // 0x11) is commonly documented at 78 bytes, matching DualSense's own report length on
        // both transports - plausible given DualSense's protocol is DS4's direct descendant, but
        // unconfirmed against real hardware for DS4 specifically. If Bluetooth reports come back
        // a different length than 78, that's the first thing to check - see ReceiveRaw.
        private const int DualShock4MaxReportLen = 78;
        private long lastDualShock4RawDumpTimestamp = 0;

        public DualShock4Controller(IntPtr handle_, string path, string serialNum, int id = 0) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            rumble_obj = new Rumble(new float[] { 0, 0, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            // Single-unit device, same "primary/solo" convention every non-Joy-Con device uses -
            // see Controller.isLeft's own comment and DualSenseController's identical field.
            isLeft = true;

            PadId = id;
            // Re-derived every packet from actual report length (see ReceiveRaw), matching
            // DualSenseController - the Joy-Con-only placeholder-serial heuristic doesn't apply.
            isUSB = false;
            this.path = path;

            RefreshGyroOnlyButtonReservations();

            connection = isUSB ? 0x01 : 0x02;
        }

        public override int Attach() {
            state = state_.ATTACHED;

            HIDapi.hid_set_nonblocking(handle, 1);

            // DS4 has no SPI factory calibration to read (same situation DualSense is in) -
            // stick_cal/stick2_cal would otherwise be left at their class defaults and
            // CenterSticks would divide by that zero the moment it's used. Seed an identity
            // calibration matching DS4's real raw domain (bytes 0-255, center 128); any stored
            // user recalibration (CalibrationState, via the existing wizard) overlays on top
            // exactly the way it already does for Joy-Con/DualSense.
            stick_cal[0] = 127; stick_cal[1] = 127;   // max above center (X, Y)
            stick_cal[2] = 128; stick_cal[3] = 128;   // center (X, Y)
            stick_cal[4] = 128; stick_cal[5] = 128;   // min below center (X, Y)
            stick2_cal[0] = 127; stick2_cal[1] = 127;
            stick2_cal[2] = 128; stick2_cal[3] = 128;
            stick2_cal[4] = 128; stick2_cal[5] = 128;
            deadzone = 8;
            deadzone2 = 8;
            getActiveStickData();

            form.AppendTextBox("DualShock 4 attached (baseline mode).\r\n");
            return 0;
        }

        private static readonly ConcurrentQueue<string> dualShock4RawDumpQueue = new ConcurrentQueue<string>();
        private static int dualShock4RawDumpWriterStarted;

        // Same async queue + background-writer pattern as DualSense's LogDualSenseRawDump - can't
        // block a controller's own Poll thread on file I/O. Gated behind DualShock4DebugLogging
        // (App.config, default off) so this doesn't write continuously for every user, only when
        // actually troubleshooting a real-hardware offset mismatch.
        internal void LogDualShock4RawDump(string message) {
            if (!Boolean.Parse(ConfigurationManager.AppSettings["DualShock4DebugLogging"]))
                return;

            if (Interlocked.CompareExchange(ref dualShock4RawDumpWriterStarted, 1, 0) == 0) {
                new Thread(DualShock4RawDumpWriterLoop) {
                    IsBackground = true,
                    Name = "DualShock4RawDumpWriter"
                }.Start();
            }
            dualShock4RawDumpQueue.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, serial_number, message));
        }

        private static void DualShock4RawDumpWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "dualshock4_raw_debug.log");
            while (true) {
                Thread.Sleep(250);
                if (dualShock4RawDumpQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (dualShock4RawDumpQueue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch {
                    // Diagnostic only: never let an unavailable log path affect controller I/O.
                }
            }
        }

        protected override int ReceiveRaw() {
            if (handle == IntPtr.Zero) return -2;

            byte[] buf = new byte[DualShock4MaxReportLen];
            int ret = HIDapi.hid_read_timeout(handle, buf, new UIntPtr((uint)DualShock4MaxReportLen), 5);

            // Packet LENGTH does not reliably indicate transport for this device the way it does
            // for DualSense - confirmed on real hardware: a real capture (dualshock4_raw_debug.log)
            // showed ret==78 with byte 0 still the USB-style single-byte report ID (0x01), not the
            // Bluetooth extended report's 0x11 - Windows/hidapi apparently pads this connection's
            // reads to the full 78-byte buffer regardless of the native report's real length.
            // Trusting ret==64-means-USB against that produced a 2-byte read misalignment that
            // explained three symptoms at once on real hardware: the right stick's data landing in
            // the left stick's output slot (both axes shifted by exactly one stick's worth of
            // bytes), the trigger analog values landing on button-region bytes, and a trigger press
            // appearing to also toggle the guide/PS button. The report-ID byte itself is the actual
            // signal, not length - checked directly below instead.
            if (ret == 64 || ret == 78) {
                byte reportId = buf[0];
                // 0x01: USB-style framing, single report-ID byte, real data starts at byte 1 -
                // confirmed against real hardware (see above). 0x11 is the Bluetooth extended
                // report's documented ID; its 2-byte-header offset (o=3) is not yet confirmed
                // against real BT hardware the way the 0x01 case now is - if BT data looks wrong
                // (sticks not centered near 128, dpad not reading neutral at rest), check
                // dualshock4_raw_debug.log for what reportId actually arrives as over BT.
                isUSB = reportId == 0x01;
                connection = isUSB ? 0x01 : 0x02;
                int reportOffset = isUSB ? 1 : 3;

                // Same "TEMPORARY diagnostic" pattern DualSense.cs used to find its own real
                // offset mistakes - dump raw bytes to a file instead of guessing twice. Throttled
                // to ~4/sec. Remove once ParseDualShock4Report's offsets are confirmed correct
                // against real hardware.
                long nowTicks = Stopwatch.GetTimestamp();
                if ((nowTicks - lastDualShock4RawDumpTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                    lastDualShock4RawDumpTimestamp = nowTicks;
                    var hex = new StringBuilder();
                    for (int i = 0; i < ret; i++)
                        hex.Append(buf[i].ToString("X2")).Append(' ');
                    LogDualShock4RawDump("DS4 raw[" + ret + "]: " + hex.ToString());
                }

                ParseDualShock4Report(buf, reportOffset);
                DoThingsWithButtons();

                if (out_xbox != null) {
                    try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch (Exception) { }
                }
                return ret;
            }

            // An unexpected length means the report stream isn't what this parser expects -
            // matches DualSenseController's own handling, so a genuinely broken connection still
            // reaches DROPPED instead of sitting as a stale, frozen "connected" entry.
            if (ret > 0)
                return -1;
            return ret; // 0 = timeout, <0 = read error - Poll()'s state machine already handles both
        }

        // DualShock 4 baseline report parsing - buttons/sticks/triggers/battery only. o is the
        // transport-skip byte count from ReceiveRaw (1 USB / 3 BT-unverified). Populates the same
        // buttons[]/stick[]/stick2[]/triggerVal[] fields every controller populates, so no
        // DualShock4-specific code is needed downstream beyond the analog-trigger branch
        // MapToXbox360Input already gates on HasAnalogTriggers.
        private void ParseDualShock4Report(byte[] r, int o) {
            // Classic DS4 field order: left stick X/Y, right stick X/Y, three button bytes (dpad +
            // face buttons, shoulder/stick-click/Share/Options, PS/touchpad-click/counter), then
            // L2/R2 analog. This is the well-established public layout, not yet cross-checked
            // against a real hardware capture the way DualSense's byte offsets were - see class
            // comment. AddStickSample/CenterSticks/Y-inversion mirror DualSenseController exactly.
            UInt16[] stickRaw = { r[0 + o], r[1 + o] };
            CalibrationState.AddStickSample(this, false, stickRaw[0], stickRaw[1]);
            float[] stickResult = CenterSticks(stickRaw, stick_cal, deadzone,
                float.Parse(ConfigurationManager.AppSettings["StickScalingFactor"]));
            stick[0] = stickResult[0];
            stick[1] = -stickResult[1];

            UInt16[] stick2Raw = { r[2 + o], r[3 + o] };
            CalibrationState.AddStickSample(this, true, stick2Raw[0], stick2Raw[1]);
            float[] stick2Result = CenterSticks(stick2Raw, stick2_cal, deadzone2,
                float.Parse(ConfigurationManager.AppSettings["StickScalingFactor2"]));
            stick2[0] = stick2Result[0];
            stick2[1] = -stick2Result[1];

            int buttonFieldBase = 4;

            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i)
                        down_[i] = buttons[i];
                }
                bool[] b = new bool[20];

                byte btn1 = r[buttonFieldBase + o];
                b[(int)Button.X] = (btn1 & 0x80) != 0; // Triangle
                b[(int)Button.A] = (btn1 & 0x40) != 0; // Circle
                b[(int)Button.B] = (btn1 & 0x20) != 0; // Cross
                b[(int)Button.Y] = (btn1 & 0x10) != 0; // Square

                int dpad = btn1 & 0x0F;
                b[(int)Button.DPAD_UP] = dpad == 0 || dpad == 1 || dpad == 7;
                b[(int)Button.DPAD_RIGHT] = dpad == 1 || dpad == 2 || dpad == 3;
                b[(int)Button.DPAD_DOWN] = dpad == 3 || dpad == 4 || dpad == 5;
                b[(int)Button.DPAD_LEFT] = dpad == 5 || dpad == 6 || dpad == 7;

                byte btn2 = r[buttonFieldBase + 1 + o];
                b[(int)Button.STICK2] = (btn2 & 0x80) != 0;      // R3
                b[(int)Button.STICK] = (btn2 & 0x40) != 0;       // L3
                b[(int)Button.PLUS] = (btn2 & 0x20) != 0;        // Options
                b[(int)Button.MINUS] = (btn2 & 0x10) != 0;       // Share
                b[(int)Button.SHOULDER2_2] = (btn2 & 0x08) != 0; // R2 (digital click)
                b[(int)Button.SHOULDER_2] = (btn2 & 0x04) != 0;  // L2 (digital click)
                b[(int)Button.SHOULDER2_1] = (btn2 & 0x02) != 0; // R1
                b[(int)Button.SHOULDER_1] = (btn2 & 0x01) != 0;  // L1

                byte btn3 = r[buttonFieldBase + 2 + o];
                b[(int)Button.HOME] = (btn3 & 0x01) != 0; // PS button
                // Touchpad click intentionally unmapped this milestone (out of scope); SL/SR have
                // no DualShock 4 equivalent, left false.

                buttons = b;
                CommitButtonState();
            }

            // Classic layout: L2 analog immediately follows the button bytes, R2 right after.
            triggerVal[0] = r[buttonFieldBase + 3 + o];
            triggerVal[1] = r[buttonFieldBase + 4 + o];

            // Classic byte 30 (battery/cable-state): low nibble is a coarse 0-10ish level, bit 4
            // is the USB-cable-connected flag. Halved to match GetBatteryColor's existing 0-4
            // scale, same as DualSenseController's own coarser battery nibble.
            byte batteryByte = r[29 + o];
            int rawLevel = batteryByte & 0x0F;
            int newBattery = battery;
            battery = Math.Min(4, rawLevel / 2);
            if (newBattery != battery)
                BatteryChanged();
        }
    }
}
