using System.Collections.Concurrent;
using System.Reflection;
using CounterStrikeSharp.API.Core;
using Xunit.Abstractions;

namespace CounterStrikeSharp.API.Tests;

public class ScriptContextAllocationTests
{
    private readonly ITestOutputHelper output;
    public ScriptContextAllocationTests(ITestOutputHelper output) => this.output = output;

    private static readonly MethodInfo Cleanup = typeof(ScriptContext).GetMethod(
        "GlobalCleanUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Finalizers = typeof(ScriptContext).GetField(
        "ms_finalizers", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void NumericContextsDoNotAllocateStringCleanupStorage()
    {
        for (int i = 0; i < 100; i++) GC.KeepAlive(new ScriptContext());
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) GC.KeepAlive(new ScriptContext());
        long bytesPerContext = (GC.GetAllocatedBytesForCurrentThread() - start) / 1000;
        output.WriteLine($"Bytes per ScriptContext: {bytesPerContext}");
        // Includes the inline native context and lock, but not an unused queue/segment.
        Assert.InRange(bytesPerContext, 1, 512);
        var context = new ScriptContext();
        context.PushPrimitive(42);
        Assert.Equal(42, context.GetArgument<int>(0));
        Cleanup.Invoke(context, null);
        Assert.Null(Finalizers.GetValue(context));
    }

    [Fact]
    public void StringsRoundTripAndCleanupCanBeRepeatedAndReused()
    {
        var context = new ScriptContext();
        try
        {
            context.Push("hello ø 世界");
            context.Push("second");
            Assert.Equal("hello ø 世界", context.GetArgument<string>(0));
            Assert.Equal("second", context.GetArgument<string>(1));
            var queue = Assert.IsType<ConcurrentQueue<IntPtr>>(Finalizers.GetValue(context));
            Assert.Equal(2, queue.Count);
            Cleanup.Invoke(context, null);
            Cleanup.Invoke(context, null);
            Assert.Empty(queue);
            context.Reset();
            context.Push("again");
            Assert.Equal("again", context.GetArgument<string>(0));
            Assert.Same(queue, Finalizers.GetValue(context));
            Cleanup.Invoke(context, null);
            Assert.Empty(queue);
        }
        finally
        {
            Cleanup.Invoke(context, null);
        }
    }
}
