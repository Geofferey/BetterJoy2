using System;
using System.Collections.Generic;

namespace BetterJoyForCemu {
    // Stick-to-mouse and stick-to-keys - the Sticks page's runtime half, split out of
    // Controller.cs the same way GyroMath.cs holds the gyro half. Still a partial class of
    // Controller: every method here reads the same instance state (stick/stick2, the activation
    // fields, the profile options) exactly as if it were declared there.
    //
    // Each stick is an independent source: either can drive the pointer, hold keyboard keys, or
    // both at once, so one stick can aim while the other walks. Nothing here is device-specific -
    // sticks are normalized to the same -1..1 floats on every controller (Controller.CenterSticks)
    // long before this runs.
    public abstract partial class Controller {
        // 100% sensitivity at full deflection. The single calibration constant the percentages in
        // the UI are relative to, chosen to feel close to gyro mouse at its default sensitivity.
        private const float StickMousePixelsPerSecond = 1000.0f;
        // A report gap outside this range is a stall (a hitching poll thread, a debugger break,
        // the first report after a recenter zeroes dt) - clamp rather than teleport the pointer.
        private const float StickMouseMinReportSeconds = 0.001f;
        private const float StickMouseMaxReportSeconds = 0.05f;

        internal const int StickKeyUp = 1;
        internal const int StickKeyDown = 2;
        internal const int StickKeyLeft = 4;
        internal const int StickKeyRight = 8;

        // The physical stick as parsed this report, captured before anything layers onto it. See
        // SnapshotPhysicalSticks.
        private readonly float[] physicalStickSnapshot = { 0, 0 };
        private readonly float[] physicalStick2Snapshot = { 0, 0 };
        private float stickMouseRemainderX;
        private float stickMouseRemainderY;
        private readonly List<string> heldStickKeyOutputs = new List<string>();
        private readonly List<string> desiredStickKeyOutputs = new List<string>();
        private readonly HashSet<string> seenStickKeyOutputs =
            new HashSet<string>(StringComparer.Ordinal);

        protected bool activeStickMouseLeft;
        protected bool activeStickMouseRight;
        protected bool activeStickKeysLeft;
        protected bool activeStickKeysRight;
        private bool prevActiveStickMouseLeftComboHeld;
        private bool prevActiveStickMouseRightComboHeld;
        private bool prevActiveStickKeysLeftComboHeld;
        private bool prevActiveStickKeysRightComboHeld;
        protected bool stickMouseLeftEnabledThisReport;
        protected bool stickMouseRightEnabledThisReport;
        protected bool stickKeysLeftEnabledThisReport;
        protected bool stickKeysRightEnabledThisReport;

        // Which stick rows mean anything for this controller. Keyed off Kind rather than
        // HasDualSticks because SNES and N64 both report HasDualSticks true as deliberately
        // preserved known issues (see their class comments). A joined Joy-Con pair cross-wires
        // both halves' sticks into both units (NintendoController.ProcessButtonsAndStick), so a
        // pair has both even though each half reports HasDualSticks false. Keep these in lockstep
        // with Reassign.KindHasSticksPage/KindHasRightStick - the pane and the runtime must agree.
        protected bool HasLeftStickOutput => Kind != ControllerKind.Snes;

        protected bool HasRightStickOutput =>
            HasLeftStickOutput && Kind != ControllerKind.N64 &&
            (HasDualSticks || (other != null && other != this));

        // Taken at the top of DoThingsWithButtons, before gyro-to-stick or the floating touchpad
        // stick layer anything on top. Deliberately not read from stick[]/stick2[] later:
        // gyro-to-stick lands INSIDE DoThingsWithButtons in raw-IMU mode but AFTER it in filtered
        // mode, so reading the live array would make the two IMU modes behave differently, and it
        // would let gyro-driven stick motion drive the pointer - a feedback loop, not an input.
        protected void SnapshotPhysicalSticks() {
            physicalStickSnapshot[0] = stick[0];
            physicalStickSnapshot[1] = stick[1];
            physicalStick2Snapshot[0] = stick2[0];
            physicalStick2Snapshot[1] = stick2[1];
        }

