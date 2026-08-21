using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Text;

namespace BetterJoyForCemu {
    // PadId assignment/compaction and virtual controller (ViGEmBus) creation/destruction, split
    // out of Program.cs (JoyconManager) into its own file per DOCS/CONTROLLERS-REFACTOR.md's
    // virtual-controller-lifecycle section - this is highest-stakes-surface #1 from "What must
    // not regress" (three prior regressions - fb3dca1/3d1c38a/156dcf3 - happened in exactly this
    // code), so it gets a dedicated, standalone home instead of living alongside the unrelated
    // device-scanning/enumeration code in Program.cs.
    //
    // Deliberately pairing-ignorant, by design, not by accident: this only ever needs to know
    // "does this controller currently want an active virtual controller, or is it passively
    // parked without one" - never *why* (Joy-Con is the only device type known to pair two
    // physical units into one logical controller; that quirk, and the "which half is the loser"
    // decision, stays in the auto-join/JoinOrSplitJoycon code that calls into these primitives,
    // not here). Still a partial class of JoyconManager, not a separate class - these methods
    // read/write j and form directly, same as everything else in Program.cs, just physically
    // relocated.
    //
    // Single-instance parameters (NextAvailablePadId's exclude, AssignPadId/CreateOutputControllers
    // /DestroyOutputControllers/ReassignSplitOffJoycon's jc) are Controller-typed as of step 4
    // Phase D - they just receive an already-resolved reference, so widening them doesn't depend
    // on j's own element type. The loop variables that iterate j directly (CleanUp/ReassignPadIds/
    // ResolveStalePadIdCollisions/DumpState/NextAvailablePadId's own loop) stay Joycon-typed for
    // now - j itself is still ConcurrentList<Joycon> until step 4's atomic-flip phase, and C#
    // generics are invariant, so a foreach variable bound to j can't be widened ahead of j itself.
    public partial class JoyconManager {
        // Smallest PadId not currently in use by a connected controller - see the call site for
        // why j.Count itself isn't safe to use directly. exclude lets a caller ask "what's free
        // for this specific controller" without that controller's own (about-to-be-replaced)
        // PadId counting against itself - see ReassignSplitOffJoycon, whose caller unlinks
        // .other before calling in, which would otherwise flip it from "passive, ignored" to
        // "solo, counted as using its own stale value" for this computation alone.
        int NextAvailablePadId(Controller exclude = null) {
            var used = new HashSet<int>();
            foreach (Joycon v in j) {
                if (v == exclude)
                    continue;

                // A joined pair's passive half doesn't occupy a visible LED/player slot on its
                // own - its physical LED just mirrors its active partner's (see ReassignPadIds).
                // Its PadId field is deliberately left untouched while paired, parked until it
                // splits back off (see ReassignSplitOffJoycon), so it must not count as "in use"
                // here - otherwise a genuinely new controller gets skipped past a slot nothing
                // visible is actually occupying.
                bool isPassiveHalf = v.other != null && v.other != v && v.out_xbox == null && v.out_ds4 == null;
                if (isPassiveHalf)
                    continue;
                used.Add(v.PadId);
            }

            int id = 0;
            while (used.Contains(id))
                id++;
            return id;
        }

