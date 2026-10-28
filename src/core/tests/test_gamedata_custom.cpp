// gamedata/custom/ overrides (core/gamedata_custom.h): per-field merge, _gameVersion pins,
// metadata keys, and the fallback to core when an overridden signature does not resolve.

#include "catch_amalgamated.hpp"

#include "core/gamedata_custom.h"

#include <set>

using counterstrikesharp::gamedata::ApplyCustomFiles;
using counterstrikesharp::gamedata::CustomFile;
using counterstrikesharp::gamedata::NormalizeGameVersion;
using nlohmann::json;

namespace {

json Core()
{
    return json::parse(R"({
        "Host_Say": { "signatures": { "library": "server", "linux": "CORE_LINUX", "windows": "CORE_WIN" } },
        "GameEntitySystem": { "offsets": { "linux": 80, "windows": 88 } }
    })");
}

// Resolves exactly the listed signature texts.
auto ResolvesOnly(std::set<std::string> good)
{
    return [good](const std::string&, const std::string& signature) {
        return good.count(signature) > 0;
    };
}

} // namespace

TEST_CASE("game versions normalise to the steam.inf tag", "[gamedata_custom]")
{
    CHECK(NormalizeGameVersion("1.41.9.2") == "14192");
    CHECK(NormalizeGameVersion("14192") == "14192");
    CHECK(NormalizeGameVersion("14192b") == "14192");
    CHECK(NormalizeGameVersion("abc").empty());
}

TEST_CASE("an override replaces only the fields it names", "[gamedata_custom]")
{
    auto core = Core();
    auto result =
        ApplyCustomFiles(core, { { "custom/a.json", json::parse(R"({"Host_Say":{"signatures":{"linux":"NEW"}}})") } }, "14192", "linux");

    CHECK(core["Host_Say"]["signatures"]["linux"] == "NEW");
    CHECK(core["Host_Say"]["signatures"]["windows"] == "CORE_WIN");
    CHECK(core["Host_Say"]["signatures"]["library"] == "server");
    REQUIRE(result.applied.size() == 1);
    CHECK_FALSE(result.applied[0].added);
    CHECK(result.applied[0].fields == std::vector<std::string>{ "signatures.linux" });
}

