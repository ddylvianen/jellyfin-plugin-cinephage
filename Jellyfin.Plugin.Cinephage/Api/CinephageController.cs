using System;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cinephage.Models;
using Jellyfin.Plugin.Cinephage.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cinephage.Api;

/// <summary>
/// Endpoints used by the injected web script and the dashboard pages.
/// </summary>
[ApiController]
[Route("Cinephage")]
public class CinephageController : ControllerBase
{
    // Jellyfin.Api's GetUserId() extension is not on NuGet, so read its claim directly.
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly CinephageClient _client;
    private readonly RequestLog _requestLog;
    private readonly ILogger<CinephageController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CinephageController"/> class.
    /// </summary>
    /// <param name="client">The Cinephage client.</param>
    /// <param name="requestLog">The request log.</param>
    /// <param name="logger">The logger.</param>
    public CinephageController(CinephageClient client, RequestLog requestLog, ILogger<CinephageController> logger)
    {
        _client = client;
        _requestLog = requestLog;
        _logger = logger;
    }

    /// <summary>
    /// Searches movies and series in Cinephage (TMDB).
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The results.</returns>
    [HttpGet("Search")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return Ok(Array.Empty<MediaResult>());
        }

        try
        {
            return Ok(await _client.SearchAsync(query.Trim(), cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cinephage search failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Cinephage is niet bereikbaar of niet goed ingesteld." });
        }
    }

    /// <summary>
    /// Lists the seasons of a series with their request/library state.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The seasons.</returns>
    [HttpGet("Series/{tmdbId:int}/Seasons")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Seasons([FromRoute] int tmdbId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _client.GetSeasonsAsync(tmdbId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cinephage seasons lookup failed for TMDB {TmdbId}", tmdbId);
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Seizoenen ophalen mislukt." });
        }
    }

    /// <summary>
    /// Requests a movie or series seasons in Cinephage and records which Jellyfin user asked for it.
    /// </summary>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Request")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestMedia([FromBody] MediaRequestBody body, CancellationToken cancellationToken)
    {
        var claim = User.Claims.FirstOrDefault(c => c.Type.Equals(UserIdClaim, StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            return Unauthorized();
        }

        var mediaType = string.Equals(body.MediaType, "series", StringComparison.OrdinalIgnoreCase) ? "series" : "movie";
        var seasons = body.Seasons?.Where(s => s > 0).Distinct().OrderBy(s => s).ToList();
        if (body.TmdbId <= 0)
        {
            return BadRequest(new RequestOutcome { Message = "Ongeldige titel." });
        }

        if (mediaType == "series" && (seasons is null || seasons.Count == 0))
        {
            return Ok(new RequestOutcome { Success = false, Code = "invalid_scope", Message = "Kies minstens één seizoen." });
        }

        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? userId.ToString("N");
        RequestOutcome outcome;
        try
        {
            outcome = await _client.RequestAsync(mediaType, body.TmdbId, seasons, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cinephage request failed");
            return Ok(new RequestOutcome { Success = false, Code = "error", Message = "Aanvragen mislukt: Cinephage is niet bereikbaar of niet goed ingesteld." });
        }

        _logger.LogInformation("Cinephage request for {Type} TMDB {TmdbId} by {User}: {Result}", mediaType, body.TmdbId, username, outcome.Success ? "ok" : outcome.Code);

        // Also log "already requested", so you can see that more people wanted it.
        if (outcome.Success || outcome.Code == "duplicate_request")
        {
            await _requestLog.AddAsync(new RequestLogEntry
            {
                RequestedAt = DateTime.UtcNow,
                UserId = userId,
                Username = username,
                MediaType = mediaType,
                TmdbId = body.TmdbId,
                Title = outcome.Title ?? body.Title ?? $"TMDB {body.TmdbId}",
                Year = outcome.Year ?? body.Year,
                Seasons = mediaType == "series" ? seasons : null,
                CinephageRequestId = outcome.CinephageRequestId,
                Status = outcome.Success ? outcome.RequestStatus : "duplicate"
            }).ConfigureAwait(false);
        }

        return Ok(outcome);
    }

    /// <summary>
    /// Lists who requested what (the audit log, unchanged) with each title's current state in Cinephage (admins only).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The log entries, newest first.</returns>
    [HttpGet("Requests")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Requests(CancellationToken cancellationToken)
    {
        var entries = await _requestLog.GetAllAsync().ConfigureAwait(false);
        try
        {
            var views = await _client.GetCurrentStatesAsync(entries, cancellationToken).ConfigureAwait(false);
            return Ok(new { StatusAvailable = true, Requests = views });
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not get current request states from Cinephage");
            return Ok(new { StatusAvailable = false, Requests = entries.Select(e => new RequestLogView { Entry = e }) });
        }
    }

    /// <summary>
    /// Tests the connection with the saved configuration (admins only).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A status message.</returns>
    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new { Success = true, Message = await _client.TestConnectionAsync(cancellationToken).ConfigureAwait(false) });
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return Ok(new { Success = false, ex.Message });
        }
    }

    /// <summary>
    /// Serves the web client script. Anonymous because a script tag sends no Jellyfin token.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("Script")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public IActionResult Script()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Cinephage.Web.cinephage-requests.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript; charset=utf-8");
    }
}
