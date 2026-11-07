#pragma once

#include <algorithm>
#include <cctype>
#include <map>
#include <optional>
#include <string>

#include <nlohmann/json.hpp>

// Opt-in upload of plugin gamedata files VibeSignatures does not know (GamedataUploadEnabled):
// the engine-free half. The network half is in gameconfig_updater.cpp (ReportGamedata uploads,
// TryUpdatePluginGamedata reads the result back).
//
// Unlike the gamedata report, which sends names, key names and a hash, this sends the file's
// text to the GamedataReportURL host (POST /api/files on VibeSignatures' verify_service). That
// service keeps the file up to date for every new game build, and the plugin updater then reads
// /api/files/<id>/<name> as the file's feed. That answer is not in a signed manifest, so it is
// applied only under the strictest rule: a broken signature is replaced by one that resolves here
// in the same library; never an offset, never the whole file. https only, and only from the
// GamedataReportURL host (redirects are not followed).
//
// State: gamedata/plugin-gamedata.uploads, { "<relative path>": Record }. A file of its own, not
// plugin-gamedata.state: it is written from the report's thread (state is the game thread's), and
// it holds the manage token, a secret that lets whoever has it replace or delete the upload.
namespace counterstrikesharp::update::upload {

struct Record
{
    std::string id; // the read secret (22 url-safe characters)
    std::string filename; // as the service stored it
    std::string manage; // manage token (X-Manage-Token), "" when the service gave none
    std::string sha256; // of the text last uploaded
    std::string written; // of the file as the plugin updater last wrote it from any feed
    std::string build;
    long long at = 0;
};

inline bool ValidId(const std::string& id)
{
    return id.size() == 22 && std::all_of(id.begin(), id.end(), [](char c) {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
    });
}

// The service's clean_filename alphabet, as nginx's read-link location accepts it.
inline bool ValidFileName(const std::string& name)
{
    return !name.empty() && name.size() <= 100 && name != "." && name != ".." && std::all_of(name.begin(), name.end(), [](char c) {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
    });
}

inline std::string Text(const nlohmann::json& object, const char* key)
{
    return object.is_object() && object.contains(key) && object[key].is_string() ? object[key].get<std::string>() : std::string();
}

// Entries with an invalid id or file name are dropped: they could not be read back anyway.
inline std::map<std::string, Record> ParseState(const nlohmann::json& state)
{
    std::map<std::string, Record> out;
    if (!state.is_object()) return out;
    for (const auto& [file, entry] : state.items())
    {
        Record record{ Text(entry, "id"),     Text(entry, "filename"), Text(entry, "manage"),
                       Text(entry, "sha256"), Text(entry, "written"),  Text(entry, "build") };
        if (entry.is_object() && entry.contains("at") && entry["at"].is_number_integer()) record.at = entry["at"].get<long long>();
        if (ValidId(record.id) && ValidFileName(record.filename)) out[file] = std::move(record);
    }
    return out;
}

inline nlohmann::json SerializeState(const std::map<std::string, Record>& records)
{
    auto out = nlohmann::json::object();
    for (const auto& [file, record] : records)
    {
        nlohmann::json entry = { { "id", record.id }, { "filename", record.filename }, { "sha256", record.sha256 }, { "at", record.at } };
        if (!record.manage.empty()) entry["manage"] = record.manage;
        if (!record.written.empty()) entry["written"] = record.written;
        if (!record.build.empty()) entry["build"] = record.build;
        out[file] = std::move(entry);
    }
    return out;
}

enum class Action
{
    None, // uploaded already, and unchanged since (or changed only by the plugin updater)
    Create, // never uploaded, or the service no longer has it
    Replace, // uploaded, then changed (a new plugin release): PUT the new text under the same links
};

inline Action Decide(const Record* existing, const std::string& currentSha256)
{
    if (!existing) return Action::Create;
    if (currentSha256 == existing->sha256 || (!existing->written.empty() && currentSha256 == existing->written)) return Action::None;
    return existing->manage.empty() ? Action::Create : Action::Replace;
}

// https://host[:port] of GamedataReportURL; nullopt (and why) for anything but https.
inline std::optional<std::string> ServiceOrigin(const std::string& reportUrl, std::string& error)
{
    constexpr const char scheme[] = "https://";
    std::string lower = reportUrl;
    std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    if (lower.rfind(scheme, 0) != 0)
    {
        error = "GamedataReportURL '" + reportUrl + "' is not https://; uploads and their read links are https only";
        return std::nullopt;
    }
    const auto hostStart = sizeof(scheme) - 1;
    const auto end = reportUrl.find_first_of("/?#", hostStart);
    const auto host = reportUrl.substr(hostStart, end == std::string::npos ? std::string::npos : end - hostStart);
    if (host.empty() || host.find('@') != std::string::npos)
    {
        error = "GamedataReportURL '" + reportUrl + "' has no plain host";
        return std::nullopt;
    }
    return "https://" + host;
}

// POST target for a new upload.
inline std::string CreateUrl(const std::string& origin) { return origin + "/api/files"; }
inline std::string ManageUrl(const std::string& origin) { return origin + "/api/files/manage"; }

// The read link the plugin updater fetches, always built from the configured host, never taken
// from the service's answer.
inline std::string ReadUrl(const std::string& origin, const Record& record)
{
    return origin + "/api/files/" + record.id + "/" + record.filename;
}

// The answer to POST /api/files (201) or PUT /api/files/manage (200), checked before anything
// is stored: {"id", "filename", "url": "/api/files/<id>/<filename>", "manage"?}. A PUT answer
// carries no id or token; pass the existing record to keep them.
inline std::optional<Record> ParseAnswer(const nlohmann::json& answer, const Record* existing, std::string& error)
{
    if (!answer.is_object())
    {
        error = "the answer is not a JSON object";
        return std::nullopt;
    }
    Record record = existing ? *existing : Record{};
    if (const auto id = Text(answer, "id"); !id.empty()) record.id = id;
    if (const auto manage = Text(answer, "manage"); !manage.empty()) record.manage = manage;
    record.filename = Text(answer, "filename");
    if (!ValidId(record.id) || !ValidFileName(record.filename))
    {
        error = "the answer has no valid id and file name";
        return std::nullopt;
    }
    if (Text(answer, "url") != "/api/files/" + record.id + "/" + record.filename)
    {
        error = "the answer's url is not /api/files/<id>/<filename>";
        return std::nullopt;
    }
    return record;
}

// Body of the upload: the text, the local file name, and the build it was taken on (the service
// keeps it up to date from there).
inline nlohmann::json UploadBody(const std::string& text, const std::string& fileName, const std::string& build)
{
    nlohmann::json body = { { "gamedata", text }, { "filename", fileName } };
    if (!build.empty()) body["build"] = build;
    return body;
}

} // namespace counterstrikesharp::update::upload
