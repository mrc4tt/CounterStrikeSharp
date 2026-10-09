/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

#pragma once

#include "core/css_crash_context.h"

namespace counterstrikesharp::crash {

// Flight recorder for crash handlers (AcceleratorCS2). Records which plugin is running which callback,
// recent plugin activity, and risky state changes the core notices, into one fixed block of memory that a
// crash handler can read without allocating. Managed code writes the plugin entries directly through the
// pointer returned by Get() (native GET_CRASH_CONTEXT); the core writes the journal.
//
// Crash handlers find the block through Metamod: MetaFactory(CSS_CRASH_CONTEXT_INTERFACE).
CssCrashContext* Get();

// Appends to the journal. plugin may be null, in which case the plugin of the most recent activity
// entry (if it is recent) is used as the suspect.
void Journal(const char* plugin, const char* kind, const char* detail);

// Called once per game frame. Every few frames compares each connected human player's controller
// m_steamID with the SteamID the engine authenticated, and journals a mismatch once per change.
// A spoofed m_steamID makes the game's inventory code (OnLoadoutChanged) abort the server later.
void OnGameFrame();

} // namespace counterstrikesharp::crash
