#pragma once

// Ed25519 check of VibeSignatures' latest/manifest.json (see gameconfig_updater.cpp). The manifest
// names every published file's sha256, so a manifest that verifies vouches for every file the
// updater applies; whoever can change what the feed's host serves cannot make a server load
// signatures VibeSignatures did not publish. Engine-free, unit-tested in
// tests/test_gamedata_update_guard.cpp.

#include <optional>
#include <string>

namespace counterstrikesharp::update::signing {

// Raw 32-byte Ed25519 public keys, base64, separated by spaces or commas; a manifest signed by any
// of them is accepted. The first signs the feed today: VibeSignatures holds its private key (on re
// and as the Pages mirror's GAMEDATA_SIGNING_KEY secret) and pins it in
// pages/verifyGameDataAssets.mjs. The second is a reserve whose private key is kept offline and
// has never signed anything: if the first is lost or leaks, the feed switches to the reserve and
// every server already running this build keeps updating; a release then drops the old key.
// core.json's GamedataSigningKey replaces the list, "" turns the check off.
inline constexpr const char* kDefaultPublicKey = "FKRxMgGEAIoWuRBmwgG/yAA9FxvcBkA44SAKxg7bfUo=";
inline constexpr const char* kReservePublicKey = "y2gtgoRQOtxtQ24awuhKxGErXUbOo9zepTT1n08h/JU=";
inline constexpr const char* kDefaultPublicKeys =
    "FKRxMgGEAIoWuRBmwgG/yAA9FxvcBkA44SAKxg7bfUo= y2gtgoRQOtxtQ24awuhKxGErXUbOo9zepTT1n08h/JU=";

// Standard base64 (RFC 4648, '=' padding optional); surrounding whitespace is ignored.
std::optional<std::string> Base64Decode(const std::string& text);

// True when `signatureText` (base64 of a 64-byte Ed25519 signature, as in manifest.json.sig) is a
// valid signature of exactly `message` by `publicKeyBase64`. `error` says why not otherwise.
bool VerifySignature(const std::string& message, const std::string& signatureText, const std::string& publicKeyBase64, std::string& error);

// VerifySignature against each key in `publicKeys` (base64, separated by spaces or commas); true
// when one of them verifies. `error` describes the last failure otherwise.
bool VerifySignatureWithAnyKey(const std::string& message,
                               const std::string& signatureText,
                               const std::string& publicKeys,
                               std::string& error);

} // namespace counterstrikesharp::update::signing
