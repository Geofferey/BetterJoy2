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
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterJoyForCemu.VirtualOutput;

namespace BetterJoyForCemu {
    // Base class for every physical controller type BetterJoy talks to - see
    // DOCS/CONTROLLERS-REFACTOR.md for the full plan this is step 2 of. Content moves in
    // incrementally, sub-step by sub-step, not all at once - see the plan's "Suggested migration
    // approach" for why, and its "What must not regress" section for the three highest-stakes
    // surfaces (PadId/auto-join, XInput mapping, gyro/IMU) that live substantially in what will
    // eventually move here.
    //
    // Content moves in as either pure data (nested type declarations, simple fields with no
    // property getter/setter, no constructor-time computation reaching into not-yet-moved
    // subsystems) or pure/self-contained methods already written as shared helpers with no
    // per-device branching (CenterSticks/CommitButtonState below) - never entangled state like
    // the other/mapping-profile-engine/gyro-pipeline, which stays on Joycon until their own,
    // deliberately later sub-steps. Everything here is exactly as safe to read/write/call from
    // Joycon as it was before the move - C# doesn't distinguish "declared in the base class"
    // from "declared in the subclass" for field/property/method access from subclass code.
    public abstract class Controller {
        public enum state_ : uint {
            NOT_ATTACHED,
            DROPPED,
            NO_JOYCONS,
            ATTACHED,
            INPUT_MODE_0x30,
            IMU_DATA_OK,
        };
        public state_ state;

        // The canonical wire contract every subclass's report parser must populate - every
        // downstream consumer (mapping profiles, MapToXbox360Input/MapToDualShock4Input, the UDP
        // server) is built on this shared shape, not on any device-specific button layout.
        public enum Button : int {
            DPAD_DOWN = 0,
            DPAD_RIGHT = 1,
            DPAD_LEFT = 2,
            DPAD_UP = 3,
            SL = 4,
            SR = 5,
            MINUS = 6,
            HOME = 7,
            PLUS = 8,
            CAPTURE = 9,
            STICK = 10,
            SHOULDER_1 = 11,
            SHOULDER_2 = 12,

            // For pro controller
            B = 13,
            A = 14,
            Y = 15,
            X = 16,
            STICK2 = 17,
            SHOULDER2_1 = 18,
            SHOULDER2_2 = 19,
        };

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        // Device identity - every controller type has an OS-assigned HID path and (usually) a
        // real serial number, used by ControllerMappings' DeviceId/DeviceSuffix to derive a
        // stable profile identity independent of PadMacAddress (which isn't known until Attach()
        // completes for some device types).
        public string path = String.Empty;
        public string serial_number;

        // Handedness/orientation convention, not literal physical handedness for every device
        // kind - Program.cs constructs a Pro controller with isLeft=true too (see Kind's default
        // fallthrough), and single-unit devices in general default to true (matches the existing
        // "primary/solo" convention). CalibrationState.FinishCalibration reads this directly to
        // correct a real Joy-Con-side gravity-axis sign difference - see its own comment for why.
        public bool isLeft;

        // Set once at connect time for Joy-Con (placeholder-serial heuristic), re-derived every
        // packet for DualSense based on observed report length - same field, two different "who
        // updates it and when" contracts (see DOCS/CONTROLLERS-REFACTOR.md's known-issues list).
        protected bool isUSB = false;

        // Calibrated stick position, gyro/accel readings, and filtered orientation - written by
        // each subclass's own report-parsing code (Joycon.ExtractIMUValues stays Nintendo-report-
        // format-specific and isn't promoted here, it just writes into these inherited fields the
        // same way it always did), read by the gyro-mouse/gyro-stick pipeline below.
        protected float[] stick = { 0, 0 };
        protected float[] stick2 = { 0, 0 };
        protected Int16[] acc_r = { 0, 0, 0 };
        protected Vector3 acc_g;
        protected Int16[] gyr_r = { 0, 0, 0 };
        protected Vector3 gyr_g;
        protected float[] cur_rotation; // Filtered IMU data

        static float AHRS_beta = float.Parse(ConfigurationManager.AppSettings["AHRS_beta"]);
        protected MadgwickAHRS AHRS = new MadgwickAHRS(0.005f, AHRS_beta); // for getting filtered Euler angles of rotation; 5ms sampling rate

        // Stick calibration - three init strategies feed this one consuming contract (Joy-Con/Pro
        // from SPI flash via Joycon.dump_calibration_data, SNES/N64 from App.config, DualSense
        // from a hardcoded identity default in its own Attach()), all still device-specific and
        // not promoted here - only the fields themselves move, since getActiveStickData/
        // CenterSticks/the auto-cal stick-center pass below all need to read and write them.
        protected UInt16[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        protected UInt16 deadzone;
        protected UInt16[] stick_precal = { 0, 0 };
        protected UInt16[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        protected UInt16 deadzone2;
        protected UInt16[] stick2_precal = { 0, 0 };

        // Gyro/accel neutral-offset calibration data, empirically republished by
        // CalibrationState (manual wizard or auto-cal) - see ActiveCaliData. gyr_neutral is read
        // by both this and Joycon.ExtractIMUValues's own factory/manual fallback path.
        protected float[] activeData;
        protected Int16[] gyr_neutral = { 0, 0, 0 };

        public void getActiveData() {
            this.activeData = CalibrationState.ActiveCaliData(serial_number);
        }

        // Applies any empirically-recalibrated stick data on top of whatever dump_calibration_data
        // already loaded from SPI - called both there (so a controller with prior stick
        // recalibration gets it immediately on every future connect, not just the session it was
        // captured in) and right after CalibrationState.FinishStickCalibration (so it takes effect
        // without needing a reconnect). No-op per stick when ActiveStickCal returns null - i.e.
        // this controller/stick has never been recalibrated, so the SPI-read values stand.
        public void getActiveStickData() {
            ushort[] primary = CalibrationState.ActiveStickCal(serial_number, false);
            if (primary != null) {
                Array.Copy(primary, stick_cal, 6);
                PrintArray(stick_cal, DebugType.STICK, len: 6, start: 0, format: "Applied recalibrated stick data: {0:S}");
            }

            if (HasDualSticks) {
                ushort[] secondary = CalibrationState.ActiveStickCal(serial_number, true);
                if (secondary != null) {
                    Array.Copy(secondary, stick2_cal, 6);
                    PrintArray(stick2_cal, DebugType.STICK, len: 6, start: 0, format: "Applied recalibrated stick2 data: {0:S}");
                }
            }
        }

        protected void PrintArray<T>(T[] arr, DebugType d = DebugType.NONE, uint len = 0, uint start = 0, string format = "{0:S}") {
            if (d != debug_type && debug_type != DebugType.ALL) return;
            if (len == 0) len = (uint)arr.Length;
            string tostr = "";
            for (int i = 0; i < len; ++i) {
                tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
            }
            DebugPrint(string.Format(format, tostr), d);
        }

        public float[] GetStick() {
            return stick;
        }
        public float[] GetStick2() {
            return stick2;
        }
        public Vector3 GetGyro() {
            return gyr_g;
        }
        public Vector3 GetAccel() {
            return acc_g;
        }

        public OutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;

        // Raw HID handle every subclass's Attach/Poll/Detach/report-parsing code reads/writes -
        // device-agnostic (every controller type talks over one), even though what gets written
        // through it is not.
        protected IntPtr handle;
        protected bool stop_polling = true;

        // Host callback for UI/status updates (AssignSlot, AppendTextBox, etc.) - every
        // controller type needs one, regardless of device kind.
        public IJoyconHost form;

        private Thread PollThreadObj;

        // Starts this controller's own read/report thread pointed at Poll() - not itself moved
        // here (see Joycon.Poll's comment: real device-branching inside, stays put until that's
        // worth splitting on its own), just referenced by name. Fully device-agnostic otherwise.
        public void Begin() {
            if (PollThreadObj == null) {
                PollThreadObj = new Thread(new ThreadStart(Poll));
                PollThreadObj.IsBackground = true;
                PollThreadObj.Start();

                form.AppendTextBox("Starting poll thread.\r\n");
            } else {
                form.AppendTextBox("Poll cannot start.\r\n");
            }
        }

        public enum DebugType : int {
            NONE,
            ALL,
            COMMS,
            THREADING,
            IMU,
            RUMBLE,
            SHAKE,
            STICK, // appended, not inserted - existing numeric values are persisted in App.config/settings
        };
        public DebugType debug_type = (DebugType)int.Parse(ConfigurationManager.AppSettings["DebugType"]);

        public void DebugPrint(String s, DebugType d) {
            if (debug_type == DebugType.NONE) return;
            if (d == DebugType.ALL || d == debug_type || debug_type == DebugType.ALL) {
                form.AppendTextBox(s + "\r\n");
            }
        }

        // Guards RetireDuplicateConnections() below so it only ever runs once per controller, the
        // first time it actually proves itself alive (not merely that Attach() didn't throw,
        // which happens before the connection is known to be stable/receiving real data).
        protected bool retiredDuplicates = false;

        // How long a connection can go without a single successful read before being forced to
        // DROPPED even though nothing ever came back as a hard read error - see the staleness
        // check in Poll()'s loop below for why this exists.
        protected const double StaleConnectionSeconds = 3.0;

        // Device-agnostic read-loop shell: LED update dispatch, queued-rumble send, ReceiveRaw
        // dispatch with a crash guard, drop/stale-connection detection, and the final gyro-mouse-
        // release backstop. The actual per-device work happens in the hooks below - ReceiveRaw is
        // the only one with no meaningful shared default (every device's report format is
        // unrelated), so it stays abstract; the rest default to a no-op and Joycon overrides them
        // with its existing bodies unchanged.
        public void Poll() {
            stop_polling = false;
            int attempts = 0;
            long lastSuccessTimestamp = Stopwatch.GetTimestamp();
            while (!stop_polling & state > state_.NO_JOYCONS) {
                int requestedLed = Interlocked.Exchange(ref pendingLedPlayerNum, -1);
                if (requestedLed >= 0) {
                    SetLEDByPlayerNum(requestedLed);
                }
                SendQueuedRumbleIfAny();

                int a;
                try {
                    a = ReceiveRaw();
                } catch (Exception ex) {
                    // ReceiveRaw covers report parsing, gyro/stick processing, and ViGEm report
                    // building for every packet - an unhandled exception anywhere in that chain
                    // reaches here uncaught, and this runs on a bare Thread (not a UI/task
                    // context with its own handler), so .NET terminates the whole process by
                    // default. One malformed packet or transient edge case shouldn't take down
                    // every connected controller - treat it the same as a read error below
                    // (brief pause, count toward the drop threshold) instead.
                    DebugPrint("Unhandled exception in ReceiveRaw: " + ex, DebugType.ALL);
                    a = -1;
                }

                if (a > 0 && state > state_.DROPPED) {
                    state = state_.IMU_DATA_OK;
                    attempts = 0;
                    lastSuccessTimestamp = Stopwatch.GetTimestamp();

                    if (!retiredDuplicates) {
                        retiredDuplicates = true;
                        RetireDuplicateConnections();
                    }
                } else if (attempts > 240) {
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped.\r\n");

                    DebugPrint("Connection lost. Is the Joy-Con connected?", DebugType.ALL);
                    break;
                } else if (a < 0) {
                    // An error on read.
                    //form.AppendTextBox("Pause 5ms");
                    Thread.Sleep((Int32)5);
                    ++attempts;
                } else if (a == 0) {
                    // The non-blocking read timed out. No need to sleep.
                    // No need to increase attempts because it's not an error.
                }

                // Belt-and-suspenders on top of the attempts>240 hard-error threshold above: a
                // connection whose transport just goes quiet (e.g. a Bluetooth radio link
                // dropping) rather than the HID handle itself becoming invalid can have
                // hid_read_timeout return plain timeouts (a==0) forever, never a hard error -
                // confirmed on real hardware with a Bluetooth DualSense, which the attempts
                // counter above never penalizes, so it never reached DROPPED and sat as a stale,
                // frozen "connected" entry (and virtual controller) indefinitely. This is a
                // second, independent detector using elapsed wall-clock time since the last
                // genuinely successful read, regardless of why reads have been failing.
                if (state > state_.DROPPED &&
                    (Stopwatch.GetTimestamp() - lastSuccessTimestamp) / (double)Stopwatch.Frequency > StaleConnectionSeconds) {
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped (connection went silent).\r\n");
                    DebugPrint("Connection lost - no successful read in " + StaleConnectionSeconds + "s.", DebugType.ALL);
                    break;
                }
            }

            // A disconnect or detach may prevent another input report from arriving. Release
            // stateful desktop inputs here as the final backstop instead of leaving Windows with
            // a button-down whose corresponding physical controller can no longer report up.
            ReleaseGyroMouseActions();
        }

        // Every report's raw HID read + parse + downstream processing - no shared default
        // possible (every device's report format is unrelated), so this is the one truly abstract
        // hook. See Joycon.ReceiveRaw for the Nintendo-family/DualSense dual implementation
        // (still one method there until DualSenseController exists as its own subclass).
        protected abstract int ReceiveRaw();

        // No-op by default; Joycon overrides this to send whatever HD-rumble/DualSense-rumble
        // data is queued in rumble_obj - kept as a hook since rumble_obj/SendRumble/
        // SendDualSenseRumble aren't promoted to Controller yet.
        protected virtual void SendQueuedRumbleIfAny() { }

        // No-op by default; Joycon overrides this with the generic MAC-based duplicate-connection
        // dedup (plus, today, DualSense's own BT-auto-disconnect tail spliced into the same
        // method - see DOCS/CONTROLLERS-REFACTOR.md's Tier-3 "danger zone" note on this method).
        protected virtual void RetireDuplicateConnections() { }

        // No-op by default; Joycon overrides this with the actual Nintendo power-off subcommand
        // sequence (SetHCIState/Subcommand) - kept as a hook since those are Nintendo-protocol-
        // only, not promoted to Controller. Called from the (now-shared) DoThingsWithButtons on
        // HOME-long-press and power-off-on-inactivity.
        public virtual void PowerOff() { }

        // No-op by default; Joycon overrides this with Nintendo's actual LED-set subcommand -
        // already self-guards on UsesNintendoProtocol today, kept as a hook (rather than promoted
        // outright) so that guard stays exactly where the rest of Joycon's Nintendo-only output
        // wiring lives.
        public virtual void SetLEDByPlayerNum(int id) { }


        // The canonical per-report button state every subclass's report parser populates (see
        // the Button enum above) - protected, not public, since nothing outside a Controller
        // subclass's own report-parsing/mapping code reads these directly today (verified: no
        // external file referenced them before this move either, they were private on Joycon).
        // down_ is the pre-update snapshot CommitButtonState diffs the freshly-parsed buttons[]
        // against to derive buttons_down/buttons_up (rising/falling edges), and
        // buttons_down_timestamp records when each button last went down, for press-and-hold/
        // double-click detection.
        protected bool[] buttons_down = new bool[20];
        protected bool[] buttons_up = new bool[20];
        protected bool[] buttons = new bool[20];
        protected bool[] down_ = new bool[20];
        protected long[] buttons_down_timestamp = new long[20];

        // Last time any button's down/up edge changed - read by auto-power-off (HomeLongPowerOff-
        // style idle checks) and gyro-mouse idle detection, both device-generic.
        protected long inactivity = Stopwatch.GetTimestamp();

        // volatile: written by other's setter (join/split thread) and read by Joycon's
        // MappingValue/ProfileBoolOption/etc (poll thread) - see OnOtherChanging's override in
        // Joycon for the race this guards against. Type kept as Joycon (not Controller) since
        // ProcessButtonsAndStick reaches into other.otherStick, a Joycon-only field - an accepted
        // interim state until NintendoController exists as this property's natural typed home
        // (see DOCS/CONTROLLERS-REFACTOR.md step 5).
        protected volatile string mappingProfileId;
        private Joycon _other = null;

        // Pairing contract: null = solo, == this = self-paired ("vertical"), == <other instance>
        // = a real two-unit pair. Only Joy-Con-family currently ever sets this away from null
        // (see SupportsPairing) - the mechanism itself is device-agnostic (a device that never
        // pairs just never touches it), which is why it lives here rather than only on the
        // Joy-Con-specific parts of the hierarchy.
        public Joycon other {
            get {
                return _other;
            }
            set {
                if (_other != value)
                    OnOtherChanging();
                _other = value;
                mappingProfileId = null;

                // Queued (RequestLEDUpdate), not written directly - this setter runs on
                // whatever thread is doing the join/split (scan thread for auto-join, UI/pipe
                // thread for a manual one), which by this point always races this Joycon's own
                // already-running Poll() thread for the HID handle. See RequestLEDUpdate's
                // comment.
                if (_other == null || _other == this) {
                    // Solo (_other == null, held sideways) and self-paired ("vertical",
                    // _other == this, held upright) both use this Joycon's own PadId for its LED -
                    // neither has a partner controller to share a pair's LED value with.
                    RequestLEDUpdate(PadId);
                } else {
                    // Set LED to current Joycon Pair
                    int lowestPadId = Math.Min(_other.PadId, PadId);
                    RequestLEDUpdate(lowestPadId);
                }
            }
        }

        // Called just before other actually changes (join/split), before the new value takes
        // effect - lets a subclass release any state tied to its old pairing/profile identity.
        // No-op by default; Joycon overrides this to invalidate synthetic input holds under the
        // old profile (see Joycon.PrepareForMappingProfileChange) - kept as a hook rather than
        // moving that method here, since it reaches into gyro-mouse/mapping-engine state that
        // isn't shared yet.
        protected virtual void OnOtherChanging() { }

        // Requested LED player-number update, applied by this controller's own Poll() thread
        // rather than the caller's - SetLEDByPlayerNum/Subcommand does a blocking HID write+read
        // on the same handle Poll() is concurrently reading from, so calling it directly from a
        // foreign thread (the scan thread doing a mass re-rank after a drop, or other's setter
        // during a join/split) on an already-Begin()'d controller risked the response getting
        // interleaved with normal packet reads and the LED update silently timing out - matching
        // the existing rumble_obj queue pattern in Joycon, just for a single latest-wins value
        // instead of a FIFO, since only the most recent requested LED value matters. -1 means "no
        // update pending" - Interlocked.Exchange (not volatile, which int? can't be) makes the
        // read-and-clear in Poll() atomic against a concurrent RequestLEDUpdate call.
        protected int pendingLedPlayerNum = -1;

        public void RequestLEDUpdate(int playerNum) {
            Interlocked.Exchange(ref pendingLedPlayerNum, playerNum);
        }

        // Establishes the connection - part of the Controller contract (Program.cs's connect
        // loop calls this on anything it opens), but with no shared shell worth extracting:
        // every implementation is either wholly one device-specific handshake or another (see
        // Joycon.Attach for Nintendo's SPI/subcommand sequence vs. DualSense's early-return
        // baseline path), unlike Detach below where most of the method really is shared.
        public abstract int Attach();

        // Device-agnostic disconnect shell: stop the poll loop, tear down whatever virtual
        // output exists, and release the HID handle. OnDetachingWhileAttached is the one point a
        // subclass gets to send its own "give the connection back" bytes before the handle
        // closes (see Joycon's override) - everything else here is identical for every device
        // type.
        public void Detach(bool close = false) {
            stop_polling = true;

            if (out_xbox != null) {
                out_xbox.Disconnect();
            }

            if (out_ds4 != null) {
                out_ds4.Disconnect();
            }

            if (state > state_.NO_JOYCONS) {
                HIDapi.hid_set_nonblocking(handle, 0);
                OnDetachingWhileAttached();
            }
            if (close || state > state_.DROPPED) {
                HIDapi.hid_close(handle);
            }
            state = state_.NOT_ATTACHED;
        }

        // No-op by default; Joycon overrides this for Nintendo's USB-only "let the controller
        // talk to Bluetooth again" handshake. See the override for a note on isUSB's DualSense
        // edge case, preserved as-is rather than fixed by this move.
        protected virtual void OnDetachingWhileAttached() { }

        // Shared by ProcessButtonsAndStick (Joy-Con/Pro) and ParseDualSenseReport - diffs the
        // freshly-populated buttons[] against down_[] (the pre-update snapshot the caller must
        // already have taken under lock(down_), matching ProcessButtonsAndStick's own pattern)
        // into buttons_up/buttons_down/buttons_down_timestamp, and updates inactivity. Report
        // parsing itself is not shareable (the two devices' byte layouts are unrelated), just
        // this bookkeeping tail.
        protected void CommitButtonState() {
            long timestamp = Stopwatch.GetTimestamp();

            lock (buttons_up) {
                lock (buttons_down) {
                    bool changed = false;
                    for (int i = 0; i < buttons.Length; ++i) {
                        buttons_up[i] = (down_[i] & !buttons[i]);
                        buttons_down[i] = (!down_[i] & buttons[i]);
                        if (down_[i] != buttons[i])
                            buttons_down_timestamp[i] = (buttons[i] ? timestamp : -1);
                        if (buttons_up[i] || buttons_down[i])
                            changed = true;
                    }

                    inactivity = (changed) ? timestamp : inactivity;
                }
            }
        }

        // Should really be called calculating stick data. Pure function of its parameters - no
        // per-device branching, so it's shared as-is rather than duplicated (Joy-Con/Pro read
        // stick_cal from SPI flash, SNES/N64 from App.config, DualSense from a hardcoded
        // identity default - three different init strategies feeding this one consuming
        // contract, per DOCS/CONTROLLERS-REFACTOR.md).
        protected float[] CenterSticks(UInt16[] vals, ushort[] cal, ushort dz, float scaling_factor) {
            ushort[] t = cal;

            float[] s = { 0, 0 };
            float dx = vals[0] - t[2], dy = vals[1] - t[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
                return s;

            s[0] = dx / (dx > 0 ? t[0] : t[4]);
            s[1] = dy / (dy > 0 ? t[1] : t[5]);

            if (scaling_factor != 1.0f) {
                s[0] *= scaling_factor;
                s[1] *= scaling_factor;

                s[0] = Math.Max(Math.Min(s[0], 1.0f), -1.0f);
                s[1] = Math.Max(Math.Min(s[1], 1.0f), -1.0f);
            }

            return s;
        }

        // Capability contract every subclass must answer explicitly - see Joycon's overrides for
        // the original isPro/isSnes/isDualSense-flag-derived logic this replaces, and
        // DualSenseController's overrides (step 4) for how trivially these reduce to hardcoded
        // constants once a device isn't sharing a class with others. Abstract, not a defaulted
        // virtual, since there's no meaningful generic answer to any of these - every concrete
        // controller type must decide.
        public abstract bool SupportsPairing { get; }      // can combine with another unit into one logical controller
        public abstract bool HasDualSticks { get; }        // has two physical sticks/thumb-stick-click buttons on one unit
        public abstract bool HasGyro { get; }               // currently populates real gyr_g/acc_g data
        public abstract bool HasAnalogTriggers { get; }     // triggers report a real analog value, not just a digital button bit
        public abstract bool UsesNintendoProtocol { get; }  // speaks the Joy-Con SPI/subcommand protocol (LED, rumble encoding, handshake)

        // Single source of truth for device-kind identity - see ServiceControlProtocol.cs for
        // the ControllerKind enum this returns (used by the remote-mode snapshot protocol).
        public abstract ControllerKind Kind { get; }

        // Monotonic creation order, assigned once in the constructor - used to decide which half
        // of a pair gets disconnected on join: whichever connected (and got its virtual
        // controller created) FIRST is the one most likely to already be the controller a
        // running game has locked onto, so joining always disconnects the newer one and keeps
        // the older one active, regardless of which physical Joycon you click to initiate the
        // join or which one a scan pass happens to enumerate first. Confirmed by testing:
        // disconnecting/suppressing the wrong half left
        // a game's already-locked-on slot silent while input went to a different slot it wasn't
        // watching.
        private static long nextVirtualControllerSequence = 0;
        public readonly long virtualControllerSequence = System.Threading.Interlocked.Increment(ref nextVirtualControllerSequence);

        // Kept public for compatibility with existing callers; this is now specifically the
        // activation latch for gyro-to-mouse. Stick outputs have independent latches below.
        public bool active_gyro = false;
        protected bool activeGyroLeftStick = false;
        protected bool activeGyroRightStick = false;

        // Real elapsed time since the last DoThingsWithButtons call, used to scale raw angular
        // velocity (gyr_g) into a per-packet rotation amount - previously a hardcoded 0.015f
        // (assumed 15ms/~66Hz) regardless of how much time had actually passed. Report timing
        // isn't perfectly metronomic, especially over Bluetooth, so a fixed assumption scales
        // every frame's motion by however wrong that assumption happened to be that frame -
        // read as jittery/inconsistent speed rather than smooth motion, independent of anything
        // else about the connection or IMU filtering settings. -1 the first call so a long gap
        // before the very first packet (e.g. right after connecting) can't produce a huge dt.
        protected long lastDoThingsTimestamp = -1;

        // Each output tracks its own combo edge and toggle state. This lets the same profile use
        // gyro mouse and either virtual stick independently (or at the same time).
        protected bool prevActiveGyroMouseComboHeld = false;
        protected bool prevActiveGyroLeftStickComboHeld = false;
        protected bool prevActiveGyroRightStickComboHeld = false;
        protected bool gyroMouseEnabledThisReport = false;

        // Same idea for reset_mouse - a one-shot action needs the rising edge only, or it would
        // keep re-centering every packet for as long as the bind stays held.
        protected bool prevResetMouseComboHeld = false;

        // Updated once per controller report in DoThingsWithButtons, then consumed by all three
        // IMU sub-samples from that report. Clenching suppresses pointer output without changing
        // active_gyro, so clicks and the rest of the active gyro-mouse state remain intact.
        protected bool gyroMouseClenched = false;

        // Gyro-only actions reserve their assigned physical controller buttons while gyro-mouse
        // is active. Keep this mask separate from buttons[]: special actions and UDP still need
        // the real input; only the virtual Xbox/DS4 report should consume it. The snapshot is
        // reused to avoid adding per-report garbage collection to the latency-sensitive path.
        protected static readonly string[] GyroOnlyBindKeys = {
            "left_click", "right_click", "center_click", "scroll_up", "scroll_down",
            "clench_gyro"
        };
        // One extra slot holds reset_mouse, which is also gyro-mouse-only under the same active
        // gate. All seven values come from the current logical controller profile.
        protected readonly string[] lastGyroOnlyBindValues =
            new string[GyroOnlyBindKeys.Length + 1];
        protected readonly bool[] gyroOnlyReservedButtons = new bool[20];
        protected readonly bool[] vigemButtons = new bool[20];

        protected string MappingValue(string key) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            string value = ControllerMappings.Value(mappingProfileId, key);

            OnMappingValueResolved(key, value);

            return value;
        }

        // No-op by default; Joycon overrides this with a TEMPORARY DualSense-specific diagnostic
        // dump (see the override) - kept as a hook since isDualSense/LogDualSenseRawDump aren't
        // promoted to Controller.
        protected virtual void OnMappingValueResolved(string key, string value) { }

        protected bool ProfileBoolOption(string key) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            return ControllerMappings.BoolOption(mappingProfileId, key);
        }

        protected int ProfileIntOption(string key, int fallback = -1) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            return ControllerMappings.IntOption(mappingProfileId, key, fallback);
        }

