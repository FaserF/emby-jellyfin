using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.ScheduledTasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Controllers;

[ApiController]
[Route("api/embyPlugin")]
public class EmbyPluginController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<EmbyPluginController> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public EmbyPluginController(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        ILogger<EmbyPluginController> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private EmbyApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());

    [HttpGet("Script")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("embyPlugin.js", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            return NotFound("// embyPlugin.js resource not found");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound("// embyPlugin.js stream null");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Content(reader.ReadToEnd(), "application/javascript; charset=utf-8");
    }

    [HttpPost("auth/test")]
    [AllowAnonymous]
    public async Task<ActionResult> TestAndSaveConnection(
        [FromBody] EmbyAuthTestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ServerUrl) || string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { ok = false, error = "Server URL and Username are required." });
        }

        using var client = CreateClient();
        var auth = await client.AuthenticateByNameAsync(request.ServerUrl, request.Username, request.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (auth == null || string.IsNullOrWhiteSpace(auth.AccessToken))
        {
            return StatusCode(StatusCodes.Status401Unauthorized, new { ok = false, error = "Emby authentication failed. Please check URL, username, and password." });
        }

        var config = Config;
        if (!string.IsNullOrWhiteSpace(request.JellyfinUser))
        {
            config.UserAccounts ??= new List<EmbyUserAccountEntry>();
            var existing = config.UserAccounts.FirstOrDefault(u =>
                string.Equals(u.JellyfinUser, request.JellyfinUser, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new EmbyUserAccountEntry { JellyfinUser = request.JellyfinUser };
                config.UserAccounts.Add(existing);
            }

            existing.JellyfinUserId = request.JellyfinUserId ?? existing.JellyfinUserId;
            existing.EmbyServerUrl = request.ServerUrl.Trim().TrimEnd('/');
            existing.EmbyUsername = request.Username.Trim();
            existing.EmbyPassword = request.Password ?? string.Empty;
            existing.EmbyUserId = auth.User?.Id ?? string.Empty;
            existing.AccessToken = auth.AccessToken;
            existing.ServerId = auth.ServerId;
        }
        else
        {
            config.EmbyServerUrl = request.ServerUrl.Trim().TrimEnd('/');
            config.EmbyUsername = request.Username.Trim();
            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                config.EmbyPassword = request.Password;
            }

            config.EmbyApiKey = auth.AccessToken;
            config.EmbyUserId = auth.User?.Id ?? string.Empty;
            config.EmbyServerId = auth.ServerId;
        }

        Plugin.Instance?.SaveConfiguration();

        // High-speed probe: single combined query requesting only Id, avoiding heavy table joins (Chapters, Streams, People)
        var sample = await client.GetItemsAsync("Movie,Series", limit: 1, fields: "Id", cancellationToken: cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            ok = true,
            serverUrl = request.ServerUrl.Trim().TrimEnd('/'),
            embyUser = auth.User?.Name ?? request.Username,
            embyUserId = auth.User?.Id,
            serverId = auth.ServerId,
            canDownload = auth.User?.Policy?.EnableContentDownloading ?? true,
            canPlay = auth.User?.Policy?.EnableMediaPlayback ?? true,
            sampleReachable = sample.Count > 0
        });
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult> GetFederationStatus(CancellationToken cancellationToken)
    {
        var config = Config;
        using var client = CreateClient();
        var (baseUrl, token, uid, srvId) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);

        var moviesDir = Path.Combine(config.StrmLibraryPath ?? "/data/emby", "movies");
        var tvDir = Path.Combine(config.StrmLibraryPath ?? "/data/emby", "tv");
        var syncedMovies = Directory.Exists(moviesDir) ? Directory.GetDirectories(moviesDir).Length : 0;
        var syncedSeries = Directory.Exists(tvDir) ? Directory.GetDirectories(tvDir).Length : 0;

        return Ok(new
        {
            ok = !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(token),
            serverUrl = baseUrl,
            embyUser = config.EmbyUsername,
            embyUserId = uid,
            serverId = srvId,
            syncedMovies,
            syncedSeries,
            strmLibraryPath = config.StrmLibraryPath,
            seerrGuardEnabled = config.EnableSeerrRequestGuard,
            userMappingsCount = config.UserAccounts?.Count ?? 0
        });
    }

    [HttpPost("sync")]
    [AllowAnonymous]
    public async Task<ActionResult> TriggerSyncNow(CancellationToken cancellationToken)
    {
        var config = Config;
        using var client = CreateClient();
        var changes = await SyncEmbyLibraryTask.PerformSyncAsync(client, config, _logger, null, cancellationToken).ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();
        return Ok(new { ok = true, changes });
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<ActionResult> SearchEmbyCatalog(
        [FromQuery] string q,
        [FromQuery] string? type = null,
        [FromQuery] int limit = 24,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(new { items = Array.Empty<object>() });
        }

        using var client = CreateClient();
        var (baseUrl, token, _, srvId) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return Ok(new { items = Array.Empty<object>(), error = "Emby server not configured" });
        }

        var itemTypes = string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase)
            ? "Movie"
            : string.Equals(type, "series", StringComparison.OrdinalIgnoreCase)
                ? "Series"
                : "Movie,Series,Episode";

        var results = await client.GetItemsAsync(itemTypes, searchTerm: q.Trim(), limit: Math.Clamp(limit, 1, 60), cancellationToken: cancellationToken).ConfigureAwait(false);
        var mapped = results.Select(it => new
        {
            id = it.Id,
            name = it.Name,
            seriesName = it.SeriesName,
            type = it.Type,
            year = it.ProductionYear,
            overview = it.Overview,
            rating = it.CommunityRating,
            tmdbId = it.GetProviderId("Tmdb"),
            tvdbId = it.GetProviderId("Tvdb"),
            imdbId = it.GetProviderId("Imdb"),
            imageUrl = client.GetPrimaryImageUrl(baseUrl, it.Id, token, 400),
            embyWebUrl = $"{baseUrl}/web/index.html#!/item?id={it.Id}&serverId={(!string.IsNullOrWhiteSpace(it.ServerId) ? it.ServerId : srvId)}",
            streamUrl = client.GetDirectStreamUrl(baseUrl, it.Id, token)
        });

        return Ok(new { items = mapped });
    }

    [HttpGet("check-availability")]
    [AllowAnonymous]
    public async Task<ActionResult> CheckAvailability(
        [FromQuery] string mediaType = "movie",
        [FromQuery] string? tmdbId = null,
        [FromQuery] string? tvdbId = null,
        [FromQuery] string? imdbId = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var match = await client.CheckAvailabilityByExternalIdAsync(mediaType, tmdbId, tvdbId, imdbId, cancellationToken).ConfigureAwait(false);
        return Ok(match);
    }

    /// <summary>
    /// Universal Seerr / Jellyseerr / Overseerr / Suggestarr Webhook Guard.
    /// Point Jellyseerr's Webhook URL directly to: http://jellyfin:8096/api/embyPlugin/webhook/seerr
    /// If the requested Movie or TV Series is already available on the federated Emby server,
    /// the request is automatically declined/removed in Seerr and Sonarr/Radarr, with reason &amp; link to Emby!
    /// </summary>
    [HttpPost("webhook/seerr")]
    [AllowAnonymous]
    public async Task<ActionResult> HandleSeerrWebhook(CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return BadRequest(new { ok = false, error = "Empty payload" });
            }
    
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
    
            var notifType = root.TryGetProperty("notification_type", out var nt) ? nt.GetString() ?? string.Empty
                : root.TryGetProperty("event", out var ev) ? ev.GetString() ?? string.Empty
                : string.Empty;
    
            if (string.Equals(notifType, "TEST_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(new { ok = true, message = "Emby Federation Seerr Webhook Guard active" });
            }
    
            if (!Config.EnableSeerrRequestGuard)
            {
                return Ok(new { ok = true, ignored = "seerr_guard_disabled" });
            }
    
            if (notifType is not ("MEDIA_PENDING" or "MEDIA_AUTO_APPROVED" or "MEDIA_APPROVED"))
            {
                return Ok(new { ok = true, ignored = notifType });
            }
    
            string mediaType = "movie";
            string? tmdbId = null;
            string? tvdbId = null;
            string? requestId = null;
    
            if (root.TryGetProperty("media", out var mediaEl) && mediaEl.ValueKind == JsonValueKind.Object)
            {
                if (mediaEl.TryGetProperty("media_type", out var mt))
                {
                    mediaType = mt.GetString() ?? "movie";
                }
    
                if (mediaEl.TryGetProperty("tmdbId", out var tmdb))
                {
                    tmdbId = tmdb.ToString();
                }
    
                if (mediaEl.TryGetProperty("tvdbId", out var tvdb))
                {
                    tvdbId = tvdb.ToString();
                }
            }
    
            if (root.TryGetProperty("request", out var reqEl) && reqEl.ValueKind == JsonValueKind.Object &&
                reqEl.TryGetProperty("request_id", out var rid))
            {
                requestId = rid.ToString();
            }
    
            if (string.IsNullOrWhiteSpace(tmdbId) && string.IsNullOrWhiteSpace(tvdbId))
            {
                return Ok(new { ok = true, skipped = "no_external_ids" });
            }
    
            using var client = CreateClient();
            var match = await client.CheckAvailabilityByExternalIdAsync(mediaType, tmdbId, tvdbId, null, cancellationToken).ConfigureAwait(false);
            if (!match.Available)
            {
                return Ok(new { ok = true, availableOnEmby = false });
            }
    
            _logger.LogWarning(
                "Intercepting Seerr request {RequestId} for '{Title}' ({MediaType} tmdb={TmdbId}) - already available on Emby: {EmbyUrl}",
                requestId,
                match.Title,
                mediaType,
                tmdbId,
                match.EmbyWebUrl);
    
            var config = Config;
            if (!string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(config.SeerrUrl) && !string.IsNullOrWhiteSpace(config.SeerrApiKey))
            {
                try
                {
                    var delUrl = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/request/{Uri.EscapeDataString(requestId)}";
                    using var delReq = new HttpRequestMessage(HttpMethod.Delete, delUrl);
                    delReq.Headers.TryAddWithoutValidation("X-Api-Key", config.SeerrApiKey.Trim());
                    await client.HttpClient.SendAsync(delReq, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not cancel Seerr request {RequestId}", requestId);
                }
            }
    
            await CleanupArrEntryIfConfiguredAsync(client.HttpClient, config, mediaType, tmdbId, tvdbId, cancellationToken).ConfigureAwait(false);
    
            return Ok(new
            {
                ok = true,
                blocked = true,
                availableOnEmby = true,
                reason = $"Already available on Emby ({match.Title}): {match.EmbyWebUrl}",
                title = match.Title,
                embyItemId = match.EmbyItemId,
                embyWebUrl = match.EmbyWebUrl,
                directStreamUrl = match.DirectStreamUrl
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Seerr webhook");
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    private async Task CleanupArrEntryIfConfiguredAsync(
        HttpClient http,
        PluginConfiguration config,
        string mediaType,
        string? tmdbId,
        string? tvdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(tvdbId) &&
                !string.IsNullOrWhiteSpace(config.SonarrUrl) &&
                !string.IsNullOrWhiteSpace(config.SonarrApiKey))
            {
                var sUrl = $"{config.SonarrUrl.TrimEnd('/')}/api/v3/series?tvdbId={Uri.EscapeDataString(tvdbId)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, sUrl);
                req.Headers.TryAddWithoutValidation("X-Api-Key", config.SonarrApiKey.Trim());
                using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.TryGetProperty("id", out var idProp))
                            {
                                var delUrl = $"{config.SonarrUrl.TrimEnd('/')}/api/v3/series/{idProp}?deleteFiles=false";
                                using var delReq = new HttpRequestMessage(HttpMethod.Delete, delUrl);
                                delReq.Headers.TryAddWithoutValidation("X-Api-Key", config.SonarrApiKey.Trim());
                                await http.SendAsync(delReq, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            else if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(tmdbId) &&
                     !string.IsNullOrWhiteSpace(config.RadarrUrl) &&
                     !string.IsNullOrWhiteSpace(config.RadarrApiKey))
            {
                var rUrl = $"{config.RadarrUrl.TrimEnd('/')}/api/v3/movie?tmdbId={Uri.EscapeDataString(tmdbId)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, rUrl);
                req.Headers.TryAddWithoutValidation("X-Api-Key", config.RadarrApiKey.Trim());
                using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.TryGetProperty("id", out var idProp))
                            {
                                var delUrl = $"{config.RadarrUrl.TrimEnd('/')}/api/v3/movie/{idProp}?deleteFiles=false";
                                using var delReq = new HttpRequestMessage(HttpMethod.Delete, delUrl);
                                delReq.Headers.TryAddWithoutValidation("X-Api-Key", config.RadarrApiKey.Trim());
                                await http.SendAsync(delReq, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clean up Arr entry after Emby interception");
        }
    }

    [HttpGet("stream/{embyItemId}")]
    [HttpGet("stream/{embyItemId}/{filename}")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamEmbyItem(
        [FromRoute] string embyItemId,
        [FromRoute] string? filename = null,
        [FromQuery] bool proxy = false,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var (baseUrl, token, _, _) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return NotFound("Emby server not configured");
        }

        var targetUrl = client.GetDirectStreamUrl(baseUrl, embyItemId, token);
        if (!proxy && Config.UseDirectEmbyStreamUrls)
        {
            return Redirect(targetUrl);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        if (Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            req.Headers.TryAddWithoutValidation("Range", rangeHeader.ToArray());
        }

        using var resp = await client.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        Response.StatusCode = (int)resp.StatusCode;

        if (resp.Content.Headers.ContentRange != null)
        {
            Response.Headers["Content-Range"] = resp.Content.Headers.ContentRange.ToString();
        }

        if (resp.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = resp.Content.Headers.ContentLength.Value;
        }

        Response.Headers["Accept-Ranges"] = "bytes";
        Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "video/x-matroska";

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.CopyToAsync(Response.Body, 65536, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    [HttpPost("seerr/test")]
    [AllowAnonymous]
    public async Task<ActionResult> TestSeerrConnection(
        [FromBody] SeerrTestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SeerrUrl))
        {
            return BadRequest(new { ok = false, error = "Seerr URL is required." });
        }

        using var client = CreateClient();
        var seerrOk = false;
        var seerrDetails = string.Empty;

        try
        {
            var sUrl = $"{request.SeerrUrl.Trim().TrimEnd('/')}/api/v1/status";
            using var sReq = new HttpRequestMessage(HttpMethod.Get, sUrl);
            if (!string.IsNullOrWhiteSpace(request.SeerrApiKey))
            {
                sReq.Headers.TryAddWithoutValidation("X-Api-Key", request.SeerrApiKey.Trim());
            }
            using var sResp = await client.HttpClient.SendAsync(sReq, cancellationToken).ConfigureAwait(false);
            if (sResp.IsSuccessStatusCode)
            {
                seerrOk = true;
                var json = await sResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "online";
                seerrDetails = $"Connected (v{version})";
            }
            else if (sResp.StatusCode == System.Net.HttpStatusCode.Unauthorized || sResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                seerrDetails = $"Unauthorized (HTTP {(int)sResp.StatusCode}) - Check API Key";
            }
            else
            {
                seerrDetails = $"HTTP {(int)sResp.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            seerrDetails = $"Unreachable: {ex.Message}";
        }

        string? sonarrDetails = null;
        if (!string.IsNullOrWhiteSpace(request.SonarrUrl))
        {
            try
            {
                var sonUrl = $"{request.SonarrUrl.Trim().TrimEnd('/')}/api/v3/system/status";
                using var req = new HttpRequestMessage(HttpMethod.Get, sonUrl);
                if (!string.IsNullOrWhiteSpace(request.SonarrApiKey))
                {
                    req.Headers.TryAddWithoutValidation("X-Api-Key", request.SonarrApiKey.Trim());
                }
                using var resp = await client.HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                sonarrDetails = resp.IsSuccessStatusCode ? "Connected" : $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                sonarrDetails = $"Unreachable ({ex.Message})";
            }
        }

        string? radarrDetails = null;
        if (!string.IsNullOrWhiteSpace(request.RadarrUrl))
        {
            try
            {
                var radUrl = $"{request.RadarrUrl.Trim().TrimEnd('/')}/api/v3/system/status";
                using var req = new HttpRequestMessage(HttpMethod.Get, radUrl);
                if (!string.IsNullOrWhiteSpace(request.RadarrApiKey))
                {
                    req.Headers.TryAddWithoutValidation("X-Api-Key", request.RadarrApiKey.Trim());
                }
                using var resp = await client.HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                radarrDetails = resp.IsSuccessStatusCode ? "Connected" : $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                radarrDetails = $"Unreachable ({ex.Message})";
            }
        }

        return Ok(new
        {
            ok = seerrOk,
            seerr = seerrDetails,
            sonarr = sonarrDetails,
            radarr = radarrDetails
        });
    }
}

public class SeerrTestRequest
{
    public string? SeerrUrl { get; set; }
    public string? SeerrApiKey { get; set; }
    public string? SonarrUrl { get; set; }
    public string? SonarrApiKey { get; set; }
    public string? RadarrUrl { get; set; }
    public string? RadarrApiKey { get; set; }
}

public class EmbyAuthTestRequest
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? JellyfinUser { get; set; }
    public string? JellyfinUserId { get; set; }
}
