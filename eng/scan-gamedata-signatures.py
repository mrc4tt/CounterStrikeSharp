#!/usr/bin/env python3
"""Scan gamedata.json byte signatures against CS2 binaries on disk.

test-gamedata-signatures.py answers the same question through Frida, but only
about the platform you are sitting on and only while a server is running. This
one reads the binaries directly, so it can check the Windows patterns from
Linux, check a build that is not installed, and check an archived build long
after it stopped being current.

    eng/scan-gamedata-signatures.py --binaries ~/CS2_VibeSignatures/bin/14181

A signature matching exactly once is the only good outcome. Zero matches is the
obvious failure; more than one is the dangerous one, because CModule::
FindSignature takes the first hit and a server will happily call the wrong
function without complaining.

Only byte patterns are checked. Offsets (vtable indices) and '@' symbol entries
carry nothing that can be verified against a file this way.
"""
import argparse
import json
import re
import struct
import sys
from pathlib import Path

RepoRoot = Path(__file__).resolve().parent.parent
DefaultGamedataPath = (
    RepoRoot / "configs" / "addons" / "counterstrikesharp" / "gamedata" / "gamedata.json"
)

# gamedata's library names are not the file names.
LibraryBaseNames = {
    "engine": "engine2",
    "server": "server",
    "tier0": "tier0",
    "vscript": "vscript",
}

PlatformFileNames = {
    "linux": lambda baseName: f"lib{baseName}.so",
    "windows": lambda baseName: f"{baseName}.dll",
}

StatusFound = "FOUND"
StatusNotFound = "NOT_FOUND"
StatusNotUnique = "NOT_UNIQUE"
StatusInvalid = "INVALID"
StatusSkipped = "SKIPPED"


class BinaryError(Exception):
    pass


def readExecutableSectionsElf(blob):
    """Executable sections of an ELF64 image, as (name, virtualAddress, bytes)."""
    if blob[4] != 2:
        raise BinaryError("not a 64-bit ELF")

    sectionHeaderOffset, = struct.unpack_from("<Q", blob, 0x28)
    sectionHeaderSize, sectionCount, nameTableIndex = struct.unpack_from("<HHH", blob, 0x3A)
    if sectionHeaderOffset == 0 or sectionCount == 0:
        raise BinaryError("ELF has no section headers")

    def sectionHeader(index):
        base = sectionHeaderOffset + index * sectionHeaderSize
        nameOffset, sectionType, flags, virtualAddress, fileOffset, size = struct.unpack_from(
            "<IIQQQQ", blob, base
        )
        return nameOffset, sectionType, flags, virtualAddress, fileOffset, size

    _, _, _, _, nameTableOffset, nameTableSize = sectionHeader(nameTableIndex)
    nameTable = blob[nameTableOffset : nameTableOffset + nameTableSize]

    ShfExecInstr = 0x4
    ShtNoBits = 8
    sections = []
    for index in range(sectionCount):
        nameOffset, sectionType, flags, virtualAddress, fileOffset, size = sectionHeader(index)
        if not flags & ShfExecInstr or sectionType == ShtNoBits or size == 0:
            continue
        name = nameTable[nameOffset : nameTable.index(b"\0", nameOffset)].decode("utf-8", "replace")
        sections.append((name, virtualAddress, blob[fileOffset : fileOffset + size]))

    return sections


def readExecutableSectionsPe(blob):
    """Executable sections of a PE32+ image, as (name, relativeVirtualAddress, bytes)."""
    peOffset, = struct.unpack_from("<I", blob, 0x3C)
    if blob[peOffset : peOffset + 4] != b"PE\0\0":
        raise BinaryError("missing PE signature")

    coffOffset = peOffset + 4
    sectionCount, = struct.unpack_from("<H", blob, coffOffset + 2)
    optionalHeaderSize, = struct.unpack_from("<H", blob, coffOffset + 16)
    optionalOffset = coffOffset + 20

    optionalMagic, = struct.unpack_from("<H", blob, optionalOffset)
    if optionalMagic != 0x20B:
        raise BinaryError("not a PE32+ (x64) image")

    ImageScnMemExecute = 0x20000000
    sectionTableOffset = optionalOffset + optionalHeaderSize
    sections = []
    for index in range(sectionCount):
        base = sectionTableOffset + index * 40
        rawName = blob[base : base + 8]
        virtualAddress, rawSize, rawOffset = struct.unpack_from("<III", blob, base + 12)
        characteristics, = struct.unpack_from("<I", blob, base + 36)
        if not characteristics & ImageScnMemExecute or rawSize == 0:
            continue
        name = rawName.rstrip(b"\0").decode("utf-8", "replace")
        sections.append((name, virtualAddress, blob[rawOffset : rawOffset + rawSize]))

    return sections