TEST_CASE("offsets merge per platform too, and new keys are additions", "[gamedata_custom]")
{
    auto core = Core();
    auto result = ApplyCustomFiles(core, { { "custom/a.json", json::parse(R"({
                                       "GameEntitySystem": { "offsets": { "linux": 96 } },
                                       "MyThing": { "offsets": { "linux": 5 } }
                                   })") } },
                                   "14192", "linux");

    CHECK(core["GameEntitySystem"]["offsets"]["linux"] == 96);
    CHECK(core["GameEntitySystem"]["offsets"]["windows"] == 88);
    CHECK(core["MyThing"]["offsets"]["linux"] == 5);
    REQUIRE(result.applied.size() == 2);
    CHECK(result.applied[1].added);
}

TEST_CASE("metadata keys are never entries", "[gamedata_custom]")
{
    auto core = Core();
    ApplyCustomFiles(core, { { "custom/a.json", json::parse(R"({"_comment":"mine","_gameVersion":"1.41.9.2"})") } }, "14192", "linux");

    CHECK_FALSE(core.contains("_comment"));
    CHECK_FALSE(core.contains("_gameVersion"));
}

TEST_CASE("a pinned file applies only on its game build", "[gamedata_custom]")
{
    const auto file = json::parse(R"({"_gameVersion":"1.41.9.2","Host_Say":{"signatures":{"linux":"NEW"}}})");

    SECTION("matching build")
    {
        auto core = Core();
        auto result = ApplyCustomFiles(core, { { "custom/a.json", file } }, "14192", "linux");
        CHECK(core["Host_Say"]["signatures"]["linux"] == "NEW");
        CHECK(result.skippedFiles.empty());
    }

    SECTION("other build: whole file skipped, core stays")
    {
        auto core = Core();
        auto result = ApplyCustomFiles(core, { { "custom/a.json", file } }, "14193", "linux");
        CHECK(core["Host_Say"]["signatures"]["linux"] == "CORE_LINUX");
        CHECK(result.applied.empty());
        REQUIRE(result.skippedFiles.size() == 1);
    }

    SECTION("unknown build: skipped, a pin cannot be verified")
    {
        auto core = Core();
        auto result = ApplyCustomFiles(core, { { "custom/a.json", file } }, "", "linux");
        CHECK(core["Host_Say"]["signatures"]["linux"] == "CORE_LINUX");
        CHECK(result.skippedFiles.size() == 1);
    }

    SECTION("a file without a pin always applies")
    {
        auto core = Core();
        ApplyCustomFiles(core, { { "custom/a.json", json::parse(R"({"Host_Say":{"signatures":{"linux":"NEW"}}})") } }, "", "linux");
        CHECK(core["Host_Say"]["signatures"]["linux"] == "NEW");
    }
}

TEST_CASE("later files win", "[gamedata_custom]")
{
    auto core = Core();
    ApplyCustomFiles(core,
                     { { "custom/a.json", json::parse(R"({"Host_Say":{"signatures":{"linux":"A"}}})") },
                       { "custom/b.json", json::parse(R"({"Host_Say":{"signatures":{"linux":"B"}}})") } },
                     "14192", "linux");
    CHECK(core["Host_Say"]["signatures"]["linux"] == "B");
}

TEST_CASE("an override signature that does not resolve falls back to a working core one", "[gamedata_custom]")
{
    auto core = Core();
    auto result = ApplyCustomFiles(
        core,
        { { "custom/a.json", json::parse(R"({"Host_Say":{"signatures":{"library":"engine","linux":"BROKEN"},"offsets":{"linux":1}}})") } },
        "14192", "linux", ResolvesOnly({ "CORE_LINUX" }));

    CHECK(core["Host_Say"]["signatures"]["linux"] == "CORE_LINUX");
    CHECK(core["Host_Say"]["signatures"]["library"] == "server");
    // The rest of the patch still applies.
    CHECK(core["Host_Say"]["offsets"]["linux"] == 1);
    REQUIRE(result.applied.size() == 1);
    CHECK(result.applied[0].fields == std::vector<std::string>{ "offsets" });
    CHECK(result.warnings.size() == 1);
}

TEST_CASE("a broken override is kept when core is broken too", "[gamedata_custom]")
{
    auto core = Core();
    auto result = ApplyCustomFiles(core, { { "custom/a.json", json::parse(R"({"Host_Say":{"signatures":{"linux":"BROKEN"}}})") } }, "14192",
                                   "linux", ResolvesOnly({}));

    CHECK(core["Host_Say"]["signatures"]["linux"] == "BROKEN");
    CHECK(result.warnings.size() == 1);
}

TEST_CASE("an override that only reverts to core is not reported", "[gamedata_custom]")
{
    auto core = Core();
    auto result = ApplyCustomFiles(core, { { "custom/a.json", json::parse(R"({"Host_Say":{"signatures":{"linux":"BROKEN"}}})") } }, "14192",
                                   "linux", ResolvesOnly({ "CORE_LINUX" }));

    CHECK(result.applied.empty());
    CHECK(core["Host_Say"]["signatures"]["linux"] == "CORE_LINUX");
}

TEST_CASE("non-object values are ignored with a warning", "[gamedata_custom]")
{
    auto core = Core();
    auto result = ApplyCustomFiles(core, { { "custom/a.json", json::parse(R"({"Host_Say":"oops"})") } }, "14192", "linux");

    CHECK(core["Host_Say"]["signatures"]["linux"] == "CORE_LINUX");
    CHECK(result.warnings.size() == 1);
}
