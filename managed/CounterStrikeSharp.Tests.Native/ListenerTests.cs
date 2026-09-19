using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Moq;
using Xunit;

public class ListenerTests
{
    [Fact]
    public async Task CanRegisterAndDeregisterListeners()
    {
        int callCount = 0;
        var callback = FunctionReference.Create((int playerSlot, string name, string ipAddress) =>
        {
            Assert.NotNull(ipAddress);
            Assert.NotEmpty(name);
            Assert.Equal("127.0.0.1", ipAddress);
            callCount++;
        });

        NativeAPI.IssueServerCommand("bot_quota 0; bot_quota_mode normal");
        await WaitOneFrame();

        var listening = false;

        try
        {
            NativeAPI.AddListener("OnClientConnect", callback);
            listening = true;

            // Test hooking
            NativeAPI.IssueServerCommand("bot_kick");
            NativeAPI.IssueServerCommand("bot_add");
            await WaitUntil(() => callCount >= 1);

            Assert.Equal(1, callCount);
            NativeAPI.RemoveListener("OnClientConnect", callback);
            listening = false;

            // Test unhooking
            NativeAPI.IssueServerCommand("bot_kick");
            NativeAPI.IssueServerCommand("bot_add");
            await WaitFrames(8);
            Assert.Equal(1, callCount);
        }
        finally
        {
            // A failed assertion must not leave the listener registered for the tests that follow.
            if (listening) NativeAPI.RemoveListener("OnClientConnect", callback);
            NativeAPI.IssueServerCommand("bot_quota 1");
        }
    }

    [Fact]
    public async Task EntityListenersAreFired()
    {
        // The listeners see every entity on the server, and bots joining around this test spawn weapons
        // in the same frames, so only count the entity class this test creates.
        int createCount = 0;
        int deleteCount = 0;

        var createCallback = FunctionReference.Create((IntPtr entityPtr) =>
        {
            if (new CBaseEntity(entityPtr).DesignerName == "prop_dynamic") createCount++;
        });

        var deleteCallback = FunctionReference.Create((IntPtr entityPtr) =>
        {
            if (new CBaseEntity(entityPtr).DesignerName == "prop_dynamic") deleteCount++;
        });

        try
        {
            NativeAPI.AddListener("OnEntityCreated", createCallback);
            NativeAPI.AddListener("OnEntityDeleted", deleteCallback);

            var ent = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            await WaitOneFrame();

            Assert.Equal(1, createCount);

            ent.Remove();
            await WaitOneFrame();

            Assert.Equal(1, deleteCount);
        }
        finally
        {
            NativeAPI.RemoveListener("OnEntityCreated", createCallback);
            NativeAPI.RemoveListener("OnEntityDeleted", deleteCallback);
        }
    }

    [Fact]
    public async Task TakeDamageListenersAreFired()
    {
        int preCallCount = 0;
        int postCallCount = 0;
        var victimPointer = IntPtr.Zero;

        // Other entities can take damage while this runs, so only count calls for our victim. The pre
        // listener has to return a HookResult: the native side reads one back for every listener.
        float seenDamage = -1;
        var preCallback = FunctionReference.Create((IntPtr entityPtr, IntPtr damageInfoPtr) =>
        {
            if (entityPtr == victimPointer)
            {
                preCallCount++;
                seenDamage = new CTakeDamageInfo(damageInfoPtr).Damage;
            }

            return HookResult.Continue;
        });

        var postCallback = FunctionReference.Create((IntPtr entityPtr, IntPtr damageInfoPtr) =>
        {
            if (entityPtr == victimPointer) postCallCount++;
        });

        try
        {
            NativeAPI.AddListener("OnEntityTakeDamagePre", preCallback);
            NativeAPI.AddListener("OnEntityTakeDamagePost", postCallback);

            var pawn = await SpawnVictim();
            victimPointer = pawn.Handle;

            var playerHealth = pawn.Health;
            DealDamage(pawn, 10);

            await WaitOneFrame();
            Assert.Equal(1, preCallCount);
            Assert.Equal(1, postCallCount);
            Assert.Equal(10, seenDamage);
            Assert.True(pawn.Health < playerHealth,
                $"Expected health below {playerHealth}, got {pawn.Health} (damage seen by listener: {seenDamage})");
        }
        finally
        {
            NativeAPI.RemoveListener("OnEntityTakeDamagePre", preCallback);
            NativeAPI.RemoveListener("OnEntityTakeDamagePost", postCallback);
        }
    }

