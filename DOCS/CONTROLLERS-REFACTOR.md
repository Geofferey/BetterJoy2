# Controller architecture refactor plan

Status: **planned, not started**. Written after the DualSense baseline work
exposed real structural risk in how `Joycon.cs` shares code across device
types. Nothing in this document has been implemented yet - it's the plan to
review before touching any code.

## Why this is needed

`Joycon.cs` (4595 lines) represents five physical controller types - Joy-Con,
Pro Controller, SNES Controller, N64 Controller, and now DualSense - through
boolean flags (`isPro`, `isSnes`, `is64`, `isDualSense`) on one class, not
separate types. This grew organically: Joy-Con came first, Pro/SNES/N64 were
added as flag variants of it, and DualSense was bolted on the same way when
it was added, on the explicit reasoning (see `DOCS/DUALSENSE.md`) that a
separate class would require generalizing seven other files that reference
`Joycon` by concrete type - too large a refactor for a baseline milestone.

That reasoning was sound for getting DualSense working. It stopped being
sound once DualSense grew past baseline: during stick-calibration and
profile-identity work, a change scoped to DualSense (`PadMacAddress` becoming
a property that invalidated a caching field on every write) leaked into
Joy-Con's own internal MAC-reassignment path inside `Attach()` and broke
Joy-Con auto-join - two Joy-Cons would show combined in the UI while each
kept its own virtual controller. The fix was reverting to a narrower,
explicit invalidation call, but the incident is the actual motivating
problem: **there is no compiler-enforced boundary between "this code is
DualSense-only" and "this code is shared,"** so a correctly-scoped-looking
change can silently affect every device type.

The deeper structural cause, found while investigating: `isPro` is a
**superset flag**, not a category -

```csharp
this.isPro = isPro || isSnes || is64 || isDualSense;   // Joycon.cs:662
```

Every `if (isPro)` branch in the file - and there are dozens, including
`DoThingsWithButtons`, `OwnsGyroMouse`, `CalibrationConfirmPressed`,
`ExtractIMUValues`'s axis-offset selection, and both `MapToXbox360Input` and
`MapToDualShock4Input` - silently also fires for DualSense (and SNES/N64).
This is the real mechanism behind "a DualSense change touched Joy-Con
behavior": DualSense doesn't need to be explicitly checked to be affected by
a change to shared, `isPro`-gated logic.

Separately motivating this: the user wants to add controllers beyond
Nintendo/Sony over time. Doing that by adding a sixth boolean flag to
`Joycon` each time is exactly the pattern that caused this incident, and gets
worse with each addition.

## What must not regress

There are **three** highest-stakes surfaces in this codebase, not one - all
three get the same "re-verify after every step, not just at the end"
treatment, and none is subordinate to the others:

**1. PadId reassignment and Joy-Con auto-join/split.** Confirmed working
again as of this plan, after today's incident. This logic lives in
`Program.cs` (`CleanUp`, `ReassignPadIds`, `AssignPadId`, the auto-join
block) and in `Joycon.cs`'s `other` property and `virtualControllerSequence`
- see "What stays as shared infrastructure" below. Three prior commits
(`fb3dca1`, `3d1c38a`, `156dcf3`) each fixed a real regression in this exact
area before DualSense existed; this refactor must not reopen any of them.

**2. Button/stick mapping to the virtual XInput (ViGEmBus) output** - for
Joy-Con-family (all four Nintendo types) *and* DualSense. This is
`MapToXbox360Input` and everything feeding it: the canonical `Button` enum/
`buttons[20]` array every device's parser populates, `stick`/`stick2`/
`CenterSticks`, and `triggerVal[]` for DualSense's analog triggers. Splitting
device-specific parsers apart must not change what any physical button/
stick/trigger produces on the virtual controller - this is the actual
player-facing output, more visible to the user than any internal state.
**DS4-output emulation (`MapToDualShock4Input`) is explicitly out of scope
for DualSense** - see the note under "Tier 3" below; do not add it, and
don't treat its absence as a bug.

