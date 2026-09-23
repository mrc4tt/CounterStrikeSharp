#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace counterstrikesharp {

// The space-separated IDA form ("48 8B ? ...") of a parsed signature, "?" for a wildcard.
//
// This is the only form KHook::LookupSignature understands: its parser splits on spaces and
// reads each token's last two characters. A code-style "\x48\x8B\x2A..." pattern has no
// spaces, so KHook parsed only its final two characters and scanned for that single byte -
// for a pattern ending in "\x2A" that is the first 0x2A in the executable segment, which on
// libserver.so is inside .plt. Every signature handed to KHook goes through this first.
//
// Header-only and free of logging so the unit tests can use the production definition.
inline std::string SignatureToSpacedHex(const std::vector<int16_t>& bytes)
{
    static constexpr char digits[] = "0123456789ABCDEF";

    std::string result;
    result.reserve(bytes.size() * 3);

    for (const auto byte : bytes)
    {
        if (!result.empty()) result += ' ';

        if (byte < 0)
        {
            result += '?';
            continue;
        }

        result += digits[(byte >> 4) & 0xF];
        result += digits[byte & 0xF];
    }

    return result;
}

} // namespace counterstrikesharp
