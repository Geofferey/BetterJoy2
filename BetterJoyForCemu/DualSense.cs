using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // DualSenseController : Controller - step 4 Phase J of DOCS/CONTROLLERS-REFACTOR.md's
    // migration order. Everything here was previously Joycon.cs's Tier 2 DualSense-specific code
    // (isDualSense-gated), relocated verbatim into its own real sibling of Joycon under
    // Controller, per the target architecture in the doc. DualSense does not pair (SupportsPairing
    // is always false) and deliberately never gets a DS4-output target - see the doc's "Tier 3"
    // note on MapToDualShock4Input. Gyro/accel support (see ExtractIMUValues/
    // ReadGyroCalibration below): byte offsets, calibration feature report, and scale constants
    // were cross-checked against three independent reference implementations; physical axis
    // sign/handedness (the one item no public reference source documented) was confirmed
    // empirically against real hardware instead - see ExtractIMUValues' own comments for what
    // that investigation found (a Nintendo-only calibration-bias leak in shared CalibrationState
    // code, and gyr_g/acc_g needing matching channel order for AHRS's sensor fusion to agree
    // with itself, not just an axis-sign guess).
    public class DualSenseController : Controller {
        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;
        public override bool HasGyro => true;
        public override bool HasAnalogTriggers => true;
        public override bool UsesNintendoProtocol => false;
        public override ControllerKind Kind => ControllerKind.DualSense;

        public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2
        protected override byte[] TriggerVal => triggerVal;

        private const int DualSenseMaxReportLen = 78; // Bluetooth report length; USB (64) fits the same buffer
        private bool sentUsbActiveLightbar = false;
        private long lastDualSenseRawDumpTimestamp = 0;
        private long lastDualSenseImuLogTimestamp = 0;
        // GyroSubSamplePeriod override support - see GyroMath.cs's field comment. DualSense's real
        // report interval has nothing like Joy-Con's fixed 5ms, so ProcessGyroMouseSample/
        // ProcessGyroStickSample need the actual elapsed time here instead of the Nintendo-tuned
        // hardcoded constant. Measured from the report's own embedded hardware timestamp
        // (r[27+o..30+o], a free-running microsecond counter), not wall-clock arrival time - a
        // real hardware capture showed a fast, sustained wrist-roll motion (gravity trust
        // correctly dropped to ~0.29, but gyro-only integration still drifted ~99deg from the
        // accelerometer's own reading, producing a corkscrew cursor path) traced to USB/BT
        // delivering multiple already-sampled reports in a burst: wall-clock arrival time bunches
        // those together near-zero apart even though the sensor captured them evenly spaced in
        // real time, so Stopwatch-based measurement was under-measuring dt exactly when a fast
        // motion made integration accuracy matter most. The hardware counter reflects the sensor's
        // own sampling clock and is immune to transport-layer buffering/batching.
        private uint? lastImuHardwareTimestampTicks;
        private uint lastLoggedImuDeltaTicks;
        private float measuredGyroSubSamplePeriod = ImuSamplePeriodSeconds;
        // Bounds for the measured interval: floor avoids a near-zero/duplicate-timestamp dt
        // collapsing the gravity-fusion integration to a no-op, ceiling avoids a single stall
        // (BT hiccup, USB re-enumeration) injecting one wildly oversized rotation step.
        private const float MinGyroSubSamplePeriod = 0.0005f;
        private const float MaxGyroSubSamplePeriod = 0.02f;

        protected override float GyroSubSamplePeriod => measuredGyroSubSamplePeriod;
        // See GyroMath.cs's GyroStickBiasCorrection declaration - opts DualSense's gyro-stick into
        // the same stationary-bias correction gyro-mouse already gets, confirmed needed via a real
        // capture (see that comment). Joy-Con never overrides this, so it stays at the class
        // default (Vector3.Zero, a no-op).
        protected override Vector3 GyroStickBiasCorrection => gyroMouseBias;

        // Gyro/accel calibration, read once from DualSense's own hardware calibration feature
        // report (0x05) at Attach() - see ReadGyroCalibration. Nominal sensor-chip scale
        // constants (fixed, not read from hardware): 16 LSB per degree/second, 8192 LSB per g -
        // cross-confirmed against two independent reference implementations (DS4Windows,
        // JoyShockLibrary; DS4Windows's calibration correction below rescales this specific
        // unit's real sensitivity onto these nominal constants before dividing by them).
        private const float GyroLsbPerDegPerSec = 16.0f;
        private const float AccelLsbPerG = 8192.0f;
        private short gyroPitchBias, gyroYawBias, gyroRollBias;
        private short gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus;
        private short gyroSpeedPlus, gyroSpeedMinus;
        private short accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus;
        // False until a real calibration report has been successfully read and CRC-verified (BT)
        // - ExtractIMUValues falls back to the nominal scale with zero bias when this is false,
        // rather than dividing by a degenerate (0-0) Plus/Minus range.
        private bool gyroCalibrationValid;

        public DualSenseController(IntPtr handle_, string path, string serialNum, int id = 0) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            // Only the amplitude (index 2) is ever read back out for DualSense's simple dual-motor
            // rumble (see SendQueuedRumbleIfAny) - the low/high-frequency values Joy-Con's HD-
            // rumble encoding would use are meaningless here, so this is seeded to all-zero rather
            // than reusing Joy-Con's LowFreqRumble/HighFreqRumble config values.
            rumble_obj = new Rumble(new float[] { 0, 0, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            // Single-unit device, same "primary/solo" convention every non-Joy-Con device uses
            // (see Controller.isLeft's own comment) - CalibrationState.FinishCalibration reads
            // this to correct a real Joy-Con-side gravity-axis sign difference that doesn't apply
            // here, but the flag itself still needs to be true for the gyro calibration wizard.
            isLeft = true;

            PadId = id;
            // Re-derived every packet from actual report length (see ReceiveRaw) - the Joy-Con-
            // only placeholder-serial heuristic doesn't apply here.
            isUSB = false;
            this.path = path;

            RefreshGyroOnlyButtonReservations();

            connection = isUSB ? 0x01 : 0x02;

            // Pitch-dominant-motion-leaking-into-yaw correction is opt-in on GyroMousePlayerSpace
            // (see its EnableExtendedAxisCorrection field comment) - confirmed needed on DualSense via
            // a real pure-pitch hardware test, but Joy-Con has no reported version of this problem,
            // so it stays off there and on only here.
            gyroMousePlayerSpace.EnableExtendedAxisCorrection = true;
            gyroStickPlayerSpace.EnableExtendedAxisCorrection = true;
        }

        // No shared shell worth extracting - see Controller.Attach's abstract declaration. This is
        // the exact body of the old Joycon.Attach()'s "if (!UsesNintendoProtocol)" early-return
        // branch, now the whole method since DualSenseController never speaks the Nintendo
        // protocol at all.
        public override int Attach() {
            state = state_.ATTACHED;

            // None of the USB handshake bytes, SPI calibration dump, home-light/player-LED writes,
            // or IMU/rumble/input-mode subcommands a Nintendo device needs apply here - a DualSense
            // doesn't speak that protocol at all. No enable-full-report-mode handshake is known to
            // be required for baseline button/stick/trigger reads; if the first real test shows
            // all-zero/empty reports over Bluetooth, that's the first thing to investigate.
            HIDapi.hid_set_nonblocking(handle, 1);

            // DualSense has no SPI factory calibration to read, so stick_cal/stick2_cal/deadzone
            // would otherwise be left at their class defaults ({0,0,0,0,0,0}/0) - CenterSticks
            // would divide by that zero the moment it's used. Seed an identity calibration matching
            // the DualSense's real raw domain (bytes 0-255, center 128) so stick output is correct
            // out of the box, then let any stored user recalibration (CalibrationState, via the
            // same wizard Joy-Con uses) overlay on top exactly the way it already does for Joy-Con.
            stick_cal[0] = 127; stick_cal[1] = 127;   // max above center (X, Y)
            stick_cal[2] = 128; stick_cal[3] = 128;   // center (X, Y)
            stick_cal[4] = 128; stick_cal[5] = 128;   // min below center (X, Y)
            stick2_cal[0] = 127; stick2_cal[1] = 127;
            stick2_cal[2] = 128; stick2_cal[3] = 128;
            stick2_cal[4] = 128; stick2_cal[5] = 128;
            // A few raw units of headroom over the idle jitter observed on real hardware
            // (~127-133 out of 0-255 at rest) so an uncalibrated DualSense doesn't bleed tiny
            // phantom stick movement before the user ever runs the wizard.
            deadzone = 8;
            deadzone2 = 8;
            getActiveStickData();

            ReadGyroCalibration();

            form.AppendTextBox("DualSense attached (baseline mode).\r\n");
            return 0;
        }

        private const byte GyroCalibrationFeatureReportId = 0x05;
        private const int GyroCalibrationFeatureReportLen = 41;

        // DualSense's own factory gyro/accel calibration, read via a HID feature report - its
        // equivalent of Joy-Con's SPI-flash calibration read (Joycon.dump_calibration_data).
        // Report ID 0x05, 41 bytes (1 report-ID byte + 36 calibration bytes + trailing CRC32).
        // On Bluetooth the reply is CRC32-verified (seeded 0xA3, via the same Crc32() helper
        // SendDualSenseRumble/SendDualSenseLightbar already use for outgoing reports with a
        // different seed) and retried up to 5 times, matching DS4Windows exactly; USB is trusted
        // on a single unconditional read, also matching DS4Windows. DualSense always uses the
        // same calibration byte grouping on both transports (unlike DualShock 4, which DS4Windows
        // varies by transport) - byte offsets below are DualSense-specific, not shared with any
        // DS4 code path. Falls back to gyroCalibrationValid=false (nominal scale, zero bias) if
        // every attempt fails, rather than leaving stale/degenerate calibration data silently in
        // place.
        private void ReadGyroCalibration() {
            byte[] buf = new byte[GyroCalibrationFeatureReportLen];
            buf[0] = GyroCalibrationFeatureReportId;

            bool verified = false;
            int attempts = isUSB ? 1 : 5;
            for (int attempt = 0; attempt < attempts && !verified; attempt++) {
                int ret = HIDapi.hid_get_feature_report(handle, buf, new UIntPtr((uint)GyroCalibrationFeatureReportLen));
                if (ret < GyroCalibrationFeatureReportLen)
                    continue;

                if (isUSB) {
                    verified = true;
                } else {
                    uint received = (uint)buf[37] | ((uint)buf[38] << 8) | ((uint)buf[39] << 16) | ((uint)buf[40] << 24);
                    uint calculated = Crc32(0xA3, buf, GyroCalibrationFeatureReportLen - 4);
                    verified = received == calculated;
                }
            }

            if (!verified) {
                gyroCalibrationValid = false;
                form.AppendTextBox("DualSense gyro calibration read failed - using uncalibrated nominal scale.\r\n");
                LogDualSenseRawDump("Gyro calibration read failed after " + attempts + " attempt(s).");
                return;
            }

            gyroPitchBias = ReadCalibrationInt16(buf, 1);
            gyroYawBias = ReadCalibrationInt16(buf, 3);
            gyroRollBias = ReadCalibrationInt16(buf, 5);
            gyroPitchPlus = ReadCalibrationInt16(buf, 7);
            gyroPitchMinus = ReadCalibrationInt16(buf, 9);
            gyroYawPlus = ReadCalibrationInt16(buf, 11);
            gyroYawMinus = ReadCalibrationInt16(buf, 13);
            gyroRollPlus = ReadCalibrationInt16(buf, 15);
            gyroRollMinus = ReadCalibrationInt16(buf, 17);
            gyroSpeedPlus = ReadCalibrationInt16(buf, 19);
            gyroSpeedMinus = ReadCalibrationInt16(buf, 21);
            accelXPlus = ReadCalibrationInt16(buf, 23);
            accelXMinus = ReadCalibrationInt16(buf, 25);
            accelYPlus = ReadCalibrationInt16(buf, 27);
            accelYMinus = ReadCalibrationInt16(buf, 29);
            accelZPlus = ReadCalibrationInt16(buf, 31);
            accelZMinus = ReadCalibrationInt16(buf, 33);
            gyroCalibrationValid = true;

            LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                "Gyro calibration OK: gyroBias=({0},{1},{2}) gyroPlusMinus=({3}/{4},{5}/{6},{7}/{8}) " +
                "gyroSpeedPlusMinus=({9}/{10}) accelPlusMinus=({11}/{12},{13}/{14},{15}/{16})",
                gyroPitchBias, gyroYawBias, gyroRollBias,
                gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus,
                gyroSpeedPlus, gyroSpeedMinus,
                accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus));
        }

        private static short ReadCalibrationInt16(byte[] buf, int offset) {
            return (short)(buf[offset] | (buf[offset + 1] << 8));
        }

        // 4-byte LE free-running counter, DualSense's own IMU sample clock - ticks at ~3MHz on
        // real hardware, not the 1MHz a "microsecond timestamp" label would suggest (see the
        // ExtractIMUValues call site for the real-hardware measurement that found this). See
        // measuredGyroSubSamplePeriod's field comment for why this is used over wall-clock timing.
        private static uint ReadTimestampTicks(byte[] buf, int offset) {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) |
                          (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        // Deliberately no override here (inherits Controller's no-op): the old Joycon.PowerOff()
        // this used to run through sent a Nintendo-protocol subcommand (SetHCIState/Subcommand)
        // that's meaningless to a DualSense - Subcommand/ReadSPI/SetHCIState are Nintendo-only and
        // stay in Joycon.cs, not promoted here. state_.DROPPED still needs to be set on power-off
        // (HOME-long-press, inactivity timeout) so the connection actually tears down - see the
        // override below.
        // Real Bluetooth-level power-off - BT only, matching that command's own documented scope:
        // a wired USB connection has no power-off analog, so attempting it there would be a no-op
        // at best. Previously this just set state_.DROPPED unconditionally with no real hardware
        // effect at all (neither transport) - harmless-looking, but BetterJoy would immediately
        // rediscover the still-physically-connected controller on the next scan and reconnect it,
        // producing a rapid disconnect/reconnect loop on every HOME-long-press or inactivity
        // timeout (confirmed via debug.log: repeated "Connect: new controller added" for the same
        // DualSense roughly every PowerOffInactivity-minutes interval, with zero "Dropped."/"went
        // silent"/"Retiring duplicate" lines - the tell that state was being dropped silently,
        // outside every path that normally logs a drop).
        public override void PowerOff() {
            if (state > state_.DROPPED && !isUSB) {
                BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                state = state_.DROPPED;
            }
        }

        // Generic MAC-based dedup runs in Controller.RetireDuplicateConnections(); this hook adds
        // DualSense's own Bluetooth-auto-disconnect tail on top, for the one case a plain MAC-based
        // drop doesn't fully clean up: a Bluetooth-connected DualSense that's now also connected
        // over USB. Marking the stale BT entry DROPPED (already done by the caller) only stops
        // BetterJoy from using it - the underlying OS-level Bluetooth HID connection is still alive
        // and gets rediscovered (and re-retired) on every subsequent scan, churning a new virtual
        // controller each time. Tell the Bluetooth radio itself to drop that connection instead,
        // the same way DS4Windows's DisconnectBT does (IOCTL_BTH_DISCONNECT_DEVICE).
        protected override void OnDuplicateRetired(Controller other) {
            if (other is DualSenseController && isUSB && !other.isUSB) {
                // Blue lightbar confirmation is handled unconditionally on the first confirmed-USB
                // read in ReceiveRaw (sentUsbActiveLightbar) - covers this case too, not just
                // fresh/no-prior-BT USB connects.
                bool disconnected = BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                form.AppendTextBox(disconnected
                    ? "Disconnected DualSense's Bluetooth link now that USB has taken over.\r\n"
                    : "Could not disconnect DualSense's Bluetooth link - it may keep reappearing.\r\n");
            }
        }

        // TEMPORARY diagnostic: user reports a DualSense still acting on click/gyro-mouse binds
        // after disabling them in the profile UI - log the actual resolved profile ID and value
        // (per key, own throttle each) so this can be confirmed against controller_mappings.xml
        // directly instead of guessed at.
        private readonly System.Collections.Generic.Dictionary<string, long> lastMappingValueDumpTimestamp =
            new System.Collections.Generic.Dictionary<string, long>();

        protected override void OnMappingValueResolved(string key, string value) {
            if (key == "left_click" || key == "right_click" || key == "active_gyro_mouse") {
                long nowTicks = Stopwatch.GetTimestamp();
                long last;
                if (!lastMappingValueDumpTimestamp.TryGetValue(key, out last) ||
                    (nowTicks - last) / (double)Stopwatch.Frequency >= 1.0) {
                    lastMappingValueDumpTimestamp[key] = nowTicks;
                    LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                        "MappingValue: profileId={0} key={1} value={2}", mappingProfileId, key, value));
                }
            }
        }

        private static readonly ConcurrentQueue<string> dualSenseRawDumpQueue = new ConcurrentQueue<string>();
        private static int dualSenseRawDumpWriterStarted;

        // Same async queue + background-writer pattern as autocal_debug.log, so this can't block a
        // controller's own Poll thread on file I/O. Gated behind DualSenseDebugLogging (default
        // off) - this writes continuously while a DualSense is connected, so it shouldn't run
        // unconditionally for every user, only when actually troubleshooting something.
        internal void LogDualSenseRawDump(string message) {
            if (!Boolean.Parse(ConfigurationManager.AppSettings["DualSenseDebugLogging"]))
                return;

            if (Interlocked.CompareExchange(ref dualSenseRawDumpWriterStarted, 1, 0) == 0) {
                new Thread(DualSenseRawDumpWriterLoop) {
                    IsBackground = true,
                    Name = "DualSenseRawDumpWriter"
                }.Start();
            }
            dualSenseRawDumpQueue.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, serial_number, message));
        }

        private static void DualSenseRawDumpWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "dualsense_raw_debug.log");
            while (true) {
                Thread.Sleep(250);
                if (dualSenseRawDumpQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (dualSenseRawDumpQueue.TryDequeue(out string line))
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

            byte[] dsBuf = new byte[DualSenseMaxReportLen];
            int dsRet = HIDapi.hid_read_timeout(handle, dsBuf, new UIntPtr((uint)DualSenseMaxReportLen), 5);

            // Actual report length distinguishes USB (64 bytes) from Bluetooth (78 bytes) per read -
            // no separate transport query needed, and more reliable than the Joy-Con-only
            // placeholder-serial heuristic isUSB otherwise depends on.
            if (dsRet == 64 || dsRet == 78) {
                isUSB = dsRet == 64;
                // connection (the DSU/cemuhook transport byte UpdServer.cs reports to clients) was
                // otherwise only ever set once in the constructor from the initial isUSB guess and
                // never updated again - it could silently disagree with isUSB's real per-packet
                // correction above after a transport switch (e.g. connecting over USB after a
                // stale Bluetooth link). Keep them in lockstep here, at the one place isUSB itself
                // is corrected. See DOCS/CONTROLLERS-REFACTOR.md step 7.
                connection = isUSB ? 0x01 : 0x02;
                if (isUSB && !sentUsbActiveLightbar) {
                    // Fires once per connection, on the first confirmed-USB read - covers every
                    // USB-connect scenario (fresh plug-in, reconnect, with or without a prior
                    // Bluetooth link), not just the "just force-disconnected a stale BT link" case
                    // OnDuplicateRetired handles separately.
                    sentUsbActiveLightbar = true;
                    SendDualSenseLightbar(0, 0, 255);
                }
                // hid_read_timeout does NOT strip the leading report-ID byte for either transport -
                // byte 0 is a constant 0x01 (USB) or 0x31 (BT) report ID. USB has no further
                // padding, so real data starts at byte 1. BT has one more padding/tag byte after
                // the report ID before real data starts at byte 2. Confirmed two independent ways:
                // (1) decoding a real idle BT capture at offset 2 gives sane values (sticks
                // dead-center, triggers at 0, button byte reading the DualSense's documented
                // dpad-neutral encoding 0x08) while offset 1 does not; (2) DS4Windows's own
                // DualSenseDevice.cs (a shipped Windows implementation) uses reportOffset = BT ? 1
                // : 0 relative to a buffer that, like ours, still includes the report-ID byte -
                // i.e. absolute offset 2 (BT) / 1 (USB), matching (1).
                int reportOffset = isUSB ? 1 : 2;

                // TEMPORARY diagnostic: the offsets guessed from a secondhand reference are
                // demonstrably wrong (confirmed on real hardware - trigger/button bytes don't line
                // up), so dump real bytes to a file instead of guessing a third time - the
                // on-screen console has not been a reliable way to actually see this. Throttled to
                // ~4/sec so it's readable while still catching real changes as controls are pressed
                // one at a time. Remove once ParseDualSenseReport's offsets are confirmed correct
                // against real data.
                long nowTicks = Stopwatch.GetTimestamp();
                if ((nowTicks - lastDualSenseRawDumpTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                    lastDualSenseRawDumpTimestamp = nowTicks;
                    var hex = new StringBuilder();
                    for (int i = 0; i < dsRet; i++)
                        hex.Append(dsBuf[i].ToString("X2")).Append(' ');
                    LogDualSenseRawDump("DS raw[" + dsRet + "]: " + hex.ToString());
                }

                ParseDualSenseReport(dsBuf, reportOffset);
                // BeginGyroStickDiagnosticReport/AccumulateGyroStickDiagnosticSample bracket every
                // ExtractIMUValues call so gyro_stick_debug.csv actually gets rows for DualSense -
                // previously only NintendoController.ReceiveRaw wired these up, so this csv was
                // silently empty for every DualSense session, gyro-stick issues included.
                BeginGyroStickDiagnosticReport();
                ExtractIMUValues(dsBuf, reportOffset);
                AccumulateGyroStickDiagnosticSample();
                DoThingsWithButtons();

                // The actual acc_g/gyr_g -> mouse/stick conversion - ExtractIMUValues only
                // computes calibrated sensor values and feeds the AHRS filter, it doesn't itself
                // produce any output (see NintendoController.ReceiveRaw's identical call shape).
                // flush=true unconditionally: DualSense's report carries one IMU sample, not
                // Joy-Con's three sub-samples per report, so there's no partial-accumulation case
                // to gate on.
                ProcessGyroMouseSample(true);
                ProcessGyroStickSample(true);
                // r[6+o] is DualSense's free-running sequence/status counter - the nearest
                // equivalent to Joy-Con's per-report device timer byte NintendoController passes
                // here (see ParseDualSenseReport's comment on that same byte).
                RecordGyroStickDiagnosticReport(dsBuf[6 + reportOffset], Stopwatch.GetTimestamp());

                if (out_xbox != null) {
                    try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch (Exception) { }
                }
                return dsRet;
            }

            // An unexpected length means the report stream is no longer what this parser expects -
            // possibly a transient glitch, but also possibly a connection that's genuinely gone bad
            // (confirmed on real hardware: report framing can shift after something puts the
            // controller in a bad state). Treating this as harmless previously meant such a
            // connection could never reach DROPPED and would sit in joy.cpl as a stale, frozen
            // "connected" entry forever - count it as a real error instead so a truly broken
            // connection gets cleaned up like any other.
            if (dsRet > 0)
                return -1;
            return dsRet; // 0 = timeout, <0 = read error - Poll()'s state machine already handles both
        }

        // Called from Controller.Poll()'s shared shell whenever rumble_obj's queue has data.
        // DualSense's simple dual-motor rumble has no equivalent to the low/high-frequency split
        // Joy-Con's HD-rumble Rumble.GetData() encodes - just take the queued amplitude directly
        // and drive both motors the same. Was disabled after real hardware went into continuous,
        // non-stopping rumble the first time this ran - root cause found: outputReport[2] (USB) /
        // [3] (BT) is a required feature-flags byte (0x55: mic LED, audio mute, touchpad strips,
        // player lights, motor power) that was left at 0x00 by omission, not an intentional "leave
        // alone" zero. Re-enabled with that byte now set.
        protected override void SendQueuedRumbleIfAny() {
            if (rumble_obj.queue.Count > 0) {
                float amp = rumble_obj.queue.Dequeue()[2];
                byte motor = (byte)(Math.Max(0f, Math.Min(1f, amp)) * 255f);
                SendDualSenseRumble(motor, motor);
            }
        }

        // DualSense baseline report parsing - buttons/sticks/triggers only (no gyro/touchpad/
        // adaptive-trigger reads yet). Offsets and layout from the standard DualSense USB/BT HID
        // report; o is 1 on Bluetooth (a leading byte USB doesn't have), 0 on USB. Populates the
        // exact same buttons[]/stick[]/stick2[]/triggerVal[] fields Joy-Con parsing does, so every
        // downstream consumer (MapToXbox360Input, profiles, UI) needs no DualSense-specific code
        // beyond the analog-trigger branch in MapToXbox360Input.
        private void ParseDualSenseReport(byte[] r, int o) {
            // Offsets below are from a direct hardware capture (raw hex dump, dualsense_raw_debug.log),
            // not a secondhand reference - both references checked (DS4Windows, a community wire-
            // format doc) agreed with each other on field order but disagreed with real hardware,
            // not just by a constant byte shift: the actual order is sticks, buttons1, buttons2, a
            // free-running sequence counter, THEN L2/R2 analog - references had triggers before the
            // counter and buttons after. Confirmed from real data: byte 4 reads a constant 0x08 at
            // rest (dpad nibble 8 = neutral, matching the real PS dpad convention, face-button
            // nibble 0 = nothing pressed); byte 5 toggles exactly 0x04/0x08 in sync with L2/R2's
            // digital end-of-travel click; byte 6 free-runs 0x00-0x3C regardless of input (the
            // counter); bytes 7/8 ramp with L2/R2 squeeze depth precisely when byte 5's matching
            // click bit is set. o is the genuine Bluetooth-vs-USB protocol byte (1/0).
            //
            // Raw 0-255, center ~128, run through the same CenterSticks/CalibrationState pipeline
            // Joy-Con uses (stick_cal/stick2_cal seeded with an identity default in Attach() since
            // there's no SPI factory data to read) - a DualSense can now be recalibrated with the
            // existing double-click wizard exactly like a Pro controller's sticks, now including a
            // gyro step too (see HeadlessJoyconHost.StartCalibration and ExtractIMUValues below).
            // AddStickSample is a no-op unless this controller is the one currently claimed by
            // that wizard. Y is inverted
            // after CenterSticks (not before, unlike the old fixed linear map) since CenterSticks'
            // raw subtraction/division doesn't know about BetterJoy's own "up is positive" stick
            // convention - only the sign needs flipping, not the calibration math.
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

            // USB and BT reports use the identical field order once o has skipped each transport's
            // own report-ID(+padding) prefix (see the o assignment in ReceiveRaw) - no further
            // per-transport swap needed here. Order after the sticks: L2, R2, a free-running
            // sequence/status counter (field index 6, skipped), then the two button bytes.
            // Cross-checked against DS4Windows's DualSenseDevice.cs (inputReport[5/6+ro] for
            // triggers, [8/9+ro] for the button bytes) and against a real idle BT capture, which
            // only decodes to sane values (dead-center sticks, zeroed triggers, neutral dpad) at
            // these positions.
            int triggerFieldBase = 4;
            int buttonFieldBase = 7;

            triggerVal[0] = r[triggerFieldBase + o];
            triggerVal[1] = r[triggerFieldBase + 1 + o];

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

                // byte 6 is the sequence counter (skipped). PS button confirmed via DS4Windows's
                // DualSenseDevice.cs (inputReport[10+ro], bit 0).
                byte btn3 = r[9 + o];
                b[(int)Button.HOME] = (btn3 & 0x01) != 0; // PS button
                // Touchpad click/mute/paddles intentionally unmapped this milestone (out of scope);
                // SL/SR have no DualSense equivalent, left false.

                buttons = b;
                CommitButtonState();
            }

            // Battery offset (52+o) confirmed via DS4Windows's DualSenseDevice.cs (inputReport[53+ro],
            // same absolute position once o's own transport skip is accounted for). Low nibble is a
            // coarse 0-8 level (bit 5 = full charge, forced to 8); halved to match GetBatteryColor's
            // existing 0-4 scale, the same way Joy-Con's own coarser battery nibble already does.
            byte batteryByte = r[52 + o];
            int rawLevel = (batteryByte & 0x20) != 0 ? 8 : (batteryByte & 0x0F);
            int newBattery = battery;
            battery = Math.Min(4, rawLevel / 2);
            if (newBattery != battery)
                BatteryChanged();
        }

        // Gyro/accel byte offsets, cross-checked against three independent reference
        // implementations (DS4Windows, nondebug/dualsense, JoyShockLibrary) - all three agree
        // exactly. Wire order is gyroPitch, gyroYaw, gyroRoll (raw sensor channels 0/1/2, DS4
        // Windows's own field names - not a claim about which is physically pitch/yaw/roll on
        // the real controller), then accelX/Y/Z, then a 4-byte hardware timestamp used to measure
        // real per-report elapsed time (see ReadTimestampTicks - despite the name/reference
        // sources calling it a microsecond counter, a real hardware capture shows it increments at
        // ~3 ticks/us, not 1; ReadTimestampTicks's caller compensates). One IMU sample per report
        // (unlike Joy-Con's three sub-samples per report), so this runs once per ReceiveRaw call,
        // not in a sub-sample loop.
        private void ExtractIMUValues(byte[] r, int o) {
            EnsureGyroOrientationBasis();

            uint hardwareTimestampTicks = ReadTimestampTicks(r, 27 + o);
            if (lastImuHardwareTimestampTicks.HasValue) {
                // Unsigned subtraction wraps correctly modulo 2^32 across the counter's rollover,
                // as long as the true elapsed time between consecutive reports is well under half
                // that range - always true for a per-report delta.
                uint deltaTicks = unchecked(hardwareTimestampTicks -
                                            lastImuHardwareTimestampTicks.Value);
                // Decoded directly from real raw hex dumps: consecutive samples known to be 250ms
                // apart (a throttled debug log's own wall-clock interval) showed this counter
                // advancing by ~750,000 ticks, not ~250,000 - a consistent ~3.0x ratio across five
                // separate sample pairs (750,418 average / 250,000 = 3.0017). The field name/
                // reference sources call it a microsecond counter, but on real hardware it's
                // ticking at ~3MHz, not 1MHz. Dividing by 1,000,000 (treating it as literal
                // microseconds) was silently feeding gravity integration a dt ~3x too large on
                // every single report - confirmed as the actual cause of a real corkscrew/spiral
                // cursor path during sustained wrist roll, even at full gyro trust.
                float measuredDt = deltaTicks / 3000000.0f;
                measuredGyroSubSamplePeriod = Math.Max(MinGyroSubSamplePeriod,
                    Math.Min(MaxGyroSubSamplePeriod, measuredDt));
                lastLoggedImuDeltaTicks = deltaTicks;
            }
            lastImuHardwareTimestampTicks = hardwareTimestampTicks;

            gyr_r[0] = ReadCalibrationInt16(r, 15 + o); // gyroPitch (raw channel 0)
            gyr_r[1] = ReadCalibrationInt16(r, 17 + o); // gyroYaw (raw channel 1)
            gyr_r[2] = ReadCalibrationInt16(r, 19 + o); // gyroRoll (raw channel 2)
            acc_r[0] = ReadCalibrationInt16(r, 21 + o);
            acc_r[1] = ReadCalibrationInt16(r, 23 + o);
            acc_r[2] = ReadCalibrationInt16(r, 25 + o);

            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                // Mirrors NintendoController.ExtractIMUValues's live-calibration branch exactly -
                // the manual calibration wizard's gyro step (HeadlessJoyconHost.StartCalibration,
                // now reachable for DualSense since HasGyro is true) and auto-calibration both
                // depend on samples being collected here; CalibrationState later publishes them
                // into activeData (already refreshed generically for every Controller, including
                // this one - see Program.cs's post-connect getActiveData() call). Same shared
                // mechanism Joy-Con already uses, not a DualSense-specific one - only the nominal
                // scale below (16/8192, not Joy-Con's 18642/816 and 16384/4) is DualSense-specific.
                CalibrationState.AddSample(this, CalibrationState.XA, CalibrationState.XG, acc_r[0], gyr_r[0]);
                CalibrationState.AddSample(this, CalibrationState.YA, CalibrationState.YG, acc_r[1], gyr_r[1]);
                CalibrationState.AddSample(this, CalibrationState.ZA, CalibrationState.ZG, acc_r[2], gyr_r[2]);
            }

            float gyroPitchDegPerSec, gyroYawDegPerSec, gyroRollDegPerSec;

            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]) && activeData != null) {
                // activeData[0-2] = gyro offsets, per-axis in wire-channel order - same indexing
                // NintendoController.ExtractIMUValues uses, just against DualSense's own fixed
                // nominal scale instead of Joy-Con's gyr_sen. Gyro's "zero" is orientation-
                // independent (angular velocity genuinely is ~0 at rest regardless of how the
                // controller is held), so re-deriving it from wherever the wizard's "hold still"
                // step happened to run is valid - unlike accel below.
                gyroPitchDegPerSec = (gyr_r[0] - activeData[0]) / GyroLsbPerDegPerSec;
                gyroYawDegPerSec = (gyr_r[1] - activeData[1]) / GyroLsbPerDegPerSec;
                gyroRollDegPerSec = (gyr_r[2] - activeData[2]) / GyroLsbPerDegPerSec;
            } else {
                gyroPitchDegPerSec = CorrectGyroSample(gyr_r[0], gyroPitchBias, gyroPitchPlus, gyroPitchMinus);
                gyroYawDegPerSec = CorrectGyroSample(gyr_r[1], gyroYawBias, gyroYawPlus, gyroYawMinus);
                gyroRollDegPerSec = CorrectGyroSample(gyr_r[2], gyroRollBias, gyroRollPlus, gyroRollMinus);
            }

            // Accelerometer deliberately NEVER uses activeData, unlike gyro above - the wizard's
            // "hold still" step zero-references whatever raw value it captured at THAT pose,
            // which is correct for gyro (rate is genuinely ~0 at rest in any orientation) but
            // wrong for accel (gravity is NOT ~0 in any orientation - zero-referencing it there
            // wipes out the real gravity vector UpdateCanonicalGyroMouseImu/Player Space need to
            // determine "which way is down"). Confirmed on real hardware: with activeData driving
            // accel, resting |acc_g| read ~0 instead of ~1g, and yaw was misprojected into a
            // diagonal/vertical blend instead of horizontal cursor movement as a direct result.
            // Always use the factory-calibrated (or nominal-fallback) formula instead.
            float accelXG = CorrectAccelSample(acc_r[0], accelXPlus, accelXMinus);
            float accelYG = CorrectAccelSample(acc_r[1], accelYPlus, accelYMinus);
            float accelZG = CorrectAccelSample(acc_r[2], accelZPlus, accelZMinus);

            // Keep all three DualSense gyro channels in the same handed sensor frame as its
            // accelerometer before UpdateCanonicalGyroMouseImu applies the shared proper rotation.
            // A long, flat yaw capture makes this invariant directly observable: the physical
            // rotation axis is gravity, so the calibrated gyro vector must be collinear with the
            // accelerometer vector. Pitch and yaw already were; negating only roll made that one
            // component point the opposite way, creating a fake ~10%-of-yaw roll rate. Player
            // Space integrated it into an alternating tilt even while raw accelerometer roll
            // stayed fixed. The device's factory-corrected roll sign is therefore retained here.
            gyr_g.X = gyroRollDegPerSec;
            gyr_g.Y = gyroYawDegPerSec;
            // Sign confirmed on real hardware (was -gyroPitchDegPerSec, produced inverted
            // up/down - tilting up moved the cursor down and vice versa).
            gyr_g.Z = gyroPitchDegPerSec;
            // acc_g's channel order must match gyr_g's above, index-for-index - AHRS.Update below
            // fuses gyro integration with gravity-based correction, and if the two sensors don't
            // agree on which index is which physical axis, the fused orientation estimate gets
            // internally confused (confirmed on real hardware: AHRS's "roll" output was tracking
            // pitch motion, not actual roll, because acc_g was still in the old unswapped channel
            // order after gyr_g's X/Z swap above - GyroMouseRollCompensation then misapplied that
            // wrong roll estimate as a curve on straight vertical pitch motion). Same X<->Z swap
            // as gyr_g, accelY (already index 1, already the confirmed gravity-dominant channel)
            // untouched.
            acc_g.X = accelZG;
            acc_g.Y = -accelYG;
            acc_g.Z = -accelXG;

            UpdateCanonicalGyroMouseImu();

            // AHRS is a shared field on Controller (Controller.cs:249), constructed once with a
            // hardcoded 0.005f "5ms sampling rate" - correct for Nintendo's genuinely fixed 3x5ms
            // report cadence, but MadgwickAHRS.Update integrates its quaternion using this
            // SamplePeriod internally regardless of how much real time actually elapsed between
            // calls. This is a second, independent instance of the same class of bug
            // GyroSubSamplePeriod fixed for GyroMousePlayerSpace: DualSense's real report interval
            // (measured above into measuredGyroSubSamplePeriod) is nowhere near 5ms, so AHRS's own
            // tracked orientation - which GyroMouseRollCompensation's wrist-roll correction reads
            // directly via AHRS.GetEulerAngles() - was drifting/rotating at the wrong rate on every
            // single DualSense report, independent of whether GyroMousePlayerSpace's own timing was
            // already fixed. Sync it to the same measured value every report.
            AHRS.SamplePeriod = measuredGyroSubSamplePeriod;

            float deg_to_rad = 0.0174533f;
            AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);

            // Throttled the same ~4/sec as the raw hex dump (this runs every report, far more
            // often) - lets axis-sign verification be read directly (rest flat -> which acc_g
            // axis reads ~1g; rotate around one axis -> which gyr_g axis responds) instead of
            // hand-decoding the raw hex dump for every sample.
            long nowTicks = Stopwatch.GetTimestamp();
            if ((nowTicks - lastDualSenseImuLogTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                lastDualSenseImuLogTimestamp = nowTicks;
                LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                    "IMU: raw gyro=({0},{1},{2}) raw accel=({3},{4},{5}) gyr_g=({6:F1},{7:F1},{8:F1})deg/s acc_g=({9:F2},{10:F2},{11:F2})g dt={12:F3}ms rawTicksDelta={13}",
                    gyr_r[0], gyr_r[1], gyr_r[2], acc_r[0], acc_r[1], acc_r[2],
                    gyr_g.X, gyr_g.Y, gyr_g.Z, acc_g.X, acc_g.Y, acc_g.Z,
                    measuredGyroSubSamplePeriod * 1000.0f, lastLoggedImuDeltaTicks));
            }
        }

        // Applies this specific unit's factory calibration (bias + real sensitivity rescaled onto
        // the nominal GyroLsbPerDegPerSec scale) to one raw gyro sample - see ReadGyroCalibration.
        // Falls back to the nominal scale with zero bias if calibration wasn't read successfully,
        // or if a Plus/Minus pair is degenerate (would otherwise divide by zero).
        private float CorrectGyroSample(Int16 raw, short bias, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / GyroLsbPerDegPerSec;

            float sensNumer = (gyroSpeedPlus + gyroSpeedMinus) * GyroLsbPerDegPerSec;
            float sensDenom = plus - minus;
            float corrected = (raw - bias) * (sensNumer / sensDenom);
            return corrected / GyroLsbPerDegPerSec;
        }

        private float CorrectAccelSample(Int16 raw, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / AccelLsbPerG;

            float range = plus - minus;
            float bias = plus - range / 2.0f;
            float sensNumer = 2.0f * AccelLsbPerG;
            float corrected = (raw - bias) * (sensNumer / range);
            return corrected / AccelLsbPerG;
        }

        // Standard IEEE 802.3 CRC32 (polynomial 0xEDB88320, the same one zlib/most CRC32 libraries
        // use) - DualSense's Bluetooth output reports are silently ignored by the controller unless
        // this checksum is present and correct; USB output needs none.
        private static readonly uint[] crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table() {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        // seed is a virtual leading byte folded into the running CRC state before data - the real
        // DualSense Bluetooth output checksum is computed as if a 0xA2 byte preceded the actual
        // report, without that byte itself being part of the transmitted buffer.
        private static uint Crc32(byte seed, byte[] data, int length) {
            uint crc = 0xFFFFFFFF;
            crc = crc32Table[(crc ^ seed) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < length; i++)
                crc = crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        // DualSense baseline rumble - both motors driven by the same single amplitude value
        // dequeued from rumble_obj (see the Poll() call site), since DualSense's simple dual-motor
        // rumble has no equivalent to Joy-Con's HD-rumble low/high-frequency split Rumble.GetData()
        // encodes. Report layout (motor byte offsets, enable-rumble flags, Bluetooth
        // CRC32-with-0xA2-seed) from DS4Windows's DualSense output-report code.
        private void SendDualSenseRumble(byte leftMotor, byte rightMotor) {
            bool bt = !isUSB;
            int len = bt ? DualSenseMaxReportLen : 64;
            byte[] buf = new byte[len];
            if (bt) {
                buf[0] = 0x31;
                buf[1] = 0x02;
                buf[2] = 0x0F; // enable rumble
                // Required feature-flags byte (mic LED, audio mute, touchpad strips, player lights,
                // motor power) - NOT safe to leave at 0x00 (confirmed on real hardware: omitting
                // this the first time caused continuous, non-stopping rumble).
                buf[3] = 0x55;
                buf[4] = rightMotor;
                buf[5] = leftMotor;
                uint crc = Crc32(0xA2, buf, len - 4);
                buf[len - 4] = (byte)crc;
                buf[len - 3] = (byte)(crc >> 8);
                buf[len - 2] = (byte)(crc >> 16);
                buf[len - 1] = (byte)(crc >> 24);
            } else {
                buf[0] = 0x02;
                buf[1] = 0x0F; // enable rumble
                buf[2] = 0x55; // required feature-flags byte - see the BT branch's comment
                buf[3] = rightMotor;
                buf[4] = leftMotor;
            }
            HIDapi.hid_write(handle, buf, new UIntPtr((uint)len));
        }

        // Sets the DualSense's lightbar to a solid color via an output report - used to give a
        // visible confirmation that the controller is now active on USB right after its stale
        // Bluetooth link gets disconnected (see OnDuplicateRetired). Layout matches
        // SendDualSenseRumble; rumble flags are left at "not in use" since this report isn't
        // rumble-related. RGB offsets (45/46/47 USB, 46/47/48 BT) and the fact that no separate
        // "enable lightbar" bit is needed beyond the same 0x55 feature-flags byte the rumble report
        // already sets - both confirmed via DS4Windows's DualSenseDevice.cs.
        private void SendDualSenseLightbar(byte red, byte green, byte blue) {
            bool bt = !isUSB;
            int len = bt ? DualSenseMaxReportLen : 64;
            byte[] buf = new byte[len];
            if (bt) {
                buf[0] = 0x31;
                buf[1] = 0x02;
                buf[2] = 0x0C; // rumble motors not in use for this report
                buf[3] = 0x55;
                buf[46] = red;
                buf[47] = green;
                buf[48] = blue;
                uint crc = Crc32(0xA2, buf, len - 4);
                buf[len - 4] = (byte)crc;
                buf[len - 3] = (byte)(crc >> 8);
                buf[len - 2] = (byte)(crc >> 16);
                buf[len - 1] = (byte)(crc >> 24);
            } else {
                buf[0] = 0x02;
                buf[1] = 0x0C; // rumble motors not in use for this report
                buf[2] = 0x55;
                buf[45] = red;
                buf[46] = green;
                buf[47] = blue;
            }
            HIDapi.hid_write(handle, buf, new UIntPtr((uint)len));
        }
    }
}
