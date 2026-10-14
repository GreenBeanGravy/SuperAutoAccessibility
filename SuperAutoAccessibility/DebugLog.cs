using MelonLoader;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Diagnostic logging that is off by default: tooltip, modal and page tracing and the UI dump
    /// after every page change used to fill the log (hundreds of lines per session) and bury the
    /// lines a bug report needs. Turn on with SAPAccess/DebugLogging in MelonPreferences.cfg.
    /// </summary>
    public static class DebugLog
    {
        private static MelonPreferences_Entry<bool> _enabled;

        public static bool Enabled => _enabled != null && _enabled.Value;

        public static void Initialize()
        {
            var category = MelonPreferences.CreateCategory("SAPAccess", "SAPAccess");
            _enabled = category.CreateEntry("DebugLogging", false, "Debug logging",
                "Log tooltip, modal and page tracing and dump the UI after every page change.");
        }

        public static void Msg(string message)
        {
            if (Enabled) MelonLogger.Msg(message);
        }
    }
}
