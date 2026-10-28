#pragma once

#include <string>

namespace counterstrikesharp::update {
bool TryUpdateGameConfig();

// The running game build as steam.inf names it ("1.41.9.2" -> "14192"), empty (and logged) when
// steam.inf cannot be read. Pins gamedata/custom/ files (core/gamedata_custom.h).
std::string ServerGameVersion();

// Whether `signature` ("55 48 ..", or "@symbol") is found in `library` in this process: the plain
// scan, without the vtable fallback. modules::Initialize() must have run.
bool SignatureResolves(const std::string& library, const std::string& signature);

// Checks every signature in gamedata/*.json and plugins/*/gamedata/*.json against the loaded
// binaries and writes logs/gamedata-status.json: which ones don't resolve, and from which
// file, so a hosting panel can tell which plugin a CS2 update broke.
void WriteGamedataStatus();

// True when gamedata/gamedata.gamever vouches that gamedata/gamedata.json, as it is on disk now,
// was generated for the game build in steam.inf. Gates the vtable fallback (vtable_fallback.h).
bool GamedataMatchesServerBuild();
} // namespace counterstrikesharp::update
