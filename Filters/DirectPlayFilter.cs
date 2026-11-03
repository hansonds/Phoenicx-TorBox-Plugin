using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using PhoenicxTorBox.Library;
using PhoenicxTorBox.TorBox;

namespace PhoenicxTorBox.Filters;

/// <summary>
/// Marks TorBox media sources as remote links the client opens itself, so the video goes from
/// TorBox straight to the client. Playback info also gets a fresh TorBox link in place of the
/// .strm link, so players don't even need to follow the redirect.
/// </summary>
public sealed class DirectPlayFilter : IAsyncActionFilter
{
    private readonly TorBoxClient _torbox;
    private readonly ILogger<DirectPlayFilter> _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectPlayFilter"/> class.
    /// </summary>
    public DirectPlayFilter(TorBoxClient torbox, ILogger<DirectPlayFilter> log)
    {
        _torbox = torbox;
        _log = log;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next().ConfigureAwait(false);
        if (Plugin.Instance?.Configuration.DirectToClients != true || executed.Result is not ObjectResult { Value: { } value })
        {
            return;
        }

        try
        {
            switch (value)
            {
                case PlaybackInfoResponse info when info.MediaSources is { Count: > 0 }:
                    foreach (var ms in info.MediaSources)
                    {
                        await PrepareAsync(ms, true, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    }

                    break;
                case BaseItemDto dto:
                    await PrepareItemAsync(dto).ConfigureAwait(false);
                    break;
                case QueryResult<BaseItemDto> list when list.Items is not null:
                    foreach (var dto in list.Items)
                    {
                        await PrepareItemAsync(dto).ConfigureAwait(false);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Phoenicx TorBox: could not prepare direct play");
        }
    }

    private async Task PrepareItemAsync(BaseItemDto dto)
    {
        if (dto.MediaSources is null)
        {
            return;
        }

        foreach (var ms in dto.MediaSources)
        {
            await PrepareAsync(ms, false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PrepareAsync(MediaSourceInfo ms, bool freshLink, CancellationToken ct)
    {
        if (!PlayLinks.TryParse(ms.Path, out var type, out var itemId, out var fileId))
        {
            return;
        }

        ms.Protocol = MediaProtocol.Http;
        ms.IsRemote = true;
        ms.SupportsDirectPlay = true;

        // The .strm files hold the server's local address; apps get the public one.
        ms.Path = PlayLinks.ToPublic(ms.Path!, Plugin.Instance?.Configuration.PublicServerUrl ?? string.Empty);

        if (!freshLink)
        {
            return;
        }

        try
        {
            ms.Path = await _torbox.GetStreamLinkAsync(type, itemId, fileId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The .strm link still works: it redirects to TorBox when opened.
            _log.LogWarning("Phoenicx TorBox: fresh link failed, client will use the play link: {Msg}", ex.Message);
        }
    }
}
