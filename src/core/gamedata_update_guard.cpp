#include "core/gamedata_update_guard.h"

#include <ctime>
#include <filesystem>
#include <fstream>
#include <iterator>

#include <nlohmann/json.hpp>

namespace counterstrikesharp::update::guard {

namespace {

namespace fs = std::filesystem;
using json = nlohmann::json;

std::string PendingPath(const std::string& directory) { return (fs::path(directory) / "update.pending").string(); }
std::string RejectedPath(const std::string& directory) { return (fs::path(directory) / "update.rejected").string(); }

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

std::string UtcNow()
{
    char stamp[32];
    const auto now = std::time(nullptr);
    std::tm utc{};
#ifdef _WIN32
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    std::strftime(stamp, sizeof(stamp), "%Y-%m-%dT%H:%M:%SZ", &utc);
    return stamp;
}

} // namespace

bool RecordPending(const std::string& directory, const PendingFile& file, std::string& error)
{
    auto pending = ReadObject(PendingPath(directory));
    if (!pending.contains("files") || !pending["files"].is_array()) pending["files"] = json::array();
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

BootCheck CheckPreviousStart(const std::string& directory)
{
    BootCheck check;
    const auto pendingPath = PendingPath(directory);
    std::error_code ec;
    if (!fs::exists(pendingPath, ec)) return check;

    auto pending = ReadObject(pendingPath);
    check.earlyDeaths = pending.value("earlyDeaths", 0) + 1;

    std::string error;
    if (check.earlyDeaths < kMaxEarlyDeaths)
    {
        check.outcome = Outcome::Retry;
        pending["earlyDeaths"] = check.earlyDeaths;
        if (!WriteAtomic(pendingPath, pending.dump(2) + "\n", error)) check.errors.push_back(error);
        return check;
    }

    check.outcome = Outcome::RolledBack;
    auto rejected = ReadObject(RejectedPath(directory));
    if (!rejected.contains("files") || !rejected["files"].is_array()) rejected["files"] = json::array();

    if (pending.contains("files") && pending["files"].is_array())
    {
        for (const auto& entry : pending["files"])
        {
            if (!entry.is_object()) continue;
            const auto path = entry.value("path", "");
            const auto backup = entry.value("backup", "");
            const auto sha256 = entry.value("sha256", "");
            if (path.empty()) continue;

            if (backup.empty()) fs::remove(path, ec);
            else if (!fs::exists(backup, ec))
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
            if (!sha256.empty()) rejected["files"].push_back({ { "sha256", sha256 }, { "path", path }, { "rejected", UtcNow() } });
        }
    }

    if (!WriteAtomic(RejectedPath(directory), rejected.dump(2) + "\n", error)) check.errors.push_back(error);
    fs::remove(pendingPath, ec);
    return check;
}

bool Confirm(const std::string& directory)
{
    std::error_code ec;
    return fs::remove(PendingPath(directory), ec);
}

std::set<std::string> Rejected(const std::string& directory)
{
    std::set<std::string> hashes;
    auto rejected = ReadObject(RejectedPath(directory));
    if (rejected.contains("files") && rejected["files"].is_array())
        for (const auto& entry : rejected["files"])
            if (entry.is_object() && entry.contains("sha256") && entry["sha256"].is_string())
                hashes.insert(entry["sha256"].get<std::string>());
    return hashes;
}

} // namespace counterstrikesharp::update::guard
