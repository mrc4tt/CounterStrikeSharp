// Upload of unknown plugin gamedata files (core/gamedata_upload.h) and the "patched" block of
// logs/gamedata-status.json (core/gamedata_update_status.h): the state kept between starts, when
// a file is (re-)uploaded, which hosts its read link may come from, and the status JSON shape.

#include "catch_amalgamated.hpp"

#include "core/gamedata_update_status.h"
#include "core/gamedata_upload.h"

using namespace counterstrikesharp::update;

namespace {

const std::string kId = "AbCdEfGhIjKlMnOpQrStUv"; // 22 url-safe characters

upload::Record Uploaded()
{
    upload::Record record;
    record.id = kId;
    record.filename = "custom.json";
    record.manage = "token";
    record.sha256 = "uploaded";
    return record;
}

} // namespace

TEST_CASE("Upload state round-trips and drops entries that could not be read back", "[gamedata-upload]")
{
    auto record = Uploaded();
    record.written = "patched";
    record.build = "14196";
    record.at = 1793966720;
    std::map<std::string, upload::Record> records = { { "gamedata/custom.json", record } };

    auto json = upload::SerializeState(records);
    json["gamedata/bad-id.json"] = { { "id", "../../etc" }, { "filename", "x.json" } };
    json["gamedata/bad-name.json"] = { { "id", kId }, { "filename", "a/b.json" } };
    json["gamedata/not-an-object.json"] = "x";

    auto parsed = upload::ParseState(json);
    REQUIRE(parsed.size() == 1);
    const auto& back = parsed.at("gamedata/custom.json");
    CHECK(back.id == kId);
    CHECK(back.filename == "custom.json");
    CHECK(back.manage == "token");
    CHECK(back.sha256 == "uploaded");
    CHECK(back.written == "patched");
    CHECK(back.build == "14196");
    CHECK(back.at == 1793966720);

    CHECK(upload::ParseState(nlohmann::json()).empty());
    CHECK(upload::ParseState(nlohmann::json::array()).empty());
}

TEST_CASE("Decide: upload once, re-send only what changed outside the updater", "[gamedata-upload]")
{
    auto record = Uploaded();
    CHECK(upload::Decide(nullptr, "anything") == upload::Action::Create);
    CHECK(upload::Decide(&record, "uploaded") == upload::Action::None);
    // Changed (a new plugin release): replaced under the same links while there is a token.
    CHECK(upload::Decide(&record, "new release") == upload::Action::Replace);

    // The plugin updater's own write from the upload is not a change.
    record.written = "patched";
    CHECK(upload::Decide(&record, "patched") == upload::Action::None);

    // No token: a new upload instead.
    record.manage.clear();
    CHECK(upload::Decide(&record, "new release") == upload::Action::Create);
}

TEST_CASE("ServiceOrigin: https only, host of GamedataReportURL", "[gamedata-upload]")
{
    std::string error;
    CHECK(upload::ServiceOrigin("https://sig-api.miksen.me/api/report", error) == "https://sig-api.miksen.me");
    CHECK(upload::ServiceOrigin("HTTPS://host:8443/api/report?x=1", error) == "https://host:8443");
    CHECK(upload::ServiceOrigin("https://host", error) == "https://host");

    CHECK_FALSE(upload::ServiceOrigin("http://sig-api.miksen.me/api/report", error));
    CHECK(error.find("https only") != std::string::npos);
    CHECK_FALSE(upload::ServiceOrigin("https://user@evil/api/report", error));
    CHECK_FALSE(upload::ServiceOrigin("https:///api/report", error));
    CHECK_FALSE(upload::ServiceOrigin("ftp://host/api/report", error));
}

TEST_CASE("The read link is built from the configured host, never from the answer", "[gamedata-upload]")
{
    const auto record = Uploaded();
    CHECK(upload::CreateUrl("https://h") == "https://h/api/files");
    CHECK(upload::ManageUrl("https://h") == "https://h/api/files/manage");
    CHECK(upload::ReadUrl("https://h", record) == "https://h/api/files/" + kId + "/custom.json");
}

