#pragma once

// Ed25519 check of VibeSignatures' latest/manifest.json (see gameconfig_updater.cpp). The manifest
// names every published file's sha256, so a manifest that verifies vouches for every file the
// updater applies; whoever can change what the feed's host serves cannot make a server load
// signatures VibeSignatures did not publish. Engine-free, unit-tested in
// tests/test_gamedata_update_guard.cpp.

#include <optional>
#include <string>

namespace counterstrikesharp::update::signing {

// Raw 32-byte Ed25519 public key, base64. VibeSignatures holds the private key (the Pages deploy's
// GAMEDATA_SIGNING_KEY secret) and pins the same public key in pages/verifyGameDataAssets.mjs;
// change both together. core.json's GamedataSigningKey overrides it, "" turns the check off.
inline constexpr const char* kDefaultPublicKey = "FKRxMgGEAIoWuRBmwgG/yAA9FxvcBkA44SAKxg7bfUo=";

// Standard base64 (RFC 4648, '=' padding optional); surrounding whitespace is ignored.
std::optional<std::string> Base64Decode(const std::string& text);

// True when `signatureText` (base64 of a 64-byte Ed25519 signature, as in manifest.json.sig) is a
// valid signature of exactly `message` by `publicKeyBase64`. `error` says why not otherwise.
bool VerifySignature(const std::string& message, const std::string& signatureText, const std::string& publicKeyBase64, std::string& error);

} // namespace counterstrikesharp::update::signing
