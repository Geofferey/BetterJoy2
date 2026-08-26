using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // DualShock4Controller : Controller - a real sibling class, built the same way
    // DualSenseController was (see DOCS/CONTROLLERS-REFACTOR.md and DOCS/DUALSENSE.md). Baseline
    // scope started by matching how DualSense itself began (5e7355b, "Add baseline DualSense
    // support: buttons, sticks, and triggers"). Gyro/accel now feeds the same shared calibrated
    // mouse/stick pipeline as DualSense, while DS4-specific report framing, timestamps, factory
    // calibration layout, sensor axes, and transport-specific lightbar output remain defined
    // here. Touchpad input feeds the shared Controller pipeline.
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
        public override bool HasGyro => true;
        public override bool HasTouchpad => true;
        public override bool HasAnalogTriggers => true;
        public override bool UsesNintendoProtocol => false;
        public override ControllerKind Kind => ControllerKind.DualShock4;
        public override string UsbAudioEndpointNameHint => "Wireless Controller";

        public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2
        protected override byte[] TriggerVal => triggerVal;

        // USB report is 64 bytes (report ID 0x01). Bluetooth's extended/"full" report (report ID
        // 0x11) is commonly documented at 78 bytes, matching DualSense's own report length on
        // both transports - plausible given DualSense's protocol is DS4's direct descendant, but
        // unconfirmed against real hardware for DS4 specifically. If Bluetooth reports come back
        // a different length than 78, that's the first thing to check - see ReceiveRaw.
        private const int DualShock4MaxReportLen = 78;
        private long lastDualShock4RawDumpTimestamp = 0;
        private long lastDualShock4ImuLogTimestamp = 0;
        private bool lightbarTransportKnown;
        private bool lightbarUpdatePending = true;
        private byte lightbarRed;
        private byte lightbarGreen;
        private byte lightbarBlue = 255;
        private long lightbarApplyEarliestTimestamp;
        private bool connectionLightFlashStarted;
        // Common DS4 status byte 0, bit 5 is the physical headphone detect signal. -1 means no
        // full report has been parsed yet, so headset-routed audio remains safely idle during
        // attach rather than assuming a jack is present.
        private int headphoneConnectionState = -1;
        public bool HeadphonesConnected => Volatile.Read(ref headphoneConnectionState) == 1;

        // DS4 carries a 16-bit sensor clock at common offsets 9-10. One tick is 16/3 microseconds,
        // as used by DS4Windows; unsigned subtraction handles the frequent 16-bit rollover.
        private ushort? lastImuHardwareTimestamp;
        private ushort lastLoggedImuDeltaTicks;
        private float measuredGyroSubSamplePeriod = ImuSamplePeriodSeconds;
        private const float MinGyroSubSamplePeriod = 0.0005f;
        private const float MaxGyroSubSamplePeriod = 0.02f;
        protected override float GyroSubSamplePeriod => measuredGyroSubSamplePeriod;
        protected override Vector3 GyroStickBiasCorrection => gyroMouseBias;
        // DS4 reports one very short IMU sample at a time. Average those samples over the same
        // 15 ms sensor window a Joy-Con report already contains, otherwise each 1-2 ms sample is
        // expanded into a complete stick update and its cross-axis sensor noise becomes visible.
        // ProcessGyroStickSample holds the last completed result between windows, so this does
        // not reduce button or physical-stick report frequency.
        protected override float GyroStickOutputWindowPeriod => GyroStickReferenceReportPeriod;

        // DS4 uses the same nominal Bosch sensor resolutions as DualSense, but its factory
        // calibration feature report groups gyro extrema differently on USB and Bluetooth.
        private const float GyroLsbPerDegPerSec = 16.0f;
        private const float AccelLsbPerG = 8192.0f;
        private short gyroPitchBias, gyroYawBias, gyroRollBias;
        private short gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus;
        private short gyroSpeedPlus, gyroSpeedMinus;
        private short accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus;
        private bool gyroCalibrationAttempted;
        private bool gyroCalibrationValid;

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

            // Sony's two modern controller generations share the same physical sensor mounting
            // and benefit from the same pitch-leak correction in Player Space. The report bytes
            // and calibration feeding that shared math remain DS4-specific below.
            gyroMousePlayerSpace.EnableExtendedAxisCorrection = true;
            gyroStickPlayerSpace.EnableExtendedAxisCorrection = true;

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

            RequestExtendedReportMode();

            form.AppendTextBox("DualShock 4 attached.\r\n");
            return 0;
        }

        // The shared HOME-long-press and inactivity paths call this virtual hook. A DS4 needs
        // the same radio-level Bluetooth disconnect as DualSense; merely marking it dropped
        // leaves the physical HID connection alive and the scanner immediately adds it again.
        // USB has no equivalent power-off command, so leave a wired controller connected.
        public override void PowerOff() {
            if (state > state_.DROPPED && !isUSB) {
                BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                state = state_.DROPPED;
            }
        }

        // Program.cs's periodic ApplyControllerProfileOptions only reaches attached controllers,
        // so a mid-stream disconnect would otherwise leave the stream worker thread parked in
        // BlockingCollection.Take forever and the input helper's WASAPI capture running with
        // nowhere for its frames to go. Stop explicitly here instead, before the handle closes.
        protected override void OnDetachingWhileAttached() {
            StopBluetoothAudioStream();
        }

        public override void SetLightColor(byte red, byte green, byte blue) {
            lightbarRed = red;
            lightbarGreen = green;
            lightbarBlue = blue;
            lightbarUpdatePending = true;
            if (lightbarTransportKnown) {
                lightbarUpdatePending = !SendDualShock4Lightbar(
                    lightbarRed, lightbarGreen, lightbarBlue);
            }
        }

        // If the same DS4 appears over USB while its Bluetooth HID connection is still present,
        // the shared MAC-based duplicate retirement stops BetterJoy from using the old entry.
        // Drop the underlying radio link as well so Windows does not rediscover that stale entry
        // on every scan. This mirrors DualSenseController's transport handoff behavior.
        protected override void OnDuplicateRetired(Controller other) {
            if (other is DualShock4Controller && isUSB && !other.isUSB) {
                bool disconnected = BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                form.AppendTextBox(disconnected
                    ? "Disconnected DualShock 4's Bluetooth link now that USB has taken over.\r\n"
                    : "Could not disconnect DualShock 4's Bluetooth link - it may keep reappearing.\r\n");
            }
        }

        // A DS4 connected over Bluetooth defaults to sending short, basic reports (still labeled
        // with report ID 0x01, the same ID USB uses) until the host signals it understands the
        // extended format - confirmed on real hardware: a raw capture showed buttons/sticks/
        // triggers working (they live in that short report) while every byte past them - battery,
        // gyro, touchpad - was constantly zero for the entire session, never populated at all, not
        // just misread. Reading feature report 0x02 once is the widely-used trick across
        // independent open-source DS4 implementations (the Linux kernel's hid-sony driver reads
        // this same report as part of its own Bluetooth bring-up) to make the controller switch to
        // sending extended 0x11 reports with the full field set. Best-effort: failure here doesn't
        // break the baseline fields already working, so errors are swallowed rather than surfaced.
        // UNVERIFIED against real hardware yet - if dualshock4_raw_debug.log still shows the
        // extended region as constant zero after this, this specific report ID/mechanism needs
        // re-checking.
        private const byte ExtendedReportModeFeatureReportId = 0x02;
        private const int ExtendedReportModeFeatureReportLen = 37;

        private void RequestExtendedReportMode() {
            try {
                byte[] buf = new byte[ExtendedReportModeFeatureReportLen];
                buf[0] = ExtendedReportModeFeatureReportId;
                HIDapi.hid_get_feature_report(handle, buf,
                    new UIntPtr((uint)ExtendedReportModeFeatureReportLen));
            } catch {
                // Best-effort - see method comment.
            }
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
                if (!lightbarTransportKnown) {
                    lightbarTransportKnown = true;
                    // A color written during the DS4's Bluetooth startup is accepted by HID but
                    // then overwritten as the controller finishes entering extended-report mode.
                    // Keep the profile color pending until that short initialization window ends.
                    lightbarApplyEarliestTimestamp = Stopwatch.GetTimestamp() +
                        Stopwatch.Frequency / 4;
                }
                if (lightbarUpdatePending) {
                    long lightbarNow = Stopwatch.GetTimestamp();
                    if (lightbarNow >= lightbarApplyEarliestTimestamp) {
                        if (!connectionLightFlashStarted) {
                            // DS4 finishes entering extended-report mode shortly after connect.
                            // Once ready, show the same blue confirmation as DualSense before
                            // applying this controller profile's assigned color.
                            if (SendDualShock4Lightbar(0, 0, 255)) {
                                connectionLightFlashStarted = true;
                                lightbarApplyEarliestTimestamp = lightbarNow +
                                    Stopwatch.Frequency / 4;
                            }
                        } else {
                            lightbarUpdatePending = !SendDualShock4Lightbar(
                                lightbarRed, lightbarGreen, lightbarBlue);
                        }
                    }
                }

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

                // Transport is only trustworthy after seeing the report ID, so defer the DS4's
                // transport-specific factory calibration read until this first valid packet.
                if (!gyroCalibrationAttempted)
                    ReadGyroCalibration();

                BeginGyroStickDiagnosticReport();
                ExtractIMUValues(buf, reportOffset);
                AccumulateGyroStickDiagnosticSample();
                DoThingsWithButtons();

                // DS4 carries one IMU sample per input report, just like DualSense.
                ProcessGyroMouseSample(true);
                ProcessGyroStickSample(true);
                RecordGyroStickDiagnosticReport(buf[6 + reportOffset], Stopwatch.GetTimestamp());

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

        // DualShock 4 report parsing for buttons/sticks/triggers/battery. o is the transport-skip
        // byte count from ReceiveRaw (1 USB / 3 BT). Populates the same
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
                bool[] b = new bool[ButtonCount];

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
                b[(int)Button.TOUCHPAD] = (btn3 & 0x02) != 0;
                // SL/SR have no DualShock 4 equivalent and remain false.

                buttons = b;
                CommitButtonState();
            }

            // DS4 common-report touch contacts begin at offsets 34 and 38. The shared pipeline
            // owns activation, pointer deltas, actions, click lockout, and output inhibition.
            SubmitTouchpadReport(ReadPackedTouchContact(r, 34 + o),
                                 ReadPackedTouchContact(r, 38 + o));

            // Classic layout: L2 analog immediately follows the button bytes, R2 right after.
            triggerVal[0] = r[buttonFieldBase + 3 + o];
            triggerVal[1] = r[buttonFieldBase + 4 + o];

            // status[0] at common-report offset 29: DS4Windows scales the low nibble against
            // 8 while wireless and 11 while a cable is connected, then clamps the result to 100.
            // Preserve that percentage and charge state while the shared Controller helper keeps
            // the legacy 0-4 DSU/color level in sync.
            byte batteryByte = r[29 + o];
            int nextHeadphoneState = (batteryByte & 0x20) != 0 ? 1 : 0;
            int previousHeadphoneState = Interlocked.Exchange(
                ref headphoneConnectionState, nextHeadphoneState);
            if (previousHeadphoneState != nextHeadphoneState) {
                // Profile reconciliation owns stream start/stop decisions. Queue it away from the
                // HID poll thread so plugging or unplugging headphones can transition immediately
                // without making input-report parsing perform capture-pipe or scan-lock work.
                ThreadPool.QueueUserWorkItem(_ => Program.mgr?.ApplyControllerProfileOptions());
            }
            int batteryPercent;
            ControllerBatteryStatus batteryState;
            DecodeBatteryStatus(batteryByte, out batteryPercent, out batteryState);
            SetBatteryStatus(batteryPercent, batteryState);
        }

        internal static void DecodeBatteryStatus(byte value, out int percent,
                                                 out ControllerBatteryStatus status) {
            int level = value & 0x0F;
            bool cableConnected = (value & 0x10) != 0;

            int maximumLevel = cableConnected ? 11 : 8;
            percent = Math.Min(level * 100 / maximumLevel, 100);
            status = cableConnected
                ? (percent >= 100 ? ControllerBatteryStatus.Full : ControllerBatteryStatus.Charging)
                : ControllerBatteryStatus.Discharging;
        }

        private const byte UsbGyroCalibrationFeatureReportId = 0x02;
        private const int UsbGyroCalibrationFeatureReportLen = 37;
        private const byte BluetoothGyroCalibrationFeatureReportId = 0x05;
        private const int BluetoothGyroCalibrationFeatureReportLen = 41;

        // Reads the DS4's factory IMU calibration after ReceiveRaw has established transport.
        // USB report 0x02 uses per-axis plus/minus pairs; Bluetooth report 0x05 groups all plus
        // values before all minus values and appends a CRC32 seeded with 0xA3.
        private void ReadGyroCalibration() {
            gyroCalibrationAttempted = true;
            bool usb = isUSB;
            int length = usb ? UsbGyroCalibrationFeatureReportLen : BluetoothGyroCalibrationFeatureReportLen;
            byte[] buf = new byte[length];
            buf[0] = usb ? UsbGyroCalibrationFeatureReportId : BluetoothGyroCalibrationFeatureReportId;

            bool verified = false;
            int attempts = usb ? 1 : 5;
            for (int attempt = 0; attempt < attempts && !verified; attempt++) {
                int ret = HIDapi.hid_get_feature_report(handle, buf, new UIntPtr((uint)length));
                if (ret < length)
                    continue;

                if (usb) {
                    verified = true;
                } else {
                    uint received = (uint)buf[37] | ((uint)buf[38] << 8) |
                                    ((uint)buf[39] << 16) | ((uint)buf[40] << 24);
                    verified = received == Crc32(0xA3, buf, length - 4);
                }
            }

            if (!verified) {
                LogGyroCalibrationFailure(attempts);
                return;
            }

            gyroPitchBias = ReadInt16(buf, 1);
            gyroYawBias = ReadInt16(buf, 3);
            gyroRollBias = ReadInt16(buf, 5);

            if (usb) {
                gyroPitchPlus = ReadInt16(buf, 7);
                gyroPitchMinus = ReadInt16(buf, 9);
                gyroYawPlus = ReadInt16(buf, 11);
                gyroYawMinus = ReadInt16(buf, 13);
                gyroRollPlus = ReadInt16(buf, 15);
                gyroRollMinus = ReadInt16(buf, 17);
            } else {
                gyroPitchPlus = ReadInt16(buf, 7);
                gyroYawPlus = ReadInt16(buf, 9);
                gyroRollPlus = ReadInt16(buf, 11);
                gyroPitchMinus = ReadInt16(buf, 13);
                gyroYawMinus = ReadInt16(buf, 15);
                gyroRollMinus = ReadInt16(buf, 17);
            }

            gyroSpeedPlus = ReadInt16(buf, 19);
            gyroSpeedMinus = ReadInt16(buf, 21);
            accelXPlus = ReadInt16(buf, 23);
            accelXMinus = ReadInt16(buf, 25);
            accelYPlus = ReadInt16(buf, 27);
            accelYMinus = ReadInt16(buf, 29);
            accelZPlus = ReadInt16(buf, 31);
            accelZMinus = ReadInt16(buf, 33);

            gyroCalibrationValid = gyroPitchPlus != gyroPitchMinus &&
                gyroYawPlus != gyroYawMinus && gyroRollPlus != gyroRollMinus &&
                accelXPlus != accelXMinus && accelYPlus != accelYMinus &&
                accelZPlus != accelZMinus && gyroSpeedPlus + gyroSpeedMinus != 0;
            if (!gyroCalibrationValid) {
                LogGyroCalibrationFailure(attempts);
                return;
            }

            LogDualShock4RawDump(string.Format(CultureInfo.InvariantCulture,
                "Gyro calibration OK ({0}): gyroBias=({1},{2},{3}) gyroPlusMinus=({4}/{5},{6}/{7},{8}/{9}) " +
                "gyroSpeedPlusMinus=({10}/{11}) accelPlusMinus=({12}/{13},{14}/{15},{16}/{17})",
                usb ? "USB" : "BT", gyroPitchBias, gyroYawBias, gyroRollBias,
                gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus,
                gyroSpeedPlus, gyroSpeedMinus,
                accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus));
        }

        private void LogGyroCalibrationFailure(int attempts) {
            gyroCalibrationValid = false;
            form.AppendTextBox("DualShock 4 gyro calibration read failed - using uncalibrated nominal scale.\r\n");
            LogDualShock4RawDump("Gyro calibration read failed after " + attempts + " attempt(s).");
        }

        private static short ReadInt16(byte[] buf, int offset) {
            return (short)(buf[offset] | (buf[offset + 1] << 8));
        }

        // DS4 common-report offsets: timestamp 9, gyro 12/14/16, accel 18/20/22. The sensors use
        // the same physical mounting as DualSense, so the final canonical axis transform matches
        // DualSense while raw layout, timing, and calibration stay local to this definition.
        private void ExtractIMUValues(byte[] r, int o) {
            EnsureGyroOrientationBasis();

            ushort hardwareTimestamp = (ushort)(r[9 + o] | (r[10 + o] << 8));
            if (lastImuHardwareTimestamp.HasValue) {
                ushort deltaTicks = unchecked((ushort)(hardwareTimestamp - lastImuHardwareTimestamp.Value));
                float measuredDt = deltaTicks * (16.0f / 3000000.0f);
                measuredGyroSubSamplePeriod = Math.Max(MinGyroSubSamplePeriod,
                    Math.Min(MaxGyroSubSamplePeriod, measuredDt));
                lastLoggedImuDeltaTicks = deltaTicks;
            }
            lastImuHardwareTimestamp = hardwareTimestamp;

            gyr_r[0] = ReadInt16(r, 12 + o); // pitch/raw channel 0
            gyr_r[1] = ReadInt16(r, 14 + o); // yaw/raw channel 1
            gyr_r[2] = ReadInt16(r, 16 + o); // roll/raw channel 2
            acc_r[0] = ReadInt16(r, 18 + o);
            acc_r[1] = ReadInt16(r, 20 + o);
            acc_r[2] = ReadInt16(r, 22 + o);

            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                CalibrationState.AddSample(this, CalibrationState.XA, CalibrationState.XG, acc_r[0], gyr_r[0]);
                CalibrationState.AddSample(this, CalibrationState.YA, CalibrationState.YG, acc_r[1], gyr_r[1]);
                CalibrationState.AddSample(this, CalibrationState.ZA, CalibrationState.ZG, acc_r[2], gyr_r[2]);
            }

            float pitchDegPerSec, yawDegPerSec, rollDegPerSec;
            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]) && activeData != null) {
                pitchDegPerSec = (gyr_r[0] - activeData[0]) / GyroLsbPerDegPerSec;
                yawDegPerSec = (gyr_r[1] - activeData[1]) / GyroLsbPerDegPerSec;
                rollDegPerSec = (gyr_r[2] - activeData[2]) / GyroLsbPerDegPerSec;
            } else {
                pitchDegPerSec = CorrectGyroSample(gyr_r[0], gyroPitchBias, gyroPitchPlus, gyroPitchMinus);
                yawDegPerSec = CorrectGyroSample(gyr_r[1], gyroYawBias, gyroYawPlus, gyroYawMinus);
                rollDegPerSec = CorrectGyroSample(gyr_r[2], gyroRollBias, gyroRollPlus, gyroRollMinus);
            }

            float accelXG = CorrectAccelSample(acc_r[0], accelXPlus, accelXMinus);
            float accelYG = CorrectAccelSample(acc_r[1], accelYPlus, accelYMinus);
            float accelZG = CorrectAccelSample(acc_r[2], accelZPlus, accelZMinus);

            gyr_g.X = rollDegPerSec;
            gyr_g.Y = yawDegPerSec;
            gyr_g.Z = pitchDegPerSec;
            acc_g.X = accelZG;
            acc_g.Y = -accelYG;
            acc_g.Z = -accelXG;

            UpdateCanonicalGyroMouseImu();
            AHRS.SamplePeriod = measuredGyroSubSamplePeriod;
            const float degToRad = 0.0174533f;
            AHRS.Update(gyr_g.X * degToRad, gyr_g.Y * degToRad, gyr_g.Z * degToRad,
                        acc_g.X, acc_g.Y, acc_g.Z);

            long nowTicks = Stopwatch.GetTimestamp();
            if ((nowTicks - lastDualShock4ImuLogTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                lastDualShock4ImuLogTimestamp = nowTicks;
                LogDualShock4RawDump(string.Format(CultureInfo.InvariantCulture,
                    "IMU: raw gyro=({0},{1},{2}) raw accel=({3},{4},{5}) " +
                    "gyr_g=({6:F1},{7:F1},{8:F1})deg/s acc_g=({9:F2},{10:F2},{11:F2})g " +
                    "dt={12:F3}ms rawTicksDelta={13}",
                    gyr_r[0], gyr_r[1], gyr_r[2], acc_r[0], acc_r[1], acc_r[2],
                    gyr_g.X, gyr_g.Y, gyr_g.Z, acc_g.X, acc_g.Y, acc_g.Z,
                    measuredGyroSubSamplePeriod * 1000.0f, lastLoggedImuDeltaTicks));
            }
        }

        private float CorrectGyroSample(short raw, short bias, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / GyroLsbPerDegPerSec;

            float sensitivity = (gyroSpeedPlus + gyroSpeedMinus) * GyroLsbPerDegPerSec /
                                (plus - minus);
            return (raw - bias) * sensitivity / GyroLsbPerDegPerSec;
        }

        private float CorrectAccelSample(short raw, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / AccelLsbPerG;

            float range = plus - minus;
            float bias = plus - range / 2.0f;
            return (raw - bias) * (2.0f * AccelLsbPerG / range) / AccelLsbPerG;
        }

        // DualShock 4 main output report. USB uses report 0x05 with the common payload directly
        // after the report ID. Bluetooth uses report 0x11, requests HID+CRC handling in its
        // hardware-control byte, and appends an A2-seeded CRC32. In both forms valid_flag0 bit 1
        // marks the RGB fields as authoritative. Layout follows the kernel hid-playstation DS4
        // output structures so connection color and later profile changes share one path.
        private bool SendDualShock4Lightbar(byte red, byte green, byte blue) {
            bool bt = !isUSB;
            int len = bt ? 78 : 32;
            byte[] buf = new byte[len];
            int commonOffset;
            if (bt) {
                buf[0] = 0x11;
                buf[1] = 0xC4; // HID | CRC32 | 4 ms Bluetooth poll interval
                buf[2] = 0x00; // audio control
                commonOffset = 3;
            } else {
                buf[0] = 0x05;
                commonOffset = 1;
            }

            buf[commonOffset] = 0x02; // DS4_OUTPUT_VALID_FLAG0_LED
            buf[commonOffset + 5] = red;
            buf[commonOffset + 6] = green;
            buf[commonOffset + 7] = blue;

            if (bt) {
                uint crc = Crc32(0xA2, buf, len - 4);
                buf[len - 4] = (byte)crc;
                buf[len - 3] = (byte)(crc >> 8);
                buf[len - 2] = (byte)(crc >> 16);
                buf[len - 1] = (byte)(crc >> 24);
            }
            return HIDapi.hid_write(handle, buf, new UIntPtr((uint)len)) >= 0;
        }

        // A wired CUH-ZCT2 exposes a 32 kHz stereo USB Audio Class endpoint. Its firmware chooses
        // the built-in speaker or an attached headset; this report enables their volume fields.
        public override void PrepareUsbAudio(int volumePercent) {
            if (!isUSB || state <= state_.DROPPED)
                return;

            volumePercent = Math.Max(0, Math.Min(100, volumePercent));
            byte volume = (byte)(volumePercent * 0xFF / 100);
            byte[] buf = new byte[32];
            buf[0] = 0x05;
            buf[1] = 0xB0; // left/right headphone + speaker volume fields are valid
            buf[19] = volume;
            buf[20] = volume;
            buf[22] = volume;
            HIDapi.hid_write(handle, buf, new UIntPtr((uint)buf.Length));
        }

        // EXPERIMENTAL, UNVERIFIED ON REAL HARDWARE. Bluetooth exposes no USB Audio Class endpoint
        // - there is nothing for ControllerAudio/WASAPI to open - so the only route to the built-in
        // speaker/headphone jack over BT is smuggling a real Bluetooth audio codec (SBC) inside HID
        // output reports. Report layout, field offsets, and SBC encoder parameters below are
        // transcribed from nefarius/DS4AudioStreamer (https://github.com/nefarius/DS4AudioStreamer,
        // MIT) - a real, currently maintained, complete implementation by the same author whose
        // ViGEmBus/HidHide binaries this project already bundles. Cross-validated against this
        // file's own already-shipped BT lightbar report: DS4AudioStreamer's lightbar RGB bytes land
        // at the exact same offsets (8/9/10) SendDualShock4Lightbar already uses, and both agree on
        // the Crc32(0xA2, ...) checksum this codebase already uses elsewhere. The SBC codec itself
        // comes from the native libsbc.dll (GPL-2.0, nefarius/libsbc) via SbcEncoder.cs.
        private const byte BtAudioEnableReportId = 0x11;
        private const int BtAudioEnableReportLen = 78;
        // Narrowed from the reference's proven 0xF3 (rumble|lightbar|flash|HP-L|HP-R|mic|speaker
        // all "valid" at once) - the reference's own documented flag meanings, just a subset of
        // them, kept out of the lightbar/rumble report so pressing the test-tone button (phase 1)
        // or streaming (phase 2) doesn't also blank the lightbar or cancel rumble as a side
        // effect. Selected per output path, not asserted together: an earlier version marked
        // BOTH HP and speaker "valid" always (0xB0, matching PrepareUsbAudio's USB convention),
        // zeroing whichever path wasn't in use rather than leaving its valid bit off entirely -
        // "valid but zero" is a different assertion to the hardware than "not specified at all",
        // and on real hardware that killed audio output completely, not just misrouted it.
        private const byte BtAudioValidFlagsHeadphones = 0x10 | 0x20; // HP-L | HP-R
        private const byte BtAudioValidFlagsSpeaker = 0x80; // Speaker

        private const byte BtAudioStreamProtocol4Frames = 0x17;
        private const int BtAudioStreamReport4FramesLen = 462;
        private const byte BtAudioStreamProtocol2Frames = 0x14;
        private const int BtAudioStreamReport2FramesLen = 270;
        private const int BtAudioStreamFramesPerBigBatch = 4;
        private const int BtAudioStreamFramesPerSmallBatch = 2;
        private const byte BtAudioOutputPathSpeaker = 0x02;
        // The payload target is separate from report 0x11's volume-valid flags. Target 0x02
        // feeds the controller speaker; Sony's headset-only target is 0x24. Merely changing the
        // volume fields while continuing to label every SBC packet as speaker audio can produce
        // signal at the jack, but it remains the speaker route rather than the proper stereo
        // headphone lane.
        private const byte BtAudioOutputPathHeadphones = 0x24;

        // Enables the DS4's Bluetooth audio DAC and sets its volume - report 0x11, same ID
        // SendDualShock4Lightbar's BT branch already uses. Must be sent once before any audio
        // stream reports; the controller ignores those otherwise. Only the selected path's valid
        // flag and volume byte(s) are ever written - see the const comment above for why the
        // other path is left unclaimed rather than valid-but-zero.
        public void PrepareBluetoothAudio(int volumePercent, bool routeToHeadphones) {
            if (isUSB || state <= state_.DROPPED)
                return;

            volumePercent = Math.Max(0, Math.Min(100, volumePercent));
            byte volume = (byte)(volumePercent * 0x50 / 100); // range is 0-0x50 (80 decimal), not 0-0xFF

            byte[] buf = new byte[BtAudioEnableReportLen];
            buf[0] = BtAudioEnableReportId;
            buf[1] = 0xC0;
            // A0 keeps ordinary controller input reports alive while the speaker lane is armed.
            // A2 is accepted by the audio hardware but eventually starves normal HID input on a
            // genuine CUH-ZCT2, which would also make headphone unplug detection impossible.
            buf[2] = 0xA0;
            if (routeToHeadphones) {
                buf[3] = BtAudioValidFlagsHeadphones;
                buf[21] = volume; // headphone left volume
                buf[22] = volume; // headphone right volume
            } else {
                buf[3] = BtAudioValidFlagsSpeaker;
                buf[24] = volume; // speaker volume
            }

            uint crc = Crc32(0xA2, buf, BtAudioEnableReportLen - 4);
            buf[BtAudioEnableReportLen - 4] = (byte)crc;
            buf[BtAudioEnableReportLen - 3] = (byte)(crc >> 8);
            buf[BtAudioEnableReportLen - 2] = (byte)(crc >> 16);
            buf[BtAudioEnableReportLen - 1] = (byte)(crc >> 24);

            HIDapi.hid_write(handle, buf, new UIntPtr((uint)BtAudioEnableReportLen));
        }

        // Phase 2: continuous live streaming, replacing phase 1's fixed-duration test tone.
        // EnqueueBluetoothAudioFrame receives already-SBC-encoded frames captured/encoded in the
        // per-session input helper process (see BluetoothAudioCapture.cs and
        // IJoyconHost.StartBluetoothAudioCapture - Session 0 cannot do WASAPI loopback capture
        // itself) and forwarded here over the existing helper pipe.
        //
        // Sending used to happen on a dedicated background thread, calling hid_write concurrently
        // with Poll's own hid_read_timeout on the same handle. hidapi's own docs call device
        // functions thread-unsafe when called concurrently, and on real hardware that concurrency
        // corrupted both the audio stream and unrelated input parsing (battery status went bad
        // too). Fixed by moving sending onto Poll itself (SendQueuedBluetoothAudioIfAny, called
        // once per Poll iteration, same pattern as SendQueuedRumbleIfAny) - every read and write
        // for this handle now happens from that one thread, like everything else in this class.
        // Poll's own natural iteration rate (driven by real HID read timing, typically a few ms)
        // paces output well enough on its own; no artificial sleep needed. One concurrent stream,
        // matching the reference project's own scope ("demo only supports one controller").
        // Start/StopBluetoothAudioStream run on Program.cs's profile-reconciliation thread while
        // SendQueuedBluetoothAudioIfAny runs on Poll. Live profile changes can restart an existing
        // stream, so the old publish-once ordering is no longer sufficient to protect the pending
        // List. Serialize lifecycle transitions with Poll's pending-list work; encoded-frame
        // delivery itself remains a ConcurrentQueue because it comes from the helper pipe thread.
        private readonly ConcurrentQueue<byte[]> bluetoothAudioFrameQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte[]> bluetoothAudioPending = new List<byte[]>();
        private readonly object bluetoothAudioStateLock = new object();
        private volatile bool bluetoothAudioStreaming;
        private bool bluetoothAudioPrimed;
        private ushort bluetoothAudioPacketCounter;
        private int bluetoothAudioVolumePercent = -1;
        private string bluetoothAudioEndpointId = String.Empty;
        private bool bluetoothAudioRouteHeadphones;
        private byte bluetoothAudioOutputPath = BtAudioOutputPathSpeaker;

        // Frames to accumulate before the first report ever goes out - roughly 96ms (24 * 4ms/
        // block) of cushion. Without this, sending began the instant 2 frames existed and stayed
        // at that same bare-minimum depth continuously (every Poll iteration drained pending back
        // down toward empty), leaving zero margin against any momentary capture hiccup - a classic
        // "not enough buffer" recipe for underrun artifacts (muffled/distorted, not silence,
        // since the controller's own decoder still has to do *something* with a starved stream).
        // One-time priming only, not a maintained floor - the trade is a fixed ~96ms of added
        // startup latency for continuity; if it still glitches mid-stream (not just at the start),
        // that points at sustained production/consumption imbalance instead, not a priming gap.
        private const int BtAudioPrimeFrameCount = 24;

        // One SBC block is 128 samples (BluetoothAudioCapture.cs's fixed 32kHz/8-subband/16-block
        // encoder config) = 4ms of audio. Must match that file's encoder parameters exactly.
        private const double BtAudioMsPerBlock = 4.0;
        private Stopwatch bluetoothAudioStopwatch;
        private double bluetoothAudioNextSendDeadlineMs;

        public bool IsStreamingBluetoothAudio => bluetoothAudioStreaming;

        public void StartBluetoothAudioStream(int volumePercent, string endpointId, bool routeToHeadphones) {
            lock (bluetoothAudioStateLock) {
                if (isUSB || state <= state_.DROPPED)
                    return;

                volumePercent = Math.Max(0, Math.Min(100, volumePercent));
                endpointId = endpointId ?? String.Empty;
                if (bluetoothAudioStreaming) {
                    bool settingsMatch = bluetoothAudioVolumePercent == volumePercent &&
                        String.Equals(bluetoothAudioEndpointId, endpointId, StringComparison.Ordinal) &&
                        bluetoothAudioRouteHeadphones == routeToHeadphones;
                    if (settingsMatch)
                        return;

                    // Program reconciles profile options periodically. A changed endpoint requires
                    // a new helper capture, while volume or route changes require a fresh report
                    // 0x11. Stop and restart as one ordered transition instead of leaving the old
                    // stream alive with stale settings. The lock is reentrant for this call.
                    StopBluetoothAudioStream();
                }

                PrepareBluetoothAudio(volumePercent, routeToHeadphones);
                if (!form.StartBluetoothAudioCapture(PadId, endpointId)) {
                    // The service commonly discovers the controller before its session helper is
                    // connected. Do not publish a false active state: OnHelperConnected performs
                    // another profile reconciliation once capture is genuinely available.
                    return;
                }

                while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
                bluetoothAudioPending.Clear();
                bluetoothAudioPacketCounter = 0;
                bluetoothAudioPrimed = false;
                bluetoothAudioStopwatch = Stopwatch.StartNew();
                bluetoothAudioNextSendDeadlineMs = 0;
                bluetoothAudioVolumePercent = volumePercent;
                bluetoothAudioEndpointId = endpointId;
                bluetoothAudioRouteHeadphones = routeToHeadphones;
                bluetoothAudioOutputPath = routeToHeadphones
                    ? BtAudioOutputPathHeadphones
                    : BtAudioOutputPathSpeaker;
                bluetoothAudioStreaming = true;
                AudioDebugLog.Write("DS4Send", "Start pad=" + PadId + " volume=" + volumePercent +
                    " endpoint=" + (String.IsNullOrEmpty(endpointId) ? "(default)" : endpointId) +
                    " headphones=" + routeToHeadphones);
            }
        }

        public void StopBluetoothAudioStream() {
            lock (bluetoothAudioStateLock) {
                if (!bluetoothAudioStreaming)
                    return;

                bluetoothAudioStreaming = false;
                form.StopBluetoothAudioCapture(PadId);
                bluetoothAudioVolumePercent = -1;
                bluetoothAudioEndpointId = String.Empty;
                bluetoothAudioRouteHeadphones = false;
                AudioDebugLog.Write("DS4Send", "Stop pad=" + PadId);
            }
        }

        // Called from HeadlessJoyconHost's helper-pipe read thread as frames arrive - the only
        // part of this feature that legitimately runs off the Poll thread, since it only touches
        // an in-memory queue, never the HID handle itself.
        public void EnqueueBluetoothAudioFrame(byte[] frame) {
            bluetoothAudioFrameQueue.Enqueue(frame);
        }

        // Called once per Poll iteration. Sends at most one batch per call, and only once the
        // real-time deadline for it has arrived - deliberately not a drain-everything-that's-ready
        // loop. Poll iterates roughly every ~4ms (DS4's own BT poll interval) while one 4-frame
        // batch represents 16ms of audio: with no pacing, Poll could drain buffered frames up to
        // ~4x faster than real playback speed whenever they were available, burning through any
        // priming cushion in a fraction of the time it took to build and landing right back in a
        // hand-to-mouth state - "primed but not paced" only delays the same underrun symptom
        // instead of fixing it. This never blocks/sleeps (Poll must stay free to keep reading) -
        // it just skips sending until enough wall-clock time has actually passed.
        protected override void SendQueuedBluetoothAudioIfAny() {
            lock (bluetoothAudioStateLock) {
                if (!bluetoothAudioStreaming)
                    return;

                int dequeued = 0;
                while (bluetoothAudioFrameQueue.TryDequeue(out byte[] frame)) {
                    bluetoothAudioPending.Add(frame);
                    dequeued++;
                }

                if (!bluetoothAudioPrimed) {
                    if (bluetoothAudioPending.Count < BtAudioPrimeFrameCount)
                        return;
                    bluetoothAudioPrimed = true;
                    AudioDebugLog.Write("DS4Send", "Primed pending=" + bluetoothAudioPending.Count);
                }

                if (bluetoothAudioPending.Count < BtAudioStreamFramesPerSmallBatch)
                    return;

                double nowMs = bluetoothAudioStopwatch.Elapsed.TotalMilliseconds;
                if (nowMs < bluetoothAudioNextSendDeadlineMs)
                    return;

                int batchSize = bluetoothAudioPending.Count >= BtAudioStreamFramesPerBigBatch
                    ? BtAudioStreamFramesPerBigBatch
                    : BtAudioStreamFramesPerSmallBatch;

                AudioDebugLog.Write("DS4Send", "pending=" + bluetoothAudioPending.Count +
                    " dequeuedThisCall=" + dequeued + " batch=" + batchSize +
                    " nowMs=" + nowMs.ToString("F1") +
                    " deadlineMs=" + bluetoothAudioNextSendDeadlineMs.ToString("F1"));

                SendBluetoothAudioBatch(bluetoothAudioPending, 0, batchSize, ref bluetoothAudioPacketCounter);
                bluetoothAudioPending.RemoveRange(0, batchSize);

                // Anchor the next deadline off the target schedule, not "now" - if this call ran a
                // little late, catch up gradually via subsequent early-arriving Poll iterations
                // rather than permanently shifting the whole schedule later.
                bluetoothAudioNextSendDeadlineMs += batchSize * BtAudioMsPerBlock;
            }
        }

        private void SendBluetoothAudioBatch(List<byte[]> frames, int start, int batchSize, ref ushort packetCounter) {
            byte protocol = batchSize == BtAudioStreamFramesPerBigBatch
                ? BtAudioStreamProtocol4Frames
                : BtAudioStreamProtocol2Frames;
            int reportLen = batchSize == BtAudioStreamFramesPerBigBatch
                ? BtAudioStreamReport4FramesLen
                : BtAudioStreamReport2FramesLen;

            byte[] buf = new byte[reportLen];
            buf[0] = protocol;
            buf[1] = 0x40;
            buf[2] = 0xa0; // preserve ordinary HID input while carrying outbound SBC audio
            buf[3] = (byte)(packetCounter & 0xFF);
            buf[4] = (byte)((packetCounter >> 8) & 0xFF);
            buf[5] = bluetoothAudioOutputPath;

            int offset = 6;
            for (int i = 0; i < batchSize; i++) {
                byte[] frame = frames[start + i];
                Buffer.BlockCopy(frame, 0, buf, offset, frame.Length);
                offset += frame.Length;
            }

            uint crc = Crc32(0xA2, buf, reportLen - 4);
            buf[reportLen - 4] = (byte)crc;
            buf[reportLen - 3] = (byte)(crc >> 8);
            buf[reportLen - 2] = (byte)(crc >> 16);
            buf[reportLen - 1] = (byte)(crc >> 24);

            if (HIDapi.hid_write(handle, buf, new UIntPtr((uint)reportLen)) < 0)
                LogDualShock4RawDump("Bluetooth audio stream write failed");

            packetCounter += (ushort)batchSize;
        }
    }
}
