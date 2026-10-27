// Gamedata auto-update mirrors (core/gamedata_mirrors.h): which URLs a core.json asks for, the
// per-mirror ETag record, the manifest sha256 lookup and the SHA-256 itself.

#include "catch_amalgamated.hpp"

#include "core/gamedata_mirrors.h"

using namespace counterstrikesharp::gamedata;
using nlohmann::json;

TEST_CASE("mirrors: an explicit list is used as given", "[gamedata_mirrors]")
{
    auto mirrors =
        ResolveMirrors(json::parse(R"({"AutoUpdateURLs":["https://a/latest/x/gamedata.json","","https://b/latest/x/gamedata.json"]})"));
    CHECK(mirrors == std::vector<std::string>{ "https://a/latest/x/gamedata.json", "https://b/latest/x/gamedata.json" });
}

TEST_CASE("mirrors: the list wins over AutoUpdateURL", "[gamedata_mirrors]")
{
    auto mirrors = ResolveMirrors(json::parse(R"({"AutoUpdateURL":"https://old/x","AutoUpdateURLs":["https://new/x"]})"));
    CHECK(mirrors == std::vector<std::string>{ "https://new/x" });
}

TEST_CASE("mirrors: a custom AutoUpdateURL is a list of one", "[gamedata_mirrors]")
{
    CHECK(ResolveMirrors(json::parse(R"({"AutoUpdateURL":"https://mine/gamedata.json"})")) ==
          std::vector<std::string>{ "https://mine/gamedata.json" });
}

TEST_CASE("mirrors: the old default URL, nothing, or an empty list mean the defaults", "[gamedata_mirrors]")
{
    CHECK(ResolveMirrors(json::parse(std::string(R"({"AutoUpdateURL":")") + kLegacyDefaultUrl + R"("})")) == DefaultMirrors());
    CHECK(ResolveMirrors(json::object()) == DefaultMirrors());
    CHECK(ResolveMirrors(json::parse(R"({"AutoUpdateURLs":[]})")) == DefaultMirrors());
    CHECK(ResolveMirrors(json::parse(R"({"AutoUpdateURLs":"https://not-a-list"})")) == DefaultMirrors());
}

TEST_CASE("etag record: an ETag is only sent to the mirror that issued it", "[gamedata_mirrors]")
{
    auto record = ParseEtagRecord(FormatEtagRecord({ "\"abc\"", "https://b/x" }));
    CHECK(record.etag == "\"abc\"");
    CHECK(record.source == "https://b/x");
    CHECK(EtagFor(record, "https://b/x", "https://a/x") == "\"abc\"");
    CHECK(EtagFor(record, "https://a/x", "https://a/x").empty());
}

TEST_CASE("etag record: a file from before the mirror list belongs to the first mirror", "[gamedata_mirrors]")
{
    auto record = ParseEtagRecord("\"abc\"\r\n");
    CHECK(record.etag == "\"abc\"");
    CHECK(record.source.empty());
    CHECK(EtagFor(record, "https://a/x", "https://a/x") == "\"abc\"");
    CHECK(EtagFor(record, "https://b/x", "https://a/x").empty());
}

TEST_CASE("manifest: sha256 is looked up by the path after /latest/", "[gamedata_mirrors]")
{
    auto manifest = json::parse(R"({"gameVersion":"14192","files":[
        {"url":"cleanercs2/gamedata.json","sha256":"11"},
        {"url":"CounterStrikeSharp/gamedata.json","sha256":"ABCDEF"}]})");
    CHECK(ManifestSha256For(manifest, "https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json") == "abcdef");
    CHECK(ManifestSha256For(manifest, "https://mirror/latest/Other/gamedata.json").empty());
    CHECK(ManifestSha256For(manifest, "https://mirror/gamedata.json").empty());
    CHECK(ManifestSha256For(json::parse(R"({"gameVersion":"14192"})"), "https://a/latest/CounterStrikeSharp/gamedata.json").empty());
}

TEST_CASE("sha256 matches the FIPS 180-4 test vectors", "[gamedata_mirrors]")
{
    CHECK(Sha256Hex("") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    CHECK(Sha256Hex("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    CHECK(Sha256Hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq") ==
          "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1");
    CHECK(Sha256Hex(std::string(1000000, 'a')) == "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0");
}
