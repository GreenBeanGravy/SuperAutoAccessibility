using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MelonLoader;
using UnityA11y.Unity;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SuperAutoAccessibility.Tree
{
    /// <summary>
    /// Publishes the menus to the screen reader as a real UI Automation tree (UnityA11y over
    /// AccessKit), so NVDA reads controls itself: name, role, value, state, "n of m", the
    /// screen as a dialog, tooltips as descriptions, in the user's own verbosity settings.
    ///
    /// The mod keeps every key it already handles. Its navigation moves the EventSystem
    /// selection exactly as before, and the tree reports wherever that selection is.
    ///
    /// Speech is handed over per control: the mod stops speaking a focused control only when
    /// the tree is attached, a screen reader is running, and the tree actually publishes that
    /// control. Everything else (gameplay, battle narration, toasts, controls the tree does not
    /// publish) is still spoken through Tolk. If the tree fails to attach, nothing changes.
    /// </summary>
    public static class SapA11y
    {
        private const float StartDelaySeconds = 2f;
        private const float AttachTimeoutSeconds = 30f;
        private const float ReaderCheckSeconds = 2f;

        private static MelonPreferences_Entry<bool> _useTree;
        private static A11yRuntime _runtime;
        private static SapUiSource _source;
        private static IntPtr _accessKit;
        private static bool _started;
        private static bool _attachLogged;
        private static float _attachDeadline;
        private static float _nextAttachAt;
        private static float _nextReaderCheck;
        private static bool _readerPresent;

        public static A11yRuntime Runtime => _runtime;

        /// <summary>
        /// Development only: behave as if a screen reader were running, so the hand-over of speech
        /// to the tree can be tested without starting one. Set from a debugger or eval.
        /// </summary>
        public static bool AssumeScreenReaderForTesting;
        internal static SapUiSource Source => _source;

        public static void Initialize()
        {
            var category = MelonPreferences.CreateCategory("SAPAccess", "SAPAccess");
            _useTree = category.CreateEntry("UseAccessibilityTree", true, "Use accessibility tree",
                "Publish the menus to your screen reader as real controls. Turn off to have the mod speak menus itself.");

            if (!_useTree.Value)
            {
                MelonLogger.Msg("[tree] Accessibility tree disabled in preferences; menus are spoken by the mod.");
                return;
            }

            _accessKit = LoadAccessKit();
        }

        /// <summary>Once per frame, from SAPMod.OnUpdate.</summary>
        public static void Tick()
        {
            if (_useTree == null || !_useTree.Value || _accessKit == IntPtr.Zero) return;

            try
            {
                if (!_started)
                {
                    // Not from OnInitializeMelon: the window and the EventSystem do not exist yet.
                    if (Time.unscaledTime < StartDelaySeconds || EventSystem.current == null) return;
                    Start();
                }

                if (!_runtime.BridgeRunning) RetryAttach();
                _runtime.Tick();
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[tree] Tick failed, accessibility tree stopped: " + ex);
                Shutdown();
            }
        }

        public static void Shutdown()
        {
            try { _runtime?.Dispose(); } catch { }
            _runtime = null;
            _source = null;
            _useTree = null;
        }

        /// <summary>
        /// True once the tree is attached and a screen reader is running to read it. Without a
        /// screen reader a tree is silent, so the mod keeps speaking (SAPI users, for example).
        /// </summary>
        public static bool OwnsSpeech
        {
            get
            {
                if (_runtime == null || !_runtime.BridgeRunning) return false;
                float now = Time.unscaledTime;
                if (now >= _nextReaderCheck)
                {
                    _nextReaderCheck = now + ReaderCheckSeconds;
                    string reader = TolkSpeech.DetectScreenReader();
                    _readerPresent = AssumeScreenReaderForTesting ||
                                     (!string.IsNullOrEmpty(reader) &&
                                      !string.Equals(reader, "SAPI", StringComparison.OrdinalIgnoreCase));
                }
                return _readerPresent;
            }
        }

        /// <summary>True only when the screen reader is going to speak this exact control.</summary>
        public static bool SpeaksFor(GameObject go)
        {
            return go != null && _source != null && OwnsSpeech && _source.Publishes(go);
        }

        private static ulong _lastFocusId;
        private static float _lastFocusAt = -100f;
        private const float RecentFocusSeconds = 0.5f;

        /// <summary>
        /// For a focus announcement: true when the screen reader will hear focus arrive on this
        /// control. A screen reader speaks on a focus CHANGE, so when the mod selects the control
        /// that already had focus (Tab into a section whose only entry is the button you were on),
        /// nothing reaches it and the mod has to speak. Focus that moved here a moment ago (the
        /// game selected it and the mod selected it again) is still being read, so stay quiet.
        /// </summary>
        public static bool SpeaksFocusChange(GameObject go)
        {
            if (!SpeaksFor(go)) return false;
            if (UGuiSource.StableId(go) != _lastFocusId) return true;
            return Time.realtimeSinceStartup - _lastFocusAt < RecentFocusSeconds;
        }

        /// <summary>
        /// True when the screen reader will introduce this screen itself: the tree publishes it
        /// as a dialog with its text as the description, and the mod is about to focus one of
        /// its controls. Pages with nothing focusable are left to the mod.
        /// </summary>
        public static bool SpeaksScreen(GameObject screenRoot)
        {
            if (screenRoot == null || !OwnsSpeech) return false;
            try
            {
                var controls = screenRoot.GetComponentsInChildren<Il2CppSpacewood.Unity.UI.SelectableBase>(false);
                for (int i = 0; i < controls.Length; i++)
                    if (controls[i] != null && controls[i].GetInteractable()) return true;
            }
            catch { }
            return false;
        }

        private static void Start()
        {
            _started = true;
            _source = new SapUiSource();
            var owner = new EventSystemFocusOwner(_source.ResolveGameObject);
            owner.FocusChanged += id =>
            {
                _lastFocusId = id;
                _lastFocusAt = Time.realtimeSinceStartup;
            };
            _runtime = A11yRuntime.Create(owner, null, registerUGuiSource: false);
            _runtime.Log = msg => MelonLogger.Msg("[tree] " + msg);
            _runtime.Register(_source);
            _attachDeadline = Time.unscaledTime + AttachTimeoutSeconds;
            if (_runtime.BridgeRunning) LogAttached();
        }

        private static void RetryAttach()
        {
            float now = Time.unscaledTime;
            if (now < _nextAttachAt) return;
            _nextAttachAt = now + 0.5f;

            if (_runtime.TryAttachBridge())
            {
                LogAttached();
            }
            else if (now > _attachDeadline && !_attachLogged)
            {
                _attachLogged = true;
                MelonLogger.Warning("[tree] Could not attach to the game window; menus stay on the mod's own speech.");
            }
        }

        private static void LogAttached()
        {
            if (_attachLogged) return;
            _attachLogged = true;
            MelonLogger.Msg("[tree] Accessibility tree attached; your screen reader reads the menus. " +
                            "Gameplay speech still comes from this mod.");
        }

        // ---- accesskit.dll ---------------------------------------------------------------

        /// <summary>
        /// accesskit.dll travels inside this DLL, so the installer and the updater, which only
        /// ever handle SuperAutoAccessibility.dll, need no changes. It is written next to the
        /// Tolk libraries and replaced whenever its hash differs, so an updated mod never keeps
        /// running an old copy.
        /// </summary>
        private static IntPtr LoadAccessKit()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                byte[] bytes;
                using (var res = asm.GetManifestResourceStream("SuperAutoAccessibility.accesskit.dll"))
                {
                    if (res == null)
                    {
                        MelonLogger.Warning("[tree] accesskit.dll is not embedded in this build; menus stay on the mod's own speech.");
                        return IntPtr.Zero;
                    }
                    bytes = new byte[res.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = res.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }

                string dir = Path.Combine(Path.GetDirectoryName(asm.Location) ?? ".", "AccessKitLibs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "accesskit.dll");

                if (!File.Exists(path) || !SameBytes(path, bytes))
                {
                    try
                    {
                        string temp = path + ".new";
                        File.WriteAllBytes(temp, bytes);
                        File.Copy(temp, path, overwrite: true);
                        File.Delete(temp);
                    }
                    catch (IOException ex)
                    {
                        // Another copy of the game holding the old file open. Use what is there.
                        MelonLogger.Warning("[tree] Could not replace accesskit.dll (" + ex.Message + "); using the existing copy.");
                    }
                }

                WriteNotices(asm, dir);

                IntPtr handle = System.Runtime.InteropServices.NativeLibrary.Load(path);

                // UnityA11y's P/Invokes name "accesskit". Point them at this exact copy, so a stray
                // accesskit.dll anywhere on the search path can never be picked up instead.
                _resolvedAccessKit = handle;
                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(asm, ResolveAccessKit);

                MelonLogger.Msg("[tree] Loaded " + path);
                return handle;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[tree] Could not load accesskit.dll; menus stay on the mod's own speech: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        private const string NoticePrefix = "SuperAutoAccessibility.notice.";

        /// <summary>
        /// AccessKit's license asks for its notice to travel with every copy, and the release is
        /// a single DLL, so the notices ride inside it and land next to accesskit.dll.
        /// </summary>
        private static void WriteNotices(Assembly asm, string dir)
        {
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(NoticePrefix, StringComparison.Ordinal)) continue;
                try
                {
                    string text;
                    using (var res = asm.GetManifestResourceStream(name))
                    using (var reader = new StreamReader(res))
                        text = reader.ReadToEnd();
                    string path = Path.Combine(dir, name.Substring(NoticePrefix.Length));
                    if (!File.Exists(path) || File.ReadAllText(path) != text)
                        File.WriteAllText(path, text);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[tree] Could not write " + name + ": " + ex.Message);
                }
            }
        }

        private static IntPtr _resolvedAccessKit;

        // A method rather than a lambda: the resolver delegate carries nullable annotations, and
        // a lambda for it needs NullableAttribute, which this build's references do not provide.
        private static IntPtr ResolveAccessKit(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            return string.Equals(name, "accesskit", StringComparison.OrdinalIgnoreCase) ? _resolvedAccessKit : IntPtr.Zero;
        }

        private static bool SameBytes(string path, byte[] bytes)
        {
            try
            {
                using var sha = SHA256.Create();
                byte[] a = sha.ComputeHash(bytes);
                byte[] b;
                using (var fs = File.OpenRead(path)) b = sha.ComputeHash(fs);
                return a.AsSpan().SequenceEqual(b);
            }
            catch
            {
                return false;
            }
        }
    }
}
