// Plugin gamedata auto-update (core/gamedata_plugin_update.h): identifying which feed file a
// local plugin file is, patching only the signatures that don't resolve, and writing the file
// back in its own style.

#include "catch_amalgamated.hpp"

#include "core/gamedata_plugin_update.h"

#include <set>

using namespace counterstrikesharp::update::plugin_gamedata;

namespace {

auto ResolvesOnly(std::set<std::string> good)
{
    return [good](const std::string&, const std::string& signature) {
        return good.count(signature) > 0;
    };
}

std::vector<FeedFile> Feed()
{
    return {
        { "matchzy/matchzy.json", "matchzy.json", "aa" },
        { "matchzy-original/matchzy.json", "matchzy.json", "bb" },
        { "weaponpaints/weaponpaints.json", "weaponpaints.json", "cc" },
        { "cleanercs2/gamedata.json", "gamedata.json", "dd" },
    };
}

} // namespace

TEST_CASE("Sha256Hex matches the FIPS 180-4 test vectors", "[plugin-gamedata]")
{
    CHECK(Sha256Hex("") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    CHECK(Sha256Hex("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    CHECK(Sha256Hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq") ==
          "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1");
    // 55 and 56 bytes straddle the one-block / two-block padding boundary.
    CHECK(Sha256Hex(std::string(55, 'a')) == "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318");
    CHECK(Sha256Hex(std::string(56, 'a')) == "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a");
}

TEST_CASE("ParseManifestFiles keeps JSON files only", "[plugin-gamedata]")
{
    auto manifest = nlohmann::json::parse(R"({ "gameVersion": "14196", "files": [
        { "url": "matchzy/matchzy.json", "fileName": "matchzy.json", "sha256": "AB" },
        { "url": "gamebanfix/gamebanfix.games.txt", "fileName": "gamebanfix.games.txt", "sha256": "cd" },
        { "url": "swiftlys2/signatures.jsonc", "fileName": "signatures.jsonc", "sha256": "ef" },
        { "fileName": "broken.json" }
    ] })");
    auto files = ParseManifestFiles(manifest);
    REQUIRE(files.size() == 1);
    CHECK(files[0].url == "matchzy/matchzy.json");
    CHECK(files[0].sha256 == "ab");
}

TEST_CASE("ParseIndexHashes maps every published hash to its latest/ URL", "[plugin-gamedata]")
{
    auto index = nlohmann::json::parse(R"({ "versions": [
        { "gameVersion": "14196", "files": [ { "plugin": "matchzy", "fileName": "matchzy.json", "content": { "sha256": "AA" } } ] },
        { "gameVersion": "14195", "files": [ { "plugin": "matchzy", "fileName": "matchzy.json", "content": { "sha256": "old" } },
                                             { "plugin": "matchzy-original", "fileName": "matchzy.json", "content": { "sha256": "old" } } ] }
    ] })");
    auto hashes = ParseIndexHashes(index);
    CHECK(hashes["aa"] == std::set<std::string>{ "matchzy/matchzy.json" });
    CHECK(hashes["old"] == std::set<std::string>{ "matchzy/matchzy.json", "matchzy-original/matchzy.json" });
}

TEST_CASE("FindCandidates: explicit mapping wins, empty disables, unknown is refused", "[plugin-gamedata]")
{
    std::map<std::string, std::set<std::string>> hashes = { { "h", { "weaponpaints/weaponpaints.json" } } };

    std::string mapped = "matchzy-original/matchzy.json";
    auto c = FindCandidates("matchzy.json", "h", Feed(), hashes, &mapped, "");
    CHECK(c.match == Match::Explicit);
    CHECK(c.urls == std::vector<std::string>{ mapped });

    std::string off;
    c = FindCandidates("matchzy.json", "h", Feed(), hashes, &off, "");
    CHECK(c.urls.empty());
    CHECK(c.note == "disabled in PluginGamedataFeeds");

    std::string gone = "nope/nope.json";
    c = FindCandidates("matchzy.json", "h", Feed(), hashes, &gone, "");
    CHECK(c.urls.empty());
}

TEST_CASE("FindCandidates: a published hash beats the file name", "[plugin-gamedata]")
{
    std::map<std::string, std::set<std::string>> hashes = { { "h", { "matchzy-original/matchzy.json" } } };
    auto c = FindCandidates("matchzy.json", "H", Feed(), hashes, nullptr, "");
    CHECK(c.match == Match::Hash);
    CHECK(c.urls == std::vector<std::string>{ "matchzy-original/matchzy.json" });
}

TEST_CASE("FindCandidates: a hash published under two names is narrowed by the remembered feed", "[plugin-gamedata]")
{
    std::map<std::string, std::set<std::string>> hashes = { { "h", { "matchzy/matchzy.json", "matchzy-original/matchzy.json" } } };
    auto c = FindCandidates("matchzy.json", "h", Feed(), hashes, nullptr, "matchzy/matchzy.json");
    CHECK(c.urls == std::vector<std::string>{ "matchzy/matchzy.json" });

    c = FindCandidates("matchzy.json", "h", Feed(), hashes, nullptr, "");
    CHECK(c.urls.size() == 2);
}

TEST_CASE("FindCandidates: a hash only for plugins no longer published falls through", "[plugin-gamedata]")
{
    std::map<std::string, std::set<std::string>> hashes = { { "h", { "dropped/matchzy.json" } } };
    auto c = FindCandidates("matchzy.json", "h", Feed(), hashes, nullptr, "");
    CHECK(c.match == Match::Name);
    CHECK(c.urls.size() == 2);
}