        // Radial deadzone. The remainder is rescaled across the full 0..1 range rather than
        // clipped, so the first movement past the deadzone edge is a crawl instead of a jump, and
        // direction is preserved so a diagonal push stays diagonal.
        internal static void ApplyStickDeadzone(float x, float y, int deadzonePercent,
                                                out float outX, out float outY) {
            outX = 0.0f;
            outY = 0.0f;
            float magnitude = (float)Math.Sqrt(x * x + y * y);
            if (magnitude <= 0.0f)
                return;

            float deadzone = Math.Max(0, Math.Min(99, deadzonePercent)) / 100.0f;
            if (magnitude <= deadzone)
                return;

            float scaled = Math.Min(1.0f, (magnitude - deadzone) / (1.0f - deadzone));
            outX = x / magnitude * scaled;
            outY = y / magnitude * scaled;
        }

        // Applied to magnitude only, never per axis: shaping each axis separately bends a straight
        // diagonal push into a curve. An unknown value reads as linear rather than throwing, so a
        // hand-edited profile degrades to the plainest behavior.
        internal static float ApplyStickResponseCurve(float magnitude, string curve) {
            float clamped = Math.Max(0.0f, Math.Min(1.0f, magnitude));
            if (String.Equals(curve, "quadratic", StringComparison.OrdinalIgnoreCase))
                return clamped * clamped;
            if (String.Equals(curve, "cubic", StringComparison.OrdinalIgnoreCase))
                return clamped * clamped * clamped;
            return clamped;
        }

        // One report's pointer delta in float pixels. Y is negated because BetterJoy's stick Y
        // grows upward while the desktop's Y grows downward.
        internal static void ComputeStickMouseDelta(float x, float y, int deadzonePercent,
                                                    string curve, int sensitivityXPercent,
                                                    int sensitivityYPercent, float reportSeconds,
                                                    out float dx, out float dy) {
            dx = 0.0f;
            dy = 0.0f;
            float deadzonedX, deadzonedY;
            ApplyStickDeadzone(x, y, deadzonePercent, out deadzonedX, out deadzonedY);
            float magnitude = (float)Math.Sqrt(deadzonedX * deadzonedX + deadzonedY * deadzonedY);
            if (magnitude <= 0.0f)
                return;

            // The curve reshapes speed, not direction: scale the unit vector by the shaped
            // magnitude so a 45-degree push still travels at 45 degrees.
            float shaped = ApplyStickResponseCurve(magnitude, curve);
            float unitX = deadzonedX / magnitude;
            float unitY = deadzonedY / magnitude;
            float pixels = shaped * StickMousePixelsPerSecond * reportSeconds;
            dx = unitX * pixels * (Math.Max(10, Math.Min(400, sensitivityXPercent)) / 100.0f);
            dy = -unitY * pixels * (Math.Max(10, Math.Min(400, sensitivityYPercent)) / 100.0f);
        }

        // Per axis, not radial - that is exactly what makes a diagonal push hold the two
        // neighbouring keys (up AND right) rather than picking one "closest" direction, which is
        // what a game expecting WASD wants. One axis cannot be pushed both ways, so Up|Down and
        // Left|Right can never co-occur.
        internal static int ComputeStickKeyMask(float x, float y, int thresholdPercent) {
            float threshold = Math.Max(1, Math.Min(100, thresholdPercent)) / 100.0f;
            int mask = 0;
            if (y >= threshold)
                mask |= StickKeyUp;
            else if (y <= -threshold)
                mask |= StickKeyDown;
            if (x <= -threshold)
                mask |= StickKeyLeft;
            else if (x >= threshold)
                mask |= StickKeyRight;
            return mask;
        }

