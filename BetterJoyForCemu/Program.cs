using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using BetterJoyForCemu.Collections;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;
using Nefarius.ViGEm.Client;
using static BetterJoyForCemu._3rdPartyControllers;
using static BetterJoyForCemu.HIDapi;

namespace BetterJoyForCemu {
    public partial class JoyconManager {
        public bool EnableIMU = true;
        public bool EnableLocalize = false;

        private const ushort vendor_id = 0x57e;
        private const ushort product_l = 0x2006;
        private const ushort product_r = 0x2007;
        private const ushort product_pro = 0x2009;
        private const ushort product_snes = 0x2017;
        private const ushort product_n64 = 0x2019;

        private const ushort vendor_sony = 0x054C;
        private const ushort product_dualsense = 0x0CE6;
        private const ushort product_dualsense_edge = 0x0DF2;
        // DualShock 4 v2 (CUH-ZCT2x) only - v1 (CUH-ZCT1x, PID 0x05C4) is deliberately not
        // detected here: that PID is identical to vigemDs4ProductId below, ViGEmBus's own default
        // emulated DS4 identity. A real v1 controller would be indistinguishable from BetterJoy's
        // own virtual DS4 output at the VID/PID level with the check below, so it's excluded
        // rather than risk either mis-adding our own output as a new physical controller or
        // silently failing to filter it. Needs a real disambiguator (interface path, bus type, or
        // a verified manufacturer/product string difference) confirmed on real hardware before v1
        // can be added safely.
        private const ushort product_dualshock4_v2 = 0x09CC;

        // ViGEmBus's default emulated identities (CreateXbox360Controller()/CreateDS4Controller()
        // are called with no VID/PID override anywhere in this codebase - see
        // VirtualOutput.OutputControllerXbox360/OutputControllerDualShock4 - so BetterJoy's own
        // virtual output always shows up under these). Windows exposes that virtual pad through a
        // HID interface too, for DirectInput compatibility, which otherwise passes the same
        // generic "is this a gamepad" usage-page/usage check real controllers do - letting
        // AutoAddControllers mistake BetterJoy's own output for a brand new physical controller,
        // whose raw input then just mirrors whatever BetterJoy already sent it, one poll tick
        // later. Checked ahead of the 3rd-party allowlist/auto-add below, not after, so this never
        // becomes a Joycon object in the first place, however AutoAddControllers is configured.
        private const ushort vigemXbox360VendorId = 0x045E;
        private const ushort vigemXbox360ProductId = 0x028E;
        private const ushort vigemDs4VendorId = 0x054C;
        private const ushort vigemDs4ProductId = 0x05C4;

        public ConcurrentList<Controller> j { get; private set; } // Array of all connected controllers
        static JoyconManager instance;

        public IJoyconHost form;

        System.Timers.Timer controllerCheck;

        // Guards a scan pass (CleanUp + CheckForNewControllers) end to end. System.Timers.Timer
        // can fire Elapsed again before a slow pass has finished (HidHide retries, PnP calls),
        // so this also prevents two passes running concurrently against the same controller
        // list/hiddenInstanceIds - not just the shutdown race StopScanning uses it for below.
        readonly object scanLock = new object();

        // Timer.Stop() only blocks FUTURE ticks - an Elapsed callback that already fired and is
        // queued on the ThreadPool, but hasn't reached the scanLock yet, isn't covered by that.
        // Set before the rendezvous below (not inside it) so a callback which acquires scanLock
        // at any point after this is set - whether it was already queued or not - sees it and
        // exits without doing any scan work, instead of racing full teardown.
        volatile bool scanningStopped = false;

        public static JoyconManager Instance {
            get { return instance; }
        }

        public void Awake() {
            instance = this;
            j = new ConcurrentList<Controller>();
            HIDapi.hid_init();
        }

        public void Start() {
            controllerCheck = new System.Timers.Timer(2000); // check for new controllers every 2 seconds
            controllerCheck.Elapsed += CheckForNewControllersTime;
            controllerCheck.Start();
        }

        // Stops future scan passes AND blocks until any pass already in progress (or already
        // queued on the ThreadPool, about to start) has exited without doing further work.
        // controllerCheck.Stop() alone only blocks brand new ticks from firing - a callback that
        // fired just before Stop() but hadn't yet acquired scanLock would find it uncontested
        // and run anyway, racing full teardown (OnApplicationQuit detaching every controller and
        // calling HIDapi.hid_exit()). Setting scanningStopped first closes that: any callback
        // that acquires scanLock after this point - already queued or not - checks the flag
        // before doing anything and exits immediately.
        public void StopScanning() {
            scanningStopped = true;
            controllerCheck?.Stop();
            lock (scanLock) { }
            controllerCheck?.Dispose();
        }

        bool ControllerAlreadyAdded(string path) {
            foreach (Controller v in j)
                if (v.path == path)
                    return true;
            return false;
        }

