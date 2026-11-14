#pragma once

// Crash-loop guard for gamedata updates. A gamedata file the updater writes takes effect on the
// same start, so a bad one (a signature that resolves to the wrong function, say) can crash the
// server on every start from then on, and each start would fetch it again. So every write is
// recorded in gamedata/update.pending with the backup it replaced. Once the server has run for a
// while (or shuts down cleanly) the record is dropped. A start that finds it means the previous
// one died early: the first such start retries once, since the crash may have had nothing to do
// with gamedata; the second restores the backups and remembers each rejected file's sha256 in
// gamedata/update.rejected, so the updater does not apply that exact file again for a while.
//
// The guard cannot see why a start died (SIGKILL leaves nothing, and SIGSEGV is CoreCLR's), so
// it errs towards letting updates through:
//  - A start stopped by SIGTERM/SIGINT/SIGHUP (panel stop or restart, docker stop, Ctrl-C) leaves
//    gamedata/update.stopped and does not count as a death.
//  - Only keys whose signature/offset for this platform (or anything platform-neutral) changed
//    against the backup can crash anything. A file with none is kept and never rejected, and the
//    keys of the others are logged as the suspects.
//  - A rejection expires: 1 h, then 6 h, then 24 h for each further rollback of the same file,
//    after which the same file is tried again. A newer publication has a new hash and is tried at
//    once. A rejected file that later survives a start is dropped from update.rejected, and the
//    file is deleted once nothing in it is current.
//
// Engine-free (paths in, results out); unit-tested in tests/test_gamedata_update_guard.cpp.

#include <ctime>
#include <set>
#include <string>
#include <vector>

namespace counterstrikesharp::update::guard {

// A start that dies before this much uptime counts as a crash caused by the update.
inline constexpr int kConfirmAfterSeconds = 180;
// Starts that may die early before the update is rolled back.
inline constexpr int kMaxEarlyDeaths = 2;
// How long a rolled-back file stays rejected, by how many times it was rolled back (last one repeats).
inline constexpr std::time_t kRejectSeconds[] = { 60 * 60, 6 * 60 * 60, 24 * 60 * 60 };
// Expired rejections are kept this long to remember how often a file was rolled back.
inline constexpr std::time_t kRejectMemorySeconds = 7 * 24 * 60 * 60;
// Written by the stop-signal handler (gameconfig_updater.cpp) next to update.pending.
inline constexpr const char* kStoppedMarker = "update.stopped";

struct PendingFile
{
    std::string path; // the file written
    std::string backup; // what it replaced; empty when it did not exist before
    std::string sha256; // of the written contents
};

// Records a write in <directory>/update.pending. When the same path is already pending (written
// twice before a confirmation), its first backup is kept: that one is the state before the update.
bool RecordPending(const std::string& directory, const PendingFile& file, std::string& error);

// How far the current start got, stored in update.pending while it is unconfirmed so a rollback
// can say so. No-op without a pending update. An empty map leaves the stored one alone.
void NoteProgress(const std::string& directory, int uptimeSeconds, const std::string& map);

enum class Outcome
{
    Clean, // no pending update
    Stopped, // the previous start was stopped by a signal: not a crash, the update stays pending
    Retry, // the previous start died early once; keep the update and try again
    Innocent, // died early kMaxEarlyDeaths times, but the update changed nothing that could crash: kept
    RolledBack, // died early kMaxEarlyDeaths times: suspect files restored and rejected for a while
};

struct Suspect
{
    std::string path;
    std::vector<std::string> keys; // "Key (signatures.linux, offsets.linux)"
};

struct BootCheck
{
    Outcome outcome = Outcome::Clean;
    int earlyDeaths = 0;
    int lastUptime = -1; // seconds the dead start was seen running; -1 when unknown
    std::string lastMap; // map the dead start had loaded; empty when none
    std::string stopSignal; // Outcome::Stopped: what update.stopped says
    std::vector<std::string> restored; // paths restored from (or removed for lack of) a backup
    std::vector<std::string> kept; // pending paths the update changed nothing crash-relevant in
    std::vector<Suspect> suspects; // what changed in each restored file
    std::time_t rejectedUntil = 0; // RolledBack: when the restored files may be tried again
    std::vector<std::string> errors;
};

// Runs once per process start, before any gamedata is read or updated. `platform` picks which
// signatures/offsets count as suspects ("linux" or "windows").
BootCheck CheckPreviousStart(const std::string& directory, const std::string& platform, std::time_t now = std::time(nullptr));

// The update survived: drop update.pending, and any rejection of the files it held (they work).
// Returns whether there was one.
bool Confirm(const std::string& directory);

// The guard is turned off: forget update.pending, update.stopped and every rejection.
void Disable(const std::string& directory);

// sha256 (lowercase hex) of files rolled back and still rejected at `now`. Drops entries past
// kRejectMemorySeconds and deletes update.rejected once it is empty.
std::set<std::string> Rejected(const std::string& directory, std::time_t now = std::time(nullptr));

// When the rejection of `sha256` ends; 0 when it is not rejected.
std::time_t RejectedUntil(const std::string& directory, const std::string& sha256);

// Suspect keys: what changed in `after` against `before` (gamedata JSON text) that can make the
// server crash on `platform`. The other platform's signatures/offsets and removed keys cannot; a
// missing or unreadable `before` makes every key a suspect.
std::vector<std::string> SuspectKeys(const std::string& before, const std::string& after, const std::string& platform);

} // namespace counterstrikesharp::update::guard
