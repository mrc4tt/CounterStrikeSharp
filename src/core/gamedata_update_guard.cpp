#include "core/gamedata_update_guard.h"

#include <algorithm>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <mutex>

#include <nlohmann/json.hpp>

#include "core/gamedata_plugin_update.h"

namespace counterstrikesharp::update::guard {

namespace {

namespace fs = std::filesystem;
using json = nlohmann::json;

std::string PendingPath(const std::string& directory) { return (fs::path(directory) / "update.pending").string(); }
std::string RejectedPath(const std::string& directory) { return (fs::path(directory) / "update.rejected").string(); }
std::string StoppedPath(const std::string& directory) { return (fs::path(directory) / kStoppedMarker).string(); }

// update.pending is written from the game thread (updates, map start) and from the confirm timer
// thread (uptime); every read-modify-write of it, and of update.rejected, holds this.
std::mutex g_fileMutex;

std::string ReadAll(const std::string& path)
{
    std::ifstream in(path, std::ios::binary);
    return { std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>() };
}

json ReadObject(const std::string& path)
{
    auto parsed = json::parse(ReadAll(path), nullptr, false);
    return parsed.is_object() ? parsed : json::object();
}

bool WriteAtomic(const std::string& path, const std::string& contents, std::string& error)
{
    const auto tmp = path + ".tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out.is_open())
        {
            error = "cannot open " + tmp;
            return false;
        }
        out << contents;
        out.close();
        if (!out)
        {
            error = "write failed for " + tmp;
            std::error_code ignored;
            fs::remove(tmp, ignored);
            return false;
        }
    }
    std::error_code ec;
    fs::rename(tmp, path, ec);
    if (ec)
    {
        error = "rename to " + path + " failed: " + ec.message();
        fs::remove(tmp, ec);
        return false;
    }
    return true;
}

std::string UtcString(std::time_t now)
{
    char stamp[32];
    std::tm utc{};
#ifdef _WIN32
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    std::strftime(stamp, sizeof(stamp), "%Y-%m-%dT%H:%M:%SZ", &utc);
    return stamp;
}

std::string UtcNow() { return UtcString(std::time(nullptr)); }

json FilesOf(json& object)
{
    if (!object.contains("files") || !object["files"].is_array()) object["files"] = json::array();
    return object["files"];
}

// Writes update.rejected, or deletes it when nothing is left in it.
void WriteRejected(const std::string& directory, const json& rejected, std::vector<std::string>* errors)
{
    std::error_code ec;
    if (!rejected.contains("files") || !rejected["files"].is_array() || rejected["files"].empty())
    {
        fs::remove(RejectedPath(directory), ec);
        return;
    }
    std::string error;
    if (!WriteAtomic(RejectedPath(directory), rejected.dump(2) + "\n", error) && errors) errors->push_back(error);
}

// Drops rejections of these hashes (the files survived a start after all).
void Unreject(const std::string& directory, const std::set<std::string>& hashes)
{
    std::error_code ec;
    if (hashes.empty() || !fs::exists(RejectedPath(directory), ec)) return;
    auto rejected = ReadObject(RejectedPath(directory));
    auto files = FilesOf(rejected);
    json keep = json::array();
    for (const auto& entry : files)
        if (!entry.is_object() || !hashes.count(entry.value("sha256", ""))) keep.push_back(entry);
    if (keep.size() == files.size()) return;
    rejected["files"] = keep;
    WriteRejected(directory, rejected, nullptr);
}

std::time_t RejectSecondsFor(int strikes)
{
    constexpr auto count = sizeof(kRejectSeconds) / sizeof(kRejectSeconds[0]);
    const auto index = strikes < 1 ? 0 : static_cast<size_t>(strikes - 1);
    return kRejectSeconds[index < count ? index : count - 1];
}

} // namespace

bool RecordPending(const std::string& directory, const PendingFile& file, std::string& error)
{
    std::lock_guard lock(g_fileMutex);
    auto pending = ReadObject(PendingPath(directory));
    FilesOf(pending);
    if (!pending.contains("earlyDeaths")) pending["earlyDeaths"] = 0;
    if (!pending.contains("since")) pending["since"] = UtcNow();

    bool found = false;
    for (auto& entry : pending["files"])
    {
        if (!entry.is_object() || entry.value("path", "") != file.path) continue;
        entry["sha256"] = file.sha256; // newest contents; the backup stays the pre-update one
        found = true;
    }
    if (!found) pending["files"].push_back({ { "path", file.path }, { "backup", file.backup }, { "sha256", file.sha256 } });

    return WriteAtomic(PendingPath(directory), pending.dump(2) + "\n", error);
}