        // Called once per report from the tail of DoThingsWithButtons, after the touchpad outputs,
        // so every other stick contribution has already been applied by the time inhibit runs.
        protected void ProcessStickOutputs(float reportSeconds) {
            // A joined pair runs this on both halves against one shared profile, and both halves
            // see both cross-wired sticks - without ownership the pointer would move twice per
            // report pair. Reuses gyro mouse's existing handedness rule rather than a second one.
            bool owns = OwnsGyroMouse();
            bool leftMouse = owns && stickMouseLeftEnabledThisReport;
            bool rightMouse = owns && stickMouseRightEnabledThisReport;
            bool leftKeys = owns && stickKeysLeftEnabledThisReport;
            bool rightKeys = owns && stickKeysRightEnabledThisReport;

            // Reconciled on every report, including reports where no stick drives the pointer -
            // otherwise leaving that state while a synthetic button is down skips the only path
            // that could send its matching up (the same reason gyro reconciles its own actions
            // unconditionally).
            ReconcileStickMouseActions(leftMouse || rightMouse);

            if (!leftMouse && !rightMouse && !leftKeys && !rightKeys) {
                ReleaseStickOutputs();
                return;
            }

            float clampedSeconds = Math.Max(StickMouseMinReportSeconds,
                Math.Min(StickMouseMaxReportSeconds, reportSeconds));
            float dx = 0.0f;
            float dy = 0.0f;
            // Pointer lock freezes travel without touching activation, so the clicks and the key
            // output above keep working while it is held - the stick's counterpart to Clench gyro.
            if (!IsStickMovementLocked()) {
                if (leftMouse)
                    AccumulateStickMouse(physicalStickSnapshot, true, clampedSeconds, ref dx, ref dy);
                if (rightMouse)
                    AccumulateStickMouse(physicalStick2Snapshot, false, clampedSeconds, ref dx, ref dy);
            }
            EmitStickMouse(dx, dy);

            desiredStickKeyOutputs.Clear();
            seenStickKeyOutputs.Clear();
            if (leftKeys)
                CollectStickKeyOutputs(physicalStickSnapshot, true);
            if (rightKeys)
                CollectStickKeyOutputs(physicalStick2Snapshot, false);
            ApplyStickKeyOutputs();

            ApplyStickInhibit(leftMouse || leftKeys, rightMouse || rightKeys);
        }

        private bool IsStickMovementLocked() {
            string pointerLock = MappingValue("stick_pointer_lock");
            return !String.IsNullOrEmpty(pointerLock) && pointerLock != "0" &&
                IsComboHeld(pointerLock);
        }

        // The Sticks page's own mouse actions, on the shared edge bookkeeping gyro and touchpad
        // mouse already use. Each has its own binding, so a stick-mouse click never collides with
        // the gyro one.
        private void ReconcileStickMouseActions(bool enabled) {
            SimulateMouseActionButton("stick_left_click",
                (int)WindowsInput.Events.ButtonCode.Left, enabled);
            SimulateMouseActionButton("stick_right_click",
                (int)WindowsInput.Events.ButtonCode.Right, enabled);
            SimulateMouseActionButton("stick_center_click",
                (int)WindowsInput.Events.ButtonCode.Middle, enabled);
            SimulateMouseActionScroll("stick_scroll_up", true, enabled);
            SimulateMouseActionScroll("stick_scroll_down", false, enabled);
        }

        private void AccumulateStickMouse(float[] source, bool isLeftStick, float reportSeconds,
                                          ref float dx, ref float dy) {
            string suffix = isLeftStick ? "Left" : "Right";
            float stickDx, stickDy;
            ComputeStickMouseDelta(source[0], source[1],
                ProfileIntOption("StickMouseDeadzone" + suffix, 15),
                ProfileStringOption("StickMouseCurve" + suffix, "quadratic"),
                ProfileIntOption("StickMouseSensitivityX" + suffix, 100),
                ProfileIntOption("StickMouseSensitivityY" + suffix, 100),
                reportSeconds, out stickDx, out stickDy);
            // Both sticks sum into one delta and one emission, so two sticks on mouse add
            // together instead of fighting over the shared sub-pixel remainder below.
            dx += stickDx;
            dy += stickDy;
        }