def readExecutableSections(path):
    blob = path.read_bytes()
    if blob[:4] == b"\x7fELF":
        return readExecutableSectionsElf(blob)
    if blob[:2] == b"MZ":
        return readExecutableSectionsPe(blob)
    raise BinaryError("unrecognised binary format")


def compilePattern(patternText):
    """Turn a gamedata pattern into a regex over raw bytes.

    Accepts both spellings gamedata uses: "48 89 5C 24 ?" and "\\x48\\x89\\x2A".
    """
    sourceText = patternText.strip()
    if not sourceText:
        raise ValueError("empty signature")
    if sourceText.startswith("@"):
        raise ValueError("symbol entry, not a byte pattern")

    tokens = (
        [token for token in sourceText.split("\\x") if token]
        if sourceText.startswith("\\x")
        else sourceText.split()
    )
    if not tokens:
        raise ValueError("no tokens in signature")

    parts = []
    for token in tokens:
        if token.startswith("?") or token.upper() == "2A":
            parts.append(b".")
            continue
        try:
            value = int(token[:2], 16)
        except ValueError as error:
            raise ValueError(f"invalid byte {token!r}") from error
        parts.append(re.escape(bytes([value])))

    if all(part == b"." for part in parts):
        raise ValueError("signature is entirely wildcards")

    return re.compile(b"".join(parts), re.DOTALL)


def scanSections(sections, pattern, matchLimit):
    """Every offset the pattern matches, counting overlaps.

    Overlapping matches are counted because CModule::FindSignature does a
    byte-by-byte walk, not a non-overlapping regex sweep.
    """
    matches = []
    for name, virtualAddress, data in sections:
        position = 0
        while True:
            found = pattern.search(data, position)
            if found is None:
                break
            matches.append((name, virtualAddress + found.start()))
            if len(matches) > matchLimit:
                return matches
            position = found.start() + 1
    return matches


def resolveModulePath(binariesRoot, libraryName, platformName):
    baseName = LibraryBaseNames.get(libraryName, libraryName)
    fileName = PlatformFileNames[platformName](baseName)

    direct = binariesRoot / fileName
    if direct.is_file():
        return direct
    # The archive nests per-module (14181/server/libserver.so), so fall back to
    # a search rather than demanding a particular layout.
    candidates = sorted(binariesRoot.rglob(fileName))
    return candidates[0] if candidates else None


