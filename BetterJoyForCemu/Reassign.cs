using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterJoyForCemu {
    public partial class Reassign : Form {
        private WindowsInput.Events.Sources.IKeyboardEventSource keyboard;
        private WindowsInput.Events.Sources.IMouseEventSource mouse;

        ContextMenuStrip menu_joy_buttons = new ContextMenuStrip();
        ContextMenuStrip menu_gyro_activation = new ContextMenuStrip();
        ContextMenuStrip menu_gyro_analog_sliders = new ContextMenuStrip();
        ContextMenuStrip menu_gyro_mouse_inhibit = new ContextMenuStrip();
        ContextMenuStrip menu_touchpad_inhibit = new ContextMenuStrip();
        ContextMenuStrip menu_touchpad_sensitivity = new ContextMenuStrip();
        ContextMenuStrip menu_touchpad_axis_scale = new ContextMenuStrip();
        ContextMenuStrip menu_touchpad_tap_hold = new ContextMenuStrip();
        ContextMenuStrip menu_touchpad_click_lockout = new ContextMenuStrip();
        ContextMenuStrip menu_touchpad_two_finger_scroll = new ContextMenuStrip();
        ContextMenuStrip menu_gyro_stick_mode = new ContextMenuStrip();
        ContextMenuStrip menu_gyro_stick_axis = new ContextMenuStrip();
        ContextMenuStrip menu_default_orientation = new ContextMenuStrip();

        private Control curAssignment;
        private bool bindingCaptureSuppressionActive;

        // Controller buttons have no equivalent to WindowsInput.Capture.Global (there's no OS-
        // level hook for "a button was pressed on this specific HID device") - the only way to
        // detect a press is to poll a Joycon's own current button state directly. The service
        // owns the hardware and does that polling itself (this process has no Joycon instances
        // at all), relaying each transition over the control pipe (see ButtonTransition/
        // StartButtonCapture), landing in ServiceClient_ButtonTransition below, which feeds the
        // shared HandleButtonTransition.
        private readonly ServiceControlClient serviceClient;
        private readonly List<ControllerProfileInfo> remoteProfiles = new List<ControllerProfileInfo>();
        private readonly string preferredProfileId;
        private ComboBox controllerSelector;
        private Button btn_delete_profile;
        private Button gameControllersButton;
        private Label profileStatusDot;
        private Label profileStatusLabel;
        private Label virtualControllerNameLabel;
        private Label virtualControllerDetailLabel;
        private ProfileChoiceSelector useAsSelector;
        private ProfileChoiceSelector inactivitySelector;
        private ProfileChoiceSelector gyroActivationModeSelector;
        private ProfileChoiceSelector adaptiveTriggerModeLeftSelector;
        private ProfileChoiceSelector adaptiveTriggerModeRightSelector;
        private TextBox adaptiveTriggerStartLeftInput;
        private TextBox adaptiveTriggerSecondaryLeftInput;
        private TextBox adaptiveTriggerStrengthLeftInput;
        private TextBox adaptiveTriggerStartRightInput;
        private TextBox adaptiveTriggerSecondaryRightInput;
        private TextBox adaptiveTriggerStrengthRightInput;
        private Label adaptiveTriggerSecondaryLeftLabel;
        private Label adaptiveTriggerSecondaryRightLabel;
        private SplitButton gyroStickModeSelector;
        private SplitButton gyroStickModeRightSelector;
        private SplitButton gyroStickAxisXSelector;
        private SplitButton gyroStickAxisXRightSelector;
        private SplitButton btn_gyro_analog_sliders;
        private SplitButton btn_gyro_mouse_inhibit;
        private SplitButton btn_default_orientation;
        private CheckBox autoPowerOffCheckBox;
        private CheckBox homeLongPowerOffCheckBox;
        private TextBox homeLongPowerOffHoldInput;
        private ProfileChoiceSelector rumbleModeSelector;
        private Label rumbleModeLabel;
        private CheckBox dragToggleCheckBox;
        private CheckBox swapAbCheckBox;
        private CheckBox swapXyCheckBox;
        private CheckBox homeLedCheckBox;
        private Label lightColorLabel;
        private Button lightColorButton;
        private Label lightingModeLabel;
        private ProfileChoiceSelector lightingModeSelector;
        private ProfileChoiceSelector controllerAudioEnabledSelector;
        private ProfileChoiceSelector controllerAudioVolumeSelector;
        private ProfileChoiceSelector controllerAudioEndpointSelector;
        private Button controllerAudioTestButton;
        private ProfileChoiceSelector controllerAudioUsbLoopbackSelector;
        private Label controllerAudioUsbLoopbackLabel;
        private Label controllerAudioEnabledLabel;
        private Label controllerAudioVolumeLabel;
        private Label controllerAudioEndpointLabel;
        private ProfileChoiceSelector controllerBluetoothMicrophoneSelector;
        private Label controllerBluetoothMicrophoneLabel;
        private ProfileChoiceSelector micIndicatorSelector;
        private Label micIndicatorLabel;
        private CheckBox invertStickXCheckBox;
        private CheckBox invertStickYCheckBox;
        private CheckBox invertStickXRightCheckBox;
        private CheckBox invertStickYRightCheckBox;
        private TextBox maxDeflectionXLeftInput;
        private TextBox maxDeflectionYLeftInput;
        private TextBox maxDeflectionXRightInput;
        private TextBox maxDeflectionYRightInput;
        private TextBox minDeflectionXLeftInput;
        private TextBox minDeflectionYLeftInput;
        private TextBox minDeflectionXRightInput;
        private TextBox minDeflectionYRightInput;
        private bool updatingProfileOptions;
        private bool updatingGlobalOptions;
        private readonly Dictionary<string, CheckBox> globalOptionCheckBoxes =
            new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly Dictionary<string, Panel> profilePages = new Dictionary<string, Panel>();
        private readonly Dictionary<string, Button> profileNavigationButtons = new Dictionary<string, Button>();
        private Panel profilePageHost;
        private Panel profileNavigationAccent;
        private bool updatingControllerSelector;
        private bool initialControllerSelection = true;
        private long newestControllerSequence = -1;

        // Combo capture: hold down everything you want in the combo, then let go of all of it -
        // whatever got pressed while at least one member was held becomes the saved bind, joined
        // with "+" (see Joycon.IsComboHeld). comboMembers/comboHeldNow being non-null IS the
        // "currently combo-capturing" flag.
        private HashSet<string> comboMembers;
        private HashSet<string> comboHeldNow;
        private Timer comboTimeout;

        // These actions only accept controller inputs at runtime. Keyboard/mouse capture is
        // intentionally rejected for them below, just as before profiles were introduced.
        private static readonly HashSet<string> ControllerOnlyKeys = new HashSet<string> {
            "left_click", "right_click", "center_click", "scroll_up", "scroll_down",
            "clench_gyro", "ratchet_gyro", "touchpad_left_click", "touchpad_right_click",
            "touchpad_center_click", "touchpad_scroll_up", "touchpad_scroll_down",
            "touchpad_pointer_lock"
        };

        private ControllerProfileInfo SelectedProfile {
            get {
                if (controllerSelector == null)
                    return null;

                // ComboBox.SelectedItem internally indexes Items[SelectedIndex]. When the last
                // controller disappears, WinForms can briefly retain SelectedIndex == 0 after
                // the refresh has emptied Items, making that otherwise innocent property getter
                // throw ArgumentOutOfRangeException. Validate the pair explicitly before reading
                // the collection so the dialog transitions cleanly to its no-profile state.
                int selectedIndex = controllerSelector.SelectedIndex;
                if (selectedIndex < 0 || selectedIndex >= controllerSelector.Items.Count)
                    return null;

                ControllerProfileInfo selected =
                    controllerSelector.Items[selectedIndex] as ControllerProfileInfo;
                return selected;
            }
        }

        private string SelectedProfileId => SelectedProfile?.ProfileId;

        private string GetBindValue(string key) {
            return ControllerMappings.Value(SelectedProfileId, key);
        }

        private void SetBindValue(string key, string value) {
            if (!String.IsNullOrEmpty(SelectedProfileId))
                ControllerMappings.SetValue(SelectedProfileId, key, value);
        }

        public Reassign(ServiceControlClient serviceClient,
                        IEnumerable<ControllerRecord> remoteRecords,
                        string preferredProfileId) {
            this.serviceClient = serviceClient;
            this.preferredProfileId = preferredProfileId;
            InitializeComponent();
            CreateDynamicProfileControls();
            BuildProfileInterface();

            foreach (int i in Enum.GetValues(typeof(Controller.Button))) {
                string buttonName = ControllerButtonDisplayName(i);
                ToolStripMenuItem temp = new ToolStripMenuItem(buttonName);
                temp.Tag = i;
                menu_joy_buttons.Items.Add(temp);

                ToolStripMenuItem activationItem = new ToolStripMenuItem(buttonName);
                activationItem.Tag = i;
                menu_gyro_activation.Items.Add(activationItem);
            }

            // Explicitly disabling a special mapping is different from middle-clicking it back
            // to its default: Capture and Re-Centre Gyro have nonzero defaults, but the user may
            // still want either feature completely unbound. Keep this action visually separated
            // and permanently last after every physical controller-button choice.
            menu_joy_buttons.Items.Add(new ToolStripSeparator());
            menu_joy_buttons.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "0" });
            menu_gyro_activation.Items.Add(new ToolStripSeparator());
            menu_gyro_activation.Items.Add(new ToolStripMenuItem("Always On") { Tag = "always" });
            menu_gyro_activation.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "0" });

            menu_joy_buttons.ItemClicked += Menu_joy_buttons_ItemClicked;
            menu_gyro_activation.ItemClicked += Menu_joy_buttons_ItemClicked;

            // A plain on/off setting, not a bind - shares the SplitButton visual (StyleMappingButton,
            // via AddMappingRow) with the rows above it, but not their MouseDown/Remap combo-capture
            // behavior, which has no meaning for a flag with only two fixed states.
            menu_gyro_analog_sliders.Items.Add(new ToolStripMenuItem("Enabled") { Tag = "true" });
            menu_gyro_analog_sliders.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "false" });
            menu_gyro_analog_sliders.ItemClicked += GyroAnalogSlidersMenu_ItemClicked;

            menu_gyro_mouse_inhibit.Items.Add(new ToolStripMenuItem("Enabled") { Tag = "true" });
            menu_gyro_mouse_inhibit.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "false" });
            menu_gyro_mouse_inhibit.ItemClicked += GyroMouseInhibitMenu_ItemClicked;

            menu_touchpad_inhibit.Items.Add(new ToolStripMenuItem("Enabled") { Tag = "true" });
            menu_touchpad_inhibit.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "false" });
            menu_touchpad_inhibit.ItemClicked += TouchpadInhibitMenu_ItemClicked;

            foreach (int percent in new[] { 25, 50, 75, 100, 125, 150, 200, 300, 400 })
                menu_touchpad_sensitivity.Items.Add(
                    new ToolStripMenuItem(percent + "%") { Tag = percent.ToString() });
            menu_touchpad_sensitivity.ItemClicked += TouchpadSensitivityMenu_ItemClicked;

            foreach (int percent in new[] { 0, 25, 50, 75, 100 })
                menu_touchpad_axis_scale.Items.Add(
                    new ToolStripMenuItem(percent + "%") { Tag = percent.ToString() });
            menu_touchpad_axis_scale.ItemClicked += TouchpadAxisScaleMenu_ItemClicked;

            menu_touchpad_tap_hold.Items.Add(
                new ToolStripMenuItem("Disabled") { Tag = "disabled" });
            menu_touchpad_tap_hold.Items.Add(
                new ToolStripMenuItem("Hold to drag") { Tag = "hold" });
            menu_touchpad_tap_hold.Items.Add(
                new ToolStripMenuItem("Double-tap to drag") { Tag = "double_tap" });
            menu_touchpad_tap_hold.ItemClicked += TouchpadTapHoldMenu_ItemClicked;

            menu_touchpad_click_lockout.Items.Add(new ToolStripMenuItem("Enabled") { Tag = "true" });
            menu_touchpad_click_lockout.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "false" });
            menu_touchpad_click_lockout.ItemClicked += TouchpadClickLockoutMenu_ItemClicked;

            menu_touchpad_two_finger_scroll.Items.Add(
                new ToolStripMenuItem("Enabled") { Tag = "true" });
            menu_touchpad_two_finger_scroll.Items.Add(
                new ToolStripMenuItem("Disabled") { Tag = "false" });
            menu_touchpad_two_finger_scroll.ItemClicked +=
                TouchpadTwoFingerScrollMenu_ItemClicked;

            menu_gyro_stick_mode.Items.Add(new ToolStripMenuItem("Rate (current)") { Tag = "rate" });
            menu_gyro_stick_mode.Items.Add(new ToolStripMenuItem("Absolute tilt") { Tag = "absolute" });
            menu_gyro_stick_mode.Items.Add(new ToolStripMenuItem("Hybrid") { Tag = "hybrid" });
            menu_gyro_stick_mode.ItemClicked += GyroStickModeMenu_ItemClicked;

            menu_gyro_stick_axis.Items.Add(new ToolStripMenuItem("Yaw (twist)") { Tag = "yaw" });
            menu_gyro_stick_axis.Items.Add(new ToolStripMenuItem("Roll (bank)") { Tag = "roll" });
            menu_gyro_stick_axis.ItemClicked += GyroStickAxisMenu_ItemClicked;

            menu_default_orientation.Items.Add(
                new ToolStripMenuItem("Horizontal") { Tag = ControllerMappings.OrientationHorizontal });
            menu_default_orientation.Items.Add(
                new ToolStripMenuItem("Vertical") { Tag = ControllerMappings.OrientationVertical });
            menu_default_orientation.ItemClicked += DefaultOrientationMenu_ItemClicked;

            specialButtons = new List<SplitButton> { btn_capture, btn_home, btn_guide, btn_mic_mute, btn_toggle_built_in_mic, btn_volume_up, btn_volume_down, btn_lt_haptics, btn_rt_haptics, btn_toggle_haptics, btn_toggle_lighting, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_shake, btn_reset_mouse, btn_active_gyro, btn_ratchet_gyro, btn_touchpad_click, btn_touchpad_tap, btn_touchpad_two_finger_tap, btn_touchpad_two_finger_scroll_up, btn_touchpad_two_finger_scroll_down, btn_active_touchpad_mouse };
            specialButtons.AddRange(gyroMouseButtons);
            specialButtons.AddRange(gyroStickActivationButtons);
            specialButtons.AddRange(touchpadStickActivationButtons);
            specialButtons.AddRange(touchpadMouseButtons);

            foreach (SplitButton c in specialButtons) {
                c.Tag = c == btn_active_gyro
                    ? "active_gyro_mouse"
                    : c.Name.Substring(4);
                GetPrettyName(c);

                c.MouseDown += Remap;
                c.Menu = IsActivationKey((string)c.Tag)
                    ? menu_gyro_activation
                    : menu_joy_buttons;
                c.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            }

            StyleAssignmentMenus();
            SetRemoteProfiles(remoteRecords);
            RefreshControllerChoices();
        }

        // These controls are created in code because the profile window composes its pages at
        // runtime. Their mapping keys and event wiring are still shared with the original
        // Designer-owned buttons below, so page navigation never creates a second behavior path.
        private readonly List<SplitButton> gyroMouseButtons = new List<SplitButton>();
        private readonly List<SplitButton> gyroStickActivationButtons = new List<SplitButton>();
        private readonly List<SplitButton> touchpadStickActivationButtons =
            new List<SplitButton>();
        private readonly List<SplitButton> touchpadMouseButtons = new List<SplitButton>();
        private SplitButton btn_ratchet_gyro;
        private SplitButton btn_guide;
        private SplitButton btn_mic_mute;
        private SplitButton btn_toggle_built_in_mic;
        private SplitButton btn_volume_up;
        private SplitButton btn_volume_down;
        private SplitButton btn_lt_haptics;
        private SplitButton btn_rt_haptics;
        private SplitButton btn_toggle_haptics;
        private SplitButton btn_toggle_lighting;
        private SplitButton btn_touchpad_click;
        private SplitButton btn_touchpad_tap;
        private SplitButton btn_touchpad_two_finger_tap;
        private SplitButton btn_touchpad_two_finger_scroll_up;
        private SplitButton btn_touchpad_two_finger_scroll_down;
        private SplitButton btn_touchpad_two_finger_scroll;
        private SplitButton btn_active_touchpad_mouse;
        private SplitButton btn_touchpad_inhibit;
        private SplitButton btn_touchpad_sensitivity;
        private SplitButton btn_touchpad_stick_sensitivity;
        private SplitButton btn_touchpad_horizontal_scale;
        private SplitButton btn_touchpad_vertical_scale;
        private SplitButton btn_touchpad_tap_hold;
        private SplitButton btn_touchpad_click_lockout;
        private List<SplitButton> specialButtons;

        private static bool IsActivationKey(string key) {
            return key == "active_gyro_mouse" ||
                   key == "active_gyro_left_stick" ||
                   key == "active_gyro_right_stick" ||
                   key == "active_touchpad_mouse" ||
                   key == "active_touchpad_left_stick" ||
                   key == "active_touchpad_right_stick";
        }

        // Stick mapping mode/axis/invert only take effect in filtered IMU mode (see
        // Joycon.GyroStickModeNeedsRecenter and ComputeFilteredGyroStickOutput) - grayed out
        // rather than hidden so a raw-mode user can see the controls exist without them silently
        // doing nothing.
        private static bool GyroStickModeControlsSupported {
            get {
                bool useFilteredIMU;
                return !Boolean.TryParse(
                    ConfigurationManager.AppSettings["UseFilteredIMU"], out useFilteredIMU) ||
                    useFilteredIMU;
            }
        }

        private static readonly Color ProfileBackground = Color.FromArgb(31, 32, 33);
        private static readonly Color ProfileSidebar = Color.FromArgb(26, 27, 28);
        private static readonly Color ProfileSurface = Color.FromArgb(45, 46, 48);
        private static readonly Color ProfileSurfaceHover = Color.FromArgb(55, 56, 58);
        private static readonly Color ProfileBorder = Color.FromArgb(68, 69, 72);
        private static readonly Color ProfileText = Color.FromArgb(244, 244, 244);
        private static readonly Color ProfileMuted = Color.FromArgb(184, 190, 199);
        private static readonly Color ProfileAccent = Color.FromArgb(255, 188, 21);
        private static readonly Color ProfileConnected = Color.FromArgb(62, 201, 116);

        private void CreateDynamicProfileControls() {
            // Order must match BuildGyroPage's `labels` array exactly - each entry here becomes
            // gyroMouseButtons[i]'s actual saved config key (via Tag), while `labels[i]` is only
            // the on-screen text for that same button. A "Redesign controller profiles interface"
            // pass reordered the on-screen labels (moving Clench gyro next to Middle click) but
            // left this array in its old order, so index 3-5's buttons all displayed the wrong
            // label for the key they were really wired to - e.g. the button visually labeled
            // "Clench gyro" was actually saving/reading "scroll_up".
            var entries = new (string key, string label)[] {
                ("left_click", "Left Click"),
                ("right_click", "Right Click"),
                ("center_click", "Center Click"),
                ("clench_gyro", "Clench Gyro"),
                ("scroll_up", "Scroll Up"),
                ("scroll_down", "Scroll Down"),
            };
            foreach (var entry in entries) {
                var button = new SplitButton {
                    Name = "btn_" + entry.key,
                };
                gyroMouseButtons.Add(button);
            }

            var activationEntries = new (string key, string label)[] {
                ("active_gyro_left_stick", "Left Stick"),
                ("active_gyro_right_stick", "Right Stick"),
            };
            foreach (var entry in activationEntries) {
                var button = new SplitButton {
                    Name = "btn_" + entry.key,
                };
                gyroStickActivationButtons.Add(button);
            }

            foreach (string key in new[] {
                "active_touchpad_left_stick", "active_touchpad_right_stick",
            })
                touchpadStickActivationButtons.Add(
                    new SplitButton { Name = "btn_" + key });

            string[] touchpadActionKeys = {
                "touchpad_left_click", "touchpad_right_click", "touchpad_center_click",
                "touchpad_pointer_lock", "touchpad_scroll_up", "touchpad_scroll_down",
            };
            foreach (string key in touchpadActionKeys)
                touchpadMouseButtons.Add(new SplitButton { Name = "btn_" + key });

            btn_ratchet_gyro = new SplitButton { Name = "btn_ratchet_gyro" };
            btn_guide = new SplitButton { Name = "btn_guide" };
            btn_mic_mute = new SplitButton { Name = "btn_mic_mute" };
            btn_toggle_built_in_mic = new SplitButton { Name = "btn_toggle_built_in_mic" };
            btn_volume_up = new SplitButton { Name = "btn_volume_up" };
            btn_volume_down = new SplitButton { Name = "btn_volume_down" };
            btn_lt_haptics = new SplitButton { Name = "btn_lt_haptics" };
            btn_rt_haptics = new SplitButton { Name = "btn_rt_haptics" };
            btn_toggle_haptics = new SplitButton { Name = "btn_toggle_haptics" };
            btn_toggle_lighting = new SplitButton { Name = "btn_toggle_lighting" };
            btn_touchpad_click = new SplitButton { Name = "btn_touchpad_click" };
            btn_touchpad_tap = new SplitButton { Name = "btn_touchpad_tap" };
            btn_touchpad_two_finger_tap = new SplitButton {
                Name = "btn_touchpad_two_finger_tap"
            };
            btn_touchpad_two_finger_scroll_up = new SplitButton {
                Name = "btn_touchpad_two_finger_scroll_up"
            };
            btn_touchpad_two_finger_scroll_down = new SplitButton {
                Name = "btn_touchpad_two_finger_scroll_down"
            };
            btn_touchpad_two_finger_scroll = new SplitButton {
                Name = "btn_touchpad_two_finger_scroll",
                Menu = menu_touchpad_two_finger_scroll,
            };
            btn_touchpad_two_finger_scroll.Click += (sender, e) =>
                menu_touchpad_two_finger_scroll.Show(btn_touchpad_two_finger_scroll, 0,
                    btn_touchpad_two_finger_scroll.Height);
            btn_active_touchpad_mouse = new SplitButton { Name = "btn_active_touchpad_mouse" };

            gyroStickModeSelector = CreateChoiceSplitButton(
                "btn_gyro_stick_mode_left", menu_gyro_stick_mode);
            gyroStickModeRightSelector = CreateChoiceSplitButton(
                "btn_gyro_stick_mode_right", menu_gyro_stick_mode);
            gyroStickAxisXSelector = CreateChoiceSplitButton(
                "btn_gyro_stick_axis_left", menu_gyro_stick_axis);
            gyroStickAxisXRightSelector = CreateChoiceSplitButton(
                "btn_gyro_stick_axis_right", menu_gyro_stick_axis);

            btn_gyro_analog_sliders = new SplitButton { Name = "btn_gyro_analog_sliders" };
            btn_gyro_analog_sliders.Menu = menu_gyro_analog_sliders;
            btn_gyro_analog_sliders.Click += (sender, e) =>
                menu_gyro_analog_sliders.Show(btn_gyro_analog_sliders, 0, btn_gyro_analog_sliders.Height);

            btn_gyro_mouse_inhibit = new SplitButton { Name = "btn_gyro_mouse_inhibit" };
            btn_gyro_mouse_inhibit.Menu = menu_gyro_mouse_inhibit;
            btn_gyro_mouse_inhibit.Click += (sender, e) =>
                menu_gyro_mouse_inhibit.Show(btn_gyro_mouse_inhibit, 0, btn_gyro_mouse_inhibit.Height);

            btn_touchpad_inhibit = new SplitButton { Name = "btn_touchpad_inhibit" };
            btn_touchpad_inhibit.Menu = menu_touchpad_inhibit;
            btn_touchpad_inhibit.Click += (sender, e) =>
                menu_touchpad_inhibit.Show(btn_touchpad_inhibit, 0, btn_touchpad_inhibit.Height);

            btn_touchpad_sensitivity = CreateChoiceSplitButton(
                "btn_touchpad_sensitivity", menu_touchpad_sensitivity);
            btn_touchpad_sensitivity.RightClickHandler = (sender, e) =>
                PromptTouchpadSensitivity(btn_touchpad_sensitivity,
                    "TouchpadSensitivity", "Mouse sensitivity", "mouse sensitivity");

            btn_touchpad_stick_sensitivity = CreateChoiceSplitButton(
                "btn_touchpad_stick_sensitivity", menu_touchpad_sensitivity);
            btn_touchpad_stick_sensitivity.RightClickHandler = (sender, e) =>
                PromptTouchpadSensitivity(btn_touchpad_stick_sensitivity,
                    "TouchpadStickSensitivity", "Stick sensitivity", "stick sensitivity");

            btn_touchpad_horizontal_scale = CreateChoiceSplitButton(
                "btn_touchpad_horizontal_scale", menu_touchpad_axis_scale);
            btn_touchpad_horizontal_scale.RightClickHandler = (sender, e) =>
                PromptTouchpadAxisScale(btn_touchpad_horizontal_scale,
                    "TouchpadHorizontalScale", "Horizontal scale");
            btn_touchpad_vertical_scale = CreateChoiceSplitButton(
                "btn_touchpad_vertical_scale", menu_touchpad_axis_scale);
            btn_touchpad_vertical_scale.RightClickHandler = (sender, e) =>
                PromptTouchpadAxisScale(btn_touchpad_vertical_scale,
                    "TouchpadVerticalScale", "Vertical scale");

            btn_touchpad_tap_hold = new SplitButton { Name = "btn_touchpad_tap_hold" };
            btn_touchpad_tap_hold.Menu = menu_touchpad_tap_hold;
            btn_touchpad_tap_hold.Click += (sender, e) =>
                menu_touchpad_tap_hold.Show(btn_touchpad_tap_hold, 0,
                    btn_touchpad_tap_hold.Height);

            btn_touchpad_click_lockout = new SplitButton { Name = "btn_touchpad_click_lockout" };
            btn_touchpad_click_lockout.Menu = menu_touchpad_click_lockout;
            btn_touchpad_click_lockout.Click += (sender, e) =>
                menu_touchpad_click_lockout.Show(btn_touchpad_click_lockout, 0,
                    btn_touchpad_click_lockout.Height);

            btn_default_orientation = new SplitButton { Name = "btn_default_orientation" };
            btn_default_orientation.Menu = menu_default_orientation;
            btn_default_orientation.Click += (sender, e) =>
                menu_default_orientation.Show(btn_default_orientation, 0, btn_default_orientation.Height);

            gameControllersButton = new Button {
                Text = "Game Controllers...",
            };
            gameControllersButton.Click += GameControllersButton_Click;
            tip_reassign.SetToolTip(gameControllersButton,
                "Open the selected profile's virtual controller properties when connected.\r\n" +
                "Disconnected profiles open the standard Game Controllers list.");
        }

        private SplitButton CreateChoiceSplitButton(string name, ContextMenuStrip menu) {
            var button = new SplitButton { Name = name, Menu = menu };
            button.Click += (sender, e) => {
                menu.Tag = button;
                menu.Show(button, 0, button.Height);
            };
            return button;
        }

        // Every control below is laid out at these "design" coordinates/font sizes, then the
        // whole tree is uniformly scaled down by ProfileUiScale in one pass at the end of
        // BuildProfileInterface (see ScaleProfileUi). That's deliberate: hand-tightening
        // individual gaps (tried once, reverted) shrinks the space between controls without
        // shrinking the controls or text themselves, which just looks stubby/squat instead of
        // actually smaller. A uniform scale keeps every proportion - text, controls, spacing -
        // identical to the design layout, just smaller, matching what this dialog would look
        // like rendered at a lower DPI.
        private const float ProfileUiScale = 0.8f;

        private void BuildProfileInterface() {
            SuspendLayout();
            Controls.Clear();

            // Reassign.Designer.cs leaves AutoScaleDimensions at its Font-mode design baseline
            // (6F, 13F) - overriding just AutoScaleMode to Dpi here without correcting this too
            // makes WinForms compute the auto-scale ratio against the wrong baseline (comparing
            // the current screen DPI to a font-metrics number, not a DPI number), producing a
            // badly wrong scale factor. 96F,96F is the un-scaled ("100%") DPI baseline.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = ProfileBackground;
            ForeColor = ProfileText;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ClientSize = new Size(840, 680);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            Panel header = BuildProfileHeader();
            Panel footer = BuildProfileFooter();
            Panel body = new Panel {
                Dock = DockStyle.Fill,
                BackColor = ProfileBackground,
            };
            Panel sidebar = BuildProfileSidebar();
            profilePageHost = new Panel {
                Dock = DockStyle.Fill,
                BackColor = ProfileBackground,
            };
            body.Controls.Add(profilePageHost);
            body.Controls.Add(sidebar);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);

            Panel bindingsPage = BuildBindingsPage();
            Panel gyroPage = BuildGyroPage();
            Panel touchpadPage = BuildTouchpadPage();
            Panel adaptiveTriggersPage = BuildAdaptiveTriggersPage();
            Panel behaviorPage = BuildDeviceBehaviorPage();
            Panel virtualControllerPage = BuildVirtualControllerPage();
            Panel globalPage = BuildGlobalPage();
            profilePages.Add("bindings", bindingsPage);
            profilePages.Add("gyro", gyroPage);
            profilePages.Add("touchpad", touchpadPage);
            profilePages.Add("adaptive_triggers", adaptiveTriggersPage);
            profilePages.Add("behavior", behaviorPage);
            profilePages.Add("virtual", virtualControllerPage);
            profilePages.Add("global", globalPage);
            profilePageHost.Controls.Add(bindingsPage);
            profilePageHost.Controls.Add(gyroPage);
            profilePageHost.Controls.Add(touchpadPage);
            profilePageHost.Controls.Add(adaptiveTriggersPage);
            profilePageHost.Controls.Add(behaviorPage);
            profilePageHost.Controls.Add(virtualControllerPage);
            profilePageHost.Controls.Add(globalPage);
            LoadGlobalOptions();
            ShowProfilePage("gyro");

            ScaleProfileUi(this, ProfileUiScale);
            ClientSize = new Size(
                (int)Math.Round(ClientSize.Width * ProfileUiScale),
                (int)Math.Round(ClientSize.Height * ProfileUiScale));
            // Room for the title bar/borders Windows adds outside ClientSize - not part of the
            // scaled content, so a fixed pad rather than another multiply by ProfileUiScale.
            MinimumSize = new Size(ClientSize.Width + 16, ClientSize.Height + 39);

            ResumeLayout(true);
        }

        // Uniformly shrinks every descendant's position, size, and font by factor, in one pass,
        // so the whole dialog scales together instead of just the gaps between controls (see
        // ProfileUiScale). Docked/filled panels ignore Location and most of Size, but Height on
        // a Dock.Top/Bottom panel and Width on a Dock.Left one set the docked thickness, so those
        // still need scaling here rather than being left at design size.
        private static void ScaleProfileUi(Control control, float factor) {
            foreach (Control child in control.Controls) {
                child.Location = new Point(
                    (int)Math.Round(child.Location.X * factor), (int)Math.Round(child.Location.Y * factor));
                child.Size = new Size(
                    (int)Math.Round(child.Width * factor), (int)Math.Round(child.Height * factor));
                // Only controls that render their own text get rescaled here. Reassigning Font
                // on a plain Panel would flip it from "inherited from parent" to "explicitly set
                // on this control," which then compounds the scale factor again for any
                // descendant still relying on that inheritance - e.g. AddMappingRow's row labels
                // never set their own Font, so walking through header/body/page panels first
                // multiplied the factor into them three or four times over, landing them at a
                // fraction of their intended size instead of ProfileUiScale.
                if (child is Label || child is ButtonBase || child is ComboBox)
                    child.Font = new Font(child.Font.FontFamily, child.Font.Size * factor, child.Font.Style);
                if (child is Panel panel && panel.AutoScroll && panel.AutoScrollMinSize != Size.Empty) {
                    panel.AutoScrollMinSize = new Size(
                        (int)Math.Round(panel.AutoScrollMinSize.Width * factor),
                        (int)Math.Round(panel.AutoScrollMinSize.Height * factor));
                }
                ScaleProfileUi(child, factor);
            }
        }

        private Panel BuildProfileHeader() {
            Panel header = new Panel {
                Dock = DockStyle.Top,
                Height = 66,
                BackColor = Color.FromArgb(38, 39, 41),
            };
            header.Paint += (sender, e) => {
                using (Pen pen = new Pen(ProfileBorder))
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };

            header.Controls.Add(CreateLabel("Controller profile", 18, 25, ProfileText, false));
            controllerSelector = new ComboBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = ProfileSurface,
                ForeColor = ProfileText,
                Font = new Font(Font.FontFamily, 9.25F),
                Location = new Point(140, 19),
                Size = new Size(450, 25),
            };
            controllerSelector.SelectedIndexChanged += ControllerSelector_SelectedIndexChanged;
            header.Controls.Add(controllerSelector);

            btn_delete_profile = new Button { Text = "Delete" };
            StyleStandardButton(btn_delete_profile, false);
            btn_delete_profile.Location = new Point(600, 17);
            btn_delete_profile.Size = new Size(72, 30);
            btn_delete_profile.Enabled = false;
            tip_reassign.SetToolTip(btn_delete_profile,
                "Permanently delete this saved profile and its calibration data.\r\n" +
                "Only available for a disconnected profile.");
            btn_delete_profile.Click += DeleteProfileButton_Click;
            header.Controls.Add(btn_delete_profile);

            profileStatusDot = CreateLabel("●", 692, 23, ProfileConnected, true);
            profileStatusLabel = CreateLabel("Connected", 709, 25, ProfileConnected, false);
            header.Controls.Add(profileStatusDot);
            header.Controls.Add(profileStatusLabel);
            return header;
        }

        private Panel BuildProfileFooter() {
            Panel footer = new Panel {
                Dock = DockStyle.Bottom,
                Height = 54,
                BackColor = Color.FromArgb(37, 38, 40),
            };
            footer.Paint += (sender, e) => {
                using (Pen pen = new Pen(ProfileBorder))
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            StyleStandardButton(btn_close, false);
            btn_close.Text = "Close";
            btn_close.Size = new Size(84, 32);
            btn_close.Location = new Point(650, 12);

            StyleStandardButton(btn_apply, true);
            btn_apply.Size = new Size(84, 32);
            btn_apply.Location = new Point(744, 12);
            footer.Controls.Add(btn_close);
            footer.Controls.Add(btn_apply);
            return footer;
        }

        private Panel BuildProfileSidebar() {
            Panel sidebar = new Panel {
                Dock = DockStyle.Left,
                Width = 178,
                BackColor = ProfileSidebar,
            };
            sidebar.Paint += (sender, e) => {
                using (Pen pen = new Pen(ProfileBorder))
                    e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
            };
            sidebar.Controls.Add(CreateLabel("PROFILE SETTINGS", 20, 25,
                Color.FromArgb(151, 174, 205), false, 8F));

            profileNavigationAccent = new Panel {
                BackColor = ProfileAccent,
                Location = new Point(10, 60),
                Size = new Size(3, 40),
            };
            sidebar.Controls.Add(profileNavigationAccent);
            sidebar.Controls.Add(CreateNavigationButton("Bindings", "bindings", 60));
            sidebar.Controls.Add(CreateNavigationButton("Gyro", "gyro", 104));
            sidebar.Controls.Add(CreateNavigationButton("Touchpad", "touchpad", 148));
            sidebar.Controls.Add(CreateNavigationButton("Adaptive triggers", "adaptive_triggers", 192));
            sidebar.Controls.Add(CreateNavigationButton("Device behavior", "behavior", 236));
            sidebar.Controls.Add(CreateNavigationButton("Virtual controller", "virtual", 280));
            sidebar.Controls.Add(CreateLabel("GLOBAL", 20, 350,
                Color.FromArgb(151, 174, 205), false, 8F));
            sidebar.Controls.Add(CreateNavigationButton("Global options", "global", 380));
            return sidebar;
        }

        private Button CreateNavigationButton(string text, string key, int top) {
            Button button = new Button {
                Text = text,
                Tag = key,
                Location = new Point(13, top),
                Size = new Size(153, 40),
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0, MouseOverBackColor = ProfileSurfaceHover },
                BackColor = ProfileSidebar,
                ForeColor = ProfileText,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                Cursor = Cursors.Hand,
                Font = new Font(Font, FontStyle.Regular),
            };
            button.Click += (sender, e) => ShowProfilePage(key);
            profileNavigationButtons.Add(key, button);
            return button;
        }

        private void ShowProfilePage(string key) {
            foreach (KeyValuePair<string, Panel> page in profilePages)
                page.Value.Visible = page.Key == key;
            foreach (KeyValuePair<string, Button> navigation in profileNavigationButtons) {
                bool selected = navigation.Key == key;
                navigation.Value.BackColor = selected ? Color.FromArgb(58, 52, 34) : ProfileSidebar;
                if (navigation.Value.Font.Bold != selected) {
                    Font oldFont = navigation.Value.Font;
                    navigation.Value.Font = new Font(oldFont,
                        selected ? FontStyle.Bold : FontStyle.Regular);
                    oldFont.Dispose();
                }
                if (selected && profileNavigationAccent != null)
                    profileNavigationAccent.Top = navigation.Value.Top;
            }
            if (profilePages.TryGetValue(key, out Panel selectedPage))
                selectedPage.BringToFront();
        }

        private Panel CreateProfilePage(string title, string description) {
            Panel page = new Panel {
                Dock = DockStyle.Fill,
                BackColor = ProfileBackground,
                AutoScroll = true,
                Visible = false,
            };
            page.Controls.Add(CreateLabel(title, 24, 17, ProfileText, true, 15F));
            Label detail = CreateLabel(description, 24, 48, ProfileMuted, false, 9.25F);
            detail.AutoSize = false;
            detail.Size = new Size(570, 22);
            page.Controls.Add(detail);
            page.Controls.Add(CreateDivider(24, 78));
            return page;
        }

        private Panel BuildBindingsPage() {
            Panel page = CreateProfilePage("Bindings",
                "Choose what special inputs do and which input controls virtual Guide / PS.");
            AddSectionHeading(page, "System controls", 96,
                "Physical Home / PS behavior and virtual Guide / PS activation are independent.");
            AddMappingRow(page, lbl_capture, btn_capture, "Capture button", 157, 24, 150, 430);
            AddMappingRow(page, lbl_home, btn_home, "Home / PS button", 196, 24, 150, 430);
            AddMappingRow(page, null, btn_guide, "Guide / PS output", 235, 24, 150, 430);
            AddMappingRow(page, null, btn_mic_mute, "Microphone button", 274, 24, 150, 430);
            AddMappingRow(page, lbl_shake, btn_shake, "Shake input", 313, 24, 150, 430);

            page.Controls.Add(CreateDivider(24, 349));
            AddSectionHeading(page, "Controller functions", 366,
                "No controller has a dedicated button for these - bind any combo you like.");
            // AdjustControllerAudioVolumeOnPress/CycleOptionModeOnPress (Controller.cs) - the same
            // "any combo, one discrete step per press" model scroll_up/scroll_down (Gyro page) use,
            // rather than remapping a fixed physical button the way the rows above do. Volume:
            // DualSense/DualShock4 only. LT/RT haptics: DualSense only (greyed out otherwise - see
            // ApplySelectedController's hasAdaptiveTriggers), cycling that trigger's own Adaptive
            // trigger mode Off -> Resistance -> Weapon -> Vibration -> Off. Toggle haptics: every
            // controller type, cycling the Rumble mode above Enable -> Disable -> Disable with
            // gyro -> Enable. Toggle Built-in mic is the actual mute toggle, as opposed to
            // "Microphone button" above (which only remaps what pressing the physical mic-mute
            // button outputs elsewhere) - defaults to that same physical button alone
            // (ControllerMappings.LegacyValue), so nothing changes unless reassigned.
            AddMappingRow(page, null, btn_volume_up, "Controller vol +", 427, 24, 150, 430);
            AddMappingRow(page, null, btn_volume_down, "Controller vol -", 466, 24, 150, 430);
            AddMappingRow(page, null, btn_lt_haptics, "LT haptics", 505, 24, 150, 430);
            AddMappingRow(page, null, btn_rt_haptics, "RT haptics", 544, 24, 150, 430);
            AddMappingRow(page, null, btn_toggle_haptics, "Toggle haptics", 583, 24, 150, 430);
            AddMappingRow(page, null, btn_toggle_built_in_mic, "Toggle Built-in mic", 622, 24, 150, 430);
            // Sets the RGB lightbar to black instead of the configured Light color while off,
            // never touching that setting itself (ApplyControllerProfileLighting/LightingOff) -
            // nothing to save/restore, only to flip. DualSense/DualShock4 only, matching
            // ApplySelectedController's hasConfigurableLight.
            AddMappingRow(page, null, btn_toggle_lighting, "Toggle lighting", 661, 24, 150, 430);

            page.Controls.Add(CreateDivider(24, 710));
            AddSectionHeading(page, "Joy-Con rail buttons", 727,
                "Independent mappings for the SL and SR buttons on each Joy-Con.");
            AddMappingRow(page, lbl_sl_l, btn_sl_l, "Left Joy-Con · SL", 788, 24, 145, 140);
            AddMappingRow(page, lbl_sl_r, btn_sl_r, "Right Joy-Con · SL", 788, 315, 440, 154);
            AddMappingRow(page, lbl_sr_l, btn_sr_l, "Left Joy-Con · SR", 829, 24, 145, 140);
            AddMappingRow(page, lbl_sr_r, btn_sr_r, "Right Joy-Con · SR", 829, 315, 440, 154);
            page.AutoScrollMinSize = new Size(0, 890);
            return page;
        }

        private Panel BuildGyroPage() {
            Panel page = CreateProfilePage("Gyro",
                "Choose where motion is sent and how each output activates.");
            AddSectionHeading(page, "Output activation", 86,
                "Set each output to always on, disabled, or activate it with a binding.");
            page.Controls.Add(CreateLabel("Output", 24, 137, ProfileMuted, false, 8.25F));
            page.Controls.Add(CreateLabel("Activation", 180, 137, ProfileMuted, false, 8.25F));
            AddMappingRow(page, lbl_activate_gyro, btn_active_gyro, "Mouse", 158, 24, 180, 414);
            AddMappingRow(page, null, gyroStickActivationButtons[0], "Left stick", 195, 24, 180, 414);
            AddMappingRow(page, null, gyroStickActivationButtons[1], "Right stick", 232, 24, 180, 414);

            // Gyro-driven L2/R2 analog output while a stick-gyro output button is held (see
            // Joycon.GyroAnalogSliders) - listed last since it modifies the stick outputs above
            // rather than being an output of its own. A plain on/off flag, not a bind, so its
            // SplitButton opens menu_gyro_analog_sliders on any click (wired in
            // CreateDynamicProfileControls) instead of the Remap combo-capture the other rows use.
            AddMappingRow(page, null, btn_gyro_analog_sliders, "Analog triggers", 269, 24, 180, 414);

            // While held, zeroes gyro-to-stick output instead of tracking live rotation - lets
            // the wrist reposition back to a comfortable angle without that motion registering as
            // a reverse turn, or a mid-turn hold continuing to turn on its own (see
            // Joycon.gyroStickRatcheted).
            AddMappingRow(page, null, btn_ratchet_gyro, "Ratchet gyro", 306, 24, 180, 414);

            page.Controls.Add(CreateDivider(24, 348));
            AddSectionHeading(page, "Stick mapping", 363,
                "Shape how gyro tilt drives the stick outputs above per stick. Requires IMU filtering.");

            AddMappingRow(page, null, gyroStickModeSelector,
                "Left stick response", 419, 24, 180, 180);
            AddMappingRow(page, null, gyroStickModeRightSelector,
                "Right stick response", 457, 24, 180, 180);
            AddMappingRow(page, null, gyroStickAxisXSelector,
                "Left turn axis", 495, 24, 180, 180);
            AddMappingRow(page, null, gyroStickAxisXRightSelector,
                "Right turn axis", 533, 24, 180, 180);

            invertStickXCheckBox = CreateProfileCheckBox("Invert left X", 24, 571, "GyroStickInvertXLeft");
            invertStickYCheckBox = CreateProfileCheckBox("Invert left Y", 190, 571, "GyroStickInvertYLeft");
            invertStickXRightCheckBox = CreateProfileCheckBox("Invert right X", 24, 595, "GyroStickInvertXRight");
            invertStickYRightCheckBox = CreateProfileCheckBox("Invert right Y", 190, 595, "GyroStickInvertYRight");
            page.Controls.Add(invertStickXCheckBox);
            page.Controls.Add(invertStickYCheckBox);
            page.Controls.Add(invertStickXRightCheckBox);
            page.Controls.Add(invertStickYRightCheckBox);

            page.Controls.Add(CreateDivider(24, 636));
            AddSectionHeading(page, "Deflection limits", 651,
                "How far gyro alone may push each stick, and the minimum once it starts moving.");

            int[] deflectionColumnX = { 140, 240, 340, 440 };
            string[] deflectionColumnLabels = { "Max X", "Max Y", "Min X", "Min Y" };
            for (int column = 0; column < deflectionColumnX.Length; column++)
                page.Controls.Add(CreateLabel(deflectionColumnLabels[column], deflectionColumnX[column], 707,
                    ProfileMuted, false, 8.25F));

            page.Controls.Add(CreateLabel("Left stick", 24, 734, ProfileText, false));
            maxDeflectionXLeftInput = CreateProfilePercentInput(page, deflectionColumnX[0], 728, "GyroStickMaxDeflectionXLeft");
            maxDeflectionYLeftInput = CreateProfilePercentInput(page, deflectionColumnX[1], 728, "GyroStickMaxDeflectionYLeft");
            minDeflectionXLeftInput = CreateProfilePercentInput(page, deflectionColumnX[2], 728, "GyroStickMinDeflectionXLeft");
            minDeflectionYLeftInput = CreateProfilePercentInput(page, deflectionColumnX[3], 728, "GyroStickMinDeflectionYLeft");

            page.Controls.Add(CreateLabel("Right stick", 24, 768, ProfileText, false));
            maxDeflectionXRightInput = CreateProfilePercentInput(page, deflectionColumnX[0], 762, "GyroStickMaxDeflectionXRight");
            maxDeflectionYRightInput = CreateProfilePercentInput(page, deflectionColumnX[1], 762, "GyroStickMaxDeflectionYRight");
            minDeflectionXRightInput = CreateProfilePercentInput(page, deflectionColumnX[2], 762, "GyroStickMinDeflectionXRight");
            minDeflectionYRightInput = CreateProfilePercentInput(page, deflectionColumnX[3], 762, "GyroStickMinDeflectionYRight");

            page.Controls.Add(CreateDivider(24, 805));
            AddSectionHeading(page, "Orientation", 820,
                "Reset the current controller angle while gyro mouse or an Absolute/Hybrid " +
                "stick output is active.");
            AddMappingRow(page, lbl_reset_mouse, btn_reset_mouse, "Re-center gyro", 876, 24, 138, 456);

            page.Controls.Add(CreateDivider(24, 920));
            AddSectionHeading(page, "Mouse actions", 935,
                "Optional controller inputs available while gyro mouse is active.");
            const int mouseActionTop = 987;
            const int mouseActionRowSpacing = 34;
            string[] labels = { "Left click", "Right click", "Middle click", "Clench gyro", "Scroll up", "Scroll down" };
            for (int index = 0; index < gyroMouseButtons.Count; index++) {
                int column = index % 2;
                int row = index / 2;
                int labelX = column == 0 ? 24 : 323;
                int buttonX = column == 0 ? 114 : 423;
                AddMappingRow(page, null, gyroMouseButtons[index], labels[index],
                    mouseActionTop + row * mouseActionRowSpacing,
                    labelX, buttonX, column == 0 ? 181 : 171);
            }

            AddMappingRow(page, null, btn_gyro_mouse_inhibit, "Inhibit",
                mouseActionTop + 3 * mouseActionRowSpacing, 24, 114, 181);
            tip_reassign.SetToolTip(btn_gyro_mouse_inhibit,
                "Inhibit controller actions in mouse mode.");
            page.AutoScrollMinSize = new Size(0, 1144);
            return page;
        }

        private Panel BuildTouchpadPage() {
            Panel page = CreateProfilePage("Touchpad",
                "Use this controller's touch surface as an independently activated mouse or stick.");

            AddSectionHeading(page, "Physical control", 96,
                "Bind press and tap gestures independently, with optional tap-and-hold dragging.");
            AddMappingRow(page, null, btn_touchpad_click, "Touchpad click",
                157, 24, 114, 181);
            AddMappingRow(page, null, btn_touchpad_tap, "Tap",
                157, 323, 423, 171);
            tip_reassign.SetToolTip(btn_touchpad_tap,
                "A short one-finger touch with limited travel. Dragging or pressing the pad cancels it.");

            AddMappingRow(page, null, btn_touchpad_two_finger_tap, "Two-finger tap",
                194, 24, 114, 181);
            tip_reassign.SetToolTip(btn_touchpad_two_finger_tap,
                "A short two-finger touch with limited travel. Defaults to right click while touchpad mouse is active.");
            AddMappingRow(page, null, btn_touchpad_tap_hold, "Tap behavior",
                194, 323, 423, 171);
            tip_reassign.SetToolTip(btn_touchpad_tap_hold,
                "Choose whether holding one touch or a second tap holds the Tap action for dragging.");

            page.Controls.Add(CreateDivider(24, 242));
            AddSectionHeading(page, "Two-finger scroll", 257,
                "Enable vertical scrolling and optionally replace either direction's wheel action.");
            AddMappingRow(page, null, btn_touchpad_two_finger_scroll, "Scrolling",
                318, 24, 114, 480);
            tip_reassign.SetToolTip(btn_touchpad_two_finger_scroll,
                "Enable or disable recognition of two-finger vertical scroll gestures.");
            AddMappingRow(page, null, btn_touchpad_two_finger_scroll_up, "Scroll up",
                355, 24, 114, 181);
            AddMappingRow(page, null, btn_touchpad_two_finger_scroll_down, "Scroll down",
                355, 323, 423, 171);
            tip_reassign.SetToolTip(btn_touchpad_two_finger_scroll_up,
                "Defaults to wheel up while touchpad mouse is active. Assign any replacement input or combo.");
            tip_reassign.SetToolTip(btn_touchpad_two_finger_scroll_down,
                "Defaults to wheel down while touchpad mouse is active. Assign any replacement input or combo.");

            page.Controls.Add(CreateDivider(24, 403));
            AddSectionHeading(page, "Output activation", 418,
                "Enable mouse or floating-origin stick output independently with bindings.");
            page.Controls.Add(CreateLabel("Output", 24, 469, ProfileMuted, false, 8.25F));
            page.Controls.Add(CreateLabel("Activation", 114, 469, ProfileMuted, false, 8.25F));
            AddMappingRow(page, null, btn_active_touchpad_mouse, "Mouse",
                490, 24, 114, 480);
            AddMappingRow(page, null, touchpadStickActivationButtons[0], "Left stick",
                527, 24, 114, 480);
            AddMappingRow(page, null, touchpadStickActivationButtons[1], "Right stick",
                564, 24, 114, 480);

            AddMappingRow(page, null, btn_touchpad_sensitivity, "Mouse sensitivity",
                601, 24, 114, 181);
            AddMappingRow(page, null, btn_touchpad_stick_sensitivity, "Stick sensitivity",
                601, 323, 423, 171);
            tip_reassign.SetToolTip(btn_touchpad_sensitivity,
                "Scale touchpad mouse response. Choose a preset or right-click for 10% to 400%.");
            tip_reassign.SetToolTip(btn_touchpad_stick_sensitivity,
                "Scale floating touchpad-stick response. Choose a preset or right-click for 10% to 400%.");
            AddMappingRow(page, null, btn_touchpad_horizontal_scale, "Horizontal scale",
                638, 24, 114, 181);
            AddMappingRow(page, null, btn_touchpad_vertical_scale, "Vertical scale",
                638, 323, 423, 171);
            tip_reassign.SetToolTip(btn_touchpad_horizontal_scale,
                "Scale horizontal mouse and stick output from 0% to 100%. Zero locks the axis.");
            tip_reassign.SetToolTip(btn_touchpad_vertical_scale,
                "Scale vertical mouse and stick output from 0% to 100%. Zero locks the axis.");

            page.Controls.Add(CreateDivider(24, 686));
            AddSectionHeading(page, "Mouse actions", 701,
                "Optional controller inputs available while touchpad mouse is active.");
            const int actionTop = 753;
            const int actionSpacing = 34;
            string[] labels = {
                "Left click", "Right click", "Middle click", "Clench touchpad",
                "Scroll up", "Scroll down",
            };
            for (int index = 0; index < touchpadMouseButtons.Count; index++) {
                int column = index % 2;
                int row = index / 2;
                int labelX = column == 0 ? 24 : 323;
                int buttonX = column == 0 ? 114 : 423;
                AddMappingRow(page, null, touchpadMouseButtons[index], labels[index],
                    actionTop + row * actionSpacing,
                    labelX, buttonX, column == 0 ? 181 : 171);
            }

            AddMappingRow(page, null, btn_touchpad_inhibit, "Inhibit",
                actionTop + 3 * actionSpacing, 24, 114, 181);
            tip_reassign.SetToolTip(btn_touchpad_inhibit,
                "Inhibit controller actions in touchpad mouse mode.");
            AddMappingRow(page, null, btn_touchpad_click_lockout, "Click lockout",
                actionTop + 3 * actionSpacing, 323, 423, 171);
            tip_reassign.SetToolTip(btn_touchpad_click_lockout,
                "Prevent pointer movement while the physical touchpad is pressed.");
            page.AutoScrollMinSize = new Size(0, 921);
            return page;
        }

        private Panel BuildAdaptiveTriggersPage() {
            Panel page = CreateProfilePage("Adaptive triggers",
                "Add persistent DualSense trigger resistance even when a game outputs XInput or DS4.");

            AddSectionHeading(page, "Trigger effects", 96,
                "Effects are independent for L2 and R2, off by default, and apply after Apply is clicked.");
            page.Controls.Add(CreateLabel("L2", 24, 154, ProfileText, true, 11F));
            page.Controls.Add(CreateLabel("R2", 315, 154, ProfileText, true, 11F));

            page.Controls.Add(CreateLabel("Effect", 24, 202, ProfileText, false));
            adaptiveTriggerModeLeftSelector = CreateProfileChoiceSelector(125, 196, 165);
            page.Controls.Add(adaptiveTriggerModeLeftSelector);
            page.Controls.Add(CreateLabel("Effect", 315, 202, ProfileText, false));
            adaptiveTriggerModeRightSelector = CreateProfileChoiceSelector(416, 196, 178);
            page.Controls.Add(adaptiveTriggerModeRightSelector);
            foreach (ProfileChoiceSelector selector in new[] {
                adaptiveTriggerModeLeftSelector, adaptiveTriggerModeRightSelector,
            }) {
                foreach (var mode in ControllerMappings.AdaptiveTriggerModes)
                    selector.Items.Add(mode.Label);
                selector.SelectedIndexChanged += AdaptiveTriggerOptionChanged;
            }

            page.Controls.Add(CreateLabel("Start", 24, 244, ProfileText, false));
            adaptiveTriggerStartLeftInput = CreateProfilePercentInput(
                page, 125, 238, "AdaptiveTriggerStartLeft");
            page.Controls.Add(CreateLabel("Start", 315, 244, ProfileText, false));
            adaptiveTriggerStartRightInput = CreateProfilePercentInput(
                page, 416, 238, "AdaptiveTriggerStartRight");

            adaptiveTriggerSecondaryLeftLabel = CreateLabel(
                "Wall", 24, 286, ProfileText, false);
            page.Controls.Add(adaptiveTriggerSecondaryLeftLabel);
            adaptiveTriggerSecondaryLeftInput = CreateProfilePercentInput(
                page, 125, 280, "AdaptiveTriggerSecondaryLeft");
            adaptiveTriggerSecondaryRightLabel = CreateLabel(
                "Wall", 315, 286, ProfileText, false);
            page.Controls.Add(adaptiveTriggerSecondaryRightLabel);
            adaptiveTriggerSecondaryRightInput = CreateProfilePercentInput(
                page, 416, 280, "AdaptiveTriggerSecondaryRight");

            page.Controls.Add(CreateLabel("Strength", 24, 328, ProfileText, false));
            adaptiveTriggerStrengthLeftInput = CreateProfilePercentInput(
                page, 125, 322, "AdaptiveTriggerStrengthLeft");
            page.Controls.Add(CreateLabel("Strength", 315, 328, ProfileText, false));
            adaptiveTriggerStrengthRightInput = CreateProfilePercentInput(
                page, 416, 322, "AdaptiveTriggerStrengthRight");

            page.Controls.Add(CreateLabel("%", 181, 244, ProfileMuted, false));
            page.Controls.Add(CreateLabel("%", 181, 286, ProfileMuted, false));
            page.Controls.Add(CreateLabel("%", 181, 328, ProfileMuted, false));
            page.Controls.Add(CreateLabel("%", 472, 244, ProfileMuted, false));
            page.Controls.Add(CreateLabel("%", 472, 286, ProfileMuted, false));
            page.Controls.Add(CreateLabel("%", 472, 328, ProfileMuted, false));

            page.Controls.Add(CreateDivider(24, 380));
            AddSectionHeading(page, "How the modes feel", 397,
                "Resistance adds a firm pull after Start. Weapon creates a wall and break point. " +
                "Vibration pulses from Start, with the middle value controlling Frequency.");
            Label note = CreateLabel(
                "Weapon constrains its start/wall to the trigger's reliable hardware zones. " +
                "A strength of 0% disables the selected effect.",
                24, 459, ProfileMuted, false, 8.75F);
            note.AutoSize = false;
            note.Size = new Size(570, 42);
            page.Controls.Add(note);

            tip_reassign.SetToolTip(adaptiveTriggerStartLeftInput,
                "Where in the trigger pull the effect begins.");
            tip_reassign.SetToolTip(adaptiveTriggerStartRightInput,
                "Where in the trigger pull the effect begins.");
            tip_reassign.SetToolTip(adaptiveTriggerSecondaryLeftInput,
                "Weapon wall position or vibration frequency.");
            tip_reassign.SetToolTip(adaptiveTriggerSecondaryRightInput,
                "Weapon wall position or vibration frequency.");
            return page;
        }

        private Panel BuildDeviceBehaviorPage() {
            Panel page = CreateProfilePage("Device behavior",
                "Control power, input behavior, lighting, haptics, and audio for this profile.");

            AddSectionHeading(page, "Power", 96,
                "Choose when this controller powers itself off.");
            autoPowerOffCheckBox = CreateProfileCheckBox(
                "Power off when BetterJoy exits", 24, 153, "AutoPowerOff");
            homeLongPowerOffCheckBox = CreateProfileCheckBox(
                "Hold Home / Capture to power off", 315, 153, "HomeLongPowerOff");
            page.Controls.Add(autoPowerOffCheckBox);
            page.Controls.Add(homeLongPowerOffCheckBox);
            page.Controls.Add(CreateLabel("for", 315, 179, ProfileText, false));
            homeLongPowerOffHoldInput = CreateProfileSecondsInput(page, 340, 173, "HomeLongPowerOffHoldSeconds");
            page.Controls.Add(CreateLabel("seconds", 396, 179, ProfileText, false));
            tip_reassign.SetToolTip(homeLongPowerOffHoldInput,
                "DualSense and DualShock4 controllers have their own built-in ~5 second hold " +
                "timeout that powers them off regardless of this setting - BetterJoy can't " +
                "override that, so a value past 5s won't do anything for those specifically.");
            page.Controls.Add(CreateLabel("After inactivity", 24, 224, ProfileText, false));
            inactivitySelector = CreateProfileChoiceSelector(145, 218, 180);
            inactivitySelector.Items.AddRange(new object[] {
                "Never", "1 minute", "3 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "60 minutes",
            });
            inactivitySelector.SelectedIndexChanged += ProfileOptionControlChanged;
            page.Controls.Add(inactivitySelector);

            page.Controls.Add(CreateDivider(24, 270));
            AddSectionHeading(page, "Input behavior", 287,
                "Customize face buttons, mouse dragging, and gyro activation for this profile.");
            swapAbCheckBox = CreateProfileCheckBox("Swap A / B", 24, 318, "SwapAB");
            swapXyCheckBox = CreateProfileCheckBox("Swap X / Y", 190, 318, "SwapXY");
            dragToggleCheckBox = CreateProfileCheckBox(
                "Toggle mouse drag", 356, 318, "DragToggle");
            page.Controls.Add(swapAbCheckBox);
            page.Controls.Add(swapXyCheckBox);
            page.Controls.Add(dragToggleCheckBox);
            page.Controls.Add(CreateLabel("Output binding behavior", 24, 366, ProfileText, false));
            gyroActivationModeSelector = CreateProfileChoiceSelector(180, 360, 180);
            gyroActivationModeSelector.Items.AddRange(new object[] { "Hold", "Toggle" });
            gyroActivationModeSelector.SelectedIndexChanged += ProfileOptionControlChanged;
            page.Controls.Add(gyroActivationModeSelector);
            Label gyroHelp = CreateLabel(
                "Applies to gyro and touchpad mouse/stick activation bindings.",
                24, 399, ProfileMuted, false, 8.5F);
            page.Controls.Add(gyroHelp);

            page.Controls.Add(CreateDivider(24, 440));
            AddSectionHeading(page, "Lighting", 457,
                "Choose the lightbar color or Home LED behavior for this profile.");
            homeLedCheckBox = CreateProfileCheckBox(
                "Keep the Home LED on", 24, 514, "HomeLEDOn");
            page.Controls.Add(homeLedCheckBox);

            lightColorLabel = CreateLabel("Light color", 24, 520, ProfileText, false);
            lightColorButton = new Button {
                Location = new Point(114, 508),
                Size = new Size(180, 31),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
            };
            StyleStandardButton(lightColorButton, false);
            lightColorButton.Click += LightColorButton_Click;
            tip_reassign.SetToolTip(lightColorButton,
                "Choose the RGB color used by this controller's lightbar.");
            page.Controls.Add(lightColorLabel);
            page.Controls.Add(lightColorButton);

            lightingModeLabel = CreateLabel("Mode", 320, 514, ProfileText, false);
            page.Controls.Add(lightingModeLabel);
            lightingModeSelector = CreateProfileChoiceSelector(365, 508, 140);
            foreach (var mode in ControllerMappings.LightingModes)
                lightingModeSelector.Items.Add(mode.Label);
            lightingModeSelector.SelectedIndexChanged += ControllerAudioOptionChanged;
            page.Controls.Add(lightingModeSelector);
            tip_reassign.SetToolTip(lightingModeSelector,
                "User: always show Light color. Battery: show the controller's current charge " +
                "instead - green above 66%, yellow above 33%, red at or below 33% - updated only " +
                "when the charge crosses into a different band, to avoid constant chatter with " +
                "the controller over something that barely changes.");

            page.Controls.Add(CreateDivider(24, 558));
            AddSectionHeading(page, "Haptics", 575,
                "Control controller vibration for this profile.");
            rumbleModeLabel = CreateLabel("Rumble", 24, 638, ProfileText, false);
            page.Controls.Add(rumbleModeLabel);
            rumbleModeSelector = CreateProfileChoiceSelector(114, 632, 180);
            foreach (var mode in ControllerMappings.RumbleModes)
                rumbleModeSelector.Items.Add(mode.Label);
            rumbleModeSelector.SelectedIndexChanged += RumbleModeOptionChanged;
            page.Controls.Add(rumbleModeSelector);
            tip_reassign.SetToolTip(rumbleModeSelector,
                "Allow rumble, disable it completely, or suppress it only while gyro output is active.");

            page.Controls.Add(CreateDivider(24, 676));
            AddSectionHeading(page, "Audio", 693,
                "Route Windows audio to supported controller speakers, headphones, and microphones.");
            controllerAudioEnabledLabel = CreateLabel(
                "Controller audio", 24, 750, ProfileText, false);
            page.Controls.Add(controllerAudioEnabledLabel);
            controllerAudioEnabledSelector = CreateProfileChoiceSelector(145, 744, 180);
            controllerAudioEnabledSelector.Items.AddRange(new object[] {
                "Enable", "Disable", "Require headphones",
            });
            controllerAudioEnabledSelector.SelectedIndexChanged += ControllerAudioOptionChanged;
            page.Controls.Add(controllerAudioEnabledSelector);
            tip_reassign.SetToolTip(controllerAudioEnabledSelector,
                "Enable controller audio, disable it, or wait for Bluetooth headphones before streaming.");

            controllerAudioVolumeLabel = CreateLabel("Volume", 356, 750, ProfileText, false);
            page.Controls.Add(controllerAudioVolumeLabel);
            controllerAudioVolumeSelector = CreateProfileChoiceSelector(423, 744, 171);
            controllerAudioVolumeSelector.Items.AddRange(new object[] {
                "25%", "50%", "75%", "100%",
            });
            controllerAudioVolumeSelector.SelectedIndexChanged += ControllerAudioOptionChanged;
            page.Controls.Add(controllerAudioVolumeSelector);

            controllerAudioEndpointLabel = CreateLabel(
                "Audio endpoint", 24, 790, ProfileText, false);
            page.Controls.Add(controllerAudioEndpointLabel);
            controllerAudioEndpointSelector = CreateProfileChoiceSelector(145, 784, 320);
            controllerAudioEndpointSelector.SelectedIndexChanged += ControllerAudioOptionChanged;
            page.Controls.Add(controllerAudioEndpointSelector);

            controllerAudioTestButton = new Button {
                Location = new Point(475, 783),
                Size = new Size(119, 27),
                Text = "Test speaker",
            };
            StyleStandardButton(controllerAudioTestButton, false);
            controllerAudioTestButton.Click += ControllerAudioTestButton_Click;
            page.Controls.Add(controllerAudioTestButton);
            tip_reassign.SetToolTip(controllerAudioEndpointSelector,
                "Select the Windows audio endpoint for this controller, or leave it on Default. " +
                "USB: the playback device the test tone plays through, and the source Loopback " +
                "captures from when Default (your normal desktop audio). Bluetooth (DualShock 4 " +
                "and DualSense, experimental): the device live audio is captured from and streamed " +
                "to the controller's speaker while Controller audio is enabled and it's connected " +
                "over Bluetooth - Default there is the system's current default playback device, " +
                "i.e. whatever you're already listening through.");
            tip_reassign.SetToolTip(controllerAudioTestButton,
                "Play a short tone through the selected controller speaker.");

            controllerAudioUsbLoopbackLabel = CreateLabel(
                "Loopback audio", 24, 826, ProfileText, false);
            page.Controls.Add(controllerAudioUsbLoopbackLabel);
            controllerAudioUsbLoopbackSelector = CreateProfileChoiceSelector(145, 820, 180);
            controllerAudioUsbLoopbackSelector.Items.AddRange(new object[] { "Disabled", "Enabled" });
            controllerAudioUsbLoopbackSelector.SelectedIndexChanged += ControllerAudioOptionChanged;
            page.Controls.Add(controllerAudioUsbLoopbackSelector);
            tip_reassign.SetToolTip(controllerAudioUsbLoopbackSelector,
                "USB, experimental. Disabled by default. Instead of relying on this controller " +
                "being your Windows default playback device, capture the system's current default " +
                "device and stream it to the controller's speaker directly - useful when you'd " +
                "rather keep your normal speakers as the default output.");

            controllerBluetoothMicrophoneLabel = CreateLabel(
                "Built-in mic", 24, 870, ProfileText, false);
            page.Controls.Add(controllerBluetoothMicrophoneLabel);
            controllerBluetoothMicrophoneSelector = CreateProfileChoiceSelector(145, 864, 180);
            controllerBluetoothMicrophoneSelector.Items.AddRange(new object[] {
                "Enable", "Muted", "Disable",
            });
            controllerBluetoothMicrophoneSelector.SelectedIndexChanged +=
                ControllerAudioOptionChanged;
            page.Controls.Add(controllerBluetoothMicrophoneSelector);
            tip_reassign.SetToolTip(controllerBluetoothMicrophoneSelector,
                "Expose the DualSense built-in microphone while Bluetooth controller audio is " +
                "enabled. Muted starts it muted every time it connects, requiring a press of the " +
                "controller's own mute button before anything is actually captured.");

            micIndicatorLabel = CreateLabel("Mic indicator", 24, 910, ProfileText, false);
            page.Controls.Add(micIndicatorLabel);
            micIndicatorSelector = CreateProfileChoiceSelector(145, 904, 180);
            micIndicatorSelector.Items.AddRange(new object[] {
                "Disabled", "Inverted", "Enabled", "Enabled while disabled",
            });
            micIndicatorSelector.SelectedIndexChanged += ControllerAudioOptionChanged;
            page.Controls.Add(micIndicatorSelector);
            tip_reassign.SetToolTip(micIndicatorSelector,
                "What the mute-button LED shows. Enabled: lit while the mic is muted (matches " +
                "Sony's own behavior). Inverted: lit while the mic is active instead. Disabled: " +
                "never lit. Enabled while disabled: only ever lit when Built-in mic above is set " +
                "to Disabled, off otherwise regardless of mute state.");

            page.Controls.Add(CreateDivider(24, 950));
            AddSectionHeading(page, "Orientation", 967,
                "Only applies when this Joy-Con is used solo with no partner - whether it " +
                "defaults to horizontal (sideways) or vertical (self-paired) grip on connect.");
            AddMappingRow(page, null, btn_default_orientation, "Default orientation",
                1042, 24, 145, 449);

            page.AutoScrollMinSize = new Size(0, 1100);
            return page;
        }

        private Panel BuildVirtualControllerPage() {
            Panel page = CreateProfilePage("Virtual controller",
                "Inspect and test the virtual gamepad connected to this profile.");
            AddSectionHeading(page, "Output device", 96,
                "Windows exposes connected profiles as virtual game controllers.");

            page.Controls.Add(CreateLabel("Use as", 24, 163, ProfileText, false));
            useAsSelector = CreateProfileChoiceSelector(115, 157, 300);
            useAsSelector.Items.AddRange(new object[] {
                "Xbox 360 controller", "Xbox 360 controller (VIIPER)", "DualShock 4 controller",
                "DualSense controller (VIIPER)", "Disabled",
            });
            useAsSelector.SelectedIndexChanged += ProfileOptionControlChanged;
            page.Controls.Add(useAsSelector);
            tip_reassign.SetToolTip(useAsSelector,
                "Xbox 360 controller and DualShock 4 controller use ViGEmBus, the standard " +
                "virtual-controller driver most setups already have. The (VIIPER) options are " +
                "an alternative backend (experimental) using VIIPER/usbip-win2 instead - the " +
                "same optional drivers bundled for the DualSense Bluetooth microphone. DualSense " +
                "has no ViGEmBus equivalent at all - VIIPER is the only way to expose a real " +
                "PS5-shaped virtual controller here.");

            Panel deviceCard = new Panel {
                Location = new Point(24, 210),
                Size = new Size(570, 142),
                BackColor = Color.FromArgb(38, 39, 41),
            };
            deviceCard.Paint += (sender, e) => ControlPaint.DrawBorder(
                e.Graphics, deviceCard.ClientRectangle, ProfileBorder, ButtonBorderStyle.Solid);
            Label icon = CreateLabel("◎", 19, 22, ProfileAccent, true, 21F);
            virtualControllerNameLabel = CreateLabel("Virtual controller", 67, 22, ProfileText, true, 11F);
            virtualControllerDetailLabel = CreateLabel("Select a controller profile to view its output.",
                67, 51, ProfileMuted, false, 9F);
            virtualControllerDetailLabel.AutoSize = false;
            virtualControllerDetailLabel.Size = new Size(470, 42);
            StyleStandardButton(gameControllersButton, false);
            gameControllersButton.Location = new Point(67, 95);
            gameControllersButton.Size = new Size(210, 32);
            gameControllersButton.Text = "Open Game Controllers...";
            deviceCard.Controls.Add(icon);
            deviceCard.Controls.Add(virtualControllerNameLabel);
            deviceCard.Controls.Add(virtualControllerDetailLabel);
            deviceCard.Controls.Add(gameControllersButton);
            page.Controls.Add(deviceCard);

            page.Controls.Add(CreateDivider(24, 379));
            AddSectionHeading(page, "About testing", 396,
                "Connected profiles open the matching virtual controller's Properties dialog. " +
                "Disconnected profiles open the standard Windows Game Controllers list.");
            return page;
        }

        private Panel BuildGlobalPage() {
            Panel page = CreateProfilePage("Global options",
                "Application-wide behavior shared by every controller profile.");

            AddSectionHeading(page, "Application", 96,
                "Choose how the BetterJoy window behaves when the application starts.");
            page.Controls.Add(CreateGlobalCheckBox(
                "Start in the system tray", 24, 153, "StartInTray"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Hide the status log", 315, 153, "HideStatus"));

            page.Controls.Add(CreateDivider(24, 203));
            AddSectionHeading(page, "Controller discovery", 220,
                "Control background scanning and automatic registration of supported controllers.");
            page.Controls.Add(CreateGlobalCheckBox(
                "Scan for new controllers", 24, 277, "PassiveScan"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Automatically add supported controllers", 315, 277, "AutoAddControllers"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Block auto-add over USB", 24, 310, "BlockAutoAddUSB"));

            page.Controls.Add(CreateDivider(24, 354));
            AddSectionHeading(page, "Controller support", 371,
                "Configure calibration access and physical-controller hiding for all profiles.");
            page.Controls.Add(CreateGlobalCheckBox(
                "Allow calibration", 24, 428, "AllowCalibration"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Use HidHide", 315, 428, "UseHidHide"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Unhide on exit", 315, 461, "UnhideOnExit"));

            page.Controls.Add(CreateDivider(24, 505));
            AddSectionHeading(page, "Motion server", 522,
                "Expose controller motion through the CemuHook-compatible motion server.");
            page.Controls.Add(CreateGlobalCheckBox(
                "Enable motion server", 24, 579, "MotionServer"));

            page.Controls.Add(CreateDivider(24, 629));
            AddSectionHeading(page, "Microphone backend", 646,
                "Which Windows device the DualSense's built-in Bluetooth microphone is presented as");
            page.Controls.Add(CreateGlobalCheckBox(
                "Use VIIPER for DualSense microphone", 24, 703,
                "UseViiperForDualSenseMicrophone"));
            tip_reassign.SetToolTip(globalOptionCheckBoxes["UseViiperForDualSenseMicrophone"],
                "On: prefer VIIPER (emulates a real USB audio device). Off, or if VIIPER isn't " +
                "available: fall back to Steam Streaming Microphone instead - the same virtual " +
                "audio device Valve's own Steam Link/Remote Play voice chat uses, Microsoft " +
                "attestation-signed, no test-signing mode required.");

            page.Controls.Add(CreateDivider(24, 753));
            AddSectionHeading(page, "Debug logging", 770,
                "Diagnostic file logs can grow quickly; enable them only while troubleshooting.");
            page.Controls.Add(CreateGlobalCheckBox(
                "Debug logging", 24, 827, "DebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "DualSense debug logging", 24, 861, "DualSenseDebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "DualShock 4 debug logging", 315, 861, "DualShock4DebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Gyro mouse debug logging", 24, 895, "GyroMouseDebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Gyro stick debug logging", 315, 895, "GyroStickDebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Auto-calibration debug logging", 24, 929, "AutoCalDebugLogging"));

            page.Controls.Add(CreateLabel(
                "Some changes, including HidHide and startup behavior, take effect after restart.",
                24, 974, ProfileMuted, false, 8.5F));
            page.AutoScrollMinSize = new Size(0, 1016);
            return page;
        }

        private void AddSectionHeading(Panel page, string title, int top, string description) {
            page.Controls.Add(CreateLabel(title, 24, top, ProfileText, true, 12F));
            Label help = CreateLabel(description, 24, top + 27, ProfileMuted, false, 9F);
            help.AutoSize = false;
            // A transparent WinForms Label still paints its full rectangular bounds. Keeping a
            // one-line helper at the old two-line height let that invisible lower half sit on
            // top of the selector below it, visibly shaving several pixels off the control.
            // Reserve the taller box only for copy that can actually wrap to a second line.
            help.Size = new Size(570, description.Length > 100 ? 40 : 22);
            page.Controls.Add(help);
        }

        private void AddMappingRow(Panel page, Label label, SplitButton button, string text,
                                   int top, int labelX, int buttonX, int buttonWidth) {
            if (label == null)
                label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Location = new Point(labelX, top + 7);
            label.Size = new Size(Math.Max(80, buttonX - labelX - 12), 22);
            label.ForeColor = ProfileText;
            label.BackColor = Color.Transparent;
            label.Font = new Font("Segoe UI", 9F);
            label.TextAlign = ContentAlignment.MiddleLeft;
            page.Controls.Add(label);

            StyleMappingButton(button);
            button.Location = new Point(buttonX, top);
            button.Size = new Size(buttonWidth, 31);
            page.Controls.Add(button);
        }

        private Panel CreateDivider(int left, int top) {
            return new Panel {
                Location = new Point(left, top),
                Size = new Size(570, 1),
                BackColor = ProfileBorder,
            };
        }

        private Label CreateLabel(string text, int left, int top, Color color, bool bold,
                                  float size = 9F) {
            return new Label {
                AutoSize = true,
                Text = text,
                Location = new Point(left, top),
                ForeColor = color,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            };
        }

        private ProfileChoiceSelector CreateProfileChoiceSelector(int left, int top, int width) {
            ProfileChoiceSelector selector = new ProfileChoiceSelector();
            StyleMappingButton(selector);
            selector.Location = new Point(left, top);
            selector.Size = new Size(width, 31);
            selector.StyleMenu(ProfileSurface, ProfileText, new Font("Segoe UI", 9F));
            return selector;
        }

        private CheckBox CreateProfileCheckBox(string text, int left, int top, string optionKey) {
            return CreateOptionCheckBox(
                text, left, top, optionKey, ProfileOptionControlChanged);
        }

        private CheckBox CreateGlobalCheckBox(string text, int left, int top, string optionKey) {
            CheckBox checkBox = CreateOptionCheckBox(
                text, left, top, optionKey, GlobalOptionControlChanged);
            globalOptionCheckBoxes.Add(optionKey, checkBox);
            return checkBox;
        }

        private CheckBox CreateOptionCheckBox(string text, int left, int top, string optionKey,
                                              EventHandler changed) {
            CheckBox checkBox = new DarkCheckBox {
                AutoSize = true,
                Text = text,
                Tag = optionKey,
                Location = new Point(left, top),
                FlatStyle = FlatStyle.Flat,
                ForeColor = ProfileText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9F),
                Cursor = Cursors.Hand,
            };
            checkBox.CheckedChanged += changed;
            return checkBox;
        }

        private void GlobalOptionControlChanged(object sender, EventArgs e) {
            if (updatingGlobalOptions)
                return;

            CheckBox checkBox = (CheckBox)sender;
            try {
                ApplicationSettings.SetValue(
                    (string)checkBox.Tag, checkBox.Checked.ToString().ToLowerInvariant());
            } catch (ConfigurationErrorsException ex) {
                LoadGlobalOptions();
                MessageBox.Show("Unable to save global setting: " + ex.Message,
                    "BetterJoy", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadGlobalOptions() {
            updatingGlobalOptions = true;
            try {
                foreach (KeyValuePair<string, CheckBox> option in globalOptionCheckBoxes)
                    option.Value.Checked = ApplicationSettings.BoolValue(option.Key);
            } finally {
                updatingGlobalOptions = false;
            }
        }

        // Shared by gyro deflection and adaptive-trigger percentages. Not NumericUpDown - its
        // spin-button portion can't be recolored to match the dark flat theme every other control gets
        // via FlatStyle.Flat. A themed TextBox with digit-only input matches far better.
        private TextBox CreateProfilePercentInput(Panel page, int left, int top, string optionKey) {
            TextBox input = new TextBox {
                Location = new Point(left, top),
                Size = new Size(50, 25),
                TextAlign = HorizontalAlignment.Center,
                BackColor = ProfileSurface,
                ForeColor = ProfileText,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9F),
                Tag = optionKey,
            };
            input.KeyPress += (sender, e) => {
                if (!Char.IsDigit(e.KeyChar) && !Char.IsControl(e.KeyChar))
                    e.Handled = true;
            };
            input.Leave += ProfilePercentInput_Leave;
            page.Controls.Add(input);
            return input;
        }

        // Commits on focus-loss rather than through the immediate-commit
        // ProfileOptionControlChanged dispatcher every other control here uses (ComboBox.
        // SelectedIndexChanged/CheckBox.CheckedChanged) - a deferred-commit TextBox doesn't fit
        // that shape cleanly, so it gets its own handler instead.
        private void ProfilePercentInput_Leave(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            TextBox input = (TextBox)sender;
            int value;
            if (!Int32.TryParse(input.Text, out value))
                value = 0;
            value = Math.Max(0, Math.Min(100, value));
            input.Text = value.ToString();
            ControllerMappings.SetOptionValue(
                SelectedProfileId, AdaptiveTriggerPercentOptionKey(input), value.ToString());
        }

        // The six Adaptive trigger Start/Secondary/Strength boxes reuse the same three physical
        // controls per side across all three modes instead of showing nine separate ones - Tag
        // holds the old flat key from CreateProfilePercentInput's call site, but a commit needs
        // to land in whichever mode is actually selected right now, so it's resolved dynamically
        // here instead. Every other percent input (gyro deflection) just uses its own Tag as-is.
        private string AdaptiveTriggerPercentOptionKey(TextBox input) {
            if (input == adaptiveTriggerStartLeftInput)
                return ControllerMappings.AdaptiveTriggerFieldKey(
                    "Left", "Start", AdaptiveTriggerModeValue(adaptiveTriggerModeLeftSelector));
            if (input == adaptiveTriggerSecondaryLeftInput)
                return ControllerMappings.AdaptiveTriggerFieldKey(
                    "Left", "Secondary", AdaptiveTriggerModeValue(adaptiveTriggerModeLeftSelector));
            if (input == adaptiveTriggerStrengthLeftInput)
                return ControllerMappings.AdaptiveTriggerFieldKey(
                    "Left", "Strength", AdaptiveTriggerModeValue(adaptiveTriggerModeLeftSelector));
            if (input == adaptiveTriggerStartRightInput)
                return ControllerMappings.AdaptiveTriggerFieldKey(
                    "Right", "Start", AdaptiveTriggerModeValue(adaptiveTriggerModeRightSelector));
            if (input == adaptiveTriggerSecondaryRightInput)
                return ControllerMappings.AdaptiveTriggerFieldKey(
                    "Right", "Secondary", AdaptiveTriggerModeValue(adaptiveTriggerModeRightSelector));
            if (input == adaptiveTriggerStrengthRightInput)
                return ControllerMappings.AdaptiveTriggerFieldKey(
                    "Right", "Strength", AdaptiveTriggerModeValue(adaptiveTriggerModeRightSelector));
            return (string)input.Tag;
        }

        // Same shape as CreateProfilePercentInput/ProfilePercentInput_Leave above, just with a
        // 1-10 range instead of 0-100 - a separate clamp range doesn't fit that one cleanly.
        private TextBox CreateProfileSecondsInput(Panel page, int left, int top, string optionKey) {
            TextBox input = new TextBox {
                Location = new Point(left, top),
                Size = new Size(40, 25),
                TextAlign = HorizontalAlignment.Center,
                BackColor = ProfileSurface,
                ForeColor = ProfileText,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9F),
                Tag = optionKey,
            };
            input.KeyPress += (sender, e) => {
                if (!Char.IsDigit(e.KeyChar) && !Char.IsControl(e.KeyChar))
                    e.Handled = true;
            };
            input.Leave += ProfileSecondsInput_Leave;
            page.Controls.Add(input);
            return input;
        }

        private void ProfileSecondsInput_Leave(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            TextBox input = (TextBox)sender;
            int value;
            if (!Int32.TryParse(input.Text, out value))
                value = 2;
            value = Math.Max(1, Math.Min(10, value));
            input.Text = value.ToString();
            ControllerMappings.SetOptionValue(SelectedProfileId, (string)input.Tag, value.ToString());
        }

        private bool IsDeferredCommitProfileInput(Control control) {
            return control != null && (
                control == maxDeflectionXLeftInput || control == maxDeflectionYLeftInput ||
                control == maxDeflectionXRightInput || control == maxDeflectionYRightInput ||
                control == minDeflectionXLeftInput || control == minDeflectionYLeftInput ||
                control == minDeflectionXRightInput || control == minDeflectionYRightInput ||
                control == adaptiveTriggerStartLeftInput ||
                control == adaptiveTriggerSecondaryLeftInput ||
                control == adaptiveTriggerStrengthLeftInput ||
                control == adaptiveTriggerStartRightInput ||
                control == adaptiveTriggerSecondaryRightInput ||
                control == adaptiveTriggerStrengthRightInput ||
                control == homeLongPowerOffHoldInput);
        }

        private void ProfileOptionControlChanged(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            CheckBox checkBox = sender as CheckBox;
            if (checkBox != null) {
                ControllerMappings.SetOptionValue(
                    SelectedProfileId, (string)checkBox.Tag, checkBox.Checked.ToString().ToLowerInvariant());
            } else if (sender == useAsSelector) {
                string value;
                switch (useAsSelector.SelectedIndex) {
                    case 0: value = ControllerMappings.UseAsXbox360; break;
                    case 1: value = ControllerMappings.UseAsXbox360Viiper; break;
                    case 2: value = ControllerMappings.UseAsDualShock4; break;
                    case 3: value = ControllerMappings.UseAsDualSenseViiper; break;
                    default: value = ControllerMappings.UseAsNone; break;
                }
                ControllerMappings.SetOptionValue(SelectedProfileId, "UseAs", value);
                UpdateProfilePresentation(SelectedProfile);
            } else if (sender == gyroActivationModeSelector) {
                ControllerMappings.SetOptionValue(SelectedProfileId, "GyroHoldToggle",
                    (gyroActivationModeSelector.SelectedIndex == 0).ToString().ToLowerInvariant());
            } else if (sender == inactivitySelector) {
                int minutes = InactivityMinutesFromText(inactivitySelector.Text);
                ControllerMappings.SetOptionValue(
                    SelectedProfileId, "PowerOffInactivity", minutes.ToString());
            }
        }

        private void RumbleModeOptionChanged(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            var modes = ControllerMappings.RumbleModes;
            int index = rumbleModeSelector.SelectedIndex;
            string mode = index >= 0 && index < modes.Length ? modes[index].Value : modes[0].Value;
            ControllerMappings.SetOptionValue(SelectedProfileId, "EnableRumble", mode);
        }

        private void AdaptiveTriggerOptionChanged(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            ProfileChoiceSelector selector = (ProfileChoiceSelector)sender;
            string optionKey = selector == adaptiveTriggerModeLeftSelector
                ? "AdaptiveTriggerModeLeft"
                : "AdaptiveTriggerModeRight";
            ControllerMappings.SetOptionValue(SelectedProfileId, optionKey,
                AdaptiveTriggerModeValue(selector));
            // The three boxes below are shared across all three modes (see
            // AdaptiveTriggerPercentOptionKey) - switching modes has to refresh what they display
            // to that mode's own stored values, not just update Enabled state.
            LoadAdaptiveTriggerFieldInputs();
            UpdateAdaptiveTriggerControlState(true);
        }

        // Populates the six shared Start/Secondary/Strength boxes from whichever mode each side
        // is currently on - called on profile load and whenever a mode dropdown changes.
        private void LoadAdaptiveTriggerFieldInputs() {
            string leftMode = AdaptiveTriggerModeValue(adaptiveTriggerModeLeftSelector);
            string rightMode = AdaptiveTriggerModeValue(adaptiveTriggerModeRightSelector);
            adaptiveTriggerStartLeftInput.Text = ControllerMappings.AdaptiveTriggerFieldValue(
                SelectedProfileId, "Left", "Start", leftMode, 30).ToString();
            adaptiveTriggerSecondaryLeftInput.Text = ControllerMappings.AdaptiveTriggerFieldValue(
                SelectedProfileId, "Left", "Secondary", leftMode, 70).ToString();
            adaptiveTriggerStrengthLeftInput.Text = ControllerMappings.AdaptiveTriggerFieldValue(
                SelectedProfileId, "Left", "Strength", leftMode, 50).ToString();
            adaptiveTriggerStartRightInput.Text = ControllerMappings.AdaptiveTriggerFieldValue(
                SelectedProfileId, "Right", "Start", rightMode, 30).ToString();
            adaptiveTriggerSecondaryRightInput.Text = ControllerMappings.AdaptiveTriggerFieldValue(
                SelectedProfileId, "Right", "Secondary", rightMode, 70).ToString();
            adaptiveTriggerStrengthRightInput.Text = ControllerMappings.AdaptiveTriggerFieldValue(
                SelectedProfileId, "Right", "Strength", rightMode, 50).ToString();
        }

        private static string AdaptiveTriggerModeValue(ProfileChoiceSelector selector) {
            int index = selector?.SelectedIndex ?? 0;
            var modes = ControllerMappings.AdaptiveTriggerModes;
            return index >= 0 && index < modes.Length ? modes[index].Value : modes[0].Value;
        }

        private static void LoadAdaptiveTriggerMode(ProfileChoiceSelector selector, string value) {
            if (selector == null)
                return;
            var modes = ControllerMappings.AdaptiveTriggerModes;
            int index = Array.FindIndex(modes,
                m => String.Equals(m.Value, (value ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            selector.SelectedIndex = Math.Max(0, index);
        }

        private void UpdateAdaptiveTriggerControlState(bool hasProfile) {
            UpdateAdaptiveTriggerSide(adaptiveTriggerModeLeftSelector,
                adaptiveTriggerStartLeftInput, adaptiveTriggerSecondaryLeftInput,
                adaptiveTriggerStrengthLeftInput, adaptiveTriggerSecondaryLeftLabel,
                hasProfile);
            UpdateAdaptiveTriggerSide(adaptiveTriggerModeRightSelector,
                adaptiveTriggerStartRightInput, adaptiveTriggerSecondaryRightInput,
                adaptiveTriggerStrengthRightInput, adaptiveTriggerSecondaryRightLabel,
                hasProfile);
        }

        private void UpdateAdaptiveTriggerSide(ProfileChoiceSelector selector, TextBox start,
            TextBox secondary, TextBox strength, Label secondaryLabel, bool hasProfile) {
            if (selector == null)
                return;
            string mode = AdaptiveTriggerModeValue(selector);
            bool active = hasProfile && mode != "off";
            selector.Enabled = hasProfile;
            start.Enabled = active;
            strength.Enabled = active;
            secondary.Enabled = active && (mode == "weapon" || mode == "vibration");
            secondaryLabel.Text = mode == "vibration" ? "Frequency" : "Wall";
            secondaryLabel.ForeColor = secondary.Enabled ? ProfileText : ProfileMuted;
        }

        private void LightColorButton_Click(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            byte red, green, blue;
            ControllerMappings.GetLightColor(SelectedProfileId, out red, out green, out blue);
            using (var dialog = new ColorDialog()) {
                dialog.Color = Color.FromArgb(red, green, blue);
                dialog.AllowFullOpen = true;
                dialog.FullOpen = true;
                dialog.SolidColorOnly = true;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string value = String.Format("#{0:X2}{1:X2}{2:X2}",
                    dialog.Color.R, dialog.Color.G, dialog.Color.B);
                ControllerMappings.SetOptionValue(SelectedProfileId, "LightColor", value);
                UpdateLightColorButton(value);
            }
        }

        private void UpdateLightColorButton(string value) {
            if (lightColorButton == null)
                return;

            string normalized = ControllerMappings.NormalizeLightColor(value);
            byte red, green, blue;
            ControllerMappings.TryParseLightColor(normalized, out red, out green, out blue);
            Color color = Color.FromArgb(red, green, blue);
            lightColorButton.Text = normalized;
            lightColorButton.BackColor = color;
            lightColorButton.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color);
            int luminance = red * 299 + green * 587 + blue * 114;
            lightColorButton.ForeColor = luminance >= 150000 ? Color.Black : Color.White;
        }

        private static int ClosestAudioVolume(int value) {
            int[] choices = { 25, 50, 75, 100 };
            return choices.OrderBy(choice => Math.Abs(choice - value)).First();
        }

        private void LoadControllerAudioEndpoints() {
            if (controllerAudioEndpointSelector == null)
                return;

            string selectedId = ControllerMappings.OptionValue(
                SelectedProfileId, "ControllerAudioEndpointId");
            // Empty Id (String.Empty) is what an unset ControllerAudioEndpointId already
            // persists as - "Default" is just a real, explicitly selectable entry for that same
            // value rather than leaving the dropdown showing nothing until the user happens to
            // pick something. What "Default" actually resolves to differs by transport/feature
            // (system default render device for Bluetooth capture and the ordinary "set the
            // controller as your Windows default playback device" USB flow; the controller's own
            // endpoint, via UsbAudioEndpointNameHint, for USB loopback specifically - see
            // UsbAudioLoopback.StartInner) - explained in the tooltip below rather than trying to
            // show one universally-correct device name here.
            var defaultEndpoint = new ControllerAudioEndpoint { Id = String.Empty, Name = "Default" };
            List<ControllerAudioEndpoint> endpoints = ControllerAudio.GetRenderEndpoints();
            controllerAudioEndpointSelector.SelectedIndex = -1;
            controllerAudioEndpointSelector.Items.Clear();
            controllerAudioEndpointSelector.Items.Add(defaultEndpoint);
            controllerAudioEndpointSelector.Items.AddRange(endpoints.Cast<object>().ToArray());

            ControllerAudioEndpoint selected = String.IsNullOrEmpty(selectedId)
                ? defaultEndpoint
                : endpoints.FirstOrDefault(endpoint =>
                    String.Equals(endpoint.Id, selectedId, StringComparison.Ordinal));
            controllerAudioEndpointSelector.SelectedItem = selected ?? defaultEndpoint;
        }

        private void ControllerAudioOptionChanged(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            if (sender == controllerAudioEnabledSelector) {
                string mode = controllerAudioEnabledSelector.SelectedIndex == 0
                    ? ControllerMappings.ModeEnable
                    : (controllerAudioEnabledSelector.SelectedIndex == 2
                        ? ControllerMappings.AudioModeRequireHeadphones
                        : ControllerMappings.ModeDisable);
                ControllerMappings.SetOptionValue(
                    SelectedProfileId, "ControllerAudioEnabled", mode);
            } else if (sender == controllerAudioVolumeSelector) {
                ControllerMappings.SetOptionValue(SelectedProfileId, "ControllerAudioVolume",
                    controllerAudioVolumeSelector.Text.TrimEnd('%'));
            } else if (sender == controllerAudioEndpointSelector) {
                ControllerAudioEndpoint endpoint =
                    controllerAudioEndpointSelector.SelectedItem as ControllerAudioEndpoint;
                ControllerMappings.SetOptionValue(SelectedProfileId, "ControllerAudioEndpointId",
                    endpoint?.Id ?? String.Empty);
            } else if (sender == controllerBluetoothMicrophoneSelector) {
                string mode = controllerBluetoothMicrophoneSelector.SelectedIndex == 0
                    ? ControllerMappings.ModeEnable
                    : (controllerBluetoothMicrophoneSelector.SelectedIndex == 1
                        ? ControllerMappings.MicrophoneModeStartMuted
                        : ControllerMappings.ModeDisable);
                ControllerMappings.SetOptionValue(
                    SelectedProfileId, "ControllerBluetoothMicrophoneEnabled", mode);
            } else if (sender == micIndicatorSelector) {
                string[] modes = {
                    ControllerMappings.MicIndicatorModeDisabled,
                    ControllerMappings.MicIndicatorModeInverted,
                    ControllerMappings.MicIndicatorModeEnabled,
                    ControllerMappings.MicIndicatorModeEnabledWhileDisabled,
                };
                ControllerMappings.SetOptionValue(
                    SelectedProfileId, "MicIndicatorMode", modes[micIndicatorSelector.SelectedIndex]);
            } else if (sender == controllerAudioUsbLoopbackSelector) {
                ControllerMappings.SetOptionValue(SelectedProfileId, "ControllerAudioUsbLoopback",
                    (controllerAudioUsbLoopbackSelector.SelectedIndex == 1).ToString().ToLowerInvariant());
            } else if (sender == lightingModeSelector) {
                var modes = ControllerMappings.LightingModes;
                int index = lightingModeSelector.SelectedIndex;
                ControllerMappings.SetOptionValue(SelectedProfileId, "LightingMode",
                    index >= 0 && index < modes.Length ? modes[index].Value : modes[0].Value);
            }
            UpdateControllerAudioControlState();
        }

        private async void ControllerAudioTestButton_Click(object sender, EventArgs e) {
            ControllerProfileInfo profile = SelectedProfile;
            ControllerAudioEndpoint endpoint =
                controllerAudioEndpointSelector?.SelectedItem as ControllerAudioEndpoint;
            if (profile == null || !profile.IsConnected || !profile.IsUsb || endpoint == null)
                return;

            // "Default" (empty Id) means the controller's own endpoint for USB - resolve it the
            // same way UsbAudioLoopback does at runtime, rather than PlayTestToneAsync rejecting
            // an empty endpoint ID outright.
            string endpointId = endpoint.Id;
            if (String.IsNullOrEmpty(endpointId) && !String.IsNullOrEmpty(profile.AudioEndpointNameHint)) {
                ControllerAudioEndpoint resolved = ControllerAudio.GetRenderEndpoints()
                    .FirstOrDefault(candidate => candidate.Name.IndexOf(
                        profile.AudioEndpointNameHint, StringComparison.OrdinalIgnoreCase) >= 0);
                endpointId = resolved?.Id ?? String.Empty;
            }
            if (String.IsNullOrEmpty(endpointId)) {
                MessageBox.Show(this,
                    "BetterJoy could not automatically find this controller's Windows audio " +
                    "device. Pick it manually from the dropdown instead of Default.",
                    "Controller audio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int volume = ControllerMappings.IntOption(
                profile.ProfileId, "ControllerAudioVolume", 75);
            string oldText = controllerAudioTestButton.Text;
            controllerAudioTestButton.Enabled = false;
            controllerAudioTestButton.Text = "Playing...";
            try {
                serviceClient.PrepareUsbAudio(profile.PadId, volume);
                await Task.Delay(100);
                await ControllerAudio.PlayTestToneAsync(endpointId, volume);
            } catch (Exception ex) {
                MessageBox.Show(this,
                    "BetterJoy could not play through that controller endpoint.\r\n\r\n" + ex.Message,
                    "Controller audio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                controllerAudioTestButton.Text = oldText;
                UpdateControllerAudioControlState();
            }
        }

        private void UpdateControllerAudioControlState() {
            ControllerProfileInfo selected = SelectedProfile;
            // No longer USB-only: Enabled/Volume/Endpoint also drive DualShock4Controller's
            // automatic Bluetooth streaming lifecycle now (see Program.cs's
            // ApplyControllerProfileOptions), so they need to be editable over Bluetooth too.
            bool available = selected != null && selected.IsConnected &&
                (selected.Kind == ControllerKind.DualSense ||
                 selected.Kind == ControllerKind.DualShock4);
            foreach (Control label in new Control[] {
                controllerAudioEnabledLabel, controllerAudioVolumeLabel,
                controllerAudioEndpointLabel,
            }) {
                if (label != null)
                    label.Enabled = available;
            }
            if (controllerAudioEnabledSelector != null)
                controllerAudioEnabledSelector.Enabled = available;
            if (controllerAudioVolumeSelector != null)
                controllerAudioVolumeSelector.Enabled = available;
            if (controllerAudioEndpointSelector != null)
                controllerAudioEndpointSelector.Enabled = available;
            if (controllerAudioTestButton != null)
                controllerAudioTestButton.Enabled = available && selected.IsUsb &&
                    controllerAudioEndpointSelector.SelectedItem is ControllerAudioEndpoint;
            if (controllerAudioUsbLoopbackSelector != null)
                controllerAudioUsbLoopbackSelector.Enabled = available && selected.IsUsb;
            if (controllerAudioUsbLoopbackLabel != null)
                controllerAudioUsbLoopbackLabel.Enabled = available && selected.IsUsb;
            if (controllerBluetoothMicrophoneSelector != null) {
                bool supportsBluetoothMicrophone = selected != null &&
                    selected.Kind == ControllerKind.DualSense;
                controllerBluetoothMicrophoneSelector.Enabled = available &&
                    supportsBluetoothMicrophone;
                if (controllerBluetoothMicrophoneLabel != null)
                    controllerBluetoothMicrophoneLabel.Enabled = available &&
                        supportsBluetoothMicrophone;
                if (micIndicatorSelector != null)
                    micIndicatorSelector.Enabled = available && supportsBluetoothMicrophone;
                if (micIndicatorLabel != null)
                    micIndicatorLabel.Enabled = available && supportsBluetoothMicrophone;
            }
        }

        private void LoadProfileOptions(bool hasProfile) {
            // LoadProfileOptions can be triggered by the hardware-refresh timer (twice a second)
            // whenever the controller list changes, not only by the user picking a different
            // profile - so this runs far more often than a plain profile switch. Unconditionally
            // nulling ActiveControl here (an earlier version of this fix) stole keyboard focus
            // from WHATEVER control had it on every single tick, not just these 8 boxes -
            // including mid-calibration or any other dialog/input in progress, which is exactly
            // the kind of thing that looks like the app "quit" partway through. Only force a
            // commit when one of the deferred percentage TextBoxes specifically is focused.
            if (IsDeferredCommitProfileInput(ActiveControl))
                this.ActiveControl = null;

            Control[] controls = {
                useAsSelector, inactivitySelector, gyroActivationModeSelector,
                btn_gyro_analog_sliders, btn_gyro_mouse_inhibit, btn_default_orientation,
                btn_touchpad_inhibit, btn_touchpad_sensitivity,
                btn_touchpad_stick_sensitivity, btn_touchpad_tap_hold,
                btn_touchpad_click_lockout, btn_touchpad_two_finger_scroll,
                btn_touchpad_horizontal_scale, btn_touchpad_vertical_scale,
                autoPowerOffCheckBox, homeLongPowerOffCheckBox, dragToggleCheckBox,
                swapAbCheckBox, swapXyCheckBox, rumbleModeSelector, homeLedCheckBox,
                lightColorButton, lightingModeSelector, controllerAudioEnabledSelector, controllerAudioVolumeSelector,
                controllerAudioEndpointSelector, controllerAudioTestButton,
                controllerAudioUsbLoopbackSelector, controllerBluetoothMicrophoneSelector,
                micIndicatorSelector,
                gyroStickModeSelector, gyroStickModeRightSelector,
                gyroStickAxisXSelector, gyroStickAxisXRightSelector,
                invertStickXCheckBox, invertStickYCheckBox,
                invertStickXRightCheckBox, invertStickYRightCheckBox,
                maxDeflectionXLeftInput, maxDeflectionYLeftInput,
                maxDeflectionXRightInput, maxDeflectionYRightInput,
                minDeflectionXLeftInput, minDeflectionYLeftInput,
                minDeflectionXRightInput, minDeflectionYRightInput,
                adaptiveTriggerModeLeftSelector, adaptiveTriggerModeRightSelector,
                adaptiveTriggerStartLeftInput, adaptiveTriggerSecondaryLeftInput,
                adaptiveTriggerStrengthLeftInput, adaptiveTriggerStartRightInput,
                adaptiveTriggerSecondaryRightInput, adaptiveTriggerStrengthRightInput,
            };
            updatingProfileOptions = true;
            try {
                foreach (Control control in controls) {
                    if (control != null)
                        control.Enabled = hasProfile;
                }

                if (!GyroStickModeControlsSupported) {
                    gyroStickModeSelector.Enabled = false;
                    gyroStickModeRightSelector.Enabled = false;
                    gyroStickAxisXSelector.Enabled = false;
                    gyroStickAxisXRightSelector.Enabled = false;
                    invertStickXCheckBox.Enabled = false;
                    invertStickYCheckBox.Enabled = false;
                    invertStickXRightCheckBox.Enabled = false;
                    invertStickYRightCheckBox.Enabled = false;
                }

                if (!hasProfile)
                    return;

                string useAs = ControllerMappings.OptionValue(SelectedProfileId, "UseAs");
                if (useAs == ControllerMappings.UseAsXbox360)
                    useAsSelector.SelectedIndex = 0;
                else if (useAs == ControllerMappings.UseAsXbox360Viiper)
                    useAsSelector.SelectedIndex = 1;
                else if (useAs == ControllerMappings.UseAsDualShock4)
                    useAsSelector.SelectedIndex = 2;
                else if (useAs == ControllerMappings.UseAsDualSenseViiper)
                    useAsSelector.SelectedIndex = 3;
                else
                    useAsSelector.SelectedIndex = 4;
                autoPowerOffCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "AutoPowerOff");
                homeLongPowerOffCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "HomeLongPowerOff");
                homeLongPowerOffHoldInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "HomeLongPowerOffHoldSeconds", 2).ToString();
                dragToggleCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "DragToggle");
                swapAbCheckBox.Checked = ControllerMappings.BoolOption(SelectedProfileId, "SwapAB");
                swapXyCheckBox.Checked = ControllerMappings.BoolOption(SelectedProfileId, "SwapXY");
                string rumbleMode = ControllerMappings.RumbleMode(SelectedProfileId);
                int rumbleIndex = Array.FindIndex(ControllerMappings.RumbleModes,
                    m => String.Equals(m.Value, rumbleMode, StringComparison.OrdinalIgnoreCase));
                rumbleModeSelector.SelectedIndex = Math.Max(0, rumbleIndex);
                homeLedCheckBox.Checked = ControllerMappings.BoolOption(SelectedProfileId, "HomeLEDOn");
                UpdateLightColorButton(
                    ControllerMappings.OptionValue(SelectedProfileId, "LightColor"));
                string lightingMode = ControllerMappings.LightingMode(SelectedProfileId);
                int lightingModeIndex = Array.FindIndex(ControllerMappings.LightingModes,
                    m => String.Equals(m.Value, lightingMode, StringComparison.OrdinalIgnoreCase));
                lightingModeSelector.SelectedIndex = Math.Max(0, lightingModeIndex);
                string audioMode = ControllerMappings.ControllerAudioMode(SelectedProfileId);
                controllerAudioEnabledSelector.SelectedIndex =
                    audioMode == ControllerMappings.ModeEnable ? 0 :
                    (audioMode == ControllerMappings.AudioModeRequireHeadphones ? 2 : 1);
                int audioVolume = ControllerMappings.IntOption(
                    SelectedProfileId, "ControllerAudioVolume", 75);
                controllerAudioVolumeSelector.SelectedItem = ClosestAudioVolume(audioVolume) + "%";
                LoadControllerAudioEndpoints();
                controllerAudioUsbLoopbackSelector.SelectedIndex = ControllerMappings.BoolOption(
                    SelectedProfileId, "ControllerAudioUsbLoopback") ? 1 : 0;
                string microphoneMode = ControllerMappings.BluetoothMicrophoneMode(SelectedProfileId);
                controllerBluetoothMicrophoneSelector.SelectedIndex =
                    microphoneMode == ControllerMappings.ModeEnable ? 0 :
                    (microphoneMode == ControllerMappings.MicrophoneModeStartMuted ? 1 : 2);
                string micIndicatorMode = ControllerMappings.MicIndicatorMode(SelectedProfileId);
                micIndicatorSelector.SelectedIndex =
                    micIndicatorMode == ControllerMappings.MicIndicatorModeDisabled ? 0 :
                    (micIndicatorMode == ControllerMappings.MicIndicatorModeInverted ? 1 :
                    (micIndicatorMode == ControllerMappings.MicIndicatorModeEnabledWhileDisabled ? 3 : 2));
                gyroActivationModeSelector.SelectedIndex = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroHoldToggle") ? 0 : 1;
                btn_gyro_mouse_inhibit.Text = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroMouseInhibitButtons") ? "Enabled" : "Disabled";
                btn_touchpad_inhibit.Text = ControllerMappings.BoolOption(
                    SelectedProfileId, "TouchpadMouseInhibitButtons") ? "Enabled" : "Disabled";
                btn_touchpad_sensitivity.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "TouchpadSensitivity", 100) + "%";
                btn_touchpad_stick_sensitivity.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "TouchpadStickSensitivity", 100) + "%";
                btn_touchpad_horizontal_scale.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "TouchpadHorizontalScale", 100) + "%";
                btn_touchpad_vertical_scale.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "TouchpadVerticalScale", 100) + "%";
                btn_touchpad_tap_hold.Text = TouchpadTapHoldDisplayText(
                    ControllerMappings.OptionValue(SelectedProfileId, "TouchpadTapAndHold"));
                btn_touchpad_click_lockout.Text = ControllerMappings.BoolOption(
                    SelectedProfileId, "TouchpadClickMovementLockout") ? "Enabled" : "Disabled";
                btn_touchpad_two_finger_scroll.Text = ControllerMappings.BoolOption(
                    SelectedProfileId, "TouchpadTwoFingerScroll") ? "Enabled" : "Disabled";
                btn_gyro_analog_sliders.Text = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroAnalogSliders") ? "Enabled" : "Disabled";
                btn_default_orientation.Text = ControllerMappings.OptionValue(
                    SelectedProfileId, "DefaultOrientation") == ControllerMappings.OrientationVertical
                    ? "Vertical" : "Horizontal";

                LoadGyroStickModeButton(gyroStickModeSelector, "GyroStickModeLeft");
                LoadGyroStickModeButton(gyroStickModeRightSelector, "GyroStickModeRight");
                gyroStickAxisXSelector.Text =
                    ControllerMappings.OptionValue(SelectedProfileId, "GyroStickAxisXLeft") == "roll"
                        ? "Roll (bank)" : "Yaw (twist)";
                gyroStickAxisXRightSelector.Text =
                    ControllerMappings.OptionValue(SelectedProfileId, "GyroStickAxisXRight") == "roll"
                        ? "Roll (bank)" : "Yaw (twist)";
                invertStickXCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroStickInvertXLeft");
                invertStickYCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroStickInvertYLeft");
                invertStickXRightCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroStickInvertXRight");
                invertStickYRightCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroStickInvertYRight");

                maxDeflectionXLeftInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMaxDeflectionXLeft", 100).ToString();
                maxDeflectionYLeftInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMaxDeflectionYLeft", 100).ToString();
                maxDeflectionXRightInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMaxDeflectionXRight", 100).ToString();
                maxDeflectionYRightInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMaxDeflectionYRight", 100).ToString();
                minDeflectionXLeftInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMinDeflectionXLeft", 0).ToString();
                minDeflectionYLeftInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMinDeflectionYLeft", 0).ToString();
                minDeflectionXRightInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMinDeflectionXRight", 0).ToString();
                minDeflectionYRightInput.Text = ControllerMappings.IntOption(
                    SelectedProfileId, "GyroStickMinDeflectionYRight", 0).ToString();

                LoadAdaptiveTriggerMode(adaptiveTriggerModeLeftSelector,
                    ControllerMappings.OptionValue(SelectedProfileId,
                        "AdaptiveTriggerModeLeft"));
                LoadAdaptiveTriggerMode(adaptiveTriggerModeRightSelector,
                    ControllerMappings.OptionValue(SelectedProfileId,
                        "AdaptiveTriggerModeRight"));
                LoadAdaptiveTriggerFieldInputs();

                int inactivityMinutes = ControllerMappings.IntOption(
                    SelectedProfileId, "PowerOffInactivity", -1);
                string inactivityText = inactivityMinutes <= 0
                    ? "Never"
                    : inactivityMinutes + " minutes";
                int inactivityIndex = inactivitySelector.FindStringExact(inactivityText);
                if (inactivityIndex < 0) {
                    inactivitySelector.Items.Add(inactivityText);
                    inactivityIndex = inactivitySelector.Items.Count - 1;
                }
                inactivitySelector.SelectedIndex = inactivityIndex;
                UpdateAdaptiveTriggerControlState(true);
            } finally {
                updatingProfileOptions = false;
            }
        }

        private void LoadGyroStickModeButton(SplitButton selector, string optionKey) {
            string mode = ControllerMappings.OptionValue(SelectedProfileId, optionKey);
            selector.Text = mode == "hybrid"
                ? "Hybrid"
                : (mode == "absolute" ? "Absolute tilt" : "Rate (current)");
        }

        private static int InactivityMinutesFromText(string text) {
            if (String.IsNullOrEmpty(text) || text == "Never")
                return -1;
            string number = new string(text.TakeWhile(Char.IsDigit).ToArray());
            int minutes;
            return Int32.TryParse(number, out minutes) ? minutes : -1;
        }

        private void StyleMappingButton(SplitButton button) {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = ProfileBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = ProfileSurfaceHover;
            button.BackColor = ProfileSurface;
            button.ForeColor = ProfileText;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(8, 0, 0, 0);
            button.Font = new Font("Segoe UI", 9F);
            button.Cursor = Cursors.Hand;
        }

        private void StyleStandardButton(Button button, bool accent) {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = accent ? ProfileAccent : ProfileBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = accent
                ? Color.FromArgb(255, 201, 65)
                : ProfileSurfaceHover;
            button.BackColor = accent ? ProfileAccent : ProfileSurface;
            button.ForeColor = accent ? Color.FromArgb(28, 28, 28) : ProfileText;
            button.Font = new Font("Segoe UI", 9F, accent ? FontStyle.Bold : FontStyle.Regular);
            button.Cursor = Cursors.Hand;
        }

        private void StyleAssignmentMenus() {
            foreach (ContextMenuStrip menu in new[] {
                     menu_joy_buttons, menu_gyro_activation, menu_gyro_analog_sliders,
                     menu_gyro_mouse_inhibit, menu_touchpad_inhibit, menu_touchpad_sensitivity,
                     menu_touchpad_axis_scale,
                     menu_touchpad_tap_hold, menu_touchpad_click_lockout,
                     menu_touchpad_two_finger_scroll,
                     menu_gyro_stick_mode, menu_gyro_stick_axis,
                     menu_default_orientation }) {
                menu.BackColor = ProfileSurface;
                menu.ForeColor = ProfileText;
                menu.ShowImageMargin = false;
                menu.Font = new Font("Segoe UI", 9F);
                foreach (ToolStripItem item in menu.Items) {
                    item.BackColor = ProfileSurface;
                    item.ForeColor = ProfileText;
                }
            }
        }

        private void SetRemoteProfiles(IEnumerable<ControllerRecord> records) {
            var byProfile = new Dictionary<string, ControllerProfileInfo>(StringComparer.Ordinal);
            if (records != null) {
                foreach (ControllerRecord record in records) {
                    if (String.IsNullOrEmpty(record.ProfileId))
                        continue;

                    ControllerProfileInfo existing;
                    if (!byProfile.TryGetValue(record.ProfileId, out existing) ||
                        record.ConnectionSequence > existing.ConnectionSequence) {
                        byProfile[record.ProfileId] = new ControllerProfileInfo {
                            ProfileId = record.ProfileId,
                            DisplayName = String.IsNullOrEmpty(record.ProfileName)
                                ? "Controller " + (record.PadId + 1)
                                : record.ProfileName,
                            ConnectionSequence = record.ConnectionSequence,
                            IsConnected = true,
                            Kind = record.Kind,
                            PadId = record.PadId,
                            IsUsb = record.IsUsb,
                            AudioEndpointNameHint = record.AudioEndpointNameHint,
                        };
                    }
                }
            }

            remoteProfiles.Clear();
            remoteProfiles.AddRange(byProfile.Values.OrderByDescending(p => p.ConnectionSequence));
        }

        private void ServiceClient_SnapshotReceived(List<ControllerRecord> records) {
            if (InvokeRequired) {
                BeginInvoke(new Action<List<ControllerRecord>>(ServiceClient_SnapshotReceived), records);
                return;
            }

            SetRemoteProfiles(records);
            RefreshControllerChoices();
        }

        private void RefreshControllerChoices() {
            bool wasInitialSelection = initialControllerSelection;
            List<ControllerProfileInfo> connectedChoices =
                remoteProfiles.OrderByDescending(p => p.ConnectionSequence).ToList();
            List<ControllerProfileInfo> choices =
                ControllerMappings.IncludeDisconnectedProfiles(connectedChoices);

            string selectedId = SelectedProfileId;
            long newest = choices.Count == 0 ? -1 : choices.Max(p => p.ConnectionSequence);
            string targetId = selectedId;

            if (initialControllerSelection) {
                targetId = choices.Any(p => p.ProfileId == preferredProfileId)
                    ? preferredProfileId
                    : choices.FirstOrDefault()?.ProfileId;
            } else if (newest > newestControllerSequence) {
                // A genuinely new connection arrived while the dialog was open. Match the same
                // default as opening the dialog: the most recently connected logical controller.
                targetId = choices.FirstOrDefault()?.ProfileId;
            } else if (!choices.Any(p => p.ProfileId == selectedId)) {
                // Join/split changes the logical profile ID without creating a new Joycon.
                targetId = choices.FirstOrDefault()?.ProfileId;
            }

            bool changed = controllerSelector.Items.Count != choices.Count;
            if (!changed) {
                for (int i = 0; i < choices.Count; i++) {
                    ControllerProfileInfo current = controllerSelector.Items[i] as ControllerProfileInfo;
                    if (current == null || current.ProfileId != choices[i].ProfileId ||
                        current.DisplayName != choices[i].DisplayName ||
                        current.IsConnected != choices[i].IsConnected ||
                        current.ConnectionSequence != choices[i].ConnectionSequence ||
                        current.Kind != choices[i].Kind || current.PadId != choices[i].PadId ||
                        current.IsUsb != choices[i].IsUsb) {
                        changed = true;
                        break;
                    }
                }
            }

            updatingControllerSelector = true;
            if (changed) {
                controllerSelector.Items.Clear();
                controllerSelector.Items.AddRange(choices.Cast<object>().ToArray());
            }

            ControllerProfileInfo target = controllerSelector.Items.Cast<ControllerProfileInfo>()
                .FirstOrDefault(p => p.ProfileId == targetId);
            bool selectionChanged = selectedId != target?.ProfileId;
            controllerSelector.SelectedItem = target;
            if (target == null)
                controllerSelector.SelectedIndex = -1;
            updatingControllerSelector = false;

            newestControllerSequence = newest;
            initialControllerSelection = false;
            // The local hardware refresh timer runs twice a second. Reapplying the whole profile
            // on an unchanged snapshot resets open option ComboBoxes and steals their interaction
            // mid-selection. Only refresh controls when the available profiles or selection
            // genuinely changed; individual edits already update their own controls immediately.
            if (wasInitialSelection || changed || selectionChanged)
                ApplySelectedController();
        }

        private void ControllerSelector_SelectedIndexChanged(object sender, EventArgs e) {
            if (!updatingControllerSelector)
                ApplySelectedController();
        }

        private void ApplySelectedController() {
            CancelComboCapture();

            ControllerProfileInfo selected = SelectedProfile;
            bool hasController = selected != null && !String.IsNullOrEmpty(selected.ProfileId);
            bool hasTouchpad = selected != null &&
                (selected.Kind == ControllerKind.DualSense ||
                 selected.Kind == ControllerKind.DualShock4);
            bool hasAdaptiveTriggers = selected != null &&
                selected.Kind == ControllerKind.DualSense;
            bool hasConfigurableLight = selected != null &&
                (selected.Kind == ControllerKind.DualSense ||
                 selected.Kind == ControllerKind.DualShock4);
            // These controls intentionally share one occupied row. Unlike the capability
            // controls below, swapping them does not leave an unexplained layout gap.
            if (lightColorLabel != null)
                lightColorLabel.Visible = hasConfigurableLight;
            if (lightColorButton != null)
                lightColorButton.Visible = hasConfigurableLight;
            if (lightingModeLabel != null)
                lightingModeLabel.Visible = hasConfigurableLight;
            if (lightingModeSelector != null) {
                lightingModeSelector.Visible = hasConfigurableLight;
                lightingModeSelector.Enabled = hasConfigurableLight;
            }
            if (homeLedCheckBox != null)
                homeLedCheckBox.Visible = !hasConfigurableLight;
            foreach (Control control in new Control[] {
                controllerAudioEnabledLabel, controllerAudioVolumeLabel,
                controllerAudioEndpointLabel,
                controllerAudioEnabledSelector, controllerAudioVolumeSelector,
                controllerAudioEndpointSelector, controllerAudioTestButton,
                controllerAudioUsbLoopbackSelector, controllerAudioUsbLoopbackLabel,
            }) {
                if (control != null)
                    control.Visible = true;
            }
            // UpdateControllerAudioControlState (called below) is the source of truth for
            // .Enabled on these controls - Bluetooth vs. USB now behave differently there.
            if (profileNavigationButtons.TryGetValue("touchpad", out Button touchpadNavigation)) {
                touchpadNavigation.Visible = true;
                touchpadNavigation.Enabled = hasTouchpad;
            }
            if (!hasTouchpad && profilePages.TryGetValue("touchpad", out Panel touchpadPage) &&
                touchpadPage.Visible)
                ShowProfilePage("gyro");
            if (profileNavigationButtons.TryGetValue("adaptive_triggers",
                    out Button adaptiveTriggersNavigation)) {
                adaptiveTriggersNavigation.Visible = true;
                adaptiveTriggersNavigation.Enabled = hasAdaptiveTriggers;
            }
            if (!hasAdaptiveTriggers && profilePages.TryGetValue("adaptive_triggers",
                    out Panel adaptiveTriggersPage) && adaptiveTriggersPage.Visible)
                ShowProfilePage("gyro");
            foreach (SplitButton button in specialButtons) {
                button.Enabled = hasController;
                GetPrettyName(button);
            }
            // Overrides the blanket hasController pass just above - these specifically also need
            // a real capability, not just any connected controller.
            btn_lt_haptics.Enabled = hasController && hasAdaptiveTriggers;
            btn_rt_haptics.Enabled = hasController && hasAdaptiveTriggers;
            btn_toggle_lighting.Enabled = hasController && hasConfigurableLight;
            btn_apply.Enabled = hasController;
            gameControllersButton.Enabled = hasController;
            LoadProfileOptions(hasController);
            UpdateControllerAudioControlState();
            UpdateProfilePresentation(selected);
        }

        private void UpdateProfilePresentation(ControllerProfileInfo selected) {
            if (btn_delete_profile != null)
                btn_delete_profile.Enabled = selected != null && !selected.IsConnected;

            if (selected == null) {
                SetProfileStatus("No profile", ProfileMuted);
                if (virtualControllerNameLabel != null)
                    virtualControllerNameLabel.Text = "No controller profile selected";
                if (virtualControllerDetailLabel != null)
                    virtualControllerDetailLabel.Text =
                        "Connect a controller or select a saved profile to inspect its output.";
                if (gameControllersButton != null)
                    gameControllersButton.Text = "Open Game Controllers...";
                return;
            }

            if (selected.IsConnected) {
                SetProfileStatus("Connected", ProfileConnected);
                string useAs = ControllerMappings.OptionValue(selected.ProfileId, "UseAs");
                if (virtualControllerNameLabel != null)
                    virtualControllerNameLabel.Text = ConfiguredVirtualControllerName(selected.ProfileId);
                if (useAs == ControllerMappings.UseAsNone) {
                    if (virtualControllerDetailLabel != null)
                        virtualControllerDetailLabel.Text =
                            "This profile is connected without a virtual game controller output.";
                    if (gameControllersButton != null)
                        gameControllersButton.Text = "Open Game Controllers...";
                } else {
                    if (virtualControllerDetailLabel != null)
                        virtualControllerDetailLabel.Text =
                            "Connected to " + selected.DisplayName + ". Open Windows properties to test its inputs.";
                    if (gameControllersButton != null)
                        gameControllersButton.Text = "Open controller properties...";
                }
            } else {
                SetProfileStatus("Disconnected", ProfileMuted);
                if (virtualControllerNameLabel != null)
                    virtualControllerNameLabel.Text = "Virtual controller unavailable";
                if (virtualControllerDetailLabel != null)
                    virtualControllerDetailLabel.Text =
                        "This saved profile is offline. Its virtual controller will return when it reconnects.";
                if (gameControllersButton != null)
                    gameControllersButton.Text = "Open Game Controllers...";
            }
        }

        private void SetProfileStatus(string text, Color color) {
            if (profileStatusDot != null) {
                profileStatusDot.Text = "●";
                profileStatusDot.ForeColor = color;
            }
            if (profileStatusLabel != null) {
                profileStatusLabel.Text = text;
                profileStatusLabel.ForeColor = color;
            }
        }

        private static string ConfiguredVirtualControllerName(string profileId) {
            string useAs = ControllerMappings.OptionValue(profileId, "UseAs");
            if (useAs == ControllerMappings.UseAsXbox360)
                return "Xbox 360 virtual controller";
            if (useAs == ControllerMappings.UseAsXbox360Viiper)
                return "Xbox 360 virtual controller (VIIPER)";
            if (useAs == ControllerMappings.UseAsDualShock4)
                return "DualShock 4 virtual controller";
            if (useAs == ControllerMappings.UseAsDualSenseViiper)
                return "DualSense virtual controller (VIIPER)";
            return "Virtual controller output disabled";
        }

        // Permanently forgets a saved profile: its binds/options (ControllerMappings) and the
        // physical calibration data (CalibrationState) for every serial embedded in its profile
        // ID - deliberately both together, even though a sibling profile for the same physical
        // unit (e.g. a solo profile and a paired profile for the same Joy-Con) may still exist
        // and lose its calibration too. Only enabled for a disconnected profile (see
        // UpdateProfilePresentation) - deleting a live one makes no sense and this double-checks
        // rather than trusting the button's Enabled state alone.
        private void DeleteProfileButton_Click(object sender, EventArgs e) {
            ControllerProfileInfo selected = SelectedProfile;
            if (selected == null || selected.IsConnected)
                return;

            DialogResult confirm = MessageBox.Show(this,
                "Permanently delete \"" + selected.DisplayName + "\"?\r\n\r\n" +
                "This removes its saved button mappings and gyro/stick calibration. " +
                "This cannot be undone.",
                "Delete Controller Profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
                return;

            foreach (string serial in ControllerMappings.SerialsForProfileId(selected.ProfileId))
                CalibrationState.DeleteCalibrationData(serial);
            ControllerMappings.DeleteProfile(selected.ProfileId);

            RefreshControllerChoices();
        }

        private void GameControllersButton_Click(object sender, EventArgs e) {
            ControllerProfileInfo selected = SelectedProfile;
            if (selected == null)
                return;

            try {
                if (selected.IsConnected && OpenSelectedVirtualController(selected))
                    return;
                GameControllerControlPanel.OpenDefault();
            } catch (Exception ex) {
                MessageBox.Show(this,
                    "Windows could not open Game Controllers.\r\n\r\n" + ex.Message,
                    "Controller Profiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool OpenSelectedVirtualController(ControllerProfileInfo selected) {
            string useAs = ControllerMappings.OptionValue(selected.ProfileId, "UseAs");
            if (useAs == ControllerMappings.UseAsNone)
                return false;

            VirtualGameControllerType controllerType;
            if (useAs == ControllerMappings.UseAsXbox360 ||
                    useAs == ControllerMappings.UseAsXbox360Viiper)
                controllerType = VirtualGameControllerType.Xbox360;
            else if (useAs == ControllerMappings.UseAsDualSenseViiper)
                controllerType = VirtualGameControllerType.DualSense;
            else
                controllerType = VirtualGameControllerType.DualShock4;
            int ordinal = remoteProfiles
                .Where(profile => ControllerMappings.OptionValue(
                    profile.ProfileId, "UseAs") == useAs)
                .OrderBy(profile => profile.ConnectionSequence)
                .Select(profile => profile.ProfileId)
                .ToList()
                .FindIndex(profileId => profileId == selected.ProfileId);

            return GameControllerControlPanel.OpenForVirtualController(
                controllerType, ordinal);
        }

        private void Menu_joy_buttons_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            Control c = sender as Control;

            ToolStripItem clickedItem = e.ClickedItem;

            SplitButton caller = (SplitButton)c.Tag;
            string value = clickedItem.Tag is int
                ? "joy_" + clickedItem.Tag
                : clickedItem.Tag as string;
            if (value == null)
                return;

            SetBindValue((string)caller.Tag, value);
            GetPrettyName(caller);
        }

        private void GyroAnalogSlidersMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(SelectedProfileId, "GyroAnalogSliders", value);
            btn_gyro_analog_sliders.Text = value == "true" ? "Enabled" : "Disabled";
        }

        private void GyroMouseInhibitMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(
                SelectedProfileId, "GyroMouseInhibitButtons", value);
            btn_gyro_mouse_inhibit.Text = value == "true" ? "Enabled" : "Disabled";
        }

        private void TouchpadInhibitMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(
                SelectedProfileId, "TouchpadMouseInhibitButtons", value);
            btn_touchpad_inhibit.Text = value == "true" ? "Enabled" : "Disabled";
        }

        private void TouchpadSensitivityMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            SplitButton button = menu_touchpad_sensitivity.Tag as SplitButton;
            if (button == null || String.IsNullOrEmpty(SelectedProfileId))
                return;

            string key = button == btn_touchpad_stick_sensitivity
                ? "TouchpadStickSensitivity"
                : "TouchpadSensitivity";
            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(SelectedProfileId, key, value);
            button.Text = value + "%";
        }

        private void TouchpadAxisScaleMenu_ItemClicked(
            object sender, ToolStripItemClickedEventArgs e) {
            SplitButton button = menu_touchpad_axis_scale.Tag as SplitButton;
            if (button == null || String.IsNullOrEmpty(SelectedProfileId))
                return;

            string key = button == btn_touchpad_horizontal_scale
                ? "TouchpadHorizontalScale"
                : "TouchpadVerticalScale";
            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(SelectedProfileId, key, value);
            button.Text = value + "%";
        }

        private void PromptTouchpadSensitivity(SplitButton button, string key,
            string title, string description) {
            PromptTouchpadPercentage(button, key, title, description, 10, 400);
        }

        private void PromptTouchpadAxisScale(
            SplitButton button, string key, string description) {
            PromptTouchpadPercentage(button, key, description,
                description.ToLowerInvariant(), 0, 100);
        }

        private void PromptTouchpadPercentage(SplitButton button, string key,
            string title, string description, int minimum, int maximum) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            using (var prompt = new Form()) {
                prompt.Text = title;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.StartPosition = FormStartPosition.CenterParent;
                prompt.MinimizeBox = false;
                prompt.MaximizeBox = false;
                prompt.ShowInTaskbar = false;
                prompt.ClientSize = new Size(330, 142);
                prompt.BackColor = ProfileBackground;
                prompt.ForeColor = ProfileText;
                prompt.Font = new Font("Segoe UI", 9F);

                var label = new Label {
                    AutoSize = true,
                    Location = new Point(18, 18),
                    Text = String.Format("Enter {0} ({1}%–{2}%):",
                        description, minimum, maximum),
                    ForeColor = ProfileText,
                };
                var input = new TextBox {
                    Location = new Point(21, 48),
                    Size = new Size(288, 25),
                    BackColor = ProfileSurface,
                    ForeColor = ProfileText,
                    BorderStyle = BorderStyle.FixedSingle,
                    Text = ControllerMappings.IntOption(SelectedProfileId, key, 100) + "%",
                    TextAlign = HorizontalAlignment.Center,
                };
                var cancel = new Button {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(153, 94),
                    Size = new Size(75, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = ProfileSurface,
                    ForeColor = ProfileText,
                };
                var accept = new Button {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new Point(234, 94),
                    Size = new Size(75, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = ProfileAccent,
                    ForeColor = Color.Black,
                };
                cancel.FlatAppearance.BorderColor = ProfileBorder;
                accept.FlatAppearance.BorderColor = ProfileAccent;
                prompt.Controls.Add(label);
                prompt.Controls.Add(input);
                prompt.Controls.Add(cancel);
                prompt.Controls.Add(accept);
                prompt.CancelButton = cancel;
                prompt.AcceptButton = accept;
                prompt.Shown += (sender, e) => {
                    input.SelectAll();
                    input.Focus();
                };

                while (prompt.ShowDialog(this) == DialogResult.OK) {
                    string text = input.Text.Trim();
                    if (text.EndsWith("%", StringComparison.Ordinal))
                        text = text.Substring(0, text.Length - 1).Trim();

                    int value;
                    if (Int32.TryParse(text, out value) &&
                        value >= minimum && value <= maximum) {
                        ControllerMappings.SetOptionValue(SelectedProfileId, key, value.ToString());
                        button.Text = value + "%";
                        return;
                    }

                    MessageBox.Show(prompt,
                        String.Format("Enter a whole number from {0} to {1}.", minimum, maximum),
                        title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    input.SelectAll();
                    input.Focus();
                }
            }
        }

        private void TouchpadClickLockoutMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(
                SelectedProfileId, "TouchpadClickMovementLockout", value);
            btn_touchpad_click_lockout.Text = value == "true" ? "Enabled" : "Disabled";
        }

        private void TouchpadTwoFingerScrollMenu_ItemClicked(
            object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(
                SelectedProfileId, "TouchpadTwoFingerScroll", value);
            btn_touchpad_two_finger_scroll.Text = value == "true" ? "Enabled" : "Disabled";
        }

        private void TouchpadTapHoldMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(
                SelectedProfileId, "TouchpadTapAndHold", value);
            btn_touchpad_tap_hold.Text = TouchpadTapHoldDisplayText(value);
        }

        private static string TouchpadTapHoldDisplayText(string value) {
            if (value == "hold" || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                return "Hold to drag";
            if (value == "double_tap")
                return "Double-tap to drag";
            return "Disabled";
        }

        private void GyroStickModeMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            SplitButton caller = (SplitButton)((ContextMenuStrip)sender).Tag;
            string key = caller == gyroStickModeSelector
                ? "GyroStickModeLeft"
                : "GyroStickModeRight";
            ControllerMappings.SetOptionValue(
                SelectedProfileId, key, (string)e.ClickedItem.Tag);
            caller.Text = e.ClickedItem.Text;
        }

        private void GyroStickAxisMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            SplitButton caller = (SplitButton)((ContextMenuStrip)sender).Tag;
            string key = caller == gyroStickAxisXSelector
                ? "GyroStickAxisXLeft"
                : "GyroStickAxisXRight";
            ControllerMappings.SetOptionValue(
                SelectedProfileId, key, (string)e.ClickedItem.Tag);
            caller.Text = e.ClickedItem.Text;
        }

        private void DefaultOrientationMenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (String.IsNullOrEmpty(SelectedProfileId))
                return;

            string value = (string)e.ClickedItem.Tag;
            ControllerMappings.SetOptionValue(SelectedProfileId, "DefaultOrientation", value);
            btn_default_orientation.Text = value == ControllerMappings.OrientationVertical
                ? "Vertical" : "Horizontal";
        }

        private void Remap(object sender, MouseEventArgs e) {
            SplitButton c = sender as SplitButton;
            switch (e.Button) {
                case MouseButtons.Left:
                    curAssignment = c;
                    StartComboCapture(c);
                    break;
                case MouseButtons.Middle:
                    CancelComboCapture();
                    SetBindValue((string)c.Tag, ControllerMappings.DefaultValue((string)c.Tag));
                    GetPrettyName(c);
                    break;
                case MouseButtons.Right:
                    break;
            }
        }

        private void StartComboCapture(SplitButton c) {
            BeginBindingCaptureSuppression();
            comboMembers = new HashSet<string>();
            comboHeldNow = new HashSet<string>();
            c.Text = "Press combo...";

            // Button's own MouseDown handling grabs native Win32 mouse capture on press - normally
            // released on its matching MouseUp, but from here on WE'RE the ones deciding what
            // counts as input (via the global hook), not the button. Left stuck, that native
            // capture routes every subsequent left click system-wide back to this control (which
            // is what the focus rectangle sticking around was actually indicating - Windows only
            // force-releases stuck capture on a focus change, which is why Tab "fixed" it).
            // Releasing it explicitly, and taking focus off the button, avoids relying on that.
            c.Capture = false;
            ActiveControl = null;

            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            comboTimeout = new Timer { Interval = 15000 };
            comboTimeout.Tick += (s, e) => CancelComboCapture();
            comboTimeout.Start();
        }

        // Safety net (e.g. the user changes their mind and walks away without releasing
        // whatever they were holding) as well as the explicit middle-click-to-reset path -
        // leaves whatever bind already existed untouched, just stops listening.
        private void CancelComboCapture() {
            if (comboMembers == null) {
                EndBindingCaptureSuppression();
                return;
            }

            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            comboTimeout = null;

            Control target = curAssignment;
            curAssignment = null;
            comboMembers = null;
            comboHeldNow = null;
            EndBindingCaptureSuppression();

            if (target != null)
                GetPrettyName(target); // drop the "Press combo..." placeholder, restore the real value
        }

        // Called for every down/up transition seen while curAssignment is combo-capturing -
        // accumulates every input that gets pressed (comboMembers) and tracks which of those are
        // STILL held (comboHeldNow). Once everything that was pressed has been released again,
        // the accumulated set becomes the saved combo. downPart/upPart are mutually exclusive
        // per call, matching how the underlying key/mouse/controller sources report one
        // transition at a time. Always marshals onto the UI thread first - Keyboard_KeyEvent/
        // Mouse_MouseEvent fire from WindowsInput's background hook thread, and this both
        // touches Control.Text and mutates comboMembers/comboHeldNow/curAssignment, which
        // JoyPoll_Tick (a UI-thread Timer) also reads/writes.
        private void HandleComboInput(string downPart, string upPart) {
            if (InvokeRequired) {
                this.Invoke(new Action<string, string>(HandleComboInput), new object[] { downPart, upPart });
                return;
            }

            if (comboMembers == null)
                return; // capture was cancelled/finished between the event firing and this running

            if (downPart != null) {
                comboMembers.Add(downPart);
                comboHeldNow.Add(downPart);
                curAssignment.Text = "Press combo... (" + comboMembers.Count + ")";
            }

            if (upPart != null) {
                comboHeldNow.Remove(upPart);
                if (comboHeldNow.Count == 0 && comboMembers.Count > 0)
                    FinishComboCapture();
            }
        }

        private void FinishComboCapture() {
            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            comboTimeout = null;

            string combo = String.Join("+", comboMembers.OrderBy(s => s, StringComparer.Ordinal));
            SetBindValue((string)curAssignment.Tag, combo);
            GetPrettyName(curAssignment);

            curAssignment = null;
            comboMembers = null;
            comboHeldNow = null;
            EndBindingCaptureSuppression();
        }

        private void BeginBindingCaptureSuppression() {
            if (bindingCaptureSuppressionActive)
                return;

            bindingCaptureSuppressionActive = true;
            serviceClient.BeginBindingCapture();
        }

        private void EndBindingCaptureSuppression() {
            if (!bindingCaptureSuppressionActive)
                return;

            bindingCaptureSuppressionActive = false;
            serviceClient.EndBindingCapture();
        }

        private void Reassign_Load(object sender, EventArgs e) {
            keyboard = WindowsInput.Capture.Global.KeyboardAsync();
            keyboard.KeyEvent += Keyboard_KeyEvent;
            mouse = WindowsInput.Capture.Global.MouseAsync();
            mouse.MouseEvent += Mouse_MouseEvent;

            serviceClient.ButtonTransition += ServiceClient_ButtonTransition;
            serviceClient.SnapshotReceived += ServiceClient_SnapshotReceived;
            serviceClient.StartButtonCapture();
            serviceClient.RequestSnapshot();
        }

        // HeadlessJoyconHost polls its own Program.mgr.j and pushes each transition over the
        // control pipe. Fires on the pipe's background read thread - marshal here before reading
        // the selected ComboBox profile.
        private void ServiceClient_ButtonTransition(ButtonTransitionInfo info) {
            if (InvokeRequired) {
                BeginInvoke(new Action<ButtonTransitionInfo>(ServiceClient_ButtonTransition), info);
                return;
            }

            if (info.ProfileId != SelectedProfileId)
                return;
            HandleButtonTransition(info.ButtonIndex, info.IsDown);
        }

        private void HandleButtonTransition(int bi, bool now) {
            if (InvokeRequired) {
                this.Invoke(new Action<int, bool>(HandleButtonTransition), new object[] { bi, now });
                return;
            }

            if (curAssignment == null)
                return;

            if (comboMembers != null) {
                HandleComboInput(now ? "joy_" + bi : null, now ? null : "joy_" + bi);
            } else if (now) {
                SetBindValue((string)curAssignment.Tag, "joy_" + bi);
                GetPrettyName(curAssignment);
                curAssignment = null;
                EndBindingCaptureSuppression();
            }
        }

        private void Mouse_MouseEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.MouseEvent> e) {
            if (curAssignment == null)
                return;

            if (comboMembers != null) {
                // Left click is how this box gets opened for capture in the first place - the
                // global hook sees every left click system-wide and can't tell "the click that
                // opened the box" apart from "a left click somewhere else" by timing alone. It
                // CAN tell them apart by position: only a click actually over the box being
                // assigned is treated as real input, so a bare click-and-release on the box still
                // works as a one-shot "bind this to left click" (matching the old single-input
                // behavior) while a left click anywhere else is ignored instead of restarting or
                // corrupting the capture.
                bool leftOverBox = curAssignment.RectangleToScreen(curAssignment.ClientRectangle).Contains(Cursor.Position);

                if (e.Data.ButtonDown != null && (e.Data.ButtonDown.Button != WindowsInput.Events.ButtonCode.Left || leftOverBox))
                    HandleComboInput("mse_" + (int)e.Data.ButtonDown.Button, null);
                if (e.Data.ButtonUp != null && (e.Data.ButtonUp.Button != WindowsInput.Events.ButtonCode.Left || leftOverBox))
                    HandleComboInput(null, "mse_" + (int)e.Data.ButtonUp.Button);
                e.Next_Hook_Enabled = false;
                return;
            }

            // Gyro/touchpad mouse action buttons only check for a "joy_" binding at runtime (see
            // Controller.SimulateMouseActionButton/Scroll). A keyboard/mouse trigger would capture fine
            // here but then silently do nothing, so leave those actions uncaptured. Activation
            // mappings are not controller-only and may still use keyboard or mouse input.
            if (e.Data.ButtonDown != null && !ControllerOnlyKeys.Contains((string)curAssignment.Tag)) {
                SetBindValue((string)curAssignment.Tag, "mse_" + ((int)e.Data.ButtonDown.Button));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                EndBindingCaptureSuppression();
                e.Next_Hook_Enabled = false;
            }
        }

        private void Keyboard_KeyEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.KeyboardEvent> e) {
            if (curAssignment == null)
                return;

            if (comboMembers != null) {
                if (e.Data.KeyDown != null)
                    HandleComboInput("key_" + (int)e.Data.KeyDown.Key, null);
                if (e.Data.KeyUp != null)
                    HandleComboInput(null, "key_" + (int)e.Data.KeyUp.Key);
                e.Next_Hook_Enabled = false;
                return;
            }

            // See the same guard in Mouse_MouseEvent above.
            if (e.Data.KeyDown != null && !ControllerOnlyKeys.Contains((string)curAssignment.Tag)) {
                SetBindValue((string)curAssignment.Tag, "key_" + ((int)e.Data.KeyDown.Key));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                EndBindingCaptureSuppression();
                e.Next_Hook_Enabled = false;
            }
        }

        private void Reassign_FormClosing(object sender, FormClosingEventArgs e) {
            // Commit any uncommitted percentage-input edit (its Leave handler is the only thing
            // that saves it) rather than relying on implicit WinForms focus teardown order.
            if (IsDeferredCommitProfileInput(ActiveControl))
                this.ActiveControl = null;

            keyboard?.Dispose();
            mouse?.Dispose();
            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            EndBindingCaptureSuppression();

            serviceClient.ButtonTransition -= ServiceClient_ButtonTransition;
            serviceClient.SnapshotReceived -= ServiceClient_SnapshotReceived;
            serviceClient.StopButtonCapture();
        }

        private void AsyncPrettyName(Control c) {
            if (InvokeRequired) {
                this.Invoke(new Action<Control>(AsyncPrettyName), new object[] { c });
                return;
            }
            GetPrettyName(c);
        }

        private void GetPrettyName(Control c) {
            string val = GetBindValue((string)c.Tag);
            if ((string)c.Tag == "guide" && val == "default") {
                c.Text = "Default (Home / PS)";
                tip_reassign.SetToolTip(c,
                    "Uses this controller layout's original Guide / PS behavior.\r\n\r\n" +
                    "Left-click to detect a replacement button or combo.\r\n" +
                    "Middle-click to reset.\r\nRight-click for input options.");
                return;
            }
            if ((string)c.Tag == "touchpad_two_finger_tap" && val == "default") {
                c.Text = "Right click (default)";
                tip_reassign.SetToolTip(c,
                    "Right click while touchpad mouse is active.\r\n\r\n" +
                    "Left-click to detect a replacement input.\r\n" +
                    "Middle-click to reset.\r\nRight-click for input options.");
                return;
            }
            if ((string)c.Tag == "touchpad_two_finger_scroll_up" && val == "default") {
                c.Text = "Wheel up (default)";
                tip_reassign.SetToolTip(c,
                    "Wheel up while touchpad mouse is active.\r\n\r\n" +
                    "Left-click to detect a replacement input.\r\n" +
                    "Middle-click to reset.\r\nRight-click for input options.");
                return;
            }
            if ((string)c.Tag == "touchpad_two_finger_scroll_down" && val == "default") {
                c.Text = "Wheel down (default)";
                tip_reassign.SetToolTip(c,
                    "Wheel down while touchpad mouse is active.\r\n\r\n" +
                    "Left-click to detect a replacement input.\r\n" +
                    "Middle-click to reset.\r\nRight-click for input options.");
                return;
            }
            if (IsActivationKey((string)c.Tag) && val == "always") {
                c.Text = "Always On";
                tip_reassign.SetToolTip(c,
                    "Always on.\r\n\r\nLeft-click to detect input.\r\n" +
                    "Middle-click to reset.\r\nRight-click for activation options.");
                return;
            }
            bool unassigned = val == "0";

            // A combo is "+"-joined parts (see Joycon.IsComboHeld) - a single-input bind is just
            // a one-part combo, so this handles both uniformly.
            bool explicitlyDisabled = unassigned &&
                (IsActivationKey((string)c.Tag) || (string)c.Tag == "guide");
            string description = explicitlyDisabled
                ? "(disabled)"
                : (unassigned ? "(unassigned)" : String.Join("+", val.Split('+').Select(DescribeBindPart)));
            c.Text = explicitlyDisabled
                ? "Disabled"
                : (unassigned ? "" : description);

            // Long combos can still run out of room on the button itself (see Reassign.Designer.cs
            // for the width these buttons get) - the tooltip always shows the full, untruncated
            // bind so it's never actually ambiguous what's assigned, just hover to check.
            tip_reassign.SetToolTip(c, description + "\r\n\r\nLeft-click to detect input.\r\nMiddle-click to clear to default.\r\nRight-click to see more options.");
        }

        private static string DescribeBindPart(string part) {
            Type t = part.StartsWith("joy_") ? typeof(Controller.Button) : (part.StartsWith("key_") ? typeof(WindowsInput.Events.KeyCode) : typeof(WindowsInput.Events.ButtonCode));
            int value = Int32.Parse(part.Substring(4));
            return t == typeof(Controller.Button)
                ? ControllerButtonDisplayName(value)
                : Enum.GetName(t, value);
        }

        private static string ControllerButtonDisplayName(int value) {
            return value == (int)Controller.Button.TOUCHPAD_TAP
                ? "TOUCHPAD_TAP"
                : Enum.GetName(typeof(Controller.Button), value);
        }

        private void btn_apply_Click(object sender, EventArgs e) {
            ControllerMappings.Save();
        }

        private void btn_close_Click(object sender, EventArgs e) {
            btn_apply_Click(sender, e);
            Close();
        }
    }

    // Choice-only profile options use the same split-selector presentation as mappings while
    // retaining the SelectedIndex/SelectedItem behavior expected from the old ComboBoxes.
    public sealed class ProfileChoiceSelector : SplitButton {
        private int selectedIndex = -1;
        private Color menuBackColor;
        private Color menuForeColor;

        public List<object> Items { get; } = new List<object>();
        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex {
            get => selectedIndex;
            set {
                int normalized = value >= 0 && value < Items.Count ? value : -1;
                if (selectedIndex == normalized)
                    return;
                selectedIndex = normalized;
                Text = selectedIndex >= 0 ? Convert.ToString(Items[selectedIndex]) : String.Empty;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public object SelectedItem {
            get => selectedIndex >= 0 && selectedIndex < Items.Count
                ? Items[selectedIndex]
                : null;
            set => SelectedIndex = value == null ? -1 : Items.FindIndex(item => Equals(item, value));
        }

        public ProfileChoiceSelector() {
            Menu = new ContextMenuStrip { ShowImageMargin = false };
            Menu.Opening += (sender, e) => RebuildMenu();
            Click += (sender, e) => Menu.Show(this, 0, Height);
        }

        public int FindStringExact(string text) {
            return Items.FindIndex(item => String.Equals(
                Convert.ToString(item), text, StringComparison.CurrentCulture));
        }

        public void StyleMenu(Color backColor, Color foreColor, Font font) {
            menuBackColor = backColor;
            menuForeColor = foreColor;
            Menu.BackColor = backColor;
            Menu.ForeColor = foreColor;
            Menu.Font = font;
        }

        private void RebuildMenu() {
            Menu.Items.Clear();
            for (int index = 0; index < Items.Count; index++) {
                ToolStripMenuItem item = new ToolStripMenuItem(Convert.ToString(Items[index])) {
                    Tag = index,
                    BackColor = menuBackColor,
                    ForeColor = menuForeColor,
                };
                item.Click += (sender, e) => SelectedIndex = (int)((ToolStripItem)sender).Tag;
                Menu.Items.Add(item);
            }
        }
    }
}
