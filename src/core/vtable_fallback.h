#pragma once

#include "core/gamedata_vtable.h"

namespace counterstrikesharp::modules {
class CModule;
}

namespace counterstrikesharp::gamedata {

// Reads the "vtable" blocks (see gamedata_vtable.h) of every gamedata/*.json - the same set
// GameDataProvider merges on the managed side. Must run before the first signature scan that
// should benefit, i.e. before globals::Initialize(). `gamedataJsonTrusted` (the updater's
// gamever marker matched) lets gamedata.json's slots stand in for failed scans; every other
// slot only cross-checks.
void LoadVtableFallbacks(bool gamedataJsonTrusted);

// The function `slot` names in `module`, or nullptr when the class has no vtable there, the
// index runs past the vtable's section, or the slot does not point into .text.
void* ResolveVtableSlot(modules::CModule* module, const VtableSlot& slot);

// Final say on a scan of `signature` in `module`. A signature whose entry names no vtable slot
// is returned untouched. Otherwise: a failed scan falls back to a trusted slot, and a successful one
// is cross-checked against it - a disagreement is logged (once per key) and the scanned address
// kept, since a thunk and its body legitimately differ.
void* ApplyVtableFallback(modules::CModule* module, const char* signature, void* scanned);

} // namespace counterstrikesharp::gamedata