    [Fact]
    public async Task TakeDamageListenerCanBeCancelled()
    {
        int preCallCount = 0;
        int postCallCount = 0;
        var victimPointer = IntPtr.Zero;

        Listeners.OnEntityTakeDamagePre preCallback = (entity, damageInfo) =>
        {
            if (entity.Handle != victimPointer) return HookResult.Continue;

            preCallCount++;
            return HookResult.Stop;
        };

        Listeners.OnEntityTakeDamagePre secondCallback = (entity, damageInfo) =>
        {
            if (entity.Handle == victimPointer) preCallCount++;
            return HookResult.Continue;
        };

        Listeners.OnEntityTakeDamagePost postCallback = (entity, damageInfo, damageResult) =>
        {
            if (entity.Handle == victimPointer) postCallCount++;
        };

        try
        {
            NativeAPI.AddListener("OnEntityTakeDamagePre", preCallback);
            NativeAPI.AddListener("OnEntityTakeDamagePre", secondCallback);
            NativeAPI.AddListener("OnEntityTakeDamagePost", postCallback);

            var pawn = await SpawnVictim();
            victimPointer = pawn.Handle;

            var playerHealth = pawn.Health;
            DealDamage(pawn, 10);

            await WaitOneFrame();
            Assert.Equal(playerHealth, pawn.Health);

            Assert.Equal(1, preCallCount);
            Assert.Equal(0, postCallCount);
        }
        finally
        {
            NativeAPI.RemoveListener("OnEntityTakeDamagePre", preCallback);
            NativeAPI.RemoveListener("OnEntityTakeDamagePre", secondCallback);
            NativeAPI.RemoveListener("OnEntityTakeDamagePost", postCallback);
        }
    }

    private static async Task<CCSPlayerPawn> SpawnVictim()
    {
        // Respawn immunity would let the listeners fire without any health being lost.
        NativeAPI.IssueServerCommand("mp_respawn_immunitytime 0");
        NativeAPI.IssueServerCommand("bot_kick");
        NativeAPI.IssueServerCommand("bot_add");
        await WaitOneFrame();

        var player = Utilities.GetPlayers().First(p => p.IsBot && p.PawnIsAlive);
        var pawn = player.PlayerPawn.Value!;

        // The convar only affects later spawns; this pawn may already carry spawn protection.
        pawn.GunGameImmunity = false;
        pawn.ImmuneToGunGameDamageTime = 0;

        return pawn;
    }

    // Lets the engine build the CTakeDamageInfo. These tests used to fill one in by hand, with the attacker
    // block at a hard-coded offset, and were skipped ("Damage func broken") once a game update moved it.
    // point_hurt with "!activator" as its target damages exactly the entity passed as the input's activator.
    private static void DealDamage(CCSPlayerPawn victim, int damage)
    {
        var hurt = Utilities.CreateEntityByName<CPointHurt>("point_hurt")!;

        using (var keyValues = new CEntityKeyValues())
        {
            keyValues.SetString("DamageTarget", "!activator");
            keyValues.SetInt("Damage", damage);
            keyValues.SetInt("DamageType", (int)DamageTypes_t.DMG_GENERIC);
            hurt.DispatchSpawn(keyValues);
        }

        try
        {
            hurt.AcceptInput("Hurt", victim, victim);
        }
        finally
        {
            hurt.Remove();
        }
    }
}
