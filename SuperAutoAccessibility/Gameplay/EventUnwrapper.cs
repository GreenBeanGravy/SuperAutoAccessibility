using System;
using MelonLoader;
using Il2CppBoardEvents;
using Il2CppBoardEvents.Interfaces;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSpacewood.Core.Models.BoardResolver.BoardEvents.Wrappers;

namespace SuperAutoAccessibility.Gameplay
{
    /// <summary>
    /// Utility to unwrap Il2Cpp board event wrappers (Done&lt;T&gt;, Do&lt;T&gt;, Try&lt;T&gt;, CastEffect).
    /// The game's event pipeline wraps actual event data (DamageMinion, BuffMinion, etc.)
    /// inside lifecycle wrappers. This class extracts the inner event for narration.
    ///
    /// NOTE: Il2CppInterop's TryCast fails on constructed generic Il2Cpp types like Done&lt;DamageMinion&gt;.
    /// Instead, we cast to the non-generic BoardEvent base class and use Il2Cpp reflection to read
    /// the &lt;Event&gt;k__BackingField, then determine the concrete inner type from its Il2Cpp type name.
    /// </summary>
    public static class EventUnwrapper
    {
        /// <summary>
        /// Process a top-level IBoardEvent, recursively unwrapping containers.
        /// Calls the provided handler for each concrete event data object found.
        /// </summary>
        public static void ProcessEvent(IBoardEvent boardEvent, Action<IBoardEvent, string, string> handler)
        {
            if (boardEvent == null) return;

            try
            {
                string typeName = GetTypeName(boardEvent);

                // CastEffectsSequentially â€” container with list of CastEffects
                if (typeName == "CastEffectsSequentially")
                {
                    try
                    {
                        var seq = boardEvent.TryCast<CastEffectsSequentially>();
                        if (seq?.CastEffects != null)
                        {
                            for (int i = 0; i < seq.CastEffects.Count; i++)
                            {
                                try { ProcessEvent(seq.CastEffects[i], handler); }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    return;
                }

                // CastEffectsParallel â€” container with list of CastEffects
                if (typeName == "CastEffectsParallel")
                {
                    try
                    {
                        var par = boardEvent.TryCast<CastEffectsParallel>();
                        if (par?.CastEffects != null)
                        {
                            for (int i = 0; i < par.CastEffects.Count; i++)
                            {
                                try { ProcessEvent(par.CastEffects[i], handler); }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    return;
                }

                // CastEffect â€” container with list of child events (Try/Do/Done wrappers)
                if (typeName == "CastEffect")
                {
                    try
                    {
                        var cast = boardEvent.TryCast<CastEffect>();
                        if (cast?.Events != null)
                        {
                            for (int i = 0; i < cast.Events.Count; i++)
                            {
                                try { ProcessEvent(cast.Events[i], handler); }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    return;
                }

                // Done`1, Do`1, Try`1 â€” lifecycle wrappers containing the actual event data
                // We prefer Done (completed) events for narration; skip Try/Do to avoid duplicates
                if (typeName.StartsWith("Done`"))
                {
                    TryUnwrapAndDispatch(boardEvent, "Done", handler);
                    return;
                }

                // Skip Try and Do wrappers â€” we only narrate Done events to avoid duplicates
                if (typeName.StartsWith("Try`") || typeName.StartsWith("Do`"))
                {
                    return;
                }

                // Phase events and other non-wrapped events â€” pass through directly
                handler(boardEvent, typeName, "direct");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"EventUnwrapper.ProcessEvent error: {ex.Message}");
            }
        }

        /// <summary>
        /// Unwrap a Done/Do/Try wrapper by casting to the non-generic BoardEvent base class
        /// and reading the &lt;Event&gt;k__BackingField via Il2Cpp reflection.
        /// This bypasses the broken TryCast on constructed generic Il2Cpp types.
        /// </summary>
        private static void TryUnwrapAndDispatch(IBoardEvent wrapper, string wrapperKind, Action<IBoardEvent, string, string> handler)
        {
            try
            {
                // Step 1: Extract the inner event object via Il2Cpp reflection
                Il2CppSystem.Object innerObj = ExtractInnerEventReflection(wrapper);
                if (innerObj == null)
                {
                    string fullType = GetFullTypeName(wrapper);
                    MelonLogger.Msg($"[EventUnwrapper] Could not extract inner event from {wrapperKind} wrapper: {fullType}");
                    return;
                }

                // Step 2: Determine the concrete inner type name
                string innerTypeName = innerObj.GetIl2CppType().Name;
                MelonLogger.Msg($"[EventUnwrapper] Unwrapped {wrapperKind} -> {innerTypeName}");

                // Step 3: Dispatch with the discovered type name
                handler(wrapper, innerTypeName, wrapperKind);
            }
            catch (Exception ex)
            {
                try
                {
                    string fullType = GetFullTypeName(wrapper);
                    MelonLogger.Warning($"[EventUnwrapper] Error unwrapping {wrapperKind}: {fullType} - {ex.Message}");
                }
                catch
                {
                    MelonLogger.Warning($"[EventUnwrapper] Error unwrapping {wrapperKind}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Extract the inner event object from a Done/Do/Try wrapper using Il2Cpp reflection.
        /// Casts to non-generic BoardEvent base, then reads &lt;Event&gt;k__BackingField
        /// from the object's actual Il2Cpp class (which is the constructed generic like BoardEvent&lt;DamageMinion&gt;).
        /// </summary>
        private static Il2CppSystem.Object ExtractInnerEventReflection(IBoardEvent wrapper)
        {
            // Cast to Il2CppSystem.Object to get the raw Il2Cpp pointer
            var il2cppObj = wrapper.TryCast<Il2CppSystem.Object>();
            if (il2cppObj == null) return null;

            System.IntPtr objPtr = il2cppObj.Pointer;
            if (objPtr == System.IntPtr.Zero) return null;

            // Get the actual Il2Cpp class of this object (e.g. Done`1[[BoardEvents.DamageMinion]])
            System.IntPtr classPtr = IL2CPP.il2cpp_object_get_class(objPtr);
            if (classPtr == System.IntPtr.Zero) return null;

            // Walk the class hierarchy to find <Event>k__BackingField
            // It is declared on BoardEvent<T> which is a parent of Done<T>
            System.IntPtr fieldPtr = System.IntPtr.Zero;
            System.IntPtr searchClass = classPtr;
            while (searchClass != System.IntPtr.Zero && fieldPtr == System.IntPtr.Zero)
            {
                try
                {
                    fieldPtr = IL2CPP.il2cpp_class_get_field_from_name(searchClass, "<Event>k__BackingField");
                }
                catch { }

                if (fieldPtr == System.IntPtr.Zero)
                {
                    searchClass = IL2CPP.il2cpp_class_get_parent(searchClass);
                }
            }

            if (fieldPtr == System.IntPtr.Zero) return null;

            // Read the field value - the Event field is a reference type (Il2CppSystem.Object)
            // For reference type fields, read the pointer at (object + field_offset)
            int offset = (int)IL2CPP.il2cpp_field_get_offset(fieldPtr);
            unsafe
            {
                System.IntPtr fieldValuePtr = *(System.IntPtr*)((byte*)objPtr + offset);
                if (fieldValuePtr == System.IntPtr.Zero) return null;

                // Wrap the raw pointer as an Il2CppSystem.Object
                return new Il2CppSystem.Object(fieldValuePtr);
            }
        }

        /// <summary>
        /// Extract the inner event from a Done/Do/Try wrapper as a specific type.
        /// Uses Il2Cpp reflection to read &lt;Event&gt;k__BackingField, then TryCast to T.
        /// This replaces the old approach of TryCast&lt;Done&lt;T&gt;&gt; which fails for Il2Cpp generics.
        /// </summary>
        public static T ExtractDone<T>(IBoardEvent wrapper) where T : Il2CppSystem.Object
        {
            try
            {
                Il2CppSystem.Object innerObj = ExtractInnerEventReflection(wrapper);
                if (innerObj == null) return null;

                // TryCast on the concrete (non-generic) type works fine
                return innerObj.TryCast<T>();
            }
            catch { return null; }
        }

        /// <summary>
        /// Get the short Il2Cpp type name of an IBoardEvent.
        /// </summary>
        public static string GetTypeName(IBoardEvent evt)
        {
            try
            {
                var obj = evt.TryCast<Il2CppSystem.Object>();
                if (obj != null)
                    return obj.GetIl2CppType().Name;
            }
            catch { }
            return "Unknown";
        }

        /// <summary>
        /// Get the full Il2Cpp type name including namespace.
        /// </summary>
        public static string GetFullTypeName(IBoardEvent evt)
        {
            try
            {
                var obj = evt.TryCast<Il2CppSystem.Object>();
                if (obj != null)
                    return obj.GetIl2CppType().FullName;
            }
            catch { }
            return "Unknown";
        }
    }
}
