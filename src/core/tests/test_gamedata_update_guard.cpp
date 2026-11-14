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
    auto check = guard::CheckPreviousStart(dir.path.string(), "linux");
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
    CHECK(guard::CheckPreviousStart(dir.path.string(), "linux").outcome == guard::Outcome::Clean);
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

    auto first = guard::CheckPreviousStart(dir.path.string(), "linux");
    CHECK(first.outcome == guard::Outcome::Retry);
    CHECK(first.earlyDeaths == 1);
    CHECK(Get(dir / "gamedata.json") == "NEW");

    auto second = guard::CheckPreviousStart(dir.path.string(), "linux");
    CHECK(second.outcome == guard::Outcome::RolledBack);
    CHECK(second.errors.empty());
    CHECK(second.restored.size() == 3);
    CHECK(Get(dir / "gamedata.json") == "OLD");
    CHECK(Get(dir / "plugin.json") == "BROKEN");
    CHECK_FALSE(fs::exists(dir / "new.json"));
    CHECK(guard::Rejected(dir.path.string()) == std::set<std::string>{ "bb", "cc", "dd" });

    // Rolled back for good: the next start is clean, and the rejections stay.
    CHECK(guard::CheckPreviousStart(dir.path.string(), "linux").outcome == guard::Outcome::Clean);
    CHECK(guard::Rejected(dir.path.string()).size() == 3);
}

TEST_CASE("Guard: a vanished backup leaves the file alone and says so", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    Put(dir / "gamedata.json", "NEW");
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "gone.json", "aa" }, error));
    guard::CheckPreviousStart(dir.path.string(), "linux");
    auto check = guard::CheckPreviousStart(dir.path.string(), "linux");
    CHECK(check.outcome == guard::Outcome::RolledBack);
    CHECK(check.restored.empty());
    CHECK(check.errors.size() == 1);
    CHECK(Get(dir / "gamedata.json") == "NEW");
    CHECK(guard::Rejected(dir.path.string()).empty());
}

namespace {

const char* kOld = R"({ "A": { "signatures": { "library": "server", "linux": "AA BB", "windows": "CC" } },
  "B": { "offsets": { "linux": 1, "windows": 2 } }, "Gone": { "offsets": { "linux": 3 } } })";

} // namespace

TEST_CASE("Guard: SuspectKeys ignores the other platform and removed keys", "[gamedata-guard]")
{
    const char* windowsOnly = R"({ "A": { "signatures": { "library": "server", "linux": "AA BB", "windows": "DD" } },
      "B": { "offsets": { "linux": 1, "windows": 5 } } })";
    CHECK(guard::SuspectKeys(kOld, windowsOnly, "linux").empty());
    CHECK(guard::SuspectKeys(kOld, windowsOnly, "windows") == std::vector<std::string>{ "A (signatures.windows)", "B (offsets.windows)" });

    const char* linuxChanged = R"({ "A": { "signatures": { "library": "server", "linux": "AA ??", "windows": "CC" } },
      "B": { "offsets": { "linux": 9, "windows": 2 } }, "New": { "offsets": { "linux": 4 } } })";
    CHECK(guard::SuspectKeys(kOld, linuxChanged, "linux") ==
          std::vector<std::string>{ "A (signatures.linux)", "B (offsets.linux)", "New (added)" });

    // No usable backup: every key is a suspect.
    CHECK(guard::SuspectKeys("", R"({ "A": 1 })", "linux") == std::vector<std::string>{ "A (added)" });
    CHECK(guard::SuspectKeys(kOld, "garbage", "linux").size() == 1);
}

TEST_CASE("Guard: a stop signal does not count as a death", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    Put(dir / "gamedata.json", "NEW");
    Put(dir / "backup.json", "OLD");
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup.json", "aa" }, error));
    guard::NoteProgress(dir.path.string(), 40, "de_inferno");

    Put(dir / guard::kStoppedMarker, "SIGTERM\n");
    auto stopped = guard::CheckPreviousStart(dir.path.string(), "linux");
    CHECK(stopped.outcome == guard::Outcome::Stopped);
    CHECK(stopped.stopSignal == "SIGTERM");
    CHECK(stopped.lastUptime == 40);
    CHECK(stopped.lastMap == "de_inferno");
    CHECK_FALSE(fs::exists(dir / guard::kStoppedMarker));

    // Stopped twice, then one real death: still only a retry.
    Put(dir / guard::kStoppedMarker, "SIGINT\n");
    CHECK(guard::CheckPreviousStart(dir.path.string(), "linux").outcome == guard::Outcome::Stopped);
    auto first = guard::CheckPreviousStart(dir.path.string(), "linux");
    CHECK(first.outcome == guard::Outcome::Retry);
    CHECK(first.lastUptime == -1); // progress belongs to one start
    CHECK(Get(dir / "gamedata.json") == "NEW");

    // A stale marker without anything pending is cleaned up.
    REQUIRE(guard::Confirm(dir.path.string()));
    Put(dir / guard::kStoppedMarker, "SIGTERM\n");
    CHECK(guard::CheckPreviousStart(dir.path.string(), "linux").outcome == guard::Outcome::Clean);
    CHECK_FALSE(fs::exists(dir / guard::kStoppedMarker));
}

