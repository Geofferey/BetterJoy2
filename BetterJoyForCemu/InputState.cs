using System.Collections.Generic;

namespace BetterJoyForCemu {
    // Tracks which keyboard keys and mouse buttons are CURRENTLY held, fed by Program's
    // OnKeyDown/OnKeyUp/OnMouseButtonDown/OnMouseButtonUp - the same unified entry points that
    // already work identically in GUI mode (direct WindowsInput.Capture.Global hook) and
    // service mode (forwarded from the session-launched input helper over a pipe).
    //
    // Exists so a bind can be a COMBINATION of inputs (for example a gyro activation mapping's
    // "joy_4+key_65"), not
    // just one - checking "is this whole combo held right now" needs to know the current held/
    // released state of every key and mouse button, not just react to the one that just changed.
    // Controller buttons don't need an entry here: each Joycon already exposes its own current
    // state directly via GetButton, checked per-instance where a combo is actually evaluated.
    public static class InputState {
        private static readonly HashSet<int> heldKeys = new HashSet<int>();
        private static readonly HashSet<int> heldMouseButtons = new HashSet<int>();
        // What BetterJoy is synthesizing right now. The hooks above cannot tell our own
        // SendInput/FakerInput traffic from a real press - both GUI and service mode re-observe
        // it - so without this a stick bound to output W satisfies every key_87 bind in the same
        // profile, and a custom bind whose output is its own trigger latches. Held-ness for those
        // codes is therefore answered from the physical set MINUS whatever we are generating.
        //
        // Refcounted rather than a flag: two subsystems (a custom bind and a stick direction, or
        // both sticks) can hold the same code, and the second release must not un-mask the first
        // one's still-active output. Counts only ever move through Begin/End below, which the
        // emitters call in lockstep with the actual hold/release.
        private static readonly Dictionary<int, int> synthesizedKeys = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> synthesizedMouseButtons = new Dictionary<int, int>();
        private static readonly object stateLock = new object();

        public static void KeyDown(int keyCode) {
            lock (stateLock) { heldKeys.Add(keyCode); }
        }

        public static void KeyUp(int keyCode) {
            lock (stateLock) { heldKeys.Remove(keyCode); }
        }

        public static void MouseDown(int buttonCode) {
            lock (stateLock) { heldMouseButtons.Add(buttonCode); }
        }

        public static void MouseUp(int buttonCode) {
            lock (stateLock) { heldMouseButtons.Remove(buttonCode); }
        }

        // Call in lockstep with an actual synthesized hold/release - every Begin needs its End,
        // including on teardown paths, or that code stays masked for the rest of the session.
        public static void BeginSynthesizedKey(int keyCode) {
            lock (stateLock) { Retain(synthesizedKeys, keyCode); }
        }

        public static void EndSynthesizedKey(int keyCode) {
            lock (stateLock) { Release(synthesizedKeys, keyCode); }
        }

        public static void BeginSynthesizedMouseButton(int buttonCode) {
            lock (stateLock) { Retain(synthesizedMouseButtons, buttonCode); }
        }

        public static void EndSynthesizedMouseButton(int buttonCode) {
            lock (stateLock) { Release(synthesizedMouseButtons, buttonCode); }
        }

        public static bool IsKeyHeld(int keyCode) {
            lock (stateLock) {
                return heldKeys.Contains(keyCode) && !synthesizedKeys.ContainsKey(keyCode);
            }
        }

        public static bool IsMouseButtonHeld(int buttonCode) {
            lock (stateLock) {
                return heldMouseButtons.Contains(buttonCode) &&
                    !synthesizedMouseButtons.ContainsKey(buttonCode);
            }
        }

        // Diagnostics only - the emitters are the source of truth for these counts.
        public static bool IsSynthesizing(int keyCode) {
            lock (stateLock) { return synthesizedKeys.ContainsKey(keyCode); }
        }

        private static void Retain(Dictionary<int, int> counts, int code) {
            int count;
            counts[code] = counts.TryGetValue(code, out count) ? count + 1 : 1;
        }

        private static void Release(Dictionary<int, int> counts, int code) {
            int count;
            if (!counts.TryGetValue(code, out count))
                return;
            if (count <= 1)
                counts.Remove(code);
            else
                counts[code] = count - 1;
        }
    }
}
