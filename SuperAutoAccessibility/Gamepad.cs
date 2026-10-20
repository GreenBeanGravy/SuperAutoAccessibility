using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;
using SuperAutoAccessibility.Gameplay;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Every key the mod reads goes through here instead of <see cref="Input"/>: the keyboard's
    /// answer, or a key the controller is standing in for. Every handler the keyboard reaches
    /// (shop zones, battle keys, menus, scoreboard, help, dictionary) therefore works with a
    /// controller too, and speaks the same way.
    /// </summary>
    public static class ModInput
    {
        public static bool GetKeyDown(KeyCode key) => Input.GetKeyDown(key) || Gamepad.KeyDown(key);
        public static bool GetKey(KeyCode key) => Input.GetKey(key) || Gamepad.KeyHeld(key);
        public static bool GetKeyUp(KeyCode key) => Input.GetKeyUp(key) || Gamepad.KeyUp(key);
        public static bool anyKeyDown => Input.anyKeyDown || Gamepad.AnyKeyDown();
    }

    /// <summary>Controller buttons after the sticks and triggers are made digital.</summary>
    [Flags]
    public enum PadButton
    {
        None = 0,
        A = 1 << 0,
        B = 1 << 1,
        X = 1 << 2,
        Y = 1 << 3,
        LB = 1 << 4,
        RB = 1 << 5,
        LT = 1 << 6,
        RT = 1 << 7,
        View = 1 << 8,
        Start = 1 << 9,
        L3 = 1 << 10,
        R3 = 1 << 11,
        DUp = 1 << 12,
        DDown = 1 << 13,
        DLeft = 1 << 14,
        DRight = 1 << 15,
        RUp = 1 << 16,
        RDown = 1 << 17,
    }

    /// <summary>
    /// XInput controller support. Reads the pad straight from XInput (so the D-pad, both sticks
    /// and the triggers all work; the game's legacy Input Manager only knows the left stick and
    /// two buttons) and turns each press into the keyboard key the mod already handles.
    ///
    /// The key a button stands for depends on where the player is (menu, shop, battle), and is
    /// fixed when the button goes down, so its release matches.
    /// </summary>
    public static class Gamepad
    {
        private enum Context { Menu, Shop, Battle }

        // ---- XInput ---------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Pad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState14(uint index, out XInputState state);

        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState13(uint index, out XInputState state);

        private const uint ErrorSuccess = 0;
        private const int MaxPads = 4;

        // ---- Raw state (what a test can inject) -------------------------------------------

        /// <summary>The pad's state in XInput's own terms.</summary>
        public struct RawState
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LX, LY, RX, RY;
        }

        public const ushort XDpadUp = 0x0001, XDpadDown = 0x0002, XDpadLeft = 0x0004, XDpadRight = 0x0008,
            XStart = 0x0010, XBack = 0x0020, XLeftThumb = 0x0040, XRightThumb = 0x0080,
            XLeftShoulder = 0x0100, XRightShoulder = 0x0200,
            XA = 0x1000, XB = 0x2000, XX = 0x4000, XY = 0x8000;

        private static bool _injected;
        private static RawState _injectedState;

        /// <summary>
        /// Testing without a pad: act as if this state were the controller's, until
        /// <see cref="ClearInjected"/>. Called from MelonMCP's eval.
        /// </summary>
        public static void Inject(ushort buttons, byte lt = 0, byte rt = 0, short lx = 0, short ly = 0,
            short rx = 0, short ry = 0)
        {
            _injected = true;
            _injectedState = new RawState
            {
                Buttons = buttons, LeftTrigger = lt, RightTrigger = rt, LX = lx, LY = ly, RX = rx, RY = ry
            };
        }

        public static void ClearInjected() => _injected = false;

        // ---- Settings --------------------------------------------------------------------

        private static MelonPreferences_Entry<bool> _enabled;

        public static void Initialize()
        {
            var category = MelonPreferences.CreateCategory("SAPAccess", "SAPAccess");
            _enabled = category.CreateEntry("ControllerSupport", true, "Controller support",
                "Play with a controller (Xbox style, or any pad Steam Input presents as one). " +
                "Turn off if the pad's buttons should do nothing in the mod.");
        }

        // ---- State -----------------------------------------------------------------------

        private const float StickPress = 0.6f;
        private const float StickRelease = 0.4f;
        private const float TriggerPress = 0.5f;
        private const float TriggerRelease = 0.3f;
        private const float RepeatDelay = 0.4f;
        private const float RepeatRate = 0.11f;
        private const float ScanInterval = 1.0f;

        private static readonly PadButton[] AllButtons =
        {
            PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LB, PadButton.RB, PadButton.LT,
            PadButton.RT, PadButton.View, PadButton.Start, PadButton.L3, PadButton.R3, PadButton.DUp,
            PadButton.DDown, PadButton.DLeft, PadButton.DRight, PadButton.RUp, PadButton.RDown,
        };

        private const PadButton Repeating = PadButton.DUp | PadButton.DDown | PadButton.DLeft | PadButton.DRight;

        private static int _frame = -1;
        private static int _slot = -1;
        private static float _nextScan;
        private static bool _connected;
        private static PadButton _held;
        private static readonly Dictionary<PadButton, KeyCode[]> _active = new Dictionary<PadButton, KeyCode[]>();
        private static readonly Dictionary<PadButton, float> _nextRepeat = new Dictionary<PadButton, float>();
        private static readonly Dictionary<KeyCode, int> _heldKeys = new Dictionary<KeyCode, int>();
        private static readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> _up = new HashSet<KeyCode>();
        private static bool _stickUp, _stickDown, _stickLeft, _stickRight, _rightUp, _rightDown, _ltDown, _rtDown;
        private static bool _gaveUpOnXInput;

        /// <summary>A controller is plugged in.</summary>
        public static bool Connected => _connected;

        /// <summary>The player's last input came from the controller: hints name its buttons.</summary>
        public static bool PadLastUsed { get; private set; }

        public static bool KeyDown(KeyCode key) { Poll(); return _down.Contains(key); }
        public static bool KeyUp(KeyCode key) { Poll(); return _up.Contains(key); }
        public static bool KeyHeld(KeyCode key) { Poll(); return _heldKeys.ContainsKey(key); }
        public static bool AnyKeyDown() { Poll(); return _down.Count > 0; }

        public enum Phase { Down, Held, Up }

        /// <summary>
        /// Whether the pad presses one of the game's input module buttons: Submit is what Enter
        /// and Space press, Cancel what Escape presses.
        /// </summary>
        public static bool ModuleButton(string name, Phase phase)
        {
            if (name == "Submit")
                return Test(KeyCode.Return, phase) || Test(KeyCode.KeypadEnter, phase) || Test(KeyCode.Space, phase);
            if (name == "Cancel")
                return Test(KeyCode.Escape, phase);
            return false;
        }

        private static bool Test(KeyCode key, Phase phase) =>
            phase == Phase.Down ? KeyDown(key) : phase == Phase.Held ? KeyHeld(key) : KeyUp(key);

        /// <summary>The buttons held this frame (for tests).</summary>
        public static PadButton Held { get { Poll(); return _held; } }

        // ---- Per-frame poll ----------------------------------------------------------------

        /// <summary>Reads the pad once a frame, the first time anything asks.</summary>
        private static void Poll()
        {
            int frame = Time.frameCount;
            if (frame == _frame) return;
            _frame = frame;
            _down.Clear();
            _up.Clear();
            SpeakPending();

            try
            {
                bool wanted = (_enabled == null || _enabled.Value) && (Application.isFocused || _injected);
                if (!wanted || !TryRead(out RawState raw))
                {
                    if (_connected) SetConnected(false);
                    ReleaseAll();
                    return;
                }

                if (!_connected) SetConnected(true);
                Apply(ToButtons(raw));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[gamepad] poll error: {ex.Message}");
                ReleaseAll();
            }
        }

        private static bool TryRead(out RawState raw)
        {
            raw = default;
            if (_injected)
            {
                raw = _injectedState;
                return true;
            }
            if (_gaveUpOnXInput) return false;

            float now = Time.realtimeSinceStartup;
            // Asking an empty slot is slow, so a missing pad is looked for once a second.
            if (_slot < 0)
            {
                if (now < _nextScan) return false;
                _nextScan = now + ScanInterval;
                for (int i = 0; i < MaxPads; i++)
                {
                    if (ReadSlot(i, out raw) == ErrorSuccess) { _slot = i; return true; }
                    if (_gaveUpOnXInput) return false;
                }
                return false;
            }

            if (ReadSlot(_slot, out raw) == ErrorSuccess) return true;
            _slot = -1;
            _nextScan = now + ScanInterval;
            return false;
        }

        private static uint ReadSlot(int index, out RawState raw)
        {
            raw = default;
            XInputState state;
            uint result;
            try { result = GetState14((uint)index, out state); }
            catch (DllNotFoundException)
            {
                try { result = GetState13((uint)index, out state); }
                catch (Exception)
                {
                    _gaveUpOnXInput = true;
                    MelonLogger.Warning("[gamepad] XInput is not available; controller support is off.");
                    return 1;
                }
            }
            catch (EntryPointNotFoundException)
            {
                _gaveUpOnXInput = true;
                return 1;
            }

            if (result == ErrorSuccess)
            {
                raw = new RawState
                {
                    Buttons = state.Pad.Buttons,
                    LeftTrigger = state.Pad.LeftTrigger,
                    RightTrigger = state.Pad.RightTrigger,
                    LX = state.Pad.ThumbLX, LY = state.Pad.ThumbLY,
                    RX = state.Pad.ThumbRX, RY = state.Pad.ThumbRY,
                };
            }
            return result;
        }

        private static void SetConnected(bool connected)
        {
            _connected = connected;
            if (!connected) PadLastUsed = false;
            MelonLogger.Msg($"[gamepad] {(connected ? "connected" : "disconnected")}");
            _announce = connected ? "Controller connected. Press View for controller help." : "Controller disconnected.";
        }

        private static string _announce;

        /// <summary>
        /// Says a connection change once the game has been up a few seconds, so the first words
        /// are not lost while the screen reader is still being found.
        /// </summary>
        private static void SpeakPending()
        {
            if (_announce == null || Time.realtimeSinceStartup < 6f) return;
            AccessibilityManager.Announce(_announce, interrupt: false);
            _announce = null;
        }

        private static float Norm(short v) => Mathf.Clamp(v / 32767f, -1f, 1f);

        /// <summary>Raw XInput state to the digital buttons the mapping works with.</summary>
        private static PadButton ToButtons(RawState s)
        {
            PadButton b = PadButton.None;
            ushort x = s.Buttons;
            if ((x & XA) != 0) b |= PadButton.A;
            if ((x & XB) != 0) b |= PadButton.B;
            if ((x & XX) != 0) b |= PadButton.X;
            if ((x & XY) != 0) b |= PadButton.Y;
            if ((x & XLeftShoulder) != 0) b |= PadButton.LB;
            if ((x & XRightShoulder) != 0) b |= PadButton.RB;
            if ((x & XBack) != 0) b |= PadButton.View;
            if ((x & XStart) != 0) b |= PadButton.Start;
            if ((x & XLeftThumb) != 0) b |= PadButton.L3;
            if ((x & XRightThumb) != 0) b |= PadButton.R3;
            if ((x & XDpadUp) != 0) b |= PadButton.DUp;
            if ((x & XDpadDown) != 0) b |= PadButton.DDown;
            if ((x & XDpadLeft) != 0) b |= PadButton.DLeft;
            if ((x & XDpadRight) != 0) b |= PadButton.DRight;

            _ltDown = Hysteresis(s.LeftTrigger / 255f, _ltDown, TriggerPress, TriggerRelease);
            _rtDown = Hysteresis(s.RightTrigger / 255f, _rtDown, TriggerPress, TriggerRelease);
            if (_ltDown) b |= PadButton.LT;
            if (_rtDown) b |= PadButton.RT;

            // The left stick is a second D-pad, one direction at a time (the larger axis wins), so
            // a diagonal does not move two ways at once.
            float lx = Norm(s.LX), ly = Norm(s.LY);
            bool horizontal = Mathf.Abs(lx) >= Mathf.Abs(ly);
            _stickRight = Hysteresis(horizontal ? lx : 0f, _stickRight, StickPress, StickRelease);
            _stickLeft = Hysteresis(horizontal ? -lx : 0f, _stickLeft, StickPress, StickRelease);
            _stickUp = Hysteresis(horizontal ? 0f : ly, _stickUp, StickPress, StickRelease);
            _stickDown = Hysteresis(horizontal ? 0f : -ly, _stickDown, StickPress, StickRelease);
            if (_stickUp) b |= PadButton.DUp;
            if (_stickDown) b |= PadButton.DDown;
            if (_stickLeft) b |= PadButton.DLeft;
            if (_stickRight) b |= PadButton.DRight;

            float ry = Norm(s.RY);
            _rightUp = Hysteresis(ry, _rightUp, StickPress, StickRelease);
            _rightDown = Hysteresis(-ry, _rightDown, StickPress, StickRelease);
            if (_rightUp) b |= PadButton.RUp;
            if (_rightDown) b |= PadButton.RDown;
            return b;
        }

        private static bool Hysteresis(float value, bool was, float press, float release) =>
            was ? value > release : value >= press;

        // ---- Buttons to keys ---------------------------------------------------------------

        private static void Apply(PadButton now)
        {
            PadButton before = _held;
            _held = now;
            float time = Time.unscaledTime;

            // Releases first, so a key one button lets go of and another takes in the same frame
            // still reads as pressed.
            foreach (var button in AllButtons)
            {
                if ((before & button) != 0 && (now & button) == 0) ReleaseButton(button);
            }

            foreach (var button in AllButtons)
            {
                bool isDown = (now & button) != 0;
                bool wasDown = (before & button) != 0;
                if (isDown && !wasDown)
                {
                    PadLastUsed = true;
                    var keys = Resolve(button, now);
                    _active[button] = keys;
                    foreach (var key in keys) PressKey(key);
                    if ((button & Repeating) != 0) _nextRepeat[button] = time + RepeatDelay;
                }
                else if (isDown && (button & Repeating) != 0 && _nextRepeat.TryGetValue(button, out float at)
                         && time >= at && _active.TryGetValue(button, out var held))
                {
                    // Held direction repeats like a held arrow key.
                    foreach (var key in held) _down.Add(key);
                    _nextRepeat[button] = time + RepeatRate;
                }
            }
        }

        private static void PressKey(KeyCode key)
        {
            _down.Add(key);
            _heldKeys[key] = _heldKeys.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        private static void ReleaseButton(PadButton button)
        {
            if (_active.TryGetValue(button, out var keys))
            {
                foreach (var key in keys)
                {
                    if (!_heldKeys.TryGetValue(key, out int n)) continue;
                    if (n <= 1) { _heldKeys.Remove(key); _up.Add(key); }
                    else _heldKeys[key] = n - 1;
                }
                _active.Remove(button);
            }
            _nextRepeat.Remove(button);
        }

        private static void ReleaseAll()
        {
            if (_held == PadButton.None && _active.Count == 0 && _heldKeys.Count == 0) return;
            foreach (var button in new List<PadButton>(_active.Keys)) ReleaseButton(button);
            _heldKeys.Clear();
            _held = PadButton.None;
        }

        private static readonly KeyCode[] None = Array.Empty<KeyCode>();

        private static KeyCode[] Keys(params KeyCode[] keys) => keys;

        private static Context CurrentContext()
        {
            try
            {
                // Anything that takes the keys over the run (pause menu, settings, help, the
                // dictionary, a popup) is a menu: the shop's and the battle's own keys sit still.
                if (SAPMod.HelpOverlayActive || SAPMod.SidebarActive || DictionaryMenu.IsActive ||
                    ShopNavigationManager.IsInputSuppressed || SectionManager.MenuOverlayOpen())
                    return Context.Menu;
                if (GameplayPhaseDetector.IsShopPhase()) return Context.Shop;
                if (GameplayPhaseDetector.IsBattlePhase()) return Context.Battle;
            }
            catch { }
            return Context.Menu;
        }

        /// <summary>The keys a button press stands for here.</summary>
        private static KeyCode[] Resolve(PadButton button, PadButton heldNow)
        {
            // Directions and the right stick mean the same everywhere.
            switch (button)
            {
                case PadButton.DUp: return Keys(KeyCode.UpArrow);
                case PadButton.DDown: return Keys(KeyCode.DownArrow);
                case PadButton.DLeft: return Keys(KeyCode.LeftArrow);
                case PadButton.DRight: return Keys(KeyCode.RightArrow);
                case PadButton.RUp: return Keys(KeyCode.Home);
                case PadButton.RDown: return Keys(KeyCode.End);
                case PadButton.View: return Keys(KeyCode.H);
                case PadButton.Start: return Keys(KeyCode.Escape);
                case PadButton.B: return Keys(KeyCode.Escape);
                case PadButton.L3: return Keys(KeyCode.J);
            }

            var context = CurrentContext();
            switch (context)
            {
                case Context.Battle: return ResolveBattle(button);
                case Context.Shop: return ResolveShop(button, heldNow);
                default: return ResolveMenu(button);
            }
        }

        private static KeyCode[] ResolveMenu(PadButton button)
        {
            switch (button)
            {
                case PadButton.A: return Keys(KeyCode.Return);
                case PadButton.X: return Keys(KeyCode.Space);
                case PadButton.Y: return Keys(KeyCode.R);
                case PadButton.LB: return Keys(KeyCode.PageUp);
                case PadButton.RB: return Keys(KeyCode.PageDown);
                case PadButton.R3: return Keys(KeyCode.Space);
                default: return None;
            }
        }

        private static KeyCode[] ResolveBattle(PadButton button)
        {
            switch (button)
            {
                // Return as well as P: the round's result screens are in the battle scene.
                case PadButton.A: return Keys(KeyCode.Return, KeyCode.P);
                case PadButton.X: return Keys(KeyCode.G);
                case PadButton.Y: return Keys(KeyCode.U);
                case PadButton.RT: return Keys(KeyCode.K);
                case PadButton.LB: return Keys(KeyCode.Space);
                case PadButton.RB: return Keys(KeyCode.R);
                case PadButton.R3: return Keys(KeyCode.Space);
                default: return None;
            }
        }

        private static KeyCode[] ResolveShop(PadButton button, PadButton heldNow)
        {
            // Left trigger: status questions. Right trigger: the shop's quick actions.
            if ((heldNow & PadButton.LT) != 0 && button != PadButton.LT)
            {
                switch (button)
                {
                    case PadButton.A: return Keys(KeyCode.A);   // gold
                    case PadButton.B: return Keys(KeyCode.L);   // lives
                    case PadButton.X: return Keys(KeyCode.N);   // turn
                    case PadButton.Y: return Keys(KeyCode.W);   // wins
                    case PadButton.RB: return Keys(KeyCode.I);  // detailed info
                    case PadButton.LB: return Keys(KeyCode.O);  // scoreboard
                    case PadButton.R3: return Keys(KeyCode.V);  // bullies (Daily)
                    default: return None;
                }
            }
            if ((heldNow & PadButton.RT) != 0 && button != PadButton.RT)
            {
                switch (button)
                {
                    case PadButton.A: return Keys(KeyCode.Q);   // roll
                    case PadButton.X: return Keys(KeyCode.Z);   // freeze
                    case PadButton.Y: return Keys(KeyCode.E);   // end turn
                    default: return None;
                }
            }

            switch (button)
            {
                case PadButton.A: return Keys(KeyCode.Return);
                case PadButton.X: return Keys(KeyCode.X);       // sell
                case PadButton.Y: return Keys(KeyCode.R);
                case PadButton.LB: return Keys(NextZoneKey(-1));
                case PadButton.RB: return Keys(NextZoneKey(+1));
                case PadButton.R3: return Keys(KeyCode.Space);
                default: return None;
            }
        }

        private static readonly ShopZone[] ZoneCycle =
            { ShopZone.PetShop, ShopZone.FoodShop, ShopZone.Team, ShopZone.Actions };

        private static KeyCode KeyOfZone(ShopZone zone)
        {
            switch (zone)
            {
                case ShopZone.PetShop: return KeyCode.S;
                case ShopZone.FoodShop: return KeyCode.F;
                case ShopZone.Team: return KeyCode.T;
                default: return KeyCode.D;
            }
        }

        /// <summary>The key for the zone after (or before) the one the player is in.</summary>
        private static KeyCode NextZoneKey(int direction)
        {
            int at = Array.IndexOf(ZoneCycle, ShopNavigationManager.CurrentZone);
            if (at < 0) at = direction > 0 ? -1 : 0;
            int next = ((at + direction) % ZoneCycle.Length + ZoneCycle.Length) % ZoneCycle.Length;
            return KeyOfZone(ZoneCycle[next]);
        }

        /// <summary>
        /// Key names in a spoken hint, as the controller's buttons when the player is using one:
        /// "Press Enter to continue" is no use to a player holding a pad.
        /// </summary>
        public static string Hint(string text)
        {
            if (!PadLastUsed || string.IsNullOrEmpty(text)) return text;
            foreach (var (key, pad) in HintSwaps)
                text = text.Replace(key, pad);
            return text;
        }

        /// <summary>The controller page of the help overlay for where the player is.</summary>
        public static (string category, List<string> items) HelpCategory()
        {
            var items = new List<string>();
            if (GameplayPhaseDetector.IsShopPhase())
            {
                items.Add("Left bumper and right bumper: Previous or next zone: pet shop, food shop, team, actions");
                items.Add("D-pad or left stick, left and right: Move between slots");
                items.Add("D-pad or left stick, up and down: Browse detail lines");
                items.Add("Right stick up or down: First or last slot");
                items.Add("A: Buy or select");
                items.Add("X: Sell");
                items.Add("B: Cancel, leave the zone, then the pause menu");
                items.Add("Y: Read the whole board");
                items.Add("Right stick click: Re-read the current detail line");
                items.Add("Hold left trigger, then A: Gold. B: Lives. X: Turn. Y: Wins. Right bumper: Details. Left bumper: Scoreboard");
                items.Add("Hold right trigger, then A: Roll. X: Freeze. Y: End turn");
            }
            else if (GameplayPhaseDetector.IsBattlePhase())
            {
                items.Add("A: Pause or play");
                items.Add("X: Fast forward");
                items.Add("Y: Autoplay on or off");
                items.Add("Right trigger: Skip the battle");
                items.Add("Left bumper or right stick click: Repeat the last line");
                items.Add("Right bumper: Read the screen");
                items.Add("B or Start: Pause menu");
            }
            else
            {
                items.Add("D-pad or left stick: Move between items");
                items.Add("Left bumper and right bumper: Previous or next section");
                items.Add("Right stick up or down: First or last item");
                items.Add("A: Activate");
                items.Add("B: Back");
                items.Add("X: Space, read again");
                items.Add("Y: Read the screen");
            }
            items.Add("Start: Pause menu or back");
            items.Add("View: Open or close this help");
            items.Add("Left stick click: Dictionary");
            return ("Controller", items);
        }

        // Longest phrases first.
        private static readonly (string key, string pad)[] HintSwaps =
        {
            ("Tab or Page Down to switch categories, Shift Tab or Page Up for previous, Up and Down to browse, Escape to close.",
                "Bumpers to switch categories, D-pad up and down to browse, B or View to close."),
            ("Press T for team, arrow keys to navigate, Enter to confirm, Backspace or Escape to cancel.",
                "Press the right bumper for the team, D-pad to move, A to confirm, B to cancel."),
            ("Use left and right arrows", "Use the D-pad left and right"),
            ("Type a name and press Enter to save, or Escape to cancel", "Type a name on the keyboard and press Enter to save, or press B to cancel"),
            ("Backspace or Escape to cancel", "B to cancel"),
            ("Backspace cancels", "B cancels"),
            ("Press Enter or Space", "Press A"),
            ("press Enter", "press A"),
            ("Press Enter", "Press A"),
            ("or Escape to cancel", "or B to cancel"),
            ("press P for each step, or U to turn autoplay on", "press A for each step, or Y to turn autoplay on"),
            ("Press P to play", "Press A to play"),
            ("Use Up and Down arrows", "Use the D-pad up and down"),
            ("Use Tab to navigate", "Use the bumpers to navigate"),
            ("arrow keys to navigate", "the D-pad to navigate"),
        };
    }
}
