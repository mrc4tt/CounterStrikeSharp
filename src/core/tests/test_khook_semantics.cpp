// Contract tests for the KHook semantics the plugin's hook registrations rely on.
//
// Nothing here hooks real code. In the non-standalone build (the one the plugin
// uses) every KHOOK_API entry point in khook.hpp is an inline forwarder to
// `KHook::__exported__khook`, the IKHook implementation Metamod hands us. That
// makes the whole surface mockable: we point __exported__khook at a recorder and
// assert on WHAT KHook::Virtual asks the host to do. Everything exercised below
// -- vtable index extraction, the Add/AddGlobal/Configure state machine,
// hooks::OriginalReturnOr -- is real header/production code compiled from the
// same sources as the plugin.
//
// Why these five and not others: each one pins a semantic that a manager in
// src/core/managers depends on and that is invisible at the call site, i.e. the
// kind of thing an innocent-looking refactor silently breaks.

#include "catch_amalgamated.hpp"

#include "core/khook_original_return.h"

#include <khook.hpp>

#include <vector>

// The plugin gets this symbol from Metamod. Here the tests own it.
namespace KHook {
IKHook* __exported__khook = nullptr;
}

namespace {

// ---------------------------------------------------------------------------
// Recording IKHook. Records the arguments the tests assert on and returns
// scripted values for the rest.
// ---------------------------------------------------------------------------
class RecordingKHook : public KHook::IKHook
{
  public:
    struct SetupCall
    {
        void** vtable;
        int index;
        void* context;
    };

    std::vector<SetupCall> setups;
    std::vector<KHook::HookID_t> removed;

    // Scripted returns for the OriginalReturnOr tests.
    void* originalValue = nullptr;
    void* overrideValue = nullptr;

    KHook::HookID_t SetupHook(void*, void*, void*, void*, void*, void*, void*, unsigned int, bool) override { return KHook::INVALID_HOOK; }

    KHook::HookID_t
    SetupVirtualHook(void** vtable, int index, void* context, void*, void*, void*, void*, void*, unsigned int, bool) override
    {
        setups.push_back({ vtable, index, context });
        return static_cast<KHook::HookID_t>(setups.size()); // 1-based, never INVALID_HOOK
    }

    void RemoveHook(KHook::HookID_t id, bool) override { removed.push_back(id); }

    void* GetContextPtr() override { return nullptr; }
    void* GetOriginalFunction() override { return nullptr; }
    void* GetOriginalValuePtr() override { return originalValue; }
    void* GetOverrideValuePtr() override { return overrideValue; }
    void* GetCurrentValuePtr(bool) override { return nullptr; }
    void DestroyReturnValue() override {}
    void* FindOriginal(void*) override { return nullptr; }
    void* FindOriginalVirtual(void**, int) override { return nullptr; }
    void* DoRecall(KHook::Action, void*, std::size_t, void*, void*) override { return nullptr; }
    void SaveReturnValue(KHook::Action, void*, std::size_t, void*, void*, bool) override {}
    void* LookupSignature(void*, std::size_t, const char*) override { return nullptr; }
    bool WasOriginalFunctionSkipped() override { return false; }
};

// Stand-in for an engine interface: a pure-ish vtable whose layout we control.
class IFakeGameInterface
{
  public:
    virtual ~IFakeGameInterface() = default;
    virtual void FirstMethod(int) {}
    virtual void SecondMethod(int) {}
};

class FakeGameInterface : public IFakeGameInterface
{
};

struct Listener
{
    KHook::Return<void> Pre(IFakeGameInterface*, int) { return { KHook::Action::Ignore }; }
    KHook::Return<void> Post(IFakeGameInterface*, int) { return { KHook::Action::Ignore }; }
};

// The vtable pointer of an object, read the way KHook reads it.
void** VtableOf(void* object) { return *reinterpret_cast<void***>(object); }

struct MockGuard
{
    RecordingKHook mock;
    MockGuard() { KHook::__exported__khook = &mock; }
    ~MockGuard() { KHook::__exported__khook = nullptr; }
};

} // namespace

TEST_CASE("member-function ctor derives the vtable index KHook is handed", "[KHook][virtual]")
{
    // EntityManager/EventManager/ConCommandManager all construct their
    // KHook::Virtual from a member function pointer and never see an index. If
    // GetVtableIndex ever stopped resolving (it reads the Itanium ABI member
    // pointer layout directly), the ctor would silently store
    // INVALID_VTBL_INDEX and every hook would become a no-op -- no error, no
    // log line, just callbacks that never fire.
    const std::int32_t first = KHook::GetVtableIndex(&IFakeGameInterface::FirstMethod);
    const std::int32_t second = KHook::GetVtableIndex(&IFakeGameInterface::SecondMethod);

    CHECK(first >= 0);
    CHECK(second == first + 1);

    MockGuard guard;
    Listener listener;
    FakeGameInterface object;

    KHook::Virtual<IFakeGameInterface, void, int> hook(&IFakeGameInterface::FirstMethod, &listener, &Listener::Pre, &Listener::Post);
    hook.Add(&object);

    REQUIRE(guard.mock.setups.size() == 1);
    CHECK(guard.mock.setups[0].index == first);
    CHECK(guard.mock.setups[0].vtable == VtableOf(&object));
    CHECK(guard.mock.setups[0].context == &hook);
}