def scanPlatform(gamedata, binariesRoot, platformName, matchLimit):
    signatureEntries = {
        name: value["signatures"]
        for name, value in gamedata.items()
        if isinstance(value, dict) and isinstance(value.get("signatures"), dict)
    }

    sectionsByLibrary = {}
    modulePaths = {}
    moduleErrors = {}
    for signatureInfo in signatureEntries.values():
        libraryName = signatureInfo.get("library")
        if not libraryName or libraryName in sectionsByLibrary or libraryName in moduleErrors:
            continue
        modulePath = resolveModulePath(binariesRoot, libraryName, platformName)
        if modulePath is None:
            baseName = LibraryBaseNames.get(libraryName, libraryName)
            moduleErrors[libraryName] = (
                f"{PlatformFileNames[platformName](baseName)} not found under {binariesRoot}"
            )
            continue
        try:
            sectionsByLibrary[libraryName] = readExecutableSections(modulePath)
            modulePaths[libraryName] = modulePath
        except BinaryError as error:
            moduleErrors[libraryName] = f"{modulePath}: {error}"

    results = []
    for name, signatureInfo in sorted(signatureEntries.items()):
        libraryName = signatureInfo.get("library") or ""
        patternText = signatureInfo.get(platformName)

        if not libraryName:
            results.append((name, libraryName, StatusInvalid, 0, [], "missing library"))
            continue
        if not patternText:
            results.append(
                (name, libraryName, StatusNotFound, 0, [], f"no {platformName} pattern")
            )
            continue
        if libraryName in moduleErrors:
            results.append(
                (name, libraryName, StatusSkipped, 0, [], moduleErrors[libraryName])
            )
            continue

        try:
            pattern = compilePattern(patternText)
        except ValueError as error:
            results.append((name, libraryName, StatusInvalid, 0, [], str(error)))
            continue

        matches = scanSections(sectionsByLibrary[libraryName], pattern, matchLimit)
        if len(matches) == 0:
            status = StatusNotFound
        elif len(matches) == 1:
            status = StatusFound
        else:
            status = StatusNotUnique
        results.append((name, libraryName, status, len(matches), matches[:8], ""))

    return results, modulePaths, moduleErrors


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--binaries",
        required=True,
        type=Path,
        help="directory holding the CS2 binaries (searched recursively)",
    )
    parser.add_argument("--gamedata", type=Path, default=DefaultGamedataPath)
    parser.add_argument(
        "--platform",
        choices=["linux", "windows", "both"],
        default="both",
        help="which patterns to check (default: both)",
    )
    parser.add_argument(
        "--match-limit",
        type=int,
        default=64,
        help="stop counting a pattern's matches past this many (default: 64)",
    )
    parser.add_argument("--json", type=Path, help="also write the full report here")
    parser.add_argument(
        "--quiet",
        action="store_true",
        help="print only failures and the summary",
    )
    arguments = parser.parse_args()

    if not arguments.binaries.is_dir():
        print(f"FAILED: not a directory: {arguments.binaries}")
        return 1

    with arguments.gamedata.open("r", encoding="utf-8") as fileHandle:
        gamedata = json.load(fileHandle)

    platformNames = (
        ["linux", "windows"] if arguments.platform == "both" else [arguments.platform]
    )

    report = {}
    failures = 0
    for platformName in platformNames:
        results, modulePaths, moduleErrors = scanPlatform(
            gamedata, arguments.binaries, platformName, arguments.match_limit
        )

        print(f"=== {platformName}")
        for libraryName, modulePath in sorted(modulePaths.items()):
            print(f"  {libraryName}: {modulePath}")
        for libraryName, error in sorted(moduleErrors.items()):
            print(f"  {libraryName}: {error}")

        counts = {}
        for name, libraryName, status, matchCount, matches, note in results:
            counts[status] = counts.get(status, 0) + 1
            if arguments.quiet and status == StatusFound:
                continue
            addressText = (
                ", ".join(f"{sectionName}+0x{offset:X}" for sectionName, offset in matches)
                or "-"
            )
            if matchCount > len(matches):
                addressText += f", ... (+{matchCount - len(matches)})"
            noteText = f" ({note})" if note else ""
            print(f"  [{status}] {name} matches={matchCount} at {addressText}{noteText}")

        summaryText = " ".join(
            f"{status}={counts.get(status, 0)}"
            for status in (StatusFound, StatusNotFound, StatusNotUnique, StatusInvalid, StatusSkipped)
        )
        print(f"  summary: {summaryText}")

        failures += sum(
            counts.get(status, 0)
            for status in (StatusNotFound, StatusNotUnique, StatusInvalid, StatusSkipped)
        )
        report[platformName] = {
            "modules": {name: str(path) for name, path in modulePaths.items()},
            "moduleErrors": moduleErrors,
            "summary": counts,
            "results": [
                {
                    "name": name,
                    "library": libraryName,
                    "status": status,
                    "matchCount": matchCount,
                    "addresses": [f"{sectionName}+0x{offset:X}" for sectionName, offset in matches],
                    "note": note,
                }
                for name, libraryName, status, matchCount, matches, note in results
            ],
        }

    if arguments.json:
        arguments.json.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"wrote {arguments.json}")

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
