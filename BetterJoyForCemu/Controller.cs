using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
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

        public OutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;

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
