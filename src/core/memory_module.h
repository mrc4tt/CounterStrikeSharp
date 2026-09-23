/**
 * =============================================================================
 * CS2Fixes
 * Copyright (C) 2023 Source2ZE
 * =============================================================================
 *
 * This program is free software; you can redistribute it and/or modify it under
 * the terms of the GNU General Public License, version 3.0, as published by the
 * Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * this program.  If not, see <http://www.gnu.org/licenses/>.
 */

#pragma once
#include <cstdio>
#include <cstdint>
#include <unordered_map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#if __linux__
#include <link.h>
#endif

#include "interface.h"
#include "strtools.h"
#undef snprintf

namespace counterstrikesharp::modules {

struct Section
{
    std::string m_szName;
    void* m_pBase;
    size_t m_iSize;
};

class SignatureIterator
{
  public:
    SignatureIterator(void* pBase, size_t iSize, const byte* pSignature, size_t iSigLength)
        : m_pBase((byte*)pBase), m_iSize(iSize), m_pSignature(pSignature), m_iSigLength(iSigLength)
    {
        m_pCurrent = m_pBase;
    }

    // Same contract as before: returns the first match at or after the previous one, and leaves
    // the cursor one byte past it so a repeated call finds overlapping matches too.
    //
    // The bounds are the fix. The old loop ran its index over the FULL size on every call while
    // comparing from the advanced cursor, so a second call read up to (cursor - base) bytes past
    // the end of the region - straight out of .data.rel.ro in FindVirtualTable's match loop - and
    // the inner while had no length guard of its own either.
    void* FindNext(bool allowWildcard)
    {
        if (m_iSigLength == 0 || m_iSize < m_iSigLength) return nullptr;

        byte* last = m_pBase + m_iSize - m_iSigLength;

        for (byte* current = m_pCurrent; current <= last; ++current)
        {
            size_t Matches = 0;
            while (Matches < m_iSigLength &&
                   (current[Matches] == m_pSignature[Matches] || (allowWildcard && m_pSignature[Matches] == '\x2A')))
            {
                Matches++;
            }

            if (Matches == m_iSigLength)
            {
                m_pCurrent = current + 1;
                return current;
            }
        }

        m_pCurrent = last + 1;
        return nullptr;
    }

  private:
    byte* m_pBase;
    size_t m_iSize;
    const byte* m_pSignature;
    size_t m_iSigLength;
    byte* m_pCurrent;
};

struct Segments
{
    Segments() = default;

    Segments(const Segments&) = default;
    Segments(Segments&&) = default;
    Segments& operator=(const Segments&) = default;
    Segments& operator=(Segments&&) = default;

    std::uintptr_t address{};
    std::vector<std::uint8_t> bytes{};
};

class CModule
{
  public:
#ifdef _WIN32
    CModule(std::string_view path, std::uint64_t base);
#else
    CModule(std::string_view path, struct dl_phdr_info* info);
#endif

    void* FindSignature(const char* signature);

    void* FindInterface(std::string_view name);

    void* FindSymbol(const std::string& name);

    void* FindVirtualTable(const std::string& name);

    Section* GetSection(const std::string_view name)
    {
        for (auto& section : m_sections)
            if (section.m_szName == name) return &section;

        return nullptr;
    }

    // Section that holds `address`, or nullptr when the address is outside this module's sections.
    const Section* FindSectionContaining(const void* address) const
    {
        const auto target = reinterpret_cast<std::uintptr_t>(address);
        for (const auto& section : m_sections)
        {
            const auto base = reinterpret_cast<std::uintptr_t>(section.m_pBase);
            if (section.m_iSize != 0 && target >= base && target < base + section.m_iSize) return &section;
        }

        return nullptr;
    }

    [[nodiscard]] bool IsInitialized() const { return m_bInitialized; }

    std::string m_pszModule{};
    std::string m_pszPath{};
    void* m_base{};
    size_t m_size{};

  private:
    bool m_bInitialized{};
    std::vector<Segments> m_vecSegments{};
    std::vector<Section> m_sections{};
    std::uintptr_t m_baseAddress{};
    std::unordered_map<std::string, std::uintptr_t> _symbols{};
    std::unordered_map<std::string, std::uintptr_t> _interfaces{};
    using fnCreateInterface = void* (*)(const char*);
    fnCreateInterface m_fnCreateInterface{};

#ifdef _WIN32
    void DumpSymbols();
#else
    void DumpSymbols(ElfW(Dyn) * dyn);
#endif

    std::optional<std::vector<std::uint8_t>>
    GetOriginalBytes(const std::vector<std::uint8_t>& disk_data, std::uintptr_t rva, std::size_t size);

    // Internal scanners over the on-disk bytes and the live mapping respectively. Both skip
    // matches that land in a linker stub (see IsStubAddress) and keep scanning.
    void* FindSignature(const std::vector<int16_t>& sigBytes, const char* signature) const;
    void* FindSignatureAlternative(const std::vector<int16_t>& sigBytes, const char* signature) const;

    template <typename Accept>
    static const std::uint8_t*
    ScanForSignature(const std::uint8_t* data, std::size_t size, const std::vector<int16_t>& sigBytes, Accept&& accept);

    // Counts live matches, stopping once `limit` have been seen. Used to tell a unique
    // signature from one that merely happens to resolve first.
    std::size_t CountSignatureMatches(const std::vector<int16_t>& sigBytes, std::size_t limit) const;

    // Logs when a signature matches more than one address. Resolution picks the first match, so
    // an ambiguous pattern silently yields "a" function rather than "the" function.
    void WarnIfAmbiguous(const char* signature, const std::vector<int16_t>& sigBytes) const;

    // True (and logged) when a match landed in a linker stub or a relocation table instead of
    // real code. Those are never a legitimate signature target, and calling one is fatal:
    // PLT0 of a BIND_NOW module jumps through an unfilled GOT slot straight to rip = 0.
    // Scanners skip such a match and keep going rather than ending the search on it.
    bool IsStubAddress(const void* address, const char* signature) const;
};

} // namespace counterstrikesharp::modules