        void CleanUp() { // removes dropped controllers from list
            List<Joycon> rem = new List<Joycon>();
            List<Joycon> droppedNotify = new List<Joycon>();
            List<Joycon> partnerNotify = new List<Joycon>();

            foreach (Joycon joycon in j) {
                if (joycon.state == Joycon.state_.DROPPED) {
                    // Capture the pair partner (if any) before Detach/nulling below, so
                    // HandleJoyconDropped can still find whichever slot(s) need fixing up -
                    // the dropped Joycon's own slot, and/or the surviving partner's.
                    Joycon partner = (joycon.other != null && joycon.other != joycon) ? joycon.other : null;

                    if (joycon.other != null) {
                        Joycon survivor = joycon.other;
                        survivor.other = null; // The other of the other is the joycon itself

                        // The survivor needs its own controller back if the dropped half was the
                        // one actively driving the pair's shared controller (see JoinOrSplitJoycon/
                        // the auto-join block above, which disconnects whichever side loses the
                        // join) - otherwise it's left solo with no virtual controller at all.
                        // No-op if the survivor already has one (it was the active side).
                        CreateOutputControllers(survivor);
                    }

                    joycon.Detach(true);
                    rem.Add(joycon);

                    droppedNotify.Add(joycon);
                    partnerNotify.Add(partner);

                    form.AppendTextBox("Removed dropped controller. Can be reconnected.\r\n");
                }
            }

            foreach (Joycon v in rem)
                j.Remove(v);

            // Compact PadIds for whatever's left so dropping down to fewer controllers -
            // especially down to just one - reads as player 1 again, matching what unplugging
            // and replugging the physical controller already does today (which is exactly what
            // this is standing in for, so the user doesn't have to do that by hand). See
            // ReassignPadIds for why this also means tearing down and recreating each affected
            // survivor's virtual controller, not just its LED.
            if (rem.Count > 0)
                ReassignPadIds();

            // Notified only after removal is fully done (list + pairing), not before - MainForm's
            // implementation doesn't care (it acts on the passed-in Joycon references directly,
            // not by re-reading the controller list), but HeadlessJoyconHost's does: it rebuilds
            // its status snapshot from the live list, and building that snapshot before the
            // drop was actually applied meant a GUI connected to a running service never saw a
            // disconnect at all until it reconnected (the next change - a different controller
            // connecting - would finally push a snapshot that happened to already be missing it).
            for (int i = 0; i < droppedNotify.Count; i++)
                form.HandleJoyconDropped(droppedNotify[i], partnerNotify[i]);
        }

        // Compacts PadId assignments for whatever's currently in j down to 0..n-1, based on each
        // controller's existing PadId order. Dropping controllers can leave survivors holding
        // non-contiguous PadIds (e.g. players 1 and 3 remain after player 2 disconnects) - this
        // closes the gaps, most visibly in the "down to just one controller left" case, which
        // should read as player 1 without the user having to unplug/replug it by hand.
        //
        // Unlike the old LED-only version, a controller whose PadId actually changes gets its
        // virtual controller torn down and recreated at the new identity via AssignPadId, not
        // just a re-painted LED: ViGEmBus has no API to rename an already-Connect()ed target's
        // XInput slot, so a fresh Connect() is how identity changes here - the same "destroy and
        // recreate" pattern already tested and confirmed working for join/split (see the auto-
        // join loser handling above and JoinOrSplitJoycon). A controller whose PadId doesn't
        // change is left completely untouched - no churn, no risk to a game already using it.
        //
        // A joined pair shares one rank between both halves for LED display, matching
        // Joycon.other's setter (which also does Math.Min(...) between a pair to pick one LED
        // value for both) - but only the pair's active half (the one actually holding a virtual
        // controller) goes through AssignPadId; the passive half is deliberately left without a
        // virtual controller (see the auto-join/JoinOrSplitJoycon loser handling) and must stay
        // that way, or a joined pair goes back to showing up as two separate XInputs.
        //
        // Critically, the passive half's actual PadId field is left completely untouched here -
        // only its LED is refreshed. It already has its own distinct PadId from whenever it was
        // originally connected (join never touches PadId, only out_xbox/out_ds4), parked and
        // waiting for whenever this pair splits back apart. Overwriting it to match the active
        // half's compacted rank - as an earlier version of this method did - made both halves
        // share the same PadId, so splitting the pair later produced two controllers that both
        // claimed to be the same player instead of two distinct ones.
        void ReassignPadIds() {
            var ranked = new List<Joycon>(j);
            ranked.Sort((a, b) => a.PadId.CompareTo(b.PadId));

            var assigned = new HashSet<Joycon>();
            int rank = 0;
            foreach (Joycon jc in ranked) {
                if (assigned.Contains(jc))
                    continue;

                assigned.Add(jc);
                bool isPair = jc.other != null && jc.other != jc;
                if (isPair)
                    assigned.Add(jc.other);

                Joycon active = jc;
                Joycon passive = null;
                if (isPair) {
                    bool jcHasOutput = jc.out_xbox != null || jc.out_ds4 != null;
                    active = jcHasOutput ? jc : jc.other;
                    passive = active == jc ? jc.other : jc;
                }

                AssignPadId(active, rank);
                if (passive != null)
                    passive.RequestLEDUpdate(rank);

                rank++;
            }
        }

