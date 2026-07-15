using System.Text.Json;
using CcusageDesktop.Models;

namespace CcusageDesktop.Services;

/// <summary>
/// Cache-first access to ccusage reports. ccusage scans every agent's transcript logs,
/// which can take minutes on a large history — so the last good JSON for each command is
/// persisted to disk and served instantly on launch while a background refresh runs.
/// </summary>
public sealed class UsageDataService
{
    static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ccusage-desktop", "cache");

    static string CachePath(string command) => Path.Combine(CacheDir, command.Replace(' ', '-') + ".json");

    public sealed record Cached<T>(T Data, DateTimeOffset FetchedAt);

    public Cached<T>? LoadCached<T>(string command)
    {
        try
        {
            var path = CachePath(command);
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<T>(File.ReadAllText(path), UsageJson.Options);
            return data is null ? null : new(data, File.GetLastWriteTime(path));
        }
        catch
        {
            return null; // corrupt cache — treat as absent, next refresh rewrites it
        }
    }

    public async Task<Cached<T>> FetchAsync<T>(string command, CancellationToken ct = default)
    {
        var json = await CcusageCli.RunAsync(command, ct);
        var data = JsonSerializer.Deserialize<T>(json, UsageJson.Options)
            ?? throw new InvalidOperationException($"ccusage {command} returned empty JSON.");

        try
        {
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(CachePath(command), json, ct);
        }
        catch
        {
            // Caching is best-effort; stale/missing cache only costs startup speed.
        }

        return new(data, DateTimeOffset.Now);
    }
}
