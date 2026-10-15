using System;
using Il2CppSpacewood.Unity.UI;
using UnityEngine;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Facts about the open Picker (the game's option list popup: language, settings choices,
    /// match options) that the navigation, the announcements and the accessibility tree share.
    /// </summary>
    public static class PickerInfo
    {
        /// <summary>
        /// The picker's title. Many pickers leave their Title empty (the language picker does), so
        /// fall back to the label of the button that opened it, which is what the user just chose.
        /// </summary>
        public static string Title(Picker picker)
        {
            if (picker == null) return "Options";
            try
            {
                if (picker.Title != null && picker.Title.gameObject.activeInHierarchy &&
                    !string.IsNullOrWhiteSpace(picker.Title.text))
                    return TextExtractor.CleanTextPublic(picker.Title.text);
            }
            catch { }
            try
            {
                var target = picker.ButtonTarget;
                // The leaderboard's board picker would be titled after the current board ("Party").
                if (target != null && target.GetComponentInParent<Il2CppSpacewood.Unity.VersusLeaderboard>() != null)
                    return "Leaderboard";
                if (target != null)
                {
                    if (target.Label != null && !string.IsNullOrWhiteSpace(target.Label.text))
                        return TextExtractor.CleanTextPublic(target.Label.text);
                    string over = SectionManager.GetLabelOverride(target.gameObject);
                    if (!string.IsNullOrEmpty(over)) return over;
                }
            }
            catch { }
            return "Options";
        }

        /// <summary>The picker's note under the title, or null.</summary>
        public static string Note(Picker picker)
        {
            try
            {
                if (picker?.Note != null && picker.Note.gameObject.activeInHierarchy &&
                    !string.IsNullOrWhiteSpace(picker.Note.text))
                    return TextExtractor.CleanTextPublic(picker.Note.text);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Whether this option is currently chosen. Single-choice pickers (Language) show it by
        /// activating the option's Activator highlight; multi-select pickers give its icon the
        /// picker's checked checkbox sprite. Nothing else on the button records it.
        /// </summary>
        public static bool IsPicked(Picker picker, ButtonBase option)
        {
            try
            {
                if (option == null) return false;
                var pb = option.GetComponentInParent<PickerButton>();
                if (pb != null && pb.Activator != null && pb.Activator.gameObject.activeInHierarchy) return true;

                if (picker == null || option.Icon == null || picker.CheckboxChecked == null) return false;
                if (!option.Icon.gameObject.activeInHierarchy) return false;
                var sprite = option.Icon.sprite;
                return sprite != null && sprite.GetInstanceID() == picker.CheckboxChecked.GetInstanceID();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// An option that switches on and off by itself (its icon is the picker's checkbox,
        /// checked or not) rather than one choice of several: the Difficulty picker's Cursed
        /// modifier beside Normal, Hard and Super Hard, or every option of a multi-select picker.
        /// </summary>
        public static bool IsCheckbox(Picker picker, ButtonBase option)
        {
            try
            {
                if (picker == null || option?.Icon == null || !option.Icon.gameObject.activeInHierarchy) return false;
                var sprite = option.Icon.sprite;
                if (sprite == null) return false;
                int id = sprite.GetInstanceID();
                return (picker.Checkbox != null && id == picker.Checkbox.GetInstanceID()) ||
                       (picker.CheckboxChecked != null && id == picker.CheckboxChecked.GetInstanceID());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The open picker this control is an option of, or null.</summary>
        public static Picker OwnerOf(GameObject go)
        {
            try
            {
                var pb = go != null ? go.GetComponentInParent<PickerButton>() : null;
                if (pb == null || pb.Primary == null || pb.Primary.gameObject != go) return null;
                return go.GetComponentInParent<Picker>() ?? SceneObjects.Find<Picker>();
            }
            catch
            {
                return null;
            }
        }
    }
}
