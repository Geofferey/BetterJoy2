using System;
using System.Configuration;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
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

        // No-op by default; Joycon overrides this with Nintendo's actual LED-set subcommand -
        // already self-guards on UsesNintendoProtocol today, kept as a hook (rather than promoted
        // outright) so that guard stays exactly where the rest of Joycon's Nintendo-only output
        // wiring lives.
        public virtual void SetLEDByPlayerNum(int id) { }

        // No-op by default; Joycon overrides this to release all five gyro-mouse-only actions
        // (left/right/center click, scroll up/down) - a hook for now since the gyro-mouse
        // pipeline itself isn't promoted to Controller yet.
        protected virtual void ReleaseGyroMouseActions() { }

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
    }
}