TEST_CASE("Add is deduplicated per vtable, not per object", "[KHook][virtual]")
{
    // KHook keys installed hooks by (vtable + index), so hooking a second
    // instance that shares a vtable installs nothing new -- the first hook
    // already intercepts it. Two consequences we depend on:
    //   1. Re-registering a manager (Hook_StartupServer can fire twice in one
    //      map session on workshop ss_dead cycles) cannot double-hook.
    //   2. Conversely, Remove(objectA) does NOT stop callbacks for objectB;
    //      the hook stays installed for the shared vtable.
    MockGuard guard;
    Listener listener;
    FakeGameInterface first;
    FakeGameInterface second;

    KHook::Virtual<IFakeGameInterface, void, int> hook(&IFakeGameInterface::FirstMethod, &listener, &Listener::Pre, &Listener::Post);
    hook.Add(&first);
    hook.Add(&second);
    hook.Add(&first);

    CHECK(guard.mock.setups.size() == 1);
    CHECK(hook.IsActive());

    hook.Remove(&first);
    CHECK(hook.IsActive()); // `second` is still registered
    hook.Remove(&second);
    CHECK_FALSE(hook.IsActive());

    // Removing the last registered object does not uninstall the KHook hook --
    // it stays until the Virtual is destroyed. Managers must therefore treat
    // Remove() as "stop dispatching to me", not "the vtable is clean again".
    CHECK(guard.mock.removed.empty());
}

TEST_CASE("AddGlobal takes the ADDRESS of a vtable pointer, not an object", "[KHook][virtual]")
{
    // mm_plugin.cpp hooks CGameEventManager::LoadEventsFromFile without ever
    // owning an instance: it resolves the raw vtable out of server.so and then
    // calls AddGlobal((IGameEventManager2*)&g_pCGameEventManagerVTable) --
    // passing the address OF THE VARIABLE holding the vtable, because AddGlobal
    // dereferences its argument once to find the vtable. entity_manager.cpp's
    // CheckTransmit uses the same trick via globals::gameEntities.
    //
    // Passing the vtable pointer directly (the "obvious" cleanup) would make
    // KHook dereference the first entry of the vtable and hook garbage. This
    // test is the guard on that.
    MockGuard guard;
    Listener listener;
    FakeGameInterface object;

    void* pFakeVtable = VtableOf(&object);

    KHook::Virtual<IFakeGameInterface, void, int> hook(&IFakeGameInterface::FirstMethod, &listener, &Listener::Pre, &Listener::Post);
    hook.AddGlobal(reinterpret_cast<IFakeGameInterface*>(&pFakeVtable));

    REQUIRE(guard.mock.setups.size() == 1);
    CHECK(guard.mock.setups[0].vtable == VtableOf(&object));
    CHECK(hook.IsActive());

    hook.RemoveGlobal(reinterpret_cast<IFakeGameInterface*>(&pFakeVtable));
    CHECK_FALSE(hook.IsActive());
}

TEST_CASE("Configure is a no-op for the same index and tears down on change", "[KHook][virtual]")
{
    // EntityManager::CheckTransmit is not in the SDK headers, so its hook is
    // configured from a gamedata offset: Configure(offset) then AddContext +
    // AddGlobal. Two behaviours matter. A default-constructed Virtual holds
    // INVALID_VTBL_INDEX and refuses to install anything -- which is why the
    // `offset >= 0` guard in EntityManager::OnAllInitialized degrades to a
    // disabled hook rather than a bad one when gamedata goes stale. And
    // re-Configuring to a DIFFERENT index (a gamedata update between hooks)
    // uninstalls what is already installed instead of leaving a hook on the
    // old slot.
    MockGuard guard;
    Listener listener;
    FakeGameInterface object;

    KHook::Virtual<IFakeGameInterface, void, int> unconfigured;
    unconfigured.AddContext(&listener, nullptr, &Listener::Post);
    unconfigured.AddGlobal(&object);
    CHECK(guard.mock.setups.empty()); // INVALID_VTBL_INDEX -> nothing installed

    const std::int32_t index = KHook::GetVtableIndex(&IFakeGameInterface::FirstMethod);

    KHook::Virtual<IFakeGameInterface, void, int> hook;
    hook.Configure(index);
    hook.AddContext(&listener, nullptr, &Listener::Post);
    hook.AddGlobal(&object);
    REQUIRE(guard.mock.setups.size() == 1);
    CHECK(guard.mock.setups[0].index == index);

    hook.Configure(index); // same index -> nothing torn down
    CHECK(guard.mock.removed.empty());

    hook.Configure(index + 1); // different index -> existing hook uninstalled
    CHECK(guard.mock.removed.size() == 1);
}

TEST_CASE("OriginalReturnOr survives a superseded call", "[KHook][return]")
{
    // KHook allocates the original-return storage only when the original
    // actually ran. A plugin that supersedes IServerGameClients::ClientConnect
    // (every ban/queue plugin) leaves it null, so KHook::GetOriginalReturn<T>()
    // -- a blind `*(T*)ptr` -- would dereference null and take the server down
    // inside our own post hook. PlayerManager and mm_plugin::Hook_FindService go
    // through OriginalReturnOr for exactly that reason.
    MockGuard guard;

    bool original = true;
    bool overridden = false;

    SECTION("original present -> original wins")
    {
        guard.mock.originalValue = &original;
        guard.mock.overrideValue = &overridden;
        CHECK(counterstrikesharp::hooks::OriginalReturnOr<bool>(false) == true);
    }

    SECTION("superseded -> falls back to the overriding hook's value")
    {
        guard.mock.originalValue = nullptr;
        guard.mock.overrideValue = &overridden;
        CHECK(counterstrikesharp::hooks::OriginalReturnOr<bool>(true) == false);
    }

    SECTION("neither -> caller's default, no dereference")
    {
        guard.mock.originalValue = nullptr;
        guard.mock.overrideValue = nullptr;
        CHECK(counterstrikesharp::hooks::OriginalReturnOr<bool>(true) == true);
        CHECK(counterstrikesharp::hooks::OriginalReturnOr<void*>(nullptr) == nullptr);
    }
}
