using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Plugin;

namespace CounterStrikeSharp.API.Tests;

// Loaded into a collectible context by the tests below, standing in for a type in a plugin's private dependency.
public sealed class OwnershipDependencyType
{
    public static int Supply() => 7;
    public int SupplyFromInstance() => 9;
}

// A plugin owns its whole load context, not only its main assembly: what it registered from a private
// dependency (Plugin.dll + Plugin.Core.dll) has to go on unload too. Each test loads two assemblies
// into one collectible context the way the plugin host does: xunit.assert stands in for the plugin's
// main assembly, this test assembly for its dependency.
public class PluginAssemblyOwnershipTests
{
    private static readonly string MainAssemblyPath = typeof(Assert).Assembly.Location;
    private static readonly string DependencyPath = typeof(OwnershipDependencyType).Assembly.Location;

    private static (AssemblyLoadContext Context, Assembly Main, Assembly Dependency) LoadPlugin()
    {
        var context = new AssemblyLoadContext("ownership-test", isCollectible: true);
        return (context, context.LoadFromAssemblyPath(MainAssemblyPath), context.LoadFromAssemblyPath(DependencyPath));
    }

    private static Type DependencyType(Assembly dependency) => dependency.GetType(typeof(OwnershipDependencyType).FullName!)!;

    [Fact]
    public void OwnsEveryAssemblyOfThePluginsLoadContext()
    {
        var (context, main, dependency) = LoadPlugin();
        try
        {
            Assert.True(PluginAssemblyOwnership.Owns(main, main));
            Assert.True(PluginAssemblyOwnership.Owns(main, dependency));
            Assert.True(PluginAssemblyOwnership.Owns(main, DependencyType(dependency)));

            // The API, the runtime and the default-context copies of the same files are not the plugin's.
            Assert.False(PluginAssemblyOwnership.Owns(main, typeof(PluginAssemblyOwnership).Assembly));
            Assert.False(PluginAssemblyOwnership.Owns(main, typeof(string).Assembly));
            Assert.False(PluginAssemblyOwnership.Owns(main, typeof(OwnershipDependencyType).Assembly));
            Assert.False(PluginAssemblyOwnership.Owns(main, (Assembly?)null));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void AnotherPluginsContextIsNotOwned()
    {
        var first = LoadPlugin();
        var second = LoadPlugin();
        try
        {
            Assert.False(PluginAssemblyOwnership.Owns(first.Main, second.Main));
            Assert.False(PluginAssemblyOwnership.Owns(first.Main, second.Dependency));
        }
        finally
        {
            first.Context.Unload();
            second.Context.Unload();
        }
    }

    [Fact]
    public void AssembliesOfTheDefaultContextOwnOnlyThemselves()
    {
        // A plugin loaded into the default context (a test host) must not claim the API and the runtime.
        var plugin = typeof(PluginAssemblyOwnershipTests).Assembly;

        Assert.True(PluginAssemblyOwnership.Owns(plugin, plugin));
        Assert.False(PluginAssemblyOwnership.Owns(plugin, typeof(PluginAssemblyOwnership).Assembly));
        Assert.False(PluginAssemblyOwnership.Owns(plugin, typeof(string).Assembly));
    }

    [Fact]
    public void DelegateIsOwnedThroughItsMethodOrItsTarget()
    {
        var (context, main, dependency) = LoadPlugin();
        try
        {
            var type = DependencyType(dependency);
            var staticHandler = Delegate.CreateDelegate(typeof(Func<int>), type.GetMethod(nameof(OwnershipDependencyType.Supply))!);
            Assert.True(PluginAssemblyOwnership.Owns(main, staticHandler));

            // An API/runtime method bound to a plugin object: only the target says whose it is.
            var boundToPluginObject = Delegate.CreateDelegate(typeof(Func<string?>), Activator.CreateInstance(type),
                typeof(object).GetMethod(nameof(ToString))!);
            Assert.True(PluginAssemblyOwnership.Owns(main, boundToPluginObject));

            Assert.False(PluginAssemblyOwnership.Owns(main, new Func<int>(OwnershipDependencyType.Supply)));
            Assert.False(PluginAssemblyOwnership.Owns(main, (Delegate?)null));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void CapabilitySupplierFromADependencyIsRemovedOnUnload()
    {
        var name = "ownership:" + Guid.NewGuid();
        var context = RegisterSupplierFromDependency(name, purge: true);

        // Before: the stale supplier stayed first in the list, so Get() kept returning the old plugin's object.
        Assert.Equal(0, new PluginCapability<int>(name).Get());
        Assert.True(WaitForCollection(context), "the plugin context stayed alive after its capability suppliers were removed");
    }

    [Fact]
    public void CapabilityOverAPluginTypeDoesNotPinTheContext()
    {
        var context = RegisterCapabilityOverPluginType();
        Assert.True(WaitForCollection(context), "a capability over one of the plugin's own types kept its context alive");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterSupplierFromDependency(string name, bool purge)
    {
        var (context, main, dependency) = LoadPlugin();
        var supplier = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>),
            DependencyType(dependency).GetMethod(nameof(OwnershipDependencyType.Supply))!);

        Capabilities.RegisterPluginCapability(new PluginCapability<int>(name), supplier);
        Assert.Equal(7, new PluginCapability<int>(name).Get());

        if (purge) Capabilities.RemoveProvidersForAssembly(main);

        context.Unload();
        return new WeakReference(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterCapabilityOverPluginType()
    {
        var (context, main, dependency) = LoadPlugin();
        var type = DependencyType(dependency);

        // Capabilities.RegisterPluginCapability<T>(new PluginCapability<T>(name), () => new T()) with T from the plugin.
        var capability = Activator.CreateInstance(typeof(PluginCapability<>).MakeGenericType(type), "ownership:" + Guid.NewGuid())!;
        var supplier = Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(type), typeof(Activator)
            .GetMethod(nameof(Activator.CreateInstance), 1, Type.EmptyTypes)!.MakeGenericMethod(type));
        typeof(Capabilities).GetMethod(nameof(Capabilities.RegisterPluginCapability))!.MakeGenericMethod(type)
            .Invoke(null, new[] { capability, supplier });

        Capabilities.RemoveProvidersForAssembly(main);

        context.Unload();
        return new WeakReference(context);
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
