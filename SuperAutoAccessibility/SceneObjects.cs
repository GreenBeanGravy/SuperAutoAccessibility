using System.Collections.Generic;
using UnityEngine;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// FindObjectOfType without a scene scan per call.
    ///
    /// Under IL2CPP, FindObjectOfType costs about 2.5 ms however few objects of the type exist
    /// (FindAnyObjectByType still 0.8 ms). The mod polled some twenty screens that way every
    /// frame: about 45 ms a frame, the game at 18 fps. A screen reader then waited too: NVDA's
    /// requests to the game window (WM_GETOBJECT, the window's MSAA object) are answered only
    /// when Unity reads its messages, once a frame, and NVDA makes several on a focus change,
    /// so each focus move took most of a second to be spoken.
    ///
    /// Each type's instances, active or not, are listed once and the list is read again when a
    /// scene loads or unloads, when a listed object is destroyed, and every two seconds (every
    /// quarter second while there are none) to pick up objects created later. The game keeps
    /// its overlays in the scene and switches them on and off, so a screen opening is seen the
    /// frame it happens.
    ///
    /// A timed re-read still scans the scene (1.5 to 2 ms), so <see cref="Tick"/> does them
    /// one a frame, most overdue first, and none in a frame where a key went down: that frame
    /// moves focus and answers the screen reader, which waits on it. Types nobody asked for in
    /// the last second are left alone.
    /// </summary>
    internal static class SceneObjects
    {
        private static int _version;
        private static readonly List<CacheBase> _caches = new List<CacheBase>();

        /// <summary>Scene scans so far, for profiling.</summary>
        internal static int Scans;

        /// <summary>A scene loaded or unloaded: every list is read again on its next use.</summary>
        public static void SceneChanged() => _version++;

        /// <summary>
        /// The first active instance of T in instance-id order, as FindObjectOfType returns,
        /// or null.
        /// </summary>
        public static T Find<T>() where T : Component => Cache<T>.Instance.Find();

        /// <summary>Every live instance of T, active or not, in instance-id order.</summary>
        public static IReadOnlyList<T> All<T>() where T : Component => Cache<T>.Instance.All();

        /// <summary>Once a frame, before the mod's polls: the timed re-read that is due.</summary>
        public static void Tick()
        {
            if (ModInput.anyKeyDown) return;
            float now = Time.realtimeSinceStartup;
            CacheBase due = null;
            for (int i = 0; i < _caches.Count; i++)
            {
                var c = _caches[i];
                if (c.NextRefresh > now || now - c.LastUsed > 1f) continue;
                if (due == null || c.NextRefresh < due.NextRefresh) due = c;
            }
            due?.Refresh(now);
        }

        private abstract class CacheBase
        {
            public float NextRefresh;
            public float LastUsed;
            public abstract void Refresh(float now);
        }

        private sealed class Cache<T> : CacheBase where T : Component
        {
            public static readonly Cache<T> Instance = new Cache<T>();

            private readonly List<T> _items = new List<T>();
            private int _version = -1;
            // Types first read in the same frame would all come due together.
            private readonly float _stagger = (typeof(T).FullName.GetHashCode() & 0x3FF) / 1024f * 0.25f;

            private static readonly System.Comparison<T> ByInstanceId =
                (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID());

            private Cache() => _caches.Add(this);

            public T Find()
            {
                Prepare();
                for (int i = 0; i < _items.Count; i++)
                {
                    var item = _items[i];
                    if (item == null)
                    {
                        // Destroyed: read the list again soon.
                        NextRefresh = 0f;
                        continue;
                    }
                    if (item.gameObject.activeInHierarchy) return item;
                }
                return null;
            }

            public IReadOnlyList<T> All()
            {
                Prepare();
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] != null) continue;
                    _items.RemoveAt(i);
                    NextRefresh = 0f;
                }
                return _items;
            }

            private void Prepare()
            {
                float now = Time.realtimeSinceStartup;
                LastUsed = now;
                // A new scene, or never read: now, whatever it costs. Timed re-reads are Tick's.
                if (_version != SceneObjects._version) Refresh(now);
            }

            public override void Refresh(float now)
            {
                _items.Clear();
                try
                {
                    // Unsorted, then sorted here: Unity's InstanceID sort doubles the scan.
                    var found = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    Scans++;
                    for (int i = 0; i < found.Length; i++)
                        if (found[i] != null) _items.Add(found[i]);
                    if (_items.Count > 1) _items.Sort(ByInstanceId);
                }
                catch { }
                _version = SceneObjects._version;
                NextRefresh = now + (_items.Count == 0 ? 0.25f : 2f) + _stagger;
            }
        }
    }
}
