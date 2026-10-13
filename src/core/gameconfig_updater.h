#pragma once

namespace counterstrikesharp::update {
bool TryUpdateGameConfig();

// Checks every signature in gamedata/*.json and plugins/*/gamedata/*.json against the loaded
// binaries and writes logs/gamedata-status.json: which ones don't resolve, and from which
// file, so a hosting panel can tell which plugin a CS2 update broke.
void WriteGamedataStatus();

// True when gamedata/gamedata.gamever vouches that gamedata/gamedata.json, as it is on disk now,
// was generated for the game build in steam.inf. Gates the vtable fallback (vtable_fallback.h).
bool GamedataMatchesServerBuild();
} // namespace counterstrikesharp::update