        private void EmitStickMouse(float dx, float dy) {
            if (dx == 0.0f && dy == 0.0f) {
                stickMouseRemainderX = 0.0f;
                stickMouseRemainderY = 0.0f;
                return;
            }

            // Sub-pixel remainder carry, same model ProcessTouchpadMouse uses: a slow push must
            // still creep rather than truncating to zero on every report.
            float scaledX = dx + stickMouseRemainderX;
            float scaledY = dy + stickMouseRemainderY;
            int moveX = (int)scaledX;
            int moveY = (int)scaledY;
            stickMouseRemainderX = scaledX - moveX;
            stickMouseRemainderY = scaledY - moveY;
            if (moveX != 0 || moveY != 0)
                MoveGyroMouseBy(moveX, moveY); // inherits IsModifierHeld gating + cursor routing
        }

        private void CollectStickKeyOutputs(float[] source, bool isLeftStick) {
            string prefix = isLeftStick ? "stick_left_key_" : "stick_right_key_";
            int mask = ComputeStickKeyMask(source[0], source[1],
                ProfileIntOption("StickKeysThreshold" + (isLeftStick ? "Left" : "Right"), 55));
            if ((mask & StickKeyUp) != 0)
                CollectStickKeyMapping(prefix + "up");
            if ((mask & StickKeyDown) != 0)
                CollectStickKeyMapping(prefix + "down");
            if ((mask & StickKeyLeft) != 0)
                CollectStickKeyMapping(prefix + "left");
            if ((mask & StickKeyRight) != 0)
                CollectStickKeyMapping(prefix + "right");
        }

        private void CollectStickKeyMapping(string bindKey) {
            string mapping = MappingValue(bindKey);
            if (String.IsNullOrEmpty(mapping) || mapping == "0" ||
                    !ControllerMappings.IsValidCustomBindingOutput(mapping))
                return;

            foreach (string part in mapping.Split('+')) {
                if (part.StartsWith("joy_", StringComparison.Ordinal)) {
                    // Same virtual-button lane custom bindings use - cleared at the top of every
                    // report, never written into buttons[], so it cannot feed combo matching.
                    int buttonIndex;
                    if (Int32.TryParse(part.Substring(4), out buttonIndex) &&
                            buttonIndex >= 0 && buttonIndex < customVirtualButtons.Length)
                        customVirtualButtons[buttonIndex] = true;
                } else if (part.StartsWith("key_", StringComparison.Ordinal) ||
                           part.StartsWith("mse_", StringComparison.Ordinal)) {
                    // Deduped across both sticks: two sticks bound to the same key must not
                    // release each other when only one of them lets go.
                    if (seenStickKeyOutputs.Add(part))
                        desiredStickKeyOutputs.Add(part);
                }
                // Preset actions (ControllerMappings.TryGetCustomAction) are deliberately not
                // handled: they are one-shot commands, and a held direction has no rising edge
                // worth firing them on. Custom bindings remain the place for those.
            }
        }

        // The ProcessCustomBindings diff, applied to this subsystem's own held set: release what
        // is no longer wanted (in reverse, unwinding modifier order) before pressing what is new.
        private void ApplyStickKeyOutputs() {
            for (int i = heldStickKeyOutputs.Count - 1; i >= 0; i--) {
                string part = heldStickKeyOutputs[i];
                if (!seenStickKeyOutputs.Contains(part) && !CustomBindingsHold(part))
                    SetCustomDesktopOutput(part, false);
            }
            foreach (string part in desiredStickKeyOutputs) {
                if (!heldStickKeyOutputs.Contains(part) && !CustomBindingsHold(part))
                    SetCustomDesktopOutput(part, true);
            }
            heldStickKeyOutputs.Clear();
            heldStickKeyOutputs.AddRange(desiredStickKeyOutputs);
        }

        // Custom bindings own the same key/mouse output lane and run earlier in this same report
        // (ProcessCustomBindings), so their held set is current here. Without this check the two
        // subsystems fight over a shared code: one sends an up the other still wants held. The
        // tidier end state is a single held set shared by both - worth doing if a third consumer
        // ever appears, rather than a third copy of this guard.
        private bool CustomBindingsHold(string part) {
            lock (customBindingsLock)
                return heldCustomDesktopOutputs.Contains(part);
        }

