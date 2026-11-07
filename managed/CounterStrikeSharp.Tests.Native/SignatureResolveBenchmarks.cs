using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Xunit;

namespace NativeTestsPlugin;

/// <summary>
/// What resolving a gamedata signature costs on the game thread: the scan a binding pays on its first
/// invoke or hook. Resolves every server-library signature twice; the first pass is what a plugin
/// touching that binding for the first time waits for, the second shows what a repeat costs.
/// Run with <c>css_itest SignatureResolveBenchmarks</c> on a freshly started server.
/// </summary>
[Trait("Category", "Benchmark")]
public class SignatureResolveBenchmarks
{
    [Fact]
    public void Benchmark_SignatureResolve_AllServerSignatures()
    {
        var signatures = GameData.GameDataProvider.Methods
            .Where(m => m.Value.Signatures?.Linux is { Length: > 0 } &&
                        (m.Value.Signatures.Library is null or "server"))
            .Select(m => (Key: m.Key, Signature: m.Value.Signatures!.Linux!))
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(signatures);

        var first = Pass(signatures, out var slowest);
        var second = Pass(signatures, out _);

        Report("first resolve", first, signatures.Count);
        Report("repeat resolve", second, signatures.Count);
        Console.WriteLine($"[SignatureResolveBenchmarks] slowest first resolve: {slowest.Key} {slowest.Ms:F3} ms");
    }

    private static List<double> Pass(List<(string Key, string Signature)> signatures, out (string Key, double Ms) slowest)
    {
        var times = new List<double>(signatures.Count);
        slowest = ("", 0);
        foreach (var (key, signature) in signatures)
        {
            var sw = Stopwatch.StartNew();
            NativeAPI.FindSignature(Addresses.ServerPath, signature);
            var ms = sw.Elapsed.TotalMilliseconds;
            times.Add(ms);
            if (ms > slowest.Ms) slowest = (key, ms);
        }

        return times;
    }

    private static void Report(string name, List<double> ms, int count)
    {
        var total = ms.Sum();
        var sorted = ms.OrderBy(x => x).ToList();
        Console.WriteLine($"[SignatureResolveBenchmarks] {name,-15} x{count}: total {total,9:F3} ms, " +
                          $"median {sorted[count / 2]:F3} ms, p90 {sorted[count * 9 / 10]:F3} ms, max {sorted[^1]:F3} ms");
        ScriptContextBenchmarks.RecordMs($"Signature {name} x{count}", "Signature Resolve", total, count);
    }
}
