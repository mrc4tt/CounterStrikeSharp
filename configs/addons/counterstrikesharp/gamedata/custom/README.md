# gamedata/custom/

Your own signatures and offsets for CounterStrikeSharp's **core** gamedata go here, not into
`../gamedata.json`. The auto-updater replaces `gamedata.json` whenever the feed publishes a new
one; it never touches this folder.

Every `*.json` file here is layered on top of `gamedata.json` at startup, in file-name order
(later files win), by both the native side and managed plugins.

```json
{
  "_gameVersion": "1.41.9.2",
  "Host_Say": {
    "signatures": { "linux": "55 48 89 E5 41 57 ..." }
  },
  "MyExtraFunction": {
    "signatures": { "library": "server", "linux": "48 8B ...", "windows": "40 53 ..." }
  }
}
```

- **Per field.** Only the fields you write are replaced. Above, `Host_Say` keeps its Windows
  signature and library from `gamedata.json`. This applies to `signatures`, `offsets`,
  `patches` and `vtable`.
- **New keys** that `gamedata.json` does not have are added.
- **`_gameVersion` (optional)** pins the file to one game build (`steam.inf` PatchVersion;
  `"1.41.9.2"` and `"14192"` are the same). On any other build the whole file is skipped and
  `gamedata.json` applies, so an override written for an old build cannot outlive a CS2 update.
  Without it, the file always applies.
- Keys starting with `_` are notes/metadata and are ignored.
- An overridden signature that does not resolve while the `gamedata.json` one does is not
  applied; the server log says which file and key.
- Every applied override is logged at startup: `Gamedata override: Host_Say [signatures.linux] <- custom/fix.json`.

`_rescued.json` is written by the updater: when it replaces a `gamedata.json` that was edited by
hand, keys the feed does not have are moved here (and the old file is copied to
`../backup/`). Keep it, edit it, or delete it.

Plugin gamedata files (`gamedata/<plugin>.json`) are not affected by this folder.
