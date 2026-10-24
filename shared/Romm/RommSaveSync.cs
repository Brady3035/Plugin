using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.Romm;

/// <summary>Where one of a linked user's saves lives in RomM.</summary>
public sealed class RommSaveTarget
{
    private readonly RommClient _client;

    internal RommSaveTarget(RommClient client, int romId, bool isState, string emulator, string fileName, string localPath)
    {
        _client = client;
        RomId = romId;
        IsState = isState;
        Emulator = emulator;
        FileName = fileName;
        LocalPath = localPath;
    }

    public int RomId { get; }

    /// <summary>A save state, or else a battery save.</summary>
    public bool IsState { get; }

    public string Emulator { get; }

    public string FileName { get; }

    /// <summary>The plugin's own copy of the save.</summary>
    public string LocalPath { get; }

    internal Task<RommAsset?> FindAsync(CancellationToken cancellationToken) => IsState
        ? _client.FindStateAsync(RomId, FileName, cancellationToken)
        : _client.LatestSaveAsync(RomId, RommSaveSync.AutosaveSlot, cancellationToken);

    internal Task<byte[]> DownloadAsync(int id, CancellationToken cancellationToken) =>
        _client.DownloadAssetAsync(IsState ? "states" : "saves", id, cancellationToken);

    internal Task<RommAsset> UploadAsync(byte[] data, CancellationToken cancellationToken) => IsState
        ? _client.UploadStateAsync(RomId, Emulator, FileName, data, cancellationToken)
        : _client.UploadSaveAsync(RomId, Emulator, RommSaveSync.AutosaveSlot, FileName, data, cancellationToken);
}

/// <summary>
/// Keeps a linked user's RomM saves in their RomM account. The newer copy wins, and the plugin
/// keeps its own copy in step with RomM's update time, so a RomM outage never loses a save.
/// </summary>
public sealed class RommSaveSync
{
    /// <summary>The slot RomM's own player and sync clients file automatic progress under.</summary>
    public const string AutosaveSlot = "autosave";

    // The apps key states by backend and core (ejs-snes-romm-12) and battery saves by game alone.
    private static readonly Regex SaveKey = new Regex(@"^(?:(ejs|lr)-([A-Za-z0-9]+)-)?romm-(\d+)$", RegexOptions.Compiled);

    // Covers the gap between RomM's clock and a file time set from it.
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    private static readonly SemaphoreSlim FileLock = new SemaphoreSlim(1, 1);

    private readonly RommSource _source;

    public RommSaveSync(RommSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Null when the save stays only in the plugin: the user isn't linked, the game isn't from
    /// RomM, it is app settings, or the plugin had no file name for it. A bare key read as a state
    /// is the old EmulatorJS name, which no RomM game ever used.
    /// </summary>
    public RommSaveTarget? Target(Guid userId, string? gameId, string? kind, string? localPath)
    {
        var match = gameId == null ? null : SaveKey.Match(gameId);
        if (localPath == null || match == null || !match.Success || _source.Accounts?.TokenFor(userId) == null ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var romId))
        {
            return null;
        }

        var source = _source.ForUser(userId);
        var client = source.CreateClient();
        var normalizedKind = kind?.Trim().ToLowerInvariant();
        if (client == null || normalizedKind == "settings")
        {
            return null;
        }

        if (normalizedKind == "sram")
        {
            return source.FindGameAndPlatform(RommSource.GameIdPrefix + romId) is (var game, var platform)
                ? new RommSaveTarget(client, romId, false, platform.Core, Path.GetFileNameWithoutExtension(game.ServedName) + ".srm", localPath)
                : null;
        }

        if (!match.Groups[1].Success)
        {
            return null;
        }

        var emulator = match.Groups[1].Value + "-" + match.Groups[2].Value;
        return new RommSaveTarget(client, romId, true, emulator, "moonfin-" + emulator + ".state", localPath);
    }

    /// <summary>The newer of RomM's copy and the plugin's, or the plugin's while RomM is down.</summary>
    public async Task<byte[]?> ReadAsync(RommSaveTarget target, CancellationToken cancellationToken)
    {
        var localPath = target.LocalPath;
        DateTime? localUtc = File.Exists(localPath) ? File.GetLastWriteTimeUtc(localPath) : (DateTime?)null;
        RommAsset? remote;
        try
        {
            remote = await target.FindAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (RommAccounts.IsRommFailure(e))
        {
            return await ReadLocalAsync(localPath).ConfigureAwait(false);
        }

        if (remote != null && (localUtc == null || remote.UpdatedUtc > localUtc.Value + Tolerance))
        {
            try
            {
                var bytes = await target.DownloadAsync(remote.Id, cancellationToken).ConfigureAwait(false);
                await WriteLocalAsync(localPath, bytes, remote.UpdatedUtc).ConfigureAwait(false);
                return bytes;
            }
            catch (Exception e) when (RommAccounts.IsRommFailure(e))
            {
                return await ReadLocalAsync(localPath).ConfigureAwait(false);
            }
        }

        var local = await ReadLocalAsync(localPath).ConfigureAwait(false);
        if (local != null && (remote == null || (localUtc != null && localUtc.Value > remote.UpdatedUtc + Tolerance)))
        {
            // RomM never got this copy, from a failed upload or a save made before linking.
            _ = Task.Run(() => PushAsync(target, local, CancellationToken.None));
        }

        return local;
    }

    /// <summary>
    /// Keeps the save in the plugin, then sends it to RomM. False when RomM didn't take it, which
    /// leaves the plugin's copy newer so the next read sends it again.
    /// </summary>
    public async Task<bool> WriteAsync(RommSaveTarget target, byte[] data, CancellationToken cancellationToken)
    {
        var previous = await ReadLocalAsync(target.LocalPath).ConfigureAwait(false);
        if (previous != null && previous.AsSpan().SequenceEqual(data))
        {
            // Unchanged, so RomM gets no new version. A copy still waiting is sent on the next read.
            return true;
        }

        await WriteLocalAsync(target.LocalPath, data, null).ConfigureAwait(false);
        return await PushAsync(target, data, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> PushAsync(RommSaveTarget target, byte[] data, CancellationToken cancellationToken)
    {
        RommAsset asset;
        try
        {
            asset = await target.UploadAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (RommAccounts.IsRommFailure(e))
        {
            return false;
        }

        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // A newer save may have landed while this one uploaded, and it keeps its own time.
            if (File.Exists(target.LocalPath) && File.ReadAllBytes(target.LocalPath).AsSpan().SequenceEqual(data))
            {
                File.SetLastWriteTimeUtc(target.LocalPath, asset.UpdatedUtc);
            }
        }
        finally
        {
            FileLock.Release();
        }

        return true;
    }

    private static async Task<byte[]?> ReadLocalAsync(string path)
    {
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task WriteLocalAsync(string path, byte[] data, DateTime? updatedUtc)
    {
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            if (updatedUtc != null)
            {
                File.SetLastWriteTimeUtc(path, updatedUtc.Value);
            }
        }
        finally
        {
            FileLock.Release();
        }
    }
}
