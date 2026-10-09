#pragma once

// Shared between CounterStrikeSharp (writer) and AcceleratorCS2 (reader). Keep both copies identical.
//
// CounterStrikeSharp records which plugin is doing what into a fixed block of memory, and AcceleratorCS2
// reads that block from its crash handler. The handler can't allocate, take locks or call into .NET, so
// everything is plain fixed-size char arrays. The writer never blocks: a slot is filled, then the index
// is published. A crash in the middle of a write leaves at most one torn entry, which the reader prints
// as-is after replacing non-printable bytes.
//
// AcceleratorCS2 gets the block through Metamod: ISmmAPI::MetaFactory(CSS_CRASH_CONTEXT_INTERFACE) is
// answered by CounterStrikeSharp's OnMetamodQuery.

#include <stdint.h>

#define CSS_CRASH_CONTEXT_INTERFACE "CSSCrashContext001"
#define CSS_CRASH_CONTEXT_MAGIC 0x31585443534353ULL // "CSSCTX1"
#define CSS_CRASH_CONTEXT_VERSION 1

#define CSS_CRASH_PLUGIN_LENGTH 48
#define CSS_CRASH_KIND_LENGTH 16
#define CSS_CRASH_DETAIL_LENGTH 184

#define CSS_CRASH_MAX_DEPTH 8
#define CSS_CRASH_ACTIVITY_SIZE 128
#define CSS_CRASH_JOURNAL_SIZE 32
#define CSS_CRASH_EXCEPTION_SIZE 8

// 256 bytes. Managed code writes these through a pointer, so the layout is part of the contract.
struct CssCrashEntry
{
	int64_t unixMs;                            // wall clock, milliseconds since 1970
	char plugin[CSS_CRASH_PLUGIN_LENGTH];      // plugin name, or "core"
	char kind[CSS_CRASH_KIND_LENGTH];          // "event", "command", "listener", "timer", "hook", "load", "unload", "steamid", ...
	char detail[CSS_CRASH_DETAIL_LENGTH];      // e.g. "EventPlayerSpawn", "css_spoof @me 7656...", "slot 3 m_steamID 765.. -> 765.."
};

struct CssCrashContext
{
	uint64_t magic;
	uint32_t version;
	uint32_t size;                             // sizeof(CssCrashContext), lets the reader reject a mismatched layout

	// Callbacks currently on the main thread's stack, outermost first. depth may exceed CSS_CRASH_MAX_DEPTH,
	// only the first CSS_CRASH_MAX_DEPTH are stored.
	volatile uint32_t depth;
	uint32_t reserved0;
	CssCrashEntry current[CSS_CRASH_MAX_DEPTH];

	// Ring buffers. *Next is the slot that will be written next, so the oldest entry is at *Next.
	// High-frequency callbacks (OnTick, CheckTransmit, per-frame hooks) only update current[], never activity[],
	// or they would push everything useful out within a second.
	volatile uint32_t activityNext;
	volatile uint32_t journalNext;
	volatile uint32_t exceptionNext;
	uint32_t reserved1;
	CssCrashEntry activity[CSS_CRASH_ACTIVITY_SIZE];   // plugin callbacks: commands, events, timers, load/unload
	CssCrashEntry journal[CSS_CRASH_JOURNAL_SIZE];     // risky state changes noticed by the core, e.g. a spoofed m_steamID
	CssCrashEntry exceptions[CSS_CRASH_EXCEPTION_SIZE]; // last unhandled managed exceptions per callback
};
