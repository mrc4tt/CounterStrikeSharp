# RayTrace compatibility plugin

`RayTraceApi` (shared library) and `RayTraceImpl` (plugin) keep the `raytrace:craytraceinterface`
plugin capability working for plugins written against the original Ray-Trace project
(FUNPLAY-pro-CS2/Ray-Trace, GPL-3.0, by Slynx — archived upstream, so the sources live here now).

The original needed a native RayTrace Metamod plugin. This copy does not: `RayTraceImpl` forwards
every call to the core `CounterStrikeSharp.API.Modules.Utils.Trace` API.

- `TraceShape` / `TraceEndShape` / `TraceHullShape` — forwarded, always return `true`.
- `TraceShapeEx(filter, ray)` — not supported (core has no entry point taking a raw
  `CTraceFilter` / `Ray_t`); returns `false` and warns once.

New plugins should call `Trace.*` directly instead of going through the capability.

Deploy layout (what `build.sh` produces):

```
addons/counterstrikesharp/plugins/RayTraceImpl/RayTraceImpl.dll
addons/counterstrikesharp/shared/RayTraceApi/RayTraceApi.dll
```