**3. Gyro/IMU calibration and reading** - gyro-mouse, gyro-stick, the
auto-calibration trend-detection system (`DOCS/SMART-AUTO-CALIBRATION.md`),
roll compensation, and orientation handling (vertical/horizontal, solo vs.
paired Joy-Con axis handling). This pipeline is classified as "Tier 1 -
unambiguously shared" below because no device type branches its logic - but
shared-and-complex is not the same as low-risk, and this specific pipeline
has already burned a session: a prior gyro "fix" attempt (roll-compensation
sampling changed from continuous accel-trust-gated tracking to sample-once-
at-activation) was a net negative on real hardware and was fully reverted.
Treat any change that touches `ExtractIMUValues`, the gyro-mouse/gyro-stick
processing methods, `TryAutoCalibrate`, or the AHRS/`cur_rotation`/`gyr_g`
code with the same caution as PadId reassignment - it has a documented
history of "looked correct, broke real behavior," not just a theoretical
risk. The user has additional gyro work already planned (see "Related,
deferred" below); that work depends on this pipeline staying intact through
the refactor, not just on it compiling.

**Verification checklist to run after every step**, not just at the end:

*PadId / auto-join:*
1. Two Joy-Cons connect and auto-join into one logical pair; joy.cpl shows
   one virtual controller, not two.
2. Splitting the pair (double-click / physical gesture) restores two
   independent virtual controllers, each on its own distinct identity.
3. With three controllers connected (players 1/2/3), disconnecting player 2
   compacts player 3 down to player 2's slot without an unplug/replug.
4. Dropping to one controller reads as player 1.
5. A DualSense connecting over USB while already connected over Bluetooth
   still triggers the auto-disconnect-stale-BT-link behavior and resolves to
   one PadId, one virtual controller.

*XInput/ViGEmBus button-stick mapping:*
6. Every physical button on a solo Joy-Con, a joined pair, a Pro Controller,
   an SNES/N64 controller, and a DualSense produces the correct button on
   the virtual Xbox 360 controller in `joy.cpl`'s test page - re-run the
   full per-device button/stick/trigger list from `DOCS/DUALSENSE.md`'s
   verification section for DualSense specifically.
7. DualSense trigger analog range (0-max, not snapping) is preserved on the
   XInput output.
8. No DS4 output target is created for a DualSense (confirms the
   out-of-scope decision above didn't get silently reversed).

*Gyro / IMU:*
9. Gyro-mouse movement direction and sensitivity are unchanged for a solo
   Joy-Con, a joined pair, and a Pro Controller - all three have distinct
   axis-selection code paths (`ExtractIMUValues`'s `isPro`/`isLeft`/solo-mode
   branches) that must each still be reachable and correct.
10. Gyro-to-stick mode (both left-stick and right-stick targets) still
    produces correct axis mapping and deflection limits.
11. Auto-calibration still triggers only on genuine stillness (trend
    detection), converges to a sane center, and does not fire for DualSense
    (`TryAutoCalibrate`'s guard) or contend across multiple connected
    controllers.
12. Roll compensation still uses continuous accel-trust-gated tracking, not
    sample-once-at-activation - re-read `DOCS/SMART-AUTO-CALIBRATION.md`
    and the roll-compensation history before changing anything here.
13. Vertical (self-paired) orientation and horizontal orientation both still
    produce correct gyro-mouse/gyro-stick output.

## Current-state map (from direct investigation, not assumption)

### Coupling to `Joycon` from the rest of the app - wide but shallow

Every external file that references `Joycon` by concrete type -
`Program.cs`, `MainForm.cs`, `Reassign.cs`, `ControllerMappings.cs`,
`HeadlessJoyconHost.cs`, `UpdServer.cs`, `IJoyconHost.cs` - only ever touches
generic, already-computed state: `PadId`, `PadMacAddress`, `state`/`state_`,
`other` (pairing), `battery`, `virtualControllerSequence`, `out_xbox`/
`out_ds4`, `GetButton(Button)`, `SetRumble(...)`, and the kind flags
themselves (`isLeft`/`isPro`/`isSnes`/`is64`/`isDualSense`) for icon/label/
profile-ID selection. None of it calls into Joy-Con protocol internals (SPI
reads, subcommands, calibration-dump parsing) - those stay fully encapsulated
inside `Joycon.cs` already. There is exactly **one** `new Joycon(...)` call
site (`Program.cs:623`).

This means the external coupling is not the hard part of this refactor - it
can realistically be satisfied by an interface/base-class reference instead
of the concrete type, with limited changes to those seven files (mostly:
change a field/parameter type, and replace kind-flag checks with a
`ControllerKind`-style property or pattern match).

One existing pattern already does exactly this kind of abstraction:
`ServiceControlProtocol.cs`'s `ControllerKind` enum (`Left, Right, Pro, Snes,
N64, DualSense` - explicitly append-only, wire-protocol-stable) and
`ControllerRecord` struct, built from a live `Joycon` in
`HeadlessJoyconHost.cs`. Adding a new physical controller type already means
adding one `ControllerKind` value today; this refactor should keep that
pattern, not replace it.

### Inside `Joycon.cs` - three tiers

**Tier 1 - unambiguously shared, device-agnostic (~78% of the file, ~3600
lines).** No device-specific logic; every device type uses this identically:
- The `state_` state machine and `Poll()`/`Begin()` thread lifecycle.
- The canonical `Button` enum and `buttons[20]` array - every device's report
  parser populates this same shape; everything downstream (mapping profiles,
  `MapTo*Input`, the UDP server) is built on it.
- `PadId`, `PadMacAddress`, `out_xbox`/`out_ds4`, the `other` pairing
  property (three-state contract: `null` = solo, `== this` = self-paired
  vertical, `== <other instance>` = real pair).
- The entire gyro-mouse/gyro-stick pipeline (~1200 lines) - structurally
  shared even though DualSense doesn't feed it real data yet.
- The mapping-profile/bind-simulation engine (`MappingValue`,
  `ProfileBoolOption`, `IsComboHeld`, `Simulate*`, ~350 lines).
- `CenterSticks()` and `CommitButtonState()` - already deliberately written
  as shared helpers both Joy-Con's `ProcessButtonsAndStick` and DualSense's
  `ParseDualSenseReport` call into, rather than duplicating.
- Auto-calibration (`TryAutoCalibrate`, ~180 lines) - DualSense opts out via
  one guard at the top; otherwise fully shared.

**Tier 2 - device-specific, near-zero shared code (~330-350 lines for
DualSense; ~630 lines for Joy-Con/Pro/SNES/N64).** Clean split candidates:
- DualSense: `ParseDualSenseReport`, `SendDualSenseRumble`,
  `SendDualSenseLightbar`, the CRC32 helpers, `Attach()`'s DualSense
  early-return branch, `ReceiveRaw()`'s DualSense branch, the raw-dump
  diagnostic logger.
- Joy-Con/Pro/SNES/N64: `Subcommand()`, `ReadSPI()`, the SPI-reading portion
  of `dump_calibration_data()`, the `Rumble` struct's HD-rumble encoding,
  `SendRumble()`, `Getn64StickValues`/`GetNormalizedValue` (N64-only).

**Tier 3 - mostly shared logic with a device-specific tweak spliced in.**
This is the actual danger zone - not the cleanly-separable Tier 2 code, but
methods that are *mostly* shared and have one device-specific branch mixed
into otherwise-generic logic, exactly the shape of today's incident:
- `RetireDuplicateConnections` - generic MAC-based dedup logic for every
  device type, with a DualSense-only Bluetooth-auto-disconnect tail spliced
  into the same method body.
- `MappingValue` - the single most shared method in the class (every bind
  lookup for every device type funnels through it), with a temporary
  DualSense-only diagnostic block inline inside it today.
- `DoThingsWithButtons`, `OwnsGyroMouse`, `CalibrationConfirmPressed`,
  `ExtractIMUValues` - not DualSense-specific themselves, but `isPro`-gated
  in a way that silently includes DualSense via the superset flag.
- `MapToXbox360Input` / `MapToDualShock4Input` - shared output-object
  skeleton, with per-device branches for buttons/sticks/triggers.
  `MapToDualShock4Input` has no `isDualSense` branch at all, so DualSense
  triggers are digital-only on DS4 output. **Explicit product decision (not
  a bug to fix): DualSense does not need DS4-output emulation at all** -
  emulating a DS4 from a controller that already speaks DS4-shaped input
  natively is redundant. `DualSenseController` should only ever implement
  the XInput/ViGEmBus (`MapToXbox360Input`-equivalent) output path; do not
  add DS4-output support for it during this refactor, and don't treat the
  missing branch as something that needs fixing.

### Fields shared by all types but initialized differently per type

The trickiest category - same field, same downstream consumers, different
setup per device:
- `stick_cal`/`stick2_cal`/`deadzone`/`deadzone2`: Joy-Con/Pro from SPI
  flash, SNES/N64 from `App.config`, DualSense hardcoded identity default in
  `Attach()` (no factory source exists). Three init strategies, one
  consuming contract (`CenterSticks`).
- `acc_neutral`/`acc_sensiti`/`gyr_neutral`/`gyr_sensiti`: SPI or config for
  Joy-Con-family, **never initialized at all** for DualSense (fine today
  since nothing reads gyro/accel off a DualSense, but a footgun for future
  gyro support - see "Related, deferred" below).
- `isUSB`: set once at connect time for Joy-Con (placeholder-serial
  heuristic), but re-derived every packet for DualSense based on observed
  report length. Same field, two different "who updates it and when"
  contracts.
- `connection` (transport byte): set once from the *initial* `isUSB` value
  and never updated again - can silently disagree with DualSense's
  per-packet-corrected `isUSB` after a transport switch. **Latent bug,
  independent of this refactor**, worth fixing while touching this code.

## Target architecture

### Shape: abstract base class, not a bare interface

Given how much is Tier 1 (genuinely shared state and behavior, not just a
shared contract), a bare interface would force re-implementing identical
logic in every subclass. An abstract base class carrying all of Tier 1,
with `protected virtual`/`abstract` hooks for Tier 2, fits what's actually
here.

```
Controller (abstract base - most of today's Joycon.cs Tier 1 content)
├── NintendoController (abstract - Subcommand/ReadSPI/SPI calibration plumbing shared
│   │                    by every Nintendo device; today's Tier 2 Joy-Con-family code)
│   ├── JoyconController   (Joy-Con-pair-specific: other/pairing, dual physical units)
│   ├── ProController      (single-unit, dual sticks, no pairing)
│   ├── SnesController     (single-unit, no sticks)
│   └── N64Controller      (single-unit, N64 stick remap)
└── DualSenseController    (new DualSense.cs - today's Tier 2 DualSense code)
```

`NintendoController` exists as an intermediate layer because Joy-Con/Pro/
SNES/N64 genuinely share the SPI/subcommand protocol layer with each other,
just not with DualSense - collapsing them straight into `Controller` would
either duplicate that protocol code four times or force DualSense to
inherit it unused. This mirrors what Tier 2's size comparison already showed
(Joy-Con-family code is ~630 lines of real shared protocol, not incidental
overlap).

### Replacing the `isPro` superset flag: explicit capabilities, not inherited booleans

The root cause of today's incident was a boolean that means "this device
happens to share a code path with Pro Controller," checked in dozens of
places that didn't intend to reason about DualSense at all. Replace it with
explicit capability properties on `Controller`, defaulted per-subclass, that
callers check for what they actually mean:

```csharp
public abstract class Controller {
    public virtual bool SupportsPairing => false;   // only JoyconController overrides true
    public virtual bool HasDualSticks => true;
    public virtual bool HasAnalogTriggers => false;  // DualSenseController overrides true
    public virtual bool HasGyro => false;            // DualSenseController stays false until gyro lands
    public ControllerKind Kind { get; protected set; }  // maps directly to the existing wire-protocol enum
}
```

Every current `if (isPro)` / `if (isSnes)` / `if (is64)` / `if (isDualSense)`
call site gets re-examined against what it's actually testing for (pairing
eligibility? trigger encoding? gyro availability?) and rewritten against the
matching capability - not against a device-identity flag. This is the change
that actually prevents a repeat of today's incident: a future controller type
can only affect behavior it explicitly opts into, not behavior it happens to
inherit through a shared flag.

### What moves into `DualSense.cs`

`DualSenseController : Controller` gets everything already identified as
Tier 2 DualSense-specific: report parsing, rumble/lightbar output, CRC32
helpers, the raw-dump diagnostic logger, calibration-identity seeding. The
`RetireDuplicateConnections` Bluetooth-auto-disconnect tail and the
`MappingValue` diagnostic hook move here too, as overrides/hooks called from
the shared base rather than inline branches in shared methods - directly
fixing the Tier 3 danger-zone pattern that caused today's incident.

### What stays as shared infrastructure (do not move, do not fork)

- `Program.cs`'s `CleanUp`/`ReassignPadIds`/`AssignPadId`/auto-join block -
  already fully generic (verified: contains no `isPro`/`isSnes`/`is64`
  conditionals), works against the base type/interface with no changes to
  its own logic, only to the collection's element type.
- The gyro-mouse/gyro-stick pipeline and mapping-profile engine - stay on
  `Controller` as shared methods. Not worth splitting further right now;
  they're already correctly device-agnostic and splitting them adds risk
  without fixing anything broken.
- `state_`, `Button`, `buttons[20]` - the canonical wire contract every
  subclass's parser must still populate. Do not let any subclass introduce
  its own button representation.
- `ServiceControlProtocol.cs`'s `ControllerKind` enum - keep append-only as
  it already documents itself; map `Controller.Kind` to it directly.

## Modularity for future controller types

The goal stated for this refactor: adding a sixth controller type later
should mean writing one new `XyzController : Controller` (or
`: NintendoController` if it's SPI/subcommand-based) file, registering its
VID/PID at the single `Program.cs:623` construction site, and adding one
`ControllerKind` value - not touching `Joycon.cs`, not risking every other
device type's behavior. Concretely, that means:

1. A `Controller` reference (not `new Joycon(...)`) is what `Program.cs`
   constructs, via a small factory keyed on VID/PID - the one existing
   construction site becomes a dispatch point instead of a single
   constructor call.
2. `Program.j` becomes `ConcurrentList<Controller>` (or an interface if one
   still makes sense once the base class is designed) - a mechanical type
   change across the seven external files, not a logic change, per the
   coupling map above.
3. Every capability a new device type might need (pairing, dual sticks,
   analog triggers, gyro, adaptive triggers, touchpad, lightbar) is a
   virtual property or method on `Controller` with a safe default, not a
   boolean flag callers have to know to check.

## Settings architecture: moving off global App.config toward per-profile settings

A second, related consolidation the user wants done as part of this same
planning effort: BetterJoy currently has **three overlapping config
surfaces**, and the oldest of them keeps accumulating settings that should
never have been global in the first place.

### Current state (found via direct grep, not assumption)

1. **`App.config`/the deployed `.exe.config`**, read via
   `ConfigurationManager.AppSettings[key]` scattered across essentially every
   file (60+ distinct call sites found in `Joycon.cs`, `Program.cs`,
   `MainForm.cs`, `Reassign.cs`, `ControllerMappings.cs`, `UpdServer.cs`,
   `DesktopInputBackend.cs`). `MainForm.cs:99`'s legacy Settings UI edits a
   `displayedConfigKeys`-driven list of these directly.
2. **A separate `settings` file**, read via a distinct `Config` class
   (`Config.Value`, `Config.GetDefaultValue`, `Config.ReloadSettingsOnly`,
   `Config.SaveCaliData`/`SaveStickCaliData`) - used for calibration data and
   as a fallback inside `ControllerMappings.LegacyValue`.
3. **`controller_mappings.xml`**, read/written via `ControllerMappings`
   (`Value`/`OptionValue`/`BoolOption`/`IntOption`, per `profileId`) - the
   modern, per-controller-profile store this whole refactor is built around.

Bridging (2)/(1) into (3): `ControllerMappings.AppConfigBackedKeys` (a fixed
set: `left_click`, `right_click`, `center_click`, `scroll_up`,
`scroll_down`, `clench_gyro`, `ratchet_gyro`) plus `GyroActivationKeys`
(`active_gyro_mouse`, `active_gyro_left_stick`, `active_gyro_right_stick`)
fall back to reading `App.config`/`Config` directly (`LegacyValue`/
`LegacyGyroActivationValue`/`LegacyOptionValue`) whenever a profile doesn't
have its own stored value - i.e. whenever a brand-new profile is created.
This is fragile in exactly the way the user is objecting to: it was the
direct cause of a real bug this session (`GyroToJoyOrMouse`'s stale
`"mouse"` default silently propagating `active_gyro_mouse=always` into every
newly-created profile, contradicting the file's own "Default: none"
comment) and it only covers a handful of keys - every other AppSettings key
below has **no per-profile override path at all today**, global is the only
option.

### The sensitivity/behavior settings that are global today but shouldn't be

Grepping every `ConfigurationManager.AppSettings[...]` call site turns up
roughly 60 keys. The large majority are gyro-mouse/gyro-stick/auto-
calibration tuning values that are read fresh, globally, every time they're
used - meaning **every connected controller, regardless of type or the
user's own per-controller preference, shares identical sensitivity**:
`GyroMouseSensitivityX/Y`, `GyroMouseScreenTraversalDegrees`,
`GyroMouseTighteningThreshold`, `GyroMouseSmoothingTimeMs/Threshold`,
`GyroStickSensitivityX/Y`, `GyroStickReduction`, `GyroStickTiltRangeX/Y`,
`GyroStickHybridRateWeight`, `GyroAnalogSensitivity`,
`GyroMouseRollCompensation`, `GyroMouseDirectCursor`, `GyroMouseScreenWrap`,
`GyroMouseLeftHanded`, `ChangeOrientationDoubleClick`, the `AutoCal*` family
(`AutoCalibrationEnabled`, `AutoCalStillDurationSeconds`,
`AutoCalTrendFraction`, `AutoCalArmDelaySeconds`,
`AutoCalButtonInactivitySeconds`, `AutoCalibrateStickCenter`),
`StickScalingFactor`/`StickScalingFactor2` (directly relevant to this
session's DualSense stick-calibration work), `EnableShakeInput`/
`ShakeInputSensitivity`/`ShakeInputDelay`, `LowFreqRumble`/`HighFreqRumble`/
`EnableRumble`. These belong on `Controller`/per-profile, not global - a
user should be able to want different gyro-mouse sensitivity on their
DualSense than on their Joy-Con, or shake-input on one controller and not
another, without it affecting every other connected device.

A smaller set is genuinely app-wide and correctly belongs in a global
section, not per-controller: `UseHidHide`, `IP`/`Port` (the Cemu motion
server), `AutoAddControllers`/`BlockAutoAddUSB`/`BlockAutoAddBluetooth`,
`PassiveScan`, `DoNotRejoinJoycons`, `HideStatus`, `StartInTray`,
`UnhideOnExit`, `MotionServer`, `UseFakerInput`, `AllowCalibration` (gates
whether the wizard is reachable at all), and the various `*DebugLogging`
toggles (`DualSenseDebugLogging`, `GyroMouseDebugLogging`,
`GyroStickDebugLogging`, `AutoCalDebugLogging`).

`ShowAsXInput`/`ShowAsDS4`/`GyroToJoyOrMouse` are already dead weight once
Default-profile seeding (below) exists - they only exist today as
`LegacyOptionValue`/`LegacyGyroActivationValue` migration sources, not as
settings anyone should edit directly going forward.

**This grep is a starting inventory, not a final classification** - a real
pass through this refactor should re-examine every one of these ~60 keys
individually, not just sort them into the two buckets above by pattern-
matching their names.

### Target design

- **A reserved `Default` profile** (sentinel profile ID, e.g. `"default"` -
  distinct in shape from every real device-derived ID like `pro:xxxxx` or
  `dualsense:xxxxx`, so it can never collide with one) replaces
  `AppConfigBackedKeys`/`LegacyValue`/`LegacyGyroActivationValue`/
  `LegacyOptionValue` entirely. `EnsureProfileSaved`/
  `SnapshotMissingProfileValues` seeds a brand-new profile from the `Default`
  profile's stored values instead of reading `App.config`/`Config`
  key-by-key. Per the user's framing ("if enabled"), this seeding is itself
  a toggle - when off, new profiles fall back to hardcoded in-code defaults
  instead of the user-edited `Default` profile. The `Default` profile is
  editable through the same Controller Profiles UI (`Reassign.cs`) real
  controllers use today - exact UI entry point (a permanent extra row in the
  controller dropdown? a separate button?) is a real design decision to
  settle before implementing, not assumed here.
- **A reserved global-settings profile** (another sentinel ID, e.g.
  `"__global__"`) reuses the exact same storage/persistence/file-watcher/
  `Reload()` machinery `ControllerMappings` already has - not a fourth
  config surface. This holds the app-wide keys listed above. The legacy
  Settings UI (`MainForm.cs`'s `displayedConfigKeys` list) either goes away
  or becomes a thin editor over this profile instead of raw `App.config`.
- **The sensitivity/behavior keys move to being genuine profile `OptionKey`s**
  (like `HomeLEDOn`/`SwapAB`/the `GyroStick*` deflection settings already
  are today) - each controller's own profile can override them, with the
  `Default` profile providing the seed value for new ones. `Controller`
  reads these the same way it already reads `MappingValue`/
  `ProfileBoolOption`/etc. today, not via `ConfigurationManager.AppSettings`
  at the point of use.
- `App.config` itself doesn't disappear entirely - whatever is genuinely
  process-bootstrap config (read once before any profile store could exist,
  e.g. very early startup behavior) can reasonably stay there, but that
  should end up being a small, deliberately-justified remainder, not the
  default assumption for a new setting the way it is today.

### Relationship to the `Controller` class refactor above

These are two separable concerns (class hierarchy vs. settings storage) but
they touch the same surface - every sensitivity read this section moves off
`ConfigurationManager.AppSettings` is a read that would otherwise need to be
re-homed again when `Controller`/`DualSenseController` are extracted.
Sequencing worth considering once both plans are final: doing the
Default-profile/global-settings-profile plumbing in `ControllerMappings.cs`
first (self-contained, lower risk, doesn't touch `Joycon.cs`'s device
branching at all) before or alongside migration step 1 below, so the
capability-property pass and the settings-read migration happen together
per call site instead of touching the same lines twice.

## Suggested migration approach

Given the stakes (PadId reassignment breaking is the specific failure mode
to avoid, and it's already broken once this session), this should be
incremental and independently testable, not a single large rewrite:

1. Introduce the capability properties on the existing `Joycon` class first,
   *without* creating any new class - replace `isPro`/`isSnes`/`is64`/
   `isDualSense` checks at each call site with the matching capability
   check. Verify the full checklist above after this step alone, since it
   touches every `isPro`-gated method in the file.
2. Extract `Controller` as a base class with `Joycon` (renamed or not) as
   its first/only subclass initially - a pure mechanical move of Tier 1
   content, zero behavior change. Verify again.
3. Extract `DualSenseController`/`DualSense.cs` as a second subclass,
   moving Tier 2 DualSense code out of the now-slimmer base. Verify again,
   including the DualSense-specific checklist item.
4. Only then consider splitting Joy-Con/Pro/SNES/N64 apart into their own
   subclasses under `NintendoController` - lower priority, since that
   family isn't where new controller types are expected to land, and it's
   the largest, most interleaved (isLeft/isPro/other) code in the file.
5. Fix the `connection`/`isUSB` staleness bug as part of whichever step
   touches those fields, not as an afterthought. Do **not** add DS4-output
   support to `DualSenseController` - see the explicit decision above.

Each step should be its own commit, buildable and testable independently -
not one large branch merged all at once.

## Related, deferred (not part of this refactor, noted so they aren't lost)

- Gyro support for DualSense - user has specific plans "in my head" not yet
  written down; surface them into this document (or a new one) before
  starting, since `HasGyro`/the acc/gyr-neutral initialization gap above
  directly affects how that gets designed.
- The three near-identical async diagnostic-log-writer implementations
  (`DualSenseRawDumpWriterLoop`, `AutoCalDiagWriterLoop`,
  `GyroStickDiagWriterLoop`) are a good candidate for a single shared
  utility once `Controller` exists to hang it off of - not urgent.
- `DOCS/DUALSENSE.md`'s "out of scope this pass" section is stale (rumble
  has since shipped) - update whenever this refactor touches that area.
