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
    // This first content sub-step moves only pure data with no attached logic: nested type
    // declarations (state_, Button) and simple fields with no property getter/setter, no
    // constructor-time computation that reaches into not-yet-moved subsystems (mapping-profile
    // engine, gyro pipeline, etc.), and no method bodies. Everything here is exactly as safe to
    // read/write from Joycon as it was before the move - C# doesn't distinguish "declared in the
    // base class" from "declared in the subclass" for field/property access from subclass code.
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
