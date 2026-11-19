using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core.Translations;

/// <summary>
/// Owns the GeoIP country database: loads it, keeps it fresh and answers lookups.
/// Loading and downloading run on the thread pool only; this class never touches Server.*, Utilities.*
/// or entities. <see cref="LookupCountry"/> is an in-memory tree walk (no I/O), safe on the game thread.
/// Any failure is logged once and leaves the previous database in use; with no database at all, lookups
/// return null and players simply keep the server language.
/// </summary>
internal sealed class GeoIpDatabase : IDisposable
{
    internal const long MaxDownloadBytes = 50L * 1024 * 1024;
    internal const long MinDatabaseBytes = 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly Lazy<HttpClient> SharedClient = new(CreateHttpClient);

    private readonly ILogger _logger;
    private readonly Func<string> _databasePath;
    private readonly Func<IReadOnlyList<string>> _downloadUrls;
    private readonly Func<int> _maxAgeDays;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _timer;
    private volatile MaxMindCountryReader? _reader;
    private string? _lastFailure;

    public GeoIpDatabase(ILogger logger, Func<string> databasePath, Func<IReadOnlyList<string>> downloadUrls,
        Func<int> maxAgeDays, Func<HttpClient>? httpClientFactory = null)
    {
        _logger = logger;
        _databasePath = databasePath;
        _downloadUrls = downloadUrls;
        _maxAgeDays = maxAgeDays;
        _httpClientFactory = httpClientFactory ?? (() => SharedClient.Value);
    }

    public bool IsLoaded => _reader != null;

    /// <summary>Loads the file (if any) and downloads when missing or stale, then checks once a day.</summary>
    public void Start()
    {
        _ = Task.Run(() => RefreshAsync(loadExisting: true));
        _timer = new System.Threading.Timer(_ => _ = Task.Run(() => RefreshAsync(loadExisting: false)), null, CheckInterval, CheckInterval);
    }

    public string? LookupCountry(IPAddress address)
    {
        var reader = _reader;
        if (reader == null) return null;

        try
        {
            return reader.LookupCountry(address);
        }
        catch (Exception ex)
        {
            LogFailureOnce($"GeoIP lookup failed: {ex.Message}");
            return null;
        }
    }

    internal async Task RefreshAsync(bool loadExisting)
    {
        if (_cts.IsCancellationRequested) return;
        if (!await _updateLock.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            var path = _databasePath();

            if (loadExisting && _reader == null && File.Exists(path))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, _cts.Token).ConfigureAwait(false);
                    _reader = Validate(bytes);
                    _logger.LogInformation("GeoIP country database loaded ({Type}, built {Built:yyyy-MM-dd})",
                        _reader.DatabaseType, _reader.BuildTime);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailureOnce($"GeoIP database {path} is unusable ({ex.Message}); downloading a new one");
                }
            }

            var maxAge = _maxAgeDays();
            var stale = _reader == null ||
                        (maxAge > 0 && File.Exists(path) &&
                         DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(maxAge));
            if (!stale) return;

            await DownloadAsync(path).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailureOnce($"GeoIP update failed: {ex.Message}");
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task DownloadAsync(string path)
    {
        var urls = _downloadUrls() ?? Array.Empty<string>();
        if (urls.Count == 0)
        {
            if (_reader == null) LogFailureOnce($"GeoIP language guess is off: no database at {path} and no DownloadUrls in geoip.json");
            return;
        }

        var errors = new List<string>();
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;

            try
            {
                var bytes = await FetchAsync(url).ConfigureAwait(false);
                var reader = Validate(bytes);

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, _cts.Token).ConfigureAwait(false);
                File.Move(tmp, path, true);

                // The reader owns its bytes, so lookups still running on the old one finish safely;
                // the GC frees it once nothing references it.
                _reader = reader;
                _lastFailure = null;
                _logger.LogInformation("GeoIP country database updated from {Url} ({Type}, built {Built:yyyy-MM-dd}, {Size} bytes)",
                    url, reader.DatabaseType, reader.BuildTime, bytes.Length);
                return;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{url}: {ex.Message}");
            }
            finally
            {
                TryDelete(path + ".tmp");
            }
        }

        LogFailureOnce(_reader == null
            ? $"GeoIP language guess is off: could not download a country database ({string.Join("; ", errors)})"
            : $"GeoIP database update failed, keeping the current one ({string.Join("; ", errors)})");
    }

    private async Task<byte[]> FetchAsync(string url)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(DownloadTimeout);

        var client = _httpClientFactory();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidDataException($"file is larger than {MaxDownloadBytes} bytes");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxDownloadBytes)
                throw new InvalidDataException($"file is larger than {MaxDownloadBytes} bytes");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Throws unless <paramref name="bytes"/> is a usable country database.</summary>
    internal static MaxMindCountryReader Validate(byte[] bytes)
    {
        if (bytes.Length < MinDatabaseBytes)
            throw new InvalidDataException($"file is only {bytes.Length} bytes");

        var reader = MaxMindCountryReader.FromBytes(bytes);
        if (!reader.DatabaseType.Contains("Country", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"database type is \"{reader.DatabaseType}\", not a country database");

        var probe = reader.LookupCountry(IPAddress.Parse("8.8.8.8"));
        if (probe != "US")
            throw new InvalidDataException($"8.8.8.8 resolved to \"{probe ?? "nothing"}\", expected \"US\"");

        return reader;
    }

    private void LogFailureOnce(string message)
    {
        if (message == _lastFailure) return;
        _lastFailure = message;
        _logger.LogWarning("{Message}", message);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Overwritten by the next attempt.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CounterStrikeSharp");
        return client;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _timer = null;
    }
}