        public void ApplyControllerProfileOptions() {
            RunExclusiveOfScanning(() => {
                var handledProfiles = new HashSet<string>(StringComparer.Ordinal);
                foreach (Controller jc in j) {
                    if (jc.state == Controller.state_.DROPPED)
                        continue;
                    string profileId = ControllerMappings.ProfileIdFor(jc);
                    ApplyControllerProfileLighting(jc, profileId);
                    if (jc is DualSenseController triggerController) {
                        // Each mode keeps its own Start/Secondary/Strength now (see
                        // ControllerMappings.AdaptiveTriggerFieldValue) - read using whichever
                        // mode is actually configured per side, not a shared flat value.
                        string leftMode = ControllerMappings.OptionValue(profileId, "AdaptiveTriggerModeLeft");
                        string rightMode = ControllerMappings.OptionValue(profileId, "AdaptiveTriggerModeRight");
                        triggerController.SetAdaptiveTriggerProfile(
                            leftMode,
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Left", "Start", leftMode, 30),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Left", "Secondary", leftMode, 70),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Left", "Strength", leftMode, 50),
                            rightMode,
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Right", "Start", rightMode, 30),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Right", "Secondary", rightMode, 70),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Right", "Strength", rightMode, 50));
                    }
                    string audioMode = ControllerMappings.ControllerAudioMode(profileId);
                    bool audioEnabled = audioMode != ControllerMappings.ModeDisable;
                    bool requireHeadphones =
                        audioMode == ControllerMappings.AudioModeRequireHeadphones;
                    int audioVolume = ControllerMappings.IntOption(profileId, "ControllerAudioVolume", 75);
                    // Matches the Bluetooth gating just below: "Require headphones" means no
                    // speaker fallback, not just correct routing once already enabled. DS4/
                    // DualSense both expose HeadphonesConnected but don't share a common base
                    // member for it, hence the per-type check here instead of on jc directly.
                    bool usbHeadphonesSatisfied = !requireHeadphones ||
                        (jc is DualShock4Controller ds4Headphones && ds4Headphones.HeadphonesConnected) ||
                        (jc is DualSenseController dualSenseHeadphones && dualSenseHeadphones.HeadphonesConnected);
                    if (audioEnabled && usbHeadphonesSatisfied)
                        jc.PrepareUsbAudio(audioVolume);
                    // Opt-in alternative to the "set the controller as your Windows default
                    // playback device" flow above - off by default (see ControllerMappings'
                    // "false" default for this key). Target is the controller's own selected
                    // endpoint (ControllerAudioEndpointId, the same one PrepareUsbAudio's test
                    // tone already targets) - empty ("Default") resolves via
                    // UsbAudioEndpointNameHint to the controller's own device rather than the
                    // system default, since looping the system default into itself would be a
                    // feedback loop. Source is left empty for now (the system default render
                    // device), matching Bluetooth capture's own fallback rather than adding a
                    // second endpoint picker for this first pass.
                    bool usbLoopbackEnabled =
                        ControllerMappings.BoolOption(profileId, "ControllerAudioUsbLoopback");
                    if (jc.isUSB && audioEnabled && usbHeadphonesSatisfied && usbLoopbackEnabled)
                        form.StartUsbAudioLoopback(jc.PadId, String.Empty,
                            ControllerMappings.OptionValue(profileId, "ControllerAudioEndpointId"),
                            jc.UsbAudioEndpointNameHint, audioVolume);
                    else
                        form.StopUsbAudioLoopback(jc.PadId);
                    // Bluetooth has no audio-class endpoint to prepare - DualShock4Controller owns
                    // a full continuous capture/encode/stream lifecycle instead (see
                    // StartBluetoothAudioStream). Both calls are idempotent no-ops when already in
                    // the requested state, so re-evaluating this every scan pass is cheap and is
                    // what actually starts the stream once a profile is (re)enabled or the
                    // controller reconnects - mid-stream disconnect is handled separately via
                    // OnDetachingWhileAttached, since this loop only reaches attached controllers.
                    if (!jc.isUSB && jc is DualShock4Controller ds4) {
                        // The physical jack always selects speaker vs. headset output. This option
                        // only controls whether speaker fallback is allowed while the jack is empty.
                        // InputHelper runs one independent capture pipeline per pad (see
                        // BluetoothAudioCapture/InputHelper.cs), so this and DualSense's own Start
                        // below don't contend with each other the way they used to when InputHelper
                        // had only one shared capture slot.
                        if (audioEnabled && (!requireHeadphones || ds4.HeadphonesConnected))
                            ds4.StartBluetoothAudioStream(audioVolume,
                                ControllerMappings.OptionValue(profileId, "ControllerAudioEndpointId"),
                                ds4.HeadphonesConnected);
                        else
                            ds4.StopBluetoothAudioStream();
                    }
                    if (jc is DualSenseController dualSense) {
                        string microphoneMode = ControllerMappings.BluetoothMicrophoneMode(profileId);
                        bool bluetoothMicrophoneEnabled = microphoneMode == ControllerMappings.ModeEnable ||
                            microphoneMode == ControllerMappings.MicrophoneModeStartMuted;
                        // The built-in Bluetooth microphone is independent of the 3.5 mm jack.
                        // Controller audio remains the single opt-in for the physical media
                        // transport; a missing optional virtual-mic backend must not disturb the
                        // existing speaker/headset path.
                        if (!jc.isUSB && audioEnabled && bluetoothMicrophoneEnabled)
                            dualSense.StartBluetoothMicrophone(
                                microphoneMode == ControllerMappings.MicrophoneModeStartMuted);
                        else {
                            dualSense.StopBluetoothMicrophone();
                            // StopBluetoothMicrophone only tears down BetterJoy's own worker/
                            // virtual-mic pipeline - it doesn't touch the controller's own
                            // hardware mute state, so without this, Disabled (or Controller audio
                            // itself off) never actually pushes the mute LED/power-save bit to the
                            // controller at all, leaving whatever state an earlier session left it
                            // in instead of the muted state this branch implies.
                            dualSense.ApplyBluetoothMicrophoneMuteDefault();
                        }
                        // USB's mic is a native USB Audio Class endpoint - no BetterJoy-owned
                        // start/stop lifecycle the way Bluetooth's is, so Disabled can't stop a
                        // worker the way it does there; it hard-mutes the real hardware instead
                        // (see SetMicrophoneMuted/DualSensePowerSaveMicMute) and, like Muted,
                        // still needs applying once per connection. Both Disabled and Muted start
                        // muted - only Enable starts unmuted - the physical button can still
                        // toggle either at runtime. Deliberately independent of audioEnabled/
                        // Controller audio: this should reflect this dropdown's own setting, not
                        // get entangled with a separate master switch's timing.
                        if (jc.isUSB)
                            dualSense.ApplyUsbMicrophoneMuteDefault(
                                microphoneMode != ControllerMappings.ModeEnable);
                        if (!jc.isUSB && audioEnabled &&
                            (!requireHeadphones || dualSense.HeadphonesConnected))
                            dualSense.StartBluetoothAudioStream(audioVolume,
                                ControllerMappings.OptionValue(profileId,
                                    "ControllerAudioEndpointId"),
                                dualSense.HeadphonesConnected);
                        else
                            dualSense.StopBluetoothAudioStream();
                    }
                    if (!handledProfiles.Add(profileId))
                        continue;

                    // Covers identities that only exist after joining (a "pair:" profile) or
                    // self-pairing into vertical - the raw per-controller attach hook only ever
                    // sees the solo identity a controller had the instant it connected. No-op,
                    // no-disk-write once the profile already exists (see EnsureProfileSaved), so
                    // this is safe on every call here, not just the ones that follow a connect.
                    ControllerMappings.EnsureProfileSaved(profileId);

                    bool paired = jc.other != null && jc.other != jc;
                    Controller active = jc;
                    Controller passive = null;
                    if (paired) {
                        bool jcHasOutput = jc.out_xbox != null || jc.out_ds4 != null;
                        bool otherHasOutput = jc.other.out_xbox != null || jc.other.out_ds4 != null;
                        active = jcHasOutput || !otherHasOutput ? jc : jc.other;
                        passive = active == jc ? jc.other : jc;
                    }

                    if (passive != null)
                        DestroyOutputControllers(passive);
                    CreateOutputControllers(active);
                }
                form.RefreshControllerState();
                // The OpenRGB SDK server exposes one fixed device regardless of what's connected
                // (see OpenRgbServer's own comment on why), so there's no device list to notify
                // about here - just resync any controller that's newly eligible (just connected,
                // or Lighting Mode just changed to OpenRGB) to whatever color was last set.
                OpenRgbServer.ApplyCachedColorToEligibleControllers();
            });
        }

