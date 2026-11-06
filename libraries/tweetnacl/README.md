# TweetNaCl

Vendored, unmodified, from https://tweetnacl.cr.yp.to/20140427/ (public domain):

| file | sha256 |
|---|---|
| tweetnacl.c | 02e65bc3013ff2168983365e55906bc783c4c7e0a60d8100f17bb303a17175c4 |
| tweetnacl.h | 43f29ad721d9927b747b0100ab4160c119e7bb180c7c98a66e4bf79d31244287 |

Only `crypto_sign_open` (Ed25519 with SHA-512) is used, by `src/core/gamedata_signature.cpp`, to
check the signature on VibeSignatures' `latest/manifest.json`. `randombytes`, which TweetNaCl
leaves to the embedder for key generation, is defined there as a stub that aborts: nothing in
CounterStrikeSharp generates keys.

Keep the files byte-identical to the upstream release, so the hashes above stay checkable.
