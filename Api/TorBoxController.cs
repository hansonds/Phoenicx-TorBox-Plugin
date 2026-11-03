using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PhoenicxTorBox.Library;
using PhoenicxTorBox.Tasks;
using PhoenicxTorBox.TorBox;

namespace PhoenicxTorBox.Api;

/// <summary>Play links and the plugin page's buttons.</summary>
[ApiController]
[Route("PhoenicxTorBox")]
public class TorBoxController : ControllerBase
{
    private readonly TorBoxClient _torbox;
    private readonly LibrarySync _sync;
    private readonly ITaskManager _tasks;
    private readonly ILogger<TorBoxController> _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="TorBoxController"/> class.
    /// </summary>
    public TorBoxController(TorBoxClient torbox, LibrarySync sync, ITaskManager tasks, ILogger<TorBoxController> log)
    {
        _torbox = torbox;
        _sync = sync;
        _tasks = tasks;
        _log = log;
    }

    /// <summary>
    /// The link inside every .strm file. Sends the player on to a fresh TorBox link, so the video
    /// itself comes from TorBox, not through this server. The signature stops guessed links.
    /// </summary>
    [HttpGet("Play/{t}/{itemId}/{fileId}")]
    [HttpHead("Play/{t}/{itemId}/{fileId}")]
    [AllowAnonymous]
    public async Task<ActionResult> Play(
        [FromRoute] string t,
        [FromRoute] long itemId,
        [FromRoute] long fileId,
        [FromQuery] string? sig,
        CancellationToken ct)
    {
        t = t.ToLowerInvariant();
        if (TorBoxClient.FromShort(t) is not { } type || !PlayLinks.Verify(t, itemId, fileId, sig))
        {
            return NotFound();
        }

        try
        {
            var link = await _torbox.GetStreamLinkAsync(type, itemId, fileId, ct).ConfigureAwait(false);
            return Redirect(link);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Phoenicx TorBox: no link for {T}/{Item}/{File}: {Msg}", t, itemId, fileId, ex.Message);
            return StatusCode(502, "TorBox did not give a link for this file. It may have been removed from TorBox.");
        }
    }

    /// <summary>Folders, last sync result. For the plugin page.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> Status()
    {
        var p = Plugin.Instance!;
        return new
        {
            moviesPath = p.MoviesPath,
            showsPath = p.ShowsPath,
            hasKey = !string.IsNullOrWhiteSpace(p.Configuration.TorBoxApiKey),
            last = _sync.Status,
        };
    }

    /// <summary>Starts a sync now (as the scheduled task, so it shows in the dashboard).</summary>
    [HttpPost("Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SyncNow()
    {
        _tasks.CancelIfRunningAndQueue<SyncTask>();
        return NoContent();
    }

    /// <summary>Checks the saved API key.</summary>
    [HttpGet("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> Test(CancellationToken ct)
    {
        try
        {
            return new { ok = true, message = await _torbox.TestAsync(ct).ConfigureAwait(false) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { ok = false, message = ex.Message };
        }
    }
}
