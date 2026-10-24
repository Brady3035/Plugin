using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// A dedicated log for working out why a badge is or isnt appearing. It records every
    /// marker request a client makes and what it was answered with.
    /// </summary>
    public class AnimeMarkerDiagnosticLog
    {
        private const string LogFileName = "moonfin-anime-markers.log";

        // Past this the log rotates to a .1 file, so only the current file and one previous are kept.
        private const long MaxBytes = 2 * 1024 * 1024;

        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public AnimeMarkerDiagnosticLog(ILogger logger)
        {
            _logger = logger;
            FilePath = Path.Combine(Plugin.Instance?.LogDirectoryPath ?? Path.GetTempPath(), LogFileName);
        }

        public static bool Enabled =>
            Plugin.Instance?.Configuration?.AnimeMarkerVerboseLogging == true;

        public string FilePath { get; }

        public long? SizeBytes => File.Exists(FilePath) ? new FileInfo(FilePath).Length : (long?)null;

        /// <summary>
        /// Appends one timestamped line while verbose logging is on. Never throws, since a
        /// diagnostic that breaks its own request is worse than none.
        /// </summary>
        public void Write(string message)
        {
            if (!Enabled)
            {
                return;
            }

            _writeLock.Wait();
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    var rotated = FilePath + ".1";
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(FilePath, rotated);
                }

                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.Debug("Anime marker diagnostic log could not be written: " + ex.Message);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<bool> ClearAsync()
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var removed = false;
                foreach (var path in new[] { FilePath, FilePath + ".1" })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        removed = true;
                    }
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.Warn("Anime marker diagnostic log could not be cleared: " + ex.Message);
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public IReadOnlyList<string> Tail(int lines)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return Array.Empty<string>();
                }

                var all = File.ReadAllLines(FilePath);
                return all.Length <= lines ? all : all.Skip(all.Length - lines).ToArray();
            }
            catch (Exception ex)
            {
                _logger.Debug("Anime marker diagnostic log could not be read: " + ex.Message);
                return Array.Empty<string>();
            }
        }
    }
}
