// Gamedata manifest signature check (core/gamedata_signature.h) and the crash-loop guard for
// gamedata updates (core/gamedata_update_guard.h).

#include "catch_amalgamated.hpp"

#include "core/gamedata_signature.h"
#include "core/gamedata_update_guard.h"

#include <filesystem>
#include <fstream>
#include <iterator>
#include <random>

using namespace counterstrikesharp::update;

namespace {

namespace fs = std::filesystem;

struct TempDir
{
    fs::path path;
    TempDir()
    {
        std::random_device random;
        path = fs::temp_directory_path() / ("css-guard-" + std::to_string(random()));
        fs::create_directories(path);
    }
    ~TempDir()
    {
        std::error_code ec;
        fs::remove_all(path, ec);
    }
    std::string operator/(const std::string& name) const { return (path / name).string(); }
};

void Put(const std::string& path, const std::string& text) { std::ofstream(path, std::ios::binary) << text; }

std::string Get(const std::string& path)
{
    std::ifstream in(path, std::ios::binary);
    return { std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>() };
}

} // namespace

TEST_CASE("Base64Decode reads RFC 4648 and refuses garbage", "[gamedata-signing]")
{
    CHECK(signing::Base64Decode("Zm9vYmFy") == std::string("foobar"));
    CHECK(signing::Base64Decode("Zm9vYg==\n") == std::string("foob"));
    CHECK(signing::Base64Decode("Zm9vYg") == std::string("foob"));
    CHECK(signing::Base64Decode("") == std::string());
    CHECK_FALSE(signing::Base64Decode("Zm9v!mFy"));
    CHECK_FALSE(signing::Base64Decode("Zm9v=YmFy"));
    CHECK_FALSE(signing::Base64Decode("Z"));
}

TEST_CASE("VerifySignature accepts the RFC 8032 test vector and rejects any change", "[gamedata-signing]")
{
    const std::string key = "11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=";
    const std::string signature = "5VZDAMNgrHKQhuLMgG6CioSHfx645dl02HPgZSJJAVVfuIIVkKM7rMYeOXAc+bRr0lv18FlbviRlUUFDjnoQCw==";
    std::string error;
    CHECK(signing::VerifySignature("", signature, key, error));
    CHECK_FALSE(signing::VerifySignature("x", signature, key, error));
    CHECK(error == "the signature does not verify");

    auto flipped = signature;
    flipped[5] = flipped[5] == 'A' ? 'B' : 'A';
    CHECK_FALSE(signing::VerifySignature("", flipped, key, error));
    CHECK_FALSE(signing::VerifySignature("", "c2hvcnQ=", key, error));
    CHECK(error == "the signature is not 64 bytes of base64");
    CHECK_FALSE(signing::VerifySignature("", signature, "c2hvcnQ=", error));
    CHECK(error == "the signing public key is not 32 bytes of base64");
}

TEST_CASE("VerifySignature accepts a signature made with VibeSignatures' key", "[gamedata-signing]")
{
    // openssl pkeyutl -sign -rawin with the Pages GAMEDATA_SIGNING_KEY: the same Ed25519 that
    // Node's crypto.sign(null, ...) produces for latest/manifest.json.sig.
    const std::string message = "gamedata manifest test vector\n";
    const std::string signature = "wjVq6ZZmxaIxJjmjsuKzNkejCOQfz9exaegjy6u+9r7jVTQ8KaKcF6C8pdQDpdDVf1nwLcnXKW9pKQv3C2d0CQ==\n";
    std::string error;
    CHECK(signing::VerifySignature(message, signature, signing::kDefaultPublicKey, error));
    CHECK_FALSE(signing::VerifySignature(message + " ", signature, signing::kDefaultPublicKey, error));
}