        // Withholds the physical stick from the virtual controller while it is driving mouse or
        // keys, so aiming the pointer doesn't also shove the in-game stick. Subtracts the snapshot
        // rather than zeroing the axis on purpose: gyro-to-stick (raw mode) and the floating
        // touchpad stick have already been added on top by now, and they are separate outputs the
        // user never asked to inhibit.
        private void ApplyStickInhibit(bool leftDriving, bool rightDriving) {
            if (leftDriving && ProfileBoolOption("StickInhibitLeft"))
                SubtractStickSnapshot(stick, physicalStickSnapshot);
            if (rightDriving && ProfileBoolOption("StickInhibitRight"))
                SubtractStickSnapshot(stick2, physicalStick2Snapshot);
        }

        private static void SubtractStickSnapshot(float[] target, float[] snapshot) {
            target[0] = Math.Max(-1.0f, Math.Min(1.0f, target[0] - snapshot[0]));
            target[1] = Math.Max(-1.0f, Math.Min(1.0f, target[1] - snapshot[1]));
        }

        // Force every stick-held key/button up. Must be reachable from every teardown path that
        // already calls ReleaseGyroMouseActions - a missed one leaves Windows holding W with no
        // controller left to release it.
        protected void ReleaseStickOutputs() {
            if (form != null)
                ReconcileStickMouseActions(false);
            for (int i = heldStickKeyOutputs.Count - 1; i >= 0; i--)
                SetCustomDesktopOutput(heldStickKeyOutputs[i], false);
            heldStickKeyOutputs.Clear();
            desiredStickKeyOutputs.Clear();
            seenStickKeyOutputs.Clear();
            stickMouseRemainderX = 0.0f;
            stickMouseRemainderY = 0.0f;
        }

        // Activation state only - the held outputs themselves are released by ReleaseStickOutputs,
        // which every caller of this also calls (see PrepareForMappingProfileChange).
        protected void ResetStickActivationState() {
            activeStickMouseLeft = false;
            activeStickMouseRight = false;
            activeStickKeysLeft = false;
            activeStickKeysRight = false;
            prevActiveStickMouseLeftComboHeld = false;
            prevActiveStickMouseRightComboHeld = false;
            prevActiveStickKeysLeftComboHeld = false;
            prevActiveStickKeysRightComboHeld = false;
            stickMouseLeftEnabledThisReport = false;
            stickMouseRightEnabledThisReport = false;
            stickKeysLeftEnabledThisReport = false;
            stickKeysRightEnabledThisReport = false;
        }

        // Same four-line shape as the six gyro/touchpad activations, but reading the Sticks page's
        // own StickHoldToggle preference instead of GyroHoldToggle. justEnabled is discarded:
        // unlike gyro there is no orientation to recenter on the rising edge.
        protected void UpdateStickActivation() {
            bool justEnabled;
            stickMouseLeftEnabledThisReport = HasLeftStickOutput && UpdateOutputActivation(
                "active_stick_mouse_left", ref activeStickMouseLeft,
                ref prevActiveStickMouseLeftComboHeld, out justEnabled, "StickHoldToggle");
            stickMouseRightEnabledThisReport = HasRightStickOutput && UpdateOutputActivation(
                "active_stick_mouse_right", ref activeStickMouseRight,
                ref prevActiveStickMouseRightComboHeld, out justEnabled, "StickHoldToggle");
            stickKeysLeftEnabledThisReport = HasLeftStickOutput && UpdateOutputActivation(
                "active_stick_keys_left", ref activeStickKeysLeft,
                ref prevActiveStickKeysLeftComboHeld, out justEnabled, "StickHoldToggle");
            stickKeysRightEnabledThisReport = HasRightStickOutput && UpdateOutputActivation(
                "active_stick_keys_right", ref activeStickKeysRight,
                ref prevActiveStickKeysRightComboHeld, out justEnabled, "StickHoldToggle");
        }
    }
}
