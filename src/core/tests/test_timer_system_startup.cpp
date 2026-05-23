// Unit tests for counterstrikesharp::timer_startup::ShouldFireLevelEndAndResetTickState.
//
// This is the pure-logic helper that TimerSystem::OnStartupServer in
// src/core/timer_system.cpp delegates to. Production and tests compile from
// the SAME inline definition in timer_system_startup_logic.h, so if anyone
// reverts the unconditional reset (the heart of the workshop-ss_dead-reload
// fix), the corresponding assertion here fails at CI time.
//
// What we are asserting:
//   1. levelShutdown=true on a ticked map -> returns true (caller fires
//      OnLevelEnd) AND clears tick flags.
//   2. levelShutdown=false (workshop ss_dead reload cycle) -> returns false
//      (caller does NOT fire OnLevelEnd), but STILL clears tick flags. This
//      is the regression guard: if the reset becomes conditional again,
//      universal_time math in OnGameFrame drifts and MatchZy's AutoStart
//      timer fires arbitrarily late mid-match.
//   3. On a fresh instance (no prior tick), neither bool fires OnLevelEnd.

#include "catch_amalgamated.hpp"

#include "core/timer_system_startup_logic.h"

using counterstrikesharp::timer_startup::ShouldFireLevelEndAndResetTickState;

TEST_CASE("levelShutdown=true on a ticked map -> fire + clear", "[TimerSystem][startup]")
{
    bool ticked = true;
    bool simulated = true;

    const bool fire = ShouldFireLevelEndAndResetTickState(true, ticked, simulated);

    CHECK(fire);
    CHECK_FALSE(ticked);
    CHECK_FALSE(simulated);
}

TEST_CASE("levelShutdown=false STILL clears tick state (regression guard)", "[TimerSystem][startup]")
{
    // On a workshop ss_dead reload cycle, Hook_StartupServer fires WITHOUT a
    // preceding OnLevelShutdown -- levelShutdown is false. The pre-fix code
    // only cleared the tick flags inside the levelShutdown branch, so
    // OnGameFrame on the next frame computed universal_time against an
    // unrelated last_ticked_time and jumped forward by minutes. Per-map
    // one-off timers (notably MatchZy's 1-second AddTimer(AutoStart)) then
    // fired effectively-immediately and aborted live matches.
    //
    // The fix: reset unconditionally. The CHECK_FALSE(ticked) below is the
    // assertion that catches a regression of that fix.
    bool ticked = true;
    bool simulated = true;

    const bool fire = ShouldFireLevelEndAndResetTickState(false, ticked, simulated);

    CHECK_FALSE(fire); // gating preserved -- no listener fan-out on ss_dead
    CHECK_FALSE(ticked);
    CHECK_FALSE(simulated);
}

TEST_CASE("fresh instance -> never fires regardless of levelShutdown", "[TimerSystem][startup]")
{
    bool ticked = false;
    bool simulated = false;

    CHECK_FALSE(ShouldFireLevelEndAndResetTickState(true, ticked, simulated));
    CHECK_FALSE(ticked);
    CHECK_FALSE(simulated);

    CHECK_FALSE(ShouldFireLevelEndAndResetTickState(false, ticked, simulated));
    CHECK_FALSE(ticked);
    CHECK_FALSE(simulated);
}