        protected string ProfileStringOption(string key, string fallback) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            string value = ControllerMappings.OptionValue(mappingProfileId, key);
            return String.IsNullOrEmpty(value) ? fallback : value;
        }

        // Join/split changes which mapping profile this physical half belongs to. Release any
        // synthetic holds under the old profile before changing topology; otherwise pressing an
        // SL/SR mouse/key bind while joined and releasing it after a split would look up the new
        // solo bind for the release and could leave the old profile's key stuck down forever.
        protected void PrepareForMappingProfileChange() {
            if (form == null)
                return;

            ReleaseGyroMouseActions();
            ReleaseMappedHold(MappingValue(isLeft ? "sl_l" : "sl_r"));
            ReleaseMappedHold(MappingValue(isLeft ? "sr_l" : "sr_r"));
            if (hasShaked)
                ReleaseMappedHold(MappingValue("shake"));

            hasShaked = false;
            mouse_toggle_btn.Clear();
            active_gyro = false;
            activeGyroLeftStick = false;
            activeGyroRightStick = false;
            prevActiveGyroMouseComboHeld = false;
            prevActiveGyroLeftStickComboHeld = false;
            prevActiveGyroRightStickComboHeld = false;
            gyroMouseEnabledThisReport = false;
            gyroLeftStickActiveThisReport = false;
            gyroRightStickActiveThisReport = false;
            prevResetMouseComboHeld = false;
            gyroMouseClenched = false;
            gyroStickRatcheted = false;
        }

        protected void ReleaseMappedHold(string mapping) {
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return;

            // Simulate(mapping, click:false, up:true) already releases key_ parts
            // unconditionally, and mse_ parts too whenever DragToggle is off. The one gap is
            // mse_ under DragToggle: its hold branch only acts when !up (see Simulate's
            // dragToggle block above), so a toggled-on mouse hold needs forcing below regardless
            // of toggle state - mouse_toggle_btn itself doesn't need resetting here, the caller
            // (PrepareForMappingProfileChange) clears it right after this returns.
            Simulate(mapping, click: false, up: true);

            if (ProfileBoolOption("DragToggle")) {
                foreach (string part in mapping.Split('+')) {
                    int code;
                    if (part.StartsWith("mse_") && Int32.TryParse(part.Substring(4), out code))
                        form.SimulateButtonRelease(code);
                }
            }
        }

        // A bind is one or more "joy_N"/"key_N"/"mse_N" parts joined with "+" (a single part is
        // just a combo of one) - true only when every part is currently held at once. Controller
        // parts check this Joycon's own buttons (and its pair partner's, if joined, matching how
        // every other joy_ bind here already treats a pair as one logical controller);
        // keyboard/mouse parts check InputState, fed from Program.OnKeyDown/OnKeyUp/
        // OnMouseButtonDown/OnMouseButtonUp - the same unified entry points that already work in
        // both GUI and service mode.
        protected bool IsComboHeld(string combo) {
            foreach (string part in combo.Split('+')) {
                if (part.StartsWith("joy_")) {
                    int i = Int32.Parse(part.Substring(4));
                    if (!(buttons[i] || (other != null && other != this && other.buttons[i])))
                        return false;
                } else if (part.StartsWith("key_")) {
                    if (!InputState.IsKeyHeld(Int32.Parse(part.Substring(4))))
                        return false;
                } else if (part.StartsWith("mse_")) {
                    if (!InputState.IsMouseButtonHeld(Int32.Parse(part.Substring(4))))
                        return false;
                } else {
                    return false; // malformed/unknown part - fail closed rather than ignore it
                }
            }
            return true;
        }

        // Gyro activation mappings have three explicit states:
        //   always  - active without a bind
        //   0       - disabled
        //   combo   - controlled by the profile's hold/toggle preference
        // The old unbound value is migrated to "always" by ControllerMappings, so 0 can safely
        // mean disabled for every new output without unexpectedly enabling all three.
        protected bool UpdateGyroActivation(string key, ref bool toggledActive,
                                           ref bool previousComboHeld,
                                           out bool justEnabled) {
            string mapping = MappingValue(key);
            if (mapping == "always") {
                toggledActive = false;
                previousComboHeld = false;
                justEnabled = false; // always-on has no activation edge to recenter on
                return true;
            }

            if (String.IsNullOrEmpty(mapping) || mapping == "0") {
                toggledActive = false;
                previousComboHeld = false;
                justEnabled = false;
                return false;
            }

            bool wasEnabled = toggledActive;
            bool comboHeld = IsComboHeld(mapping);
            if (ProfileBoolOption("GyroHoldToggle")) {
                toggledActive = comboHeld;
            } else if (comboHeld && !previousComboHeld) {
                toggledActive = !toggledActive;
            }
            previousComboHeld = comboHeld;
            justEnabled = !wasEnabled && toggledActive;
            return toggledActive;
        }

        // Rebuild only when a bind actually changes. ControllerMappings is live-reloaded by the
        // service watcher, so this also picks up remote edits without reconnecting the controller.
        protected void RefreshGyroOnlyButtonReservations() {
            bool changed = false;
            for (int i = 0; i < GyroOnlyBindKeys.Length; i++) {
                string value = MappingValue(GyroOnlyBindKeys[i]);
                if (lastGyroOnlyBindValues[i] != value) {
                    lastGyroOnlyBindValues[i] = value;
                    changed = true;
                }
            }

            int resetMouseSlot = GyroOnlyBindKeys.Length;
            string resetMouseValue = MappingValue("reset_mouse");
            if (String.IsNullOrEmpty(resetMouseValue))
                resetMouseValue = "0";
            if (lastGyroOnlyBindValues[resetMouseSlot] != resetMouseValue) {
                lastGyroOnlyBindValues[resetMouseSlot] = resetMouseValue;
                changed = true;
            }

            if (!changed)
                return;

            Array.Clear(gyroOnlyReservedButtons, 0, gyroOnlyReservedButtons.Length);
            foreach (string value in lastGyroOnlyBindValues) {
                if (value == "0")
                    continue;

                foreach (string part in value.Split('+')) {
                    if (!part.StartsWith("joy_"))
                        continue; // keyboard/mouse combo members never enter ViGEm

                    int buttonIndex;
                    if (Int32.TryParse(part.Substring(4), out buttonIndex) &&
                        buttonIndex >= 0 && buttonIndex < gyroOnlyReservedButtons.Length)
                        gyroOnlyReservedButtons[buttonIndex] = true;
                }
            }
        }

        protected bool OwnsGyroMouse() {
            return !SupportsPairing || other == null || other == this ||
                 (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"])
                    ? isLeft : !isLeft);
        }

        protected bool IsGyroMouseActive() {
            return OwnsGyroMouse() && gyroMouseEnabledThisReport;
        }

        // A joined pair's ViGEm target stays on whichever half connected first, while gyro-mouse
        // ownership is selected independently by handedness. Query both halves so consumption
        // follows the actual gyro owner instead of whichever object happens to emit the report.
        protected bool PairHasActiveGyroMouse() {
            return IsGyroMouseActive() ||
                (other != null && other != this && other.IsGyroMouseActive());
        }

        // Bind capture deliberately uses the left Joycon as a joined pair's canonical Pro-style
        // view. If the ViGEm target survived on the right Joycon, that object's local button array
        // stores the same physical controls under the opposite-half indices. Translate the
        // canonical reserved index before filtering so join order cannot make us consume the
        // wrong physical control.
        protected int CanonicalButtonToLocalVigemIndex(int canonicalIndex) {
            if (!SupportsPairing || other == null || other == this || isLeft)
                return canonicalIndex;

            switch ((Button)canonicalIndex) {
                case Button.DPAD_DOWN: return (int)Button.B;
                case Button.DPAD_RIGHT: return (int)Button.A;
                case Button.DPAD_LEFT: return (int)Button.Y;
                case Button.DPAD_UP: return (int)Button.X;
                case Button.B: return (int)Button.DPAD_DOWN;
                case Button.A: return (int)Button.DPAD_RIGHT;
                case Button.Y: return (int)Button.DPAD_LEFT;
                case Button.X: return (int)Button.DPAD_UP;
                case Button.STICK: return (int)Button.STICK2;
                case Button.STICK2: return (int)Button.STICK;
                case Button.SHOULDER_1: return (int)Button.SHOULDER2_1;
                case Button.SHOULDER2_1: return (int)Button.SHOULDER_1;
                case Button.SHOULDER_2: return (int)Button.SHOULDER2_2;
                case Button.SHOULDER2_2: return (int)Button.SHOULDER_2;
                default: return canonicalIndex;
            }
        }

        protected bool[] GetButtonsForVigem() {
            if (!PairHasActiveGyroMouse())
                return buttons;

            Array.Copy(buttons, vigemButtons, buttons.Length);
            for (int canonicalIndex = 0;
                 canonicalIndex < gyroOnlyReservedButtons.Length;
                 canonicalIndex++) {
                if (gyroOnlyReservedButtons[canonicalIndex])
                    vigemButtons[CanonicalButtonToLocalVigemIndex(canonicalIndex)] = false;
            }
            return vigemButtons;
        }

        protected readonly Stopwatch shakeTimer = Stopwatch.StartNew(); //Setup a timer for measuring shake in milliseconds
        protected long shakedTime = 0;
        protected bool hasShaked;
        protected bool shakeInputEnabled = Boolean.Parse(ConfigurationManager.AppSettings["EnableShakeInput"]);
        protected float shakeSensitivity = float.Parse(ConfigurationManager.AppSettings["ShakeInputSensitivity"]);
        protected float shakeDelay = float.Parse(ConfigurationManager.AppSettings["ShakeInputDelay"]);
        protected void DetectShake() {
            if (shakeInputEnabled) {
                long currentShakeTime = shakeTimer.ElapsedMilliseconds;

                // Shake detection logic
                bool isShaking = GetAccel().LengthSquared() >= shakeSensitivity;
                if (isShaking && currentShakeTime >= shakedTime + shakeDelay || isShaking && shakedTime == 0) {
                    shakedTime = currentShakeTime;
                    hasShaked = true;

                    // Mapped shake key down
                    Simulate(MappingValue("shake"), false, false);
                    DebugPrint("Shaked at time: " + shakedTime.ToString(), DebugType.SHAKE);
                }

                // If controller was shaked then release mapped key after a small delay to simulate a button press, then reset hasShaked
                if (hasShaked && currentShakeTime >= shakedTime + 10) {
                    // Mapped shake key up
                    Simulate(MappingValue("shake"), false, true);
                    DebugPrint("Shake completed", DebugType.SHAKE);
                    hasShaked = false;
                }

            } else {
                shakeTimer.Stop();
                return;
            }
        }

        // ConcurrentDictionary, not plain Dictionary: PrepareForMappingProfileChange (join/split
        // thread) calls Clear() on this while the poll thread concurrently reads/writes it via
        // Simulate() - a plain Dictionary under that access pattern can throw or corrupt its
        // bucket state.
        ConcurrentDictionary<int, bool> mouse_toggle_btn = new ConcurrentDictionary<int, bool>();

        // s can be a "+"-joined combo (see Reassign's combo capture) - here that means "simulate
        // all of these together", e.g. a capture bind of "key_17+key_67" presses Ctrl+C. Any
        // joy_ part is silently skipped - there's no "press another virtual controller button"
        // output, that's what SimulateContinous below is for instead.
        protected void Simulate(string s, bool click = true, bool up = false) {
            foreach (string part in s.Split('+')) {
                if (part.StartsWith("key_")) {
                    int key = Int32.Parse(part.Substring(4));
                    if (click) {
                        form.SimulateKeyClick(key);
                    } else {
                        if (up) {
                            form.SimulateKeyRelease(key);
                        } else {
                            form.SimulateKeyHold(key);
                        }
                    }
                } else if (part.StartsWith("mse_")) {
                    int button = Int32.Parse(part.Substring(4));
                    if (click) {
                        form.SimulateButtonClick(button);
                    } else {
                        if (ProfileBoolOption("DragToggle")) {
                            if (!up) {
                                bool release;
                                mouse_toggle_btn.TryGetValue(button, out release);
                                if (release)
                                    form.SimulateButtonRelease(button);
                                else
                                    form.SimulateButtonHold(button);
                                mouse_toggle_btn[button] = !release;
                            }
                        } else {
                            if (up) {
                                form.SimulateButtonRelease(button);
                            } else {
                                form.SimulateButtonHold(button);
                            }
                        }
                    }
                }
            }
        }

