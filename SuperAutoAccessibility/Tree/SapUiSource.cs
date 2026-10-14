using System;
using System.Collections.Generic;
using System.Text;
using Il2CppSpacewood.Unity;
using Il2CppSpacewood.Unity.UI;
using MelonLoader;
using UnityA11y.Core;
using UnityA11y.Unity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SuperAutoAccessibility.Tree
{
    /// <summary>
    /// The tree the screen reader reads. Built from the mod's own sections (SectionManager), so
    /// reading order, grouping, labels and "n of m" are exactly what the keyboard navigation
    /// already uses: one group per section, inside a dialog for the page, modal or menu, whose
    /// description is the screen's text that is not part of any control.
    ///
    /// Screens the mod has no sections for fall back to a generic scan of the visible controls,
    /// and the in-run shop and battle (sprites, read by the mod's own narration) publish a single
    /// node naming the phase, so the screen reader's focus is never left on a menu that is gone.
    /// </summary>
    internal sealed class SapUiSource : IUiSource
    {
        private const ulong RootId = 1;
        private const ulong ScreenId = 16;
        private const ulong PhaseNodeId = 17;
        private const ulong FirstSectionId = 32;
        private const int MaxDescription = 600;

        private readonly List<ulong> _ring = new List<ulong>();
        private readonly Dictionary<ulong, GameObject> _goById = new Dictionary<ulong, GameObject>();
        private readonly Dictionary<ulong, int> _indexById = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _sizeById = new Dictionary<ulong, int>();
        /// <summary>Tab list each tab belongs to, so the focused-node refresh keeps it a tab.</summary>
        private readonly Dictionary<ulong, NavigationSection> _tabListById = new Dictionary<ulong, NavigationSection>();
        private readonly ScanFallback _scan = new ScanFallback();

        private bool _usingScan;
        private bool _dirty = true;
        private int _lastSignature;
        private float _nextSignatureAt;
        private int _refreshFrame = -1;
        private bool _refreshAllowed;
        private float _nextRefreshAt;

        public string Name => "Super Auto Pets";
        public int Priority => 100;
        public bool IsActive => EventSystem.current != null;
        public IReadOnlyList<ulong> FocusRing => _usingScan ? _scan.FocusRing : _ring;

        // ---- structure ---------------------------------------------------------------------

        public bool IsDirty
        {
            get
            {
                if (_dirty) return true;
                float now = Time.unscaledTime;
                if (now < _nextSignatureAt) return false;
                _nextSignatureAt = now + 0.25f;
                int sig = Signature();
                if (sig == _lastSignature) return false;
                _lastSignature = sig;
                return true;
            }
        }

        /// <summary>
        /// Structural fingerprint: every active Selectable, whether it can be used, plus the page,
        /// modal and phase. Cheap enough to take four times a second; the sections themselves are
        /// only rebuilt when this changes.
        /// </summary>
        private int Signature()
        {
            unchecked
            {
                int hash = 17;
                var all = Compat.AllSelectables();
                for (int i = 0; i < all.Count; i++)
                {
                    var s = all[i];
                    if (s == null) continue;
                    hash = hash * 31 + s.GetInstanceID();
                    hash = hash * 31 + (s.IsInteractable() ? 1 : 0);
                    hash = hash * 31 + (s.gameObject.activeInHierarchy ? 1 : 0);
                }
                hash = hash * 31 + (InRunWithoutMenu() ? 1 : 0);
                var modal = AccessibilityManager.GetActiveModal();
                hash = hash * 31 + (modal != null ? modal.GetInstanceID() : 0);
                return hash;
            }
        }

        public void BuildSnapshot(AccessSnapshot into)
        {
            _dirty = false;
            _lastSignature = Signature();

            List<NavigationSection> sections = null;
            if (!InRunWithoutMenu())
            {
                try { sections = SectionManager.GetSections(); }
                catch (Exception ex) { MelonLogger.Warning("[tree] Sections unavailable: " + ex.Message); }
            }

            if (InRunWithoutMenu())
            {
                _usingScan = false;
                BuildPhaseTree(into);
            }
            else if (sections != null && CountElements(sections) > 0)
            {
                _usingScan = false;
                BuildSectionTree(into, sections);
            }
            else
            {
                _usingScan = true;
                _scan.BuildSnapshot(into);
                if (!into.Contains(SelectedId())) into.FocusId = into.RootId;
            }
        }

        private static ulong SelectedId()
        {
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            return go != null ? UGuiSource.StableId(go) : 0;
        }

        private static int CountElements(List<NavigationSection> sections)
        {
            int n = 0;
            for (int i = 0; i < sections.Count; i++)
                if (sections[i]?.Elements != null) n += sections[i].Elements.Count;
            return n;
        }

        private void Reset(AccessSnapshot into)
        {
            into.Clear();
            _ring.Clear();
            _goById.Clear();
            _indexById.Clear();
            _sizeById.Clear();
            _tabListById.Clear();

            into.Add(new AccessNode(RootId, AccessRole.Window) { Label = Application.productName, Focusable = false });
            into.RootId = RootId;
        }

        private void BuildPhaseTree(AccessSnapshot into)
        {
            Reset(into);
            string phase = Gameplay.GameplayPhaseDetector.IsBattlePhase() ? "Battle" : "Shop";
            var root = into.Find(RootId);

            var screen = new AccessNode(ScreenId, AccessRole.Dialog) { Label = phase, Focusable = false };
            into.Add(screen);
            root.Children.Add(ScreenId);

            var node = new AccessNode(PhaseNodeId, AccessRole.Label) { Label = phase, Focusable = true };
            into.Add(node);
            screen.Children.Add(PhaseNodeId);
            _ring.Add(PhaseNodeId);
            into.FocusId = PhaseNodeId;
        }

        private void BuildSectionTree(AccessSnapshot into, List<NavigationSection> sections)
        {
            Reset(into);
            var root = into.Find(RootId);

            // Live elements only: the game destroys controls under the mod's feet.
            var live = new List<KeyValuePair<NavigationSection, List<SelectableBase>>>();
            GameObject first = null;
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section?.Elements == null) continue;
                var elements = new List<SelectableBase>();
                foreach (var e in section.Elements)
                {
                    try
                    {
                        if (e == null || e.gameObject == null || !e.gameObject.activeInHierarchy) continue;
                    }
                    catch { continue; }
                    elements.Add(e);
                    if (first == null) first = e.gameObject;
                }
                if (elements.Count > 0) live.Add(new KeyValuePair<NavigationSection, List<SelectableBase>>(section, elements));
            }

            if (live.Count == 0)
            {
                // Every cached element is gone: the game rebuilt the screen without changing the
                // mod's context key (a picker reopened over itself). Make the mod rebuild its
                // sections and try again next frame rather than publish an empty screen.
                SectionManager.InvalidateCache();
                _dirty = true;
            }

            GameObject screenRoot;
            string title = ScreenTitle(first, out screenRoot);
            var screen = new AccessNode(ScreenId, AccessRole.Dialog)
            {
                Label = title,
                Description = ScreenDescription(screenRoot, live, title),
                Focusable = false,
            };
            into.Add(screen);
            root.Children.Add(ScreenId);

            // A control listed in two sections belongs to the first. A section left with nothing
            // (the news screen's Actions, whose Dismiss is also the patch notes' entry) would be
            // an empty group, so it is dropped.
            var claimed = new HashSet<ulong>();
            var deduped = new List<KeyValuePair<NavigationSection, List<SelectableBase>>>();
            foreach (var pair in live)
            {
                var fresh = new List<SelectableBase>();
                foreach (var e in pair.Value)
                    if (claimed.Add(UGuiSource.StableId(e.gameObject))) fresh.Add(e);
                if (fresh.Count > 0) deduped.Add(new KeyValuePair<NavigationSection, List<SelectableBase>>(pair.Key, fresh));
            }
            live = deduped;

            // One section adds a group with nothing to say, so its controls sit in the dialog,
            // which then carries the set size: AccessKit answers UIA SizeOfSet from the item's
            // container, so without it every control reads as "n of 0".
            bool useGroups = live.Count > 1;
            if (!useGroups && live.Count == 1) screen.SizeOfSet = live[0].Value.Count;
            for (int s = 0; s < live.Count; s++)
            {
                var section = live[s].Key;
                var elements = live[s].Value;
                AccessNode parent = screen;
                if (useGroups)
                {
                    var group = new AccessNode(FirstSectionId + (ulong)s,
                        section.IsTabList ? AccessRole.TabList : AccessRole.Group)
                    {
                        Label = string.IsNullOrWhiteSpace(section.DisplayName) ? section.Name : section.DisplayName,
                        Focusable = false,
                        SizeOfSet = elements.Count,
                    };
                    into.Add(group);
                    screen.Children.Add(group.Id);
                    parent = group;
                }

                for (int i = 0; i < elements.Count; i++)
                {
                    var go = elements[i].gameObject;
                    ulong id = UGuiSource.StableId(go);
                    if (into.Contains(id)) continue; // listed in two sections: first one wins
                    AccessNode node;
                    try
                    {
                        node = new AccessNode(id, AccessRole.Button);
                        Populate(go, node, i + 1, elements.Count);
                        if (section.IsTabList)
                        {
                            _tabListById[id] = section;
                            ApplyTab(go, node, section);
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning("[tree] Skipped " + go.name + ": " + ex.Message);
                        continue;
                    }

                    into.Add(node);
                    parent.Children.Add(id);
                    _goById[id] = go;
                    _indexById[id] = i + 1;
                    _sizeById[id] = elements.Count;
                    if (!node.Disabled) _ring.Add(id);
                }
            }

            // Nothing the game has selected is on this screen yet: the mod moves focus a frame or
            // two after a screen opens. Focusing the first control meanwhile made the screen
            // reader start on it ("简体中文" before the picker's current "English"), and the real
            // focus then cancelled that speech, dialog title included, without saying the title
            // again. The window root says nothing worth cancelling and keeps the title for the
            // real focus.
            if (into.FocusId == 0 || !into.Contains(into.FocusId))
                into.FocusId = RootId;
        }

        // ---- one control ---------------------------------------------------------------------

        private static void ApplyTab(GameObject go, AccessNode node, NavigationSection tabList)
        {
            if (node.Role != AccessRole.Button) return;
            node.Role = AccessRole.Tab;
            node.Selected = go == tabList.SelectedElement;
        }

        private static void Populate(GameObject go, AccessNode node, int index, int total)
        {
            node.PositionInSet = index;
            node.SizeOfSet = total;
            node.AddAction(AccessAction.Focus);
            Bounds.Apply(go, node);

            var selectable = go.GetComponent<SelectableBase>();
            node.Disabled = selectable != null && !SafeInteractable(selectable);

            var input = go.GetComponent<InputFieldBase>();
            if (input != null)
            {
                var field = input.InputField;
                bool password = field != null && field.contentType == Il2CppTMPro.TMP_InputField.ContentType.Password;
                node.Role = password ? AccessRole.PasswordInput : AccessRole.TextInput;
                node.Label = Text(input.Label) ?? LabelFor(go);
                node.Value = password || field == null ? null : UGuiSource.Clean(field.text);
                node.Placeholder = field != null && field.placeholder != null
                    ? Text(Compat.As<Il2CppTMPro.TMP_Text>(field.placeholder))
                    : null;
                node.AddAction(AccessAction.Click).AddAction(AccessAction.SetValue);
                return;
            }

            var sliderBase = go.GetComponent<SliderBase>();
            if (sliderBase != null)
            {
                node.Role = AccessRole.Slider;
                node.Label = Text(sliderBase.Label) ?? LabelFor(go);
                var slider = sliderBase.Slider;
                if (slider != null)
                {
                    node.NumericValue = slider.value;
                    node.MinNumericValue = slider.minValue;
                    node.MaxNumericValue = slider.maxValue;
                    node.NumericValueStep = Math.Max((slider.maxValue - slider.minValue) / 20.0, 0.0001);
                }
                node.Value = AccessibilityManager.GetControlValue(go) ?? Text(sliderBase.LabelRight);
                node.AddAction(AccessAction.Increment).AddAction(AccessAction.Decrement).AddAction(AccessAction.SetValue);
                return;
            }

            var dropdownBase = go.GetComponent<DropdownBase>();
            if (dropdownBase != null)
            {
                node.Role = AccessRole.ComboBox;
                node.Label = Text(dropdownBase.Label) ?? LabelFor(go);
                node.Value = AccessibilityManager.GetControlValue(go);
                node.Expanded = false;
                node.AddAction(AccessAction.Click).AddAction(AccessAction.Increment).AddAction(AccessAction.Decrement);
                return;
            }

            // Picker options: one of them is the current choice, shown only by a checked icon.
            var picker = PickerInfo.OwnerOf(go);
            if (picker != null)
            {
                bool picked = PickerInfo.IsPicked(picker, go.GetComponent<ButtonBase>());
                node.Role = AccessRole.RadioButton;
                node.Label = LabelFor(go);
                node.Toggled = picked ? Toggled.True : Toggled.False;
                node.Selected = picked;
                node.Description = TooltipOf(go);
                node.AddAction(AccessAction.Click);
                return;
            }

            if (TryPopulatePack(go, node)) return;

            // Option buttons (settings) show their name on the left and the current choice on the
            // right ("Window mode ... Off"). Publish those as name and value, so the choice is
            // spoken as a value and a change to it is reported as one.
            var button = go.GetComponent<ButtonBase>();
            string left = button != null ? Text(button.Label) : null;
            string right = button != null ? Text(button.LabelRight) : null;
            if (left != null && right != null)
            {
                node.Role = AccessRole.Button;
                string over = SectionManager.GetLabelOverride(go);
                node.Label = !string.IsNullOrEmpty(over) && !left.Contains(over) ? over + ", " + left : left;
                node.Value = right;
                node.Description = TooltipOf(go);
                node.AddAction(AccessAction.Click);
                return;
            }

            node.Role = go.GetComponent<Scrollbar>() != null ? AccessRole.ScrollBar : AccessRole.Button;
            node.Label = LabelFor(go);
            string value = AccessibilityManager.GetControlValue(go);
            if (!string.IsNullOrEmpty(value) && (node.Label == null || !node.Label.Contains(value))) node.Value = value;
            node.Description = TooltipOf(go);
            node.AddAction(AccessAction.Click);
        }

        /// <summary>The same label the mod's own announcement used, minus role, value and position.</summary>
        private static string LabelFor(GameObject go)
        {
            string text = TextExtractor.GetElementText(go);
            string over = SectionManager.GetLabelOverride(go);
            if (!string.IsNullOrEmpty(over))
            {
                if (string.IsNullOrEmpty(text)) text = over;
                else if (!text.Contains(over)) text = over + ", " + text;
            }
            text = UGuiSource.Clean(text);
            text = WithoutTooltip(text, TooltipOf(go));
            return string.IsNullOrEmpty(text) ? Patches.PageManagerPatches.SplitCamelCase(go.name) : text;
        }

        /// <summary>
        /// A button's tooltip is a child of it, so once it shows (the mod hovers the focused
        /// control) its text joined the label: "Arena, Play against teams created by other
        /// players..." as the name and the same sentence again as the description.
        /// </summary>
        private static string WithoutTooltip(string text, string tip)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(tip) || text == tip) return text;
            if (text.EndsWith(", " + tip, StringComparison.Ordinal)) return text.Substring(0, text.Length - tip.Length - 2);
            if (text.StartsWith(tip + ", ", StringComparison.Ordinal)) return text.Substring(tip.Length + 2);
            return text.Replace(", " + tip + ", ", ", ");
        }

        /// <summary>
        /// Pack shop entries hold several texts (price, "New", pack name, pet count, days left)
        /// that read in layout order as "$10.00, New, Danger pack, 62, New pets". The name is the
        /// label, owned or the price is the value, the rest is the description.
        /// </summary>
        private static bool TryPopulatePack(GameObject go, AccessNode node)
        {
            var name = Find(go, "Tween/Name/Label");
            if (name == null || go.GetComponent<ButtonBase>() == null) return false;
            string label = Text(name);
            if (label == null) return false;

            node.Role = AccessRole.Button;
            node.Label = label;
            string state = AccessibilityManager.GetControlValue(go);
            string price = Text(Find(go, "Tween/PriceLabel/Background/Text"));
            node.Value = !string.IsNullOrEmpty(state) ? state : price;

            var details = PackDetails(go);
            if (price != null && node.Value != price) details.Add(price);
            string tip = TooltipOf(go);
            if (tip != null) details.Add(tip);
            node.Description = details.Count > 0 ? string.Join(", ", details) : null;
            node.AddAction(AccessAction.Click);
            return true;
        }

        private static List<string> PackDetails(GameObject go)
        {
            var details = new List<string>();
            if (Text(Find(go, "Tween/NewLabel/Background/Text")) != null) details.Add("New");
            if (Text(Find(go, "Tween/Wild/Background/Text")) != null) details.Add("Wild");
            string count = Text(Find(go, "Tween/Band/LabelTop"));
            string countWhat = Text(Find(go, "Tween/Band/LabelBottom"));
            if (count != null) details.Add(countWhat != null ? count + " " + countWhat.ToLowerInvariant() : count);
            string preview = Text(Find(go, "Tween/Preview/Label"));
            if (preview != null) details.Add(preview);
            return details;
        }

        /// <summary>
        /// The mod's own wording for a pack shop entry, name first ("Danger pack, $10.00, New,
        /// 62 new pets"), or null when this is not one. The texts in layout order read
        /// "$10.00, New, Danger pack, 62, New pets".
        /// </summary>
        internal static string PackSpeechText(GameObject go)
        {
            try
            {
                if (go == null || go.GetComponent<ButtonBase>() == null) return null;
                string name = Text(Find(go, "Tween/Name/Label"));
                if (name == null) return null;
                var parts = new List<string> { name };
                string price = Text(Find(go, "Tween/PriceLabel/Background/Text"));
                if (price != null) parts.Add(price);
                parts.AddRange(PackDetails(go));
                return string.Join(", ", parts);
            }
            catch { return null; }
        }

        private static Il2CppTMPro.TMP_Text Find(GameObject go, string path)
        {
            var t = go.transform.Find(path);
            return t != null ? t.GetComponent<Il2CppTMPro.TMP_Text>() : null;
        }

        internal static string TooltipOf(GameObject go)
        {
            try
            {
                var button = go.GetComponent<ButtonBase>();
                if (button == null || button.TooltipDisabled || button.Tooltip == null) return null;
                var text = Text(button.Tooltip.TextMesh);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch
            {
                return null;
            }
        }

        private static string Text(Il2CppTMPro.TMP_Text t)
        {
            if (t == null) return null;
            try
            {
                if (!t.gameObject.activeInHierarchy) return null;
                var s = UGuiSource.Clean(t.text);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }

        private static bool SafeInteractable(SelectableBase s)
        {
            try { return s.GetInteractable(); } catch { return true; }
        }

        // ---- screen ------------------------------------------------------------------------

        /// <summary>
        /// Names the screen the controls belong to: the open modal, the side menu, or the page,
        /// found by walking up from the first control.
        /// </summary>
        private static string ScreenTitle(GameObject first, out GameObject screenRoot)
        {
            screenRoot = null;
            var modal = AccessibilityManager.GetActiveModal();
            if (modal != null)
            {
                screenRoot = modal.gameObject;
                return Patches.PageManagerPatches.SplitCamelCase(modal.gameObject.name);
            }

            for (var t = first != null ? first.transform : null; t != null; t = t.parent)
            {
                if (t.GetComponent<SideBar>() != null)
                {
                    screenRoot = t.gameObject;
                    return "Pause menu";
                }
                if (t.GetComponent<Il2CppSpacewood.Unity.MonoBehaviours.Build.SettingsMenu>() != null)
                {
                    screenRoot = t.gameObject;
                    return "Settings";
                }
                if (t.GetComponent<Page>() != null)
                {
                    screenRoot = t.gameObject;
                    return Patches.PageManagerPatches.SplitCamelCase(t.gameObject.name);
                }
                var picker = t.GetComponent<Picker>();
                if (picker != null)
                {
                    screenRoot = t.gameObject;
                    return PickerInfo.Title(picker);
                }
                if (t.GetComponent<Il2CppSpacewood.Unity.UI.Modal>() != null)
                {
                    screenRoot = t.gameObject;
                    return Patches.PageManagerPatches.SplitCamelCase(t.gameObject.name);
                }
            }

            // Nothing recognised: name the screen after the object holding the controls' canvas
            // ("UserSettings" -> "User Settings"), which is how the game itself names its screens.
            var canvas = first != null ? first.GetComponentInParent<Canvas>() : null;
            if (canvas != null)
            {
                var holder = canvas.rootCanvas.transform.parent != null
                    ? canvas.rootCanvas.transform.parent
                    : canvas.rootCanvas.transform;
                screenRoot = holder.gameObject;
                return Patches.PageManagerPatches.SplitCamelCase(holder.name);
            }
            screenRoot = first != null ? first.transform.root.gameObject : null;
            return Application.productName;
        }

        /// <summary>
        /// The screen's own text, without the text of its controls (those are their names). The
        /// screen reader reads it once, when focus first lands inside the screen.
        /// </summary>
        private static string ScreenDescription(GameObject screenRoot, List<KeyValuePair<NavigationSection, List<SelectableBase>>> live, string title)
        {
            if (screenRoot == null) return null;
            try
            {
                var controls = new HashSet<int>();
                foreach (var pair in live)
                    foreach (var e in pair.Value) controls.Add(e.gameObject.transform.GetInstanceID());

                // Text a section reads line by line itself (the news screen's patch notes) stays
                // out: as the dialog's description the screen reader would read all of it every
                // time focus entered the dialog.
                var readElsewhere = new List<string>();
                foreach (var pair in live)
                {
                    string tag = pair.Key.Tag;
                    if (string.IsNullOrEmpty(tag)) continue;
                    string first = UGuiSource.Clean(tag.Split('\n')[0]);
                    if (!string.IsNullOrEmpty(first) && first.Length >= 12)
                        readElsewhere.Add(first.Length > 40 ? first.Substring(0, 40) : first);
                }

                var texts = screenRoot.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(false);
                // The title is already the dialog's name.
                var seen = new HashSet<string> { title ?? "" };
                var sb = new StringBuilder();

                // Stats tiles: label and number as one line, and for "most bought" tiles the pet
                // or food, which is only an icon ("Most bought pet. 27" said nothing about which).
                var statsItems = new HashSet<int>();
                foreach (var item in screenRoot.GetComponentsInChildren<StatsSummaryItem>(false))
                {
                    string line = StatsLine(item);
                    statsItems.Add(item.transform.GetInstanceID());
                    if (string.IsNullOrEmpty(line) || sb.Length >= MaxDescription) continue;
                    if (sb.Length > 0) sb.Append(". ");
                    sb.Append(line);
                }
                for (int i = 0; i < texts.Length && sb.Length < MaxDescription; i++)
                {
                    var t = texts[i];
                    if (t == null || InsideControl(t.transform, screenRoot.transform, controls)) continue;
                    if (statsItems.Count > 0 && InsideAny(t.transform, screenRoot.transform, statsItems)) continue;
                    string s = UGuiSource.Clean(t.text);
                    if (string.IsNullOrEmpty(s) || !seen.Add(s)) continue;
                    // The lobby's "28 / 50" counter says nothing on its own. The game calls these
                    // snacks (Stats: "Snacks gained").
                    if (UnderObjectNamed(t.transform, "Snacks", screenRoot.transform)) s = "Snacks: " + s;
                    if (readElsewhere.Exists(f => s.Contains(f))) continue;
                    if (sb.Length > 0) sb.Append(". ");
                    sb.Append(s);
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string StatsLine(StatsSummaryItem item)
        {
            try
            {
                string label = Text(item.Label);
                string value = Text(item.Value);
                if (label == null) return value;
                string metric = item.Metric.ToString();
                string what = null;
                if (metric == "MinionBought" || metric == "SpellBought")
                {
                    var sprite = item.Image != null ? item.Image.sprite : null;
                    if (sprite != null) what = Patches.PageManagerPatches.SplitCamelCase(sprite.name);
                }
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(what)) parts.Add(what);
                if (value != null) parts.Add(value);
                return parts.Count > 0 ? label + ": " + string.Join(", ", parts) : label;
            }
            catch
            {
                return null;
            }
        }

        private static bool InsideAny(Transform t, Transform stop, HashSet<int> ids)
        {
            for (; t != null && t != stop; t = t.parent)
                if (ids.Contains(t.GetInstanceID())) return true;
            return false;
        }

        private static bool UnderObjectNamed(Transform t, string name, Transform stop)
        {
            for (; t != null && t != stop; t = t.parent)
                if (t.name == name) return true;
            return false;
        }

        private static bool InsideControl(Transform t, Transform stop, HashSet<int> controls)
        {
            for (; t != null && t != stop; t = t.parent)
            {
                if (controls.Contains(t.GetInstanceID())) return true;
                if (t.GetComponent<Selectable>() != null || t.GetComponent<SelectableBase>() != null) return true;
            }
            return false;
        }

        /// <summary>In the shop or a battle with no menu, modal or side menu over it.</summary>
        private static bool InRunWithoutMenu()
        {
            try
            {
                if (!Gameplay.GameplayPhaseDetector.IsShopPhase() && !Gameplay.GameplayPhaseDetector.IsBattlePhase())
                    return false;
                if (AccessibilityManager.GetActiveModal() != null) return false;
                if (SectionManager.MenuOverlayOpen()) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---- refresh, focus, actions ----------------------------------------------------------

        public bool TryRefreshNode(ulong id, AccessNode into)
        {
            if (_usingScan) return _scan.TryRefreshNode(id, into);
            if (!RefreshAllowedThisFrame()) return false;

            GameObject go;
            if (!_goById.TryGetValue(id, out go) || go == null) return false;
            int index, size;
            _indexById.TryGetValue(id, out index);
            _sizeById.TryGetValue(id, out size);
            into.Reset(id, AccessRole.Button);
            Populate(go, into, index, size);
            NavigationSection tabList;
            if (_tabListById.TryGetValue(id, out tabList)) ApplyTab(go, into, tabList);
            return true;
        }

        private bool RefreshAllowedThisFrame()
        {
            int frame = Time.frameCount;
            if (frame != _refreshFrame)
            {
                _refreshFrame = frame;
                float now = Time.unscaledTime;
                _refreshAllowed = now >= _nextRefreshAt;
                if (_refreshAllowed) _nextRefreshAt = now + 0.1f;
            }
            return _refreshAllowed;
        }

        public void OnFocusChanged(ulong nodeId)
        {
            // The EventSystem selection IS the focus; nothing to mirror back.
        }

        /// <summary>
        /// Whether the screen reader will speak this control: it is in the tree, or it is in the
        /// mod's current sections, which the tree is built from. The second case is the first
        /// focus on a new screen: the mod announces it in the same frame it builds the screen's
        /// sections, before the tree has rebuilt, so "is it in the tree yet" said no and the
        /// control was spoken twice. It also makes the tree rebuild at once.
        /// </summary>
        public bool Publishes(GameObject go)
        {
            if (go == null) return false;
            ulong id = UGuiSource.StableId(go);
            if (!_usingScan && _goById.ContainsKey(id)) return true;
            if (InSections(go))
            {
                _dirty = true;
                return true;
            }
            return _usingScan && _scan.Resolve(id) != null;
        }

        private static bool InSections(GameObject go)
        {
            if (InRunWithoutMenu()) return false;
            try
            {
                var sections = SectionManager.GetSections();
                for (int s = 0; s < sections.Count; s++)
                {
                    var elements = sections[s]?.Elements;
                    if (elements == null) continue;
                    for (int e = 0; e < elements.Count; e++)
                    {
                        try
                        {
                            if (elements[e] != null && elements[e].gameObject == go) return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        public GameObject ResolveGameObject(ulong id)
        {
            if (_usingScan)
            {
                var sel = _scan.Resolve(id);
                return sel != null ? sel.gameObject : null;
            }
            GameObject go;
            return _goById.TryGetValue(id, out go) ? go : null;
        }

        public bool PerformAction(PendingAction action)
        {
            if (_usingScan) return _scan.PerformAction(action);

            var go = ResolveGameObject(action.TargetNode);
            if (go == null) return false;

            switch (action.Action)
            {
                case AccessAction.Focus:
                    EventSystem.current?.SetSelectedGameObject(go);
                    return true;
                case AccessAction.Click:
                case AccessAction.Expand:
                    return Activate(go);
                case AccessAction.Increment:
                    return Nudge(go, +1);
                case AccessAction.Decrement:
                    return Nudge(go, -1);
                case AccessAction.SetValue:
                    return SetValue(go, action);
            }
            return false;
        }

        private static bool Activate(GameObject go)
        {
            var es = EventSystem.current;
            if (es == null) return false;
            var input = go.GetComponent<InputFieldBase>();
            if (input != null && input.InputField != null)
            {
                es.SetSelectedGameObject(go);
                input.InputField.ActivateInputField();
                return true;
            }
            if (ExecuteEvents.Execute(go, new BaseEventData(es), ExecuteEvents.submitHandler)) return true;
            var pointer = new PointerEventData(es) { button = PointerEventData.InputButton.Left };
            return ExecuteEvents.Execute(go, pointer, ExecuteEvents.pointerClickHandler);
        }

        private static bool Nudge(GameObject go, int direction)
        {
            var sliderBase = go.GetComponent<SliderBase>();
            if (sliderBase != null && sliderBase.Slider != null)
            {
                var s = sliderBase.Slider;
                float step = s.wholeNumbers ? 1f : (s.maxValue - s.minValue) / 20f;
                s.value = Mathf.Clamp(s.value + step * direction, s.minValue, s.maxValue);
                return true;
            }
            var dropdownBase = go.GetComponent<DropdownBase>();
            if (dropdownBase != null && dropdownBase.Dropdown != null && dropdownBase.Dropdown.options.Count > 0)
            {
                var d = dropdownBase.Dropdown;
                int count = d.options.Count;
                d.value = (d.value + direction + count) % count;
                return true;
            }
            return false;
        }

        private static bool SetValue(GameObject go, PendingAction action)
        {
            var sliderBase = go.GetComponent<SliderBase>();
            if (action.HasNumericValue && sliderBase != null && sliderBase.Slider != null)
            {
                var s = sliderBase.Slider;
                s.value = Mathf.Clamp((float)action.NumericValue, s.minValue, s.maxValue);
                return true;
            }
            var input = go.GetComponent<InputFieldBase>();
            if (action.StringValue != null && input != null && input.InputField != null &&
                input.InputField.contentType != Il2CppTMPro.TMP_InputField.ContentType.Password)
            {
                input.InputField.text = action.StringValue;
                return true;
            }
            return false;
        }

        /// <summary>Screen-space bounds, so screen reader highlights and mouse routing line up.</summary>
        private static class Bounds
        {
            public static void Apply(GameObject go, AccessNode node)
            {
                try
                {
                    var rt = Compat.As<RectTransform>(go.transform);
                    if (rt == null) return;
                    var canvas = go.GetComponentInParent<Canvas>();
                    Camera cam = null;
                    if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    {
                        cam = canvas.worldCamera;
                        if (cam == null) cam = Camera.main;
                    }
                    Vector3 bl, tr;
                    Compat.GetWorldCorners(rt, out bl, out tr);
                    var min = RectTransformUtility.WorldToScreenPoint(cam, bl);
                    var max = RectTransformUtility.WorldToScreenPoint(cam, tr);
                    node.SetBounds(Mathf.Min(min.x, max.x), Screen.height - Mathf.Max(min.y, max.y),
                        Mathf.Abs(max.x - min.x), Mathf.Abs(max.y - min.y));
                }
                catch { }
            }
        }

        /// <summary>
        /// Screens the mod builds no sections for. Grouped by page instead of by Canvas, labelled
        /// with the same text the mod would speak.
        /// </summary>
        private sealed class ScanFallback : UGuiSource
        {
            protected override object GroupKeyFor(Selectable sel)
            {
                for (var t = sel.transform; t != null; t = t.parent)
                    if (t.GetComponent<Page>() != null || t.GetComponent<SideBar>() != null) return t.gameObject;
                return base.GroupKeyFor(sel);
            }

            protected override string GroupLabelFor(object key)
            {
                var go = Compat.As<GameObject>(key);
                if (go != null)
                    return go.GetComponent<SideBar>() != null ? "Pause menu" : Patches.PageManagerPatches.SplitCamelCase(go.name);
                return base.GroupLabelFor(key);
            }

            /// <summary>
            /// Also hides fully transparent hotspots with no text, such as the sign-in page's
            /// ConsoleButton (an invisible developer-console trigger).
            /// </summary>
            protected override bool IsVisible(Selectable s)
            {
                if (!base.IsVisible(s)) return false;
                var g = s.targetGraphic;
                if (g == null || g.color.a > 0.01f) return true;
                var texts = s.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(false);
                for (int i = 0; i < texts.Length; i++)
                    if (!string.IsNullOrEmpty(UGuiSource.Clean(texts[i].text))) return true;
                return false;
            }

            protected override string ExtractLabel(Selectable sel)
            {
                var text = LabelFor(sel.gameObject);
                return string.IsNullOrEmpty(text) ? base.ExtractLabel(sel) : text;
            }
        }
    }
}
