using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
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
    private static readonly Regex ClientRx = new(@"Client=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TorBoxClient _torbox;
    private readonly ILibraryManager _library;
    private readonly ILogger<DirectPlayFilter> _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectPlayFilter"/> class.
    /// </summary>
    public DirectPlayFilter(TorBoxClient torbox, ILibraryManager library, ILogger<DirectPlayFilter> log)
    {
        _torbox = torbox;
        _library = library;
        _log = log;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Apps that play the server's own stream address (/Videos/{id}/stream?static=true):
        // answer with a redirect to TorBox, so the video still comes from TorBox, not through here.
        if (Plugin.Instance?.Configuration.DirectToClients == true)
        {
            try
            {
                if (await TryRedirectStreamAsync(context).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let the plugin stop a normal (non-TorBox) video from playing.
                _log.LogWarning(ex, "Torvia: stream check failed, Jellyfin plays it normally");
            }
        }

        var executed = await next().ConfigureAwait(false);

        // "Download" in an app: Jellyfin would hand out the tiny .strm file. Send the real
        // video from TorBox instead (Jellyfin has already checked the user may download).
        if (executed.Result is PhysicalFileResult { FileName: { } strmFile }
            && strmFile.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = (await File.ReadAllTextAsync(strmFile).ConfigureAwait(false)).Trim();
                if (PlayLinks.TryParse(text, out var dt, out var di, out var df))
                {
                    var url = await _torbox.GetStreamLinkAsync(dt, di, df, context.HttpContext.RequestAborted, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                    executed.Result = new RedirectResult(url, permanent: false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Torvia: download link failed: {Msg}", ex.Message);
            }

            return;
        }
        if (Plugin.Instance?.Configuration.DirectToClients != true || executed.Result is not ObjectResult { Value: { } value })
        {
            return;
        }

        try
        {
            var web = IsWebPlayer(context.HttpContext);
            switch (value)
            {
                case PlaybackInfoResponse info when info.MediaSources is { Count: > 0 }:
                    foreach (var ms in info.MediaSources)
                    {
                        if (web)
                        {
                            await PrepareAsync(ms, true, PublicBase(context.HttpContext), context.HttpContext.RequestAborted).ConfigureAwait(false);
                        }
                        else
                        {
                            PrepareForStreamUrl(ms);
                        }
                    }

                    break;
                case BaseItemDto dto:
                    await PrepareItemAsync(dto, web, PublicBase(context.HttpContext)).ConfigureAwait(false);
                    break;
                case QueryResult<BaseItemDto> list when list.Items is not null:
                    foreach (var dto in list.Items)
                    {
                        await PrepareItemAsync(dto, web, PublicBase(context.HttpContext)).ConfigureAwait(false);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Torvia: could not prepare direct play");
        }
    }

    private async Task PrepareItemAsync(BaseItemDto dto, bool web, string publicBase)
    {
        if (dto.MediaSources is null)
        {
            return;
        }

        foreach (var ms in dto.MediaSources)
        {
            if (web)
            {
                await PrepareAsync(ms, false, publicBase, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                PrepareForStreamUrl(ms);
            }
        }
    }

    /// <summary>
    /// Jellyfin web and Jellyfin Media Player / Desktop open a remote link straight away. Other
    /// apps (the Android app's player, Android TV, ...) treat a remote link as a live HLS stream
    /// and fail on a movie file; they get a normal "file" source and play the server's stream
    /// address, which this filter answers with a redirect to TorBox.
    /// </summary>
    private static bool IsWebPlayer(HttpContext http)
    {
        var auth = http.Request.Headers["Authorization"].ToString();
        if (auth.Length == 0)
        {
            auth = http.Request.Headers["X-Emby-Authorization"].ToString();
        }

        var m = ClientRx.Match(auth);
        var client = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : string.Empty;
        return client.Contains("Jellyfin Web", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Jellyfin Media Player", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Jellyfin Desktop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The address apps reach this server on: the address the app itself used for this request,
    /// so every server works without setting anything.
    /// </summary>
    private static string PublicBase(HttpContext http)
    {
        var r = http.Request;
        var proto = r.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
        var host = r.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
        var scheme = proto.Length > 0 ? proto : r.Scheme;
        var h = host.Length > 0 ? host : r.Host.Value;
        return $"{scheme}://{h}{r.PathBase.Value}".TrimEnd('/');
    }

    private static void PrepareForStreamUrl(MediaSourceInfo ms)
    {
        if (!PlayLinks.TryParse(ms.Path, out _, out _, out _))
        {
            return;
        }

        ms.Protocol = MediaProtocol.File;
        ms.IsRemote = false;
        ms.SupportsDirectPlay = true;
    }

    private async Task<bool> TryRedirectStreamAsync(ActionExecutingContext context)
    {
        var action = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        if (action is not ("GetVideoStream" or "GetVideoStreamByContainer"))
        {
            return false;
        }

        var q = context.HttpContext.Request.Query;
        if (!string.Equals(q["static"].ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A version of a movie has its own id, passed as mediaSourceId.
        if (!Guid.TryParse(q["mediaSourceId"].ToString(), out var id)
            && !Guid.TryParse(context.RouteData.Values["itemId"]?.ToString(), out id))
        {
            return false;
        }

        var item = _library.GetItemById(id);

        // Only TorBox .strm items; every other video is left completely to Jellyfin.
        if (item is null || item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var link = item.ShortcutPath;
        if (string.IsNullOrEmpty(link) && item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true && File.Exists(item.Path))
        {
            link = (await File.ReadAllTextAsync(item.Path).ConfigureAwait(false)).Trim();
        }

        if (!PlayLinks.TryParse(link, out var type, out var itemId, out var fileId))
        {
            return false;
        }

        try
        {
            var url = await _torbox.GetStreamLinkAsync(type, itemId, fileId, context.HttpContext.RequestAborted).ConfigureAwait(false);
            context.Result = new RedirectResult(url, permanent: false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Jellyfin then streams it the normal way (through the server).
            _log.LogWarning("Torvia: redirect to TorBox failed, streaming through the server: {Msg}", ex.Message);
            return false;
        }
    }


    private async Task PrepareAsync(MediaSourceInfo ms, bool freshLink, string publicBase, CancellationToken ct)
    {
        if (!PlayLinks.TryParse(ms.Path, out var type, out var itemId, out var fileId))
        {
            return;
        }

        ms.Protocol = MediaProtocol.Http;
        ms.IsRemote = true;
        ms.SupportsDirectPlay = true;

        // The .strm files hold the server's local address; apps get the public one.
        ms.Path = PlayLinks.ToPublic(ms.Path!, publicBase);

        if (!freshLink)
        {
            return;
        }

        try
        {
            // The app keeps this link for the whole movie (pauses included), so it gets a new one,
            // not one that has already been sitting in the cache for a while.
            ms.Path = await _torbox.GetStreamLinkAsync(type, itemId, fileId, ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The .strm link still works: it redirects to TorBox when opened.
            _log.LogWarning("Torvia: fresh link failed, client will use the play link: {Msg}", ex.Message);
        }
    }
}