        // Full dump of every controller's PadId/pairing/output state - see DebugLog (off by
        // default, gated behind the DebugLogging AppSetting). Called at every PadId-affecting
        // decision point (connect, join, split, rank compaction) so player-slot/LED bugs can be
        // diagnosed from debug.log instead of guessed at.
        void DumpState(string tag) {
            var sb = new StringBuilder(tag).Append(':');
            foreach (Joycon v in j) {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " [pad={0} other={1} hasXbox={2} hasDs4={3}]",
                    v.PadId,
                    v.other == null ? "null" : (v.other == v ? "self" : v.other.PadId.ToString(CultureInfo.InvariantCulture)),
                    v.out_xbox != null, v.out_ds4 != null);
            }
            DebugLog.Write(sb.ToString());
        }

        void AssignPadId(Controller jc, int newPadId) {
            if (jc.PadId == newPadId)
                return;

            DebugLog.Write(string.Format(CultureInfo.InvariantCulture, "AssignPadId: pad {0} -> {1}", jc.PadId, newPadId));
            jc.PadId = newPadId;
            ResolveStalePadIdCollisions();
            jc.RequestLEDUpdate(newPadId);

            DestroyOutputControllers(jc);
            CreateOutputControllers(jc);
        }

        // Called right after a Joycon splits off from a pair (see JoinOrSplitJoycon's split
        // branch in MainForm.cs/HeadlessJoyconHost.cs) - its PadId was deliberately left
        // untouched while it was the passive half (see ReassignPadIds's comment on why), and
        // NextAvailablePadId stopped counting it as "in use" the moment it went passive, so a
        // different controller may already have claimed that same number. Give it a fresh,
        // guaranteed-free identity instead of assuming the old one is still safe to reuse. A
        // no-op (via AssignPadId's own check) if nothing actually claimed it in the meantime.
        public void ReassignSplitOffJoycon(Controller jc) {
            AssignPadId(jc, NextAvailablePadId(jc));
        }

        // NextAvailablePadId deliberately skips a joined pair's passive half when computing
        // what's free (it has no virtual controller and doesn't occupy a visible LED slot), so
        // whenever something claims a "next available" number, that number may already be held
        // by a passive half that's still parked on it. Both then share one PadId, which breaks
        // anything that assumes PadId uniquely identifies a controller: BuildSnapshot/
        // RenderSnapshot's pair de-duplication (a colliding record gets mistaken for the pair's
        // already-rendered passive half and silently dropped from the UI - the colliding
        // controller works fine, it just never appears) and remote-mode command routing by PadId.
        // Called after every real PadId change (see AssignPadId) - loops because resolving one
        // collision can, in principle, land on a different pair's stale value in turn.
        //
        // Deliberately bookkeeping-only: unlike AssignPadId, this must NOT call RequestLEDUpdate
        // or touch out_xbox/out_ds4 - the passive half being moved has no virtual controller to
        // recreate, and its physical LED is intentionally left showing its active partner's
        // shared rank (see ReassignPadIds), not its own PadId. Only the in-memory identity moves;
        // nothing observable to the user changes. Two passive halves sharing a stale number is
        // left alone - neither is ever treated as "in use", so it can't cause this symptom.
        void ResolveStalePadIdCollisions() {
            bool changed = true;
            while (changed) {
                changed = false;
                foreach (Joycon v in j) {
                    bool isPassiveHalf = v.other != null && v.other != v && v.out_xbox == null && v.out_ds4 == null;
                    if (!isPassiveHalf)
                        continue;

                    foreach (Joycon other in j) {
                        if (other == v || other.PadId != v.PadId)
                            continue;
                        bool otherIsPassiveHalf = other.other != null && other.other != other && other.out_xbox == null && other.out_ds4 == null;
                        if (otherIsPassiveHalf)
                            continue;

                        int freed = v.PadId;
                        v.PadId = NextAvailablePadId();
                        DebugLog.Write(string.Format(CultureInfo.InvariantCulture,
                            "ResolveStalePadIdCollisions: passive half moved pad {0} -> {1} (collided with pad={2} hasXbox={3} hasDs4={4})",
                            freed, v.PadId, other.PadId, other.out_xbox != null, other.out_ds4 != null));
                        changed = true;
                        break;
                    }
                    if (changed)
                        break;
                }
            }
        }

