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

namespace counterstrikesharp::fatal {

// Installs an async-signal-safe SIGABRT handler that prints, as the very LAST
// console line before the process dies, the last native->managed callback and
// the suspect plugin. This is the only way to surface a culprit AFTER the .NET
// runtime FailFasts on a garbage-collected delegate:
//   "A callback was made on a garbage collected delegate ...CallbackDelegate::Invoke"
// FailFast -> abort() -> SIGABRT, and no managed code runs after it, so the
// customer would otherwise only see the cryptic CLR line followed by "Process
// terminated" with no plugin named. We run first, print, then chain to the CLR's
// own handler so its crash dump still happens. Idempotent. Only SIGABRT is hooked
// (NOT SIGSEGV: CoreCLR uses SIGSEGV for normal operation e.g. null-ref handling).
void InstallHandler();

// Records the callback about to be dispatched into managed code. Lock-free and
// cheap (two relaxed atomic stores); called on the hot path in ScriptCallback::Execute.
void SetCallbackBreadcrumb(const char* callbackName, int index);

// Marks the dispatch loop as finished so an unrelated later abort does not blame
// the last callback that happened to run.
void ClearCallbackBreadcrumb();

// Sets the plugin most likely to be the culprit (the last plugin that failed to
// load). Copied into a fixed buffer; safe to call from managed via a native.
void SetSuspectPlugin(const char* pluginName);

// Records the console command being dispatched, and who ran it. A crash that
// follows a command ("css_reloadadmins", a map change, an admin command) is
// otherwise indistinguishable from a spontaneous one in the log. Fixed buffers,
// game thread only, read from the signal handler.
void SetCommandBreadcrumb(const char* command, const char* issuer);

// Where crash reports and the live state file are written, and the identity that
// ties a report back to one of many servers. Call once during startup, before
// anything can crash: the signal handler cannot build paths or allocate, so it
// needs them ready. A null or empty directory disables both files (console
// output still happens).
void ConfigureReporting(const char* directory, const char* serverId);

// Current map, refreshed on level init. Shown in the report and the state file.
void SetMap(const char* mapName);

// Build identity ("v1.0.400 @ abc1234"), recorded once at startup. A crash report
// is worth little if you cannot tell which build produced it -- across a fleet
// mid-rollout, that is the first thing to check.
void SetBuildVersion(const char* version);

// Current tick, refreshed with the state file. Distinguishes "died during startup"
// from "died after six hours", which the timestamps alone do not when a server is
// restarted in a loop.
void SetTick(int tick);

// Rewrites the "last known state" file: what the server was doing, on disk,
// BEFORE anything goes wrong. This is the only evidence that survives a crash we
// cannot catch -- a native segfault (CoreCLR owns SIGSEGV), the OOM killer, or a
// hang with no signal at all. Cheap enough to call on a timer; it writes a few
// hundred bytes and returns immediately if nothing changed since the last write.
void WriteStateFile();

} // namespace counterstrikesharp::fatal
