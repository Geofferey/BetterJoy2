using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Concentus;
using Concentus.Enums;

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
        public override bool HasTouchpad => true;
        public override bool HasAnalogTriggers => true;
        public override bool UsesNintendoProtocol => false;
        public override ControllerKind Kind => ControllerKind.DualSense;
        public override string UsbAudioEndpointNameHint => "Wireless Controller";

        public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2
        protected override byte[] TriggerVal => triggerVal;

        private const int DualSenseMaxReportLen = 78; // Bluetooth report length; USB (64) fits the same buffer
        private bool lightbarTransportKnown;
        private bool lightbarUpdatePending = true;
        private byte lightbarRed;
        private byte lightbarGreen;
        private byte lightbarBlue = 255;
        // The small player-number indicator LEDs below the touchpad - physically separate from
        // the RGB lightbar above but packed into the same output report byte range (see
        // SendDualSenseLightbar/WriteRetainedRumbleAndTriggerState). 0 (all off) by default,
        // matching PlayerLedModes' own Disabled default - see SetLEDByPlayerNum.
        private byte currentPlayerLeds;
        // Every DualSense HID write is serialized here. Bluetooth speaker audio uses the same
        // physical output endpoint as lightbar and rumble state, so those states are folded into
        // the audio carrier while streaming instead of allowing independent reports to collide.
        private readonly object outputReportLock = new object();
        private byte bluetoothOutputSequence;
        private byte currentLeftMotor;
        private byte currentRightMotor;
        // DualSense's two adaptive-trigger blocks are part of the same common output state as
        // rumble, audio, microphone routing, and the lightbar. Keep the encoded hardware state
        // here so every transport writer can compose it instead of competing with a trigger-only
        // HID writer. Each block is the controller-native 11-byte right/left trigger payload.
        private readonly byte[] currentRightTriggerEffect = CreateOffTriggerEffect();
        private readonly byte[] currentLeftTriggerEffect = CreateOffTriggerEffect();
        private bool adaptiveTriggerStateKnown;
        private bool adaptiveTriggerUpdatePending = true;
        private bool bluetoothOutputStateDirty = true;
        private bool lightbarControlReleased;
        private bool connectionLightFlashStarted;
        private long connectionLightColorTimestamp;
        // DualSense common input status[1] bits 0/1 report headphone/microphone presence. -1
        // means no valid input report has established the physical jack state yet.
        private int headphoneConnectionState = -1;
        public bool HeadphonesConnected => Volatile.Read(ref headphoneConnectionState) == 1;
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

        // DualSense Bluetooth speaker transport. These values describe the controller protocol;
        // desktop capture and Opus encoding remain generic in BluetoothAudioCapture.cs.
        private const int BtAudioReportLength = 398;
        private const int BtAudioStateOffset = 13;
        private const int BtAudioStateLength = 63;
        private const int BtAudioHapticsOffset = 76;
        private const int BtAudioHapticsLength = 64;
        private const int BtAudioSpeakerOffset = 142;
        private const int BtAudioSpeakerDataOffset = 144;
        private const int BtAudioOpusFrameLength = 200;
        private const int BtMicrophoneOpusFrameOffset = 3;
        private const int BtMicrophoneOpusFrameLength = 71;
        private const int BtMicrophonePcmFrames = 480;
        private const byte BtAudioSpeakerPacketType = 0x93;
        private const byte BtAudioHeadsetPacketType = 0x96;
        private const byte DualSenseValidCompatibleVibration = 0x01;
        private const byte DualSenseValidHapticsSelect = 0x02;
        private const byte DualSenseValidRightTrigger = 0x04;
        private const byte DualSenseValidLeftTrigger = 0x08;
        private const byte DualSenseValidRumbleAndTriggers =
            DualSenseValidCompatibleVibration | DualSenseValidHapticsSelect |
            DualSenseValidRightTrigger | DualSenseValidLeftTrigger;
        // valid_flag1/power_save_control names and bit values match the Linux hid-playstation
        // driver exactly - this is what actually powers the mic capsule down at the hardware
        // level (see WriteRetainedRumbleAndTriggerState), not just the mute LED.
        private const byte DualSensePowerSaveControlEnable = 0x02; // DS_OUTPUT_VALID_FLAG1_POWER_SAVE_CONTROL_ENABLE
        // Lightbar-related validity bits from dualsense_output_report_common. Bluetooth audio
        // transports that structure inside report 0x36, so its lighting controls must be gated
        // just like ordinary USB 0x02 / Bluetooth 0x31 output reports.
        private const byte DualSenseValidLightbarControl = 0x04;
        private const byte DualSenseValidPlayerIndicatorControl = 0x10;
        private const byte DualSenseValidLightingFlag1 =
            DualSenseValidLightbarControl | DualSenseValidPlayerIndicatorControl;
        private const byte DualSenseValidLedBrightnessControl = 0x01;
        private const byte DualSenseValidLightbarSetupControl = 0x02;
        private const byte DualSenseValidLightingFlag2 =
            DualSenseValidLedBrightnessControl | DualSenseValidLightbarSetupControl;
        private const byte DualSensePowerSaveMicMute = 0x10; // DS_OUTPUT_POWER_SAVE_CONTROL_MIC_MUTE
        // Both trade added latency for jitter tolerance - at ~10.67ms/frame the prior 8/12 values
        // baked in ~85-128ms of steady-state buffering, audible as lag. Trimmed down now that
        // SendQueuedBluetoothAudioIfAny's synthetic-silence frame (see below) already absorbs a
        // brief capture stall without needing a deep prime to hide it, and the dedicated
        // OVERLAPPED write pool (see BluetoothAudioWritePool) already absorbs write-side stalls
        // independently of this queue. Revisit upward only if real hardware shows audible
        // stutter/underrun at these depths - see AudioDebugLog's "DualSenseSend" pendingMinMax/
        // silence counters.
        private const int BtAudioPrimeFrameCount = 3;
        // Bound latency as well as memory - see BtAudioPrimeFrameCount's comment above.
        private const int BtAudioMaximumQueuedFrames = 6;
        private const double BtAudioFrameCadenceMs = 10.0 + (2.0 / 3.0);
        private static readonly byte[] DefaultBluetoothAudioState = {
            0xFD, 0xF7, 0x00, 0x00, 0x64, 0x64, 0xFF, 0x09,
            0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x07, 0x00,
            0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        private readonly ConcurrentQueue<byte[]> bluetoothAudioFrameQueue =
            new ConcurrentQueue<byte[]>();
        private readonly List<byte[]> bluetoothAudioPending = new List<byte[]>();
        private readonly object bluetoothAudioStateLock = new object();
        private volatile bool bluetoothAudioStreaming;
        private byte bluetoothAudioPacketSequence;
        private int bluetoothAudioVolumePercent = -1;
        private string bluetoothAudioEndpointId = String.Empty;
        private bool bluetoothAudioRouteHeadphones;
        private Stopwatch bluetoothAudioStopwatch;
        private double bluetoothAudioNextSendDeadlineMs;
        // Bluetooth audio owns a second, shareable HID session while streaming. Its bounded
        // OVERLAPPED write pool lets Windows keep reports moving during short scheduler/storage
        // stalls without ever racing hidapi reads on this controller's primary handle.
        private BluetoothAudioWritePool bluetoothAudioWritePool;
        private byte[] bluetoothAudioSilenceFrame;
        // Owned exclusively by Poll. AVRT registration and release must happen on the same thread.
        private IntPtr bluetoothAudioMmcssHandle;
        private bool bluetoothAudioMmcssAttempted;
        // Cross-stage timing telemetry. Capture/IPC arrivals are recorded by the helper-pipe
        // thread; the remaining counters belong to Poll under bluetoothAudioStateLock.
        private long bluetoothAudioLastEnqueueTimestamp;
        private long bluetoothAudioMaximumEnqueueGapTicks;
        private long bluetoothAudioFramesEnqueued;
        private double bluetoothAudioLastSendMs;
        private double bluetoothAudioMaximumSendGapMs;
        private double bluetoothAudioMaximumLatenessMs;
        private double bluetoothAudioMaximumSubmitMs;
        private double bluetoothAudioLastDiagnosticMs;
        private long bluetoothAudioSyntheticSilenceFrames;
        private long bluetoothAudioLastSummarySyntheticSilenceFrames;
        private int bluetoothAudioSendsSinceSummary;
        private int bluetoothAudioMinimumPending;
        private int bluetoothAudioMaximumPending;

        // Bluetooth microphone packets share report ID 0x31 with ordinary controller input, but
        // carry a distinct transport tag and a fixed 71-byte, 48 kHz mono Opus frame. Keep the
        // capture/decode worker off the HID poll thread; the controller file owns Sony's framing,
        // while whichever IMicrophoneEndpoint MicrophoneEndpointFactory selects owns only the
        // generic delivery edge (VIIPER's virtual UAC device, or a Virtual Audio Driver render
        // endpoint).
        private readonly ConcurrentQueue<byte[]> bluetoothMicrophoneFrameQueue =
            new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent bluetoothMicrophoneSignal = new AutoResetEvent(false);
        private volatile bool bluetoothMicrophoneRequested;
        private volatile bool bluetoothMicrophoneStreaming;
        // Not Bluetooth-specific despite the neighboring fields - the physical mute button and
        // its LED are the same hardware/report field (mute_button_led, valid_flag1 bit 0 in the
        // Linux hid-playstation driver's dualsense_output_report_common) on both transports, so
        // this tracks the mute toggle regardless of whether Bluetooth mic streaming happens to be
        // active. See WriteRetainedRumbleAndTriggerState.
        private volatile bool microphoneMuted;
        private volatile bool microphoneMuteStatePending;
        // USB has no equivalent to StartBluetoothMicrophone's "genuine fresh start" moment - the
        // mic is a native USB Audio Class endpoint, no BetterJoy-owned capture pipeline to start
        // at all - so ApplyUsbMicrophoneMuteDefault needs its own one-shot latch instead, reset
        // on every fresh Attach so a later reconnect re-applies the Muted default again.
        private volatile bool usbMicrophoneMuteDefaultApplied;
        // Bluetooth's own equivalent latch - StartBluetoothMicrophone only ever runs (and so only
        // ever pushes a mute default) when the Built-in mic mode is Enable or Muted; Disabled
        // never calls it at all, so without this the mute LED/power-save state is never actively
        // pushed to the controller in that mode, leaving whatever state a previous session left it
        // in rather than the Disabled default the UI claims.
        private volatile bool bluetoothMicrophoneMuteDefaultApplied;
        private volatile bool bluetoothMicrophoneDisablePending;
        private volatile bool bluetoothMicrophoneControlPending;
        private Thread bluetoothMicrophoneThread;
        private bool bluetoothSpeakerPrimed;

        private enum AvrtPriority {
            Low = -1,
            Normal = 0,
            High = 1,
            Critical = 2,
        }

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(
            string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvSetMmThreadPriority(
            IntPtr avrtHandle, AvrtPriority priority);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

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

            usbMicrophoneMuteDefaultApplied = false;
            bluetoothMicrophoneMuteDefaultApplied = false;

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
                StopBluetoothMicrophone();
                StopBluetoothAudioStream();
                BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                state = state_.DROPPED;
                AbandonBluetoothMediaTransport();
            }
        }

        public override void SetLightColor(byte red, byte green, byte blue) {
            lock (outputReportLock) {
                // Profile reconciliation reapplies controller options on every scan pass. Once
                // this exact color has reached an initialized transport, another 0x31 report is
                // redundant and can collide with the shared rumble/audio state lane. Preserve the
                // pending path during attach so the first real color write is never suppressed.
                if (lightbarTransportKnown && !lightbarUpdatePending &&
                    lightbarRed == red && lightbarGreen == green &&
                    lightbarBlue == blue)
                    return;

                lightbarRed = red;
                lightbarGreen = green;
                lightbarBlue = blue;
                lightbarUpdatePending = true;
                bluetoothOutputStateDirty = true;
                if (lightbarTransportKnown) {
                    SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue);
                    lightbarUpdatePending = false;
                }
            }
        }

        // Sony's own player-LED convention (the player_ids table in the Linux kernel's
        // hid-playstation.c dualsense_set_player_leds) - one of 5 patterns for the 5 small LEDs
        // below the touchpad, growing from a single center LED for player 1 up to all five lit
        // for player 5+. Same role as NintendoController.SetLEDByPlayerNum (called from the same
        // RequestLEDUpdate plumbing whenever PadId changes), just Sony's own bit layout instead
        // of Joy-Con's.
        private static readonly byte[] PlayerLedPatterns = {
            0x04, // player 1: center LED only               - BIT(2)
            0x0A, // player 2: two LEDs either side of center - BIT(3)|BIT(1)
            0x15, // player 3: three LEDs                     - BIT(4)|BIT(2)|BIT(0)
            0x1B, // player 4: four LEDs                      - BIT(4)|BIT(3)|BIT(1)|BIT(0)
            0x1F, // player 5+: all five LEDs
        };

        public override void SetLEDByPlayerNum(int id) {
            // false: before this dropdown existed, DualSense's player LEDs were always silently
            // off (see PlayerLedEnabled's own comment) - unlike Joy-Con/Pro, an unset profile
            // should not suddenly start lighting them.
            byte desired = ControllerMappings.PlayerLedEnabled(
                    ControllerMappings.ProfileIdFor(this), false)
                ? PlayerLedPatterns[Math.Max(0, Math.Min(PlayerLedPatterns.Length - 1, id))]
                : (byte)0;

            lock (outputReportLock) {
                if (currentPlayerLeds == desired)
                    return;

                currentPlayerLeds = desired;
                bluetoothOutputStateDirty = true;
                // Reuses the exact same "not yet known, retry once ReceiveRaw confirms transport"
                // path SetLightColor already relies on - SendDualSenseLightbar publishes both the
                // lightbar color and currentPlayerLeds together in one report either way.
                if (lightbarTransportKnown)
                    SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue);
                else
                    lightbarUpdatePending = true;
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
                // The profile lightbar color is applied on the first transport-confirmed read,
                // covering this handoff as well as a fresh USB or Bluetooth connection.
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

                // Mic-duplex frames are media, not controller state. They intentionally use the
                // same 0x31 report ID and length as Bluetooth gamepad input, so they must be
                // separated before any stick/button/IMU parsing. Byte 1 bit 1 identifies the mic
                // lane; byte 2 is its sequence and bytes 3..73 are one fixed Opus frame.
                if (!isUSB && IsBluetoothMicrophoneFrame(dsBuf)) {
                    EnqueueBluetoothMicrophoneFrame(dsBuf);
                    return dsRet;
                }

                lightbarTransportKnown = true;
                if (adaptiveTriggerUpdatePending) {
                    lock (outputReportLock) {
                        if (adaptiveTriggerUpdatePending && !bluetoothAudioStreaming &&
                            !bluetoothMicrophoneStreaming &&
                            !bluetoothMicrophoneDisablePending &&
                            !bluetoothMicrophoneControlPending) {
                            SendAdaptiveTriggerStateLocked();
                            adaptiveTriggerUpdatePending = false;
                        }
                    }
                }
                // Lighting Mode: Default means never touch the LED, not even to confirm a fresh
                // connection - relying on SendDualSenseLightbar's own internal bit-masking to make
                // this call a no-op isn't good enough (today's BT-audio investigation showed that
                // kind of masking can silently fail to gate what it's assumed to), so just never
                // issue it here at all while Default is active. lightbarUpdatePending is
                // deliberately left set (not cleared) so switching the profile away from Default
                // later, even without a reconnect, still applies its assigned color once.
                if (lightbarUpdatePending && !LightingModeIsDefault()) {
                    long lightbarNow = Stopwatch.GetTimestamp();
                    if (!connectionLightFlashStarted) {
                        // Confirm every new USB or Bluetooth connection with a short blue light,
                        // then replace it with the controller profile's assigned color.
                        SendDualSenseLightbar(0, 0, 255);
                        connectionLightFlashStarted = true;
                        connectionLightColorTimestamp = lightbarNow + Stopwatch.Frequency / 4;
                    } else if (lightbarNow >= connectionLightColorTimestamp) {
                        SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue);
                        lightbarUpdatePending = false;
                    }
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
                // Previously never fed for a physical DualSense/DualShock4 (see DualShock4.cs's
                // identical block) - the "DualShock 4 controller"/new DualSense output options
                // silently did nothing for a PlayStation-family physical controller until now.
                if (out_ds4 != null || out_dualsense != null) {
                    var ds4State = MapToDualShock4Input(this);
                    if (out_ds4 != null) {
                        try { out_ds4.UpdateInput(ds4State); } catch (Exception) { }
                    }
                    if (out_dualsense != null) {
                        try { out_dualsense.UpdateInput(ds4State); } catch (Exception) { }
                    }
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

                // byte 6 is the sequence counter (skipped). PS button confirmed via DS4Windows's
                // DualSenseDevice.cs (inputReport[10+ro], bit 0).
                byte btn3 = r[9 + o];
                b[(int)Button.HOME] = (btn3 & 0x01) != 0; // PS button
                b[(int)Button.TOUCHPAD] = (btn3 & 0x02) != 0;
                b[(int)Button.MIC_MUTE] = (btn3 & 0x04) != 0;
                // The actual mute toggle used to live here as a hardcoded check against this one
                // physical button - now a real binding (toggle_built_in_mic, defaulting to this
                // same button), dispatched from DoDeviceSpecificButtonActions once buttons is
                // committed below and combo-matching against it is valid. Still populating the
                // raw state here regardless, since IsComboHeld needs it live either way.
                // Edge paddles remain unmapped; SL/SR have no DualSense equivalent.

                buttons = b;
                CommitButtonState();
            }

            // DualSense contact status bytes are common-report offsets 32 and 36 (absolute
            // Bluetooth offsets 34 and 38). Live touch movement changes the first packed contact
            // at absolute 34 while the second remains the inactive 0x80 record at absolute 38.
            // Everything after these device-specific offsets is shared with the DS4 path.
            SubmitTouchpadReport(ReadPackedTouchContact(r, 32 + o),
                                 ReadPackedTouchContact(r, 36 + o));

            // DualSense packs both capacity and charge state into status[0]: the low nibble is a
            // 10-percent capacity bucket and the high nibble distinguishes discharging, charging,
            // full, thermal/voltage lockout, and charge errors. status[1] is jack/mic detection;
            // using its 0x08 bit as the charge flag made wired controllers appear to discharge.
            byte batteryByte = r[52 + o];
            byte powerStateByte = r[53 + o];
            int nextHeadphoneState = (powerStateByte & 0x03) != 0 ? 1 : 0;
            int previousHeadphoneState = Interlocked.Exchange(
                ref headphoneConnectionState, nextHeadphoneState);
            if (previousHeadphoneState != nextHeadphoneState) {
                // Profile reconciliation owns the existing "Route Bluetooth audio to headphones"
                // policy. Keep capture/pipe work off this HID poll thread; when enabled, insertion
                // starts the 0x96 headset lane and removal stops it immediately.
                ThreadPool.QueueUserWorkItem(_ => Program.mgr?.ApplyControllerProfileOptions());
            }
            int batteryPercent;
            ControllerBatteryStatus batteryState;
            DecodeBatteryStatus(batteryByte, out batteryPercent, out batteryState);
            SetBatteryStatus(batteryPercent, batteryState);
        }

        internal static void DecodeBatteryStatus(byte batteryValue, out int percent,
                                                 out ControllerBatteryStatus status) {
            int capacityBucket = batteryValue & 0x0F;
            int chargingState = (batteryValue >> 4) & 0x0F;

            // Sony reports 0 as 0-9%, 1 as 10-19%, and so on. Use each bucket's midpoint just as
            // the Linux hid-playstation driver and dualsensectl do, except that full has an exact
            // state of its own. A lockout/error state must remain visible instead of masquerading
            // as an ordinary discharge.
            percent = Math.Min(capacityBucket * 10 + 5, 100);
            switch (chargingState) {
                case 0x0:
                    status = ControllerBatteryStatus.Discharging;
                    break;
                case 0x1:
                    status = ControllerBatteryStatus.Charging;
                    break;
                case 0x2:
                    percent = 100;
                    status = ControllerBatteryStatus.Full;
                    break;
                case 0xA: // voltage or temperature outside the charging range
                case 0xB: // temperature error
                    status = ControllerBatteryStatus.NotCharging;
                    break;
                default:  // includes 0xF, the controller's charging-error state
                    status = ControllerBatteryStatus.Unknown;
                    break;
            }
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

        private static bool IsBluetoothMicrophoneFrame(byte[] report) {
            return report != null && report.Length == DualSenseMaxReportLen &&
                report[0] == 0x31 && (report[1] & 0x02) != 0;
        }

        private void EnqueueBluetoothMicrophoneFrame(byte[] report) {
            if (!bluetoothMicrophoneStreaming || report == null ||
                report.Length < BtMicrophoneOpusFrameOffset + BtMicrophoneOpusFrameLength)
                return;

            byte[] frame = new byte[BtMicrophoneOpusFrameLength];
            Buffer.BlockCopy(report, BtMicrophoneOpusFrameOffset, frame, 0, frame.Length);
            bluetoothMicrophoneFrameQueue.Enqueue(frame);
            while (bluetoothMicrophoneFrameQueue.Count > 16)
                bluetoothMicrophoneFrameQueue.TryDequeue(out _);
            bluetoothMicrophoneSignal.Set();
        }

        // startMuted applies microphoneMuted only on a genuine fresh start (the worker thread
        // wasn't already running) - Program.cs's reconciliation loop calls this every ~2 seconds
        // regardless of whether anything changed, and forcing this on every one of those calls
        // would make the physical unmute button unusable, re-muting moments after every press.
        // Always calls SetMicrophoneMuted explicitly, even to unmute - the real hardware mute
        // state can otherwise be left muted from an earlier Disabled/Muted session (or the
        // ApplyBluetoothMicrophoneMuteDefault path below) with nothing to correct it here.
        public void StartBluetoothMicrophone(bool startMuted) {
            if (isUSB || state <= state_.DROPPED)
                return;

            bluetoothMicrophoneRequested = true;
            Thread worker = bluetoothMicrophoneThread;
            if (worker != null && worker.IsAlive)
                return;

            // Deliberately NOT SetMicrophoneMuted(startMuted) - that publishes through the
            // Bluetooth media carrier (EnsureBluetoothMediaTransport), which puts the controller
            // into the mode where it interleaves mic-duplex frames into the same 0x31 report ID
            // as ordinary input (see ReceiveRaw's IsBluetoothMicrophoneFrame comment) - for every
            // reader of the raw device, not just BetterJoy. Doing that here meant simply choosing
            // Muted put the controller in that mode for the entire session even if nothing was
            // ever actually recorded from it. The hardware mute/power-save state still gets
            // published correctly below via the ordinary (non-media-carrier) rumble/lightbar
            // report - WriteRetainedRumbleAndTriggerState writes it regardless. Only
            // BluetoothMicrophoneWorker's own interfaceActive transition (a real recording app
            // actually opening the endpoint) should ever call EnsureBluetoothMediaTransport - and
            // already does, further down.
            microphoneMuted = startMuted;
            microphoneMuteStatePending = true;
            SendDualSenseRumble(currentLeftMotor, currentRightMotor);

            bluetoothMicrophoneThread = new Thread(BluetoothMicrophoneWorker) {
                IsBackground = true,
                Name = "BetterJoyDualSenseMicrophone"
            };
            bluetoothMicrophoneThread.Start();
        }

        // USB's built-in mic is a native USB Audio Class endpoint with no BetterJoy-owned capture
        // pipeline to start - there's no equivalent "genuine fresh start" moment to hang the
        // startMuted default on the way StartBluetoothMicrophone does, so this is applied once
        // per physical connection instead (the usbMicrophoneMuteDefaultApplied latch, reset in
        // Attach). Program.cs's reconciliation loop calls this every ~2 seconds for every USB
        // DualSense regardless of the Built-in mic setting; the latch is what keeps this a true
        // one-shot "on connect" default rather than fighting the physical mute button afterward.
        // Always calls SetMicrophoneMuted explicitly, even to unmute - the real hardware endpoint's
        // mute state is a persistent Windows setting that survives reconnects, so a fresh
        // connection with startMuted=false still has to actively correct a mic left muted from an
        // earlier session, not just skip touching it.
        public void ApplyUsbMicrophoneMuteDefault(bool startMuted) {
            if (!isUSB || usbMicrophoneMuteDefaultApplied)
                return;

            usbMicrophoneMuteDefaultApplied = true;
            SetMicrophoneMuted(startMuted);
        }

        // Bluetooth's equivalent of the above, for the specific case where StartBluetoothMicrophone
        // never runs at all - Built-in mic: Disabled (or Controller audio itself off) never starts
        // the worker, so without this the mute LED/power-save hardware mute is never actively
        // pushed to the controller in that case, leaving whatever state a previous session left it
        // in rather than the Disabled default the UI claims. Program.cs only calls this from the
        // branch where StartBluetoothMicrophone is NOT also being called, so the two never race to
        // set different values on the same connection.
        public void ApplyBluetoothMicrophoneMuteDefault() {
            if (isUSB || bluetoothMicrophoneMuteDefaultApplied)
                return;

            bluetoothMicrophoneMuteDefaultApplied = true;
            SetMicrophoneMuted(true);
        }

        public void StopBluetoothMicrophone() {
            bluetoothMicrophoneRequested = false;
            bluetoothMicrophoneSignal.Set();
            Thread worker = bluetoothMicrophoneThread;
            if (worker != null && worker.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
                worker.Join(1500);
            if (worker == null || !worker.IsAlive)
                bluetoothMicrophoneThread = null;
        }

        private void BluetoothMicrophoneWorker() {
            bool failureReported = false;
            try {
                while (bluetoothMicrophoneRequested && state > state_.DROPPED) {
                    // Muted means fully inert, not just muted PCM - no endpoint, no media
                    // transport, nothing that could put the controller into its mic-duplex report
                    // mode (see StartBluetoothMicrophone's comment) - until the user actually
                    // unmutes. SetMicrophoneMuted signals bluetoothMicrophoneSignal on every
                    // toggle, so this wakes promptly in both directions rather than polling.
                    if (microphoneMuted) {
                        bluetoothMicrophoneSignal.WaitOne(250);
                        continue;
                    }

                    IMicrophoneEndpoint endpoint = null;
                    try {
                        endpoint = MicrophoneEndpointFactory.Open();
                        if (!bluetoothMicrophoneRequested || microphoneMuted)
                            continue;

                        form.AppendTextBox(failureReported
                            ? "DualSense Bluetooth microphone backend recovered.\r\n"
                            : "DualSense Bluetooth microphone is available as a Windows recording device.\r\n");
                        failureReported = false;

                        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
                        short[] monoPcm = new short[BtMicrophonePcmFrames];
                        byte[] stereoPcm = new byte[BtMicrophonePcmFrames * 2 * sizeof(short)];
                        while (bluetoothMicrophoneRequested && state > state_.DROPPED &&
                                !microphoneMuted) {
                            bluetoothMicrophoneSignal.WaitOne(250);
                            bool interfaceActive = endpoint.IsMicrophoneInterfaceActive();
                            if (interfaceActive != bluetoothMicrophoneStreaming) {
                                lock (bluetoothAudioStateLock) {
                                    if (interfaceActive)
                                        EnsureBluetoothMediaTransport();
                                    bluetoothMicrophoneStreaming = interfaceActive;
                                    if (interfaceActive)
                                        bluetoothMicrophoneDisablePending = false;
                                    else
                                        bluetoothMicrophoneDisablePending = true;
                                    lock (outputReportLock)
                                        bluetoothOutputStateDirty = true;
                                    if (!interfaceActive)
                                        StopBluetoothMediaTransportIfIdle();
                                }
                            }

                            byte[] opusFrame;
                            while (bluetoothMicrophoneStreaming &&
                                bluetoothMicrophoneRequested &&
                                bluetoothMicrophoneFrameQueue.TryDequeue(out opusFrame)) {
                                int decoded = decoder.Decode(new ReadOnlySpan<byte>(opusFrame),
                                    new Span<short>(monoPcm), BtMicrophonePcmFrames, false);
                                if (decoded <= 0)
                                    continue;

                                bool muted = microphoneMuted;
                                Array.Clear(stereoPcm, 0, stereoPcm.Length);
                                int frames = Math.Min(decoded, BtMicrophonePcmFrames);
                                if (!muted) {
                                    for (int frame = 0; frame < frames; frame++) {
                                        short sample = monoPcm[frame];
                                        int offset = frame * 4;
                                        stereoPcm[offset] = (byte)sample;
                                        stereoPcm[offset + 1] = (byte)(sample >> 8);
                                        stereoPcm[offset + 2] = (byte)sample;
                                        stereoPcm[offset + 3] = (byte)(sample >> 8);
                                    }
                                }
                                endpoint.WriteMicrophonePcm(stereoPcm);
                            }
                        }
                    } catch (Exception ex) {
                        if (bluetoothMicrophoneRequested && !failureReported) {
                            form.AppendTextBox("DualSense Bluetooth microphone unavailable; " +
                                "retrying automatically: " + ex.Message + "\r\n");
                            failureReported = true;
                        }
                    } finally {
                        CleanupBluetoothMicrophoneAttempt(endpoint);
                    }

                    if (bluetoothMicrophoneRequested && state > state_.DROPPED)
                        bluetoothMicrophoneSignal.WaitOne(10000);
                }
            } finally {
                bluetoothMicrophoneThread = null;
            }
        }

        private void CleanupBluetoothMicrophoneAttempt(
            IMicrophoneEndpoint endpoint) {
            while (bluetoothMicrophoneFrameQueue.TryDequeue(out _)) { }
            endpoint?.Dispose();
            lock (bluetoothAudioStateLock) {
                if (bluetoothMicrophoneStreaming)
                    bluetoothMicrophoneDisablePending = true;
                bluetoothMicrophoneStreaming = false;
                lock (outputReportLock)
                    bluetoothOutputStateDirty = true;
                StopBluetoothMediaTransportIfIdle();
            }
        }

        // toggle_built_in_mic defaults to the physical mute button alone (see
        // ControllerMappings.LegacyValue) but is a real binding like volume_up/lt_haptics, so it
        // can be reassigned to a different chord instead. Same discrete-per-press model; the
        // combo is checked against buttons here rather than in the raw report parser, since that
        // runs before buttons is committed for this report and combo-matching needs it live.
        protected override void DoDeviceSpecificButtonActions() {
            bool held = UpdateDesktopActionComboHeld("toggle_built_in_mic", true, out bool wasHeld);
            if (held && !wasHeld)
                SetMicrophoneMuted(!microphoneMuted);
        }

        private void SetMicrophoneMuted(bool muted) {
            microphoneMuted = muted;
            // Wakes BluetoothMicrophoneWorker immediately in both directions instead of leaving it
            // to its own poll interval - unmuting lets it actually open the endpoint (and, only
            // once something genuinely captures from it, the media transport); muting lets it tear
            // both back down right away. Deliberately no longer eagerly calling
            // EnsureBluetoothMediaTransport/setting bluetoothMicrophoneControlPending here the way
            // this used to (to publish the LED before any app opened the endpoint) - that kept the
            // controller in its mic-duplex report mode (see StartBluetoothMicrophone's comment) on
            // every mute-button press, not just genuine capture. The mute LED still updates
            // immediately below via the ordinary rumble/lightbar report instead.
            bluetoothMicrophoneSignal.Set();
            lock (bluetoothAudioStateLock) {
                lock (outputReportLock)
                    bluetoothOutputStateDirty = true;
            }

            // USB (and Bluetooth outside the media-carrier path above) has no continuously-polled
            // report loop to piggyback the new mute state on - the ordinary rumble/lightbar report
            // is the only channel, so push it out immediately rather than waiting for it to
            // happen to be resent for an unrelated reason. SendDualSenseRumble already skips
            // sending on its own while the Bluetooth media carrier is authoritative instead
            // (bluetoothAudioStreaming/bluetoothMicrophoneStreaming/...), so this is a no-op there.
            microphoneMuteStatePending = true;
            SendDualSenseRumble(currentLeftMotor, currentRightMotor);
        }

        public bool IsStreamingBluetoothAudio => bluetoothAudioStreaming;

        // The 0x36 media clock is shared by speaker output and microphone duplex. Either lane may
        // keep it alive independently; in microphone-only mode valid encoded silence supplies the
        // carrier without starting desktop loopback capture or producing audible output.
        private void EnsureBluetoothMediaTransport() {
            if (bluetoothAudioStopwatch != null)
                return;

            DisposeBluetoothAudioWritePool();
            bluetoothAudioWritePool = BluetoothAudioWritePool.TryOpen(path,
                out int audioHandleError);
            if (bluetoothAudioWritePool == null)
                AudioDebugLog.Write("DualSenseSend",
                    "Dedicated audio handle unavailable error=" + audioHandleError +
                    "; using primary HID handle fallback");
            else
                AudioDebugLog.Write("DualSenseSend",
                    "Dedicated overlapped audio handle opened");

            while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
            bluetoothAudioPending.Clear();
            bluetoothAudioPacketSequence = 0;
            bluetoothSpeakerPrimed = false;
            bluetoothAudioStopwatch = Stopwatch.StartNew();
            bluetoothAudioNextSendDeadlineMs = 0;
            if (bluetoothAudioSilenceFrame == null)
                bluetoothAudioSilenceFrame = CreateBluetoothAudioSilenceFrame();
            Interlocked.Exchange(ref bluetoothAudioLastEnqueueTimestamp, 0);
            Interlocked.Exchange(ref bluetoothAudioMaximumEnqueueGapTicks, 0);
            Interlocked.Exchange(ref bluetoothAudioFramesEnqueued, 0);
            bluetoothAudioLastSendMs = 0;
            bluetoothAudioMaximumSendGapMs = 0;
            bluetoothAudioMaximumLatenessMs = 0;
            bluetoothAudioMaximumSubmitMs = 0;
            bluetoothAudioLastDiagnosticMs = 0;
            bluetoothAudioSyntheticSilenceFrames = 0;
            bluetoothAudioLastSummarySyntheticSilenceFrames = 0;
            bluetoothAudioSendsSinceSummary = 0;
            bluetoothAudioMinimumPending = Int32.MaxValue;
            bluetoothAudioMaximumPending = 0;
        }

        private void StopBluetoothMediaTransportIfIdle() {
            if (bluetoothAudioStreaming || bluetoothMicrophoneStreaming ||
                bluetoothMicrophoneDisablePending ||
                bluetoothMicrophoneControlPending)
                return;
            bluetoothAudioPending.Clear();
            while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
            DisposeBluetoothAudioWritePool();
            bluetoothAudioStopwatch = null;
            bluetoothSpeakerPrimed = false;
        }

        private void AbandonBluetoothMediaTransport() {
            lock (bluetoothAudioStateLock) {
                // Once the physical link is leaving there is no controller left to acknowledge a
                // final FE carrier. Drop the pending barrier so the dedicated HID handles are not
                // retained until process exit.
                bluetoothMicrophoneDisablePending = false;
                bluetoothMicrophoneControlPending = false;
                bluetoothMicrophoneStreaming = false;
                bluetoothAudioStreaming = false;
                StopBluetoothMediaTransportIfIdle();
            }
        }

        public void StartBluetoothAudioStream(int volumePercent, string endpointId,
            bool routeToHeadphones) {
            lock (bluetoothAudioStateLock) {
                if (isUSB || state <= state_.DROPPED)
                    return;

                volumePercent = Math.Max(0, Math.Min(100, volumePercent));
                endpointId = endpointId ?? String.Empty;
                if (bluetoothAudioStreaming) {
                    bool endpointMatches = String.Equals(bluetoothAudioEndpointId, endpointId,
                        StringComparison.Ordinal);
                    if (endpointMatches) {
                        // Volume and route are state bytes inside the same 0x36 carrier. Apply
                        // them on its next packet instead of tearing down the active capture.
                        bluetoothAudioVolumePercent = volumePercent;
                        bluetoothAudioRouteHeadphones = routeToHeadphones;
                        lock (outputReportLock)
                            bluetoothOutputStateDirty = true;
                        return;
                    }

                    StopBluetoothAudioStream();
                }

                if (!form.StartBluetoothAudioCapture(PadId, endpointId,
                    BluetoothAudioCodec.DualSenseOpus)) {
                    return;
                }

                EnsureBluetoothMediaTransport();
                while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
                bluetoothAudioPending.Clear();
                bluetoothSpeakerPrimed = false;
                bluetoothAudioVolumePercent = volumePercent;
                bluetoothAudioEndpointId = endpointId;
                bluetoothAudioRouteHeadphones = routeToHeadphones;
                lock (outputReportLock)
                    bluetoothOutputStateDirty = true;
                bluetoothAudioStreaming = true;
                AudioDebugLog.Write("DualSenseSend", "Start pad=" + PadId +
                    " volume=" + volumePercent + " endpoint=" +
                    (String.IsNullOrEmpty(endpointId) ? "(default)" : endpointId) +
                    " headphones=" + routeToHeadphones);
            }
        }

        public void StopBluetoothAudioStream() {
            bool restoreControllerOutput = false;
            lock (bluetoothAudioStateLock) {
                // Sent unconditionally, before the bluetoothAudioStreaming check below: Start's
                // live-settings-change restart path stops the old stream and starts a new one as
                // two separate fire-and-forget pipe messages with no delivery confirmation, so
                // this flag can end up false while the helper is still actually capturing.
                // OnDetachingWhileAttached is the one guaranteed last chance to clean that up
                // before the handle closes - a stop sent to an already-idle helper is a harmless
                // no-op (BluetoothAudioCapture.Stop is idempotent), but skipping it here when it
                // turns out to be needed orphans the capture with nothing left to ever stop it.
                form.StopBluetoothAudioCapture(PadId);

                if (!bluetoothAudioStreaming)
                    return;

                bluetoothAudioStreaming = false;
                bluetoothAudioVolumePercent = -1;
                bluetoothAudioEndpointId = String.Empty;
                bluetoothAudioRouteHeadphones = false;
                bluetoothSpeakerPrimed = false;
                bluetoothAudioPending.Clear();
                while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
                StopBluetoothMediaTransportIfIdle();
                restoreControllerOutput = !bluetoothMicrophoneStreaming &&
                    state > state_.DROPPED;
                AudioDebugLog.Write("DualSenseSend", "Stop pad=" + PadId);
            }

            // Return ownership to ordinary 0x31 output reports after the media carrier stops.
            // These calls are outside bluetoothAudioStateLock so no pipe/lifecycle work is held
            // across physical HID writes.
            if (restoreControllerOutput) {
                SendDualSenseRumble(currentLeftMotor, currentRightMotor);
                SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue);
            }
        }

        public void EnqueueBluetoothAudioFrame(byte[] frame) {
            if (frame == null || frame.Length != BtAudioOpusFrameLength)
                return;

            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(
                ref bluetoothAudioLastEnqueueTimestamp, now);
            if (previous != 0)
                InterlockedMaximum(ref bluetoothAudioMaximumEnqueueGapTicks,
                    now - previous);
            Interlocked.Increment(ref bluetoothAudioFramesEnqueued);
            bluetoothAudioFrameQueue.Enqueue(frame);
            while (bluetoothAudioFrameQueue.Count > BtAudioMaximumQueuedFrames)
                bluetoothAudioFrameQueue.TryDequeue(out _);
        }

        private void DisposeBluetoothAudioWritePool() {
            BluetoothAudioWritePool pool = bluetoothAudioWritePool;
            bluetoothAudioWritePool = null;
            pool?.Dispose();
        }

        private static void InterlockedMaximum(ref long target, long candidate) {
            long observed = Volatile.Read(ref target);
            while (candidate > observed) {
                long replaced = Interlocked.CompareExchange(ref target,
                    candidate, observed);
                if (replaced == observed)
                    return;
                observed = replaced;
            }
        }

        protected override void SendQueuedBluetoothAudioIfAny() {
            lock (bluetoothAudioStateLock) {
                bool speakerActive = bluetoothAudioStreaming;
                bool microphoneActive = bluetoothMicrophoneStreaming;
                bool microphoneDisablePending = bluetoothMicrophoneDisablePending;
                bool microphoneControlPending = bluetoothMicrophoneControlPending;
                if (!speakerActive && !microphoneActive &&
                    !microphoneDisablePending && !microphoneControlPending) {
                    ReleaseBluetoothAudioPollScheduling();
                    return;
                }

                EnsureBluetoothAudioPollScheduling();

                int dequeued = 0;
                while (bluetoothAudioFrameQueue.TryDequeue(out byte[] queuedFrame)) {
                    bluetoothAudioPending.Add(queuedFrame);
                    dequeued++;
                }
                if (bluetoothAudioPending.Count > BtAudioMaximumQueuedFrames)
                    bluetoothAudioPending.RemoveRange(0,
                        bluetoothAudioPending.Count - BtAudioMaximumQueuedFrames);

                if (speakerActive && !bluetoothSpeakerPrimed) {
                    if (bluetoothAudioPending.Count < BtAudioPrimeFrameCount &&
                        !microphoneActive)
                        return;
                    if (bluetoothAudioPending.Count >= BtAudioPrimeFrameCount) {
                        bluetoothSpeakerPrimed = true;
                        AudioDebugLog.Write("DualSenseSend", "Primed pending=" +
                            bluetoothAudioPending.Count);
                    }
                }

                double nowMs = bluetoothAudioStopwatch.Elapsed.TotalMilliseconds;
                if (nowMs < bluetoothAudioNextSendDeadlineMs)
                    return;

                bool syntheticSilence = !speakerActive || !bluetoothSpeakerPrimed ||
                    bluetoothAudioPending.Count == 0;
                byte[] frame = syntheticSilence
                    ? bluetoothAudioSilenceFrame
                    : bluetoothAudioPending[0];
                if (frame == null)
                    return;

                double latenessMs = nowMs - bluetoothAudioNextSendDeadlineMs;
                double sendGapMs = bluetoothAudioLastSendMs <= 0
                    ? 0
                    : nowMs - bluetoothAudioLastSendMs;
                long submitStartTicks = Stopwatch.GetTimestamp();
                bool submitted;
                lock (outputReportLock) {
                    byte outputSequenceBefore = bluetoothOutputSequence;
                    byte packetSequenceBefore = bluetoothAudioPacketSequence;
                    byte[] report = BuildBluetoothSpeakerReport(frame);
                    bool hardFailure = false;
                    submitted = bluetoothAudioWritePool != null
                        ? bluetoothAudioWritePool.TrySend(report, out hardFailure)
                        : HIDapi.hid_write(handle, report,
                            new UIntPtr((uint)report.Length)) >= 0;
                    if (!submitted && hardFailure) {
                        DisposeBluetoothAudioWritePool();
                        AudioDebugLog.Write("DualSenseSend",
                            "Dedicated audio write failed; using primary HID handle fallback");
                        submitted = HIDapi.hid_write(handle, report,
                            new UIntPtr((uint)report.Length)) >= 0;
                    }

                    if (submitted) {
                        bluetoothOutputStateDirty = false;
                        adaptiveTriggerUpdatePending = false;
                    } else {
                        // Building a carrier reserves both protocol sequence values. A saturated
                        // native pool did not publish it, so preserve contiguous wire sequences for
                        // the retry rather than creating a phantom lost packet ourselves.
                        bluetoothOutputSequence = outputSequenceBefore;
                        bluetoothAudioPacketSequence = packetSequenceBefore;
                    }
                }
                double submitMs = (Stopwatch.GetTimestamp() - submitStartTicks) *
                    1000.0 / Stopwatch.Frequency;

                if (!submitted)
                    return;
                if (syntheticSilence)
                    bluetoothAudioSyntheticSilenceFrames++;
                else
                    bluetoothAudioPending.RemoveAt(0);

                bluetoothAudioLastSendMs = nowMs;
                bluetoothAudioMaximumSendGapMs = Math.Max(
                    bluetoothAudioMaximumSendGapMs, sendGapMs);
                bluetoothAudioMaximumLatenessMs = Math.Max(
                    bluetoothAudioMaximumLatenessMs, latenessMs);
                bluetoothAudioMaximumSubmitMs = Math.Max(
                    bluetoothAudioMaximumSubmitMs, submitMs);
                bluetoothAudioSendsSinceSummary++;
                bluetoothAudioMinimumPending = Math.Min(
                    bluetoothAudioMinimumPending, bluetoothAudioPending.Count);
                bluetoothAudioMaximumPending = Math.Max(
                    bluetoothAudioMaximumPending, bluetoothAudioPending.Count);

                if (nowMs - bluetoothAudioLastDiagnosticMs >= 1000.0) {
                    BluetoothAudioWriteStatus hidStatus = bluetoothAudioWritePool != null
                        ? bluetoothAudioWritePool.GetStatus()
                        : default(BluetoothAudioWriteStatus);
                    long enqueueGapTicks = Interlocked.Exchange(
                        ref bluetoothAudioMaximumEnqueueGapTicks, 0);
                    long enqueued = Interlocked.Exchange(
                        ref bluetoothAudioFramesEnqueued, 0);
                    long intervalSilence = bluetoothAudioSyntheticSilenceFrames -
                        bluetoothAudioLastSummarySyntheticSilenceFrames;
                    AudioDebugLog.Write("DualSenseSend", "sends=" +
                        bluetoothAudioSendsSinceSummary +
                        " maxGapMs=" + bluetoothAudioMaximumSendGapMs.ToString("F2") +
                        " maxLateMs=" + bluetoothAudioMaximumLatenessMs.ToString("F2") +
                        " maxSubmitMs=" + bluetoothAudioMaximumSubmitMs.ToString("F2") +
                        " pendingMinMax=" +
                        (bluetoothAudioMinimumPending == Int32.MaxValue ? 0 :
                            bluetoothAudioMinimumPending) + "/" +
                        bluetoothAudioMaximumPending +
                        " dequeuedLast=" + dequeued +
                        " enqueued=" + enqueued +
                        " maxEnqueueGapMs=" +
                        (enqueueGapTicks * 1000.0 / Stopwatch.Frequency).ToString("F2") +
                        " silence=" + intervalSilence + "/" +
                        bluetoothAudioSyntheticSilenceFrames +
                        (bluetoothAudioWritePool != null
                            ? " hidPending=" + hidStatus.PendingWrites +
                              " hidOldestMs=" + hidStatus.OldestPendingMs.ToString("F2") +
                              " hidMaxCompleteMs=" +
                                  hidStatus.MaximumIntervalCompletionMs.ToString("F2") +
                              " hidSaturated=" + hidStatus.IntervalSaturations +
                              " hidFailures=" + hidStatus.CompletionFailures +
                              " hidShort=" + hidStatus.ShortTransfers
                            : " hid=primary-sync"));
                    bluetoothAudioLastDiagnosticMs = nowMs;
                    bluetoothAudioLastSummarySyntheticSilenceFrames =
                        bluetoothAudioSyntheticSilenceFrames;
                    bluetoothAudioMaximumSendGapMs = 0;
                    bluetoothAudioMaximumLatenessMs = 0;
                    bluetoothAudioMaximumSubmitMs = 0;
                    bluetoothAudioSendsSinceSummary = 0;
                    bluetoothAudioMinimumPending = Int32.MaxValue;
                    bluetoothAudioMaximumPending = 0;
                }

                bluetoothAudioNextSendDeadlineMs += BtAudioFrameCadenceMs;
                if (nowMs - bluetoothAudioNextSendDeadlineMs > BtAudioFrameCadenceMs * 4)
                    bluetoothAudioNextSendDeadlineMs = nowMs + BtAudioFrameCadenceMs;

                // Publish one in-order FE carrier before releasing a microphone-only media
                // clock. Without this barrier the controller can continue transmitting mic input
                // after Windows closes the endpoint because it never observes the disable state.
                if (microphoneDisablePending || microphoneControlPending) {
                    bluetoothMicrophoneDisablePending = false;
                    bluetoothMicrophoneControlPending = false;
                    StopBluetoothMediaTransportIfIdle();
                }
            }
        }

        private void EnsureBluetoothAudioPollScheduling() {
            if (bluetoothAudioMmcssHandle != IntPtr.Zero || bluetoothAudioMmcssAttempted)
                return;

            bluetoothAudioMmcssAttempted = true;
            try {
                uint taskIndex = 0;
                bluetoothAudioMmcssHandle = AvSetMmThreadCharacteristics(
                    "Pro Audio", ref taskIndex);
                if (bluetoothAudioMmcssHandle != IntPtr.Zero) {
                    AvSetMmThreadPriority(bluetoothAudioMmcssHandle,
                        AvrtPriority.Critical);
                    AudioDebugLog.Write("DualSenseSend",
                        "Poll thread registered with MMCSS Pro Audio");
                }
            } catch (DllNotFoundException) {
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            } catch (EntryPointNotFoundException) {
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            }
        }

        private void ReleaseBluetoothAudioPollScheduling() {
            if (bluetoothAudioMmcssHandle != IntPtr.Zero) {
                try {
                    AvRevertMmThreadCharacteristics(bluetoothAudioMmcssHandle);
                } catch { }
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            }
            bluetoothAudioMmcssAttempted = false;
        }

        private static byte[] CreateBluetoothAudioSilenceFrame() {
            try {
                IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 2,
                    OpusApplication.OPUS_APPLICATION_AUDIO);
                encoder.Bitrate = 160000;
                encoder.UseVBR = false;
                encoder.Complexity = 0;
                var samples = new float[480 * 2];
                var frame = new byte[BtAudioOpusFrameLength];
                int encoded = encoder.Encode(new ReadOnlySpan<float>(samples), 480,
                    new Span<byte>(frame), frame.Length);
                return encoded == BtAudioOpusFrameLength ? frame : null;
            } catch {
                return null;
            }
        }

        private byte[] BuildBluetoothSpeakerReport(byte[] opusFrame) {
            byte[] report = new byte[BtAudioReportLength];
            report[0] = 0x36;
            report[1] = (byte)((bluetoothOutputSequence & 0x0F) << 4);
            bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
            report[2] = 0x91;
            report[3] = 0x07;
            // 0xFF enables the controller-to-host microphone lane; 0xFE leaves only host-to-
            // controller audio active. A valid encoded-silence speaker frame remains present in
            // mic-only mode because both directions share this media clock.
            report[4] = bluetoothMicrophoneStreaming ? (byte)0xFF : (byte)0xFE;
            for (int i = 5; i <= 9; i++)
                report[i] = 0x80;
            report[10] = bluetoothAudioPacketSequence++;
            report[11] = 0x90;
            report[12] = BtAudioStateLength;
            Buffer.BlockCopy(DefaultBluetoothAudioState, 0, report, BtAudioStateOffset,
                DefaultBluetoothAudioState.Length);

            // Trigger and LED validity bits are one-shot state strobes. Compatible rumble is the
            // exception: both main-motor bits must remain asserted while either motor is active.
            // Dropping from F3 to F1 immediately after the transition switches the controller
            // back to its audio-haptics lane before ordinary rumble can be felt. A zero-motor
            // transition is still published once through bluetoothOutputStateDirty, after which
            // steady media carriers return to F1.
            if (!bluetoothOutputStateDirty) {
                report[BtAudioStateOffset] &= 0xF0;
                report[BtAudioStateOffset] |= DualSenseValidCompatibleVibration;
                report[BtAudioStateOffset + 1] &= 0x83;
                report[BtAudioStateOffset + 38] = 0;
            }

            if (LightingModeIsDefault()) {
                // A 0x36 carrier contains the complete DualSense output-state structure. Its
                // default template asserts RGB and player-indicator controls in valid_flag1,
                // plus lightbar setup and LED brightness in valid_flag2. Default delegates all
                // lighting to firmware or another application, so strip every lighting ownership
                // bit on every frame. Compatible vibration2 (valid_flag2 bit 2) remains intact.
                report[BtAudioStateOffset + 1] &=
                    unchecked((byte)~DualSenseValidLightingFlag1);
                report[BtAudioStateOffset + 38] &=
                    unchecked((byte)~DualSenseValidLightingFlag2);
            }

            if (bluetoothOutputStateDirty || currentLeftMotor != 0 || currentRightMotor != 0) {
                report[BtAudioStateOffset] |=
                    DualSenseValidCompatibleVibration | DualSenseValidHapticsSelect;
            }

            report[BtAudioStateOffset + 2] = currentRightMotor;
            report[BtAudioStateOffset + 3] = currentLeftMotor;
            byte speakerVolume = MapBluetoothSpeakerVolume(bluetoothAudioVolumePercent);
            report[BtAudioStateOffset + 4] = bluetoothAudioRouteHeadphones
                ? MapBluetoothHeadphoneVolume(bluetoothAudioVolumePercent)
                : speakerVolume;
            report[BtAudioStateOffset + 5] = bluetoothAudioRouteHeadphones
                ? (byte)0
                : speakerVolume;
            // DualSense's physical microphone gain is 0x00..0x40. Muting is intentionally
            // represented both here and in the mute/power-save state below so the hardware LED,
            // captured PCM, and Windows endpoint agree.
            report[BtAudioStateOffset + 6] = bluetoothMicrophoneStreaming
                ? (byte)0x40
                : (byte)0x00;
            report[BtAudioStateOffset + 7] = bluetoothAudioRouteHeadphones
                ? (byte)0x00
                : (byte)0x09;
            WriteMicrophoneMuteState(report, BtAudioStateOffset, microphoneMuted);
            WriteAdaptiveTriggerState(report, BtAudioStateOffset,
                bluetoothOutputStateDirty);
            report[BtAudioStateOffset + 37] = 0x0A;
            report[BtAudioStateOffset + 43] = currentPlayerLeds;
            report[BtAudioStateOffset + 44] = lightbarRed;
            report[BtAudioStateOffset + 45] = lightbarGreen;
            report[BtAudioStateOffset + 46] = lightbarBlue;

            report[BtAudioHapticsOffset] = 0x92;
            report[BtAudioHapticsOffset + 1] = BtAudioHapticsLength;
            report[BtAudioSpeakerOffset] = bluetoothAudioRouteHeadphones
                ? BtAudioHeadsetPacketType
                : BtAudioSpeakerPacketType;
            report[BtAudioSpeakerOffset + 1] = BtAudioOpusFrameLength;
            Buffer.BlockCopy(opusFrame, 0, report, BtAudioSpeakerDataOffset,
                BtAudioOpusFrameLength);

            uint crc = Crc32(0xA2, report, report.Length - 4);
            report[report.Length - 4] = (byte)crc;
            report[report.Length - 3] = (byte)(crc >> 8);
            report[report.Length - 2] = (byte)(crc >> 16);
            report[report.Length - 1] = (byte)(crc >> 24);
            return report;
        }

        private static byte MapBluetoothSpeakerVolume(int volumePercent) {
            if (volumePercent <= 0)
                return 0;
            volumePercent = Math.Min(100, volumePercent);
            return (byte)(0x3D + (volumePercent * (0x64 - 0x3D) + 50) / 100);
        }

        private static byte MapBluetoothHeadphoneVolume(int volumePercent) {
            volumePercent = Math.Max(0, Math.Min(100, volumePercent));
            return (byte)(volumePercent * 0x64 / 100);
        }

        // Applies profile-owned, persistent DualSense trigger effects. These are real hardware
        // adaptive-trigger effects even when the virtual output is XInput/DS4; "pseudo" only
        // describes the fact that a profile supplies the effect instead of native game feedback.
        // The three modes use the public zone packing documented by Nielk1's MIT-licensed
        // TriggerEffectGenerator and cross-checked against hbashton/DS4Windows's Trigger Lab:
        // resistance, weapon wall/break, and vibration.
        public void SetAdaptiveTriggerProfile(
            string leftMode, int leftStartPercent, int leftSecondaryPercent,
            int leftStrengthPercent, string rightMode, int rightStartPercent,
            int rightSecondaryPercent, int rightStrengthPercent) {
            byte[] nextLeft = EncodeAdaptiveTriggerEffect(leftMode, leftStartPercent,
                leftSecondaryPercent, leftStrengthPercent);
            byte[] nextRight = EncodeAdaptiveTriggerEffect(rightMode, rightStartPercent,
                rightSecondaryPercent, rightStrengthPercent);

            lock (outputReportLock) {
                if (adaptiveTriggerStateKnown &&
                    ByteArraysEqual(currentLeftTriggerEffect, nextLeft) &&
                    ByteArraysEqual(currentRightTriggerEffect, nextRight))
                    return;

                Buffer.BlockCopy(nextLeft, 0, currentLeftTriggerEffect, 0,
                    currentLeftTriggerEffect.Length);
                Buffer.BlockCopy(nextRight, 0, currentRightTriggerEffect, 0,
                    currentRightTriggerEffect.Length);
                adaptiveTriggerStateKnown = true;
                adaptiveTriggerUpdatePending = true;
                bluetoothOutputStateDirty = true;

                // Until the first full input report arrives, isUSB is only a constructor-time
                // guess. Defer exactly like the lightbar so the effect gets the correct USB/BT
                // framing after ReceiveRaw establishes the physical transport.
                if (!lightbarTransportKnown)
                    return;

                // A streaming Bluetooth media report owns this HID lane and will publish the
                // dirty state on its next frame. USB and idle Bluetooth can apply immediately.
                if (!isUSB && (bluetoothAudioStreaming || bluetoothMicrophoneStreaming ||
                    bluetoothMicrophoneDisablePending || bluetoothMicrophoneControlPending))
                    return;

                SendAdaptiveTriggerStateLocked();
                adaptiveTriggerUpdatePending = false;
            }
        }

        private void SendAdaptiveTriggerStateLocked() {
            bool bt = !isUSB;
            int len = bt ? DualSenseMaxReportLen : 64;
            byte[] report = new byte[len];
            int commonOffset;
            if (bt) {
                report[0] = 0x31;
                report[1] = (byte)(bluetoothOutputSequence << 4);
                bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
                report[2] = 0x10;
                commonOffset = 3;
            } else {
                report[0] = 0x02;
                commonOffset = 1;
            }

            WriteAdaptiveTriggerState(report, commonOffset, true);
            if (bt) {
                uint crc = Crc32(0xA2, report, report.Length - 4);
                report[report.Length - 4] = (byte)crc;
                report[report.Length - 3] = (byte)(crc >> 8);
                report[report.Length - 2] = (byte)(crc >> 16);
                report[report.Length - 1] = (byte)(crc >> 24);
            }
            HIDapi.hid_write(handle, report, new UIntPtr((uint)report.Length));
        }

        private void WriteAdaptiveTriggerState(byte[] report, int commonOffset,
                                               bool enableEffects) {
            // valid_flag0 bits 2/3 select the right/left trigger blocks. The common structure
            // starts at USB byte 1, ordinary BT byte 3, and BtAudioStateOffset in report 0x36.
            if (enableEffects)
                report[commonOffset] |= 0x0C;
            Buffer.BlockCopy(currentRightTriggerEffect, 0, report, commonOffset + 10,
                currentRightTriggerEffect.Length);
            Buffer.BlockCopy(currentLeftTriggerEffect, 0, report, commonOffset + 21,
                currentLeftTriggerEffect.Length);
        }

        // Single source of truth for the mute LED and the real hardware mic-mute bit: both bytes
        // only ever come from here, from the same muted bool, so no other code path can write one
        // without the other and let the LED drift from what the mic hardware is actually doing.
        // There's no hardware-readback status to double check this against instead (checked
        // against Sony's own Linux driver source - it doesn't exist), so keeping these two writes
        // structurally inseparable is the strongest guarantee the protocol allows. The LED's own
        // on/off meaning is separately customizable (MicIndicatorLedByte, the "Mic indicator"
        // profile option) - but power_save_control here always reflects the real muted state
        // exactly, regardless of that setting, since the actual hardware mute must never be
        // allowed to drift from what SetMicrophoneMuted's caller asked for.
        private void WriteMicrophoneMuteState(byte[] report, int offset, bool muted) {
            report[offset + 8] = MicIndicatorLedByte(muted); // mute_button_led
            report[offset + 9] = muted ? DualSensePowerSaveMicMute : (byte)0; // power_save_control
        }

        // "Mic indicator" profile option: what the mute-button LED actually shows, independent of
        // the real hardware mute state it's paired with above. Enabled matches Sony's own default
        // behavior (mute_button_led = mic_muted); Inverted flips it (lit = active, not muted);
        // Disabled never lights it; EnabledWhileDisabled only lights it when Built-in mic itself
        // is set to Disabled, ignoring the runtime mute toggle entirely otherwise.
        private byte MicIndicatorLedByte(bool muted) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);

            switch (ControllerMappings.MicIndicatorMode(mappingProfileId)) {
                case ControllerMappings.MicIndicatorModeDisabled:
                    return 0;
                case ControllerMappings.MicIndicatorModeInverted:
                    return muted ? (byte)0 : (byte)1;
                case ControllerMappings.MicIndicatorModeEnabledWhileDisabled:
                    return ControllerMappings.BluetoothMicrophoneMode(mappingProfileId) ==
                        ControllerMappings.ModeDisable ? (byte)1 : (byte)0;
                default: // MicIndicatorModeEnabled
                    return muted ? (byte)1 : (byte)0;
            }
        }

        // Read once per report rather than cached on the instance - LightingMode can change at
        // any time via a live profile edit, and this needs to reflect whatever's current on every
        // single write, not a stale snapshot from Attach.
        private bool LightingModeIsDefault() {
            return ControllerMappings.LightingMode(ControllerMappings.ProfileIdFor(this)) ==
                ControllerMappings.LightingModeDefault;
        }

        private void WriteRetainedRumbleAndTriggerState(byte[] report, int commonOffset,
                                                        byte leftMotor, byte rightMotor) {
            // A rumble publication enables both compatibility-rumble bits and both trigger
            // blocks. The trigger payloads must therefore accompany it; advertising 0x0C while
            // leaving those bytes zero silently replaces the profile's adaptive-trigger state.
            report[commonOffset] = DualSenseValidRumbleAndTriggers;
            // 0x55 already sets valid_flag1 bit 0 (DS_OUTPUT_VALID_FLAG1_MIC_MUTE_LED_CONTROL_
            // ENABLE in the Linux hid-playstation driver), claiming authority over the mute LED
            // byte below on every single report - previously that byte was just left at its
            // zero-initialized default, meaning every rumble/lightbar report silently forced the
            // controller back to unmuted regardless of what the physical button had set.
            // Also OR in bit 1 (DS_OUTPUT_VALID_FLAG1_POWER_SAVE_CONTROL_ENABLE) so
            // power_save_control below actually takes effect - mute_button_led only ever
            // controlled the LED (confirmed against the same Linux driver's naming; the physical
            // button's own mute state has never affected the mic hardware, on real hardware or in
            // any OS), power_save_control's DS_OUTPUT_POWER_SAVE_CONTROL_MIC_MUTE bit (BIT 4) is
            // the real thing - it's what actually powers the mic capsule down at the hardware
            // level, the same control Sony's own driver uses. DualSenseValidLightbarControl is
            // conditionally dropped so Lighting Mode: Default leaves the physical lightbar alone -
            // see that constant's own comment.
            byte validFlag1 = (byte)(0x55 | DualSensePowerSaveControlEnable);
            if (LightingModeIsDefault())
                validFlag1 &= unchecked((byte)~DualSenseValidLightingFlag1);
            report[commonOffset + 1] = validFlag1;
            report[commonOffset + 2] = rightMotor;
            report[commonOffset + 3] = leftMotor;
            WriteMicrophoneMuteState(report, commonOffset, microphoneMuted);
            WriteAdaptiveTriggerState(report, commonOffset, true);
            // 0x55 above already includes bit 4 (DS_OUTPUT_VALID_FLAG1_PLAYER_INDICATOR_CONTROL_
            // ENABLE), claiming authority over player_leds on every report through this shared
            // path (rumble, adaptive triggers) the same way it already does for mute_button_led -
            // omitting this write would silently blank the player-number LEDs on the next rumble
            // or trigger update, the same class of bug that motivated writing mute_button_led here.
            report[commonOffset + 43] = currentPlayerLeds;
            report[commonOffset + 44] = lightbarRed;
            report[commonOffset + 45] = lightbarGreen;
            report[commonOffset + 46] = lightbarBlue;
        }

        private static byte[] EncodeAdaptiveTriggerEffect(string mode, int startPercent,
                                                           int secondaryPercent,
                                                           int strengthPercent) {
            mode = (mode ?? "off").Trim().ToLowerInvariant();
            int strength = PercentToTriggerStrength(strengthPercent);
            if ((mode != "resistance" && mode != "weapon" && mode != "vibration") ||
                strength == 0)
                return CreateOffTriggerEffect();

            int start = PercentToTriggerPosition(startPercent);
            if (mode == "weapon") {
                start = Math.Max(2, Math.Min(7, start));
                int wall = Math.Max(start + 1,
                    Math.Min(8, PercentToTriggerPosition(secondaryPercent)));
                int zones = (1 << start) | (1 << wall);
                byte[] effect = new byte[11];
                effect[0] = 0x25;
                effect[1] = (byte)zones;
                effect[2] = (byte)(zones >> 8);
                effect[3] = (byte)((strength - 1) & 0x07);
                return effect;
            }

            byte effectMode = mode == "vibration" ? (byte)0x26 : (byte)0x21;
            int activeZones = 0;
            uint packedStrength = 0;
            uint value = (uint)((strength - 1) & 0x07);
            for (int zone = start; zone < 10; zone++) {
                activeZones |= 1 << zone;
                packedStrength |= value << (3 * zone);
            }

            byte[] zoneEffect = new byte[11];
            zoneEffect[0] = effectMode;
            zoneEffect[1] = (byte)activeZones;
            zoneEffect[2] = (byte)(activeZones >> 8);
            zoneEffect[3] = (byte)packedStrength;
            zoneEffect[4] = (byte)(packedStrength >> 8);
            zoneEffect[5] = (byte)(packedStrength >> 16);
            zoneEffect[6] = (byte)(packedStrength >> 24);
            if (effectMode == 0x26)
                zoneEffect[9] = (byte)Math.Max(1,
                    (ClampPercent(secondaryPercent) * 28 + 50) / 100);
            return zoneEffect;
        }

        private static byte[] CreateOffTriggerEffect() {
            byte[] effect = new byte[11];
            effect[0] = 0x05;
            return effect;
        }

        private static int PercentToTriggerStrength(int percent) {
            percent = ClampPercent(percent);
            return percent == 0 ? 0 : Math.Max(1, (percent * 8 + 99) / 100);
        }

        private static int PercentToTriggerPosition(int percent) {
            return Math.Min(9, (ClampPercent(percent) + 5) / 10);
        }

        private static int ClampPercent(int percent) {
            return Math.Max(0, Math.Min(100, percent));
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right) {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++) {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        protected override void OnDetachingWhileAttached() {
            StopBluetoothMicrophone();
            StopBluetoothAudioStream();
            AbandonBluetoothMediaTransport();
        }

        // DualSense baseline rumble - both motors driven by the same single amplitude value
        // dequeued from rumble_obj (see the Poll() call site), since DualSense's simple dual-motor
        // rumble has no equivalent to Joy-Con's HD-rumble low/high-frequency split Rumble.GetData()
        // encodes. Report layout (motor byte offsets, enable-rumble flags, Bluetooth
        // CRC32-with-0xA2-seed) from DS4Windows's DualSense output-report code.
        private void SendDualSenseRumble(byte leftMotor, byte rightMotor) {
            lock (outputReportLock) {
                // Disabling rumble calls StopRumble during every profile reconciliation. Do not
                // emit a fresh zero-motor 0x31 report when the physical state is already stopped:
                // on Bluetooth that report shares state with the lightbar and alternated with the
                // profile color report, visibly strobing whenever headphone-gated audio was idle.
                // A real nonzero -> zero transition still falls through and sends the stop. A
                // pending mic-mute toggle also forces this through even at rest - it's the only
                // report that carries mute_button_led outside the Bluetooth media carrier, so
                // skipping it here would silently drop the mute press whenever rumble was idle.
                if (leftMotor == 0 && rightMotor == 0 &&
                    currentLeftMotor == 0 && currentRightMotor == 0 &&
                    !microphoneMuteStatePending)
                    return;

                currentLeftMotor = leftMotor;
                currentRightMotor = rightMotor;
                bluetoothOutputStateDirty = true;
                if (!isUSB && (bluetoothAudioStreaming ||
                    bluetoothMicrophoneStreaming || bluetoothMicrophoneDisablePending ||
                    bluetoothMicrophoneControlPending)) {
                    // The Bluetooth media carrier (bluetoothMicrophoneControlPending) is the
                    // authoritative channel for mute state while it's active, not this report.
                    microphoneMuteStatePending = false;
                    return;
                }
                microphoneMuteStatePending = false;

                bool bt = !isUSB;
                int len = bt ? DualSenseMaxReportLen : 64;
                byte[] buf = new byte[len];
                int commonOffset;
                if (bt) {
                    buf[0] = 0x31;
                    buf[1] = (byte)(bluetoothOutputSequence << 4);
                    bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
                    buf[2] = 0x10;
                    commonOffset = 3;
                    WriteRetainedRumbleAndTriggerState(
                        buf, commonOffset, leftMotor, rightMotor);
                    uint crc = Crc32(0xA2, buf, len - 4);
                    buf[len - 4] = (byte)crc;
                    buf[len - 3] = (byte)(crc >> 8);
                    buf[len - 2] = (byte)(crc >> 16);
                    buf[len - 1] = (byte)(crc >> 24);
                } else {
                    buf[0] = 0x02;
                    commonOffset = 1;
                    WriteRetainedRumbleAndTriggerState(
                        buf, commonOffset, leftMotor, rightMotor);
                }
                HIDapi.hid_write(handle, buf, new UIntPtr((uint)len));
            }
        }

        // The USB audio endpoint's first pair is ordinary audio; its second pair drives the
        // voice-coil actuators. ControllerAudio keeps the test tone off that actuator pair. This
        // report sends the right audio channel to the built-in mono speaker and sets its volume -
        // or, when the aux jack is occupied, both channels to the headphones instead.
        public override void PrepareUsbAudio(int volumePercent) {
            if (!isUSB || state <= state_.DROPPED)
                return;

            lock (outputReportLock) {
                volumePercent = Math.Max(0, Math.Min(100, volumePercent));
                byte[] buf = new byte[64];
                buf[0] = 0x02;
                buf[1] = 0xB0; // headphone volume + speaker volume + audio routing are valid
                buf[5] = (byte)(volumePercent * 0x7F / 100);
                buf[6] = (byte)(volumePercent * 0x64 / 100);
                // byte 8 bits 4-5 (DS_OUTPUT_AUDIO_FLAGS_OUTPUT_PATH_SEL in the Linux
                // hid-playstation driver, cross-checked against this codebase's own pre-existing
                // 0x30 speaker-only value): 0x30 mutes headphones and routes the right channel to
                // the internal speaker, 0x00 mutes the speaker and routes both channels to
                // headphones. This never read HeadphonesConnected at all before, so plugging in
                // headphones over USB never routed audio to them.
                buf[8] = HeadphonesConnected ? (byte)0x00 : (byte)0x30;
                HIDapi.hid_write(handle, buf, new UIntPtr((uint)buf.Length));
            }
        }

        // Sets the DualSense's lightbar to the profile's solid RGB color, plus the current player-
        // number indicator LEDs (currentPlayerLeds - see SetLEDByPlayerNum), via one output
        // report. Layout matches SendDualSenseRumble; rumble flags are left at "not in use" since
        // this report isn't rumble-related. RGB offsets (45/46/47 USB, 46/47/48 BT) and the fact
        // that no separate "enable lightbar" bit is needed beyond the same 0x55 feature-flags byte
        // the rumble report already sets - both confirmed via DS4Windows's DualSenseDevice.cs.
        // player_leds sits at USB offset 44 / BT commonOffset+43 (one byte before lightbar red) -
        // confirmed against the Linux kernel's hid-playstation.c dualsense_output_report_common
        // struct, cross-checked against this same function's own already-working RGB offsets.
        private void SendDualSenseLightbar(byte red, byte green, byte blue) {
            lock (outputReportLock) {
                bluetoothOutputStateDirty = true;
                bool bt = !isUSB;
                // This helper carries both RGB and player-indicator state. Default delegates all
                // lighting, even when the separate Player LEDs option is enabled. Retain the
                // desired state as pending so leaving Default can apply it without reconnecting,
                // but do not publish any LED command while Default is active.
                bool lightingDefault = LightingModeIsDefault();
                if (lightingDefault) {
                    lightbarUpdatePending = true;
                    return;
                }
                if (!bt) {
                    const int len = 64;
                    byte[] buf = new byte[len];
                    buf[0] = 0x02;
                    buf[1] = DualSenseValidRightTrigger | DualSenseValidLeftTrigger;
                    buf[2] = 0x55;
                    WriteAdaptiveTriggerState(buf, 1, true);
                    buf[44] = currentPlayerLeds;
                    buf[45] = red;
                    buf[46] = green;
                    buf[47] = blue;
                    HIDapi.hid_write(handle, buf, new UIntPtr((uint)len));
                    return;
                }

                if (bluetoothAudioStreaming || bluetoothMicrophoneStreaming ||
                    bluetoothMicrophoneDisablePending ||
                    bluetoothMicrophoneControlPending)
                    return;

                if (!lightbarControlReleased) {
                    byte[] setup = CreateDualSenseBluetoothLightbarReport();
                    const int commonOffset = 3;
                    setup[commonOffset + 38] =
                        DualSenseValidLightbarSetupControl;
                    setup[commonOffset + 41] = 0x02; // release startup animation ownership
                    WriteDualSenseBluetoothLightbarReport(setup);
                    lightbarControlReleased = true;
                }

                byte[] color = CreateDualSenseBluetoothLightbarReport();
                const int colorCommonOffset = 3;
                // 0x04 lightbar control enable | 0x10 player-indicator control enable
                // (DS_OUTPUT_VALID_FLAG1_PLAYER_INDICATOR_CONTROL_ENABLE) - without the latter the
                // controller ignores player_leds below on this particular report.
                color[colorCommonOffset + 1] =
                    DualSenseValidLightbarControl |
                    DualSenseValidPlayerIndicatorControl;
                color[colorCommonOffset + 43] = currentPlayerLeds;
                color[colorCommonOffset + 44] = red;
                color[colorCommonOffset + 45] = green;
                color[colorCommonOffset + 46] = blue;
                WriteDualSenseBluetoothLightbarReport(color);
            }
        }

        private byte[] CreateDualSenseBluetoothLightbarReport() {
            byte[] buf = new byte[DualSenseMaxReportLen];
            buf[0] = 0x31;
            buf[1] = (byte)(bluetoothOutputSequence << 4);
            bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
            buf[2] = 0x10;
            return buf;
        }

        private void WriteDualSenseBluetoothLightbarReport(byte[] buf) {
            uint crc = Crc32(0xA2, buf, buf.Length - 4);
            buf[buf.Length - 4] = (byte)crc;
            buf[buf.Length - 3] = (byte)(crc >> 8);
            buf[buf.Length - 2] = (byte)(crc >> 16);
            buf[buf.Length - 1] = (byte)(crc >> 24);
            HIDapi.hid_write(handle, buf, new UIntPtr((uint)buf.Length));
        }

        private struct BluetoothAudioWriteStatus {
            public readonly int PendingWrites;
            public readonly long CompletionFailures;
            public readonly long ShortTransfers;
            public readonly long IntervalSaturations;
            public readonly double OldestPendingMs;
            public readonly double MaximumIntervalCompletionMs;

            public BluetoothAudioWriteStatus(int pendingWrites,
                long completionFailures, long shortTransfers,
                long intervalSaturations, double oldestPendingMs,
                double maximumIntervalCompletionMs) {
                PendingWrites = pendingWrites;
                CompletionFailures = completionFailures;
                ShortTransfers = shortTransfers;
                IntervalSaturations = intervalSaturations;
                OldestPendingMs = oldestPendingMs;
                MaximumIntervalCompletionMs = maximumIntervalCompletionMs;
            }
        }

        // DualSense 0x36 media carriers are ordinary HID output reports. Keep a bounded number of
        // native OVERLAPPED writes in flight on a second shared file session so a transient Windows
        // Bluetooth/HIDCLASS completion stall does not block the controller's input Poll thread.
        // The primary hidapi handle remains the sole input owner.
        private sealed class BluetoothAudioWritePool : IDisposable {
            private const int SlotCount = 32;
            private const int NativeBackingBufferLength = 640;
            private const uint GenericWrite = 0x40000000;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint OpenExisting = 3;
            private const uint FileFlagOverlapped = 0x40000000;
            private const uint WaitObject0 = 0;
            private const int ErrorIoPending = 997;
            private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeOverlappedState {
                public IntPtr Internal;
                public IntPtr InternalHigh;
                public uint Offset;
                public uint OffsetHigh;
                public IntPtr EventHandle;
            }

            private readonly object gate = new object();
            private readonly IntPtr nativeHandle;
            private readonly byte[][] buffers = new byte[SlotCount][];
            private readonly GCHandle[] pins = new GCHandle[SlotCount];
            private readonly IntPtr[] events = new IntPtr[SlotCount];
            private readonly IntPtr[] overlapped = new IntPtr[SlotCount];
            private readonly bool[] outstanding = new bool[SlotCount];
            private readonly int[] expectedLengths = new int[SlotCount];
            private readonly long[] submittedTimestamps = new long[SlotCount];
            private int nextSlot;
            private bool disposed;
            private long completionFailures;
            private long shortTransfers;
            private long maximumIntervalCompletionTicks;
            private long intervalSaturations;

            private BluetoothAudioWritePool(IntPtr nativeHandle) {
                this.nativeHandle = nativeHandle;
                try {
                    int overlappedSize = Marshal.SizeOf(typeof(NativeOverlappedState));
                    for (int slot = 0; slot < SlotCount; slot++) {
                        buffers[slot] = new byte[NativeBackingBufferLength];
                        pins[slot] = GCHandle.Alloc(buffers[slot],
                            GCHandleType.Pinned);
                        events[slot] = CreateEventW(IntPtr.Zero, true, true, null);
                        if (events[slot] == IntPtr.Zero)
                            throw new IOException(
                                "Could not create a DualSense audio completion event.");
                        overlapped[slot] = Marshal.AllocHGlobal(overlappedSize);
                        ResetOverlapped(slot);
                    }
                } catch {
                    ReleaseAllocatedSlots(false);
                    throw;
                }
            }

            public static BluetoothAudioWritePool TryOpen(string devicePath,
                out int error) {
                error = 0;
                if (String.IsNullOrEmpty(devicePath)) {
                    error = 87;
                    return null;
                }

                // Write-only: this pool only ever calls WriteFile. Requesting GENERIC_READ too
                // used to leave a second read-capable handle sitting on the same Bluetooth HID
                // device for as long as the media transport was active (on top of the primary
                // handle and whatever else - a game - has it open), which correlated with
                // duplicate input reports reaching other readers. Unverified as the actual
                // mechanism, but this handle never reads, so the access it requests should match.
                IntPtr nativeHandle = CreateFileW(devicePath,
                    GenericWrite, FileShareRead | FileShareWrite,
                    IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                if (nativeHandle == IntPtr.Zero ||
                    nativeHandle == InvalidHandleValue) {
                    error = Marshal.GetLastWin32Error();
                    return null;
                }

                try {
                    return new BluetoothAudioWritePool(nativeHandle);
                } catch {
                    error = Marshal.GetLastWin32Error();
                    CloseHandle(nativeHandle);
                    return null;
                }
            }

            public bool TrySend(byte[] report, out bool hardFailure) {
                hardFailure = false;
                if (report == null || report.Length == 0 ||
                    report.Length > NativeBackingBufferLength) {
                    hardFailure = true;
                    return false;
                }

                lock (gate) {
                    if (disposed) {
                        hardFailure = true;
                        return false;
                    }

                    if (!ReapCompletedNoLock()) {
                        hardFailure = true;
                        return false;
                    }

                    int slot = FindFreeSlotNoLock();
                    if (slot < 0) {
                        intervalSaturations++;
                        return false;
                    }

                    if (!SubmitNoLock(slot, report)) {
                        completionFailures++;
                        hardFailure = true;
                        return false;
                    }
                    return true;
                }
            }

            private int FindFreeSlotNoLock() {
                for (int offset = 0; offset < SlotCount; offset++) {
                    int candidate = (nextSlot + offset) % SlotCount;
                    if (!outstanding[candidate])
                        return candidate;
                }
                return -1;
            }

            private bool SubmitNoLock(int slot, byte[] report) {
                Array.Clear(buffers[slot], 0, buffers[slot].Length);
                Buffer.BlockCopy(report, 0, buffers[slot], 0, report.Length);
                ResetEvent(events[slot]);
                ResetOverlapped(slot);
                bool completedSynchronously = WriteFile(nativeHandle,
                    pins[slot].AddrOfPinnedObject(), (uint)report.Length,
                    IntPtr.Zero, overlapped[slot]);
                int error = completedSynchronously ? 0 : Marshal.GetLastWin32Error();
                if (!completedSynchronously && error != ErrorIoPending) {
                    SetEvent(events[slot]);
                    AudioDebugLog.Write("DualSenseSend",
                        "Overlapped audio submit failed error=" + error);
                    return false;
                }

                outstanding[slot] = true;
                expectedLengths[slot] = report.Length;
                submittedTimestamps[slot] = Stopwatch.GetTimestamp();
                nextSlot = (slot + 1) % SlotCount;
                return true;
            }

            private bool ReapCompletedNoLock() {
                bool success = true;
                for (int slot = 0; slot < SlotCount; slot++) {
                    if (!outstanding[slot] ||
                        WaitForSingleObject(events[slot], 0) != WaitObject0)
                        continue;

                    bool completed = GetOverlappedResult(nativeHandle,
                        overlapped[slot], out uint transferred, false);
                    long completionTicks = Stopwatch.GetTimestamp() -
                        submittedTimestamps[slot];
                    maximumIntervalCompletionTicks = Math.Max(
                        maximumIntervalCompletionTicks, completionTicks);
                    outstanding[slot] = false;
                    if (!completed) {
                        completionFailures++;
                        success = false;
                    } else if (transferred != 0 &&
                        transferred < expectedLengths[slot]) {
                        shortTransfers++;
                    }
                }
                return success;
            }

            public BluetoothAudioWriteStatus GetStatus() {
                lock (gate) {
                    if (disposed)
                        return default(BluetoothAudioWriteStatus);

                    ReapCompletedNoLock();
                    int pending = 0;
                    long oldestTicks = 0;
                    long now = Stopwatch.GetTimestamp();
                    for (int slot = 0; slot < SlotCount; slot++) {
                        if (!outstanding[slot])
                            continue;
                        pending++;
                        oldestTicks = Math.Max(oldestTicks,
                            now - submittedTimestamps[slot]);
                    }

                    long intervalMaximum = maximumIntervalCompletionTicks;
                    long saturations = intervalSaturations;
                    maximumIntervalCompletionTicks = 0;
                    intervalSaturations = 0;
                    return new BluetoothAudioWriteStatus(pending,
                        completionFailures, shortTransfers, saturations,
                        oldestTicks * 1000.0 / Stopwatch.Frequency,
                        intervalMaximum * 1000.0 / Stopwatch.Frequency);
                }
            }

            private void ResetOverlapped(int slot) {
                var value = new NativeOverlappedState {
                    EventHandle = events[slot]
                };
                Marshal.StructureToPtr(value, overlapped[slot], false);
            }

            public void Dispose() {
                lock (gate) {
                    if (disposed)
                        return;
                    disposed = true;
                    ReleaseAllocatedSlots(true);
                    CloseHandle(nativeHandle);
                }
            }

            private void ReleaseAllocatedSlots(bool cancelOutstanding) {
                for (int slot = 0; slot < SlotCount; slot++) {
                    bool safeToFree = true;
                    if (events[slot] != IntPtr.Zero && cancelOutstanding &&
                        outstanding[slot] &&
                        WaitForSingleObject(events[slot], 0) != WaitObject0) {
                        CancelIoEx(nativeHandle, overlapped[slot]);
                        safeToFree = WaitForSingleObject(events[slot], 250) ==
                            WaitObject0;
                    }
                    if (!safeToFree) {
                        // Kernel I/O can still own this memory. A bounded teardown leak is safer
                        // than freeing a live OVERLAPPED structure or pinned report buffer.
                        events[slot] = IntPtr.Zero;
                        overlapped[slot] = IntPtr.Zero;
                        pins[slot] = default(GCHandle);
                        continue;
                    }

                    if (events[slot] != IntPtr.Zero) {
                        CloseHandle(events[slot]);
                        events[slot] = IntPtr.Zero;
                    }
                    if (overlapped[slot] != IntPtr.Zero) {
                        Marshal.FreeHGlobal(overlapped[slot]);
                        overlapped[slot] = IntPtr.Zero;
                    }
                    if (pins[slot].IsAllocated)
                        pins[slot].Free();
                }
            }

            [DllImport("kernel32.dll", EntryPoint = "CreateFileW",
                CharSet = CharSet.Unicode, ExactSpelling = true,
                SetLastError = true)]
            private static extern IntPtr CreateFileW(string fileName,
                uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                uint creationDisposition, uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool WriteFile(IntPtr file, IntPtr buffer,
                uint bytesToWrite, IntPtr bytesWritten, IntPtr nativeOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetOverlappedResult(IntPtr file,
                IntPtr nativeOverlapped, out uint bytesTransferred,
                [MarshalAs(UnmanagedType.Bool)] bool wait);

            [DllImport("kernel32.dll", EntryPoint = "CreateEventW",
                CharSet = CharSet.Unicode, ExactSpelling = true,
                SetLastError = true)]
            private static extern IntPtr CreateEventW(IntPtr eventAttributes,
                [MarshalAs(UnmanagedType.Bool)] bool manualReset,
                [MarshalAs(UnmanagedType.Bool)] bool initialState,
                string name);

            [DllImport("kernel32.dll")]
            private static extern uint WaitForSingleObject(IntPtr handle,
                uint milliseconds);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ResetEvent(IntPtr handle);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetEvent(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CancelIoEx(IntPtr handle,
                IntPtr nativeOverlapped);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