        // Unconditionally tears down whatever virtual output(s) jc currently has, if any - the
        // primitive every "this controller shouldn't have a virtual controller right now" call
        // site should share instead of hand-rolling its own out_xbox/out_ds4 Disconnect(), per
        // DOCS/CONTROLLERS-REFACTOR.md's virtual-controller-lifecycle section: the auto-join
        // block's loser-destroy logic used to duplicate this inline rather than reusing
        // CreateOutputControllers/AssignPadId's existing pattern, which is exactly the kind of
        // duplication that makes a clean lifecycle-module extraction impossible. Deliberately
        // pairing-ignorant - it destroys whatever it's given, it doesn't decide who's a "loser".
        public void DestroyOutputControllers(Controller jc) {
            if (jc.out_xbox != null) {
                try { jc.out_xbox.Disconnect(); } catch { }
                jc.out_xbox = null;
            }
            if (jc.out_ds4 != null) {
                try { jc.out_ds4.Disconnect(); } catch { }
                jc.out_ds4 = null;
            }
        }

        // Shared by attach, profile changes, AssignPadId, and survivor restoration. Reconciles
        // both directions: changing a profile from Xbox to DS4/Disabled removes the old target,
        // while enabling an output creates and connects the requested target.
        void CreateOutputControllers(Controller jc) {
            string useAs = ControllerMappings.OptionValue(
                ControllerMappings.ProfileIdFor(jc), "UseAs");
            bool useXbox = useAs == ControllerMappings.UseAsXbox360;
            bool useDs4 = useAs == ControllerMappings.UseAsDualShock4;

            if (!useXbox && jc.out_xbox != null) {
                try { jc.out_xbox.Disconnect(); } catch { }
                jc.out_xbox = null;
            }
            if (!useDs4 && jc.out_ds4 != null) {
                try { jc.out_ds4.Disconnect(); } catch { }
                jc.out_ds4 = null;
            }

            if ((useXbox || useDs4) && !Program.EnsureVigemClient())
                return;

            // ReceiveRumble/Ds4_FeedbackReceived aren't promoted to Controller yet (step 4's
            // rumble_obj/SetRumble promotion is a later phase) - is-check rather than a hard
            // dependency, so this stays correct (rumble just isn't wired up) for a future
            // non-Joycon controller until that phase lands.
            Joycon rumbleJc = jc as Joycon;

            if (useXbox && jc.out_xbox == null) {
                jc.out_xbox = new VirtualOutput.OutputControllerXbox360();
                if (rumbleJc != null && Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]))
                    jc.out_xbox.FeedbackReceived += rumbleJc.ReceiveRumble;
                jc.out_xbox.Connect();
            }
            if (useDs4 && jc.out_ds4 == null) {
                jc.out_ds4 = new VirtualOutput.OutputControllerDualShock4();
                if (rumbleJc != null && Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]))
                    jc.out_ds4.FeedbackReceived += rumbleJc.Ds4_FeedbackReceived;
                jc.out_ds4.Connect();
            }
        }
    }
}
