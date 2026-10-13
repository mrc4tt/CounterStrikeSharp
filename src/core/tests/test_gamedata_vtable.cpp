// Parsing of the gamedata "vtable" block (core/gamedata_vtable.h): which entries carry a slot,
// and how the signature-text index the scanner consults is built across files.

#include "catch_amalgamated.hpp"

#include "core/gamedata_vtable.h"

using counterstrikesharp::gamedata::GameverMarker;
using counterstrikesharp::gamedata::GameverMarkerMatches;
using counterstrikesharp::gamedata::ParseVtableSlot;
using counterstrikesharp::gamedata::VtableFallbacks;
using nlohmann::json;

namespace {

json Entry(const std::string& linuxSig, json vtable)
{
    json entry = { { "signatures", { { "library", "server" }, { "linux", linuxSig }, { "windows", "WIN" } } } };
    if (!vtable.is_null()) entry["vtable"] = std::move(vtable);
    return entry;
}

json Slot(const std::string& className, int linuxIndex, int windowsIndex)
{
    return { { "class", className }, { "linux_index", linuxIndex }, { "windows_index", windowsIndex } };
}

} // namespace

TEST_CASE("a vtable block yields the slot for the requested platform", "[gamedata_vtable]")
{
    auto entry = Entry("AA BB CC DD", Slot("CBaseTrigger", 151, 152));

    auto linuxSlot = ParseVtableSlot(entry, "linux");
    auto windowsSlot = ParseVtableSlot(entry, "windows");

    REQUIRE(linuxSlot);
    CHECK(linuxSlot->className == "CBaseTrigger");
    CHECK(linuxSlot->index == 151);
    REQUIRE(windowsSlot);
    CHECK(windowsSlot->index == 152);
}

TEST_CASE("slot 0 is a valid slot", "[gamedata_vtable]")
{
    auto slot = ParseVtableSlot(Entry("AA BB CC DD", Slot("CEntityResourceManifest", 0, 2)), "linux");

    REQUIRE(slot);
    CHECK(slot->index == 0);
}

TEST_CASE("incomplete or malformed vtable blocks are ignored", "[gamedata_vtable]")
{
    CHECK_FALSE(ParseVtableSlot(Entry("AA", json()), "linux"));
    CHECK_FALSE(ParseVtableSlot(Entry("AA", { { "linux_index", 3 } }), "linux"));
    CHECK_FALSE(ParseVtableSlot(Entry("AA", { { "class", "CFoo" } }), "linux"));
    CHECK_FALSE(ParseVtableSlot(Entry("AA", { { "class", "CFoo" }, { "windows_index", 3 } }), "linux"));
    CHECK_FALSE(ParseVtableSlot(Entry("AA", { { "class", "CFoo" }, { "linux_index", "3" } }), "linux"));
    CHECK_FALSE(ParseVtableSlot(Entry("AA", { { "class", "" }, { "linux_index", 3 } }), "linux"));
    CHECK_FALSE(ParseVtableSlot(Entry("AA", { { "class", "CFoo" }, { "linux_index", -1 } }), "linux"));
    CHECK_FALSE(ParseVtableSlot(Entry("AA", "CFoo"), "linux"));
}

TEST_CASE("fallbacks are keyed by the platform's signature text", "[gamedata_vtable]")
{
    VtableFallbacks fallbacks;
    fallbacks.Add({ { "EndTouch", Entry("AA BB CC DD", Slot("CBaseTrigger", 151, 152)) },
                    { "NoSlot", Entry("11 22 33 44", json()) },
                    { "Symbol", Entry("@_ZN3Foo3BarEv", Slot("CFoo", 1, 1)) },
                    { "Offset", { { "offsets", { { "linux", 5 } } }, { "vtable", Slot("CFoo", 5, 5) } } } },
                  "linux", true);

    CHECK(fallbacks.Size() == 1);
    const auto* found = fallbacks.Find("AA BB CC DD");
    REQUIRE(found);
    CHECK(found->key == "EndTouch");
    CHECK(found->slot.index == 151);
    CHECK_FALSE(fallbacks.Find("WIN"));
}

TEST_CASE("the same signature with the same slot in two files is kept", "[gamedata_vtable]")
{
    VtableFallbacks fallbacks;
    fallbacks.Add({ { "A", Entry("AA BB CC DD", Slot("CFoo", 4, 5)) } }, "linux", true);
    fallbacks.Add({ { "B", Entry("AA BB CC DD", Slot("CFoo", 4, 5)) } }, "linux", true);

    CHECK(fallbacks.Find("AA BB CC DD"));
    CHECK(fallbacks.Conflicting().empty());
}

TEST_CASE("the same signature with two different slots is dropped for good", "[gamedata_vtable]")
{
    VtableFallbacks fallbacks;
    fallbacks.Add({ { "A", Entry("AA BB CC DD", Slot("CFoo", 4, 5)) } }, "linux", true);
    fallbacks.Add({ { "B", Entry("AA BB CC DD", Slot("CFoo", 9, 9)) } }, "linux", true);
    fallbacks.Add({ { "C", Entry("AA BB CC DD", Slot("CFoo", 4, 5)) } }, "linux", true);

    CHECK_FALSE(fallbacks.Find("AA BB CC DD"));
    CHECK(fallbacks.Conflicting().count("AA BB CC DD") == 1);
}

TEST_CASE("a slot is trusted when any file vouching for it is trusted", "[gamedata_vtable]")
{
    VtableFallbacks fallbacks;
    fallbacks.Add({ { "Plugin", Entry("AA BB CC DD", Slot("CFoo", 4, 5)) } }, "linux", false);
    fallbacks.Add({ { "Core", Entry("AA BB CC DD", Slot("CFoo", 4, 5)) }, { "Other", Entry("11 22 33 44", Slot("CBar", 1, 1)) } }, "linux",
                  true);
    fallbacks.Add({ { "Late", Entry("11 22 33 44", Slot("CBar", 1, 1)) } }, "linux", false);

    REQUIRE(fallbacks.Find("AA BB CC DD"));
    CHECK(fallbacks.Find("AA BB CC DD")->trusted);
    REQUIRE(fallbacks.Find("11 22 33 44"));
    CHECK(fallbacks.Find("11 22 33 44")->trusted);
}

TEST_CASE("an untrusted file's slot stays untrusted", "[gamedata_vtable]")
{
    VtableFallbacks fallbacks;
    fallbacks.Add({ { "Plugin", Entry("AA BB CC DD", Slot("CFoo", 4, 5)) } }, "linux", false);

    REQUIRE(fallbacks.Find("AA BB CC DD"));
    CHECK_FALSE(fallbacks.Find("AA BB CC DD")->trusted);
}

TEST_CASE("the gamever marker vouches for one build and one file content", "[gamedata_vtable]")
{
    const std::string contents = R"({"A":{"signatures":{"linux":"AA"}}})";
    const auto marker = GameverMarker("14190", contents);

    CHECK(marker.rfind("14190 ", 0) == 0);
    CHECK(marker.size() == 6 + 16);
    CHECK(GameverMarkerMatches(marker, "14190", contents));
    CHECK_FALSE(GameverMarkerMatches(marker, "14191", contents));
    CHECK_FALSE(GameverMarkerMatches(marker, "14190", contents + " "));
    CHECK_FALSE(GameverMarkerMatches(marker, "", contents));
    CHECK_FALSE(GameverMarkerMatches("", "14190", contents));
}
