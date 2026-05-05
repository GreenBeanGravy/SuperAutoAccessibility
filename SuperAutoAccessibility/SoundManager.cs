using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.Networking;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Manages loading and playing sound effects for accessibility cues.
    /// Sound files are embedded in the DLL as resources and extracted to a temp folder at runtime.
    ///
    /// IMPORTANT: AudioSource and AudioClip objects must be kept alive on the Il2Cpp side.
    /// Storing them only in managed C# fields is not sufficient â€” the Il2Cpp GC runs
    /// independently and will collect native objects it can't reach. We use DontDestroyOnLoad
    /// + HideFlags on clips, and re-acquire the AudioSource from the persistent GameObject
    /// on each play call.
    /// </summary>
    public static class SoundManager
    {
        private static GameObject _audioObject;
        private static Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private static bool _initialized = false;
        private static string _extractedSoundsFolder;
        private const float VOLUME_SCALE = 0.5f; // Master volume for all SFX (0.0 - 1.0)

        // Sound file names (without extension)
        private static readonly string[] SoundNames = {
            "SAP_IsFrozen",
            "SAP_CanCombine",
            "SAP_CanCombineAndWillLevelUp",
            "SAP_Unchained",
            "SAP_Chained"
        };

        /// <summary>
        /// Initializes the sound system. Creates a persistent AudioSource and loads all OGG files.
        /// Sounds are extracted from embedded resources in the DLL, with fallback to external files.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                // Create persistent audio GameObject
                _audioObject = new GameObject("SAPAccessibility_Audio");
                UnityEngine.Object.DontDestroyOnLoad(_audioObject);
                _audioObject.hideFlags = HideFlags.HideAndDontSave;

                var source = _audioObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f; // 2D by default

                // Extract embedded sounds to a temp folder
                _extractedSoundsFolder = Path.Combine(Path.GetTempPath(), "SAPAccessibility_Sounds");
                Directory.CreateDirectory(_extractedSoundsFolder);

                int embeddedCount = 0;
                var assembly = Assembly.GetExecutingAssembly();

                foreach (string name in SoundNames)
                {
                    string resourceName = $"SuperAutoAccessibility.Sounds.{name}.ogg";
                    string targetPath = Path.Combine(_extractedSoundsFolder, $"{name}.ogg");

                    // Extract from embedded resource
                    try
                    {
                        using (var stream = assembly.GetManifestResourceStream(resourceName))
                        {
                            if (stream != null)
                            {
                                using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                                {
                                    stream.CopyTo(fs);
                                }
                                embeddedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[SoundManager] Failed to extract {name}: {ex.Message}");
                    }

                    // If embedded extraction failed, try external file fallback
                    if (!File.Exists(targetPath))
                    {
                        // Try Mods/Sounds
                        string gamePath = Path.GetDirectoryName(assembly.Location);
                        string modsFolder = Directory.GetParent(gamePath)?.FullName ?? gamePath;
                        string externalPath = Path.Combine(modsFolder, "Sounds", $"{name}.ogg");

                        if (File.Exists(externalPath))
                        {
                            targetPath = externalPath;
                        }
                        else
                        {
                            // Dev fallback
                            string devPath = Path.Combine(@"D:\decomp\sounds", $"{name}.ogg");
                            if (File.Exists(devPath))
                                targetPath = devPath;
                        }
                    }

                    if (File.Exists(targetPath))
                    {
                        MelonCoroutines.Start(LoadAudioClip(name, targetPath));
                    }
                    else
                    {
                        MelonLogger.Warning($"[SoundManager] Sound not available: {name}");
                    }
                }

                MelonLogger.Msg($"[SoundManager] Extracted {embeddedCount}/{SoundNames.Length} sounds from DLL");
                _initialized = true;
                MelonLogger.Msg("[SoundManager] Sound system initialized");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SoundManager] Initialization error: {ex.Message}");
            }
        }

        /// <summary>
        /// Coroutine to load an OGG file as an AudioClip using UnityWebRequest.
        /// After loading, the clip is marked DontDestroyOnLoad and HideAndDontSave
        /// to prevent Il2Cpp GC from collecting the native object.
        /// </summary>
        private static IEnumerator LoadAudioClip(string name, string filePath)
        {
            string fileUri = "file:///" + filePath.Replace('\\', '/');

            var request = UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.OGGVORBIS);
            try
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        clip.name = name;

                        // Prevent Il2Cpp GC and Unity asset unloading from collecting this clip.
                        // Without these, the native Il2Cpp object can be garbage collected
                        // even though we hold a managed-side reference.
                        UnityEngine.Object.DontDestroyOnLoad(clip);
                        clip.hideFlags = HideFlags.HideAndDontSave;

                        _clips[name] = clip;
                        MelonLogger.Msg($"[SoundManager] Loaded: {name}");
                    }
                    else
                    {
                        MelonLogger.Warning($"[SoundManager] Clip null after load: {name}");
                    }
                }
                else
                {
                    MelonLogger.Warning($"[SoundManager] Failed to load {name}: {request.error}");
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Plays a sound effect by name (center panning).
        /// </summary>
        public static void Play(string clipName)
        {
            PlaySpatial(clipName, 0f);
        }

        /// <summary>
        /// Gets the AudioSource from the persistent GameObject.
        /// Re-acquires from the GameObject each time to avoid stale Il2Cpp references.
        /// The AudioSource is a component on a DontDestroyOnLoad + HideAndDontSave GameObject,
        /// so it stays alive as long as the GameObject does.
        /// </summary>
        private static AudioSource GetAudioSource()
        {
            if (_audioObject == null) return null;
            try
            {
                return _audioObject.GetComponent<AudioSource>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Plays a sound effect with stereo panning.
        /// pan: -1 = full left (player), 0 = center, +1 = full right (enemy)
        /// </summary>
        public static void PlaySpatial(string clipName, float pan)
        {
            if (!_initialized) return;

            if (!_clips.TryGetValue(clipName, out var clip))
            {
                return; // Clip not loaded, silently skip
            }

            try
            {
                var source = GetAudioSource();
                if (source == null)
                {
                    MelonLogger.Warning("[SoundManager] AudioSource not found on persistent object");
                    return;
                }

                source.panStereo = Mathf.Clamp(pan, -1f, 1f);
                source.PlayOneShot(clip, VOLUME_SCALE);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SoundManager] Play error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a clip is loaded and available.
        /// </summary>
        public static bool HasClip(string clipName)
        {
            return _clips.ContainsKey(clipName);
        }
    }
}
