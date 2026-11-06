#pragma once

// Crash-loop guard for gamedata updates. A gamedata file the updater writes takes effect on the
// same start, so a bad one (a signature that resolves to the wrong function, say) can crash the
// server on every start from then on, and each start would fetch it again. So every write is
// recorded in gamedata/update.pending with the backup it replaced. Once the server has run for a
// while (or shuts down cleanly) the record is dropped. A start that finds it means the previous
// one died early: the first such start retries once, since the crash may have had nothing to do
// with gamedata; the second restores the backups and remembers each rejected file's sha256 in
// gamedata/update.rejected, so the updater does not apply that exact file again. A newer
// publication has a new hash and is tried normally. Delete update.rejected to retry by hand.
//
// Engine-free (paths in, results out); unit-tested in tests/test_gamedata_update_guard.cpp.

#include <set>
#include <string>
#include <vector>

namespace counterstrikesharp::update::guard {

// A start that dies before this much uptime counts as a crash caused by the update.
inline constexpr int kConfirmAfterSeconds = 180;
// Starts that may die early before the update is rolled back.
inline constexpr int kMaxEarlyDeaths = 2;

struct PendingFile
{
    std::string path; // the file written
    std::string backup; // what it replaced; empty when it did not exist before
    std::string sha256; // of the written contents
};

// Records a write in <directory>/update.pending. When the same path is already pending (written
// twice before a confirmation), its first backup is kept: that one is the state before the update.
bool RecordPending(const std::string& directory, const PendingFile& file, std::string& error);

enum class Outcome
{
    Clean, // no pending update
    Retry, // the previous start died early once; keep the update and try again
    RolledBack, // died early kMaxEarlyDeaths times: backups restored, files rejected
};

struct BootCheck
{
    Outcome outcome = Outcome::Clean;
    int earlyDeaths = 0;
    std::vector<std::string> restored; // paths restored from (or removed for lack of) a backup
    std::vector<std::string> errors;
};

// Runs once per process start, before any gamedata is read or updated.
BootCheck CheckPreviousStart(const std::string& directory);

// The update survived: drop update.pending. Returns whether there was one.
bool Confirm(const std::string& directory);

// sha256 (lowercase hex) of files rolled back before.
std::set<std::string> Rejected(const std::string& directory);

} // namespace counterstrikesharp::update::guard