        internal static void ApplyControllerProfileLighting(Controller controller,
                                                             string profileId) {
            // While the held color-wheel binding owns the touchpad, its live preview is the
            // lighting source of truth. Reapplying the last persisted profile color from this
            // periodic reconciliation pass would make the lightbar jump backward mid-swipe.
            if (controller.TouchpadColorWheelActive)
                return;

            string lightingMode = ControllerMappings.LightingMode(profileId);
            // Default and OpenRGB both mean BetterJoy never sends a single lighting command for
            // this profile: not the Home LED, RGB lightbar, player indicators, or a
            // toggle_lighting press. This leaves firmware or another application (OpenRGB
            // included) as the sole lighting owner even if the profile's separate Player LED
            // option is enabled.
            if (ControllerMappings.LightingModeIsHandsOff(profileId))
                return;

            // Player LED (the small player-number indicator LEDs, DualSense only) is a separate
            // physical strip with its own Enable/Disable setting in every managed lighting mode.
            if (controller is DualSenseController dualSensePlayerLed)
                dualSensePlayerLed.RequestLEDUpdate(dualSensePlayerLed.PadId);

            controller.SetHomeLight(ControllerMappings.BoolOption(profileId, "HomeLEDOn"));

            byte red, green, blue;
            // LightingOff (toggle_lighting binding) never touches the user's actual LightColor
            // setting or Lighting mode - it's a separate flag applied on top of whichever of those
            // is otherwise in effect, so what was there before is always still there to go back to
            // the instant it's toggled back on, nothing to save/restore. Disabled is the same
            // black output but as a persistent saved mode instead of a runtime toggle.
            if (lightingMode == ControllerMappings.LightingModeDisabled ||
                    ControllerMappings.BoolOption(profileId, "LightingOff")) {
                red = green = blue = 0;
            } else if (lightingMode == ControllerMappings.LightingModeBattery) {
                (red, green, blue) = BatteryLightColor(controller.batteryPercent);
            } else {
                ControllerMappings.GetLightColor(profileId, out red, out green, out blue);
            }
            (red, green, blue) = ControllerMappings.ApplyLightBrightness(
                profileId, red, green, blue);

            // Deliberately unconditional, not deduped up here - both DualSenseController and
            // DualShock4Controller's own SetLightColor already dedupe identical colors
            // internally, but specifically only once their transport is known and no update is
            // still pending, precisely so a stale/pre-transport call doesn't suppress the retry
            // that's needed once it becomes known (DualShock4's own comment calls this the
            // "ordered audio-lane barrier" while audio is active). An earlier version of this
            // method added a second, transport-unaware cache here that looked redundant but
            // wasn't - it could permanently prevent that retry from ever being reached, which
            // broke DualShock4 Bluetooth audio outright. Let each controller's own SetLightColor
            // decide when a resend is actually redundant.
            controller.SetLightColor(red, green, blue);
        }

        // Pure green/yellow/red bands rather than a gradient - deliberately coarse (matches the
        // "update only when the charge crosses into a different band" requirement this exists
        // for) so the lightbar doesn't need a fresh color sent on every small percentage change.
        // Luminosity capped at 15 per channel rather than full 255 - a lightbar at max brightness
        // for something meant to be a subtle, glanceable indicator is uncomfortably bright.
        private const byte BatteryLightLuminosity = 15;

        private static (byte, byte, byte) BatteryLightColor(int batteryPercent) {
            if (batteryPercent <= 33)
                return (BatteryLightLuminosity, 0, 0);
            if (batteryPercent <= 66)
                return (BatteryLightLuminosity, BatteryLightLuminosity, 0);
            return (0, BatteryLightLuminosity, 0);
        }

        void CheckForNewControllersTime(Object source, ElapsedEventArgs e) {
            lock (scanLock) {
                if (scanningStopped)
                    return;

                CleanUp();
                if (Boolean.Parse(ConfigurationManager.AppSettings["PassiveScan"])) {
                    CheckForNewControllers();
                }
            }
        }

        // Lets a caller outside the scan loop (see HeadlessJoyconHost's config-file watcher)
        // mutate state a scan pass reads - e.g. Program.thirdPartyCons/blacklistedCons via
        // _3rdPartyControllers.LoadIntoProgramLists - without racing CheckForNewControllers'
        // own iteration over them. Those are plain Lists, not ConcurrentList, so a Clear()+
        // AddRange() from another thread while a scan enumerates them could throw "Collection
        // was modified" or hand device classification a half-rebuilt list.
        public void RunExclusiveOfScanning(Action action) {
            lock (scanLock) {
                action();
            }
        }

        // Attempts to hide a device via HidHide, retrying a few times (short delay) within this
        // pass since a freshly-plugged-in device's PnP instance can occasionally not be settled
        // yet. Returns true if hidden (or HidHide isn't in use), false if hiding failed after
        // retries - callers decide what that means for them (e.g. skip attaching this pass, or
        // for an already-blacklisted device, nothing further at all).
        private bool TryHideController(hid_device_info enumerate) {
            if (!Program.useHidHide)
                return true;

            string instanceId = null;
            for (int hideAttempt = 0; hideAttempt < 5 && instanceId == null; hideAttempt++) {
                if (hideAttempt > 0)
                    Thread.Sleep(50);

                try {
                    instanceId = PnPDevice.GetInstanceIdFromInterfaceId(enumerate.path);
                    Program.hidHide.AddBlockedInstanceId(instanceId);
                    lock (Program.hiddenInstanceIdsLock) {
                        Program.hiddenInstanceIds.Add(instanceId);
                    }
                } catch {
                    instanceId = null;
                }
            }

            return instanceId != null;
        }

        // Adds or removes jc's own HidHide block after the fact - the one case that needs this is
        // a profile set to ControllerMappings.UseAsPassthrough, which wants its physical device
        // visible to other programs again instead of staying exclusively blocked the way
        // TryHideController leaves every newly-connected controller by default. Safe to call
        // unconditionally from CreateOutputControllers on every pass (attach, profile change,
        // AssignPadId, survivor restoration) - HidHide's block list is idempotent, so reasserting
        // a state that's already correct is a harmless no-op.
        private void ReconcileHidHideForController(Controller jc, bool wantHidden) {
            if (!Program.useHidHide || Program.hidHide == null || String.IsNullOrEmpty(jc.path))
                return;

            string instanceId;
            try {
                instanceId = PnPDevice.GetInstanceIdFromInterfaceId(jc.path);
            } catch {
                return;
            }

            try {
                if (wantHidden) {
                    Program.hidHide.AddBlockedInstanceId(instanceId);
                    lock (Program.hiddenInstanceIdsLock) {
                        if (!Program.hiddenInstanceIds.Contains(instanceId))
                            Program.hiddenInstanceIds.Add(instanceId);
                    }
                } else {
                    bool wasHidden;
                    lock (Program.hiddenInstanceIdsLock) {
                        wasHidden = Program.hiddenInstanceIds.Remove(instanceId);
                    }
                    Program.hidHide.RemoveBlockedInstanceId(instanceId);
                    // Only a real hidden-to-visible transition means OpenRGB's raw HID access
                    // just changed - reasserting an already-visible device (the common,
                    // idempotent reconciliation pass) has nothing new for it to see.
                    if (wasHidden && ControllerMappings.LightingMode(
                            ControllerMappings.ProfileIdFor(jc)) ==
                            ControllerMappings.LightingModeOpenRgb) {
                        DebugLog.Write("OpenRgbRescan: triggered from HidHide hidden->visible, instanceId=" + instanceId);
                        OpenRgbRescan.RequestRescan();
                    }
                }
            } catch { }
        }

