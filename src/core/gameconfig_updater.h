#pragma once

#include <string>

namespace counterstrikesharp::update {
bool TryUpdateGameConfig();

// Swaps signatures that don't resolve in gamedata/*.json (other than gamedata.json) for the ones
// the AutoUpdateURL site publishes for the same plugin,
// when those resolve here (core/gamedata_plugin_update.h). Never replaces a whole file.
void TryUpdatePluginGamedata();

// Crash-loop guard for gamedata updates (core/gamedata_update_guard.h). CheckPreviousStart runs
// first thing, before any update or gamedata read: when the previous start died early after an
// update it retries once, then restores the backups. StartUpdateConfirmTimer, after the updates,
// keeps them for good once this start has run a few minutes; StopUpdateConfirmTimer(true) on a
// clean Unload does the same at once.
void CheckPreviousStart();
void StartUpdateConfirmTimer();
void StopUpdateConfirmTimer(bool cleanShutdown);
// Map of this start, stored with a pending update so a rollback can name it.
void NoteMapStart(const char* map);
// Linux: SIGTERM/SIGINT/SIGHUP while an update is pending leave gamedata/update.stopped, so a
// deliberate stop is not counted as a crash. Install after the .NET runtime (it chains to the
// handler installed before it); removed again by StopUpdateConfirmTimer(true).
void InstallStopSignalMarker();
void RemoveStopSignalMarker();

// Opt-in (GamedataReportEnabled): posts which plugin gamedata files this server runs (names,
// sha256, key names, broken keys; no file text) to GamedataReportURL from a thread of its own.
// The answer helps the next start's TryUpdatePluginGamedata identify each file.
void ReportGamedata();

// The running game build as steam.inf names it ("1.41.9.2" -> "14192"), empty (and logged) when
// steam.inf cannot be read. Pins gamedata/custom/ files (core/gamedata_custom.h).
std::string ServerGameVersion();

// Whether `signature` ("55 48 ..", or "@symbol") is found in `library` in this process: the plain
// scan, without the vtable fallback. modules::Initialize() must have run.
bool SignatureResolves(const std::string& library, const std::string& signature);

// Checks every signature in gamedata/*.json (plugins/*/gamedata/ copies are not read by anyone,
// so not checked either: GamedataFiles in gameconfig_updater.cpp) against the loaded
// binaries and writes logs/gamedata-status.json: which ones don't resolve, and from which
// file, so a hosting panel can tell which plugin a CS2 update broke.
void WriteGamedataStatus();

// True when gamedata/gamedata.gamever vouches that gamedata/gamedata.json, as it is on disk now,
// was generated for the game build in steam.inf. Gates the vtable fallback (vtable_fallback.h).
bool GamedataMatchesServerBuild();
} // namespace counterstrikesharp::update
