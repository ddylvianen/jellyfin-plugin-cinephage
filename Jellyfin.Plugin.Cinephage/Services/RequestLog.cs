using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cinephage.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cinephage.Services;

/// <summary>
/// Remembers which Jellyfin user requested which movie. Cinephage only knows the
/// API-key owner as requester, so this log is the only place the real requester is kept.
/// Stored as JSON in the plugin's data folder.
/// </summary>
public sealed class RequestLog : IDisposable
{
    private const int MaxEntries = 5000;

    private readonly ILogger<RequestLog> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<RequestLogEntry>? _entries;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestLog"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public RequestLog(ILogger<RequestLog> logger)
    {
        _logger = logger;
    }

    private static string FilePath =>
        Path.Combine(Plugin.Instance?.DataFolderPath ?? throw new CinephageException("Plugin is not initialized."), "requests.json");

    /// <summary>
    /// Adds an entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>A task.</returns>
    public async Task AddAsync(RequestLogEntry entry)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync().ConfigureAwait(false);
            entries.Add(entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(0, entries.Count - MaxEntries);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(entries)).ConfigureAwait(false);
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets all entries, newest first.
    /// </summary>
    /// <returns>The entries.</returns>
    public async Task<IReadOnlyList<RequestLogEntry>> GetAllAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return (await LoadAsync().ConfigureAwait(false)).OrderByDescending(e => e.RequestedAt).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    private async Task<List<RequestLogEntry>> LoadAsync()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
                _entries = JsonSerializer.Deserialize<List<RequestLogEntry>>(json) ?? [];
                return _entries;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Could not read {Path}; starting a new request log", FilePath);
        }

        _entries = [];
        return _entries;
    }
}
