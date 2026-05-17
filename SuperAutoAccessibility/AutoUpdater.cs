using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MelonLoader;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// On game start, fetches the latest release of GreenBeanGravy/SuperAutoAccessibility,
    /// reads the SuperAutoAccessibility.dll asset's SHA-256 digest from the API response
    /// (no asset download needed), and compares against the local DLL. If they differ
    /// (content update) or the local file is named with the legacy SuperAutoPetsMod.dll
    /// filename (migration), prompts the user to update and hands off to a PowerShell
    /// script that performs the replace/rename after the game exits, then relaunches
    /// via Steam.
    /// </summary>
    public static class AutoUpdater
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        private const uint MB_YESNO = 0x04;
        private const uint MB_ICONINFORMATION = 0x40;
        private const int IDYES = 6;

        private const string EXPECTED_DLL_NAME = "SuperAutoAccessibility.dll";
        private const string LEGACY_DLL_NAME = "SuperAutoPetsMod.dll";
        private const string LATEST_RELEASE_API =
            "https://api.github.com/repos/GreenBeanGravy/SuperAutoAccessibility/releases/latest";

        public static void CheckForUpdate()
        {
            Task.Run(CheckForUpdateAsync);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // Find the asset whose "name" equals targetName, then read its "digest" and
        // "browser_download_url" fields. GitHub release-asset digests are formatted as
        // "sha256:<hex>"; we return just the lowercase hex digest along with the URL.
        // Returns (null, null) on not-found / parse failure.
        private static (string digest, string url) FindAssetDigestAndUrl(string json, string targetName)
        {
            int assetsIdx = json.IndexOf("\"assets\":[", StringComparison.Ordinal);
            if (assetsIdx < 0) return (null, null);

            const string nameMarker = "\"name\":\"";
            const string digestMarker = "\"digest\":\"";
            const string urlMarker = "\"browser_download_url\":\"";
            int cursor = assetsIdx;

            while (true)
            {
                int nameIdx = json.IndexOf(nameMarker, cursor, StringComparison.Ordinal);
                if (nameIdx < 0) return (null, null);
                int nameStart = nameIdx + nameMarker.Length;
                int nameEnd = json.IndexOf('"', nameStart);
                if (nameEnd < 0) return (null, null);
                string name = json.Substring(nameStart, nameEnd - nameStart);

                if (name == targetName)
                {
                    string digest = ReadStringField(json, digestMarker, nameEnd);
                    string url = ReadStringField(json, urlMarker, nameEnd);
                    if (!string.IsNullOrEmpty(digest))
                    {
                        const string prefix = "sha256:";
                        if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            digest = digest.Substring(prefix.Length);
                        digest = digest.ToLowerInvariant();
                    }
                    return (digest, url);
                }
                cursor = nameEnd + 1;
            }
        }

        private static string ReadStringField(string json, string marker, int from)
        {
            int idx = json.IndexOf(marker, from, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + marker.Length;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        private static async Task CheckForUpdateAsync()
        {
            try
            {
                string localDllPath = typeof(AutoUpdater).Assembly.Location;
                if (string.IsNullOrEmpty(localDllPath) || !File.Exists(localDllPath))
                {
                    MelonLogger.Warning("[AutoUpdater] Cannot resolve local DLL path.");
                    return;
                }

                string modsDir = Path.GetDirectoryName(localDllPath);
                string localFileName = Path.GetFileName(localDllPath);
                bool isLegacyName = string.Equals(localFileName, LEGACY_DLL_NAME, StringComparison.OrdinalIgnoreCase);

                string localSha = ComputeSha256(File.ReadAllBytes(localDllPath));

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.Add("User-Agent", "SAPAccessMod-AutoUpdater");
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                string releaseJson = await http.GetStringAsync(LATEST_RELEASE_API);
                var (remoteSha, assetUrl) = FindAssetDigestAndUrl(releaseJson, EXPECTED_DLL_NAME);
                if (string.IsNullOrEmpty(assetUrl) || string.IsNullOrEmpty(remoteSha))
                {
                    MelonLogger.Warning($"[AutoUpdater] Could not find {EXPECTED_DLL_NAME} asset (or its digest) in latest release.");
                    return;
                }

                bool contentDiffers = !string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase);

                MelonLogger.Msg($"[AutoUpdater] Local SHA: {localSha} ({localFileName}), Remote SHA: {remoteSha}");

                if (!contentDiffers && !isLegacyName)
                    return; // Up to date and correctly named.

                string promptText = contentDiffers
                    ? "A new version of the Super Auto Pets Accessibility Mod is available.\n\n" +
                      "Would you like to update now? The game will restart automatically."
                    : "The Super Auto Pets Accessibility Mod needs to migrate to its new filename.\n\n" +
                      "Apply now? The game will restart automatically.";

                int result = MessageBoxW(
                    IntPtr.Zero,
                    promptText,
                    "Mod Update Available",
                    MB_YESNO | MB_ICONINFORMATION);

                if (result != IDYES)
                {
                    MelonLogger.Msg("[AutoUpdater] User declined update.");
                    return;
                }

                MelonLogger.Msg("[AutoUpdater] Downloading new DLL...");
                byte[] remoteBytes = await http.GetByteArrayAsync(assetUrl);

                string targetDllPath = Path.Combine(modsDir, EXPECTED_DLL_NAME);
                string updatePath = Path.Combine(modsDir, EXPECTED_DLL_NAME + ".update");
                File.WriteAllBytes(updatePath, remoteBytes);
                MelonLogger.Msg($"[AutoUpdater] Wrote {remoteBytes.Length} bytes to {updatePath}");

                // Delete the legacy-named DLL only when it's a different file from the target
                // (avoid deleting the file we just wrote over via Copy-Item).
                string legacyDeleteLine = "";
                if (isLegacyName &&
                    !string.Equals(localDllPath, targetDllPath, StringComparison.OrdinalIgnoreCase))
                {
                    legacyDeleteLine = $"Remove-Item -Path '{localDllPath}' -ErrorAction SilentlyContinue\n";
                }

                string scriptPath = Path.Combine(modsDir, "sap_update.ps1");
                string scriptContent =
$@"Start-Sleep -Seconds 2
while (Get-Process 'Super Auto Pets' -ErrorAction SilentlyContinue) {{ Start-Sleep -Seconds 1 }}
Copy-Item -Path '{updatePath}' -Destination '{targetDllPath}' -Force
Remove-Item -Path '{updatePath}' -ErrorAction SilentlyContinue
{legacyDeleteLine}Start-Process 'steam://rungameid/1714040'
Remove-Item -Path '{scriptPath}' -ErrorAction SilentlyContinue
";
                File.WriteAllText(scriptPath, scriptContent);

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