        // A joined Joy-Con pair is two separate physical devices sharing one profile/logical
        // controller - passthrough (or re-hiding out of it) needs to reach both halves, not just
        // whichever one currently holds the ViGEm/VIIPER target.
        private void ReconcileHidHideForProfile(Controller jc, bool wantHidden) {
            ReconcileHidHideForController(jc, wantHidden);
            if (jc.other != null && jc.other != jc)
                ReconcileHidHideForController(jc.other, wantHidden);
        }

        // Reads the DualSense's own Bluetooth MAC address over USB via HID feature report 0x09
        // ("pairing info"). hidapi's hid_get_feature_report convention: caller sets buf[0] to the
        // report ID before the call: the returned buffer still has the report ID at index 0
        // followed by the report body, and the return value counts that leading byte. Report
        // layout confirmed against the Linux kernel's hid-playstation.c driver
        // (DS_FEATURE_REPORT_PAIRING_INFO, size 20, MAC at buf[1..6]).
        private const byte DualSensePairingInfoReportId = 0x09;
        private const int DualSensePairingInfoReportSize = 20;

        private static bool TryGetDualSenseMac(IntPtr handle, byte[] mac) {
            byte[] buf = new byte[DualSensePairingInfoReportSize];
            buf[0] = DualSensePairingInfoReportId;
            int ret = HIDapi.hid_get_feature_report(handle, buf, new UIntPtr((uint)buf.Length));
            if (ret < 7)
                return false;

            // Confirmed on real hardware: the feature report's 6 MAC bytes (buf[1..6]) are the
            // exact byte-reversal of what the same physical controller reports as its
            // serial_number when paired over Bluetooth - reverse here so both transports resolve
            // to the same PadMacAddress/profile identity.
            for (int i = 0; i < 6; i++)
                mac[i] = buf[6 - i];
            return true;
        }

        // Walks up the PnP device tree from a HID interface to find the underlying bus (USB or
        // Bluetooth) it's actually connected through. GetInstanceIdFromInterfaceId only resolves
        // to the HID-level device node itself (always prefixed "HID\...", for either transport),
        // not its parent bus device, so checking that string directly never actually
        // distinguishes USB from Bluetooth - the real answer is a few levels up the tree.
        private static void GetControllerTransport(string hidPath, out bool isUsb, out bool isBluetooth) {
            isUsb = false;
            isBluetooth = false;

            IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(hidPath, DeviceLocationFlags.Normal);
            for (int depth = 0; device != null && depth < 8; depth++) {
                if (device.InstanceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) {
                    isUsb = true;
                    return;
                }
                if (device.InstanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) {
                    isBluetooth = true;
                    return;
                }
                device = device.Parent;
            }
        }

        // See this method's call site for why VID/PID alone can't identify VIIPER's own virtual
        // DualSense output. Walks the same PnP parent chain GetControllerTransport does, looking
        // for usbip-win2's virtual host controller hardware ID instead of a real USB/Bluetooth
        // bus - present on every device VIIPER creates, never on a real physical controller.
        private const string ViiperVirtualBusHardwareId = "ROOT\\USBIP_WIN2\\UDE";

        private static bool IsUnderViiperVirtualBus(string hidPath) {
            // Same retry-with-short-delay shape as TryHideController just above, and for the same
            // reason: a device this fresh (this runs on the very first scan pass after VIIPER
            // creates it) can have a PnP instance/parent chain that isn't fully populated yet.
            // Without retrying here, that first-pass failure fell through to the real-controller
            // path and got it HidHidden before a later, successful pass ever got a chance to
            // exclude it - confirmed on real hardware: hidden exactly once, only on first
            // creation, never again after a manual unhide.
            for (int attempt = 0; attempt < 5; attempt++) {
                if (attempt > 0)
                    Thread.Sleep(50);

                try {
                    IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(hidPath, DeviceLocationFlags.Normal);
                    for (int depth = 0; device != null && depth < 8; depth++) {
                        if (device.HardwareIds != null && device.HardwareIds.Any(id =>
                                String.Equals(id, ViiperVirtualBusHardwareId, StringComparison.OrdinalIgnoreCase)))
                            return true;
                        device = device.Parent;
                    }
                    return false;
                } catch {
                    // Not settled yet - fall through and retry rather than treating this as a
                    // real controller on the strength of one failed attempt.
                }
            }
            return false;
        }

        private ushort TypeToProdId(byte type) {
            switch (type) {
                case 1:
                    return product_pro;
                case 2:
                    return product_l;
                case 3:
                    return product_r;
            }
            return 0;
        }

        public void CheckForNewControllers() {
            // move all code for initializing devices here and well as the initial code from Start()
            bool isLeft = false;
            IntPtr ptr = HIDapi.hid_enumerate(0x0, 0x0);
            IntPtr top_ptr = ptr;

            hid_device_info enumerate; // Add device to list
            bool foundNew = false;
            while (ptr != IntPtr.Zero) {
                SController thirdParty = null;
                enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));

                if (enumerate.serial_number == null) {
                    ptr = enumerate.next; // can't believe it took me this long to figure out why USB connections used up so much CPU.
                                          // it was getting stuck in an inf loop here!
                    continue;
                }

                // BetterJoy's own virtual output, mistaken for a brand new physical controller -
                // see the constants above. Unlike the blacklist case below, deliberately NOT
                // hidden via HidHide: other programs (games, Steam) are supposed to see this as a
                // normal Xbox360/DS4 controller, that's the entire point of it existing.
                if ((enumerate.vendor_id == vigemXbox360VendorId && enumerate.product_id == vigemXbox360ProductId) ||
                    (enumerate.vendor_id == vigemDs4VendorId && enumerate.product_id == vigemDs4ProductId)) {
                    ptr = enumerate.next;
                    continue;
                }

                // Same idea, for OutputControllerDualSenseViiper's own virtual output: unlike
                // ViGEmBus's Xbox360/DS4 targets above, VIIPER's DualSense device has no separate
                // "this is obviously virtual" VID/PID to check - it deliberately reports Sony's
                // real DualSense VID/PID (0x054C/0x0CE6), the same ones a genuine physical
                // DualSense uses, since that's what makes games/Windows actually recognize it as
                // one. VID/PID alone can't tell the two apart, so this checks where the device
                // actually lives instead: usbip-win2's virtual USB host controller registers with
                // hardware ID ROOT\USBIP_WIN2\UDE (confirmed via Get-PnpDeviceProperty on the live
                // driver), which a real physical controller - USB or Bluetooth - never sits under.
                if (enumerate.vendor_id == vendor_sony && enumerate.product_id == product_dualsense &&
                        IsUnderViiperVirtualBus(enumerate.path)) {
                    ptr = enumerate.next;
                    continue;
                }

                // Blacklisted devices (set from the Add Controllers dialog - e.g. a 3rd-party
                // controller that identifies differently over USB vs Bluetooth, where only one
                // of those identities should be usable) are skipped unconditionally, ahead of
                // both the manual custom-controller match and auto-add below. Still hide it from
                // other programs, though - the same physical controller may be reachable through
                // this identity from another program (e.g. Steam) while BetterJoy uses a
                // different transport/identity for it, which would otherwise let that other
                // program read duplicate raw input alongside BetterJoy's own virtual output.
                bool isBlacklisted = false;
                foreach (SController v in Program.blacklistedCons) {
                    if (enumerate.vendor_id == v.vendor_id && enumerate.product_id == v.product_id && enumerate.serial_number == v.serial_number) {
                        isBlacklisted = true;
                        break;
                    }
                }
                if (isBlacklisted) {
                    TryHideController(enumerate);
                    ptr = enumerate.next;
                    continue;
                }

