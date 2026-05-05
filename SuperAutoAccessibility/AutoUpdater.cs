using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MelonLoader;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Checks for mod updates on game start by comparing the local DLL's git blob SHA
    /// against the remote SHA from the GitHub Contents API. Zero maintenance â€” just push
    /// a new DLL to the repo and it auto-detects. No version file needed.
    ///
    /// If a new version is found, prompts the user with a native MessageBox (screen-reader
    /// accessible), downloads the new DLL, writes a PowerShell script to replace it after
    /// the game exits, and restarts the game via Steam.
    /// </summary>
    public static class AutoUpdater
    {
        // Native MessageBox via P/Invoke (avoids System.Windows.Forms dependency)
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        private const uint MB_YESNO = 0x04;
        private const uint MB_ICONINFORMATION = 0x40;
        private const int IDYES = 6;

        // GitHub API endpoint returns JSON with a "sha" field (git blob SHA-1)
        private const string API_URL =
            "https://api.github.com/repos/GreenBeanGravy/SAPAccess-Release/contents/SuperAutoAccessibility.dll";
        private const string DLL_URL =
            "https://raw.githubusercontent.com/GreenBeanGravy/SAPAccess-Release/main/SuperAutoAccessibility.dll";

        /// <summary>
        /// Kicks off the update check on a background thread so it doesn't block game startup.
        /// </summary>
        public static void CheckForUpdate()
        {
            Task.Run(CheckForUpdateAsync);
        }

        /// <summary>
        /// Computes the git blob SHA-1 hash for a file, matching what GitHub stores.
        /// Git blob hash = SHA1("blob {fileSize}\0{fileContents}")
        /// </summary>
        private static string ComputeGitBlobSha(byte[] fileBytes)
        {
            string header = $"blob {fileBytes.Length}\0";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            byte[] fullBytes = new byte[headerBytes.Length + fileBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, fullBytes, 0, headerBytes.Length);
            Buffer.BlockCopy(fileBytes, 0, fullBytes, headerBytes.Length, fileBytes.Length);
            using var sha1 = SHA1.Create();
            byte[] hash = sha1.ComputeHash(fullBytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Extracts the "sha" value from the GitHub Contents API JSON response.
        /// Simple string parsing to avoid needing a JSON library.
        /// </summary>
        private static string ExtractShaFromJson(string json)
        {
            // Look for "sha":"<40-char hex>"
            const string marker = "\"sha\":\"";
            int idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + marker.Length;
            int end = json.IndexOf('"', start);
            if (end < 0 || end - start != 40) return null;
            return json.Substring(start, 40);
        }

        private static async Task CheckForUpdateAsync()
        {
            try
            {
                // Compute local DLL hash
                string localDllPath = typeof(AutoUpdater).Assembly.Location;
                if (!File.Exists(localDllPath))
                {
                    MelonLogger.Warning("[AutoUpdater] Cannot find local DLL path.");
                    return;
                }
                byte[] localBytes = File.ReadAllBytes(localDllPath);
                string localSha = ComputeGitBlobSha(localBytes);

                // Fetch remote SHA from GitHub Contents API
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.Add("User-Agent", "SAPAccessMod-AutoUpdater");

                string json = await http.GetStringAsync(API_URL);
                string remoteSha = ExtractShaFromJson(json);

                if (string.IsNullOrEmpty(remoteSha))
                {
                    MelonLogger.Warning("[AutoUpdater] Could not parse remote SHA from GitHub API.");
                    return;
                }

                MelonLogger.Msg($"[AutoUpdater] Local SHA: {localSha}, Remote SHA: {remoteSha}");

                if (localSha == remoteSha)
                    return; // Already up to date

                // Prompt user via native MessageBox (screen-reader accessible)
                int result = MessageBoxW(
                    IntPtr.Zero,
                    "A new version of the Super Auto Pets Accessibility Mod is available.\n\n" +
                    "Would you like to update now? The game will restart automatically.",
                    "Mod Update Available",
                    MB_YESNO | MB_ICONINFORMATION);

                if (result != IDYES)
                {
                    MelonLogger.Msg("[AutoUpdater] User declined update.");
                    return;
                }

                MelonLogger.Msg("[AutoUpdater] Downloading update...");

                // Download new DLL to a temp path next to the current one
                string modsDir = Path.GetDirectoryName(localDllPath);
                string dllPath = Path.Combine(modsDir, "SuperAutoAccessibility.dll");
                string updatePath = Path.Combine(modsDir, "SuperAutoAccessibility.dll.update");

                byte[] dllBytes = await http.GetByteArrayAsync(DLL_URL);
                File.WriteAllBytes(updatePath, dllBytes);

                MelonLogger.Msg($"[AutoUpdater] Downloaded {dllBytes.Length} bytes to {updatePath}");

                // Write a PowerShell script that waits for the game to exit,
                // replaces the DLL, relaunches via Steam, then self-deletes
                string scriptPath = Path.Combine(modsDir, "sap_update.ps1");
                string scriptContent =
$@"Start-Sleep -Seconds 2
while (Get-Process 'Super Auto Pets' -ErrorAction SilentlyContinue) {{ Start-Sleep -Seconds 1 }}
Copy-Item -Path '{updatePath}' -Destination '{dllPath}' -Force
Remove-Item -Path '{updatePath}' -ErrorAction SilentlyContinue
Start-Process 'steam://rungameid/1714040'
Remove-Item -Path '{scriptPath}' -ErrorAction SilentlyContinue
";
                File.WriteAllText(scriptPath, scriptContent);

                // Launch the update script as a detached hidden process
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                MelonLogger.Msg("[AutoUpdater] Update script launched. Quitting game...");
                UnityEngine.Application.Quit();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[AutoUpdater] Update check failed: {ex.Message}");
            }
        }
    }
}
