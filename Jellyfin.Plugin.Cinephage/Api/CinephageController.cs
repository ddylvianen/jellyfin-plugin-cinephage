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
    /// Searches movies in Cinephage (TMDB).
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
            return Ok(Array.Empty<MovieResult>());
        }

        try
        {
            return Ok(await _client.SearchMoviesAsync(query.Trim(), cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cinephage search failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Cinephage is niet bereikbaar of niet goed ingesteld." });
        }
    }

    /// <summary>
    /// Requests a movie in Cinephage and records which Jellyfin user asked for it.
    /// </summary>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Request")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestMovie([FromBody] MovieRequestBody body, CancellationToken cancellationToken)
    {
        var claim = User.Claims.FirstOrDefault(c => c.Type.Equals(UserIdClaim, StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            return Unauthorized();
        }

        if (body.TmdbId <= 0)
        {
            return BadRequest(new RequestOutcome { Message = "Ongeldige film." });
        }

        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? userId.ToString("N");
        RequestOutcome outcome;
        try
        {
            outcome = await _client.RequestMovieAsync(body.TmdbId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cinephage request failed");
            return Ok(new RequestOutcome { Success = false, Code = "error", Message = "Aanvragen mislukt: Cinephage is niet bereikbaar of niet goed ingesteld." });
        }

        _logger.LogInformation("Cinephage request for TMDB {TmdbId} by {User}: {Result}", body.TmdbId, username, outcome.Success ? "ok" : outcome.Code);

        // Also log "already requested", so you can see that more people wanted it.
        if (outcome.Success || outcome.Code == "duplicate_request")
        {
            await _requestLog.AddAsync(new RequestLogEntry
            {
                RequestedAt = DateTime.UtcNow,
                UserId = userId,
                Username = username,
                TmdbId = body.TmdbId,
                Title = outcome.Title ?? body.Title ?? $"TMDB {body.TmdbId}",
                Year = outcome.Year ?? body.Year,
                CinephageRequestId = outcome.CinephageRequestId,
                Status = outcome.Success ? outcome.RequestStatus : "duplicate"
            }).ConfigureAwait(false);
        }

        return Ok(outcome);
    }

    /// <summary>
    /// Lists who requested what, with the current Cinephage status (admins only).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The log entries, newest first.</returns>
    [HttpGet("Requests")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Requests(CancellationToken cancellationToken)
    {
        var entries = await _requestLog.GetAllAsync().ConfigureAwait(false);
        var statusAvailable = true;
        try
        {
            var statuses = await _client.GetRequestStatusesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.CinephageRequestId is not null && statuses.TryGetValue(entry.CinephageRequestId, out var status))
                {
                    entry.Status = status;
                }
            }
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not refresh request statuses from Cinephage");
            statusAvailable = false;
        }

        return Ok(new { StatusAvailable = statusAvailable, Requests = entries });
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
