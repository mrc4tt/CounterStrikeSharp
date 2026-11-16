using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using CounterStrikeSharp.API.Core.Translations;
using Microsoft.Extensions.Logging.Abstractions;

namespace CounterStrikeSharp.API.Tests;

public class GeoIpLanguageTests
{
    // Builds a tiny IPv4 MaxMind DB (record size 24): `networks` (prefix, country) map to
    // { "country": { "iso_code": country, "names": { "en": ... } } }. `padTo` pads the data section so the
    // file passes GeoIpDatabase.Validate's minimum size.
    internal static byte[] BuildDatabase((string Prefix, string Country)[] networks, string databaseType = "GeoLite2-Country",
        int padTo = 0, (string Key, byte[] Value)? firstEntry = null)
    {
        // Search tree: node 0 is the root; nodes are appended as paths are inserted.
        var left = new List<int>();
        var right = new List<int>();
        var leaves = new Dictionary<(int Node, int Bit), int>(); // -> data index
        left.Add(-1);
        right.Add(-1);

        var data = new List<byte[]>();
        for (var n = 0; n < networks.Length; n++)
        {
            var parts = networks[n].Prefix.Split('/');
            var ip = IPAddress.Parse(parts[0]).GetAddressBytes();
            var length = int.Parse(parts[1]);

            var node = 0;
            for (var i = 0; i < length; i++)
            {
                var bit = (ip[i >> 3] >> (7 - (i & 7))) & 1;
                if (i == length - 1)
                {
                    leaves[(node, bit)] = n;
                    break;
                }

                var next = bit == 0 ? left[node] : right[node];
                if (next < 0)
                {
                    next = left.Count;
                    left.Add(-1);
                    right.Add(-1);
                    if (bit == 0) left[node] = next;
                    else right[node] = next;
                }
                node = next;
            }

            var country = ("country", Map(("iso_code", Str(networks[n].Country)),
                ("names", Map(("en", Str("Name " + networks[n].Country)), ("de", Str("Name"))))));
            data.Add(firstEntry is { } first ? Map(first, country) : Map(country));
        }

        var nodeCount = left.Count;
        var dataBytes = new List<byte>();
        var dataOffsets = new List<int>();
        foreach (var d in data)
        {
            dataOffsets.Add(dataBytes.Count);
            dataBytes.AddRange(d);
        }

        int Record(int node, int bit)
        {
            if (leaves.TryGetValue((node, bit), out var leaf)) return nodeCount + 16 + dataOffsets[leaf];
            var child = bit == 0 ? left[node] : right[node];
            return child < 0 ? nodeCount : child;
        }

        var file = new List<byte>();
        for (var node = 0; node < nodeCount; node++)
        {
            foreach (var bit in new[] { 0, 1 })
            {
                var r = Record(node, bit);
                file.Add((byte)(r >> 16));
                file.Add((byte)(r >> 8));
                file.Add((byte)r);
            }
        }

        file.AddRange(new byte[16]);
        file.AddRange(dataBytes);
        if (file.Count < padTo) file.AddRange(new byte[padTo - file.Count]);

        file.AddRange(new byte[] { 0xAB, 0xCD, 0xEF });
        file.AddRange(Encoding.ASCII.GetBytes("MaxMind.com"));
        file.AddRange(Map(
            ("node_count", UInt32((uint)nodeCount)),
            ("record_size", UInt16(24)),
            ("ip_version", UInt16(4)),
            ("database_type", Str(databaseType)),
            ("build_epoch", UInt64(1_700_000_000)),
            ("binary_format_major_version", UInt16(2))));

        return file.ToArray();
    }

    private static byte[] Control(int type, int size)
    {
        var bytes = new List<byte>();
        var typeBits = type <= 7 ? type : 0;
        if (size < 29) bytes.Add((byte)((typeBits << 5) | size));
        else
        {
            bytes.Add((byte)((typeBits << 5) | 29));
            bytes.Add((byte)(size - 29));
        }
        if (type > 7) bytes.Insert(1, (byte)(type - 7));
        return bytes.ToArray();
    }

