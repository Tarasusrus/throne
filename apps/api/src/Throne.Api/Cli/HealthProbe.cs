using System.Text.Json;

namespace Throne.Api.Cli;

/// <summary>
/// Talks to a running instance over its own HTTP surface: <c>/health</c> to know
/// when a freshly spawned daemon is actually serving (so the browser opens only
/// when the UI is ready), and <c>/version</c> so <c>status</c> reports the version
/// and manifest fingerprint the live process is running rather than the
/// launcher's own build.
/// </summary>
public sealed record VersionInfo(string Version, string? ManifestHash);

internal static class HealthProbe
{
    public static async Task<bool> WaitHealthyAsync(
        string url,
        TimeSpan timeout,
        Func<bool> processAlive,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!processAlive())
            {
                return false;
            }

            if (await PingAsync(http, $"{url}/health", ct))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }

        return false;
    }

    public static async Task<VersionInfo?> TryGetVersionAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            await using var stream = await http.GetStreamAsync($"{url}/version", ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (version is null)
            {
                return null;
            }

            var manifestHash = doc.RootElement.TryGetProperty("manifest_hash", out var m) ? m.GetString() : null;
            return new VersionInfo(version, manifestHash);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> PingAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