TEST_CASE("FindCandidates: remembered feed beats the name; then names match case-insensitively", "[plugin-gamedata]")
{
    auto c = FindCandidates("matchzy.json", "x", Feed(), {}, nullptr, "matchzy-original/matchzy.json");
    CHECK(c.match == Match::Remembered);

    c = FindCandidates("WeaponPaints.json", "x", Feed(), {}, nullptr, "");
    CHECK(c.match == Match::Name);
    CHECK(c.urls == std::vector<std::string>{ "weaponpaints/weaponpaints.json" });

    c = FindCandidates("other.json", "x", Feed(), {}, nullptr, "");
    CHECK(c.urls.empty());
    CHECK(c.match == Match::None);
}

TEST_CASE("PickByKeyOverlap picks the feed file sharing most keys and refuses ties and strangers", "[plugin-gamedata]")
{
    auto local = ordered_json::parse(R"({ "_comment": {}, "A": {}, "B": {}, "C": {} })");
    auto two = ordered_json::parse(R"({ "A": {}, "B": {} })");
    auto one = ordered_json::parse(R"({ "C": {}, "Z": {} })");
    auto none = ordered_json::parse(R"({ "Z": {}, "_comment": {} })");

    CHECK(PickByKeyOverlap(local, { &one, &two }) == 1);
    CHECK(PickByKeyOverlap(local, { &one, &one }) == kOverlapTie);
    CHECK(PickByKeyOverlap(local, { &none }) == kOverlapNone);
    CHECK(PickByKeyOverlap(local, { &none, nullptr }) == kOverlapNone);
    CHECK(PickByKeyOverlap(local, { nullptr, &one }) == 1);
}

TEST_CASE("PatchBrokenSignatures replaces only broken signatures with resolving feed ones", "[plugin-gamedata]")
{
    auto local = ordered_json::parse(R"({
        "Works":     { "signatures": { "library": "server", "linux": "GOOD", "windows": "W1" } },
        "Broken":    { "signatures": { "library": "server", "linux": "OLD", "windows": "W2" }, "offsets": { "linux": 1 } },
        "NoLibrary": { "signatures": { "linux": "OLD2" } },
        "Unknown":   { "signatures": { "linux": "OLD3" } },
        "Same":      { "signatures": { "linux": "STILL" } },
        "BadFeed":   { "signatures": { "linux": "OLD4" } },
        "OtherLib":  { "signatures": { "library": "engine", "linux": "OLD5" } },
        "Offset":    { "offsets": { "linux": 5 } }
    })");
    auto feed = ordered_json::parse(R"({
        "Works":     { "signatures": { "library": "server", "linux": "DIFFERENT" } },
        "Broken":    { "signatures": { "library": "server", "linux": "NEW", "windows": "FEEDWIN" }, "offsets": { "linux": 99 } },
        "NoLibrary": { "signatures": { "library": "server", "linux": "NEW2" } },
        "Same":      { "signatures": { "linux": "STILL" } },
        "BadFeed":   { "signatures": { "linux": "ALSOBAD" } },
        "OtherLib":  { "signatures": { "library": "server", "linux": "NEW5" } },
        "Offset":    { "offsets": { "linux": 6 } }
    })");

    auto result = PatchBrokenSignatures(local, feed, "linux", ResolvesOnly({ "GOOD", "NEW", "NEW2", "NEW5", "DIFFERENT" }));

    CHECK(result.patched == std::vector<std::string>{ "Broken", "NoLibrary" });
    CHECK(result.unresolved.size() == 4); // Unknown, Same, BadFeed, OtherLib

    CHECK(local["Works"]["signatures"]["linux"] == "GOOD");
    CHECK(local["Broken"]["signatures"]["linux"] == "NEW");
    // Other platform and offsets are not ours to judge here.
    CHECK(local["Broken"]["signatures"]["windows"] == "W2");
    CHECK(local["Broken"]["offsets"]["linux"] == 1);
    CHECK(local["NoLibrary"]["signatures"]["linux"] == "NEW2");
    CHECK_FALSE(local["NoLibrary"]["signatures"].contains("library"));
    CHECK(local["OtherLib"]["signatures"]["linux"] == "OLD5");
    CHECK(local["Offset"]["offsets"]["linux"] == 5);
}

TEST_CASE("Serialize keeps key order, indentation, line endings and the trailing newline", "[plugin-gamedata]")
{
    const std::string original =
        "{\r\n  \"Z\": {\r\n    \"signatures\": {\r\n      \"linux\": \"OLD\"\r\n    }\r\n  },\r\n  \"A\": 1\r\n}\r\n";
    auto json = ordered_json::parse(original);
    json["Z"]["signatures"]["linux"] = "NEW";
    CHECK(Serialize(json, original) ==
          "{\r\n  \"Z\": {\r\n    \"signatures\": {\r\n      \"linux\": \"NEW\"\r\n    }\r\n  },\r\n  \"A\": 1\r\n}\r\n");

    const std::string tabs = "{\n\t\"A\": 1\n}";
    CHECK(Serialize(ordered_json::parse(tabs), tabs) == "{\n\t\"A\": 1\n}");

    CHECK(DetectIndent("{\"A\":1}").width == 4);
}