        // For Joystick->Joystick inputs - s can likewise be a "+"-joined combo; every joy_ part
        // gets OR'd in (mse_/key_ parts are Simulate's job above, not this one's).
        protected void SimulateContinous(int origin, string s) {
            foreach (string part in s.Split('+')) {
                if (part.StartsWith("joy_")) {
                    int button = Int32.Parse(part.Substring(4));
                    buttons[button] |= buttons[origin];
                }
            }
        }

        protected bool ChangeOrientationDoubleClick = Boolean.Parse(ConfigurationManager.AppSettings["ChangeOrientationDoubleClick"]);
        protected long lastDoubleClick = -1;

        protected bool UseFilteredIMU = Boolean.Parse(ConfigurationManager.AppSettings["UseFilteredIMU"]);
        // TEMPORARY, for the figure-eight drift investigation (see CODE_REVIEW.md) - off by
        // default since the logging itself (file I/O every ~150ms while gyro-mouse is active) is
        // its own source of timing interference, exactly the kind of thing this investigation has
        // spent most of its effort chasing out of the real path. Only turn on while deliberately
        // capturing a test.
        protected bool GyroMouseDebugLogging = Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseDebugLogging"]);
        protected bool GyroStickDebugLogging = Boolean.Parse(ConfigurationManager.AppSettings["GyroStickDebugLogging"]);
        protected bool GyroMouseDirectCursor = Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseDirectCursor"]);
        protected bool GyroMouseScreenWrap = Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseScreenWrap"]);
        protected int GyroMouseSensitivityX = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityX"]);
        protected int GyroMouseSensitivityY = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityY"]);
        const float GyroMouseDefaultScreenTraversalDegrees = 45.0f;
        protected float GyroMouseScreenTraversalDegrees = float.Parse(ConfigurationManager.AppSettings["GyroMouseScreenTraversalDegrees"]);
        protected float GyroMouseTighteningThreshold = float.Parse(ConfigurationManager.AppSettings["GyroMouseTighteningThreshold"]);
        protected int GyroMouseSmoothingTimeMs = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSmoothingTimeMs"]);
        protected float GyroMouseSmoothingThreshold = float.Parse(ConfigurationManager.AppSettings["GyroMouseSmoothingThreshold"]);
        protected float GyroStickSensitivityX = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityX"]);
        protected float GyroStickSensitivityY = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityY"]);
        protected float GyroStickReduction = float.Parse(ConfigurationManager.AppSettings["GyroStickReduction"]);
        protected float GyroStickTiltRangeX = float.Parse(ConfigurationManager.AppSettings["GyroStickTiltRangeX"]);
        protected float GyroStickTiltRangeY = float.Parse(ConfigurationManager.AppSettings["GyroStickTiltRangeY"]);
        protected float GyroStickHybridRateWeight = float.Parse(ConfigurationManager.AppSettings["GyroStickHybridRateWeight"]);
        protected bool GyroAnalogSliders => ProfileBoolOption("GyroAnalogSliders");
        // "rate" (default, matches long-standing behavior) | "absolute" | "hybrid", independently
        // per stick - see GyroStickTiltRangeX/Y and GyroStickHybridRateWeight in App.config. Only
        // takes effect when UseFilteredIMU is true; raw mode has no absolute-angle source for the
        // stick.
        protected string GyroStickModeLeft => ProfileStringOption("GyroStickModeLeft", "rate");
        protected string GyroStickModeRight => ProfileStringOption("GyroStickModeRight", "rate");
        // "yaw" (default, twisting the controller) or "roll" (banking it side-to-side, for
        // flight-sim-style aileron input), independently per stick. Y always follows pitch.
        protected string GyroStickAxisXLeft => ProfileStringOption("GyroStickAxisXLeft", "yaw");
        protected string GyroStickAxisXRight => ProfileStringOption("GyroStickAxisXRight", "yaw");
        protected bool GyroStickInvertXLeft => ProfileBoolOption("GyroStickInvertXLeft");
        protected bool GyroStickInvertYLeft => ProfileBoolOption("GyroStickInvertYLeft");
        protected bool GyroStickInvertXRight => ProfileBoolOption("GyroStickInvertXRight");
        protected bool GyroStickInvertYRight => ProfileBoolOption("GyroStickInvertYRight");
        // 0-100%. Caps how far gyro alone may deflect a stick - the physical stick can still
        // reach full deflection independently on top of a capped gyro contribution. Works in
        // both raw and filtered IMU mode, unlike Mode/AxisX/Invert above.
        protected int GyroStickMaxDeflectionXLeft => ProfileIntOption("GyroStickMaxDeflectionXLeft", 100);
        protected int GyroStickMaxDeflectionYLeft => ProfileIntOption("GyroStickMaxDeflectionYLeft", 100);
        protected int GyroStickMaxDeflectionXRight => ProfileIntOption("GyroStickMaxDeflectionXRight", 100);
        protected int GyroStickMaxDeflectionYRight => ProfileIntOption("GyroStickMaxDeflectionYRight", 100);
        // 0-100%. The instant real gyro rotation is detected, output jumps to at least this much
        // deflection instead of ramping from near-zero (see ApplyDeflectionLimits).
        protected int GyroStickMinDeflectionXLeft => ProfileIntOption("GyroStickMinDeflectionXLeft", 0);
        protected int GyroStickMinDeflectionYLeft => ProfileIntOption("GyroStickMinDeflectionYLeft", 0);
        protected int GyroStickMinDeflectionXRight => ProfileIntOption("GyroStickMinDeflectionXRight", 0);
        protected int GyroStickMinDeflectionYRight => ProfileIntOption("GyroStickMinDeflectionYRight", 0);

        protected static bool IsAbsoluteOrHybridGyroStickMode(string mode) {
            return mode == "absolute" || mode == "hybrid";
        }

        // Not user-facing - small fixed gate distinguishing genuine rotation from residual gyro
        // noise at rest, so Min doesn't pin the stick near its floor 24/7 from calibrated sensor
        // jitter alone.
        protected const float DeflectionNoiseEpsilon = 0.001f;

        // A plain two-sided clamp, not a rescale: anything already inside [min, max] passes
        // through untouched: only overshoot gets capped and only already-moving undershoot gets
        // floored. At defaults (min=0, max=100) this is the identity function past the noise gate.
        protected static float ApplyDeflectionLimits(float rawValue, int minPercent, int maxPercent) {
            float magnitude = Math.Abs(rawValue);
            if (magnitude < DeflectionNoiseEpsilon)
                return 0.0f;

            float minFraction = Math.Max(0, Math.Min(100, minPercent)) / 100.0f;
            float maxFraction = Math.Max(0, Math.Min(100, maxPercent)) / 100.0f;
            if (maxFraction < minFraction)
                maxFraction = minFraction; // guard against inverted config

            magnitude = Math.Min(magnitude, maxFraction);
            magnitude = Math.Max(magnitude, minFraction);
            return Math.Sign(rawValue) * magnitude;
        }
        protected int GyroAnalogSensitivity = Int32.Parse(ConfigurationManager.AppSettings["GyroAnalogSensitivity"]);
        protected byte[] sliderVal = new byte[] { 0, 0 };

        // A/B/X/Y only ever reflect THIS device's own buttons on a Pro controller. A solo
        // Joycon's 4 primary buttons live at the DPAD_* indices instead (labeled a d-pad on the
        // left one, the same 4 buttons Nintendo prints as A/B/X/Y on the right) - and critically,
        // that's still true when joined: ProcessButtonsAndStick's buttons[A/B/X/Y] cross-
        // reference on a joined pair pulls from the OTHER Joycon's DPAD_* to build one merged
        // Pro-style layout for output, so it does NOT represent this specific physical device's
        // own buttons. Checking DPAD_* here instead, unconditionally for every non-Pro case,
        // is what actually stays correct for whichever single physical Joycon the caller means.
        protected bool CalibrationConfirmPressed() {
            if (!SupportsPairing)
                return buttons_down[(int)Button.A] || buttons_down[(int)Button.B] || buttons_down[(int)Button.X] || buttons_down[(int)Button.Y];
            return buttons_down[(int)Button.DPAD_UP] || buttons_down[(int)Button.DPAD_DOWN] || buttons_down[(int)Button.DPAD_LEFT] || buttons_down[(int)Button.DPAD_RIGHT];
        }

