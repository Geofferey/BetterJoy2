using System;
using System.Collections.Generic;
using System.Configuration;

namespace BetterJoyForCemu {
    // One write path for application-wide settings. The legacy Settings table and the new
    // Global profile pane both edit the redirected AppData/ProgramData config established by
    // EntryPoint, so moving an option between those UIs never changes where it is stored.
    internal static class ApplicationSettings {
        private static readonly HashSet<string> GlobalOptionKeys =
            new HashSet<string>(StringComparer.Ordinal) {
                "StartInTray", "HideStatus", "MotionServer", "PassiveScan",
                "AutoAddControllers", "BlockAutoAddUSB", "AllowCalibration",
                "UseHidHide", "UnhideOnExit", "AutoCalDebugLogging",
                "DualSenseDebugLogging", "DualShock4DebugLogging", "DebugLogging", "GyroMouseDebugLogging",
                "GyroStickDebugLogging", "UseViiperForDualSenseMicrophone",
            };

        public static bool IsGlobalOption(string key) {
            return GlobalOptionKeys.Contains(key);
        }

        public static bool BoolValue(string key) {
            bool value;
            return Boolean.TryParse(ConfigurationManager.AppSettings[key], out value) && value;
        }

        public static void SetValue(string key, string value) {
            Configuration config = ConfigurationManager.OpenExeConfiguration(
                ConfigurationUserLevel.None);
            KeyValueConfigurationElement setting = config.AppSettings.Settings[key];
            if (setting == null)
                throw new ConfigurationErrorsException("Missing app setting: " + key);

            setting.Value = value;
            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection(config.AppSettings.SectionInformation.Name);
        }
    }
}
