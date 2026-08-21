namespace BetterJoyForCemu {
    // Base class for every physical controller type BetterJoy talks to - see
    // DOCS/CONTROLLERS-REFACTOR.md for the full plan this is step 2 of. Currently empty: this is
    // the safest possible first move (establish the inheritance shape, zero behavior change,
    // nothing moved into it yet), with Joycon becoming its first/only subclass. Content moves
    // into this class incrementally over following steps, not all at once - see the plan's
    // "Suggested migration approach" for why, and its "What must not regress" section for the
    // three highest-stakes surfaces (PadId/auto-join, XInput mapping, gyro/IMU) that live
    // substantially in what will eventually move here.
    public abstract class Controller {
    }
}