void NoteProgress(const std::string& directory, int uptimeSeconds, const std::string& map)
{
    std::lock_guard lock(g_fileMutex);
    std::error_code ec;
    if (!fs::exists(PendingPath(directory), ec)) return;
    auto pending = ReadObject(PendingPath(directory));
    if (uptimeSeconds >= 0) pending["uptime"] = uptimeSeconds;
    if (!map.empty()) pending["map"] = map;
    std::string error;
    WriteAtomic(PendingPath(directory), pending.dump(2) + "\n", error);
}

std::vector<std::string> SuspectKeys(const std::string& before, const std::string& after, const std::string& platform)
{
    using plugin_gamedata::ordered_json;
    const auto other = platform == "windows" ? std::string("linux") : std::string("windows");
    auto oldJson = ordered_json::parse(before, nullptr, false);
    auto newJson = ordered_json::parse(after, nullptr, false);
    if (newJson.is_discarded() || !newJson.is_object()) return { "(the file is not a JSON object)" };
    if (oldJson.is_discarded() || !oldJson.is_object()) oldJson = ordered_json::object();

    std::vector<std::string> out;
    for (const auto& change : plugin_gamedata::DiffEntries(oldJson, newJson))
    {
        std::string fields;
        for (const auto& field : change.changed)
        {
            // A removed key makes a plugin throw a managed exception, not crash the server; the
            // other platform's entries are never read here.
            if (field == "removed" || field == "signatures." + other || field == "offsets." + other) continue;
            fields += (fields.empty() ? "" : ", ") + field;
        }
        if (!fields.empty()) out.push_back(change.name + " (" + fields + ")");
    }
    return out;
}

BootCheck CheckPreviousStart(const std::string& directory, const std::string& platform, std::time_t now)
{
    std::lock_guard lock(g_fileMutex);
    BootCheck check;
    const auto pendingPath = PendingPath(directory);
    const auto stoppedPath = StoppedPath(directory);
    std::error_code ec;
    const bool stopped = fs::exists(stoppedPath, ec);
    if (stopped) check.stopSignal = ReadAll(stoppedPath);
    // Left by a stopped start whose update had been confirmed, or by this one: stale either way.
    fs::remove(stoppedPath, ec);
    while (!check.stopSignal.empty() && (check.stopSignal.back() == '\n' || check.stopSignal.back() == '\r'))
        check.stopSignal.pop_back();

    if (!fs::exists(pendingPath, ec)) return check;

    auto pending = ReadObject(pendingPath);
    check.lastUptime = pending.contains("uptime") && pending["uptime"].is_number_integer() ? pending["uptime"].get<int>() : -1;
    check.lastMap = pending.contains("map") && pending["map"].is_string() ? pending["map"].get<std::string>() : "";
    // Progress belongs to one start; the next one writes its own.
    pending.erase("uptime");
    pending.erase("map");

    std::string error;
    if (stopped)
    {
        check.outcome = Outcome::Stopped;
        check.earlyDeaths = pending.value("earlyDeaths", 0);
        if (!WriteAtomic(pendingPath, pending.dump(2) + "\n", error)) check.errors.push_back(error);
        return check;
    }

    check.earlyDeaths = pending.value("earlyDeaths", 0) + 1;
    if (check.earlyDeaths < kMaxEarlyDeaths)
    {
        check.outcome = Outcome::Retry;
        pending["earlyDeaths"] = check.earlyDeaths;
        if (!WriteAtomic(pendingPath, pending.dump(2) + "\n", error)) check.errors.push_back(error);
        return check;
    }

    auto rejected = ReadObject(RejectedPath(directory));
    FilesOf(rejected);
    std::time_t until = 0;

    for (const auto& entry : FilesOf(pending))
    {
        if (!entry.is_object()) continue;
        const auto path = entry.value("path", "");
        const auto backup = entry.value("backup", "");
        const auto sha256 = entry.value("sha256", "");
        if (path.empty()) continue;

        const bool backupExists = !backup.empty() && fs::exists(backup, ec);
        auto keys = SuspectKeys(backupExists ? ReadAll(backup) : std::string(), ReadAll(path), platform);
        if (keys.empty())
        {
            // Nothing that could crash the server changed: the deaths were something else.
            check.kept.push_back(path);
            continue;
        }

        if (backup.empty()) fs::remove(path, ec);
        else if (!backupExists)
        {
            check.errors.push_back("backup " + backup + " of " + path + " is gone, left as it is");
            continue;
        }
        else if (!WriteAtomic(path, ReadAll(backup), error))
        {
            check.errors.push_back(error);
            continue;
        }
        check.restored.push_back(path);
        check.suspects.push_back({ path, std::move(keys) });
        if (sha256.empty()) continue;

        // The same file rolled back before: a longer rejection this time.
        int strikes = 1;
        json keep = json::array();
        for (const auto& old : rejected["files"])
        {
            if (old.is_object() && old.value("sha256", "") == sha256) strikes = std::max(strikes, old.value("strikes", 1) + 1);
            else
                keep.push_back(old);
        }
        const auto fileUntil = now + RejectSecondsFor(strikes);
        until = std::max(until, fileUntil);
        keep.push_back({ { "sha256", sha256 },
                         { "path", path },
                         { "rejected", UtcString(now) },
                         { "until", static_cast<long long>(fileUntil) },
                         { "untilUtc", UtcString(fileUntil) },
                         { "strikes", strikes } });
        rejected["files"] = keep;
    }

    check.outcome = check.restored.empty() && check.errors.empty() ? Outcome::Innocent : Outcome::RolledBack;
    check.rejectedUntil = until;
    WriteRejected(directory, rejected, &check.errors);
    fs::remove(pendingPath, ec);
    return check;
}