        protected void DoThingsWithButtons() {
            // Checked first and returns early like the other button-driven side effects below -
            // a face button doubling as "confirm" only ever matters while a calibration prompt
            // is actually showing (PendingConfirmController names this exact controller only
            // then), so there's no real conflict with its normal mapped behavior the rest of the
            // time.
            if (CalibrationState.PendingConfirmController == this && CalibrationConfirmPressed()) {
                ReleaseGyroMouseActions();
                form.HandleCalibrationConfirm(this);
                return;
            }

            int powerOffButton = (int)((!SupportsPairing || !isLeft || other != null) ? Button.HOME : Button.CAPTURE);

            long timestamp = Stopwatch.GetTimestamp();
            if (ProfileBoolOption("HomeLongPowerOff") && buttons[powerOffButton]) {
                if ((timestamp - buttons_down_timestamp[powerOffButton]) / 10000 > 2000.0) {
                    if (other != null)
                        other.PowerOff();

                    ReleaseGyroMouseActions();
                    PowerOff();
                    return;
                }
            }

            if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && lastDoubleClick != -1 && SupportsPairing) {
                if ((buttons_down_timestamp[(int)Button.STICK] - lastDoubleClick) < 3000000) {
                    ReleaseGyroMouseActions();
                    // is-check, not a bare cast: JoinOrSplitJoycon is Joycon-typed (pairing is
                    // Joy-Con-only, see Controller.other's comment) - SupportsPairing being true
                    // above already guarantees this is a Joycon today, but this stays correct
                    // (a silent no-op) rather than throwing if that ever changes.
                    if (this is Joycon joyconForDoubleClick)
                        form.JoinOrSplitJoycon(joyconForDoubleClick); // trigger connection button click

                    lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                    return;
                }
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            } else if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && SupportsPairing) {
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            }

            int powerOffInactivityMins = ProfileIntOption("PowerOffInactivity", -1);
            if (powerOffInactivityMins > 0) {
                if ((timestamp - inactivity) / 10000 > powerOffInactivityMins * 60 * 1000) {
                    if (other != null)
                        other.PowerOff();

                    ReleaseGyroMouseActions();
                    PowerOff();
                    return;
                }
            }

            TryAutoCalibrate();

            DetectShake();

            if (buttons_down[(int)Button.CAPTURE])
                Simulate(MappingValue("capture"));
            if (buttons_down[(int)Button.HOME])
                Simulate(MappingValue("home"));
            SimulateContinous((int)Button.CAPTURE, MappingValue("capture"));
            SimulateContinous((int)Button.HOME, MappingValue("home"));

            if (isLeft) {
                if (buttons_down[(int)Button.SL])
                    Simulate(MappingValue("sl_l"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(MappingValue("sl_l"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(MappingValue("sr_l"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(MappingValue("sr_l"), false, true);

                SimulateContinous((int)Button.SL, MappingValue("sl_l"));
                SimulateContinous((int)Button.SR, MappingValue("sr_l"));
            } else {
                if (buttons_down[(int)Button.SL])
                    Simulate(MappingValue("sl_r"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(MappingValue("sl_r"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(MappingValue("sr_r"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(MappingValue("sr_r"), false, true);

                SimulateContinous((int)Button.SL, MappingValue("sl_r"));
                SimulateContinous((int)Button.SR, MappingValue("sr_r"));
            }

            // Filtered IMU data
            this.cur_rotation = AHRS.GetEulerAngles();

            long nowTimestamp = Stopwatch.GetTimestamp();
            float dt = lastDoThingsTimestamp < 0
                ? 0.015f // no prior packet to measure from yet - same assumption this always used
                : (float)((nowTimestamp - lastDoThingsTimestamp) / (double)Stopwatch.Frequency);
            lastDoThingsTimestamp = nowTimestamp;

            // Evaluate all three profile outputs independently. A real mouse activation edge
            // recenters; Always Active intentionally has no edge, matching the old unbound
            // activation behavior.
            bool gyroMouseJustEnabled;
            gyroMouseEnabledThisReport = UpdateGyroActivation(
                "active_gyro_mouse", ref active_gyro,
                ref prevActiveGyroMouseComboHeld, out gyroMouseJustEnabled);
            bool gyroLeftStickJustEnabled;
            gyroLeftStickActiveThisReport = UpdateGyroActivation(
                "active_gyro_left_stick", ref activeGyroLeftStick,
                ref prevActiveGyroLeftStickComboHeld, out gyroLeftStickJustEnabled);
            bool gyroRightStickJustEnabled;
            gyroRightStickActiveThisReport = UpdateGyroActivation(
                "active_gyro_right_stick", ref activeGyroRightStick,
                ref prevActiveGyroRightStickComboHeld, out gyroRightStickJustEnabled);
            gyroStickReportDt = dt;

            RefreshGyroOnlyButtonReservations();

            bool ownsGyroMouse = OwnsGyroMouse();
            bool gyroMouseActionsEnabled = ownsGyroMouse && gyroMouseEnabledThisReport;
            gyroMouseJustEnabled = ownsGyroMouse && gyroMouseJustEnabled;

            string clenchGyroVal = MappingValue("clench_gyro");
            gyroMouseClenched = gyroMouseActionsEnabled && clenchGyroVal != "0" &&
                IsComboHeld(clenchGyroVal);

            string ratchetGyroVal = MappingValue("ratchet_gyro");
            gyroStickRatcheted = (gyroLeftStickActiveThisReport || gyroRightStickActiveThisReport) &&
                ratchetGyroVal != "0" && IsComboHeld(ratchetGyroVal);

            // "Re-Centre Gyro" is a one-shot orientation operation, not merely a request to move
            // the Windows pointer. Apply it before sliders/stick/mouse consume this packet so the
            // pose held at the rising edge is neutral immediately. The legacy config key remains
            // reset_mouse for compatibility. Keep tracking the bind edge while gyro-mouse is
            // inactive, but do not let it reset orientation or move the pointer unless this
            // controller currently owns active gyro-mouse output.
            string resetMouseVal = MappingValue("reset_mouse");
            bool manualRecenterRequested = false;
            bool gyroStickManualRecenterRequested = false;
            if (resetMouseVal != "0") {
                bool resetMouseHeld = IsComboHeld(resetMouseVal);
                bool resetMouseRisingEdge = resetMouseHeld && !prevResetMouseComboHeld;
                manualRecenterRequested = gyroMouseActionsEnabled && resetMouseRisingEdge;
                // Same bind, independently gated: lets a controller using gyro-stick only (no
                // gyro-mouse output at all) still have a way to declare its current pose neutral,
                // without requiring a second bind just for Absolute/Hybrid stick modes. Mode is
                // per-stick, so each side only requests a recenter under its own mode/active state.
                gyroStickManualRecenterRequested = UseFilteredIMU && resetMouseRisingEdge &&
                    ((gyroLeftStickActiveThisReport && IsAbsoluteOrHybridGyroStickMode(GyroStickModeLeft)) ||
                     (gyroRightStickActiveThisReport && IsAbsoluteOrHybridGyroStickMode(GyroStickModeRight)));
                prevResetMouseComboHeld = resetMouseHeld;
            } else {
                prevResetMouseComboHeld = false;
            }

            // Gyro-stick's own activation edge also recenters when Absolute/Hybrid mode is
            // configured, mirroring gyro-mouse's auto-recenter-on-activation below - Absolute
            // tilt output is only meaningful relative to a neutral pose, so establishing one
            // automatically on activation (rather than requiring "Re-center gyro" to be bound)
            // matches how gyro-mouse already behaves. Checked independently per stick since mode
            // is now per-stick.
            bool gyroStickActivationRecenter = UseFilteredIMU &&
                ((gyroLeftStickJustEnabled && IsAbsoluteOrHybridGyroStickMode(GyroStickModeLeft)) ||
                 (gyroRightStickJustEnabled && IsAbsoluteOrHybridGyroStickMode(GyroStickModeRight)));

            // Enabling gyro-mouse establishes both a fresh desktop origin and a fresh orientation
            // frame. If the manual recenter bind rises on that same report, perform the operation
            // only once. Gyro-stick-only recenters (Absolute/Hybrid) share the same orientation
            // reset but must never move the mouse pointer, so SimulateMoveToScreenCenter stays
            // gated on only the original two conditions.
            if (gyroMouseJustEnabled || manualRecenterRequested ||
                gyroStickActivationRecenter || gyroStickManualRecenterRequested) {
                if (gyroMouseJustEnabled || manualRecenterRequested)
                    form.SimulateMoveToScreenCenter();

                RecenterGyro();
                dt = 0.0f;
                LogGyroMouseDiagnosticMarker(gyroMouseJustEnabled ? "GYRO ENABLED"
                    : (manualRecenterRequested ? "RESET" : "STICK RECENTER"));
            }

            if (GyroAnalogSliders && (other != null || HasDualSticks)) {
                Button leftT = isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2;
                Button rightT = isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2;
                Controller left = isLeft ? this : (HasDualSticks ? this : this.other); Controller right = !isLeft ? this : (HasDualSticks ? this : this.other);

                int ldy, rdy;
                if (UseFilteredIMU) {
                    ldy = (int)(GyroAnalogSensitivity * (left.cur_rotation[0] - left.cur_rotation[3]));
                    rdy = (int)(GyroAnalogSensitivity * (right.cur_rotation[0] - right.cur_rotation[3]));
                } else {
                    ldy = (int)(GyroAnalogSensitivity * (left.gyr_g.Y * dt));
                    rdy = (int)(GyroAnalogSensitivity * (right.gyr_g.Y * dt));
                }

                if (buttons[(int)leftT]) {
                    sliderVal[0] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[0] + ldy));
                } else {
                    sliderVal[0] = 0;
                }

                if (buttons[(int)rightT]) {
                    sliderVal[1] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[1] + rdy));
                } else {
                    sliderVal[1] = 0;
                }
            }

            if (!UseFilteredIMU &&
                (gyroLeftStickActiveThisReport || gyroRightStickActiveThisReport)) {
                float dx = 0.0f;
                float dy = 0.0f;
                if (!gyroStickRatcheted) {
                    dx = GyroStickSensitivityX * (gyr_g.Z * dt); // yaw
                    dy = -GyroStickSensitivityY * (gyr_g.Y * dt); // pitch
                }
                float[] diagnosticStick = gyroLeftStickActiveThisReport ? stick : stick2;
                float diagnosticPhysicalX = diagnosticStick[0];
                float diagnosticPhysicalY = diagnosticStick[1];

                float diagnosticDx = dx;
                float diagnosticDy = dy;
                if (gyroLeftStickActiveThisReport) {
                    diagnosticDx = ApplyDeflectionLimits(dx, GyroStickMinDeflectionXLeft, GyroStickMaxDeflectionXLeft);
                    diagnosticDy = ApplyDeflectionLimits(dy, GyroStickMinDeflectionYLeft, GyroStickMaxDeflectionYLeft);
                    ApplyGyroToStick(stick, diagnosticDx, diagnosticDy);
                }
                if (gyroRightStickActiveThisReport) {
                    diagnosticDx = ApplyDeflectionLimits(dx, GyroStickMinDeflectionXRight, GyroStickMaxDeflectionXRight);
                    diagnosticDy = ApplyDeflectionLimits(dy, GyroStickMinDeflectionYRight, GyroStickMaxDeflectionYRight);
                    ApplyGyroToStick(stick2, diagnosticDx, diagnosticDy);
                }

                CaptureGyroStickDiagnosticOutput(true, dt,
                    diagnosticPhysicalX, diagnosticPhysicalY, diagnosticDx, diagnosticDy,
                    diagnosticStick[0], diagnosticStick[1]);
            }

            // Movement itself is applied per IMU sub-sample in ProcessGyroMouseSample. Reconcile
            // button state on every controller report, including reports where gyro-mouse is
            // inactive or this half no longer owns it. Otherwise leaving either condition while
            // a synthetic button is down skips the only path that could send its matching up.
            SimulateGyroMouseButton("left_click", (int)WindowsInput.Events.ButtonCode.Left,
                                    gyroMouseActionsEnabled);
            SimulateGyroMouseButton("right_click", (int)WindowsInput.Events.ButtonCode.Right,
                                    gyroMouseActionsEnabled);
            SimulateGyroMouseButton("center_click", (int)WindowsInput.Events.ButtonCode.Middle,
                                    gyroMouseActionsEnabled);
            SimulateGyroMouseScroll("scroll_up", true, gyroMouseActionsEnabled);
            SimulateGyroMouseScroll("scroll_down", false, gyroMouseActionsEnabled);
        }

        // Gyro-mouse movement is calculated once per IMU sub-sample from ReceiveRaw rather than
        // once per report. gyr_g reflects whichever sub-sample ExtractIMUValues just parsed
        // immediately before this is called; the two must always be called as a pair. Each
        // bundled sub-sample is a fixed ~5ms apart internally (matching MadgwickAHRS's own
        // SamplePeriod and the Timestamp += 5000 bookkeeping already in ReceiveRaw's loop), so
        // this uses that fixed period rather than report-level wall-clock time.
        //
        // Sub-pixel remainder left over from ProcessGyroMouseSample's int truncation, carried
        // into the next sample instead of discarded - three sub-samples a report, each covering
        // only ~5ms, means a slow/deliberate rotation's per-sample delta is very often under 1.0
        // in magnitude on its own. Truncating that straight to 0 every time would zero out slow,
        // precise movement entirely and only respond to fast motion - accumulating the remainder
        // means that same slow rotation still adds up to real movement, just spread over a few
        // more samples rather than lost.
        protected float pendingMouseDx, pendingMouseDy;

        // Canonical JoyShockLibrary/GamepadMotionHelpers Y-up sensor frame used only by gyro
        // mouse. BetterJoy's public gyr_g/acc_g frame is retained untouched for UDP, gyro-stick,
        // analog sliders and compatibility. This frame deliberately does not change when a
        // Joy-Con is joined or split: gyro-mouse orientation follows the physical sensor, while
        // the legacy solo transform below exists for controller-layout compatibility.
        protected Vector3 gyroMouseSensorRate;
        protected Vector3 gyroMouseSensorAccel;

        // Constant roll correction captured by Re-Centre Gyro. Rows describe the neutral X/Y
        // axes in the canonical sensor frame; Z is the controller's pointing/roll axis and is
        // unchanged. Identity preserves the normal Pro/paired/sideways defaults until recentered.
        protected Vector2 gyroMouseNeutralX = new Vector2(1.0f, 0.0f);
        protected Vector2 gyroMouseNeutralY = new Vector2(0.0f, 1.0f);

        // Smoothing removes high-frequency noise but deliberately preserves DC, including the
        // small temperature/unit-specific zero-rate bias that shows up as a steady cursor crawl
        // while a Joycon is sitting untouched. Learn that bias only from a sustained stillness
        // window. Accelerometer magnitude is used strictly as a confidence gate (near 1g means
        // no obvious linear acceleration); accelerometer direction never enters cursor motion.
        protected const int GyroMouseBiasWindowSamples = 100; // 0.5s at 200 Hz
        protected const float GyroMouseInitialStillRateLimit = 2.0f; // degrees/sec per axis
        protected const float GyroMouseLearnedStillRateLimit = 1.25f;
        protected const float GyroMouseStillRangeLimit = 1.0f;
        protected const float GyroMouseStillAccelTolerance = 0.15f;
        protected Vector3 gyroMouseBias;
        protected bool gyroMouseBiasInitialized;
        protected Vector3 gyroMouseBiasWindowSum;
        protected Vector3 gyroMouseBiasWindowMin;
        protected Vector3 gyroMouseBiasWindowMax;
        protected int gyroMouseBiasWindowCount;

        // Gyro auto-calibration: watches for genuine stillness and, once seen for long enough,
        // silently runs the same calibration CalibrationState.FinishCalibration already performs
        // for the manual wizard - just triggered by a background stillness check instead of a
        // human clicking through a dialog. Persists to the same on-disk data, so unlike the
        // gyro-mouse bias learning above (session-only, mouse-specific), this is a much
        // higher-stakes, deliberately much stricter check - see TryAutoCalibrate.
        protected bool AutoCalibrationEnabled = Boolean.Parse(ConfigurationManager.AppSettings["AutoCalibrationEnabled"]);
        // Fixed, not scaled by anything - see AutoCalTrendFraction below for why this no longer
        // needs to vary with how far off the reading looks.
        protected float AutoCalStillDurationSeconds = float.Parse(ConfigurationManager.AppSettings["AutoCalStillDurationSeconds"]);
        // No absolute or relative-to-magnitude tolerance here on purpose - a prior version tried
        // exactly that (an absolute deg/s or g floor, later widened by a percentage of the
        // reading's own size) and it was never going to work: the whole reason a controller needs
        // auto-calibration is that its bias is unknown in advance, so no physical-unit number
        // (fixed or scaled) can be picked that's simultaneously loose enough to admit a badly
        // miscalibrated controller's real noise floor and tight enough to reject real motion.
        // Real per-axis sensor noise measured on an actual uncalibrated Joy-Con at rest
        // (gyro_mouse_debug.log) was already comparable to or bigger than every threshold tried.
        //
        // Sidestep needing a physical-unit number at all: split the window in half by time and
        // compare the first-half average to the second-half average, judged against the window's
        // OWN observed spread (max-min), not an external constant. A sensor bias - however large -
        // sits on a fixed value; both halves land in the same place regardless of what that place
        // is. Real motion (or a human trying to hold something steady) doesn't - the second half
        // measurably drifts from the first, no matter how small or large the underlying numbers
        // are. This fraction is the one remaining tunable, and it's dimensionless: "the two
        // halves must agree to within this fraction of the window's own noise," not tied to
        // deg/s, g, or any other physical unit, so the same value should hold regardless of how
        // badly any given controller is miscalibrated.
        protected float AutoCalTrendFraction = float.Parse(ConfigurationManager.AppSettings["AutoCalTrendFraction"]);
        protected int AutoCalArmDelaySeconds = Int32.Parse(ConfigurationManager.AppSettings["AutoCalArmDelaySeconds"]);
        // The on-screen debug console (DebugType=IMU) requires the app/controller to be watched
        // live and is easy to miss entirely - a real log file is what actually worked for
        // diagnosing the gyro range-limit tuning earlier, so auto-cal gets the same treatment as
        // GyroStickDebugLogging: every state transition also lands in autocal_debug.log under
        // AppPaths.DataDir, independent of whether anyone's looking at the console when it happens.
        protected bool AutoCalDebugLogging = Boolean.Parse(ConfigurationManager.AppSettings["AutoCalDebugLogging"]);
        // Buttons don't drift the way sensors do, so "nothing pressed in this long" is treated
        // as an OVERRIDE, not just a corroborating signal (see TryAutoCalibrate) - once
        // satisfied, it lets calibration through even if the accel/gyro readings themselves look
        // like they're drifting, since that drift is exactly what a bad/absent calibration
        // produces. Deliberately long (minutes, not seconds): this is the "nothing has been
        // touched for a very long time, just trust it" fallback, not the primary/fast path.
        protected float AutoCalButtonInactivitySeconds = float.Parse(ConfigurationManager.AppSettings["AutoCalButtonInactivitySeconds"]);
        // True once a successful auto-calibration has published for this physical connection -
        // never attempted again until the next reconnect (a fresh Joycon instance), so a
        // well-calibrated controller doesn't keep re-writing its calibration every time it sits
        // idle for a while.
        protected bool autoCalCompleted = false;
        // True only while THIS instance currently holds the CalibrationState claim.
        protected bool autoCalWindowOpen = false;
        protected long autoCalWindowStartTimestamp;
        protected readonly long autoCalConnectTimestamp = Stopwatch.GetTimestamp();
        // Per-axis throughout, not magnitude - a constant magnitude with a slowly changing
        // direction (e.g. a smooth hand-driven arc) would pass a magnitude-only check but is real
        // motion. Min/max give the window's own noise spread (the yardstick the trend comparison
        // below is judged against); the two half-window sum/count pairs give the first-half vs
        // second-half means the trend itself is measured from. See AutoCalTrendFraction.
        protected Vector3 autoCalGyroWindowMin, autoCalGyroWindowMax;
        protected Vector3 autoCalGyroFirstHalfSum, autoCalGyroSecondHalfSum;
        protected int autoCalGyroFirstHalfCount, autoCalGyroSecondHalfCount;
        protected Vector3 autoCalAccelWindowMin, autoCalAccelWindowMax;
        protected Vector3 autoCalAccelFirstHalfSum, autoCalAccelSecondHalfSum;
        protected int autoCalAccelFirstHalfCount, autoCalAccelSecondHalfCount;

        // Optional, separately toggleable: rides the exact same stillness window as gyro auto-
        // calibration above, but only ever replaces a stick's CENTER, never its range - a
        // stillness-only pass can never produce genuine max/min range data (that needs the user
        // actually rotating the stick to its physical edges), so the range half of whatever's
        // currently active (factory SPI data, or an earlier manual/auto calibration) is always
        // kept as-is. Needs no shared/global sample buffers the way gyro's does: raw stick
        // position is already a private instance field (stick_precal/stick2_precal), so these
        // just accumulate directly with no cross-controller claim/race concerns at all.
        protected bool AutoCalibrateStickCenter = Boolean.Parse(ConfigurationManager.AppSettings["AutoCalibrateStickCenter"]);
        protected readonly List<int> autoCalStickCenterX = new List<int>();
        protected readonly List<int> autoCalStickCenterY = new List<int>();
        protected readonly List<int> autoCalStick2CenterX = new List<int>();
        protected readonly List<int> autoCalStick2CenterY = new List<int>();

        // Raw mode retains the gyro-only quaternion mapper for A/B comparison. Filtered mode uses
        // the proven Player Space approach from GamepadMotionHelpers: gravity influences which
        // gyro axis means horizontal, but only gyro rate can produce movement.
        // internal, not protected: GyroMouseOrientation/GyroMousePlayerSpace are themselves
        // internal sealed classes (single-assembly helpers) - a protected field could in
        // principle expose an internal type across an assembly boundary via inheritance, which
        // C# disallows (CS0052). internal is still fully accessible to Joycon in this assembly.
        internal readonly GyroMouseOrientation gyroMouseOrientation = new GyroMouseOrientation();
        internal readonly GyroMousePlayerSpace gyroMousePlayerSpace = new GyroMousePlayerSpace();

        // Gyro-stick shares the exact gravity tracker and world-space rate mapper proven by
        // gyro-mouse, but keeps independent state so enabling one feature cannot perturb the
        // other. Fusion determines the gravity-relative axes; only gyro rate creates output.
        internal readonly GyroMousePlayerSpace gyroStickPlayerSpace = new GyroMousePlayerSpace();
        // Independent per stick, not shared: GyroStickAxisXLeft/Right can differ, so each side
        // must accumulate its own X source (see ProcessGyroStickSample).
        protected float pendingGyroStickDxLeft, pendingGyroStickDyLeft;
        protected float pendingGyroStickDxRight, pendingGyroStickDyRight;
        // Gravity-referenced roll (always zero at physical level, independent of RecenterGyro) -
        // captured from gyroStickPlayerSpace.Map()'s previously-discarded rollRadians output, for
        // use as the stick-X source when GyroStickAxisX == "roll" in Absolute/Hybrid mode. Rate
        // mode's roll source is a separate raw rate (stickGyroRate.Z), not this angle.
        protected float gyroStickLatestWorldRoll;
        protected bool gyroLeftStickActiveThisReport;
        protected bool gyroRightStickActiveThisReport;

        // Ratchet gyro (see ratchet_gyro in App.config): updated once per report in
        // DoThingsWithButtons, then consumed by both the raw and filtered gyro-stick paths. Gyro
        // output is a per-report rate, not an accumulated position - a stick held at a constant
        // nonzero deflection reads to the game as "keep turning at this rate," so freezing output
        // at its last live (likely nonzero, mid-turn) value would keep turning through the whole
        // hold instead of stopping. Ratcheting therefore zeroes output instead, matching a real
        // ratchet wrench: disengaging it stops applying new rotation while you reposition your
        // grip, it doesn't keep spinning the bolt on its own.
        protected bool gyroStickRatcheted = false;
        protected float gyroStickReportDt;

        // Smooth mapped 2D motion, not the raw 3D sensor. The filtered state is blended back
        // toward the live rate as speed rises, preserving fine-motion stability without making
        // fast turns feel delayed.
        protected Vector2 filteredGyroMouseRate;
        protected bool filteredGyroMouseRateInitialized;

        // A solo Joycon is held sideways and ExtractIMUValues rotates its gyro axes; a joined
        // Joycon uses the pair/vertical basis instead. Keeping an orientation integrated in the
        // old basis after other changes would mix two coordinate systems and make gyro-mouse or
        // another filtered gyro feature jump/bend badly after join/split. This snapshot is read
        // and updated only by the controller's poll thread.
        protected Joycon gyroMouseOrientationPartner;

        // Gyro-stick evidence capture. This records the applied path beside the legacy-frame raw
        // rate candidate and all three calibrated sensor samples. Nintendo reports bundle three
        // 5ms IMU samples; keeping them together makes timing loss, axis leakage, acceleration
        // contamination and source ownership distinguishable in one capture. The Euler/AHRS
        // columns remain diagnostic comparators and no longer drive filtered stick displacement.
        protected static readonly ConcurrentQueue<string> gyroStickDiagQueue =
            new ConcurrentQueue<string>();
        protected static int gyroStickDiagWriterStarted;
        protected static int gyroStickDiagHeaderWritten;
        protected const float ImuSamplePeriodSeconds = 0.005f;

        protected long gyroStickDiagReportSequence;
        protected long gyroStickDiagLastArrivalTimestamp;
        protected bool gyroStickDiagHasDeviceTimer;
        protected byte gyroStickDiagLastDeviceTimer;
        protected int gyroStickDiagSampleCount;
        protected Vector3 gyroStickDiagLegacyGyroSum;
        protected Vector3 gyroStickDiagLegacyAccelSum;
        protected Vector3 gyroStickDiagSensorGyroSum;
        protected Vector3 gyroStickDiagSensorAccelSum;
        protected Vector3 gyroStickDiagFirstLegacyGyro;
        protected Vector3 gyroStickDiagSecondLegacyGyro;
        protected Vector3 gyroStickDiagThirdLegacyGyro;
        protected Vector3 gyroStickDiagFirstLegacyAccel;
        protected float gyroStickDiagDt;
        protected bool gyroStickDiagActive;
        protected string gyroStickDiagTarget = "none";
        protected float gyroStickDiagPhysicalX, gyroStickDiagPhysicalY;
        protected float gyroStickDiagAppliedDx, gyroStickDiagAppliedDy;
        protected float gyroStickDiagOutputX, gyroStickDiagOutputY;
        protected float gyroStickDiagPitch, gyroStickDiagYaw, gyroStickDiagRoll;
        protected float gyroStickDiagPitchDelta, gyroStickDiagYawDelta, gyroStickDiagRollDelta;

        protected bool IsGyroStickConfigured() {
            return MappingValue("active_gyro_left_stick") != "0" ||
                   MappingValue("active_gyro_right_stick") != "0";
        }

        protected string GyroStickDiagnosticTarget() {
            if (gyroLeftStickActiveThisReport && gyroRightStickActiveThisReport)
                return "joy_both";
            if (gyroLeftStickActiveThisReport)
                return "joy_left";
            if (gyroRightStickActiveThisReport)
                return "joy_right";
            return "none";
        }

        protected void BeginGyroStickDiagnosticReport() {
            if (!GyroStickDebugLogging || !IsGyroStickConfigured())
                return;

            gyroStickDiagSampleCount = 0;
            gyroStickDiagLegacyGyroSum = Vector3.Zero;
            gyroStickDiagLegacyAccelSum = Vector3.Zero;
            gyroStickDiagSensorGyroSum = Vector3.Zero;
            gyroStickDiagSensorAccelSum = Vector3.Zero;
            gyroStickDiagFirstLegacyGyro = Vector3.Zero;
            gyroStickDiagSecondLegacyGyro = Vector3.Zero;
            gyroStickDiagThirdLegacyGyro = Vector3.Zero;
            gyroStickDiagFirstLegacyAccel = Vector3.Zero;
            gyroStickDiagDt = 0.0f;
            gyroStickDiagActive = false;
            gyroStickDiagTarget = "none";
            gyroStickDiagPhysicalX = gyroStickDiagPhysicalY = 0.0f;
            gyroStickDiagAppliedDx = gyroStickDiagAppliedDy = 0.0f;
            gyroStickDiagOutputX = gyroStickDiagOutputY = 0.0f;
            gyroStickDiagPitch = gyroStickDiagYaw = gyroStickDiagRoll = 0.0f;
            gyroStickDiagPitchDelta = gyroStickDiagYawDelta = gyroStickDiagRollDelta = 0.0f;
        }

        protected void AccumulateGyroStickDiagnosticSample() {
            if (!GyroStickDebugLogging || !IsGyroStickConfigured())
                return;

            if (gyroStickDiagSampleCount == 0) {
                gyroStickDiagFirstLegacyGyro = gyr_g;
                gyroStickDiagFirstLegacyAccel = acc_g;
            } else if (gyroStickDiagSampleCount == 1) {
                gyroStickDiagSecondLegacyGyro = gyr_g;
            } else if (gyroStickDiagSampleCount == 2) {
                gyroStickDiagThirdLegacyGyro = gyr_g;
            }
            gyroStickDiagLegacyGyroSum += gyr_g;
            gyroStickDiagLegacyAccelSum += acc_g;
            gyroStickDiagSensorGyroSum += gyroMouseSensorRate;
            gyroStickDiagSensorAccelSum += gyroMouseSensorAccel;
            gyroStickDiagSampleCount++;
        }

        protected void CaptureGyroStickDiagnosticOutput(bool gyroEnabled, float dt,
                                                        float physicalX, float physicalY,
                                                        float dx, float dy,
                                                        float outputX, float outputY) {
            if (!GyroStickDebugLogging || !IsGyroStickConfigured())
                return;

            gyroStickDiagActive = gyroEnabled;
            gyroStickDiagTarget = GyroStickDiagnosticTarget();
            gyroStickDiagDt = dt;
            gyroStickDiagPhysicalX = physicalX;
            gyroStickDiagPhysicalY = physicalY;
            gyroStickDiagAppliedDx = dx;
            gyroStickDiagAppliedDy = dy;
            gyroStickDiagOutputX = outputX;
            gyroStickDiagOutputY = outputY;
            if (cur_rotation != null && cur_rotation.Length >= 6) {
                gyroStickDiagPitch = cur_rotation[0];
                gyroStickDiagYaw = cur_rotation[1];
                gyroStickDiagRoll = cur_rotation[2];
                gyroStickDiagPitchDelta = cur_rotation[0] - cur_rotation[3];
                gyroStickDiagYawDelta = cur_rotation[1] - cur_rotation[4];
                gyroStickDiagRollDelta = cur_rotation[2] - cur_rotation[5];
            }
        }

        protected static string GyroStickCsv(float value) {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        protected static string GyroStickCsv(double value) {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        protected static void EnsureGyroStickDiagWriterStarted() {
            if (Interlocked.CompareExchange(ref gyroStickDiagWriterStarted, 1, 0) != 0)
                return;
            new Thread(GyroStickDiagWriterLoop) {
                IsBackground = true,
                Name = "GyroStickDiagLogWriter"
            }.Start();
        }

        protected static void GyroStickDiagWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "gyro_stick_debug.csv");
            const string header =
                "utc,report,source,serial,pad_id,virtual_sequence,submit,target,active,filtered,beta," +
                "timer,timer_delta,arrival_ms,legacy_dt_ms,sensitivity_x,sensitivity_y,reduction," +
                "physical_x,physical_y,applied_dx,applied_dy,output_x,output_y," +
                "euler_pitch_deg,euler_yaw_deg,euler_roll_deg,euler_dp_deg,euler_dy_deg,euler_dr_deg," +
                "sample0_gx_dps,sample0_gy_dps,sample0_gz_dps," +
                "sample1_gx_dps,sample1_gy_dps,sample1_gz_dps," +
                "sample2_gx_dps,sample2_gy_dps,sample2_gz_dps," +
                "avg_gx_dps,avg_gy_dps,avg_gz_dps,integrated_pitch_deg,integrated_yaw_deg," +
                "rate_candidate_dx,rate_candidate_dy," +
                "avg_ax_g,avg_ay_g,avg_az_g,avg_accel_mag_g," +
                "sensor_gx_dps,sensor_gy_dps,sensor_gz_dps," +
                "sensor_ax_g,sensor_ay_g,sensor_az_g,sensor_accel_mag_g,q0,q1,q2,q3\r\n";

            while (true) {
                Thread.Sleep(500);
                if (gyroStickDiagQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                if (Interlocked.CompareExchange(ref gyroStickDiagHeaderWritten, 1, 0) == 0) {
                    try {
                        if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
                            batch.Append(header);
                    } catch {
                        batch.Append(header);
                    }
                }
                while (gyroStickDiagQueue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch {
                    // Diagnostic only: never let an unavailable log path affect controller I/O.
                }
            }
        }

        protected void RecordGyroStickDiagnosticReport(byte deviceTimer, long arrivalTimestamp) {
            if (!GyroStickDebugLogging || !IsGyroStickConfigured() ||
                gyroStickDiagSampleCount == 0)
                return;

            // Match the feature's effective activation gate: diagnostics may be enabled in the
            // config for an entire test session, but inactive gyro must not create log rows. Also
            // discard timing continuity across the inactive gap so the first report after a
            // reactivation does not claim that the whole off period was one delayed packet.
            if (!gyroStickDiagActive) {
                gyroStickDiagLastArrivalTimestamp = 0;
                gyroStickDiagHasDeviceTimer = false;
                return;
            }

            EnsureGyroStickDiagWriterStarted();

            double arrivalMs = gyroStickDiagLastArrivalTimestamp == 0 ? 0.0 :
                (arrivalTimestamp - gyroStickDiagLastArrivalTimestamp) * 1000.0 /
                Stopwatch.Frequency;
            gyroStickDiagLastArrivalTimestamp = arrivalTimestamp;

            int timerDelta = gyroStickDiagHasDeviceTimer
                ? (byte)(deviceTimer - gyroStickDiagLastDeviceTimer) : 0;
            gyroStickDiagLastDeviceTimer = deviceTimer;
            gyroStickDiagHasDeviceTimer = true;

            float inverseSamples = 1.0f / gyroStickDiagSampleCount;
            Vector3 averageGyro = gyroStickDiagLegacyGyroSum * inverseSamples;
            Vector3 averageAccel = gyroStickDiagLegacyAccelSum * inverseSamples;
            Vector3 averageSensorGyro = gyroStickDiagSensorGyroSum * inverseSamples;
            Vector3 averageSensorAccel = gyroStickDiagSensorAccelSum * inverseSamples;
            float radiansToDegrees = 57.2957795f;
            float degreesToRadians = 0.0174532925f;
            float integratedPitch = gyroStickDiagLegacyGyroSum.Y * ImuSamplePeriodSeconds;
            float integratedYaw = gyroStickDiagLegacyGyroSum.Z * ImuSamplePeriodSeconds;
            float rateCandidateDx = GyroStickSensitivityX * integratedYaw * degreesToRadians;
            float rateCandidateDy = -GyroStickSensitivityY * integratedPitch * degreesToRadians;
            float[] quaternion = AHRS.Quaternion;
            string submit = out_xbox != null ? "xbox" : (out_ds4 != null ? "ds4" : "none");

            string[] fields = {
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                (++gyroStickDiagReportSequence).ToString(CultureInfo.InvariantCulture),
                GyroMouseDiagnosticSource(),
                serial_number == null ? String.Empty : serial_number.Replace(',', '_'),
                PadId.ToString(CultureInfo.InvariantCulture),
                virtualControllerSequence.ToString(CultureInfo.InvariantCulture),
                submit,
                gyroStickDiagTarget,
                gyroStickDiagActive ? "1" : "0",
                UseFilteredIMU ? "1" : "0",
                GyroStickCsv(AHRS.Beta),
                deviceTimer.ToString(CultureInfo.InvariantCulture),
                timerDelta.ToString(CultureInfo.InvariantCulture),
                GyroStickCsv(arrivalMs),
                GyroStickCsv(gyroStickDiagDt * 1000.0f),
                GyroStickCsv(GyroStickSensitivityX),
                GyroStickCsv(GyroStickSensitivityY),
                GyroStickCsv(GyroStickReduction),
                GyroStickCsv(gyroStickDiagPhysicalX),
                GyroStickCsv(gyroStickDiagPhysicalY),
                GyroStickCsv(gyroStickDiagAppliedDx),
                GyroStickCsv(gyroStickDiagAppliedDy),
                GyroStickCsv(gyroStickDiagOutputX),
                GyroStickCsv(gyroStickDiagOutputY),
                GyroStickCsv(gyroStickDiagPitch * radiansToDegrees),
                GyroStickCsv(gyroStickDiagYaw * radiansToDegrees),
                GyroStickCsv(gyroStickDiagRoll * radiansToDegrees),
                GyroStickCsv(gyroStickDiagPitchDelta * radiansToDegrees),
                GyroStickCsv(gyroStickDiagYawDelta * radiansToDegrees),
                GyroStickCsv(gyroStickDiagRollDelta * radiansToDegrees),
                GyroStickCsv(gyroStickDiagFirstLegacyGyro.X),
                GyroStickCsv(gyroStickDiagFirstLegacyGyro.Y),
                GyroStickCsv(gyroStickDiagFirstLegacyGyro.Z),
                GyroStickCsv(gyroStickDiagSecondLegacyGyro.X),
                GyroStickCsv(gyroStickDiagSecondLegacyGyro.Y),
                GyroStickCsv(gyroStickDiagSecondLegacyGyro.Z),
                GyroStickCsv(gyroStickDiagThirdLegacyGyro.X),
                GyroStickCsv(gyroStickDiagThirdLegacyGyro.Y),
                GyroStickCsv(gyroStickDiagThirdLegacyGyro.Z),
                GyroStickCsv(averageGyro.X),
                GyroStickCsv(averageGyro.Y),
                GyroStickCsv(averageGyro.Z),
                GyroStickCsv(integratedPitch),
                GyroStickCsv(integratedYaw),
                GyroStickCsv(rateCandidateDx),
                GyroStickCsv(rateCandidateDy),
                GyroStickCsv(averageAccel.X),
                GyroStickCsv(averageAccel.Y),
                GyroStickCsv(averageAccel.Z),
                GyroStickCsv(averageAccel.Length()),
                GyroStickCsv(averageSensorGyro.X),
                GyroStickCsv(averageSensorGyro.Y),
                GyroStickCsv(averageSensorGyro.Z),
                GyroStickCsv(averageSensorAccel.X),
                GyroStickCsv(averageSensorAccel.Y),
                GyroStickCsv(averageSensorAccel.Z),
                GyroStickCsv(averageSensorAccel.Length()),
                GyroStickCsv(quaternion[0]),
                GyroStickCsv(quaternion[1]),
                GyroStickCsv(quaternion[2]),
                GyroStickCsv(quaternion[3])
            };
            gyroStickDiagQueue.Enqueue(string.Join(",", fields) + "\r\n");
        }

        // TEMPORARY diagnostic instrumentation for the figure-eight/circle drift investigation
        // (see CODE_REVIEW.md). Everything below is scoped to the CURRENT interval only (reset
        // after every write) rather than a lifetime running average - a lifetime average is too
        // smoothed out to see what's actually happening moment to moment. Interval length matches
        // the report/write cadence: short enough (150ms) to see shape within a single loop, long
        // enough that the file stays readable. Remove once the investigation concludes.
        //
        // Review-flagged fix: File.AppendAllText used to be called directly from here, i.e. on a
        // Joycon's own poll thread, roughly every 150ms - synchronous file I/O on the exact path
        // whose timing this investigation cares about, capable of distorting the jank/burstiness
        // being measured. Formatted lines are now only ever enqueued (cheap, no I/O) here; a
        // dedicated background thread (DiagLogWriterLoop) drains the queue and does the actual
        // write, off this path entirely.
        protected const double DiagLogIntervalSeconds = 0.15;
        protected static readonly ConcurrentQueue<string> diagLogQueue = new ConcurrentQueue<string>();
        protected static int diagLogWriterStarted;

        protected long diagIntervalDx, diagIntervalDy, diagIntervalSampleCount;
        protected long diagIntervalPositiveCount, diagIntervalNegativeCount;
        protected double diagIntervalSumGyrGY, diagIntervalSumRawGyrY;
        protected float diagIntervalMinGyrGY = float.MaxValue, diagIntervalMaxGyrGY = float.MinValue;
        protected long diagLastLogTimestamp;

        // Raw yaw/roll rates (gyr_g.Z/X - gyr_g.Y=pitch is tracked above), the quaternion-derived
        // orientation roll, and the orientation-mapped yaw/pitch rates that actually reach
        // sensitivity scaling - side by side so mapping behavior is directly visible.
        protected double diagIntervalSumGyrGZ, diagIntervalSumGyrGX, diagIntervalSumRollDeg;
        protected double diagIntervalSumYawRate, diagIntervalSumPitchRate;
        protected float diagIntervalMinGyrGZ = float.MaxValue, diagIntervalMaxGyrGZ = float.MinValue;
        protected float diagIntervalMinGyrGX = float.MaxValue, diagIntervalMaxGyrGX = float.MinValue;
        protected float diagIntervalMinRollDeg = float.MaxValue, diagIntervalMaxRollDeg = float.MinValue;

        // Timing evidence for the Joy-Con-only jagged-pointer investigation. HID arrival is
        // captured immediately after hid_read_timeout returns a report; pointer request timing
        // is captured immediately before BetterJoy hands a non-zero delta to its host. Keeping
        // both on this controller instance identifies whether unevenness already exists at the
        // Bluetooth/HID boundary or first appears later in BetterJoy's output path. Stopwatch and
        // arithmetic only here; the existing background writer remains the sole file-I/O owner.
        protected long diagLastReportArrivalTimestamp, diagLastPointerRequestTimestamp;
        protected bool diagHasLastDeviceTimer;
        protected byte diagLastDeviceTimer;
        protected long diagIntervalReportDeltaCount, diagIntervalPointerRequestDeltaCount;
        protected double diagIntervalReportDeltaSumMs, diagIntervalPointerRequestDeltaSumMs;
        protected double diagIntervalReportDeltaMinMs = double.MaxValue;
        protected double diagIntervalReportDeltaMaxMs = double.MinValue;
        protected double diagIntervalPointerRequestDeltaMinMs = double.MaxValue;
        protected double diagIntervalPointerRequestDeltaMaxMs = double.MinValue;
        protected long diagIntervalDeviceTimerDeltaCount, diagIntervalUnexpectedDeviceTimerDeltas;
        protected long diagIntervalDeviceTimerDeltaSum;
        protected int diagIntervalDeviceTimerDeltaMin = int.MaxValue;
        protected int diagIntervalDeviceTimerDeltaMax = int.MinValue;
        protected long diagPreviousHidCallEndTimestamp;
        protected long diagPendingHidWaitTicks, diagPendingOutsideHidTicks;
        protected long diagIntervalHidPhaseCount;
        protected double diagIntervalHidWaitSumMs, diagIntervalOutsideHidSumMs;
        protected double diagIntervalHidWaitMinMs = double.MaxValue;
        protected double diagIntervalHidWaitMaxMs = double.MinValue;
        protected double diagIntervalOutsideHidMinMs = double.MaxValue;
        protected double diagIntervalOutsideHidMaxMs = double.MinValue;

        // Auto-detected "controller genuinely at rest" periods, marked in the log so a stationary
        // window doesn't have to be manually timestamped and reported separately - can't just
        // threshold gyr_g.Y's raw magnitude (a biased reading won't sit near zero even at true
        // rest, that's the whole bug), so this tracks how much gyr_g.Y VARIES over a running
        // streak instead: a genuinely still controller holds a narrow band (whatever its bias
        // happens to be), real wrist motion breaks out of a narrow band almost immediately.
        protected const float StillnessSpreadThresholdDegPerSec = 3.0f;
        protected const double StillnessMinDurationSeconds = 10.0;
        protected float stillStreakMinGyrGY = float.MaxValue, stillStreakMaxGyrGY = float.MinValue;
        protected long stillStreakStartTimestamp;
        protected bool stillStreakMarked;

        // Started lazily on first use rather than from a constructor - matches how the rest of
        // this diagnostic code only activates once GyroMouseDebugLogging/actual gyro-mouse use
        // requires it, instead of running for every Joycon regardless of whether it's ever used.
        protected static void EnsureDiagLogWriterStarted() {
            if (Interlocked.CompareExchange(ref diagLogWriterStarted, 1, 0) != 0)
                return;
            new Thread(DiagLogWriterLoop) { IsBackground = true, Name = "GyroMouseDiagLogWriter" }.Start();
        }

        protected static void DiagLogWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "gyro_mouse_debug.log");
            while (true) {
                Thread.Sleep(500);
                if (diagLogQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (diagLogQueue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch {
                    // diagnostic only - never let logging itself take down gyro-mouse
                }
            }
        }

        protected void RecordGyroMouseHidCall(int result, byte deviceTimer,
                                            long callStart, long callEnd) {
            if (!GyroMouseDebugLogging)
                return;

            // A report may require several 5ms timeout calls. Accumulate every native wait and
            // every interval outside the native call until one succeeds; looking only at the
            // final call would mislabel preceding timeouts as BetterJoy processing time.
            if (diagPreviousHidCallEndTimestamp != 0)
                diagPendingOutsideHidTicks += callStart - diagPreviousHidCallEndTimestamp;
            diagPendingHidWaitTicks += callEnd - callStart;
            diagPreviousHidCallEndTimestamp = callEnd;

            if (result <= 0)
                return;

            if (IsGyroMouseActive()) {
                double hidWaitMs = diagPendingHidWaitTicks * 1000.0 / Stopwatch.Frequency;
                double outsideHidMs = diagPendingOutsideHidTicks * 1000.0 / Stopwatch.Frequency;
                RecordGyroMouseReportTiming(deviceTimer, callEnd, hidWaitMs, outsideHidMs);
            }

            diagPendingHidWaitTicks = 0;
            diagPendingOutsideHidTicks = 0;
        }

        protected void RecordGyroMouseReportTiming(byte deviceTimer, long now,
                                                  double hidWaitMs, double outsideHidMs) {
            if (!GyroMouseDebugLogging || !IsGyroMouseActive())
                return;

            if (diagLastReportArrivalTimestamp != 0) {
                double deltaMs = (now - diagLastReportArrivalTimestamp) * 1000.0 /
                                 Stopwatch.Frequency;
                diagIntervalReportDeltaSumMs += deltaMs;
                diagIntervalReportDeltaCount++;
                if (deltaMs < diagIntervalReportDeltaMinMs) diagIntervalReportDeltaMinMs = deltaMs;
                if (deltaMs > diagIntervalReportDeltaMaxMs) diagIntervalReportDeltaMaxMs = deltaMs;
            }
            diagLastReportArrivalTimestamp = now;

            if (diagHasLastDeviceTimer) {
                // Byte subtraction intentionally wraps modulo 256. BetterJoy already expects a
                // normal full-IMU report to advance this timer by three bundled samples.
                int timerDelta = (byte)(deviceTimer - diagLastDeviceTimer);
                diagIntervalDeviceTimerDeltaSum += timerDelta;
                diagIntervalDeviceTimerDeltaCount++;
                if (timerDelta < diagIntervalDeviceTimerDeltaMin) diagIntervalDeviceTimerDeltaMin = timerDelta;
                if (timerDelta > diagIntervalDeviceTimerDeltaMax) diagIntervalDeviceTimerDeltaMax = timerDelta;
                if (timerDelta != 3) diagIntervalUnexpectedDeviceTimerDeltas++;
            }
            diagLastDeviceTimer = deviceTimer;
            diagHasLastDeviceTimer = true;

            diagIntervalHidWaitSumMs += hidWaitMs;
            diagIntervalOutsideHidSumMs += outsideHidMs;
            diagIntervalHidPhaseCount++;
            if (hidWaitMs < diagIntervalHidWaitMinMs) diagIntervalHidWaitMinMs = hidWaitMs;
            if (hidWaitMs > diagIntervalHidWaitMaxMs) diagIntervalHidWaitMaxMs = hidWaitMs;
            if (outsideHidMs < diagIntervalOutsideHidMinMs) diagIntervalOutsideHidMinMs = outsideHidMs;
            if (outsideHidMs > diagIntervalOutsideHidMaxMs) diagIntervalOutsideHidMaxMs = outsideHidMs;
        }

        protected void RecordGyroMousePointerRequestTiming() {
            if (!GyroMouseDebugLogging)
                return;

            long now = Stopwatch.GetTimestamp();
            if (diagLastPointerRequestTimestamp != 0) {
                double deltaMs = (now - diagLastPointerRequestTimestamp) * 1000.0 /
                                 Stopwatch.Frequency;
                diagIntervalPointerRequestDeltaSumMs += deltaMs;
                diagIntervalPointerRequestDeltaCount++;
                if (deltaMs < diagIntervalPointerRequestDeltaMinMs) diagIntervalPointerRequestDeltaMinMs = deltaMs;
                if (deltaMs > diagIntervalPointerRequestDeltaMaxMs) diagIntervalPointerRequestDeltaMaxMs = deltaMs;
            }
            diagLastPointerRequestTimestamp = now;
        }

        protected string GyroMouseDiagnosticSource() {
            string controller = !SupportsPairing ? "Pro" : (isLeft ? "JoyCon-L" : "JoyCon-R");
            string transport = isUSB ? "USB" : "BT";
            string layout = !SupportsPairing ? "single" :
                (other == null ? "solo" : (other == this ? "self" : "joined"));
            return controller + "/" + transport + "/" + layout;
        }

        protected void ResetGyroMouseTimingInterval() {
            diagIntervalReportDeltaCount = 0;
            diagIntervalReportDeltaSumMs = 0.0;
            diagIntervalReportDeltaMinMs = double.MaxValue;
            diagIntervalReportDeltaMaxMs = double.MinValue;
            diagIntervalPointerRequestDeltaCount = 0;
            diagIntervalPointerRequestDeltaSumMs = 0.0;
            diagIntervalPointerRequestDeltaMinMs = double.MaxValue;
            diagIntervalPointerRequestDeltaMaxMs = double.MinValue;
            diagIntervalDeviceTimerDeltaCount = 0;
            diagIntervalDeviceTimerDeltaSum = 0;
            diagIntervalDeviceTimerDeltaMin = int.MaxValue;
            diagIntervalDeviceTimerDeltaMax = int.MinValue;
            diagIntervalUnexpectedDeviceTimerDeltas = 0;
            diagIntervalHidPhaseCount = 0;
            diagIntervalHidWaitSumMs = 0.0;
            diagIntervalOutsideHidSumMs = 0.0;
            diagIntervalHidWaitMinMs = double.MaxValue;
            diagIntervalHidWaitMaxMs = double.MinValue;
            diagIntervalOutsideHidMinMs = double.MaxValue;
            diagIntervalOutsideHidMaxMs = double.MinValue;
        }

        protected void ResetGyroMouseTimingTracking() {
            diagLastReportArrivalTimestamp = 0;
            diagLastPointerRequestTimestamp = 0;
            diagHasLastDeviceTimer = false;
            diagPreviousHidCallEndTimestamp = 0;
            diagPendingHidWaitTicks = 0;
            diagPendingOutsideHidTicks = 0;
            ResetGyroMouseTimingInterval();
        }

        // Called for every sub-sample - accumulates this interval's stats and runs the stillness
        // streak check - and again whenever a flush actually injects movement (with the real
        // dx/dy that were sent, 0/0 otherwise). Enqueues at most once per DiagLogIntervalSeconds.
        protected void RecordGyroMouseDiagnosticSample(int dx, int dy, float rollDeg, float yawRate, float pitchRate) {
            if (!GyroMouseDebugLogging)
                return;

            EnsureDiagLogWriterStarted();

            diagIntervalDx += dx;
            diagIntervalDy += dy;
            diagIntervalSumGyrGY += gyr_g.Y;
            diagIntervalSumRawGyrY += gyr_r[1];
            diagIntervalSampleCount++;
            if (gyr_g.Y >= 0) diagIntervalPositiveCount++; else diagIntervalNegativeCount++;
            if (gyr_g.Y < diagIntervalMinGyrGY) diagIntervalMinGyrGY = gyr_g.Y;
            if (gyr_g.Y > diagIntervalMaxGyrGY) diagIntervalMaxGyrGY = gyr_g.Y;

            diagIntervalSumGyrGZ += gyr_g.Z;
            if (gyr_g.Z < diagIntervalMinGyrGZ) diagIntervalMinGyrGZ = gyr_g.Z;
            if (gyr_g.Z > diagIntervalMaxGyrGZ) diagIntervalMaxGyrGZ = gyr_g.Z;

            diagIntervalSumGyrGX += gyr_g.X;
            if (gyr_g.X < diagIntervalMinGyrGX) diagIntervalMinGyrGX = gyr_g.X;
            if (gyr_g.X > diagIntervalMaxGyrGX) diagIntervalMaxGyrGX = gyr_g.X;

            diagIntervalSumRollDeg += rollDeg;
            if (rollDeg < diagIntervalMinRollDeg) diagIntervalMinRollDeg = rollDeg;
            if (rollDeg > diagIntervalMaxRollDeg) diagIntervalMaxRollDeg = rollDeg;

            diagIntervalSumYawRate += yawRate;
            diagIntervalSumPitchRate += pitchRate;

            UpdateStillnessStreak();

            long now = Stopwatch.GetTimestamp();
            if (diagLastLogTimestamp != 0 && (now - diagLastLogTimestamp) / (double)Stopwatch.Frequency < DiagLogIntervalSeconds)
                return;
            diagLastLogTimestamp = now;

            bool allowCalibration = Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]);
            float neutralValue = allowCalibration ? activeData[1] : gyr_neutral[1];

            double reportDeltaAverage = diagIntervalReportDeltaCount > 0
                ? diagIntervalReportDeltaSumMs / diagIntervalReportDeltaCount : 0.0;
            double reportDeltaMinimum = diagIntervalReportDeltaCount > 0
                ? diagIntervalReportDeltaMinMs : 0.0;
            double reportDeltaMaximum = diagIntervalReportDeltaCount > 0
                ? diagIntervalReportDeltaMaxMs : 0.0;
            double pointerDeltaAverage = diagIntervalPointerRequestDeltaCount > 0
                ? diagIntervalPointerRequestDeltaSumMs / diagIntervalPointerRequestDeltaCount : 0.0;
            double pointerDeltaMinimum = diagIntervalPointerRequestDeltaCount > 0
                ? diagIntervalPointerRequestDeltaMinMs : 0.0;
            double pointerDeltaMaximum = diagIntervalPointerRequestDeltaCount > 0
                ? diagIntervalPointerRequestDeltaMaxMs : 0.0;
            double deviceTimerDeltaAverage = diagIntervalDeviceTimerDeltaCount > 0
                ? diagIntervalDeviceTimerDeltaSum / (double)diagIntervalDeviceTimerDeltaCount : 0.0;
            int deviceTimerDeltaMinimum = diagIntervalDeviceTimerDeltaCount > 0
                ? diagIntervalDeviceTimerDeltaMin : 0;
            int deviceTimerDeltaMaximum = diagIntervalDeviceTimerDeltaCount > 0
                ? diagIntervalDeviceTimerDeltaMax : 0;
            double hidWaitAverage = diagIntervalHidPhaseCount > 0
                ? diagIntervalHidWaitSumMs / diagIntervalHidPhaseCount : 0.0;
            double hidWaitMinimum = diagIntervalHidPhaseCount > 0
                ? diagIntervalHidWaitMinMs : 0.0;
            double hidWaitMaximum = diagIntervalHidPhaseCount > 0
                ? diagIntervalHidWaitMaxMs : 0.0;
            double outsideHidAverage = diagIntervalHidPhaseCount > 0
                ? diagIntervalOutsideHidSumMs / diagIntervalHidPhaseCount : 0.0;
            double outsideHidMinimum = diagIntervalHidPhaseCount > 0
                ? diagIntervalOutsideHidMinMs : 0.0;
            double outsideHidMaximum = diagIntervalHidPhaseCount > 0
                ? diagIntervalOutsideHidMaxMs : 0.0;

            string line = string.Format(
                "{0:HH:mm:ss.fff}  Y(pitch,raw): avg={1,7:F3} min={2,7:F3} max={3,7:F3} pos={4,4} neg={5,4}  |  Z(yaw,raw): avg={6,7:F3} min={7,7:F3} max={8,7:F3}  |  X(roll rate): avg={9,7:F3} min={10,7:F3} max={11,7:F3}  |  Roll angle(quat): avg={12,7:F2} min={13,7:F2} max={14,7:F2}deg  |  mapped: yaw avg={15,7:F3} pitch avg={16,7:F3}  |  raw gyr_r[1] avg={17,8:F1} neutral({18})={19,8:F1}  |  interval dx={20,5} dy={21,5}  samples={22,4}",
                DateTime.Now,
                diagIntervalSumGyrGY / diagIntervalSampleCount, diagIntervalMinGyrGY, diagIntervalMaxGyrGY,
                diagIntervalPositiveCount, diagIntervalNegativeCount,
                diagIntervalSumGyrGZ / diagIntervalSampleCount, diagIntervalMinGyrGZ, diagIntervalMaxGyrGZ,
                diagIntervalSumGyrGX / diagIntervalSampleCount, diagIntervalMinGyrGX, diagIntervalMaxGyrGX,
                diagIntervalSumRollDeg / diagIntervalSampleCount, diagIntervalMinRollDeg, diagIntervalMaxRollDeg,
                diagIntervalSumYawRate / diagIntervalSampleCount, diagIntervalSumPitchRate / diagIntervalSampleCount,
                diagIntervalSumRawGyrY / diagIntervalSampleCount,
                allowCalibration ? "activeData[1]" : "gyr_neutral[1]", neutralValue,
                diagIntervalDx, diagIntervalDy, diagIntervalSampleCount);
            line += string.Format(
                "  |  gravity trust={0,5:F3} yaw-dom={1,5:F3} error={2,6:F2}deg even-leak={3,7:F4} corr={4,7:F3}  |  timing[{5}]: HID ms avg={6,6:F2} min={7,6:F2} max={8,6:F2} n={9,3}; timer d avg={10,5:F2} min={11,3} max={12,3} unexpected={13,3}; phase ms/report HID-wait avg={14,6:F2} min={15,6:F2} max={16,6:F2}, outside-HID avg={17,6:F2} min={18,6:F2} max={19,6:F2} n={20,3}; pointer-request ms avg={21,6:F2} min={22,6:F2} max={23,6:F2} n={24,3}\r\n",
                gyroMousePlayerSpace.GravityCorrectionTrust,
                gyroMousePlayerSpace.YawDominance,
                gyroMousePlayerSpace.GravityErrorDegrees,
                gyroMousePlayerSpace.EvenYawLeakRatio,
                gyroMousePlayerSpace.EvenYawLeakCorrection,
                GyroMouseDiagnosticSource(),
                reportDeltaAverage, reportDeltaMinimum, reportDeltaMaximum,
                diagIntervalReportDeltaCount,
                deviceTimerDeltaAverage, deviceTimerDeltaMinimum, deviceTimerDeltaMaximum,
                diagIntervalUnexpectedDeviceTimerDeltas,
                hidWaitAverage, hidWaitMinimum, hidWaitMaximum,
                outsideHidAverage, outsideHidMinimum, outsideHidMaximum,
                diagIntervalHidPhaseCount,
                pointerDeltaAverage, pointerDeltaMinimum, pointerDeltaMaximum,
                diagIntervalPointerRequestDeltaCount);
            diagLogQueue.Enqueue(line);

            diagIntervalDx = 0; diagIntervalDy = 0; diagIntervalSampleCount = 0;
            diagIntervalPositiveCount = 0; diagIntervalNegativeCount = 0;
            diagIntervalSumGyrGY = 0; diagIntervalSumRawGyrY = 0;
            diagIntervalMinGyrGY = float.MaxValue; diagIntervalMaxGyrGY = float.MinValue;
            diagIntervalSumGyrGZ = 0; diagIntervalSumGyrGX = 0; diagIntervalSumRollDeg = 0;
            diagIntervalSumYawRate = 0; diagIntervalSumPitchRate = 0;
            diagIntervalMinGyrGZ = float.MaxValue; diagIntervalMaxGyrGZ = float.MinValue;
            diagIntervalMinGyrGX = float.MaxValue; diagIntervalMaxGyrGX = float.MinValue;
            diagIntervalMinRollDeg = float.MaxValue; diagIntervalMaxRollDeg = float.MinValue;
            ResetGyroMouseTimingInterval();
        }

        protected void UpdateStillnessStreak() {
            float candidateMin = Math.Min(stillStreakMinGyrGY, gyr_g.Y);
            float candidateMax = Math.Max(stillStreakMaxGyrGY, gyr_g.Y);

            if (candidateMax - candidateMin > StillnessSpreadThresholdDegPerSec) {
                if (stillStreakMarked)
                    LogGyroMouseDiagnosticMarker("STATIONARY END");

                stillStreakMinGyrGY = gyr_g.Y;
                stillStreakMaxGyrGY = gyr_g.Y;
                stillStreakStartTimestamp = Stopwatch.GetTimestamp();
                stillStreakMarked = false;
                return;
            }

            stillStreakMinGyrGY = candidateMin;
            stillStreakMaxGyrGY = candidateMax;

            if (!stillStreakMarked) {
                double elapsed = (Stopwatch.GetTimestamp() - stillStreakStartTimestamp) / (double)Stopwatch.Frequency;
                if (elapsed >= StillnessMinDurationSeconds) {
                    LogGyroMouseDiagnosticMarker(string.Format("STATIONARY START (held within {0}deg/s band for {1:F1}s so far)", StillnessSpreadThresholdDegPerSec, elapsed));
                    stillStreakMarked = true;
                }
            }
        }

        // Marks a single instant in the log - reset_mouse actually firing, or an auto-detected
        // stillness streak starting/ending - so the regular interval lines above can be lined up
        // against it. TEMPORARY, same as RecordGyroMouseDiagnosticSample.
        protected void LogGyroMouseDiagnosticMarker(string label) {
            if (!GyroMouseDebugLogging)
                return;

            EnsureDiagLogWriterStarted();
            diagLogQueue.Enqueue(string.Format("{0:HH:mm:ss.fff}  *** {1} ***\r\n", DateTime.Now, label));
        }

        protected void ResetGyroMouseMotionState(bool resetPlayerSpace = false) {
            pendingMouseDx = pendingMouseDy = 0.0f;
            gyroMouseOrientation.Reset();
            if (resetPlayerSpace)
                gyroMousePlayerSpace.Reset();
            filteredGyroMouseRate = Vector2.Zero;
            filteredGyroMouseRateInitialized = false;
        }

        protected void ResetGyroMouseBiasWindow() {
            gyroMouseBiasWindowSum = Vector3.Zero;
            gyroMouseBiasWindowMin = Vector3.Zero;
            gyroMouseBiasWindowMax = Vector3.Zero;
            gyroMouseBiasWindowCount = 0;
        }

        protected void ResetGyroMouseBiasEstimator() {
            gyroMouseBias = Vector3.Zero;
            gyroMouseBiasInitialized = false;
            ResetGyroMouseBiasWindow();
        }

        protected static float MaxAbsComponent(Vector3 value) {
            return Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        }

        protected static Vector3 AbsVector3(Vector3 value) {
            return new Vector3(Math.Abs(value.X), Math.Abs(value.Y), Math.Abs(value.Z));
        }

        // Returns gyro rate with the learned stationary zero-rate offset removed. Before the
        // first stable 0.5s window completes, a sample that is itself a stillness candidate is
        // suppressed rather than allowed to crawl the cursor; deliberate motion immediately
        // breaks the window and passes through normally.
        protected Vector3 ApplyGyroMouseStationaryBias(Vector3 rawRate, bool allowBiasLearning) {
            Vector3 residual = rawRate - gyroMouseBias;

            // A constant slow yaw is indistinguishable from bias using only gyro and gravity:
            // rotation around gravity does not change the accelerometer direction. Never let an
            // already-calibrated pointer learn while active, or deliberate slow movement in one
            // direction becomes the new zero and makes that direction feel like it is fighting
            // the user. Initial lock is still allowed so always-on gyro can remove startup drift;
            // subsequent temperature adjustment happens while the activation bind is released.
            if (gyroMouseBiasInitialized && !allowBiasLearning) {
                ResetGyroMouseBiasWindow();
                return residual;
            }

            float stillRateLimit = gyroMouseBiasInitialized
                ? GyroMouseLearnedStillRateLimit
                : GyroMouseInitialStillRateLimit;
            float accelMagnitude = gyroMouseSensorAccel.Length();
            bool stillCandidate = Math.Abs(accelMagnitude - 1.0f) <= GyroMouseStillAccelTolerance &&
                                  MaxAbsComponent(residual) <= stillRateLimit;

            if (!stillCandidate) {
                ResetGyroMouseBiasWindow();
                return residual;
            }

            if (gyroMouseBiasWindowCount == 0) {
                gyroMouseBiasWindowMin = rawRate;
                gyroMouseBiasWindowMax = rawRate;
            } else {
                gyroMouseBiasWindowMin = Vector3.Min(gyroMouseBiasWindowMin, rawRate);
                gyroMouseBiasWindowMax = Vector3.Max(gyroMouseBiasWindowMax, rawRate);
            }

            gyroMouseBiasWindowSum += rawRate;
            gyroMouseBiasWindowCount++;

            if (gyroMouseBiasWindowCount >= GyroMouseBiasWindowSamples) {
                Vector3 range = gyroMouseBiasWindowMax - gyroMouseBiasWindowMin;
                if (MaxAbsComponent(range) <= GyroMouseStillRangeLimit) {
                    Vector3 measuredBias = gyroMouseBiasWindowSum / gyroMouseBiasWindowCount;
                    bool firstBiasLock = !gyroMouseBiasInitialized;
                    gyroMouseBias = gyroMouseBiasInitialized
                        ? gyroMouseBias + 0.2f * (measuredBias - gyroMouseBias)
                        : measuredBias;
                    gyroMouseBiasInitialized = true;
                    residual = rawRate - gyroMouseBias;
                    if (firstBiasLock) {
                        LogGyroMouseDiagnosticMarker(string.Format(
                            "GYRO BIAS LOCK x={0:F3} y={1:F3} z={2:F3} deg/s",
                            gyroMouseBias.X, gyroMouseBias.Y, gyroMouseBias.Z));
                    }
                }
                ResetGyroMouseBiasWindow();
            }

            return gyroMouseBiasInitialized ? residual : Vector3.Zero;
        }

        // Watches for the controller sitting genuinely motionless for AutoCalStillDurationSeconds
        // and, if so, silently claims the shared CalibrationState session and publishes a fresh
        // calibration - the same FinishCalibration/getActiveData pipeline the manual wizard uses,
        // just triggered by this background check instead of a human running the dialog. Called
        // once per report from DoThingsWithButtons - no sub-sample-rate responsiveness is needed
        // for a multi-second window, and this deliberately stays out of ExtractIMUValues (the
        // exact function tonight's yaw-sign calibration bug lived in).
        //
        // Much stricter than the gyro-mouse bias learning above on purpose: that's a fast,
        // session-only nudge; this is a permanent disk write. It also can't reuse that learner's
        // approach directly - ApplyGyroMouseStationaryBias checks the reading against a fixed
        // still-rate limit, which assumes the bias is already small. Auto-cal exists specifically
        // for controllers where that's not true, so it detects the bias pattern itself (see the
        // first-half-vs-second-half trend check below) instead of checking against any expected
        // physical value.

        protected static readonly ConcurrentQueue<string> autoCalDiagQueue = new ConcurrentQueue<string>();
        protected static int autoCalDiagWriterStarted;

        // Always mirrors to the on-screen debug console (unconditionally - callers no longer call
        // DebugPrint directly); additionally queues to autocal_debug.log when AutoCalDebugLogging
        // is on, via the same async background-writer pattern as the gyro-stick CSV diagnostics,
        // so this never risks blocking a controller's own Poll thread on file I/O.
        protected void AutoCalLog(string message) {
            DebugPrint(message, DebugType.IMU);
            if (!AutoCalDebugLogging)
                return;

            EnsureAutoCalDiagWriterStarted();
            autoCalDiagQueue.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, serial_number, message));
        }

        protected static void EnsureAutoCalDiagWriterStarted() {
            if (Interlocked.CompareExchange(ref autoCalDiagWriterStarted, 1, 0) != 0)
                return;
            new Thread(AutoCalDiagWriterLoop) {
                IsBackground = true,
                Name = "AutoCalDiagLogWriter"
            }.Start();
        }

        protected static void AutoCalDiagWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "autocal_debug.log");
            while (true) {
                Thread.Sleep(500);
                if (autoCalDiagQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (autoCalDiagQueue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch {
                    // Diagnostic only: never let an unavailable log path affect controller I/O.
                }
            }
        }

        protected void TryAutoCalibrate() {
            // DualSense's baseline support never populates gyroMouseSensorRate/Accel (no gyro
            // parsing yet) - they stay at a constant Vector3.Zero forever, which would trivially
            // pass the stillness/trend check every single time (an unchanging zero reading looks
            // like perfect stillness) and publish bogus all-zero calibration data almost
            // immediately after every connect, while also contending for the shared
            // CalibrationState claim with any other controller's own legitimate auto-cal window.
            if (!HasGyro)
                return;
            if (!AutoCalibrationEnabled || autoCalCompleted)
                return;
            // Mirrors the live (not cached) AllowCalibration check ExtractIMUValues already uses
            // to gate AddSample - auto-cal piggybacks on that same call site, so it's equally
            // inert whenever calibration sampling itself is turned off, and reacts the same way
            // if the user flips it mid-session.
            if (!Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]))
                return;

            long now = Stopwatch.GetTimestamp();
            double sinceConnectSeconds = (now - autoCalConnectTimestamp) / (double)Stopwatch.Frequency;
            if (sinceConnectSeconds < AutoCalArmDelaySeconds)
                return;

            double sinceLastButtonSeconds = (now - inactivity) / (double)Stopwatch.Frequency;
            // Buttons don't drift the way sensors do, so a long enough stretch with literally
            // nothing pressed is, on its own, stronger proof of genuine stillness than the
            // accel/gyro constancy check below - a human cannot go this long without touching
            // anything if they're actually holding/using the controller. Once true, this
            // OVERRIDES the constancy check entirely rather than just adding to it - it exists
            // specifically to let calibration through when the controller's own drift is bad
            // enough to otherwise keep failing that check (the exact case auto-cal is for).
            bool buttonInactiveOverride = sinceLastButtonSeconds >= AutoCalButtonInactivitySeconds;

            if (!autoCalWindowOpen) {
                // No instantaneous "does this already look calibrated" pre-filter, on either
                // sensor - detection happens entirely from the completed window's own shape (see
                // below), not from how the reading compares to any external expectation. Opening
                // unconditionally (once claimable) means a window can briefly flicker open during
                // genuinely active use, but real motion fails the trend check once the window
                // completes, so it costs nothing but a claim/release cycle.
                if (!CalibrationState.TryClaim(this))
                    return;

                AutoCalLog("Auto-calibration: stillness window opened.");
                autoCalWindowOpen = true;
                autoCalWindowStartTimestamp = now;
                autoCalGyroWindowMin = autoCalGyroWindowMax = gyroMouseSensorRate;
                autoCalAccelWindowMin = autoCalAccelWindowMax = gyroMouseSensorAccel;
                autoCalGyroFirstHalfSum = autoCalGyroSecondHalfSum = Vector3.Zero;
                autoCalGyroFirstHalfCount = autoCalGyroSecondHalfCount = 0;
                autoCalAccelFirstHalfSum = autoCalAccelSecondHalfSum = Vector3.Zero;
                autoCalAccelFirstHalfCount = autoCalAccelSecondHalfCount = 0;
                ClearAutoCalStickSamples();
                return;
            }

            // Lost the claim to a manual calibration elsewhere (see CalibrationState.ForceClaim) -
            // abort silently, publish nothing under this controller's serial.
            if (!CalibrationState.IsClaimedBy(this)) {
                AutoCalLog("Auto-calibration: lost claim to another calibration, aborting window.");
                autoCalWindowOpen = false;
                ClearAutoCalStickSamples();
                return;
            }

            Vector3 gyroRate = gyroMouseSensorRate;
            Vector3 accel = gyroMouseSensorAccel;
            autoCalGyroWindowMin = Vector3.Min(autoCalGyroWindowMin, gyroRate);
            autoCalGyroWindowMax = Vector3.Max(autoCalGyroWindowMax, gyroRate);
            autoCalAccelWindowMin = Vector3.Min(autoCalAccelWindowMin, accel);
            autoCalAccelWindowMax = Vector3.Max(autoCalAccelWindowMax, accel);

            // Split the window in half by time so it can be judged for a TREND at the end,
            // instead of checking against any external, physical-unit threshold (see
            // AutoCalTrendFraction).
            double elapsedSeconds = (now - autoCalWindowStartTimestamp) / (double)Stopwatch.Frequency;
            if (elapsedSeconds < AutoCalStillDurationSeconds / 2.0) {
                autoCalGyroFirstHalfSum += gyroRate;
                autoCalGyroFirstHalfCount++;
                autoCalAccelFirstHalfSum += accel;
                autoCalAccelFirstHalfCount++;
            } else {
                autoCalGyroSecondHalfSum += gyroRate;
                autoCalGyroSecondHalfCount++;
                autoCalAccelSecondHalfSum += accel;
                autoCalAccelSecondHalfCount++;
            }

            // Rides the same window as the gyro/accel tracking above - no separate stillness
            // gate, no separate claim (see the field comment on autoCalStickCenterX).
            if (AutoCalibrateStickCenter) {
                autoCalStickCenterX.Add(stick_precal[0]);
                autoCalStickCenterY.Add(stick_precal[1]);
                if (HasDualSticks) {
                    autoCalStick2CenterX.Add(stick2_precal[0]);
                    autoCalStick2CenterY.Add(stick2_precal[1]);
                }
            }

            if (elapsedSeconds < AutoCalStillDurationSeconds)
                return;

            // Neither signal is checked against where it "should" read at rest (near-zero gyro
            // rate, 1g of accel), and neither is checked against any fixed or magnitude-scaled
            // physical-unit tolerance - a bad or absent calibration can leave either one reading
            // an arbitrarily large constant offset (this is exactly what the accZ sign bug found
            // earlier this session looked like), and there is no way to know that offset's size
            // in advance, so no such number could ever be picked correctly. Instead, detect the
            // actual signature of a sensor bias directly: it sits on a fixed value, so the
            // window's first half and second half land in the same place, whatever that place
            // is. Real motion - or a human trying to hold something artificially steady - can't
            // sustain that; the second half measurably drifts from the first. The drift is judged
            // against the window's OWN observed spread (max-min), not an external constant, so
            // the same fraction applies regardless of how large the underlying bias turns out to
            // be. See AutoCalTrendFraction.
            bool passesTrendCheck = buttonInactiveOverride;
            Vector3 gyroDrift = Vector3.Zero, gyroSpread = Vector3.Zero;
            Vector3 accelDrift = Vector3.Zero, accelSpread = Vector3.Zero;
            if (!buttonInactiveOverride) {
                if (autoCalGyroFirstHalfCount == 0 || autoCalGyroSecondHalfCount == 0 ||
                    autoCalAccelFirstHalfCount == 0 || autoCalAccelSecondHalfCount == 0) {
                    // Not enough samples in one half to judge a trend at all - shouldn't happen
                    // at normal poll rates over a multi-second window, but fail closed rather
                    // than publish off too little data.
                    passesTrendCheck = false;
                } else {
                    Vector3 gyroFirstMean = autoCalGyroFirstHalfSum / autoCalGyroFirstHalfCount;
                    Vector3 gyroSecondMean = autoCalGyroSecondHalfSum / autoCalGyroSecondHalfCount;
                    gyroDrift = AbsVector3(gyroSecondMean - gyroFirstMean);
                    gyroSpread = autoCalGyroWindowMax - autoCalGyroWindowMin;

                    Vector3 accelFirstMean = autoCalAccelFirstHalfSum / autoCalAccelFirstHalfCount;
                    Vector3 accelSecondMean = autoCalAccelSecondHalfSum / autoCalAccelSecondHalfCount;
                    accelDrift = AbsVector3(accelSecondMean - accelFirstMean);
                    accelSpread = autoCalAccelWindowMax - autoCalAccelWindowMin;

                    passesTrendCheck =
                        gyroDrift.X <= gyroSpread.X * AutoCalTrendFraction &&
                        gyroDrift.Y <= gyroSpread.Y * AutoCalTrendFraction &&
                        gyroDrift.Z <= gyroSpread.Z * AutoCalTrendFraction &&
                        accelDrift.X <= accelSpread.X * AutoCalTrendFraction &&
                        accelDrift.Y <= accelSpread.Y * AutoCalTrendFraction &&
                        accelDrift.Z <= accelSpread.Z * AutoCalTrendFraction;
                }
            }

            if (!passesTrendCheck) {
                AutoCalLog(string.Format(CultureInfo.InvariantCulture,
                    "Auto-calibration: second half drifted from first half, aborting. " +
                    "gyroDrift x={0:F3} y={1:F3} z={2:F3} gyroSpread x={3:F3} y={4:F3} z={5:F3} " +
                    "accelDrift x={6:F4} y={7:F4} z={8:F4} accelSpread x={9:F4} y={10:F4} z={11:F4} fraction={12:F2}",
                    gyroDrift.X, gyroDrift.Y, gyroDrift.Z, gyroSpread.X, gyroSpread.Y, gyroSpread.Z,
                    accelDrift.X, accelDrift.Y, accelDrift.Z, accelSpread.X, accelSpread.Y, accelSpread.Z,
                    AutoCalTrendFraction));
                CalibrationState.Release(this);
                autoCalWindowOpen = false;
                ClearAutoCalStickSamples();
                return;
            }

            // Re-verify immediately before publishing - belt-and-suspenders alongside
            // FinishCalibration's own internal ownership check (see CalibrationState.TryClaim).
            if (!CalibrationState.IsClaimedBy(this)) {
                AutoCalLog("Auto-calibration: lost claim just before publish, aborting.");
                autoCalWindowOpen = false;
                ClearAutoCalStickSamples();
                return;
            }

            CalibrationState.FinishCalibration(this);
            getActiveData();
            if (AutoCalibrateStickCenter)
                PublishAutoCalStickCenter();
            autoCalWindowOpen = false;
            autoCalCompleted = true;
            AutoCalLog("Auto-calibration: complete, new calibration published.");
        }

        protected void ClearAutoCalStickSamples() {
            autoCalStickCenterX.Clear();
            autoCalStickCenterY.Clear();
            autoCalStick2CenterX.Clear();
            autoCalStick2CenterY.Clear();
        }

        // Only ever replaces a stick's center - see the field comment on autoCalStickCenterX for
        // why range is deliberately untouched. Median, not average, matching the manual wizard's
        // own center-phase computation (CalibrationState.ComputeStickCal) - robust against a
        // stray outlier reading rather than letting one skew the result.
        protected static int Median(List<int> values) {
            List<int> sorted = new List<int>(values);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        protected void PublishAutoCalStickCenter() {
            if (autoCalStickCenterX.Count > 0) {
                CalibrationState.PublishStickCenter(serial_number, false, stick_cal,
                    Median(autoCalStickCenterX), Median(autoCalStickCenterY));
                getActiveStickData();
            }
            if (HasDualSticks && autoCalStick2CenterX.Count > 0) {
                CalibrationState.PublishStickCenter(serial_number, true, stick2_cal,
                    Median(autoCalStick2CenterX), Median(autoCalStick2CenterY));
                getActiveStickData();
            }
            ClearAutoCalStickSamples();
        }

        // Makes the pose held at the moment the Re-Centre Gyro bind is pressed the new neutral
        // orientation. This intentionally does not touch activeData/gyr_neutral/acc calibration:
        // recentering is a coordinate-frame change, while calibration estimates sensor offsets.
        protected void RecenterGyro() {
            CaptureGyroMouseNeutralFrame();

            // Throw away the old gravity frame as well as pending mouse motion. The next IMU
            // sub-sample seeds gravity in the newly captured grip frame. Bias is intentionally
            // retained in the underlying sensor frame: orientation and calibration are separate.
            ResetGyroMouseMotionState(true);
            ResetGyroMouseBiasWindow();
            AHRS.Recenter();
            cur_rotation = AHRS.GetEulerAngles();

            // Deliberately NOT resetting lastDoThingsTimestamp here: DoThingsWithButtons already
            // refreshed it to nowTimestamp (a genuinely fresh, valid baseline) earlier in this
            // same call, before this method ever runs, and separately forces this report's own
            // dt to 0.0f right after calling this. Resetting it to -1 here would only poison the
            // *next* report's dt into the "no prior packet" fallback instead of the real elapsed
            // time - a one-report timing glitch that leaked into GyroAnalogSliders whenever it
            // shares a controller with gyro-mouse.
        }

        // A solo Joycon and a joined Joycon transform the same physical IMU into different
        // coordinate bases in ExtractIMUValues. Never carry either orientation estimator across
        // that boundary. Kept on the Poll thread so it cannot race AHRS.Update/MapSample.
        protected void EnsureGyroOrientationBasis() {
            Joycon currentPartner = other;
            if (Object.ReferenceEquals(currentPartner, gyroMouseOrientationPartner))
                return;

            gyroMouseNeutralX = new Vector2(1.0f, 0.0f);
            gyroMouseNeutralY = new Vector2(0.0f, 1.0f);
            ResetGyroMouseMotionState(true);
            ResetGyroStickMotionState(true);
            ResetGyroMouseBiasEstimator();
            AHRS.Reset();
            gyroMouseOrientationPartner = currentPartner;
        }

        protected void MoveGyroMouseBy(int dx, int dy) {
            if (!GyroMouseDirectCursor) {
                form.SimulateMoveBy(dx, dy);
            } else if (GyroMouseScreenWrap) {
                form.SimulateWrappedCursorMoveBy(dx, dy);
            } else {
                form.SimulateCursorMoveBy(dx, dy);
            }
        }

        protected void UpdateCanonicalGyroMouseImu() {
            // BetterJoy parses Nintendo packet axes as X=raw Z, Y=raw X, Z=raw Y and applies
            // controller-side signs. This proper rotation converts that established frame to the
            // same Y-up convention JoyShockLibrary feeds into GamepadMotionHelpers. Do not apply
            // BetterJoy's solo sideways-layout transform here: doing so rotates a solo Joy-Con's
            // physical pitch axis into Player Space's yaw/roll plane, suppressing vertical and
            // diagonal pointer motion. Joined, self-paired and solo use the same sensor frame.
            gyroMouseSensorAccel = new Vector3(-acc_g.Y, acc_g.Z, -acc_g.X);
            gyroMouseSensorRate = new Vector3(gyr_g.Y, -gyr_g.Z, -gyr_g.X);
        }

        protected Vector3 TransformGyroMouseToNeutralFrame(Vector3 value) {
            return new Vector3(
                gyroMouseNeutralX.X * value.X + gyroMouseNeutralX.Y * value.Y,
                gyroMouseNeutralY.X * value.X + gyroMouseNeutralY.Y * value.Y,
                value.Z);
        }

        protected void CaptureGyroMouseNeutralFrame() {
            float accelLength = gyroMouseSensorAccel.Length();
            if (accelLength <= 0.0f)
                return;

            Vector3 down = -gyroMouseSensorAccel / accelLength;
            float projectedLength = (float)Math.Sqrt(down.X * down.X + down.Y * down.Y);
            if (projectedLength <= 0.1f)
                return; // pointing almost vertically: grip roll is undefined

            float inverseLength = 1.0f / projectedLength;
            float downX = down.X * inverseLength;
            float downY = down.Y * inverseLength;

            // Express future samples in axes where the current projected gravity is (0,-1).
            // That makes the user's present grip define local pitch/up-down without altering the
            // forward Z axis or allowing acceleration itself to create mouse displacement.
            gyroMouseNeutralX = new Vector2(-downY, downX);
            gyroMouseNeutralY = new Vector2(-downX, -downY);
        }

        protected void SmoothGyroMouseRates(ref float yawRate, ref float pitchRate,
                                          float samplePeriod) {
            Vector2 current = new Vector2(yawRate, pitchRate);
            float directionRelease = 0.0f;
            if (GyroMouseSmoothingTimeMs <= 0 || GyroMouseSmoothingThreshold <= 0.0f) {
                filteredGyroMouseRate = current;
                filteredGyroMouseRateInitialized = true;
                return;
            }

            if (!filteredGyroMouseRateInitialized) {
                filteredGyroMouseRate = current;
                filteredGyroMouseRateInitialized = true;
            } else {
                Vector2 previousFiltered = filteredGyroMouseRate;
                float timeConstant = GyroMouseSmoothingTimeMs / 1000.0f;
                float alpha = 1.0f - (float)Math.Exp(-samplePeriod / timeConstant);
                filteredGyroMouseRate += alpha * (current - filteredGyroMouseRate);

                // Speed alone cannot tell a deliberate corner from ordinary low-speed jitter.
                // Release the filter as the live pointer vector turns away from its history so
                // a slow square does not retain motion from the preceding side and bow outward.
                // Comparing against the pre-update value measures the actual direction change;
                // the epsilon guard leaves true stops to decay normally without normalizing zero.
                const float directionEpsilon = 1e-6f;
                if (current.LengthSquared() > directionEpsilon &&
                    previousFiltered.LengthSquared() > directionEpsilon) {
                    float alignment = Vector2.Dot(Vector2.Normalize(current),
                                                  Vector2.Normalize(previousFiltered));
                    alignment = Math.Max(-1.0f, Math.Min(1.0f, alignment));
                    // Combine this with the ordinary speed release below without changing the
                    // persistent filter state until the final blend is known.
                    directionRelease = Math.Max(0.0f, Math.Min(1.0f,
                        (1.0f - alignment) * 2.0f));
                }
            }

            float speed = current.Length();
            // The GyroMouseSmoothingThreshold <= 0.0f case already returned at the top of this
            // method, so lowerThreshold (half of a strictly positive threshold) is always
            // strictly less than GyroMouseSmoothingThreshold here - no divide-by-zero to guard.
            float lowerThreshold = GyroMouseSmoothingThreshold * 0.5f;
            float unsmoothedFactor = Math.Max(0.0f, Math.Min(1.0f,
                (speed - lowerThreshold) / (GyroMouseSmoothingThreshold - lowerThreshold)));

            // Smoothstep avoids a perceptible gain corner as the filter releases. Once fully
            // released, follow the live rate so old slow-motion history cannot create a tail
            // when the user stops after a quick sweep.
            unsmoothedFactor = unsmoothedFactor * unsmoothedFactor *
                               (3.0f - 2.0f * unsmoothedFactor);
            unsmoothedFactor = Math.Max(unsmoothedFactor, directionRelease);
            Vector2 result = Vector2.Lerp(filteredGyroMouseRate, current, unsmoothedFactor);
            if (unsmoothedFactor >= 1.0f)
                filteredGyroMouseRate = current;
            yawRate = result.X;
            pitchRate = result.Y;
        }

        protected void ResetGyroStickMotionState(bool resetPlayerSpace = false) {
            pendingGyroStickDxLeft = pendingGyroStickDyLeft = 0.0f;
            pendingGyroStickDxRight = pendingGyroStickDyRight = 0.0f;
            gyroLeftStickActiveThisReport = false;
            gyroRightStickActiveThisReport = false;
            if (resetPlayerSpace)
                gyroStickPlayerSpace.Reset();
        }

        protected float EffectiveGyroStickReduction() {
            // Reduction is a divisor. Treat zero/invalid values as the neutral 1x setting rather
            // than allowing centered 0/0 -> NaN and tiny physical-stick noise / 0 -> +/-Infinity,
            // which later clamps into apparently direction-sensitive full deflection.
            return GyroStickReduction > 0.0f &&
                   !float.IsNaN(GyroStickReduction) &&
                   !float.IsInfinity(GyroStickReduction)
                ? GyroStickReduction
                : 1.0f;
        }

        protected const float DegreesToRadiansGyroStick = 0.0174532925f;

        // Tilt range is a divisor (degrees of tilt -> full deflection). Same zero/invalid guard
        // as EffectiveGyroStickReduction, with a safe fallback matching this axis's App.config
        // default rather than an arbitrary constant.
        protected float EffectiveGyroStickTiltRangeX() {
            return GyroStickTiltRangeX > 0.0f &&
                   !float.IsNaN(GyroStickTiltRangeX) &&
                   !float.IsInfinity(GyroStickTiltRangeX)
                ? GyroStickTiltRangeX * DegreesToRadiansGyroStick
                : 45.0f * DegreesToRadiansGyroStick;
        }

        protected float EffectiveGyroStickTiltRangeY() {
            return GyroStickTiltRangeY > 0.0f &&
                   !float.IsNaN(GyroStickTiltRangeY) &&
                   !float.IsInfinity(GyroStickTiltRangeY)
                ? GyroStickTiltRangeY * DegreesToRadiansGyroStick
                : 35.0f * DegreesToRadiansGyroStick;
        }

        protected void ApplyGyroToStick(float[] controlStick, float dx, float dy) {
            float stickReduction = EffectiveGyroStickReduction();
            controlStick[0] = Math.Max(-1.0f, Math.Min(1.0f,
                controlStick[0] / stickReduction + dx));
            controlStick[1] = Math.Max(-1.0f, Math.Min(1.0f,
                controlStick[1] / stickReduction + dy));
        }

        // Combines this stick's own pending rate accumulation with cur_rotation (absolute
        // pitch/yaw relative to the pose RecenterGyro() last captured - see
        // MadgwickAHRS.GetEulerAngles()'s own comment; NOT cur_rotation[3..5], which is only last
        // report's [0..2], a frame-to-frame rate approximation, not a baseline) per that stick's
        // own GyroStickMode/AxisX/Invert settings - Mode, Axis, and Invert are all independent
        // per stick, so left and right can differ. Only called while gyro-stick output is
        // actually active and unratcheted - callers zero dx/dy themselves otherwise.
        protected void ComputeFilteredGyroStickOutput(bool isLeftStick, float pendingDx, float pendingDy,
                                                     out float dx, out float dy) {
            string mode = isLeftStick ? GyroStickModeLeft : GyroStickModeRight;
            string axisX = isLeftStick ? GyroStickAxisXLeft : GyroStickAxisXRight;
            if (mode == "absolute" || mode == "hybrid") {
                float absoluteX = axisX == "roll" ? gyroStickLatestWorldRoll : cur_rotation[1];
                float absoluteY = cur_rotation[0];
                dx = Math.Max(-1.0f, Math.Min(1.0f, absoluteX / EffectiveGyroStickTiltRangeX()));
                dy = Math.Max(-1.0f, Math.Min(1.0f, absoluteY / EffectiveGyroStickTiltRangeY()));
                if (mode == "hybrid") {
                    dx += pendingDx * GyroStickHybridRateWeight;
                    dy += pendingDy * GyroStickHybridRateWeight;
                }
            } else {
                dx = pendingDx;
                dy = pendingDy;
            }

            bool invertX = isLeftStick ? GyroStickInvertXLeft : GyroStickInvertXRight;
            bool invertY = isLeftStick ? GyroStickInvertYLeft : GyroStickInvertYRight;
            if (invertX)
                dx = -dx;
            if (invertY)
                dy = -dy;
        }

        // Filtered gyro-stick consumes the same canonical IMU frame and gravity-relative rate
        // mapper as filtered gyro-mouse. Nintendo supplies three samples per report, so integrate
        // all three at their fixed 5 ms cadence and apply the result once at the report boundary.
        // Crucially, accelerometer data updates only the gravity frame: with zero gyro rate these
        // accumulators remain zero regardless of translation or AHRS correction.
        protected void ProcessGyroStickSample(bool flushToStick) {
            if (!IsGyroStickConfigured() || !UseFilteredIMU) {
                if (flushToStick)
                    ResetGyroStickMotionState();
                return;
            }

            const float subSamplePeriod = 0.005f;
            const float degreesToRadians = 0.0174532925f;
            Vector3 stickGyroRate = gyroMouseSensorRate;
            Vector3 stickAccel = gyroMouseSensorAccel;

            // Keep gravity current while the activation control is released so reactivation has
            // no stale-frame correction. Update cannot create output by itself.
            gyroStickPlayerSpace.Update(stickGyroRate, stickAccel, subSamplePeriod);

            bool anyStickActive = gyroLeftStickActiveThisReport ||
                                  gyroRightStickActiveThisReport;
            // While ratcheted, stop integrating live rotation into the pending delta - output is
            // zeroed below regardless - so releasing the ratchet bind resumes from the live angle
            // rather than replaying whatever the wrist did while ratcheted.
            if (anyStickActive && !gyroStickRatcheted) {
                float yawRate;
                float pitchRate;
                float rollRadians;
                gyroStickPlayerSpace.Map(stickGyroRate, subSamplePeriod, out yawRate,
                                         out pitchRate, out rollRadians);
                gyroStickLatestWorldRoll = rollRadians;

                // Rate mode with roll selected as the X source uses the raw local roll rate
                // directly - unlike yaw/pitch, roll needs no gravity reference to be a
                // well-defined rotation rate, and Map() only ever reports rollRadians as an
                // absolute angle (see ComputeFilteredGyroStickOutput), not a rate. Default axis
                // is "yaw", so this is unchanged (xRate == yawRate) unless a profile opts in.
                // Accumulated independently per stick since left/right axis choice can differ.
                if (gyroLeftStickActiveThisReport) {
                    float xRate = GyroStickAxisXLeft == "roll" ? stickGyroRate.Z : yawRate;
                    pendingGyroStickDxLeft += GyroStickSensitivityX * xRate *
                                              subSamplePeriod * degreesToRadians;
                    // The canonical Player Space pitch axis is opposite BetterJoy's virtual-stick
                    // Y convention. Positive mapped pitch therefore adds to stick Y here; the
                    // previous subtraction made raising/lowering aim feel inverted.
                    pendingGyroStickDyLeft += GyroStickSensitivityY * pitchRate *
                                              subSamplePeriod * degreesToRadians;
                }
                if (gyroRightStickActiveThisReport) {
                    float xRate = GyroStickAxisXRight == "roll" ? stickGyroRate.Z : yawRate;
                    pendingGyroStickDxRight += GyroStickSensitivityX * xRate *
                                               subSamplePeriod * degreesToRadians;
                    pendingGyroStickDyRight += GyroStickSensitivityY * pitchRate *
                                               subSamplePeriod * degreesToRadians;
                }
            }

            if (!flushToStick)
                return;

            float[] diagnosticStick = gyroLeftStickActiveThisReport ? stick : stick2;
            float physicalX = diagnosticStick[0];
            float physicalY = diagnosticStick[1];
            float leftDx = 0.0f, leftDy = 0.0f, rightDx = 0.0f, rightDy = 0.0f;
            if (gyroLeftStickActiveThisReport && !gyroStickRatcheted) {
                ComputeFilteredGyroStickOutput(true, pendingGyroStickDxLeft, pendingGyroStickDyLeft,
                                               out leftDx, out leftDy);
                leftDx = ApplyDeflectionLimits(leftDx, GyroStickMinDeflectionXLeft, GyroStickMaxDeflectionXLeft);
                leftDy = ApplyDeflectionLimits(leftDy, GyroStickMinDeflectionYLeft, GyroStickMaxDeflectionYLeft);
            }
            if (gyroRightStickActiveThisReport && !gyroStickRatcheted) {
                ComputeFilteredGyroStickOutput(false, pendingGyroStickDxRight, pendingGyroStickDyRight,
                                               out rightDx, out rightDy);
                rightDx = ApplyDeflectionLimits(rightDx, GyroStickMinDeflectionXRight, GyroStickMaxDeflectionXRight);
                rightDy = ApplyDeflectionLimits(rightDy, GyroStickMinDeflectionYRight, GyroStickMaxDeflectionYRight);
            }

            if (gyroLeftStickActiveThisReport)
                ApplyGyroToStick(stick, leftDx, leftDy);
            if (gyroRightStickActiveThisReport)
                ApplyGyroToStick(stick2, rightDx, rightDy);

            float diagnosticDx = gyroLeftStickActiveThisReport ? leftDx : rightDx;
            float diagnosticDy = gyroLeftStickActiveThisReport ? leftDy : rightDy;
            CaptureGyroStickDiagnosticOutput(anyStickActive, gyroStickReportDt,
                                              physicalX, physicalY, diagnosticDx, diagnosticDy,
                                              diagnosticStick[0], diagnosticStick[1]);
            pendingGyroStickDxLeft = pendingGyroStickDyLeft = 0.0f;
            pendingGyroStickDxRight = pendingGyroStickDyRight = 0.0f;
        }

        // flushToMouse: integrate every sub-sample (all 3, for accuracy - see the field comment
        // above), but only actually call SimulateMoveBy once per report (the last sub-sample),
        // matching the pre-fix call rate. Calling it 3x/report instead tripled the pipe-write
        // rate in service mode (HeadlessJoyconHost.SendMessage's queue, see the fix there) - under
        // sustained motion that can outrun the writer thread and start dropping the newest
        // messages, which reads as the same "constrained" symptom this was meant to fix, just
        // from a different cause, and would plausibly cycle with motion intensity (queue fills
        // during a burst, drains during a lull) rather than being constant.
        protected void ProcessGyroMouseSample(bool flushToMouse) {
            EnsureGyroOrientationBasis();

            if (!OwnsGyroMouse()) {
                ResetGyroMouseTimingTracking();
                ResetGyroMouseMotionState(true);
                return;
            }

            // Keep learning the selected controller's zero-rate bias while gyro-mouse is
            // inactive, so activating it after the controller has been resting does not begin
            // with half a second of cursor crawl.
            bool gyroPointerActive = gyroMouseEnabledThisReport;
            Vector3 mouseGyroRate = gyr_g;
            Vector3 mouseAccel = acc_g;
            if (UseFilteredIMU) {
                Vector3 calibratedSensorRate = ApplyGyroMouseStationaryBias(
                    gyroMouseSensorRate, !gyroPointerActive);
                mouseGyroRate = TransformGyroMouseToNeutralFrame(calibratedSensorRate);
                mouseAccel = TransformGyroMouseToNeutralFrame(gyroMouseSensorAccel);
            }

            const float subSamplePeriod = 0.005f;
            const float degToRad = 0.0174533f;

            // The legacy X/Y sensitivities define the established 45-degree reference gain.
            // Expose the physical range as one intuitive control while preserving that tuned
            // horizontal/vertical balance. Invalid non-positive values safely retain the
            // established default rather than producing an inverted or infinite cursor gain.
            float traversalDegrees = GyroMouseScreenTraversalDegrees;
            if (traversalDegrees <= 0.0f || float.IsNaN(traversalDegrees) ||
                float.IsInfinity(traversalDegrees))
                traversalDegrees = GyroMouseDefaultScreenTraversalDegrees;
            float traversalScale = GyroMouseDefaultScreenTraversalDegrees / traversalDegrees;
            float mouseSensitivityX = GyroMouseSensitivityX * traversalScale;
            float mouseSensitivityY = GyroMouseSensitivityY * traversalScale;

            // Keep the tilt reference current even while the activation button is released.
            // World Space fuses acceleration into the coordinate basis only; this call cannot
            // add cursor displacement.
            if (UseFilteredIMU)
                gyroMousePlayerSpace.Update(mouseGyroRate, mouseAccel, subSamplePeriod);

            if (!gyroPointerActive) {
                ResetGyroMouseTimingTracking();
                pendingMouseDx = pendingMouseDy = 0.0f;
                filteredGyroMouseRate = Vector2.Zero;
                filteredGyroMouseRateInitialized = false;
                return;
            }

            if (gyroMouseClenched) {
                // Raw roll-compensation integrates a complete orientation and reports deltas
                // from its previous sample. Keep consuming samples while clenched but discard
                // those deltas; freezing this estimator would turn all repositioning into one
                // large catch-up jump on release. Player Space was already advanced by Update
                // above, while the direct-rate path has no integration state to maintain.
                if (!UseFilteredIMU &&
                    Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseRollCompensation"])) {
                    float ignoredYaw;
                    float ignoredPitch;
                    float ignoredRoll;
                    gyroMouseOrientation.MapSample(
                        mouseGyroRate.X, mouseGyroRate.Y, mouseGyroRate.Z, subSamplePeriod,
                        out ignoredYaw, out ignoredPitch, out ignoredRoll);
                }

                // Drop fractional movement and filter history on every clenched sample. This
                // makes the clamp immediate and prevents either pre-clench remainder or a
                // smoothing tail from leaking out after release. Do not enable stationary-bias
                // learning here: deliberate repositioning is motion, not a new sensor zero.
                pendingMouseDx = pendingMouseDy = 0.0f;
                filteredGyroMouseRate = Vector2.Zero;
                filteredGyroMouseRateInitialized = false;
                RecordGyroMouseDiagnosticSample(0, 0, 0.0f, 0.0f, 0.0f);
                return;
            }

            float yawRate = UseFilteredIMU ? mouseGyroRate.Y : mouseGyroRate.Z;
            float pitchRate = UseFilteredIMU ? mouseGyroRate.X : mouseGyroRate.Y;
            float rollRad = 0.0f;

            if (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseRollCompensation"])) {
                float deltaYawRad;
                float deltaPitchRad;
                if (UseFilteredIMU) {
                    gyroMousePlayerSpace.Map(mouseGyroRate, subSamplePeriod, out yawRate,
                                             out pitchRate, out rollRad);

                    SmoothGyroMouseRates(ref yawRate, ref pitchRate, subSamplePeriod);

                    // JoyShockLibrary's reference mouse sample calls this "tightening": below a
                    // small real-world angular speed, smoothly reduce gain instead of imposing a
                    // deadzone or adding more low-pass lag. At and above the threshold the Player
                    // Space result is unchanged.
                    // Tightening is a pointer-output rule, so base it only on the two mapped
                    // cursor axes. Using the full three-axis gyro magnitude lets otherwise
                    // unused wrist roll raise yaw/pitch gain during diagonals and figure-eights.
                    float inputSpeed = new Vector2(yawRate, pitchRate).Length();
                    if (GyroMouseTighteningThreshold > 0.0f &&
                        inputSpeed < GyroMouseTighteningThreshold) {
                        float tightening = inputSpeed / GyroMouseTighteningThreshold;
                        yawRate *= tightening;
                        pitchRate *= tightening;
                    }
                    deltaYawRad = yawRate * subSamplePeriod * degToRad;
                    deltaPitchRad = pitchRate * subSamplePeriod * degToRad;
                } else {
                    filteredGyroMouseRate = Vector2.Zero;
                    filteredGyroMouseRateInitialized = false;
                    gyroMouseOrientation.MapSample(
                        mouseGyroRate.X, mouseGyroRate.Y, mouseGyroRate.Z, subSamplePeriod,
                        out deltaYawRad, out deltaPitchRad, out rollRad);

                    // Keep diagnostics comparable to the direct-rate and Player Space paths.
                    yawRate = deltaYawRad / (subSamplePeriod * degToRad);
                    pitchRate = deltaPitchRad / (subSamplePeriod * degToRad);
                }
                pendingMouseDx += mouseSensitivityX * deltaYawRad;
                pendingMouseDy += -(mouseSensitivityY * deltaPitchRad);
            } else {
                gyroMouseOrientation.Reset();
                filteredGyroMouseRate = Vector2.Zero;
                filteredGyroMouseRateInitialized = false;
                pendingMouseDx += mouseSensitivityX * (yawRate * subSamplePeriod * degToRad);
                pendingMouseDy += -(mouseSensitivityY * (pitchRate * subSamplePeriod * degToRad));
            }

            float rollDeg = rollRad * (180.0f / (float)Math.PI);

            if (!flushToMouse) {
                RecordGyroMouseDiagnosticSample(0, 0, rollDeg, yawRate, pitchRate);
                return;
            }

            int dx = (int)pendingMouseDx;
            int dy = (int)pendingMouseDy;
            pendingMouseDx -= dx;
            pendingMouseDy -= dy;

            if (dx != 0 || dy != 0)
                RecordGyroMousePointerRequestTiming();

            RecordGyroMouseDiagnosticSample(dx, dy, rollDeg, yawRate, pitchRate);

            if (dx != 0 || dy != 0)
                MoveGyroMouseBy(dx, dy);
        }

        // left_click/right_click/center_click/scroll_up/scroll_down - bindable controller
        // buttons that simulate a mouse action, reachable only from
        // inside the same "gyro-mouse is actually active" block as the cursor movement above, so
        // they're inert the rest of the time rather than stealing a button from its normal game
        // mapping. Read fresh from the profile snapshot each call, not cached in a field the way
        // most other settings here are - so a newly-bound key takes effect immediately instead of
        // needing the controller to reconnect first. Can be a combo like every other bind now
        // (see Reassign.cs), which IsComboHeld handles - this used to be a bare
        // Int32.Parse(val.Substring(4)) on the whole value, which crashed the poll thread with a
        // FormatException the moment val held a "+"-joined combo instead of one plain joy_N.
        // ConcurrentDictionary, not plain Dictionary: PrepareForMappingProfileChange (join/split
        // thread, via ReleaseGyroMouseActions) reads/writes this while the poll thread
        // concurrently does the same via SimulateGyroMouseButton/Scroll every report.
        protected readonly ConcurrentDictionary<string, bool> gyroMouseComboHeld =
            new ConcurrentDictionary<string, bool>();

        // Shared rising/falling-edge bookkeeping for both gyro-mouse-only actions below - resolve
        // configKey's current bind, evaluate whether it's held, and report whether it was held on
        // the previous call so each caller only needs its own 2-line edge reaction.
        protected bool UpdateGyroMouseComboHeld(string configKey, bool enabled, out bool wasHeld) {
            string val = MappingValue(configKey);
            bool held = enabled && val != "0" && IsComboHeld(val);
            wasHeld = gyroMouseComboHeld.TryGetValue(configKey, out bool prev) && prev;
            gyroMouseComboHeld[configKey] = held;
            return held;
        }

        protected void SimulateGyroMouseButton(string configKey, int buttonCode, bool enabled) {
            bool held = UpdateGyroMouseComboHeld(configKey, enabled, out bool wasHeld);

            if (held && !wasHeld)
                form.SimulateButtonHold(buttonCode);
            else if (!held && wasHeld)
                form.SimulateButtonRelease(buttonCode);
        }

        // Scroll has no hold/release equivalent - just a discrete tick per press, matching a
        // physical scroll wheel's own click detents rather than a continuous rate while held.
        protected void SimulateGyroMouseScroll(string configKey, bool up, bool enabled) {
            bool held = UpdateGyroMouseComboHeld(configKey, enabled, out bool wasHeld);

            if (held && !wasHeld)
                form.SimulateScroll(up);
        }

        // Final backstop, called by Poll()'s shell after the read loop exits (a disconnect/detach
        // may prevent another report from arriving to naturally release these) - releases all
        // five gyro-mouse-only actions. Fully shared, no per-device variance expected, so this is
        // a plain method rather than a virtual hook (unlike the other Poll()-shell hooks, which
        // exist specifically because their bodies differ per device).
        protected void ReleaseGyroMouseActions() {
            SimulateGyroMouseButton("left_click", (int)WindowsInput.Events.ButtonCode.Left,
                                    false);
            SimulateGyroMouseButton("right_click", (int)WindowsInput.Events.ButtonCode.Right,
                                    false);
            SimulateGyroMouseButton("center_click", (int)WindowsInput.Events.ButtonCode.Middle,
                                    false);
            SimulateGyroMouseScroll("scroll_up", true, false);
            SimulateGyroMouseScroll("scroll_down", false, false);
        }

    }
}
