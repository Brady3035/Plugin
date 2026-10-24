using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Downloads (or accepts an uploaded) EmulatorJS cores zip and installs its data/ folder
    /// under the plugin data folder for offline self-hosting, using the built-in ZipArchive.
    /// Runs in the background. Poll GetStatus.
    /// </summary>
    internal static class GameCoresHelper
    {
        private static readonly object Gate = new object();
        private static string _state = "idle";
        private static string? _error;
        private static int _filesInstalled;

        public static CoresStatus GetStatus()
        {
            lock (Gate)
            {
                return new CoresStatus
                {
                    Installed = LocalCoresInstalled(),
                    Downloading = _state == "downloading",
                    State = _state,
                    Error = _error,
                    FilesInstalled = _filesInstalled
                };
            }
        }

        public static void StartInstall()
        {
            if (!BeginInstall()) return;
            _ = Task.Run(RunDownloadInstallAsync);
        }

        /// <summary>Installs from an already-uploaded zip. The file is deleted when done.</summary>
        public static void StartInstallFromFile(string tempZipPath)
        {
            if (!BeginInstall()) return;
            _ = Task.Run(() => InstallFromTempFile(tempZipPath));
        }

        private static bool BeginInstall()
        {
            lock (Gate)
            {
                if (_state == "downloading") return false;
                _state = "downloading";
                _error = null;
                _filesInstalled = 0;
            }
            return true;
        }

        private static async Task RunDownloadInstallAsync()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"emulatorjs-{Guid.NewGuid():N}.zip");
            try
            {
                using var client = MoonfinHttp.CreateClient(TimeSpan.FromMinutes(20), "Moonfin/1.0");

                var zipUrl = await ResolveZipUrlAsync(client).ConfigureAwait(false);
                if (zipUrl == null) { Fail("No cores zip found. Upload a zip, set a Cores zip URL, or attach an 'emulatorjs-data.zip' asset to the plugin's GitHub release."); return; }

                using (var src = await client.GetStreamAsync(zipUrl).ConfigureAwait(false))
                using (var dst = File.Create(tempFile))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }

                InstallFromTempFile(tempFile);
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* ignore */ }
            }
        }

        private static void InstallFromTempFile(string tempZipPath)
        {
            var dataFolder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(dataFolder)) { Fail("Plugin data folder unavailable."); return; }

            var dataRoot = Path.Combine(dataFolder!, "emulatorjs", "data");
            try
            {
                var installed = ExtractDataFolder(tempZipPath, dataRoot);
                if (installed == 0) { Fail("The cores zip did not contain an EmulatorJS data folder (no loader.js)."); return; }
                lock (Gate) { _filesInstalled = installed; _state = "installed"; }
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { /* ignore */ }
            }
        }

        private static void Fail(string message)
        {
            lock (Gate) { _error = message; _state = "failed"; }
        }

        private static async Task<string?> ResolveZipUrlAsync(HttpClient client)
        {
            var overrideUrl = Plugin.Instance?.Configuration?.GamesCoreZipUrl;
            if (!string.IsNullOrWhiteSpace(overrideUrl)) return overrideUrl;

            // Otherwise look for an "emulatorjs-data.zip" asset on the plugin's latest release.
            var releaseJson = await client.GetStringAsync(
                "https://api.github.com/repos/Moonfin-Client/Plugin/releases/latest").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(releaseJson);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(url) && name != null &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.IndexOf("emulatorjs", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return url;
                }
            }
            return null;
        }

        private static int ExtractDataFolder(string zipPath, string dataRoot)
        {
            using var fs = File.OpenRead(zipPath);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

            var loader = entries.FirstOrDefault(e =>
                Path.GetFileName(e.FullName.Replace('\\', '/')).Equals("loader.js", StringComparison.OrdinalIgnoreCase));
            if (loader == null) return 0;

            var loaderKey = loader.FullName.Replace('\\', '/');
            var prefix = loaderKey.Substring(0, loaderKey.Length - "loader.js".Length);

            Directory.CreateDirectory(dataRoot);
            var rootFull = Path.GetFullPath(dataRoot);
            var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString()) ? rootFull : rootFull + Path.DirectorySeparatorChar;

            var count = 0;
            foreach (var entry in entries)
            {
                var key = entry.FullName.Replace('\\', '/');
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = key.Substring(prefix.Length);
                if (relative.Length == 0) continue;

                var destination = Path.GetFullPath(Path.Combine(dataRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(rootWithSep, StringComparison.Ordinal)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var entryStream = entry.Open())
                using (var outStream = File.Create(destination))
                {
                    entryStream.CopyTo(outStream);
                }
                count++;
            }
            return count;
        }

        public static bool LocalCoresInstalled()
        {
            var dataFolder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(dataFolder)) return false;
            return File.Exists(Path.Combine(dataFolder!, "emulatorjs", "data", "loader.js"));
        }

        public static string ResolveDataPath()
        {
            var overrideUrl = Plugin.Instance?.Configuration?.GamesCoreDataUrl;
            if (!string.IsNullOrWhiteSpace(overrideUrl))
                return overrideUrl!.EndsWith("/") ? overrideUrl : overrideUrl + "/";
            if (LocalCoresInstalled()) return "./data/";
            return "https://cdn.emulatorjs.org/stable/data/";
        }

        public static string LocalDataRoot()
        {
            var dataFolder = Plugin.Instance?.DataFolderPath ?? string.Empty;
            return Path.Combine(dataFolder, "emulatorjs", "data");
        }
    }

    internal static class GameSavesHelper
    {
        private static readonly object Gate = new object();

        private static string SavesRoot()
        {
            var dataFolder = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
            return Path.Combine(dataFolder, "saves");
        }

        public static byte[]? Get(Guid userId, string gameId, string kind)
        {
            var path = ResolvePath(userId, gameId, kind);
            if (path == null || !File.Exists(path)) return null;
            lock (Gate) { return File.ReadAllBytes(path); }
        }

        public static void Save(Guid userId, string gameId, string kind, byte[] data)
        {
            var path = ResolvePath(userId, gameId, kind);
            if (path == null) throw new ArgumentException("Invalid game id.", nameof(gameId));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Gate) { File.WriteAllBytes(path, data); }
        }

        internal static string? ResolvePath(Guid userId, string gameId, string kind)
        {
            if (string.IsNullOrWhiteSpace(gameId)) return null;
            var normalizedKind = kind?.Trim().ToLowerInvariant();
            var safeKind = (normalizedKind == "sram" || normalizedKind == "settings") ? normalizedKind : "state";
            var safeGame = SanitizeFileName(gameId);
            if (safeGame.Length == 0) return null;

            var root = SavesRoot();
            var path = Path.GetFullPath(Path.Combine(root, userId.ToString("N"), $"{safeGame}.{safeKind}"));
            var rootFull = Path.GetFullPath(root);
            var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString()) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            return path.StartsWith(rootWithSep, StringComparison.Ordinal) ? path : null;
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Where(c => Array.IndexOf(invalid, c) < 0 && c != '.').ToArray();
            var s = new string(chars);
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }
    }
}