bool Confirm(const std::string& directory)
{
    std::lock_guard lock(g_fileMutex);
    std::error_code ec;
    if (!fs::exists(PendingPath(directory), ec)) return false;
    auto pending = ReadObject(PendingPath(directory));
    std::set<std::string> hashes;
    for (const auto& entry : FilesOf(pending))
        if (entry.is_object() && !entry.value("sha256", "").empty()) hashes.insert(entry.value("sha256", ""));
    Unreject(directory, hashes);
    return fs::remove(PendingPath(directory), ec);
}

void Disable(const std::string& directory)
{
    std::lock_guard lock(g_fileMutex);
    std::error_code ec;
    fs::remove(PendingPath(directory), ec);
    fs::remove(StoppedPath(directory), ec);
    fs::remove(RejectedPath(directory), ec);
}

std::set<std::string> Rejected(const std::string& directory, std::time_t now)
{
    std::lock_guard lock(g_fileMutex);
    std::set<std::string> hashes;
    std::error_code ec;
    if (!fs::exists(RejectedPath(directory), ec)) return hashes;

    auto rejected = ReadObject(RejectedPath(directory));
    auto files = FilesOf(rejected);
    json keep = json::array();
    for (const auto& entry : files)
    {
        if (!entry.is_object() || !entry.contains("sha256") || !entry["sha256"].is_string()) continue;
        // Written before rejections expired: no "until", treat it as rejected from now on.
        const std::time_t until = entry.contains("until") && entry["until"].is_number() ? entry["until"].get<long long>() : 0;
        if (until == 0)
        {
            auto upgraded = entry;
            const auto fileUntil = now + RejectSecondsFor(1);
            upgraded["until"] = static_cast<long long>(fileUntil);
            upgraded["untilUtc"] = UtcString(fileUntil);
            upgraded["strikes"] = 1;
            keep.push_back(upgraded);
            hashes.insert(entry["sha256"].get<std::string>());
            continue;
        }
        if (until + kRejectMemorySeconds <= now) continue; // forgotten
        keep.push_back(entry);
        if (until > now) hashes.insert(entry["sha256"].get<std::string>());
    }
    if (keep != files)
    {
        rejected["files"] = keep;
        WriteRejected(directory, rejected, nullptr);
    }
    return hashes;
}

std::time_t RejectedUntil(const std::string& directory, const std::string& sha256)
{
    std::lock_guard lock(g_fileMutex);
    auto rejected = ReadObject(RejectedPath(directory));
    for (const auto& entry : FilesOf(rejected))
        if (entry.is_object() && entry.value("sha256", "") == sha256 && entry.contains("until") && entry["until"].is_number())
            return entry["until"].get<long long>();
    return 0;
}

} // namespace counterstrikesharp::update::guard
