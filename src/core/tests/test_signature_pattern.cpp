// KHook's signature parser against the two gamedata signature styles.
//
// Compiles KHook's real Ranges::Lookup (ranges.cpp) and the production
// SignatureToSpacedHex (core/signature_pattern.h). The crash this pins: a plugin passed
// CBaseEntity::SetParent as "\x48\x85\xF6...\xB8\x2A\x2A\x2A\x2A". KHook split on spaces,
// found none, parsed only the trailing "2A", and returned the first 0x2A byte of the
// executable segment - libserver.so + 0x9f4c72, inside .plt. CModule::FindSignature now
// hands KHook the space-separated form of the parsed bytes instead.

#include "catch_amalgamated.hpp"

#include "core/signature_pattern.h"

#include "ranges.hpp"

#include <cstdint>
#include <string>
#include <vector>

namespace {

// Code-style parse, mirroring CGameConfig::HexToByte (which logs, so it cannot link here):
// "\x2A" is a wildcard in that style, as in SourceMod gamedata.
std::vector<int16_t> ParseCodeStyle(const std::string& src)
{
    std::vector<int16_t> out;
    for (std::size_t pos = 0; (pos = src.find("\\x", pos)) != std::string::npos; pos += 4)
    {
        const auto byte = src.substr(pos + 2, 2);
        out.push_back(byte == "2A" ? int16_t(-1) : int16_t(std::stoi(byte, nullptr, 16)));
    }
    return out;
}

// .plt-like bytes containing a stray 0x2A, then the real function body.
struct Image
{
    std::vector<std::uint8_t> bytes;
    std::size_t function_offset;

    Image()
    {
        bytes = { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00, 0xCC, 0xCC };
        function_offset = bytes.size();
        const std::uint8_t body[] = { 0x48, 0x85, 0xF6, 0x74, 0x10, 0x48, 0x8B, 0x47, 0x10, 0xF6, 0x40, 0x31, 0x02, 0x75, 0x07,
                                      0x48, 0x8B, 0x46, 0x10, 0xF6, 0x40, 0x31, 0x02, 0x75, 0x05, 0xB8, 0x01, 0x00, 0x00, 0x00 };
        bytes.insert(bytes.end(), std::begin(body), std::end(body));
        bytes.insert(bytes.end(), 16, 0xCC);
    }

    std::uintptr_t start() const { return reinterpret_cast<std::uintptr_t>(bytes.data()); }
    std::uintptr_t lookup(const std::string& pattern) const { return KHook::Ranges::Lookup(start(), bytes.size(), pattern); }
};

const std::string kCodeStyle = "\\x48\\x85\\xF6\\x74\\x2A\\x48\\x8B\\x47\\x10\\xF6\\x40\\x31\\x02\\x75\\x2A"
                               "\\x48\\x8B\\x46\\x10\\xF6\\x40\\x31\\x02\\x75\\x2A\\xB8\\x2A\\x2A\\x2A\\x2A";
const std::string kIdaStyle = "48 85 F6 74 ? 48 8B 47 10 F6 40 31 02 75 ? 48 8B 46 10 F6 40 31 02 75 ? B8 ? ? ? ?";

} // namespace

TEST_CASE("KHook never resolves a raw code-style signature to the function", "[signature]")
{
    const Image image;
    // Metamod's KHook scans it as its last byte alone and lands on the stray 0x2A at +6; a
    // KHook with the strict token parser returns 0. Either way it is not the function, which
    // is why FindSignature must normalise before calling KHook - the KHook that runs is
    // Metamod's, not the one in this tree.
    const auto result = image.lookup(kCodeStyle);
    CHECK((result == 0 || result == image.start() + 6));
    CHECK(result != image.start() + image.function_offset);
}

TEST_CASE("Normalised code-style and IDA-style signatures resolve to the function", "[signature]")
{
    const Image image;
    const auto expected = image.start() + image.function_offset;

    const auto normalised = counterstrikesharp::SignatureToSpacedHex(ParseCodeStyle(kCodeStyle));
    CHECK(normalised == kIdaStyle);
    CHECK(image.lookup(normalised) == expected);
    CHECK(image.lookup(kIdaStyle) == expected);
}

TEST_CASE("SignatureToSpacedHex renders wildcards and pads single digits", "[signature]")
{
    CHECK(counterstrikesharp::SignatureToSpacedHex({ 0x05, -1, 0xAB }) == "05 ? AB");
    CHECK(counterstrikesharp::SignatureToSpacedHex({}).empty());
}
