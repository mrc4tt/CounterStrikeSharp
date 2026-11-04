using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Tests;

// Loaded into a collectible context by the tests below, standing in for a plugin method.
public static class InvokerCacheUnloadTarget
{
    public static int Echo(int value) => value;
}

// FunctionReference's compiled-invoker cache is static in the default context and keyed by the
// handler's MethodInfo, so an entry for a plugin method roots that plugin's AssemblyLoadContext.
// BasePlugin.Dispose drops them with RemoveInvokersForAssembly; these tests load this assembly into
// a collectible context the way the plugin host does and check that it really goes away.
public class InvokerCacheUnloadTests
{
    [Fact]
    public void CachedInvoker_RootsUnloadedContext()
    {
        var context = LoadAndCacheInvoker(removeInvokers: false, out var assembly);
        try
        {
            Assert.False(WaitForCollection(context), "control: a cached invoker should keep the context alive");
        }
        finally
        {
            // Leave nothing behind for other tests.
            RemoveInvokers(assembly);
        }
    }

    [Fact]
    public void RemoveInvokersForAssembly_LetsContextCollect()
    {
        var context = LoadAndCacheInvoker(removeInvokers: true, out _);
        Assert.True(WaitForCollection(context), "the plugin context stayed alive after its invokers were removed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndCacheInvoker(bool removeInvokers, out WeakReference assemblyRef)
    {
        var context = new AssemblyLoadContext("invoker-cache-test", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(InvokerCacheUnloadTarget).Assembly.Location);
        var method = assembly.GetType(typeof(InvokerCacheUnloadTarget).FullName!)!.GetMethod(nameof(InvokerCacheUnloadTarget.Echo))!;
        var handler = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), method);

        var invoker = FunctionReference.GetCompiledInvoker(handler);
        Assert.NotNull(invoker);
        Assert.Equal(42, invoker!(null!, new object[] { 42 }));

        if (removeInvokers) FunctionReference.RemoveInvokersForAssembly(assembly);

        assemblyRef = new WeakReference(assembly);
        context.Unload();
        return new WeakReference(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveInvokers(WeakReference assemblyRef)
    {
        if (assemblyRef.Target is System.Reflection.Assembly assembly)
            FunctionReference.RemoveInvokersForAssembly(assembly);
    }

    private static bool WaitForCollection(WeakReference context)
    {
        // Unloading a collectible context takes several collections with finalizers in between.
        for (var i = 0; i < 20 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !context.IsAlive;
    }
}
