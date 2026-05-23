#pragma once

namespace counterstrikesharp {
namespace timer_startup {

// Pure decision logic for TimerSystem::OnStartupServer.
//
// Extracted into a header-only helper so unit tests can exercise the exact
// same source-line logic that production runs without linking against
// timer_system.cpp (which transitively pulls in hl2sdk-cs2, spdlog, protobuf,
// dotnet host, and metamod). Both callers compile from this single
// definition; any logic change here will be caught by the unit test.
//
// Contract:
//   - Returns true iff the caller should fire the OnLevelEnd listener
//     fan-out (only on a genuine OnLevelShutdown -> StartupServer sequence
//     where at least one frame ticked during the previous session).
//   - has_map_ticked / has_map_simulated are ALWAYS cleared. This is the
//     heart of the workshop-ss_dead-reload fix: universal_time math in
//     OnGameFrame depends on those flags being false on the first frame of
//     every new session, regardless of whether the cycle came in via a
//     genuine LevelShutdown. Conditional reset re-introduces the MatchZy
//     mid-match abort.
inline bool ShouldFireLevelEndAndResetTickState(bool levelShutdown, bool& has_map_ticked, bool& has_map_simulated)
{
    const bool fire = levelShutdown && has_map_ticked;
    has_map_ticked = false;
    has_map_simulated = false;
    return fire;
}

} // namespace timer_startup
} // namespace counterstrikesharp