TEST_CASE("ParseAnswer accepts the service's answers and nothing else", "[gamedata-upload]")
{
    std::string error;

    SECTION("POST /api/files (201)")
    {
        auto answer = nlohmann::json{ { "id", kId },
                                      { "manage", "secret" },
                                      { "filename", "custom.json" },
                                      { "url", "/api/files/" + kId + "/custom.json" },
                                      { "build", "14196" } };
        auto record = upload::ParseAnswer(answer, nullptr, error);
        REQUIRE(record);
        CHECK(record->id == kId);
        CHECK(record->manage == "secret");
        CHECK(record->filename == "custom.json");
    }
    SECTION("PUT /api/files/manage (200) keeps the id and token, takes the new file name")
    {
        auto existing = Uploaded();
        auto answer =
            nlohmann::json{ { "filename", "renamed.json" }, { "url", "/api/files/" + kId + "/renamed.json" }, { "current", "{}" } };
        auto record = upload::ParseAnswer(answer, &existing, error);
        REQUIRE(record);
        CHECK(record->id == kId);
        CHECK(record->manage == "token");
        CHECK(record->filename == "renamed.json");
    }
    SECTION("a url that points elsewhere")
    {
        auto answer =
            nlohmann::json{ { "id", kId }, { "filename", "custom.json" }, { "url", "https://elsewhere/api/files/x/custom.json" } };
        CHECK_FALSE(upload::ParseAnswer(answer, nullptr, error));
    }
    SECTION("an id or file name that is not the service's alphabet")
    {
        CHECK_FALSE(upload::ParseAnswer({ { "id", "short" }, { "filename", "custom.json" }, { "url", "/api/files/short/custom.json" } },
                                        nullptr, error));
        CHECK_FALSE(upload::ParseAnswer({ { "id", kId }, { "filename", ".." }, { "url", "/api/files/" + kId + "/.." } }, nullptr, error));
        CHECK_FALSE(upload::ParseAnswer(nlohmann::json::array(), nullptr, error));
    }
}

TEST_CASE("UploadBody carries the text, the name and the build when there is one", "[gamedata-upload]")
{
    auto body = upload::UploadBody("{ \"A\": {} }", "custom.json", "14196");
    CHECK(body["gamedata"] == "{ \"A\": {} }");
    CHECK(body["filename"] == "custom.json");
    CHECK(body["build"] == "14196");
    CHECK_FALSE(upload::UploadBody("{}", "custom.json", "").contains("build"));
}

TEST_CASE("The status 'patched' block lists the core outcome and each file written", "[gamedata-status]")
{
    status::StartRecord record;
    record.core.result = "updated";
    record.core.feed = "https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json";
    record.core.build = "14196";
    record.core.sha256 = "abc";

    status::FilePatch signatures;
    signatures.file = "gamedata/matchzy.json";
    signatures.feed = "matchzy/matchzy.json";
    signatures.build = "14196";
    signatures.matchedBy = "file name";
    signatures.platform = "linux";
    signatures.keys = status::SignatureChanges({ "A", "B" }, "linux");
    record.files.push_back(signatures);

    status::FilePatch whole;
    whole.file = "gamedata/weaponpaints.json";
    whole.mode = "file";
    whole.feed = "weaponpaints/weaponpaints.json";
    whole.build = "14196";
    whole.fromBuild = "14195";
    whole.matchedBy = "published file hash";
    whole.platform = "linux";
    whole.keys = { { "Off", { "offsets.linux", "offsets.windows" } } };
    record.files.push_back(whole);

    status::FilePatch uploaded;
    uploaded.file = "gamedata/custom.json";
    uploaded.source = "upload";
    uploaded.feed = "https://sig-api.miksen.me/api/files/" + kId + "/custom.json";
    uploaded.matchedBy = "upload";
    uploaded.platform = "linux";
    uploaded.keys = status::SignatureChanges({ "C" }, "linux");
    record.files.push_back(uploaded);
    record.rolledBack = { "gamedata/old.json" };

    auto json = status::ToJson(record);
    CHECK(json["core"]["file"] == "gamedata/gamedata.json");
    CHECK(json["core"]["result"] == "updated");
    CHECK(json["core"]["build"] == "14196");
    CHECK(json["core"]["sha256"] == "abc");
    CHECK_FALSE(json["core"].contains("detail"));

    REQUIRE(json["files"].size() == 3);
    const auto& first = json["files"][0];
    CHECK(first["file"] == "gamedata/matchzy.json");
    CHECK(first["source"] == "feed");
    CHECK(first["mode"] == "signatures");
    CHECK(first["platform"] == "linux");
    CHECK(first["keys"][0]["name"] == "A");
    CHECK(first["keys"][0]["changed"] == nlohmann::json::array({ "signatures.linux" }));
    CHECK(first["keys"][1]["name"] == "B");
    CHECK_FALSE(first.contains("fromBuild"));

    const auto& second = json["files"][1];
    CHECK(second["mode"] == "file");
    CHECK(second["fromBuild"] == "14195");
    CHECK(second["keys"][0]["changed"] == nlohmann::json::array({ "offsets.linux", "offsets.windows" }));

    const auto& third = json["files"][2];
    CHECK(third["source"] == "upload");
    CHECK(third["matchedBy"] == "upload");
    CHECK_FALSE(third.contains("build"));

    CHECK(json["rolledBack"] == nlohmann::json::array({ "gamedata/old.json" }));
}

TEST_CASE("A start that updated nothing still says why", "[gamedata-status]")
{
    status::StartRecord record;
    record.core.detail = "AutoUpdateEnabled is off";
    auto json = status::ToJson(record);
    CHECK(json["core"]["result"] == "disabled");
    CHECK(json["core"]["detail"] == "AutoUpdateEnabled is off");
    CHECK(json["files"].empty());
    CHECK(json["rolledBack"].empty());
}
