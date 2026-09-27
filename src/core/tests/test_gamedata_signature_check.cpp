// The auto-updater's signature regression check (core/gamedata_signature_check.h).
//
// The updater only replaces gamedata.json when the downloaded file does not break a
// signature the current file resolves in the running binaries. The resolver here is a fake
// (a set of "resolving" signature strings), so this pins the decision logic: what counts as
// a regression, what is merely still broken, and that unchanged keys are never scanned.

#include "catch_amalgamated.hpp"

#include "core/gamedata_signature_check.h"

#include <set>
#include <string>

using counterstrikesharp::update::CheckSignatureRegressions;
using nlohmann::json;

namespace {

json Sig(const std::string& linuxSig, const std::string& library = "server")
{
    return { { "signatures", { { "library", library }, { "linux", linuxSig }, { "windows", "WIN" } } } };
}

struct FakeBinary
{
    std::set<std::string> resolving;
    int calls = 0;

    counterstrikesharp::update::SignatureResolver Resolver()
    {
        return [this](const std::string&, const std::string& signature) {
            ++calls;
            return resolving.count(signature) != 0;
        };
    }
};

} // namespace

TEST_CASE("identical signatures are not scanned and never regress", "[gamedata_signature_check]")
{
    json current = { { "A", Sig("AA") }, { "B", Sig("BB") } };
    FakeBinary bin;

    auto result = CheckSignatureRegressions(current, current, "linux", bin.Resolver());

    CHECK(result.regressions.empty());
    CHECK(result.changed == 0);
    CHECK(bin.calls == 0);
}

TEST_CASE("a changed signature that resolves is accepted with one scan", "[gamedata_signature_check]")
{
    json current = { { "A", Sig("OLD") } };
    json incoming = { { "A", Sig("NEW") } };
    FakeBinary bin{ { "OLD", "NEW" } };

    auto result = CheckSignatureRegressions(current, incoming, "linux", bin.Resolver());

    CHECK(result.regressions.empty());
    CHECK(result.changed == 1);
    CHECK(bin.calls == 1);
}

TEST_CASE("a changed signature that stops resolving is a regression", "[gamedata_signature_check]")
{
    json current = { { "A", Sig("OLD") } };
    json incoming = { { "A", Sig("BROKEN") } };
    FakeBinary bin{ { "OLD" } };

    auto result = CheckSignatureRegressions(current, incoming, "linux", bin.Resolver());

    REQUIRE(result.regressions.size() == 1);
    CHECK(result.regressions[0].rfind("A:", 0) == 0);
}

TEST_CASE("a key removed while its current signature resolves is a regression", "[gamedata_signature_check]")
{
    json current = { { "A", Sig("OLD") }, { "Gone", Sig("GONE") } };
    json incoming = { { "A", Sig("OLD") } };
    FakeBinary bin{ { "OLD", "GONE" } };

    auto result = CheckSignatureRegressions(current, incoming, "linux", bin.Resolver());

    REQUIRE(result.regressions.size() == 1);
    CHECK(result.regressions[0].rfind("Gone:", 0) == 0);
}

TEST_CASE("removing a key that already fails to resolve is fine", "[gamedata_signature_check]")
{
    json current = { { "Dead", Sig("DEAD") } };
    json incoming = json::object();
    FakeBinary bin;

    auto result = CheckSignatureRegressions(current, incoming, "linux", bin.Resolver());

    CHECK(result.regressions.empty());
    CHECK(result.changed == 1);
}

TEST_CASE("a change where neither version resolves is reported, not blocked", "[gamedata_signature_check]")
{
    json current = { { "A", Sig("OLD") } };
    json incoming = { { "A", Sig("ALSO_BROKEN") } };
    FakeBinary bin;

    auto result = CheckSignatureRegressions(current, incoming, "linux", bin.Resolver());

    CHECK(result.regressions.empty());
    REQUIRE(result.stillBroken.size() == 1);
    CHECK(result.stillBroken[0] == "A");
}

TEST_CASE("a library change counts as a change", "[gamedata_signature_check]")
{
    json current = { { "A", Sig("SIG", "server") } };
    json incoming = { { "A", Sig("SIG", "engine") } };
    FakeBinary bin{ { "SIG" } };

    auto result = CheckSignatureRegressions(current, incoming, "linux", bin.Resolver());

    CHECK(result.changed == 1);
    CHECK(bin.calls == 1);
}

TEST_CASE("new keys, offsets and other platforms are ignored", "[gamedata_signature_check]")
{
    json current = { { "Off", { { "offsets", { { "linux", 10 } } } } }, { "A", Sig("SIG") } };
    json incoming = { { "Off", { { "offsets", { { "linux", 99 } } } } }, { "A", Sig("SIG") }, { "New", Sig("NOPE") } };
    incoming["A"]["signatures"]["windows"] = "CHANGED_ON_WINDOWS";
    FakeBinary bin;

    auto result = CheckSignatureRegressions(current, incoming, "linux", bin.Resolver());

    CHECK(result.regressions.empty());
    CHECK(result.changed == 0);
    CHECK(bin.calls == 0);
}