                // Checked as a known, exactly-identified device ahead of the generic 3rd-party
                // allowlist/auto-add below, not folded into it - that path can only guess a
                // Nintendo shape (SController.type: Pro/Left Joy-Con/Right Joy-Con), which a
                // DualSense isn't. A DualSense connected before this VID/PID check existed may
                // already have a stale guessed entry in Program.thirdPartyCons (type=1/"Pro") -
                // the loop below must not be allowed to overwrite thirdParty with that guess for
                // a device we now identify definitively, or prod_id resolves to product_pro
                // instead of the real DualSense PID and every isDualSense branch silently never
                // engages.
                bool isDualSenseDevice = enumerate.vendor_id == vendor_sony &&
                    (enumerate.product_id == product_dualsense || enumerate.product_id == product_dualsense_edge);
                bool isDualShock4Device = enumerate.vendor_id == vendor_sony &&
                    enumerate.product_id == product_dualshock4_v2;
                bool validController = isDualSenseDevice || isDualShock4Device ||
                    ((enumerate.product_id == product_l || enumerate.product_id == product_r ||
                      enumerate.product_id == product_pro || enumerate.product_id == product_snes || enumerate.product_id == product_n64) && enumerate.vendor_id == vendor_id);
                // check list of custom controllers specified
                foreach (SController v in (isDualSenseDevice || isDualShock4Device) ? Enumerable.Empty<SController>() : Program.thirdPartyCons) {
                    if (enumerate.vendor_id == v.vendor_id && enumerate.product_id == v.product_id && enumerate.serial_number == v.serial_number) {
                        validController = true;
                        thirdParty = v;
                        break;
                    }
                }

