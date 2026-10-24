using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Romm;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Per-user EmulatorJS save-state / SRAM sync. Blobs are keyed by the opaque game id
/// (a ROM token from the games API) and the current authenticated user. A user linked to RomM
/// keeps the saves of RomM's games in their RomM account too.
/// </summary>
[ApiController]
[Route("Moonfin/Games/Saves")]
public class GameSavesController : ControllerBase
{
    private readonly GameSavesService _savesService;
    private readonly RommSaveSync _rommSaves;

    private const long MaxSaveBytes = 32 * 1024 * 1024; // 32 MB ceiling per blob

    public GameSavesController(GameSavesService savesService, RommSaveSync rommSaves)
    {
        _savesService = savesService;
        _rommSaves = rommSaves;
    }

    /// <summary>Downloads a stored save blob for the current user.</summary>
    [HttpGet("{gameId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSave(
        [FromRoute] string gameId,
        [FromQuery] string? kind,
        CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        var target = RommTarget(userId.Value, gameId, kind);
        var data = target != null
            ? await _rommSaves.ReadAsync(target, cancellationToken).ConfigureAwait(false)
            : await _savesService.GetAsync(userId.Value, gameId, kind ?? "state", cancellationToken).ConfigureAwait(false);
        if (data == null)
        {
            return NotFound();
        }

        return File(data, "application/octet-stream");
    }

    /// <summary>Uploads (overwrites) a save blob for the current user.</summary>
    [HttpPut("{gameId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PutSave(
        [FromRoute] string gameId,
        [FromQuery] string? kind,
        CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        if (ms.Length == 0)
        {
            return BadRequest(new { Error = "Empty save payload." });
        }

        if (ms.Length > MaxSaveBytes)
        {
            return BadRequest(new { Error = "Save payload too large." });
        }

        var target = RommTarget(userId.Value, gameId, kind);
        if (target != null)
        {
            await _rommSaves.WriteAsync(target, ms.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _savesService.SaveAsync(userId.Value, gameId, kind ?? "state", ms.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        return NoContent();
    }

    private RommSaveTarget? RommTarget(Guid userId, string gameId, string? kind) =>
        _rommSaves.Target(userId, gameId, kind, _savesService.ResolvePath(userId, gameId, kind ?? "state"));
}
