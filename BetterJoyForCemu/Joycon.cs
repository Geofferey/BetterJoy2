using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using BetterJoyForCemu.VirtualOutput;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu {
    public class Joycon : Controller {
        public bool isPro = false;
        public bool isSnes = false;
        public bool is64 = false;
        public bool isDualSense = false;

        // Capability properties - step 1 of DOCS/CONTROLLERS-REFACTOR.md's migration order,
        // promoted to Controller (abstract) as part of step 4 so DualSenseController can answer
        // them without any isPro/isSnes/isDualSense flags at all. isPro was a SUPERSET flag
        // (isPro = isPro || isSnes || is64 || isDualSense, set in the constructor below), so
        // every "if (isPro)" check silently also matched SNES/N64/DualSense whether that's
        // actually intended or not - the exact mechanism behind a real incident this session (a
        // DualSense-scoped change leaked into Joy-Con's own code path via a shared isPro-gated
        // method). These properties name what each call site is ACTUALLY testing for. Joycon's
        // overrides below are deliberately literal, behavior-preserving aliases of the original
        // flags for every current Nintendo-family device type - this is a pure rename/naming
        // pass, not a behavior change (SNES/N64 mathematically get the same HasDualSticks=true a
        // raw "isPro" check already gave them, even though SNES genuinely has zero sticks - that
        // real divergence is deferred to when SnesController exists, not fixed here).
        public override bool SupportsPairing => !isPro;      // Joy-Con-only: can combine with another unit into one logical controller
        public override bool HasDualSticks => isPro;         // has two physical sticks/thumb-stick-click buttons on one unit
        public override bool HasGyro => !isDualSense;         // currently populates real gyr_g/acc_g data
        public override bool HasAnalogTriggers => isDualSense; // L2/R2 report a real analog value, not just a digital button bit
        public override bool UsesNintendoProtocol => !isDualSense; // speaks the Joy-Con SPI/subcommand protocol (LED, rumble encoding, handshake)

        // Single source of truth for device-kind identity, replacing the same isDualSense-
        // before-isSnes-before-is64-before-isPro ordering dependency that used to be re-derived
        // independently (and correctly, but duplicated) in HeadlessJoyconHost.cs and
        // ControllerMappings.cs - see DOCS/CONTROLLERS-REFACTOR.md's settings/step-1 notes.
        public override ControllerKind Kind =>
            isDualSense ? ControllerKind.DualSense :
            isSnes ? ControllerKind.Snes :
            is64 ? ControllerKind.N64 :
            isPro ? ControllerKind.Pro :
            (isLeft ? ControllerKind.Left : ControllerKind.Right);

        public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2, DualSense only

        // 64 vars
        float maxX = 0.5f;
        float minX = -0.5f;
        float maxY = 0.5f;
        float minY = -0.5f;

        // Join/split changes which mapping profile this physical half belongs to - see
        // Controller.other's setter, which calls this via OnOtherChanging right before the
        // change takes effect. Kept in Joycon (not moved to Controller with other) since it
        // reaches into gyro-mouse/mapping-engine state that isn't shared yet.
        protected override void OnOtherChanging() => PrepareForMappingProfileChange();


        public bool send = true;

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private byte[] stick2_raw = { 0, 0, 0 };
        private bool imu_enabled = false;
        private Int16[] acc_neutral = { 0, 0, 0 };
        private Int16[] acc_sensiti = { 0, 0, 0 };

        private Int16[] gyr_sensiti = { 0, 0, 0 };

        private short[] acc_sen = new short[3]{
            16384,
            16384,
            16384
        };
        private short[] gyr_sen = new short[3]{
            18642,
            18642,
            18642
        };

        private Int16[] pro_hor_offset = { -710, 0, 0 };
        private Int16[] left_hor_offset = { 0, 0, 0 };
        private Int16[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const uint report_len = 49;

        private struct Rumble {
            public Queue<float[]> queue;

            public void set_vals(float low_freq, float high_freq, float amplitude) {
                float[] rumbleQueue = new float[] { low_freq, high_freq, amplitude };
                // Keep a queue of 15 items, discard oldest item if queue is full.
                if (queue.Count > 15) {
                    queue.Dequeue();
                }
                queue.Enqueue(rumbleQueue);
            }
            public Rumble(float[] rumble_info) {
                queue = new Queue<float[]>();
                queue.Enqueue(rumble_info);
            }
            private float clamp(float x, float min, float max) {
                if (x < min) return min;
                if (x > max) return max;
                return x;
            }

            private byte EncodeAmp(float amp) {
                byte en_amp;

                if (amp == 0)
                    en_amp = 0;
                else if (amp < 0.117)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) / (5 - Math.Pow(amp, 2)) - 1);
                else if (amp < 0.23)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) - 0x5c);
                else
                    en_amp = (byte)((((Math.Log(amp * 1000, 2) * 32) - 0x60) * 2) - 0xf6);

                return en_amp;
            }

            public byte[] GetData() {
                byte[] rumble_data = new byte[8];
                float[] queued_data = queue.Dequeue();

                if (queued_data[2] == 0.0f) {
                    rumble_data[0] = 0x0;
                    rumble_data[1] = 0x1;
                    rumble_data[2] = 0x40;
                    rumble_data[3] = 0x40;
                } else {
                    queued_data[0] = clamp(queued_data[0], 40.875885f, 626.286133f);
                    queued_data[1] = clamp(queued_data[1], 81.75177f, 1252.572266f);

                    queued_data[2] = clamp(queued_data[2], 0.0f, 1.0f);

                    UInt16 hf = (UInt16)((Math.Round(32f * Math.Log(queued_data[1] * 0.1f, 2)) - 0x60) * 4);
                    byte lf = (byte)(Math.Round(32f * Math.Log(queued_data[0] * 0.1f, 2)) - 0x40);
                    byte hf_amp = EncodeAmp(queued_data[2]);

                    UInt16 lf_amp = (UInt16)(Math.Round((double)hf_amp) * .5);
                    byte parity = (byte)(lf_amp % 2);
                    if (parity > 0) {
                        --lf_amp;
                    }

                    lf_amp = (UInt16)(lf_amp >> 1);
                    lf_amp += 0x40;
                    if (parity > 0) lf_amp |= 0x8000;

                    hf_amp = (byte)(hf_amp - (hf_amp % 2)); // make even at all times to prevent weird hum
                    rumble_data[0] = (byte)(hf & 0xff);
                    rumble_data[1] = (byte)(((hf >> 8) & 0xff) + hf_amp);
                    rumble_data[2] = (byte)(((lf_amp >> 8) & 0xff) + lf);
                    rumble_data[3] = (byte)(lf_amp & 0xff);
                }

                for (int i = 0; i < 4; ++i) {
                    rumble_data[4 + i] = rumble_data[i];
                }

                return rumble_data;
            }
        }

        private Rumble rumble_obj;

        private byte global_count = 0;

        // Program.cs's DualSense feature-report/serial MAC resolution runs slightly after this
        // object starts existing - if anything reads a mapping profile bind before that lands,
        // mappingProfileId's lazy cache would otherwise lock onto the placeholder MAC's fallback
        // identity (a "path-" encoded one) for the rest of the connection. Call this right after
        // PadMacAddress is actually assigned the real value, exactly like the "other" (join/split)
        // setter already does for that case. Deliberately NOT hooked into PadMacAddress's own
        // assignment generically (e.g. via a property) - Joy-Con's own Attach() also reassigns
        // PadMacAddress internally (its BT-address parse), and invalidating on every such write
        // broke Joy-Con auto-join (two Joycons showing joined in the UI but each keeping its own
        // virtual controller instead of the loser's being torn down) in a way never fully root-
        // caused; narrowing this to an explicit call at the one call site that actually needs it
        // avoids touching that path at all.
        public void InvalidateMappingProfileCache() {
            mappingProfileId = null;
        }

        int lowFreq = Int32.Parse(ConfigurationManager.AppSettings["LowFreqRumble"]);
        int highFreq = Int32.Parse(ConfigurationManager.AppSettings["HighFreqRumble"]);

        public byte LED { get; private set; } = 0x0;
        public override void SetLEDByPlayerNum(int id) {
            if (!UsesNintendoProtocol)
                return;
            if (id > 3) {
                // No support for any higher than 3 (4 Joycons/Controllers supported in the application normally)
                id = 3;
            }

            if (ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings["UseIncrementalLights"].Value.ToLower() == "true") {
                // Set all LEDs from 0 to the given id to lit
                int ledId = id;
                LED = 0x0;
                do {
                    LED |= (byte)(0x1 << ledId);
                } while (--ledId >= 0);
            } else {
                LED = (byte)(0x1 << id);
            }

            SetPlayerLED(LED);
        }

        // True for anything BetterJoy attached because it matched the 3rd-party-controller
        // allowlist (Program.thirdPartyCons - see CheckForNewControllers), rather than being a
        // real Joy-Con/Pro/SNES/N64 device matched by VID/PID directly. Notably, this also
        // catches BetterJoy's OWN virtual XInput/DS4 output controller getting misidentified as a
        // new physical controller when AutoAddControllers is on - Windows exposes ViGEmBus's
        // emulated pad through a HID interface too (for DirectInput compatibility), which passes
        // the same generic "is this a gamepad" usage-page/usage check real third-party controllers
        // do. Left attached (it may still be something the user genuinely wants passthrough for),
        // but button-mapping detection (Reassign.cs/HeadlessJoyconHost.cs) excludes it - otherwise
        // every physical press doubles into a second "press" mirrored from the virtual pad.
        public bool thirdParty = false;

        public Joycon(IntPtr handle_, bool imu, bool localize, float alpha, bool left, string path, string serialNum, int id = 0, bool isPro = false, bool isSnes = false, bool is64 = false, bool thirdParty = false, bool isDualSense = false) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            imu_enabled = imu;
            do_localize = localize;
            rumble_obj = new Rumble(new float[] { lowFreq, highFreq, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            filterweight = alpha;
            isLeft = left;

            PadId = id;
            LED = (byte)(0x1 << PadId);
            this.isPro = isPro || isSnes || is64 || isDualSense;
            this.isSnes = isSnes;
            this.is64 = is64;
            this.isDualSense = isDualSense;
            // The placeholder-serial heuristic below is Joy-Con-only (USB Joy-Cons report this
            // fixed dummy serial until Attach's handshake learns the real MAC) - a DualSense's
            // real serial never matches it, but leaving isUSB unconditional here would still
            // read false correctly by coincidence. Made explicit anyway since real transport is
            // decided per-read from actual report length in ReceiveRaw, not this field.
            isUSB = UsesNintendoProtocol && serialNum == "000000000001";
            this.thirdParty = thirdParty;

            this.path = path;

            // Seed the ViGEm-consumption mask before the first input report. Subsequent live
            // configuration changes are picked up once per report in DoThingsWithButtons.
            RefreshGyroOnlyButtonReservations();

            connection = isUSB ? 0x01 : 0x02;

            // Virtual output is created after Attach resolves the controller's durable profile
            // identity (see JoyconManager.CreateOutputControllers). Creating it here used only
            // the old global ShowAs settings and was too early for USB devices whose real MAC is
            // learned during the handshake.
        }


        public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: XInput", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: DS4", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public bool GetButtonDown(Button b) {
            return buttons_down[(int)b];
        }
        public bool GetButton(Button b) {
            return buttons[(int)b];
        }
        public bool GetButtonUp(Button b) {
            return buttons_up[(int)b];
        }
        // No shared shell worth extracting - past the state_.ATTACHED transition, this is
        // entirely one device-specific branch or the other (DualSense's early return vs.
        // Nintendo's whole USB/BT handshake + SPI calibration dump + subcommand sequence). See
        // Controller.Attach's abstract declaration.
        public override int Attach() {
            state = state_.ATTACHED;

            if (!UsesNintendoProtocol) {
                // None of what follows applies - the USB handshake bytes, SPI calibration dump,
                // home-light/player-LED writes, and IMU/rumble/input-mode subcommands are all
                // either meaningless to a DualSense or (the Subcommand-based ones) block for up
                // to ~1s each waiting for a reply that will never come, since a DualSense doesn't
                // speak this protocol at all. No enable-full-report-mode handshake is known to be
                // required for baseline button/stick/trigger reads; if the first real test shows
                // all-zero/empty reports over Bluetooth, that's the first thing to investigate.
                HIDapi.hid_set_nonblocking(handle, 1);

                // DualSense has no SPI factory calibration to read (unlike Joy-Con's
                // dump_calibration_data, entirely skipped here), so stick_cal/stick2_cal/deadzone
                // would otherwise be left at their class defaults ({0,0,0,0,0,0}/0) - CenterSticks
                // would divide by that zero the moment it's used. Seed an identity calibration
                // matching the DualSense's real raw domain (bytes 0-255, center 128) so stick
                // output is correct out of the box, then let any stored user recalibration
                // (CalibrationState, via the same wizard Joy-Con uses) overlay on top exactly the
                // way it already does for Joy-Con.
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

                form.AppendTextBox("DualSense attached (baseline mode).\r\n");
                return 0;
            }

            // Make sure command is received
            HIDapi.hid_set_nonblocking(handle, 0);

            byte[] a = { 0x0 };

            // Connect
            if (isUSB) {
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                form.AppendTextBox("Using USB.\r\n");

                a[0] = 0x80;
                a[1] = 0x1;
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                if (a[0] != 0x81) { // can occur when USB connection isn't closed properly
                    form.AppendTextBox("Resetting USB connection.\r\n");
                    Subcommand(0x06, new byte[] { 0x01 }, 1);
                    throw new Exception("reset_usb");
                }

                if (a[3] == 0x3) {
                    PadMacAddress = new PhysicalAddress(new byte[] { a[9], a[8], a[7], a[6], a[5], a[4] });
                    mappingProfileId = null;
                }

                // USB Pairing
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                a[0] = 0x80; a[1] = 0x2; // Handshake
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x3; // 3Mbit baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x2; // Handshake at new baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x4; // Prevent HID timeout
                HIDapi.hid_write(handle, a, new UIntPtr(2)); // doesn't actually prevent timout...
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

            }
            dump_calibration_data();

            // Bluetooth manual pairing
            byte[] btmac_host = Program.btMAC.GetAddressBytes();
            // send host MAC and acquire Joycon MAC
            //byte[] reply = Subcommand(0x01, new byte[] { 0x01, btmac_host[5], btmac_host[4], btmac_host[3], btmac_host[2], btmac_host[1], btmac_host[0] }, 7, true);
            //byte[] LTKhash = Subcommand(0x01, new byte[] { 0x02 }, 1, true);
            // save pairing info
            //Subcommand(0x01, new byte[] { 0x03 }, 1, true);

            BlinkHomeLight();
            SetLEDByPlayerNum(PadId);

            Subcommand(0x40, new byte[] { (imu_enabled ? (byte)0x1 : (byte)0x0) }, 1);
            Subcommand(0x48, new byte[] { 0x01 }, 1);

            Subcommand(0x3, new byte[] { 0x30 }, 1);
            DebugPrint("Done with init.", DebugType.COMMS);

            HIDapi.hid_set_nonblocking(handle, 1);

            return 0;
        }

        public void SetPlayerLED(byte leds_ = 0x0) {
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void BlinkHomeLight() { // do not call after initial setup
            if (thirdParty || !UsesNintendoProtocol)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            a[0] = 0x18;
            a[1] = 0x01;
            Subcommand(0x38, a, 25);
        }

        public void SetHomeLight(bool on) {
            if (thirdParty || !UsesNintendoProtocol)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            if (on) {
                a[0] = 0x1F;
                a[1] = 0xF0;
            } else {
                a[0] = 0x10;
                a[1] = 0x01;
            }
            Subcommand(0x38, a, 25);
        }

        private void SetHCIState(byte state) {
            byte[] a = { state };
            Subcommand(0x06, a, 1);
        }

        public override void PowerOff() {
            if (state > state_.DROPPED) {
                HIDapi.hid_set_nonblocking(handle, 0);
                SetHCIState(0x00);
                state = state_.DROPPED;
            }
        }

        // Shared with MainForm.AssignJoyconToSlot, which needs to apply an already-known
        // battery level immediately when a Joycon claims a slot (e.g. splitting off a
        // collapsed pair) - otherwise that slot would show no battery color at all until the
        // next battery-level event happens to fire.
        public static System.Drawing.Color GetBatteryColor(int battery) {
            switch (battery) {
                case 4:
                case 3:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Green);
                case 2:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.GreenYellow);
                case 1:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Orange);
                default:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Red);
            }
        }

        // Called once this controller (Joycon, Pro, SNES, or N64 - all share this class) has
        // actually confirmed itself alive (see retiredDuplicates in Poll()). Attach() resolves
        // the real per-unit MAC address (for a USB connection this is only known once the USB
        // handshake completes - the HID enumeration serial number USB reports is just a shared
        // placeholder, not the real MAC). If another already-connected entry turns out to be
        // this same physical controller over a different transport (e.g. it was connected
        // wirelessly and has now been plugged in via USB), retire that stale entry now rather
        // than waiting for its own poll thread to notice the connection went silent - that
        // window otherwise leaves both the old and new entries live at once, each driving their
        // own virtual output device (double presses/duplicate input in games).
        protected override void RetireDuplicateConnections() {
            // TEMPORARY diagnostic: suspected cross-controller contamination when a DualSense and
            // a Joy-Con are connected together - log every comparison this makes so the real
            // PadId/PadMacAddress values are visible instead of guessed at.
            LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                "RetireDuplicateConnections: this pad={0} dualSense={1} mac={2}",
                PadId, isDualSense, PadMacAddress));
            foreach (Joycon other in Program.mgr.j) {
                if (other != this) {
                    LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                        "  vs pad={0} dualSense={1} mac={2} state={3} equalMac={4}",
                        other.PadId, other.isDualSense, other.PadMacAddress, other.state,
                        other.PadMacAddress.Equals(PadMacAddress)));
                }
                if (other != this && other.state != state_.DROPPED && other.PadMacAddress.Equals(PadMacAddress)) {
                    other.state = state_.DROPPED;
                    form.AppendTextBox("Retiring duplicate connection for the same controller.\r\n");
                    LogDualSenseRawDump("  ^ RETIRED as duplicate");

                    // Marking the stale entry DROPPED only stops BetterJoy from using it - the
                    // underlying OS-level Bluetooth HID connection is still alive and gets
                    // rediscovered (and re-retired) on every subsequent scan, churning a new
                    // virtual controller each time. For a DualSense specifically there's a real
                    // fix: tell the Bluetooth radio itself to drop that connection, the same way
                    // DS4Windows's DisconnectBT does (IOCTL_BTH_DISCONNECT_DEVICE), once USB has
                    // taken over for the same physical controller.
                    if (isDualSense && other.isDualSense && isUSB && !other.isUSB) {
                        // Blue lightbar confirmation is handled unconditionally on the first
                        // confirmed-USB read in ReceiveRaw (sentUsbActiveLightbar) - covers this
                        // case too, not just fresh/no-prior-BT USB connects.
                        bool disconnected = BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                        form.AppendTextBox(disconnected
                            ? "Disconnected DualSense's Bluetooth link now that USB has taken over.\r\n"
                            : "Could not disconnect DualSense's Bluetooth link - it may keep reappearing.\r\n");
                    }
                }
            }
        }

        private void BatteryChanged() { // battery changed level
            form.UpdateBatteryColor(this);

            if (battery <= 1 && !isUSB) {
                form.NotifyLowBattery(this);
            }
        }

        public void SetFilterCoeff(float a) {
            filterweight = a;
        }

        // Called by Controller.Detach() while the connection had progressed past NO_JOYCONS, right
        // after the shared hid_set_nonblocking call - Nintendo-only "let the controller talk to
        // Bluetooth again" handshake, meaningless (and untested) on DualSense. Gated on isUSB,
        // matching Detach()'s pre-existing behavior exactly - NOTE: isUSB is set true for a USB-
        // connected DualSense too (see its own ReceiveRaw branch), so this could in principle
        // fire for one; that's a latent bug that predates this move (not introduced by it -
        // flagged, not fixed here, since fixing it is a real behavior change and this step is a
        // pure extraction). UsesNintendoProtocol would be the correct gate instead of isUSB, once
        // that's worth revisiting.
        protected override void OnDetachingWhileAttached() {
            // Subcommand(0x40, new byte[] { 0x0 }, 1); // disable IMU sensor
            //Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?

            if (isUSB) {
                byte[] a = Enumerable.Repeat((byte)0, 64).ToArray();
                a[0] = 0x80; a[1] = 0x5; // Allow device to talk to BT again
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                a[0] = 0x80; a[1] = 0x6; // Allow device to talk to BT again
                HIDapi.hid_write(handle, a, new UIntPtr(2));
            }
        }

        private byte ts_en;

        // An occasional duplicate timestamp is normal (we can poll faster than the device
        // produces new reports); a run of them is not - it means the report stream has
        // genuinely stalled, which happens when another program (e.g. Steam) grabbed the raw
        // device before HidHide had a chance to hide it (a real race on a fresh boot/cleared
        // settings, since HidHide's hidden-device list isn't there yet to fall back on and
        // Steam may already be running). Detaching and letting the normal reconnect flow
        // (CleanUp -> rediscovered on the next scan) pick it back up clears the stall in
        // practice, so treat a short run the same as a connection-loss timeout.
        private int duplicateTimestampCount = 0;
        private const int MaxConsecutiveDuplicateTimestamps = 3;

        private const int DualSenseMaxReportLen = 78; // Bluetooth report length; USB (64) fits the same buffer
        private long lastDualSenseRawDumpTimestamp = 0;
        private bool sentUsbActiveLightbar = false;

        private static readonly ConcurrentQueue<string> dualSenseRawDumpQueue = new ConcurrentQueue<string>();
        private static int dualSenseRawDumpWriterStarted;

        private readonly Dictionary<string, long> lastMappingValueDumpTimestamp = new Dictionary<string, long>();

        // TEMPORARY diagnostic: user reports a DualSense still acting on click/gyro-mouse binds
        // after disabling them in the profile UI - log the actual resolved profile ID and value
        // (per key, own throttle each) so this can be confirmed against controller_mappings.xml
        // directly instead of guessed at.
        protected override void OnMappingValueResolved(string key, string value) {
            if (isDualSense && (key == "left_click" || key == "right_click" || key == "active_gyro_mouse")) {
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

        // Same async queue + background-writer pattern as autocal_debug.log, so this can't block
        // a controller's own Poll thread on file I/O. Gated behind DualSenseDebugLogging (default
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

            if (isDualSense) {
                byte[] dsBuf = new byte[DualSenseMaxReportLen];
                int dsRet = HIDapi.hid_read_timeout(handle, dsBuf, new UIntPtr((uint)DualSenseMaxReportLen), 5);

                // Actual report length distinguishes USB (64 bytes) from Bluetooth (78 bytes) per
                // read - no separate transport query needed, and more reliable than the Joy-Con-
                // only placeholder-serial heuristic isUSB otherwise depends on.
                if (dsRet == 64 || dsRet == 78) {
                    isUSB = dsRet == 64;
                    if (isUSB && !sentUsbActiveLightbar) {
                        // Fires once per connection, on the first confirmed-USB read - covers
                        // every USB-connect scenario (fresh plug-in, reconnect, with or without a
                        // prior Bluetooth link), not just the "just force-disconnected a stale BT
                        // link" case RetireDuplicateConnections handles separately.
                        sentUsbActiveLightbar = true;
                        SendDualSenseLightbar(0, 0, 255);
                    }
                    // hid_read_timeout does NOT strip the leading report-ID byte for either
                    // transport - byte 0 is a constant 0x01 (USB) or 0x31 (BT) report ID. USB has
                    // no further padding, so real data starts at byte 1. BT has one more padding/
                    // tag byte after the report ID before real data starts at byte 2. Confirmed two
                    // independent ways: (1) decoding a real idle BT capture at offset 2 gives sane
                    // values (sticks dead-center, triggers at 0, button byte reading the DualSense's
                    // documented dpad-neutral encoding 0x08) while offset 1 does not; (2)
                    // DS4Windows's own DualSenseDevice.cs (a shipped Windows implementation) uses
                    // reportOffset = BT ? 1 : 0 relative to a buffer that, like ours, still includes
                    // the report-ID byte - i.e. absolute offset 2 (BT) / 1 (USB), matching (1).
                    int reportOffset = isUSB ? 1 : 2;

                    // TEMPORARY diagnostic: the offsets guessed from a secondhand reference are
                    // demonstrably wrong (confirmed on real hardware - trigger/button bytes don't
                    // line up), so dump real bytes to a file instead of guessing a third time -
                    // the on-screen console has not been a reliable way to actually see this.
                    // Throttled to ~4/sec so it's readable while still catching real changes as
                    // controls are pressed one at a time. Remove once ParseDualSenseReport's
                    // offsets are confirmed correct against real data.
                    long nowTicks = Stopwatch.GetTimestamp();
                    if ((nowTicks - lastDualSenseRawDumpTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                        lastDualSenseRawDumpTimestamp = nowTicks;
                        var hex = new StringBuilder();
                        for (int i = 0; i < dsRet; i++)
                            hex.Append(dsBuf[i].ToString("X2")).Append(' ');
                        LogDualSenseRawDump("DS raw[" + dsRet + "]: " + hex.ToString());
                    }

                    ParseDualSenseReport(dsBuf, reportOffset);
                    DoThingsWithButtons();
                    if (out_xbox != null) {
                        try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch (Exception) { }
                    }
                    return dsRet;
                }

                // An unexpected length means the report stream is no longer what this parser
                // expects - possibly a transient glitch, but also possibly a connection that's
                // genuinely gone bad (confirmed on real hardware: report framing can shift after
                // something puts the controller in a bad state). Treating this as harmless
                // previously meant such a connection could never reach DROPPED and would sit in
                // joy.cpl as a stale, frozen "connected" entry forever - count it as a real error
                // instead so a truly broken connection gets cleaned up like any other.
                if (dsRet > 0)
                    return -1;
                return dsRet; // 0 = timeout, <0 = read error - Poll()'s state machine already handles both
            }

            byte[] raw_buf = new byte[report_len];
            bool captureImuDiagnostics = GyroMouseDebugLogging || GyroStickDebugLogging;
            long hidCallStart = captureImuDiagnostics ? Stopwatch.GetTimestamp() : 0;
            int ret = HIDapi.hid_read_timeout(handle, raw_buf, new UIntPtr(report_len), 5);
            long hidCallEnd = captureImuDiagnostics ? Stopwatch.GetTimestamp() : 0;
            RecordGyroMouseHidCall(ret, ret > 0 ? raw_buf[1] : (byte)0,
                                   hidCallStart, hidCallEnd);

            if (ret > 0) {
                BeginGyroStickDiagnosticReport();
                // Process packets as soon as they come
                for (int n = 0; n < 3; n++) {
                    ExtractIMUValues(raw_buf, n);
                    AccumulateGyroStickDiagnosticSample();

                    byte lag = (byte)Math.Max(0, raw_buf[1] - ts_en - 3);
                    if (n == 0) {
                        Timestamp += (ulong)lag * 5000; // add lag once
                        ProcessButtonsAndStick(raw_buf);

                        // process buttons here to have them affect DS4
                        DoThingsWithButtons();

                        int newbat = battery;
                        battery = (raw_buf[2] >> 4) / 2;
                        if (newbat != battery)
                            BatteryChanged();
                    }
                    ProcessGyroMouseSample(n == 2);
                    ProcessGyroStickSample(n == 2);
                    Timestamp += 5000; // 5ms difference

                    packetCounter++;
                    if (Program.server != null)
                        Program.server.NewReportIncoming(this);

                    if (out_ds4 != null) {
                        try {
                            out_ds4.UpdateInput(MapToDualShock4Input(this));
                        } catch (Exception) {
                            // ignore /shrug
                        }
                    }
                }

                RecordGyroStickDiagnosticReport(raw_buf[1], hidCallEnd);

                // no reason to send XInput reports so often
                if (out_xbox != null) {
                    try {
                        out_xbox.UpdateInput(MapToXbox360Input(this));
                    } catch (Exception) {
                        // ignore /shrug
                    }
                }


                if (ts_en == raw_buf[1] && !(isSnes || is64)) {
                    form.AppendTextBox("Duplicate timestamp enqueued.\r\n");
                    DebugPrint(string.Format("Duplicate timestamp enqueued. TS: {0:X2}", ts_en), DebugType.THREADING);

                    duplicateTimestampCount++;
                    if (duplicateTimestampCount >= MaxConsecutiveDuplicateTimestamps) {
                        form.AppendTextBox("Report stream stalled (another program may have grabbed this controller before it was hidden) - reattaching to recover.\r\n");
                        duplicateTimestampCount = 0;
                        state = state_.DROPPED;
                    }
                } else {
                    duplicateTimestampCount = 0;
                }
                ts_en = raw_buf[1];
                DebugPrint(string.Format("Enqueue. Bytes read: {0:D}. Timestamp: {1:X2}", ret, raw_buf[1]), DebugType.THREADING);
            }
            return ret;
        }


        // Called from Controller.Poll()'s shared shell whenever rumble_obj's queue has data -
        // rumble_obj/SendRumble/SendDualSenseRumble aren't promoted to Controller yet, so this
        // stays a hook rather than shared logic.
        protected override void SendQueuedRumbleIfAny() {
            if (rumble_obj.queue.Count > 0) {
                if (!UsesNintendoProtocol) {
                    // DualSense's simple dual-motor rumble has no equivalent to the low/high-
                    // frequency split Rumble.GetData() encodes for Joy-Con's HD rumble - just
                    // take the queued amplitude directly and drive both motors the same. Was
                    // disabled after real hardware went into continuous, non-stopping rumble
                    // the first time this ran - root cause found: outputReport[2] (USB) /
                    // [3] (BT) is a required feature-flags byte (0x55: mic LED, audio mute,
                    // touchpad strips, player lights, motor power) that was left at 0x00 by
                    // omission, not an intentional "leave alone" zero. Re-enabled with that
                    // byte now set.
                    float amp = rumble_obj.queue.Dequeue()[2];
                    byte motor = (byte)(Math.Max(0f, Math.Min(1f, amp)) * 255f);
                    SendDualSenseRumble(motor, motor);
                } else {
                    SendRumble(rumble_obj.GetData());
                }
            }
        }

        public float[] otherStick = { 0, 0 };

        bool swapAB => ProfileBoolOption("SwapAB");
        bool swapXY => ProfileBoolOption("SwapXY");
        bool realn64Range = Boolean.Parse(ConfigurationManager.AppSettings["N64Range"]);
        float stickScalingFactor = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor"]);
        float stickScalingFactor2 = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor2"]);

        private int ProcessButtonsAndStick(byte[] report_buf) {
            // A report ID of 0 is never valid for a real Joy-Con/Pro Controller report - this
            // used to throw here, which had nothing catching it anywhere in the call chain
            // (ReceiveRaw/Poll), so a single malformed report crashed the entire process, not
            // just this controller's connection. Skip this report instead: buttons/stick just
            // hold their previous values for one tick, matching how Poll() already tolerates a
            // read error or timeout (a < 0 / a == 0) without tearing anything down.
            if (report_buf[0] == 0x00) {
                DebugPrint("Received a report with report ID 0 - skipping.", DebugType.ALL);
                return -1;
            }
            if (!isSnes) {
                stick_raw[0] = report_buf[6 + (isLeft ? 0 : 3)];
                stick_raw[1] = report_buf[7 + (isLeft ? 0 : 3)];
                stick_raw[2] = report_buf[8 + (isLeft ? 0 : 3)];

                if (HasDualSticks) {
                    stick2_raw[0] = report_buf[6 + (!isLeft ? 0 : 3)];
                    stick2_raw[1] = report_buf[7 + (!isLeft ? 0 : 3)];
                    stick2_raw[2] = report_buf[8 + (!isLeft ? 0 : 3)];
                }

                stick_precal[0] = (UInt16)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
                stick_precal[1] = (UInt16)((stick_raw[1] >> 4) | (stick_raw[2] << 4));
                CalibrationState.AddStickSample(this, false, stick_precal[0], stick_precal[1]);
                stick = CenterSticks(stick_precal, stick_cal, deadzone, isLeft ? stickScalingFactor : stickScalingFactor2);

                if (HasDualSticks) {
                    stick2_precal[0] = (UInt16)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
                    stick2_precal[1] = (UInt16)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
                    CalibrationState.AddStickSample(this, true, stick2_precal[0], stick2_precal[1]);
                    stick2 = CenterSticks(stick2_precal, stick2_cal, deadzone2, stickScalingFactor2);
                }

                // Read other Joycon's sticks
                if (isLeft && other != null && other != this) {
                    stick2 = otherStick;
                    other.otherStick = stick;
                }

                if (!isLeft && other != null && other != this) {
                    Array.Copy(stick, stick2, 2);
                    stick = otherStick;
                    other.otherStick = stick2;
                }
            }
            //

            // Set button states both for server and ViGEm
            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i) {
                        down_[i] = buttons[i];
                    }
                }
                buttons = new bool[20];

                buttons[(int)Button.DPAD_DOWN] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x01 : 0x04)) != 0;
                buttons[(int)Button.DPAD_RIGHT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x04 : 0x08)) != 0;
                buttons[(int)Button.DPAD_UP] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x02 : 0x02)) != 0;
                buttons[(int)Button.DPAD_LEFT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x08 : 0x01)) != 0;
                buttons[(int)Button.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)Button.CAPTURE] = ((report_buf[4] & 0x20) != 0);
                buttons[(int)Button.MINUS] = ((report_buf[4] & 0x01) != 0);
                buttons[(int)Button.PLUS] = ((report_buf[4] & 0x02) != 0);
                buttons[(int)Button.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
                buttons[(int)Button.SHOULDER_1] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x40) != 0;
                buttons[(int)Button.SHOULDER_2] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x80) != 0;
                buttons[(int)Button.SR] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x10) != 0;
                buttons[(int)Button.SL] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x20) != 0;

                if (HasDualSticks) {
                    buttons[(int)Button.B] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x01 : 0x04)) != 0;
                    buttons[(int)Button.A] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x04 : 0x08)) != 0;
                    buttons[(int)Button.X] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x02 : 0x02)) != 0;
                    buttons[(int)Button.Y] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x08 : 0x01)) != 0;

                    buttons[(int)Button.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
                    buttons[(int)Button.SHOULDER2_1] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x40) != 0;
                    buttons[(int)Button.SHOULDER2_2] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x80) != 0;
                }

                if (other != null && other != this) {
                    buttons[(int)(Button.B)] = other.buttons[(int)Button.DPAD_DOWN];
                    buttons[(int)(Button.A)] = other.buttons[(int)Button.DPAD_RIGHT];
                    buttons[(int)(Button.X)] = other.buttons[(int)Button.DPAD_UP];
                    buttons[(int)(Button.Y)] = other.buttons[(int)Button.DPAD_LEFT];

                    buttons[(int)Button.STICK2] = other.buttons[(int)Button.STICK];
                    buttons[(int)Button.SHOULDER2_1] = other.buttons[(int)Button.SHOULDER_1];
                    buttons[(int)Button.SHOULDER2_2] = other.buttons[(int)Button.SHOULDER_2];
                }

                if (isLeft && other != null && other != this) {
                    buttons[(int)Button.HOME] = other.buttons[(int)Button.HOME];
                    buttons[(int)Button.PLUS] = other.buttons[(int)Button.PLUS];
                }

                if (!isLeft && other != null && other != this) {
                    buttons[(int)Button.MINUS] = other.buttons[(int)Button.MINUS];
                }

                CommitButtonState();
            }

            return 0;
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
            // not just by a constant byte shift: the actual order is sticks, buttons1, buttons2,
            // a free-running sequence counter, THEN L2/R2 analog - references had triggers before
            // the counter and buttons after. Confirmed from real data: byte 4 reads a constant
            // 0x08 at rest (dpad nibble 8 = neutral, matching the real PS dpad convention, face-
            // button nibble 0 = nothing pressed); byte 5 toggles exactly 0x04/0x08 in sync with
            // L2/R2's digital end-of-travel click; byte 6 free-runs 0x00-0x3C regardless of input
            // (the counter); bytes 7/8 ramp with L2/R2 squeeze depth precisely when byte 5's
            // matching click bit is set. o is the genuine Bluetooth-vs-USB protocol byte (1/0).
            //
            // Raw 0-255, center ~128, run through the same CenterSticks/CalibrationState pipeline
            // Joy-Con uses (stick_cal/stick2_cal seeded with an identity default in Attach() since
            // there's no SPI factory data to read) - a DualSense can now be recalibrated with the
            // existing double-click wizard exactly like a Pro controller's sticks, just skipping
            // the gyro step (see MainForm.StartCalibrate's isDualSense branch). AddStickSample is
            // a no-op unless this controller is the one currently claimed by that wizard. Y is
            // inverted after CenterSticks (not before, unlike the old fixed linear map) since
            // CenterSticks' raw subtraction/division doesn't know about BetterJoy's own "up is
            // positive" stick convention - only the sign needs flipping, not the calibration math.
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

            // USB and BT reports use the identical field order once o has skipped each
            // transport's own report-ID(+padding) prefix (see the o assignment in ReceiveRaw) -
            // no further per-transport swap needed here. Order after the sticks: L2, R2, a free-
            // running sequence/status counter (field index 6, skipped), then the two button bytes.
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

                // byte 6 is the sequence counter (skipped). PS button confirmed via
                // DS4Windows's DualSenseDevice.cs (inputReport[10+ro], bit 0).
                byte btn3 = r[9 + o];
                b[(int)Button.HOME] = (btn3 & 0x01) != 0; // PS button
                // Touchpad click/mute/paddles intentionally unmapped this milestone (out of
                // scope); SL/SR have no DualSense equivalent, left false.

                buttons = b;
                CommitButtonState();
            }

            // Battery offset (52+o) confirmed via DS4Windows's DualSenseDevice.cs
            // (inputReport[53+ro], same absolute position once o's own transport skip is
            // accounted for). Low nibble is a coarse 0-8 level (bit 5 = full charge, forced to 8);
            // halved to match GetBatteryColor's existing 0-4 scale, the same way Joy-Con's own
            // coarser battery nibble already does.
            byte batteryByte = r[52 + o];
            int rawLevel = (batteryByte & 0x20) != 0 ? 8 : (batteryByte & 0x0F);
            int newBattery = battery;
            battery = Math.Min(4, rawLevel / 2);
            if (newBattery != battery)
                BatteryChanged();
        }

        // Get Gyro/Accel data
        private void ExtractIMUValues(byte[] report_buf, int n = 0) {
            if (!(isSnes || is64)) {
                // Must happen before this sample is transformed/added to either estimator. If a
                // join/split changed the basis, the orientation accumulated in the old basis is
                // invalid even though the physical controller itself never disconnected.
                EnsureGyroOrientationBasis();

                gyr_r[0] = (Int16)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
                gyr_r[1] = (Int16)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
                gyr_r[2] = (Int16)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
                acc_r[0] = (Int16)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
                acc_r[1] = (Int16)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
                acc_r[2] = (Int16)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

                if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - activeData[3]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.X = (gyr_r[i] - activeData[0]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.XA, CalibrationState.XG, acc_r[i], gyr_r[i]);
                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[4]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[1]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.YA, CalibrationState.YG, acc_r[i], gyr_r[i]);
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[5]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[2]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.ZA, CalibrationState.ZG, acc_r[i], gyr_r[i]);
                                break;
                        }
                    }
                } else {
                    Int16[] offset;
                    if (!SupportsPairing)
                        offset = pro_hor_offset;
                    else if (isLeft)
                        offset = left_hor_offset;
                    else
                        offset = right_hor_offset;

                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.X = (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                        }
                    }
                }

                if (other == null && SupportsPairing) { // single joycon mode; Z do not swap, rest do
                    if (isLeft) {
                        acc_g.X = -acc_g.X;
                        acc_g.Y = -acc_g.Y;
                        gyr_g.X = -gyr_g.X;
                    } else {
                        gyr_g.Y = -gyr_g.Y;
                    }

                    float temp = acc_g.X;
                    acc_g.X = acc_g.Y;
                    acc_g.Y = -temp;

                    temp = gyr_g.X;
                    gyr_g.X = gyr_g.Y;
                    gyr_g.Y = temp;
                }

                // Capture the canonical, Y-up JoyShockLibrary-style IMU frame from acc_g/gyr_g
                // AFTER the solo-controller swap above (not before, as this used to do) - a solo
                // Joy-Con is physically held sideways, a self-paired ("vertical") or joined one
                // is held upright, and those are genuinely different grips that need different
                // axis mappings, the same way the legacy raw pipeline right above already treats
                // them differently. Building the canonical frame from the pre-swap values made
                // solo, self-paired, and joined share one frame, which was wrong for solo.
                //
                // NOTE: an earlier version of this code applied the equivalent swap directly to
                // the canonical Y-up vector instead of reordering like this, and that reportedly
                // suppressed vertical/diagonal pointer motion for solo Joy-Cons - likely because
                // the raw swap's axis correspondence doesn't carry over 1:1 onto the already-
                // remapped canonical axes below, so applying it post-hoc there is a different,
                // NOT equivalent operation to applying it here, pre-remap, in the native frame it
                // was actually written for. This reordering has not been hardware-verified against
                // that history - test solo Joy-Con gyro-mouse/gyro-stick specifically (both
                // orientations) before trusting this.
                UpdateCanonicalGyroMouseImu();

                // Update rotation Quaternion
                float deg_to_rad = 0.0174533f;
                AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);
            }
        }

        private static short CastStickValue(float stick_value) {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, stick_value * (stick_value > 0 ? Int16.MaxValue : -Int16.MinValue)));
        }

        private static byte CastStickValueByte(float stick_value) {
            return (byte)Math.Max(Byte.MinValue, Math.Min(Byte.MaxValue, 127 - stick_value * Byte.MaxValue));
        }

        public void SetRumble(float low_freq, float high_freq, float amp) {
            if (state <= Joycon.state_.ATTACHED) return;
            rumble_obj.set_vals(low_freq, high_freq, amp);
        }

        private void SendRumble(byte[] buf) {
            byte[] buf_ = new byte[report_len];
            buf_[0] = 0x10;
            buf_[1] = global_count;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            Array.Copy(buf, 0, buf_, 2, 8);
            PrintArray(buf_, DebugType.RUMBLE, format: "Rumble data sent: {0:S}");
            HIDapi.hid_write(handle, buf_, new UIntPtr(report_len));
        }

        // Standard IEEE 802.3 CRC32 (polynomial 0xEDB88320, the same one zlib/most CRC32
        // libraries use) - DualSense's Bluetooth output reports are silently ignored by the
        // controller unless this checksum is present and correct; USB output needs none.
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

        // seed is a virtual leading byte folded into the running CRC state before data - the
        // real DualSense Bluetooth output checksum is computed as if a 0xA2 byte preceded the
        // actual report, without that byte itself being part of the transmitted buffer.
        private static uint Crc32(byte seed, byte[] data, int length) {
            uint crc = 0xFFFFFFFF;
            crc = crc32Table[(crc ^ seed) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < length; i++)
                crc = crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        // DualSense baseline rumble - both motors driven by the same single amplitude value
        // dequeued from rumble_obj (see the Poll() call site), since DualSense's simple dual-
        // motor rumble has no equivalent to Joy-Con's HD-rumble low/high-frequency split
        // Rumble.GetData() encodes. Report layout (motor byte offsets, enable-rumble flags,
        // Bluetooth CRC32-with-0xA2-seed) from DS4Windows's DualSense output-report code.
        private void SendDualSenseRumble(byte leftMotor, byte rightMotor) {
            bool bt = !isUSB;
            int len = bt ? DualSenseMaxReportLen : 64;
            byte[] buf = new byte[len];
            if (bt) {
                buf[0] = 0x31;
                buf[1] = 0x02;
                buf[2] = 0x0F; // enable rumble
                // Required feature-flags byte (mic LED, audio mute, touchpad strips, player
                // lights, motor power) - NOT safe to leave at 0x00 (confirmed on real hardware:
                // omitting this the first time caused continuous, non-stopping rumble).
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
        // Bluetooth link gets disconnected (see RetireDuplicateConnections). Layout matches
        // SendDualSenseRumble; rumble flags are left at "not in use" since this report isn't
        // rumble-related. RGB offsets (45/46/47 USB, 46/47/48 BT) and the fact that no separate
        // "enable lightbar" bit is needed beyond the same 0x55 feature-flags byte the rumble
        // report already sets - both confirmed via DS4Windows's DualSenseDevice.cs.
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

        private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true) {
            byte[] buf_ = new byte[report_len];
            byte[] response = new byte[report_len];
            Array.Copy(default_buf, 0, buf_, 2, 8);
            Array.Copy(buf, 0, buf_, 11, len);
            buf_[10] = sc;
            buf_[1] = global_count;
            buf_[0] = 0x1;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            if (print) { PrintArray(buf_, DebugType.COMMS, len, 11, "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}"); };
            HIDapi.hid_write(handle, buf_, new UIntPtr(len + 11));
            int tries = 0;
            do {
                int res = HIDapi.hid_read_timeout(handle, response, new UIntPtr(report_len), 100);
                if (res < 1) DebugPrint("No response.", DebugType.COMMS);
                else if (print) { PrintArray(response, DebugType.COMMS, report_len - 1, 1, "Response ID 0x" + string.Format("{0:X2}", response[0]) + ". Data: 0x{0:S}"); }
                tries++;
            } while (tries < 10 && response[0] != 0x21 && response[14] != sc);

            return response;
        }

        private void dump_calibration_data() {
            if (isSnes || is64 || thirdParty) {
                short[] temp = (short[])ConfigurationManager.AppSettings["acc_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                acc_sensiti[0] = temp[0]; acc_sensiti[1] = temp[1]; acc_sensiti[2] = temp[2];
                temp = (short[])ConfigurationManager.AppSettings["gyr_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                gyr_sensiti[0] = temp[0]; gyr_sensiti[1] = temp[1]; gyr_sensiti[2] = temp[2];
                ushort[] temp2 = (ushort[])ConfigurationManager.AppSettings["stick_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick_cal[0] = temp2[0]; stick_cal[1] = temp2[1]; stick_cal[2] = temp2[2];
                stick_cal[3] = temp2[3]; stick_cal[4] = temp2[4]; stick_cal[5] = temp2[5];
                deadzone = ushort.Parse(ConfigurationManager.AppSettings["deadzone"]);
                temp2 = (ushort[])ConfigurationManager.AppSettings["stick2_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick2_cal[0] = temp2[0]; stick2_cal[1] = temp2[1]; stick2_cal[2] = temp2[2];
                stick2_cal[3] = temp2[3]; stick2_cal[4] = temp2[4]; stick2_cal[5] = temp2[5];
                deadzone2 = ushort.Parse(ConfigurationManager.AppSettings["deadzone2"]);
                getActiveStickData();
                return;
            }

            HIDapi.hid_set_nonblocking(handle, 0);
            byte[] buf_ = ReadSPI(0x80, (isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
            bool found = false;
            for (int i = 0; i < 9; ++i) {
                if (buf_[i] != 0xff) {
                    form.AppendTextBox("Using user stick calibration data.\r\n");
                    found = true;
                    break;
                }
            }
            if (!found) {
                form.AppendTextBox("Using factory stick calibration data.\r\n");
                buf_ = ReadSPI(0x60, (isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
            }
            stick_cal[isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
            stick_cal[isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
            stick_cal[isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
            stick_cal[isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
            stick_cal[isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
            stick_cal[isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

            PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

            if (HasDualSticks) {
                buf_ = ReadSPI(0x80, (!isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
                found = false;
                for (int i = 0; i < 9; ++i) {
                    if (buf_[i] != 0xff) {
                        form.AppendTextBox("Using user stick calibration data.\r\n");
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    form.AppendTextBox("Using factory stick calibration data.\r\n");
                    buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
                }
                stick2_cal[!isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
                stick2_cal[!isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
                stick2_cal[!isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
                stick2_cal[!isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
                stick2_cal[!isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
                stick2_cal[!isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

                PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

                buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x86 : (byte)0x98), 16);
                deadzone2 = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);
            }

            buf_ = ReadSPI(0x60, (isLeft ? (byte)0x86 : (byte)0x98), 16);
            deadzone = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);

            buf_ = ReadSPI(0x80, 0x28, 10);
            acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x2E, 10);
            acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x34, 10);
            gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x3A, 10);
            gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "User gyro neutral position: {0:S}");

            // This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
            if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100) {
                buf_ = ReadSPI(0x60, 0x20, 10);
                acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x26, 10);
                acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x2C, 10);
                gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x32, 10);
                gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "Factory gyro neutral position: {0:S}");
            }
            HIDapi.hid_set_nonblocking(handle, 1);

            getActiveStickData();
        }

        private byte[] ReadSPI(byte addr1, byte addr2, uint len, bool print = false) {
            byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
            byte[] read_buf = new byte[len];
            byte[] buf_ = new byte[len + 20];

            for (int i = 0; i < 100; ++i) {
                buf_ = Subcommand(0x10, buf, 5, false);
                if (buf_[15] == addr2 && buf_[16] == addr1) {
                    break;
                }
            }
            Array.Copy(buf_, 20, read_buf, 0, len);
            if (print) PrintArray(read_buf, DebugType.COMMS, len);
            return read_buf;
        }

        private static float GetNormalizedValue(float value, float rawMin, float rawMax, float normalizedMin, float normalizedMax)
        {
            return (value - rawMin) / (rawMax - rawMin) * (normalizedMax - normalizedMin) + normalizedMin;
        }

        private static float[] Getn64StickValues(Joycon input)
        {
            var isLeft = input.isLeft;
            var other = input.other;
            var stick = input.stick;
            var stick2 = input.stick2;
            var stick_correction = new float[] { 0f, 0f};

            var xAxis = (other == input && !isLeft) ? stick2[0] : stick[0];
            var yAxis = (other == input && !isLeft) ? stick2[1] : stick[1];


            if (xAxis < input.minX)
            {
                input.minX = xAxis;
            }

            if (xAxis > input.maxX)
            {
                input.maxX = xAxis;
            }

            if (yAxis < input.minY)
            {
                input.minY = yAxis;
            }

            if (yAxis > input.maxY)
            {
                input.maxY = yAxis;
            }

            var middleX = (input.minX + (input.maxX - input.minX)/2);
            var middleY = (input.minY + (input.maxY - input.minY)/2);
            #if DEBUG
            var desc = "";
            desc += "x: "+xAxis+"; y: "+yAxis;
            desc += "\n X: ["+input.minX+", "+input.maxX+"]; Y: ["+input.minY+", "+input.maxY+"] ";
            desc += "; middle ["+middleX+", "+middleY+"]";
                
            Debug.WriteLine(desc);
            #endif

            var negative_normalized = new float[] {-1, 0};
            var positive_normalized = new float[] {0, 1};

            var xRange = new float[] {-1f, 1f};
            var yRange = new float[] {-1f, 1f};

            if (input.realn64Range)
            {
                xRange = new float[] {-0.79f, 0.79f};
                yRange = new float[] {-0.79f, 0.79f};
            }
            

            if (xAxis < (middleX - middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, input.minX, (middleX - middleX), xRange[0], 0f);
            }

            if (xAxis > (middleX+middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, (middleX+middleX), input.maxX, 0f, xRange[1]);
            }

            if (yAxis < (middleY-middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, input.minY, (middleY-middleY), yRange[0], 0f);
            }

            if (yAxis > (middleY+middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, (middleY+middleY), input.maxY, 0f, yRange[1]);
            }


            return stick_correction;
        }

        private static OutputControllerXbox360InputState MapToXbox360Input(Joycon input) {
            var output = new OutputControllerXbox360InputState();


            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var is64 = input.is64;
            var hasDualSticks = input.HasDualSticks;
            var hasAnalogTriggers = input.HasAnalogTriggers;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.GetButtonsForVigem();
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (is64)
            {
                output.axis_right_x = (short) ((buttons[(int)Button.X] ? Int16.MinValue : 0) + (buttons[(int)Button.MINUS] ? Int16.MaxValue : 0));
                output.axis_right_y = (short) ((buttons[(int)Button.SHOULDER2_2] ? Int16.MinValue: 0) + (buttons[(int)Button.Y] ? Int16.MaxValue: 0));

                var n64Stick = Getn64StickValues(input);

                output.axis_left_x = CastStickValue(n64Stick[0]);
                output.axis_left_y = CastStickValue(n64Stick[1]);

                output.start = buttons[(int)Button.PLUS];
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);

                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];
                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.guide = buttons[(int)Button.HOME];

            }
            else if (hasDualSticks) {
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.y = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.x = buttons[(int)(!swapXY ? Button.Y : Button.X)];

                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];

                output.back = buttons[(int)Button.MINUS];
                output.start = buttons[(int)Button.PLUS];
                output.guide = buttons[(int)Button.HOME];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.thumb_stick_left = buttons[(int)Button.STICK];
                output.thumb_stick_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];

                    output.dpad_up = buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)];
                    output.dpad_down = buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)];
                    output.dpad_left = buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)];
                    output.dpad_right = buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)];

                    output.back = buttons[(int)Button.MINUS];
                    output.start = buttons[(int)Button.PLUS];
                    output.guide = buttons[(int)Button.HOME];

                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];

                    output.thumb_stick_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_stick_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.back = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.start = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_stick_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (input.MappingValue("home") != "0")
                output.guide = false;

            if (!(isSnes || is64)) {
                if (other != null || hasDualSticks) { // no need for && other != this
                    output.axis_left_x = CastStickValue((other == input && !isLeft) ? stick2[0] : stick[0]);
                    output.axis_left_y = CastStickValue((other == input && !isLeft) ? stick2[1] : stick[1]);

                    output.axis_right_x = CastStickValue((other == input && !isLeft) ? stick[0] : stick2[0]);
                    output.axis_right_y = CastStickValue((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.axis_left_y = CastStickValue((isLeft ? 1 : -1) * stick[0]);
                    output.axis_left_x = CastStickValue((isLeft ? -1 : 1) * stick[1]);
                }
            }

            if (!is64)
            {
                if (hasAnalogTriggers) {
                    // A DualSense's L2/R2 are genuinely analog, unlike Joy-Con/Pro (which have no
                    // trigger sensor at all and only ever derive a digital 0-or-max value from a
                    // button bit below) - pass the real raw value straight through.
                    output.trigger_left = input.triggerVal[0];
                    output.trigger_right = input.triggerVal[1];
                } else if (other != null || hasDualSticks) {
                    byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                    byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                    output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                    output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
                } else {
                    output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                    output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
                }
            }

            return output;
        }

        public static OutputControllerDualShock4InputState MapToDualShock4Input(Joycon input) {
            var output = new OutputControllerDualShock4InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var is64 = input.is64;
            var hasDualSticks = input.HasDualSticks;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.GetButtonsForVigem();
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (is64)
            {
                output.thumb_right_x = (byte) ((buttons[(int)Button.X] ? Byte.MinValue : 0) + (buttons[(int)Button.MINUS] ? Byte.MaxValue : 0));
                output.thumb_right_y = (byte) ((buttons[(int)Button.SHOULDER2_2] ? Byte.MinValue: 0) + (buttons[(int)Button.Y] ? Byte.MaxValue: 0));

                output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);

                output.options = buttons[(int)Button.PLUS];
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = buttons[(int)Button.SHOULDER_2];
                output.trigger_right = buttons[(int)Button.STICK];
                output.trigger_left_value = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right_value = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;                
            }

            if (hasDualSticks) {
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.triangle = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.square = buttons[(int)(!swapXY ? Button.Y : Button.X)];


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;

                output.share = buttons[(int)Button.CAPTURE];
                output.options = buttons[(int)Button.PLUS];
                output.ps = buttons[(int)Button.HOME];
                output.touchpad = buttons[(int)Button.MINUS];
                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];
                output.thumb_left = buttons[(int)Button.STICK];
                output.thumb_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];

                    if (buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Northwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Northeast;
                        else
                            output.dPad = DpadDirection.North;
                    else if (buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Southwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Southeast;
                        else
                            output.dPad = DpadDirection.South;
                    else if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                        output.dPad = DpadDirection.West;
                    else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                        output.dPad = DpadDirection.East;

                    output.share = buttons[(int)Button.CAPTURE];
                    output.options = buttons[(int)Button.PLUS];
                    output.ps = buttons[(int)Button.HOME];
                    output.touchpad = buttons[(int)Button.MINUS];
                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];
                    output.thumb_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.ps = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.options = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (input.MappingValue("home") != "0")
                output.ps = false;

            if (!(isSnes || is64)) {
                if (other != null || hasDualSticks) { // no need for && other != this
                    output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                    output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);
                    output.thumb_right_x = CastStickValueByte((other == input && !isLeft) ? -stick[0] : -stick2[0]);
                    output.thumb_right_y = CastStickValueByte((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.thumb_left_y = CastStickValueByte((isLeft ? 1 : -1) * stick[0]);
                    output.thumb_left_x = CastStickValueByte((isLeft ? 1 : -1) * stick[1]);
                }
            }

            if (!is64)
            {
                if (other != null || hasDualSticks) {
                    byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                    byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                    output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                    output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
                } else {
                    output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                    output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
                }
            // Output digital L2 / R2 in addition to analog L2 / R2
            output.trigger_left = output.trigger_left_value > 0 ? output.trigger_left = true : output.trigger_left = false;
            output.trigger_right = output.trigger_right_value > 0 ? output.trigger_right = true : output.trigger_right = false;
            }

            return output;
        }
    }
}