TEST_CASE("VerifySignatureWithAnyKey accepts the reserve key and refuses keys that did not sign", "[gamedata-signing]")
{
    const std::string message = "gamedata manifest test vector\n";
    // Made with the offline reserve key (kReservePublicKey), which never signs the feed.
    const std::string byReserve = "wetjfU84IGBkeH5Xe+CHZsnKhXVpI2AwOD8gW8hEoveFrmvepIbfzJGAsvlVFC4wCQihIX0JwYmL8Spdh5pFAA==";
    const std::string byPrimary = "wjVq6ZZmxaIxJjmjsuKzNkejCOQfz9exaegjy6u+9r7jVTQ8KaKcF6C8pdQDpdDVf1nwLcnXKW9pKQv3C2d0CQ==";
    std::string error;
    CHECK(signing::VerifySignatureWithAnyKey(message, byPrimary, signing::kDefaultPublicKeys, error));
    CHECK(signing::VerifySignatureWithAnyKey(message, byReserve, signing::kDefaultPublicKeys, error));
    CHECK(signing::VerifySignatureWithAnyKey(message, byReserve, std::string(",") + signing::kReservePublicKey + ",", error));
    CHECK_FALSE(signing::VerifySignatureWithAnyKey(message, byReserve, signing::kDefaultPublicKey, error));
    CHECK_FALSE(signing::VerifySignatureWithAnyKey(message + "x", byPrimary, signing::kDefaultPublicKeys, error));
    CHECK(error == "the signature does not verify (with any of 2 keys)");
    CHECK_FALSE(signing::VerifySignatureWithAnyKey(message, byPrimary, " , ", error));
    CHECK(error == "no signing public key configured");
}

TEST_CASE("Guard: nothing pending is a clean start", "[gamedata-guard]")
{
    TempDir dir;
    auto check = guard::CheckPreviousStart(dir.path.string());
    CHECK(check.outcome == guard::Outcome::Clean);
    CHECK_FALSE(guard::Confirm(dir.path.string()));
}

TEST_CASE("Guard: a confirmed update is kept", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    Put(dir / "gamedata.json", "NEW");
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup.json", "aa" }, error));
    CHECK(guard::Confirm(dir.path.string()));
    CHECK(guard::CheckPreviousStart(dir.path.string()).outcome == guard::Outcome::Clean);
    CHECK(Get(dir / "gamedata.json") == "NEW");
}

TEST_CASE("Guard: one early death retries, the second rolls back and rejects", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    Put(dir / "gamedata.json", "NEW");
    Put(dir / "backup.json", "OLD");
    Put(dir / "plugin.json", "PATCHED");
    Put(dir / "plugin.bak", "BROKEN");
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup.json", "aa" }, error));
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "plugin.json", dir / "plugin.bak", "bb" }, error));
    // Written twice before a confirmation: the newest hash, but the first (pre-update) backup.
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup2.json", "cc" }, error));
    Put(dir / "new.json", "CREATED");
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "new.json", "", "dd" }, error));

    auto first = guard::CheckPreviousStart(dir.path.string());
    CHECK(first.outcome == guard::Outcome::Retry);
    CHECK(first.earlyDeaths == 1);
    CHECK(Get(dir / "gamedata.json") == "NEW");

    auto second = guard::CheckPreviousStart(dir.path.string());
    CHECK(second.outcome == guard::Outcome::RolledBack);
    CHECK(second.errors.empty());
    CHECK(second.restored.size() == 3);
    CHECK(Get(dir / "gamedata.json") == "OLD");
    CHECK(Get(dir / "plugin.json") == "BROKEN");
    CHECK_FALSE(fs::exists(dir / "new.json"));
    CHECK(guard::Rejected(dir.path.string()) == std::set<std::string>{ "bb", "cc", "dd" });

    // Rolled back for good: the next start is clean, and the rejections stay.
    CHECK(guard::CheckPreviousStart(dir.path.string()).outcome == guard::Outcome::Clean);
    CHECK(guard::Rejected(dir.path.string()).size() == 3);
}

TEST_CASE("Guard: a vanished backup leaves the file alone and says so", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    Put(dir / "gamedata.json", "NEW");
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "gone.json", "aa" }, error));
    guard::CheckPreviousStart(dir.path.string());
    auto check = guard::CheckPreviousStart(dir.path.string());
    CHECK(check.outcome == guard::Outcome::RolledBack);
    CHECK(check.restored.empty());
    CHECK(check.errors.size() == 1);
    CHECK(Get(dir / "gamedata.json") == "NEW");
    CHECK(guard::Rejected(dir.path.string()).empty());
}
