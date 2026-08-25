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
        ContextMenuStrip menu_gyro_stick_mode = new ContextMenuStrip();
        ContextMenuStrip menu_gyro_stick_axis = new ContextMenuStrip();
        ContextMenuStrip menu_default_orientation = new ContextMenuStrip();

        private Control curAssignment;

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
        private ComboBox useAsSelector;
        private ComboBox inactivitySelector;
        private ComboBox gyroActivationModeSelector;
        private SplitButton gyroStickModeSelector;
        private SplitButton gyroStickModeRightSelector;
        private SplitButton gyroStickAxisXSelector;
        private SplitButton gyroStickAxisXRightSelector;
        private SplitButton btn_gyro_analog_sliders;
        private SplitButton btn_gyro_mouse_inhibit;
        private SplitButton btn_default_orientation;
        private CheckBox autoPowerOffCheckBox;
        private CheckBox homeLongPowerOffCheckBox;
        private CheckBox dragToggleCheckBox;
        private CheckBox swapAbCheckBox;
        private CheckBox swapXyCheckBox;
        private CheckBox homeLedCheckBox;
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
            "clench_gyro", "ratchet_gyro"
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
                ToolStripMenuItem temp = new ToolStripMenuItem(Enum.GetName(typeof(Controller.Button), i));
                temp.Tag = i;
                menu_joy_buttons.Items.Add(temp);

                ToolStripMenuItem activationItem = new ToolStripMenuItem(Enum.GetName(typeof(Controller.Button), i));
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

            specialButtons = new List<SplitButton> { btn_capture, btn_home, btn_guide, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_shake, btn_reset_mouse, btn_active_gyro, btn_ratchet_gyro };
            specialButtons.AddRange(gyroMouseButtons);
            specialButtons.AddRange(gyroStickActivationButtons);

            foreach (SplitButton c in specialButtons) {
                c.Tag = c == btn_active_gyro
                    ? "active_gyro_mouse"
                    : c.Name.Substring(4);
                GetPrettyName(c);

                c.MouseDown += Remap;
                c.Menu = IsGyroActivationKey((string)c.Tag)
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
        private SplitButton btn_ratchet_gyro;
        private SplitButton btn_guide;
        private List<SplitButton> specialButtons;

        private static bool IsGyroActivationKey(string key) {
            return key == "active_gyro_mouse" ||
                   key == "active_gyro_left_stick" ||
                   key == "active_gyro_right_stick";
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

            btn_ratchet_gyro = new SplitButton { Name = "btn_ratchet_gyro" };
            btn_guide = new SplitButton { Name = "btn_guide" };

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
            Panel behaviorPage = BuildDeviceBehaviorPage();
            Panel virtualControllerPage = BuildVirtualControllerPage();
            Panel globalPage = BuildGlobalPage();
            profilePages.Add("bindings", bindingsPage);
            profilePages.Add("gyro", gyroPage);
            profilePages.Add("behavior", behaviorPage);
            profilePages.Add("virtual", virtualControllerPage);
            profilePages.Add("global", globalPage);
            profilePageHost.Controls.Add(bindingsPage);
            profilePageHost.Controls.Add(gyroPage);
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
            sidebar.Controls.Add(CreateNavigationButton("Device behavior", "behavior", 148));
            sidebar.Controls.Add(CreateNavigationButton("Virtual controller", "virtual", 192));
            sidebar.Controls.Add(CreateLabel("GLOBAL", 20, 262,
                Color.FromArgb(151, 174, 205), false, 8F));
            sidebar.Controls.Add(CreateNavigationButton("Global options", "global", 292));
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
            AddMappingRow(page, lbl_shake, btn_shake, "Shake input", 274, 24, 150, 430);

            page.Controls.Add(CreateDivider(24, 323));
            AddSectionHeading(page, "Joy-Con rail buttons", 340,
                "Independent mappings for the SL and SR buttons on each Joy-Con.");
            AddMappingRow(page, lbl_sl_l, btn_sl_l, "Left Joy-Con · SL", 401, 24, 145, 140);
            AddMappingRow(page, lbl_sl_r, btn_sl_r, "Right Joy-Con · SL", 401, 315, 440, 154);
            AddMappingRow(page, lbl_sr_l, btn_sr_l, "Left Joy-Con · SR", 442, 24, 145, 140);
            AddMappingRow(page, lbl_sr_r, btn_sr_r, "Right Joy-Con · SR", 442, 315, 440, 154);
            return page;
        }

        private Panel BuildGyroPage() {
            Panel page = CreateProfilePage("Gyro",
                "Choose where motion is sent and how each output activates.");
            AddSectionHeading(page, "Output activation", 86,
                "Set each output to always on, disabled, or activate it with a binding.");
            page.Controls.Add(CreateLabel("Output", 24, 137, ProfileMuted, false, 8.25F));
            page.Controls.Add(CreateLabel("Activation", 232, 137, ProfileMuted, false, 8.25F));
            AddMappingRow(page, lbl_activate_gyro, btn_active_gyro, "Mouse", 158, 24, 232, 362);
            AddMappingRow(page, null, gyroStickActivationButtons[0], "Left stick", 195, 24, 232, 362);
            AddMappingRow(page, null, gyroStickActivationButtons[1], "Right stick", 232, 24, 232, 362);

            // Gyro-driven L2/R2 analog output while a stick-gyro output button is held (see
            // Joycon.GyroAnalogSliders) - listed last since it modifies the stick outputs above
            // rather than being an output of its own. A plain on/off flag, not a bind, so its
            // SplitButton opens menu_gyro_analog_sliders on any click (wired in
            // CreateDynamicProfileControls) instead of the Remap combo-capture the other rows use.
            AddMappingRow(page, null, btn_gyro_analog_sliders, "Analog triggers", 269, 24, 232, 362);

            // While held, zeroes gyro-to-stick output instead of tracking live rotation - lets
            // the wrist reposition back to a comfortable angle without that motion registering as
            // a reverse turn, or a mid-turn hold continuing to turn on its own (see
            // Joycon.gyroStickRatcheted).
            AddMappingRow(page, null, btn_ratchet_gyro, "Ratchet gyro", 306, 24, 232, 362);

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
            maxDeflectionXLeftInput = CreateDeflectionInput(page, deflectionColumnX[0], 728, "GyroStickMaxDeflectionXLeft");
            maxDeflectionYLeftInput = CreateDeflectionInput(page, deflectionColumnX[1], 728, "GyroStickMaxDeflectionYLeft");
            minDeflectionXLeftInput = CreateDeflectionInput(page, deflectionColumnX[2], 728, "GyroStickMinDeflectionXLeft");
            minDeflectionYLeftInput = CreateDeflectionInput(page, deflectionColumnX[3], 728, "GyroStickMinDeflectionYLeft");

            page.Controls.Add(CreateLabel("Right stick", 24, 768, ProfileText, false));
            maxDeflectionXRightInput = CreateDeflectionInput(page, deflectionColumnX[0], 762, "GyroStickMaxDeflectionXRight");
            maxDeflectionYRightInput = CreateDeflectionInput(page, deflectionColumnX[1], 762, "GyroStickMaxDeflectionYRight");
            minDeflectionXRightInput = CreateDeflectionInput(page, deflectionColumnX[2], 762, "GyroStickMinDeflectionXRight");
            minDeflectionYRightInput = CreateDeflectionInput(page, deflectionColumnX[3], 762, "GyroStickMinDeflectionYRight");

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

        private Panel BuildDeviceBehaviorPage() {
            Panel page = CreateProfilePage("Device behavior",
                "Control power, button layout, gyro activation, and controller lighting.");

            AddSectionHeading(page, "Power", 96,
                "Choose when this controller powers itself off.");
            autoPowerOffCheckBox = CreateProfileCheckBox(
                "Power off when BetterJoy exits", 24, 153, "AutoPowerOff");
            homeLongPowerOffCheckBox = CreateProfileCheckBox(
                "Hold Home / Capture to power off", 315, 153, "HomeLongPowerOff");
            page.Controls.Add(autoPowerOffCheckBox);
            page.Controls.Add(homeLongPowerOffCheckBox);
            page.Controls.Add(CreateLabel("After inactivity", 24, 198, ProfileText, false));
            inactivitySelector = CreateProfileComboBox(145, 192, 180);
            inactivitySelector.DropDownStyle = ComboBoxStyle.DropDownList;
            inactivitySelector.Items.AddRange(new object[] {
                "Never", "1 minute", "3 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "60 minutes",
            });
            inactivitySelector.SelectedIndexChanged += ProfileOptionControlChanged;
            page.Controls.Add(inactivitySelector);

            page.Controls.Add(CreateDivider(24, 244));
            AddSectionHeading(page, "Input behavior", 261,
                "Customize face buttons, mouse dragging, and gyro activation for this profile.");
            swapAbCheckBox = CreateProfileCheckBox("Swap A / B", 24, 318, "SwapAB");
            swapXyCheckBox = CreateProfileCheckBox("Swap X / Y", 190, 318, "SwapXY");
            dragToggleCheckBox = CreateProfileCheckBox(
                "Toggle mouse drag", 356, 318, "DragToggle");
            page.Controls.Add(swapAbCheckBox);
            page.Controls.Add(swapXyCheckBox);
            page.Controls.Add(dragToggleCheckBox);
            page.Controls.Add(CreateLabel("Gyro binding behavior", 24, 366, ProfileText, false));
            gyroActivationModeSelector = CreateProfileComboBox(180, 360, 180);
            gyroActivationModeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            gyroActivationModeSelector.Items.AddRange(new object[] { "Hold", "Toggle" });
            gyroActivationModeSelector.SelectedIndexChanged += ProfileOptionControlChanged;
            page.Controls.Add(gyroActivationModeSelector);
            Label gyroHelp = CreateLabel(
                "Applies to mouse, left-stick, and right-stick gyro activation bindings.",
                24, 399, ProfileMuted, false, 8.5F);
            page.Controls.Add(gyroHelp);

            page.Controls.Add(CreateDivider(24, 440));
            AddSectionHeading(page, "Lighting", 457,
                "Control the Home-button ring light for this controller.");
            homeLedCheckBox = CreateProfileCheckBox(
                "Keep the Home LED on", 24, 514, "HomeLEDOn");
            page.Controls.Add(homeLedCheckBox);

            page.Controls.Add(CreateDivider(24, 560));
            AddSectionHeading(page, "Orientation", 577,
                "Only applies when this Joy-Con is used solo with no partner - whether it " +
                "defaults to horizontal (sideways) or vertical (self-paired) grip on connect.");
            AddMappingRow(page, null, btn_default_orientation, "Default orientation",
                652, 24, 232, 362);

            page.AutoScrollMinSize = new Size(0, 710);
            return page;
        }

        private Panel BuildVirtualControllerPage() {
            Panel page = CreateProfilePage("Virtual controller",
                "Inspect and test the virtual gamepad connected to this profile.");
            AddSectionHeading(page, "Output device", 96,
                "Windows exposes connected profiles as virtual game controllers.");

            page.Controls.Add(CreateLabel("Use as", 24, 163, ProfileText, false));
            useAsSelector = CreateProfileComboBox(115, 157, 300);
            useAsSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            useAsSelector.Items.AddRange(new object[] {
                "Xbox 360 controller", "DualShock 4 controller", "Disabled",
            });
            useAsSelector.SelectedIndexChanged += ProfileOptionControlChanged;
            page.Controls.Add(useAsSelector);

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
            AddSectionHeading(page, "Debug logging", 646,
                "Diagnostic file logs can grow quickly; enable them only while troubleshooting.");
            page.Controls.Add(CreateGlobalCheckBox(
                "Debug logging", 24, 703, "DebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "DualSense debug logging", 315, 703, "DualSenseDebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Gyro mouse debug logging", 24, 737, "GyroMouseDebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Gyro stick debug logging", 315, 737, "GyroStickDebugLogging"));
            page.Controls.Add(CreateGlobalCheckBox(
                "Auto-calibration debug logging", 24, 771, "AutoCalDebugLogging"));

            page.Controls.Add(CreateLabel(
                "Some changes, including HidHide and startup behavior, take effect after restart.",
                24, 816, ProfileMuted, false, 8.5F));
            page.AutoScrollMinSize = new Size(0, 858);
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

        private ComboBox CreateProfileComboBox(int left, int top, int width) {
            return new ComboBox {
                Location = new Point(left, top),
                Size = new Size(width, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = ProfileSurface,
                ForeColor = ProfileText,
                Font = new Font("Segoe UI", 9F),
            };
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

        // Not NumericUpDown - nothing else in this dialog uses one, and its spin-button portion
        // can't be recolored to match the dark flat theme every other control here already gets
        // via FlatStyle.Flat. A themed TextBox with digit-only input matches far better.
        private TextBox CreateDeflectionInput(Panel page, int left, int top, string optionKey) {
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
            input.Leave += DeflectionInput_Leave;
            page.Controls.Add(input);
            return input;
        }

        // Commits on focus-loss rather than through the immediate-commit
        // ProfileOptionControlChanged dispatcher every other control here uses (ComboBox.
        // SelectedIndexChanged/CheckBox.CheckedChanged) - a deferred-commit TextBox doesn't fit
        // that shape cleanly, so it gets its own handler instead.
        private void DeflectionInput_Leave(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            TextBox input = (TextBox)sender;
            int value;
            if (!Int32.TryParse(input.Text, out value))
                value = 0;
            value = Math.Max(0, Math.Min(100, value));
            input.Text = value.ToString();
            ControllerMappings.SetOptionValue(SelectedProfileId, (string)input.Tag, value.ToString());
        }

        private bool IsDeflectionInput(Control control) {
            return control != null && (
                control == maxDeflectionXLeftInput || control == maxDeflectionYLeftInput ||
                control == maxDeflectionXRightInput || control == maxDeflectionYRightInput ||
                control == minDeflectionXLeftInput || control == minDeflectionYLeftInput ||
                control == minDeflectionXRightInput || control == minDeflectionYRightInput);
        }

        private void ProfileOptionControlChanged(object sender, EventArgs e) {
            if (updatingProfileOptions || String.IsNullOrEmpty(SelectedProfileId))
                return;

            CheckBox checkBox = sender as CheckBox;
            if (checkBox != null) {
                ControllerMappings.SetOptionValue(
                    SelectedProfileId, (string)checkBox.Tag, checkBox.Checked.ToString().ToLowerInvariant());
            } else if (sender == useAsSelector) {
                string value = useAsSelector.SelectedIndex == 0
                    ? ControllerMappings.UseAsXbox360
                    : (useAsSelector.SelectedIndex == 1
                        ? ControllerMappings.UseAsDualShock4
                        : ControllerMappings.UseAsNone);
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

        private void LoadProfileOptions(bool hasProfile) {
            // LoadProfileOptions can be triggered by the hardware-refresh timer (twice a second)
            // whenever the controller list changes, not only by the user picking a different
            // profile - so this runs far more often than a plain profile switch. Unconditionally
            // nulling ActiveControl here (an earlier version of this fix) stole keyboard focus
            // from WHATEVER control had it on every single tick, not just these 8 boxes -
            // including mid-calibration or any other dialog/input in progress, which is exactly
            // the kind of thing that looks like the app "quit" partway through. Only force a
            // commit when one of the deflection TextBoxes specifically is the thing focused.
            if (IsDeflectionInput(ActiveControl))
                this.ActiveControl = null;

            Control[] controls = {
                useAsSelector, inactivitySelector, gyroActivationModeSelector,
                btn_gyro_analog_sliders, btn_gyro_mouse_inhibit, btn_default_orientation,
                autoPowerOffCheckBox, homeLongPowerOffCheckBox, dragToggleCheckBox,
                swapAbCheckBox, swapXyCheckBox, homeLedCheckBox,
                gyroStickModeSelector, gyroStickModeRightSelector,
                gyroStickAxisXSelector, gyroStickAxisXRightSelector,
                invertStickXCheckBox, invertStickYCheckBox,
                invertStickXRightCheckBox, invertStickYRightCheckBox,
                maxDeflectionXLeftInput, maxDeflectionYLeftInput,
                maxDeflectionXRightInput, maxDeflectionYRightInput,
                minDeflectionXLeftInput, minDeflectionYLeftInput,
                minDeflectionXRightInput, minDeflectionYRightInput,
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
                useAsSelector.SelectedIndex = useAs == ControllerMappings.UseAsXbox360
                    ? 0
                    : (useAs == ControllerMappings.UseAsDualShock4 ? 1 : 2);
                autoPowerOffCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "AutoPowerOff");
                homeLongPowerOffCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "HomeLongPowerOff");
                dragToggleCheckBox.Checked = ControllerMappings.BoolOption(
                    SelectedProfileId, "DragToggle");
                swapAbCheckBox.Checked = ControllerMappings.BoolOption(SelectedProfileId, "SwapAB");
                swapXyCheckBox.Checked = ControllerMappings.BoolOption(SelectedProfileId, "SwapXY");
                homeLedCheckBox.Checked = ControllerMappings.BoolOption(SelectedProfileId, "HomeLEDOn");
                gyroActivationModeSelector.SelectedIndex = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroHoldToggle") ? 0 : 1;
                btn_gyro_mouse_inhibit.Text = ControllerMappings.BoolOption(
                    SelectedProfileId, "GyroMouseInhibitButtons") ? "Enabled" : "Disabled";
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
                     menu_gyro_mouse_inhibit, menu_gyro_stick_mode, menu_gyro_stick_axis,
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
                        current.ConnectionSequence != choices[i].ConnectionSequence) {
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
            foreach (SplitButton button in specialButtons) {
                button.Enabled = hasController;
                GetPrettyName(button);
            }
            btn_apply.Enabled = hasController;
            gameControllersButton.Enabled = hasController;
            LoadProfileOptions(hasController);
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
            if (useAs == ControllerMappings.UseAsDualShock4)
                return "DualShock 4 virtual controller";
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

            VirtualGameControllerType controllerType = useAs == ControllerMappings.UseAsXbox360
                ? VirtualGameControllerType.Xbox360
                : VirtualGameControllerType.DualShock4;
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
            if (comboMembers == null)
                return;

            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            comboTimeout = null;

            Control target = curAssignment;
            curAssignment = null;
            comboMembers = null;
            comboHeldNow = null;

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

            // Gyro-mouse action buttons only ever check for a "joy_" binding at runtime (see
            // Joycon.SimulateGyroMouseButton/Scroll). A keyboard/mouse trigger would capture fine
            // here but then silently do nothing, so leave those actions uncaptured. Activation
            // mappings are not controller-only and may still use keyboard or mouse input.
            if (e.Data.ButtonDown != null && !ControllerOnlyKeys.Contains((string)curAssignment.Tag)) {
                SetBindValue((string)curAssignment.Tag, "mse_" + ((int)e.Data.ButtonDown.Button));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
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
                e.Next_Hook_Enabled = false;
            }
        }

        private void Reassign_FormClosing(object sender, FormClosingEventArgs e) {
            // Commit any uncommitted deflection-input edit (its Leave handler is the only thing
            // that saves it) rather than relying on implicit WinForms focus teardown order.
            if (IsDeflectionInput(ActiveControl))
                this.ActiveControl = null;

            keyboard?.Dispose();
            mouse?.Dispose();
            comboTimeout?.Stop();
            comboTimeout?.Dispose();

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
            if (IsGyroActivationKey((string)c.Tag) && val == "always") {
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
                (IsGyroActivationKey((string)c.Tag) || (string)c.Tag == "guide");
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
            return Enum.GetName(t, Int32.Parse(part.Substring(4)));
        }

        private void btn_apply_Click(object sender, EventArgs e) {
            ControllerMappings.Save();
        }

        private void btn_close_Click(object sender, EventArgs e) {
            btn_apply_Click(sender, e);
            Close();
        }
    }
}