TEST_CASE("Guard: an update that changed nothing for this platform is kept", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    const char* windowsOnly = R"({ "A": { "signatures": { "library": "server", "linux": "AA BB", "windows": "DD" } },
      "B": { "offsets": { "linux": 1, "windows": 2 } } })";
    Put(dir / "gamedata.json", windowsOnly);
    Put(dir / "backup.json", kOld);
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup.json", "aa" }, error));
    guard::CheckPreviousStart(dir.path.string(), "linux");
    auto check = guard::CheckPreviousStart(dir.path.string(), "linux");
    CHECK(check.outcome == guard::Outcome::Innocent);
    CHECK(check.kept == std::vector<std::string>{ dir / "gamedata.json" });
    CHECK(Get(dir / "gamedata.json") == windowsOnly);
    CHECK(guard::Rejected(dir.path.string()).empty());
    CHECK_FALSE(fs::exists(dir / "update.rejected"));
}

TEST_CASE("Guard: rollback names the suspects, rejections expire and grow", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    const char* changed = R"({ "A": { "signatures": { "library": "server", "linux": "AA ??", "windows": "CC" } },
      "B": { "offsets": { "linux": 1, "windows": 2 } } })";
    const std::time_t t0 = 1'800'000'000;

    auto rollBack = [&](std::time_t now) {
        Put(dir / "gamedata.json", changed);
        Put(dir / "backup.json", kOld);
        REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup.json", "aa" }, error));
        guard::CheckPreviousStart(dir.path.string(), "linux", now);
        return guard::CheckPreviousStart(dir.path.string(), "linux", now);
    };

    auto check = rollBack(t0);
    CHECK(check.outcome == guard::Outcome::RolledBack);
    REQUIRE(check.suspects.size() == 1);
    CHECK(check.suspects[0].keys == std::vector<std::string>{ "A (signatures.linux)" });
    CHECK(Get(dir / "gamedata.json") == kOld);
    CHECK(check.rejectedUntil == t0 + guard::kRejectSeconds[0]);
    CHECK(guard::Rejected(dir.path.string(), t0 + 60) == std::set<std::string>{ "aa" });
    CHECK(guard::RejectedUntil(dir.path.string(), "aa") == t0 + guard::kRejectSeconds[0]);
    // Expired: tried again, but remembered.
    CHECK(guard::Rejected(dir.path.string(), t0 + guard::kRejectSeconds[0]).empty());
    CHECK(fs::exists(dir / "update.rejected"));

    // The same file kills it again: a longer rejection, then the longest one repeats.
    const auto t1 = t0 + guard::kRejectSeconds[0] + 10;
    CHECK(rollBack(t1).rejectedUntil == t1 + guard::kRejectSeconds[1]);
    const auto t2 = t1 + guard::kRejectSeconds[1] + 10;
    CHECK(rollBack(t2).rejectedUntil == t2 + guard::kRejectSeconds[2]);
    const auto t3 = t2 + guard::kRejectSeconds[2] + 10;
    CHECK(rollBack(t3).rejectedUntil == t3 + guard::kRejectSeconds[2]);

    // Forgotten after a while: update.rejected goes away.
    CHECK(guard::Rejected(dir.path.string(), t3 + guard::kRejectSeconds[2] + guard::kRejectMemorySeconds).empty());
    CHECK_FALSE(fs::exists(dir / "update.rejected"));
}

TEST_CASE("Guard: a rejected file that survives a start is unrejected", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    Put(dir / "gamedata.json", "NEW");
    Put(dir / "backup.json", "OLD");
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup.json", "aa" }, error));
    guard::CheckPreviousStart(dir.path.string(), "linux");
    REQUIRE(guard::CheckPreviousStart(dir.path.string(), "linux").outcome == guard::Outcome::RolledBack);
    REQUIRE(fs::exists(dir / "update.rejected"));

    // Tried again (expired, or by hand) and this time it lives.
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", dir / "backup.json", "aa" }, error));
    CHECK(guard::Confirm(dir.path.string()));
    CHECK_FALSE(fs::exists(dir / "update.rejected"));
}

TEST_CASE("Guard: a rejection from before expiry existed gets one", "[gamedata-guard]")
{
    TempDir dir;
    Put(dir / "update.rejected", R"({ "files": [ { "sha256": "aa", "path": "x", "rejected": "2026-11-11T20:31:47Z" } ] })");
    const std::time_t now = 1'800'000'000;
    CHECK(guard::Rejected(dir.path.string(), now) == std::set<std::string>{ "aa" });
    CHECK(guard::RejectedUntil(dir.path.string(), "aa") == now + guard::kRejectSeconds[0]);
    CHECK(guard::Rejected(dir.path.string(), now + guard::kRejectSeconds[0]).empty());
}

TEST_CASE("Guard: Disable forgets everything", "[gamedata-guard]")
{
    TempDir dir;
    std::string error;
    REQUIRE(guard::RecordPending(dir.path.string(), { dir / "gamedata.json", "", "aa" }, error));
    Put(dir / "update.rejected", R"({ "files": [ { "sha256": "bb" } ] })");
    Put(dir / guard::kStoppedMarker, "SIGTERM\n");
    guard::Disable(dir.path.string());
    CHECK_FALSE(fs::exists(dir / "update.pending"));
    CHECK_FALSE(fs::exists(dir / "update.rejected"));
    CHECK_FALSE(fs::exists(dir / guard::kStoppedMarker));
}