    private static byte[] Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return Control(2, b.Length).Concat(b).ToArray();
    }

    private static byte[] UInt16(ushort v) => Control(5, 2).Concat(new[] { (byte)(v >> 8), (byte)v }).ToArray();

    private static byte[] UInt32(uint v) =>
        Control(6, 4).Concat(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }).ToArray();

    private static byte[] UInt64(ulong v) =>
        Control(9, 8).Concat(Enumerable.Range(0, 8).Select(i => (byte)(v >> (56 - 8 * i)))).ToArray();

    private static byte[] Map(params (string Key, byte[] Value)[] entries) =>
        Control(7, entries.Length).Concat(entries.SelectMany(e => Str(e.Key).Concat(e.Value))).ToArray();

    private static byte[] Array(params byte[][] items) =>
        Control(11, items.Length).Concat(items.SelectMany(i => i)).ToArray();

    [Fact]
    public void ReaderSkipsUnrelatedKeysAndStopsDeepNesting()
    {
        // Moderate nesting before "country" is skipped fine.
        var nested = Str("x");
        for (var i = 0; i < 10; i++) nested = Array(nested);
        var ok = MaxMindCountryReader.FromBytes(BuildDatabase(Networks, firstEntry: ("aaa", nested)));
        Assert.Equal("DK", ok.LookupCountry(IPAddress.Parse("80.1.2.3")));

        // Hostile nesting must fail as InvalidDataException, not overflow the stack (uncatchable, kills the server).
        var deep = Str("x");
        for (var i = 0; i < 1_000; i++) deep = Array(deep);
        var bad = MaxMindCountryReader.FromBytes(BuildDatabase(Networks, firstEntry: ("aaa", deep)));
        Assert.Throws<InvalidDataException>(() => bad.LookupCountry(IPAddress.Parse("80.1.2.3")));
    }

    [Fact]
    public void ReaderRejectsInflatedContainerSizes()
    {
        var file = BuildDatabase(Networks).ToList();
        // Metadata map header (type 7, 6 entries) -> declared size 0xFFFFFF via size code 31.
        var metaStart = IndexOfMarker(file);
        file[metaStart] = (7 << 5) | 31;
        file.InsertRange(metaStart + 1, new byte[] { 0xFF, 0xFF, 0xFF });
        Assert.Throws<InvalidDataException>(() => MaxMindCountryReader.FromBytes(file.ToArray()));
    }

    private static int IndexOfMarker(List<byte> file)
    {
        var marker = new byte[] { 0xAB, 0xCD, 0xEF }.Concat(Encoding.ASCII.GetBytes("MaxMind.com")).ToArray();
        for (var i = file.Count - marker.Length; i >= 0; i--)
        {
            if (file.Skip(i).Take(marker.Length).SequenceEqual(marker)) return i + marker.Length;
        }
        throw new InvalidOperationException("no marker");
    }

    [Theory]
    [InlineData("de", "de-DE", true)]
    [InlineData("de", "de", true)]
    [InlineData("da", "en", false)]
    [InlineData("pt-BR", "pt", false)]
    [InlineData("en", "en-US", true)]
    public void GuessCoveredByServerCultureIsSkipped(string guessed, string server, bool same)
    {
        Assert.Equal(same, PlayerLanguageManager.SameLanguage(CultureInfo.GetCultureInfo(guessed), CultureInfo.GetCultureInfo(server)));
    }

    [Fact]
    public void KnownCulturesIsCaseInsensitiveAndRejectsTypos()
    {
        Assert.Equal("pt-BR", KnownCultures.Find("pt-br")?.Name);
        Assert.Equal("da", KnownCultures.Find(" DA ")?.Name);
        Assert.Null(KnownCultures.Find("not-a-culture-xx"));
        Assert.Null(KnownCultures.Find(""));
    }

    private static readonly (string, string)[] Networks =
    {
        ("8.8.8.0/24", "US"),
        ("80.0.0.0/8", "DK"),
        ("90.90.0.0/16", "ZZ"),
    };

    [Fact]
    public void ReaderFindsCountry()
    {
        var reader = MaxMindCountryReader.FromBytes(BuildDatabase(Networks));

        Assert.Equal("GeoLite2-Country", reader.DatabaseType);
        Assert.Equal("US", reader.LookupCountry(IPAddress.Parse("8.8.8.8")));
        Assert.Equal("DK", reader.LookupCountry(IPAddress.Parse("80.1.2.3")));
        Assert.Equal("DK", reader.LookupCountry(IPAddress.Parse("::ffff:80.1.2.3")));
        Assert.Null(reader.LookupCountry(IPAddress.Parse("8.8.9.1")));
        Assert.Null(reader.LookupCountry(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void ReaderRejectsGarbage()
    {
        Assert.ThrowsAny<InvalidDataException>(() => MaxMindCountryReader.FromBytes(new byte[4096]));

        var truncated = BuildDatabase(Networks);
        Assert.ThrowsAny<InvalidDataException>(() => MaxMindCountryReader.FromBytes(truncated[..^20]));
    }

    [Fact]
    public void ValidateChecksSizeTypeAndProbe()
    {
        var good = BuildDatabase(Networks, padTo: (int)GeoIpDatabase.MinDatabaseBytes);
        Assert.Equal("US", GeoIpDatabase.Validate(good).LookupCountry(IPAddress.Parse("8.8.8.8")));

        Assert.Throws<InvalidDataException>(() => GeoIpDatabase.Validate(BuildDatabase(Networks)));
        Assert.Throws<InvalidDataException>(() =>
            GeoIpDatabase.Validate(BuildDatabase(Networks, "GeoLite2-ASN", (int)GeoIpDatabase.MinDatabaseBytes)));
        Assert.Throws<InvalidDataException>(() =>
            GeoIpDatabase.Validate(BuildDatabase(new[] { ("80.0.0.0/8", "DK") }, padTo: (int)GeoIpDatabase.MinDatabaseBytes)));
    }

    [Theory]
    [InlineData("80.1.2.3:27005", "80.1.2.3")]
    [InlineData("80.1.2.3", "80.1.2.3")]
    [InlineData("[2a00:1450::1]:27005", "2a00:1450::1")]
    [InlineData("2a00:1450::1", "2a00:1450::1")]
    [InlineData("127.0.0.1:27005", null)]
    [InlineData("10.1.2.3", null)]
    [InlineData("172.16.0.1", null)]
    [InlineData("172.32.0.1", "172.32.0.1")]
    [InlineData("192.168.1.10:27005", null)]
    [InlineData("100.64.1.1", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData("loopback", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("::1", null)]
    [InlineData("fd00::1", null)]
    public void ParsesOnlyPublicAddresses(string? input, string? expected)
    {
        Assert.Equal(expected, GeoIpLanguages.ParsePublicAddress(input)?.ToString());
    }

    [Fact]
    public void CountryMapMergesHostOverrides()
    {
        var map = GeoIpLanguages.BuildCountryMap(new Dictionary<string, List<string>>
        {
            ["fr"] = new() { "BE", "ch" },
            ["not-a-culture-xx"] = new() { "DK" },
        });

        Assert.Equal("da", map["DK"].Name);
        Assert.Equal("fr", map["BE"].Name);
        Assert.Equal("fr", map["CH"].Name);
        Assert.Equal("pt-BR", map["BR"].Name);
        Assert.False(map.ContainsKey("FI"));
    }

    [Fact]
    public async Task CorruptDownloadKeepsCurrentDatabase()
    {
        var dir = Path.Join(Path.GetTempPath(), "css-geoip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Join(dir, "GeoLite2-Country.mmdb");
            var good = BuildDatabase(Networks, padTo: (int)GeoIpDatabase.MinDatabaseBytes);
            File.WriteAllBytes(path, good);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30)); // stale: forces a download

            var served = good[..(good.Length / 2)]; // truncated download
            using var client = new HttpClient(new StubHandler(() => served));
            var db = new GeoIpDatabase(NullLogger.Instance, () => path, () => new[] { "http://test/a.mmdb" }, () => 7,
                () => client);

            await db.RefreshAsync(loadExisting: true);

            Assert.True(db.IsLoaded);
            Assert.Equal("DK", db.LookupCountry(IPAddress.Parse("80.1.2.3")));
            Assert.Equal(good, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));

            // A valid download replaces it.
            var updated = BuildDatabase(new[] { ("8.8.8.0/24", "US"), ("80.0.0.0/8", "SE") },
                padTo: (int)GeoIpDatabase.MinDatabaseBytes);
            served = updated;
            await db.RefreshAsync(loadExisting: false);
            Assert.Equal("SE", db.LookupCountry(IPAddress.Parse("80.1.2.3")));
            Assert.Equal(updated, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MissingDatabaseAndFailedDownloadLeavesGeoIpOff()
    {
        var dir = Path.Join(Path.GetTempPath(), "css-geoip-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new StubHandler(() => throw new HttpRequestException("offline")));
            var db = new GeoIpDatabase(NullLogger.Instance, () => Path.Join(dir, "x.mmdb"),
                () => new[] { "http://test/a.mmdb" }, () => 7, () => client);

            await db.RefreshAsync(loadExisting: true);

            Assert.False(db.IsLoaded);
            Assert.Null(db.LookupCountry(IPAddress.Parse("80.1.2.3")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // Opt-in check against a real database: GEOIP_TEST_DB=/path/GeoLite2-Country.mmdb dotnet test
    [Fact]
    public void RealDatabaseIfProvided()
    {
        var path = Environment.GetEnvironmentVariable("GEOIP_TEST_DB");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var reader = GeoIpDatabase.Validate(File.ReadAllBytes(path));
        Assert.Equal("US", reader.LookupCountry(IPAddress.Parse("8.8.8.8")));
        Assert.Equal("US", reader.LookupCountry(IPAddress.Parse("2001:4860:4860::8888")));
        Assert.Equal("DE", reader.LookupCountry(IPAddress.Parse("85.214.132.117"))); // Strato, Berlin
        Assert.Equal("DK", reader.LookupCountry(IPAddress.Parse("::ffff:193.162.153.164"))); // dr.dk range
        Assert.Null(reader.LookupCountry(IPAddress.Parse("10.0.0.1")));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var random = new Random(1);
        var bytes = new byte[4];
        for (var i = 0; i < 100_000; i++)
        {
            random.NextBytes(bytes);
            reader.LookupCountry(new IPAddress(bytes));
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"100k lookups took {sw.Elapsed}");
        Console.WriteLine($"GeoIP: 100k lookups in {sw.Elapsed.TotalMilliseconds:F0} ms, type {reader.DatabaseType}");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<byte[]> _body;

        public StubHandler(Func<byte[]> body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body()) });
    }
}
