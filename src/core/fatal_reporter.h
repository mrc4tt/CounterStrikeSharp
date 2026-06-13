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

} // namespace counterstrikesharp::fatal
