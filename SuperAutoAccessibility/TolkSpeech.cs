using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MelonLoader;

namespace SuperAutoAccessibility
{
    public static class TolkSpeech
    {
        private const string TolkDllName = "Tolk.dll";
        private const string TolkDotNetDllName = "TolkDotNet.dll";
        private const string NvdaClientDllName = "nvdaControllerClient64.dll";
        private const string SapiDllName = "SAAPI64.dll";

        private static Dictionary<string, IntPtr> loadedDlls = new Dictionary<string, IntPtr>();
        private static string tempDirectory;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);

        private delegate void LoadDelegate();
        private delegate void UnloadDelegate();
        private delegate bool OutputDelegate([MarshalAs(UnmanagedType.LPWStr)] string str, bool interrupt);
        private delegate IntPtr DetectScreenReaderDelegate();

        private static LoadDelegate Tolk_Load;
        private static UnloadDelegate Tolk_Unload;
        private static OutputDelegate Tolk_Output;
        private static DetectScreenReaderDelegate Tolk_DetectScreenReader;

        public static void Initialize()
        {
            try
            {
                string assemblyLocation = Assembly.GetExecutingAssembly().Location;
                string pluginPath = Path.GetDirectoryName(assemblyLocation);
                tempDirectory = Path.Combine(pluginPath, "TolkLibs");
                Directory.CreateDirectory(tempDirectory);

                MelonLogger.Msg($"Extracting Tolk libraries to: {tempDirectory}");

                ExtractAndLoadDll(NvdaClientDllName);
                ExtractAndLoadDll(SapiDllName);
                ExtractAndLoadDll(TolkDotNetDllName);
                ExtractAndLoadDll(TolkDllName);

                if (loadedDlls.ContainsKey(TolkDllName))
                {
                    InitializeTolk(loadedDlls[TolkDllName]);
                }
                else
                {
                    MelonLogger.Error("Failed to initialize Tolk. Speech synthesis will not be available.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Error initializing Tolk speech: {e.Message}");
                MelonLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        private static void ExtractAndLoadDll(string dllName)
        {
            string resourceName = $"SuperAutoAccessibility.{dllName}";

            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                MelonLogger.Error($"Failed to find embedded resource: {resourceName}");
                return;
            }

            try
            {
                string tempFilePath = Path.Combine(tempDirectory, dllName);

                if (!File.Exists(tempFilePath))
                {
                    using (FileStream fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fileStream);
                        fileStream.Flush();
                    }
                    Thread.Sleep(10);
                }

                if (File.Exists(tempFilePath))
                {
                    FileInfo fileInfo = new FileInfo(tempFilePath);
                    MelonLogger.Msg($"Ready to load {dllName}: {fileInfo.Length} bytes");
                }
                else
                {
                    MelonLogger.Error($"Failed to extract {dllName} to {tempFilePath}");
                    return;
                }

                IntPtr dllHandle = LoadLibrary(tempFilePath);
                if (dllHandle != IntPtr.Zero)
                {
                    loadedDlls[dllName] = dllHandle;
                    MelonLogger.Msg($"Successfully loaded {dllName}");
                }
                else
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    MelonLogger.Warning($"Failed to load {dllName} from {tempFilePath}. Error code: {errorCode}");
                }
            }
            finally
            {
                stream.Dispose();
            }
        }

        private static T GetDelegate<T>(IntPtr module, string procName) where T : class
        {
            IntPtr pAddressOfFunctionToCall = GetProcAddress(module, procName);
            if (pAddressOfFunctionToCall == IntPtr.Zero)
            {
                throw new Exception($"Failed to get address of {procName}");
            }
            return Marshal.GetDelegateForFunctionPointer(pAddressOfFunctionToCall, typeof(T)) as T;
        }

        private static void InitializeTolk(IntPtr tolkDllHandle)
        {
            Tolk_Load = GetDelegate<LoadDelegate>(tolkDllHandle, "Tolk_Load");
            Tolk_Unload = GetDelegate<UnloadDelegate>(tolkDllHandle, "Tolk_Unload");
            Tolk_Output = GetDelegate<OutputDelegate>(tolkDllHandle, "Tolk_Output");
            Tolk_DetectScreenReader = GetDelegate<DetectScreenReaderDelegate>(tolkDllHandle, "Tolk_DetectScreenReader");

            Tolk_Load();

            IntPtr pScreenReader = Tolk_DetectScreenReader();
            string detectedReader = pScreenReader != IntPtr.Zero ? Marshal.PtrToStringUni(pScreenReader) : "None";
            MelonLogger.Msg($"Tolk initialized. Detected screen reader: {detectedReader}");
        }

        public static void Speak(string text, bool interrupt = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (Tolk_Output != null)
            {
                // Never force interrupt â€” always queue speech so announcements don't cut each other off
                bool result = Tolk_Output(text, false);
                // Uncomment for debug logging:
                // MelonLogger.Msg($"Speech output: {text} (success: {result})");
            }
        }

        public static void Shutdown()
        {
            if (Tolk_Unload != null)
            {
                Tolk_Unload();
                MelonLogger.Msg("Tolk unloaded.");
            }

            foreach (var dll in loadedDlls.Values)
            {
                FreeLibrary(dll);
            }
            loadedDlls.Clear();
        }
    }
}