                // auto-detect and register new 3rd-party controllers instead of requiring manual setup
                if (!validController && Boolean.Parse(ConfigurationManager.AppSettings["AutoAddControllers"]) && IsGameController(enumerate)) {
                    bool blockedByTransport = false;
                    if (Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddUSB"]) || Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddBluetooth"])) {
                        try {
                            GetControllerTransport(enumerate.path, out bool isUsbDevice, out bool isBluetoothDevice);
                            blockedByTransport = (isUsbDevice && Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddUSB"])) ||
                                                  (isBluetoothDevice && Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddBluetooth"]));
                        } catch {
                            // Can't determine transport - fall through and allow auto-add rather
                            // than silently blocking a device we couldn't actually classify.
                        }
                    }

                    if (!blockedByTransport) {
                        thirdParty = new SController(BuildDeviceName(enumerate), enumerate.vendor_id, enumerate.product_id, GuessType(enumerate), enumerate.serial_number);
                        Program.thirdPartyCons.Add(thirdParty);
                        _3rdPartyControllers.PersistCustomController(thirdParty);
                        validController = true;
                        form.AppendTextBox("Auto-added new controller: " + thirdParty + "\r\n");
                    } else {
                        // Same reasoning as the blacklist case above: BetterJoy won't use this
                        // device, but the same physical controller may still be reachable
                        // through it from another program (e.g. Steam) over the transport we're
                        // not using, so hide it from other programs anyway.
                        TryHideController(enumerate);
                    }
                }

                ushort prod_id = thirdParty == null ? enumerate.product_id : TypeToProdId(thirdParty.type);
                if (prod_id == 0) {
                    ptr = enumerate.next; // controller was not assigned a type, but advance ptr anyway
                    continue;
                }

                if (validController && !ControllerAlreadyAdded(enumerate.path)) {
                    switch (prod_id) {
                        case product_l:
                            isLeft = true;
                            form.AppendTextBox("Left Joy-Con connected.\r\n"); break;
                        case product_r:
                            isLeft = false;
                            form.AppendTextBox("Right Joy-Con connected.\r\n"); break;
                        case product_pro:
                            isLeft = true;
                            form.AppendTextBox("Pro controller connected.\r\n"); break;
                        case product_snes:
                            isLeft = true;
                            form.AppendTextBox("SNES controller connected.\r\n"); break;
                        case product_n64:
                            isLeft = true;
                            form.AppendTextBox("N64 controller connected.\r\n"); break;
                        case product_dualsense:
                        case product_dualsense_edge:
                            isLeft = true;
                            form.AppendTextBox("DualSense controller connected.\r\n"); break;
                        case product_dualshock4_v2:
                            isLeft = true;
                            form.AppendTextBox("DualShock 4 controller connected.\r\n"); break;
                        default:
                            form.AppendTextBox("Non Joy-Con Nintendo input device skipped.\r\n"); break;
                    }

                    // Hide this controller (Joycon, Pro, SNES, or N64 - all share this same
                    // connect path) from other programs (e.g. Steam) via HidHide, before opening/
                    // attaching it ourselves below. If it's still not ready to hide after a few
                    // retries, skip attaching it this pass entirely (rather than falling through
                    // and opening it unhidden) so other programs can't grab the raw device and
                    // end up double-processing input alongside our virtual output. The next
                    // periodic scan (2s later) retries again from there.
                    if (!TryHideController(enumerate)) {
                        form.AppendTextBox("Controller not ready to hide yet, will retry.\r\n");
                        ptr = enumerate.next;
                        continue;
                    }
                    // -------------------- //

                    // hid_open_path returns a null handle (rather than throwing) when it can't
                    // open the device - already exclusively held by another process racing for
                    // the same physical device, momentarily unavailable mid-HidHide-toggle, etc.
                    // Passing that straight into hid_set_nonblocking used to crash the whole
                    // process with an unrecoverable AccessViolationException: native access
                    // violations are a "corrupted state exception" the CLR deliberately lets
                    // bypass ordinary try/catch (since .NET 4.0), so the catch here never
                    // actually protected against this - validate the handle first instead.
                    IntPtr handle = HIDapi.hid_open_path(enumerate.path);
                    if (handle == IntPtr.Zero) {
                        form.AppendTextBox("Unable to open path to device - are you using the correct (64 vs 32-bit) version for your PC?\r\n");
                        break;
                    }
                    HIDapi.hid_set_nonblocking(handle, 1);

                    bool isPro = prod_id == product_pro;
                    bool isSnes = prod_id == product_snes;
                    bool is64 = prod_id == product_n64;
                    bool isDualSense = prod_id == product_dualsense || prod_id == product_dualsense_edge;
                    bool isDualShock4 = prod_id == product_dualshock4_v2;
                    // j.Count (list size, not a stable slot) duplicates an existing PadId the
                    // moment a middle controller disconnects and a new one connects afterward -
                    // e.g. with PadIds 0/1/2 connected, 1 drops, the next new controller would
                    // also get j.Count == 2, colliding with the controller still holding it.
                    // Remote-mode commands (TestRumble/JoinOrSplit/StartCalibration) resolve a
                    // controller by PadId alone, so a collision could route a command to the
                    // wrong physical controller, not just misrender a GUI slot.
                    if (isDualSense) {
                        j.Add(new DualSenseController(handle, enumerate.path, enumerate.serial_number, NextAvailablePadId()));
                    } else if (isDualShock4) {
                        j.Add(new DualShock4Controller(handle, enumerate.path, enumerate.serial_number, NextAvailablePadId()));
                    } else if (isSnes) {
                        j.Add(new SnesController(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, enumerate.path, enumerate.serial_number, NextAvailablePadId(), thirdParty != null));
                    } else if (is64) {
                        j.Add(new N64Controller(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, enumerate.path, enumerate.serial_number, NextAvailablePadId(), thirdParty != null));
                    } else if (isPro) {
                        j.Add(new ProController(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, enumerate.path, enumerate.serial_number, NextAvailablePadId(), thirdParty != null));
                    } else {
                        j.Add(new JoyconController(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, isLeft, enumerate.path, enumerate.serial_number, NextAvailablePadId(), thirdParty != null));
                    }
                    DumpState("Connect: new controller added, pad=" + j.Last().PadId.ToString(CultureInfo.InvariantCulture));
                    ResolveStalePadIdCollisions();
                    DumpState("Connect: after ResolveStalePadIdCollisions");

                    foundNew = true;
                    j.Last().form = form;
                    form.AssignSlot(j.Last());

                    byte[] mac = new byte[6];
                    bool macParsed = false;
                    string macSource = "serial";
                    try {
                        for (int n = 0; n < 6; n++)
                            mac[n] = byte.Parse(enumerate.serial_number.Substring(n * 2, 2), System.Globalization.NumberStyles.HexNumber);
                        macParsed = true;
                    } catch (Exception) {
                        // could not parse mac address
                    }
                    if (!macParsed) {
                        // A device whose serial_number doesn't parse as a MAC (confirmed on real
                        // hardware: a wired DualSense reports an empty serial_number over USB)
                        // would otherwise silently leave every byte at 0 - a fixed, shared value
                        // ANY other device hitting this same fallback would also get, which
                        // RetireDuplicateConnections (PadMacAddress.Equals) would then treat as
                        // the same physical controller and wrongly drop one of them.
                        //
                        // For a DualSense specifically this has a real fix, not just a workaround:
                        // the controller exposes its own Bluetooth MAC address to the host over
                        // USB via HID feature report 0x09 ("pairing info", 20 bytes, MAC at bytes
                        // 1-6 - confirmed against the Linux kernel's hid-playstation.c driver,
                        // DS_FEATURE_REPORT_PAIRING_INFO). That's the actual hardware identity, not
                        // a synthesized stand-in - stable across app restarts AND identical to what
                        // the same controller reports as its serial_number when paired over
                        // Bluetooth, so USB and BT connections of the same physical unit now
                        // resolve to the same profile.
                        if (isDualSense && TryGetDualSenseMac(handle, mac)) {
                            macParsed = true;
                            macSource = "dualsense-feature-report";
                        } else {
                            macSource = "path-hash";
                            // Fallback for anything else that reaches here (or if the feature
                            // report read fails): enumerate.path is OS-assigned and unique per
                            // physical device instance, so hash that into the 6 bytes instead -
                            // not a real MAC, but guaranteed not to collide with an unrelated
                            // device the way an untouched all-zero default could. MUST be
                            // deterministic across app runs, not just within one - a real
                            // regression found on real hardware: string.GetHashCode() is
                            // randomized per-process by default in .NET Framework (a security
                            // mitigation), so hashing with it produced a DIFFERENT PadMacAddress
                            // for the same physical device on every restart, which
                            // ControllerMappings.DeviceId/DeviceSuffix (using PadMacAddress as
                            // profile identity) turned into a new profile each launch. FNV-1a is a
                            // plain, non-cryptographic string hash with no such randomization.
                            uint hash = 2166136261;
                            foreach (byte pb in Encoding.UTF8.GetBytes(enumerate.path ?? enumerate.serial_number ?? String.Empty)) {
                                hash ^= pb;
                                hash *= 16777619;
                            }
                            byte[] hashBytes = BitConverter.GetBytes(hash);
                            Array.Copy(hashBytes, 0, mac, 0, Math.Min(hashBytes.Length, mac.Length));
                        }
                    }
                    if (isDualSense) {
                        // Comparing the resolved MAC across USB vs Bluetooth connections of the
                        // same physical DualSense, since real hardware testing found they don't
                        // currently match. Gated behind DualSenseDebugLogging (see
                        // LogDualSenseRawDump) - file-only, never the GUI panel.
                        (j.Last() as DualSenseController)?.LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                            "DualSense MAC resolved: {0} (source={1}, serial=\"{2}\")",
                            BitConverter.ToString(mac).Replace("-", ""), macSource, enumerate.serial_number));
                    }
                    j[j.Count - 1].PadMacAddress = new PhysicalAddress(mac);
                    j[j.Count - 1].InvalidateMappingProfileCache();
                }

                ptr = enumerate.next;
            }

            HIDapi.hid_free_enumeration(top_ptr);

            // Connect/attach every newly-found device BEFORE auto-join runs below. Auto-join's
            // pairing (Joycon.other's setter) sends a player-LED subcommand immediately, which
            // needs the physical controller to have already been through its handshake (Attach)
            // to respond correctly. Pairing a controller that just connected this same pass (not
            // yet attached) with one left over from an earlier pass (already attached) used to
            // send that LED command before the fresh one was ready for it - silently dropped, or
            // leaving its protocol state confused. Reliably reproduced: whichever Joycon happened
            // to connect second kept showing its own solo player number instead of the pair's,
            // and the pair's virtual controller didn't actually work in games afterward even
            // though it looked fine in joy.cpl.
            foreach (Controller jc in j) { // Connect device straight away
                if (jc.state == Controller.state_.NOT_ATTACHED) {
                    try {
                        jc.Attach();
                    } catch (Exception) {
                        jc.state = Controller.state_.DROPPED;
                        continue;
                    }

                    // USB enumeration exposes a shared placeholder serial; Attach resolves the
                    // controller's real MAC. Give service-mode clients a fresh profile identity
                    // before button polling begins, rather than leaving the dropdown on the
                    // temporary HID-path identity sent when the slot was first discovered.
                    form.RefreshControllerState();

                    CreateOutputControllers(jc);
                    string profileId = ControllerMappings.ProfileIdFor(jc);
                    ApplyControllerProfileLighting(jc, profileId);
                    ControllerMappings.EnsureProfileSaved(profileId);
                    // Fresh connection: OpenRGB's own device list won't have this controller yet
                    // unless it happens to already be running a scan - nudge it once it's had a
                    // moment to see the HID device (OpenRgbRescan's own settle delay).
                    if (ControllerMappings.LightingMode(profileId) ==
                            ControllerMappings.LightingModeOpenRgb) {
                        DebugLog.Write("OpenRgbRescan: triggered from Attach, profileId=" + profileId);
                        OpenRgbRescan.RequestRescan();
                    }

                    jc.Begin();
                    if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                        jc.getActiveData();
                    }
                }
            }

            if (foundNew && !Boolean.Parse(ConfigurationManager.AppSettings["DoNotRejoinJoycons"])) { // attempt to auto join-up joycons on connection
                JoyconController temp = null;
                // Pairing is Joy-Con-only (see Controller.other's comment) - the auto-join
                // decision itself stays JoyconController-typed/filtered here rather than moving
                // to the generic lifecycle module, per DOCS/CONTROLLERS-REFACTOR.md's design.
                foreach (Controller vBase in j) {
                    // Do not attach two controllers if they are either:
                    // - Not a JoyconController, or one that doesn't support pairing
                    // - Already attached to another one (that isn't itself)
                    if (!(vBase is JoyconController v) || !v.SupportsPairing || (v.other != null && v.other != v)) {
                        continue;
                    }

                    // Otherwise, iterate through and find the controller with the lowest
                    // id that has not been attached already (Does not include self)
                    if (temp == null)
                        temp = v;
                    else if (temp.isLeft != v.isLeft && v.other == null) {
                        temp.other = v;
                        v.other = temp;

                        // Disconnect whichever controller was created later - see Joycon.
                        // virtualControllerSequence - not just whichever happened to be found
                        // first in this iteration (usually but not guaranteed to be the older
                        // one). Its controller was just Connect()ed moments ago by the "connect
                        // device straight away" loop above, so this is a real disconnect, not a
                        // no-op - matches a real unplug (clean, no leftover state); the other one
                        // is left untouched as the pair's shared controller. The loser DECISION is
                        // pairing-specific and stays here (Joy-Con is the only type that pairs at
                        // all - see DOCS/CONTROLLERS-REFACTOR.md's virtual-controller-lifecycle
                        // section), but the actual destroy goes through the same pairing-ignorant
                        // primitive AssignPadId/ApplyControllerProfileOptions use, not a duplicate.
                        JoyconController loser = temp.virtualControllerSequence > v.virtualControllerSequence ? temp : v;
                        DestroyOutputControllers(loser);

                        JoyconController left = temp.isLeft ? temp : v;
                        JoyconController right = temp.isLeft ? v : temp;
                        form.CollapseJoinedPair(left, right);
                        DumpState("AutoJoin: paired");

                        temp = null;    // repeat
                    }
                }

                // Anything still solo after the join pass above (no available opposite-handed
                // partner) self-pairs into vertical orientation if that profile's saved default
                // says to - matching what a manual double right-click/ForceSelfPair would do,
                // just automatically on connect. Profile lookup uses this controller's still-solo
                // identity (ProfileIdFor), since that's the identity it had before this decision.
                foreach (Controller vBase in j) {
                    if (!(vBase is JoyconController v) || !v.SupportsPairing || v.other != null)
                        continue;
                    string profileId = ControllerMappings.ProfileIdFor(v);
                    if (ControllerMappings.OptionValue(profileId, "DefaultOrientation") ==
                        ControllerMappings.OrientationVertical) {
                        v.other = v;
                        form.RefreshOrientationIcon(v);
                    }
                }

                DumpState("AutoJoin: end of pass");
            }

            // Not Joy-Con-specific and not conditional on DoNotRejoinJoycons. This is the attach
            // path that starts saved controller behavior—including DS4 Bluetooth audio—without
            // requiring the user to toggle an option after every connection. Reconciliation is
            // idempotent and also picks up jack-state changes on periodic scan passes.
            ApplyControllerProfileOptions();
        }

        public void OnApplicationQuit() {
            foreach (Controller v in j) {
                if (ControllerMappings.BoolOption(ControllerMappings.ProfileIdFor(v), "AutoPowerOff"))
                    v.PowerOff();

                form.StopUsbAudioLoopback(v.PadId);
                v.Detach();

                // A target that was created but never actually got plugged in (or was already
                // unplugged) throws VigemTargetNotPluggedInException on Disconnect() - same
                // "wasn't connected in the first place" case already guarded against elsewhere
                // (see the auto-join block above), just missed here. Left unhandled, this was
                // surfacing as a failed/hung service stop (DeferredStop propagating the
                // exception back to the SCM) rather than a clean shutdown.
                if (v.out_xbox != null) {
                    try { v.out_xbox.Disconnect(); } catch { }
                }

                if (v.out_ds4 != null) {
                    try { v.out_ds4.Disconnect(); } catch { }
                }

                if (v.out_dualsense != null) {
                    try { v.out_dualsense.Disconnect(); } catch { }
                }
            }

            StopScanning();
            HIDapi.hid_exit();
        }
    }

    class Program {
        public static PhysicalAddress btMAC = new PhysicalAddress(new byte[] { 0, 0, 0, 0, 0, 0 });
        public static UdpServer server;

        public static ViGEmClient emClient;
        private static readonly object emClientLock = new object();

        // One libVIIPER USB server for the whole process, mirroring emClient above - every
        // VIIPER-backed virtual controller (regardless of profile) shares this single server and
        // gets its own bus/device on it via OutputControllerXbox360Viiper. UIntPtr.Zero is never a
        // real handle (libVIIPER's cgo.Handle values start at 1), so it doubles as "not created".
        public static UIntPtr viiperServerHandle = UIntPtr.Zero;
        private static readonly object viiperServerLock = new object();
        private static readonly VirtualOutput.LibViiper.LogCallback viiperLogCallback = ViiperLog;

        private static void ViiperLog(VirtualOutput.LibViiper.LogLevel level, string message) {
            if (level >= VirtualOutput.LibViiper.LogLevel.Warn)
                form?.AppendTextBox("VIIPER: " + message + "\r\n");
        }

        public static bool EnsureViiperServer() {
            if (viiperServerHandle != UIntPtr.Zero)
                return true;
            lock (viiperServerLock) {
                if (viiperServerHandle != UIntPtr.Zero)
                    return true;
                try {
                    var config = new VirtualOutput.LibViiper.ServerConfig { Addr = "localhost:0" };
                    if (VirtualOutput.LibViiper.NewUSBServer(
                            ref config, out UIntPtr handle, viiperLogCallback)) {
                        viiperServerHandle = handle;
                        return true;
                    }
                } catch (DllNotFoundException) {
                    // libVIIPER.dll missing or usbip-win2 not installed - same "tell the user,
                    // don't crash" handling as EnsureVigemClient's VigemBusNotFoundException.
                }
                form?.AppendTextBox(
                    "Could not start the VIIPER virtual USB server. Make sure the VIIPER/" +
                    "usbip-win2 drivers are installed correctly.\r\n");
                return false;
            }
        }

        public static JoyconManager mgr;

        static IJoyconHost form;

        // Lets a non-GUI host (BetterJoyService, running headless with no MainForm at all) wire
        // itself in before calling Start(). GUI mode sets this itself via Main() below.
        public static void SetHost(IJoyconHost host) {
            form = host;
        }

        static public bool useHidHide = Boolean.Parse(ConfigurationManager.AppSettings["UseHidHide"]);
        public static IHidHideControlService hidHide;
        public static readonly List<string> hiddenInstanceIds = new List<string>();

        // Guards hiddenInstanceIds - a plain List<string> accessed from both the scan timer's
        // background thread (JoyconManager.TryHideController, adding) and Stop() below
        // (enumerating + clearing) on whatever thread calls it. Stopping the scan timer first
        // (see StopScanning) closes most of the window but doesn't wait for a callback already
        // in flight to finish - this lock is what actually guarantees no concurrent mutation.
        public static readonly object hiddenInstanceIdsLock = new object();

        public static List<SController> thirdPartyCons = new List<SController>();
        public static List<SController> blacklistedCons = new List<SController>();

        private static WindowsInput.Events.Sources.IKeyboardEventSource keyboard;
        private static WindowsInput.Events.Sources.IMouseEventSource mouse;

        public static bool EnsureVigemClient() {
            if (emClient != null)
                return true;
            lock (emClientLock) {
                if (emClient != null)
                    return true;
                try {
                    emClient = new ViGEmClient();
                    return true;
                } catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException) {
                    form?.AppendTextBox(
                        "Could not start VigemBus. Make sure drivers are installed correctly.\r\n");
                    return false;
                }
            }
        }

        public static void Start() {
            // Previously only ever called from MainForm_Load, so a Windows Service (which never
            // constructs a MainForm) silently never loaded remap keybinds (capture/home/sl_*/
            // sr_*/shake/reset_mouse/legacy active_gyro) at all - every Config.Value(...) lookup for
            // them returned "" under service mode. Moved here so both modes get it.
            Config.Init(CalibrationState.CaliData, CalibrationState.StickCaliData, CalibrationState.Stick2CaliData);

            if (useHidHide) {
                try {
                    // HidHide's config lives in a read/write-protected registry key (not file-based,
                    // intentionally, so it can't be casually edited outside the driver API):
                    // https://github.com/nefarius/HidHide/discussions/130
                    hidHide = new HidHideControlService();
                    if (!hidHide.IsInstalled) {
                        form.AppendTextBox("HidHide isn't installed - controllers won't be hidden from other programs.\r\n");
                        useHidHide = false;
                    } else {
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        if (!hidHide.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
                            hidHide.AddApplicationPath(exePath);
                        hidHide.IsActive = true;
                    }
                } catch (Exception e) {
                    form.AppendTextBox("Unable to configure HidHide - everything should work fine without it. (" + e.GetType().Name + ": " + e.Message + ")\r\n");
                    useHidHide = false;
                }
            }

            if (ControllerMappings.AnyVirtualOutputEnabled())
                EnsureVigemClient();

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
                // Get local BT host MAC
                if (nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) {
                    if (nic.Name.Split()[0] == "Bluetooth") {
                        btMAC = nic.GetPhysicalAddress();
                    }
                }
            }

            // GUI mode goes through the actual Form (also where the Add Controllers dialog's
            // lists get populated for editing); headless/service mode has no desktop for a Form
            // to exist on, so it loads the persisted lists directly instead - see
            // _3rdPartyControllers.LoadIntoProgramLists.
            if (form is MainForm) {
                // a bit hacky
                _3rdPartyControllers partyForm = new _3rdPartyControllers();
                partyForm.CopyCustomControllers();
                partyForm.CopyBlacklistedControllers();
            } else {
                _3rdPartyControllers.LoadIntoProgramLists();
            }

            mgr = new JoyconManager();
            mgr.form = form;
            mgr.Awake();
            mgr.CheckForNewControllers();
            mgr.Start();

            server = new UdpServer(mgr.j);
            server.form = form;

            server.Start(IPAddress.Parse(ConfigurationManager.AppSettings["IP"]), Int32.Parse(ConfigurationManager.AppSettings["Port"]));

            OpenRgbServer.SyncEnabledState();

            // Global keyboard/mouse hooks need an interactive desktop - fine in GUI mode, but
            // Session 0 (where a Windows Service runs) has none, so this would throw/do nothing
            // useful there. Service mode instead gets forwarded events over a pipe from a
            // session-launched helper process that can (see HeadlessJoyconHost/SessionLauncher).
            if (form is MainForm) {
                keyboard = WindowsInput.Capture.Global.KeyboardAsync();
                keyboard.KeyEvent += (sender, e) => {
                    if (e.Data.KeyDown != null) OnKeyDown((int)e.Data.KeyDown.Key);
                    if (e.Data.KeyUp != null) OnKeyUp((int)e.Data.KeyUp.Key);
                };
                mouse = WindowsInput.Capture.Global.MouseAsync();
                mouse.MouseEvent += (sender, e) => {
                    if (e.Data.ButtonDown != null) OnMouseButtonDown((int)e.Data.ButtonDown.Button);
                    if (e.Data.ButtonUp != null) OnMouseButtonUp((int)e.Data.ButtonUp.Button);
                };
            }

            form.AppendTextBox("All systems go\r\n");
        }

        // reset_mouse and the legacy shared active_gyro used to be decided here (single
        // key_/mse_ trigger only), but
        // now that both support a "+"-joined combo mixing controller/keyboard/mouse inputs
        // together (see Joycon.IsComboHeld), they need simultaneous visibility into all three at
        // once - evaluated once per packet in Joycon.DoThingsWithButtons instead, which already
        // runs per connected controller and can check its own buttons directly. These handlers
        // now just feed InputState so that check has an up-to-date view of which keys/mouse
        // buttons are currently held - kept independent of *how* the raw event was observed, so
        // both GUI mode's direct WindowsInput.Capture.Global hook and service mode's pipe-
        // forwarded events from the session-launched helper feed the exact same tracker.
        public static void OnKeyDown(int keyCode) => InputState.KeyDown(keyCode);
        public static void OnKeyUp(int keyCode) => InputState.KeyUp(keyCode);
        public static void OnMouseButtonDown(int buttonCode) => InputState.MouseDown(buttonCode);
        public static void OnMouseButtonUp(int buttonCode) => InputState.MouseUp(buttonCode);

        public static void Stop() {
            // Stop the background scan first - otherwise it can still be adding to
            // hiddenInstanceIds (TryHideController) while the loop below enumerates/clears it.
            mgr.StopScanning();

            if (useHidHide && hidHide != null && Boolean.Parse(ConfigurationManager.AppSettings["UnhideOnExit"])) {
                lock (hiddenInstanceIdsLock) {
                    foreach (string id in hiddenInstanceIds) {
                        try { hidHide.RemoveBlockedInstanceId(id); } catch { }
                    }
                    hiddenInstanceIds.Clear();
                }
            }

            keyboard?.Dispose(); mouse?.Dispose();
            server.Stop();
            mgr.OnApplicationQuit();
        }

        private static string appGuid = "1bf709e9-c133-41df-933a-c9ff3f664c7b"; // randomly-generated
        public static void Main(string[] args) {
            using (Mutex mutex = new Mutex(false, "Global\\" + appGuid)) {
                if (!mutex.WaitOne(0, false)) {
                    MessageBox.Show("Instance already running.", "BetterJoy2");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm mainForm = new MainForm();
                form = mainForm;
                Application.Run(mainForm);
            }
        }

    }
}
