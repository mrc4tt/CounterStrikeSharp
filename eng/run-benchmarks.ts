#!/usr/bin/env -S deno run --allow-run --allow-read --allow-env --allow-net --allow-write

import $ from "https://deno.land/x/dax@0.39.2/mod.ts";
import { load } from "https://deno.land/std@0.224.0/dotenv/mod.ts";
import { z } from "https://deno.land/x/zod@v3.22.4/mod.ts";

const env = { ...Deno.env.toObject(), ...await load({ export: true }) };

const config = z.object({
  GS_HOST: z.string().min(1),
  GS_PORT: z.string().min(1),
  GS_PASS: z.string().min(1),
  SFTP_HOST: z.string().min(1),
  SFTP_USER: z.string().min(1),
  SFTP_PASS: z.string().min(1),
  GS_PLUGIN_DIR: z.string().default("/game/csgo/addons/counterstrikesharp/plugins/NativeTestsPlugin"),
  GS_API_DIR: z.string().default("/game/csgo/addons/counterstrikesharp/api"),
  GS_NATIVE_DIR: z.string().default("/game/csgo/addons/counterstrikesharp/bin/linuxsteamrt64"),
  PTERO_URL: z.string().min(1),
  PTERO_API_KEY: z.string().min(1),
  PTERO_SERVER_ID: z.string().min(1),
  GS_RESTART_TIMEOUT: z.string().default("120"),
}).parse(env);

const HERE = $.path(import.meta).parentOrThrow();
const ROOT = HERE.parentOrThrow();

const paths = {
  apiProject: ROOT.join("managed/CounterStrikeSharp.API"),
  apiBuild: ROOT.join("managed/CounterStrikeSharp.API/bin/Release/net10.0"),
  testProject: ROOT.join("managed/CounterStrikeSharp.Tests.Native/NativeTestsPlugin.csproj"),
  testBuild: ROOT.join("managed/CounterStrikeSharp.Tests.Native/bin/Debug/net10.0"),
  nativeSo: ROOT.join("build/addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so"),
  results: ROOT.join("TestResults/Benchmarks"),
};

const rcon = `${HERE}/rcon`;
const remoteJson = `${config.GS_PLUGIN_DIR}/benchmark-results.json`;
const remoteMd = `${config.GS_PLUGIN_DIR}/benchmark-results.md`;

async function poll(timeoutSec: number, intervalMs: number, label: string, check: () => Promise<void>) {
  const start = Date.now();
  while (Date.now() - start < timeoutSec * 1000) {
    try {
      await check();
      return;
    } catch {
      const elapsed = Math.round((Date.now() - start) / 1000);
      $.logLight(`  ${label} (${elapsed}s elapsed)...`);
    }
    await $.sleep(intervalMs);
  }
  throw new Error(`${label}: timed out after ${timeoutSec}s`);
}

// ── Build ───────────────────────────────────────────────────────────────

$.logStep("Building API...");
await $`dotnet build -c Release`.cwd(paths.apiProject.toString());

$.logStep("Building NativeTestsPlugin...");
await $`dotnet build ${paths.testProject} -c Debug`;

// ── Upload ──────────────────────────────────────────────────────────────

$.logStep("Uploading API...");
await $`lftp -u ${config.SFTP_USER},${config.SFTP_PASS} ${config.SFTP_HOST} -e ${`set xfer:clobber on; mkdir -p ${config.GS_API_DIR}; mirror -R --delete ${paths.apiBuild} ${config.GS_API_DIR}; bye`}`.quiet();

if (await Deno.stat(paths.nativeSo.toString()).then(() => true).catch(() => false)) {
  $.logStep("Uploading native .so...");
  await $`lftp -u ${config.SFTP_USER},${config.SFTP_PASS} ${config.SFTP_HOST} -e ${`set xfer:clobber on; mkdir -p ${config.GS_NATIVE_DIR}; put ${paths.nativeSo} -o ${config.GS_NATIVE_DIR}/counterstrikesharp.so; bye`}`.quiet();
}

$.logStep("Uploading NativeTestsPlugin...");
await $`lftp -u ${config.SFTP_USER},${config.SFTP_PASS} ${config.SFTP_HOST} -e ${`set xfer:clobber on; mkdir -p ${config.GS_PLUGIN_DIR}; mirror -R --delete ${paths.testBuild} ${config.GS_PLUGIN_DIR}; bye`}`.quiet();

// ── Restart server ──────────────────────────────────────────────────────

const pteroHeaders = {
  "Authorization": `Bearer ${config.PTERO_API_KEY}`,
  "Content-Type": "application/json",
  "Accept": "application/json",
};

$.logStep("Restarting game server...");
const resp = await fetch(`${config.PTERO_URL}/api/client/servers/${config.PTERO_SERVER_ID}/power`, {
  method: "POST",
  headers: pteroHeaders,
  body: JSON.stringify({ signal: "restart" }),
});
if (!resp.ok) {
  $.logError(`Restart failed (${resp.status}): ${await resp.text()}`);
  Deno.exit(1);
}

await $.sleep(10_000);

const restartTimeout = parseInt(config.GS_RESTART_TIMEOUT, 10);
await poll(restartTimeout, 5_000, "Server not ready yet", async () => {
  const out = await $`${rcon} -a ${config.GS_HOST}:${config.GS_PORT} -p ${config.GS_PASS} "status"`.text();
  if (!out) throw new Error();
});

$.logStep("Server is back online.");
await $.sleep("10s");

// ── Run benchmarks ──────────────────────────────────────────────────────

$.logStep("Loading plugin...");
try {
  await $`${rcon} -a ${config.GS_HOST}:${config.GS_PORT} -p ${config.GS_PASS} "css_plugins load NativeTestsPlugin"`.text();
} catch {
  await $`${rcon} -a ${config.GS_HOST}:${config.GS_PORT} -p ${config.GS_PASS} "css_plugins reload NativeTestsPlugin"`.text();
}

// Delete stale results so we can detect when new ones land
$.logStep("Clearing old results...");
try {
  await $`lftp -u ${config.SFTP_USER},${config.SFTP_PASS} ${config.SFTP_HOST} -e ${`rm -f ${remoteJson}; rm -f ${remoteMd}; bye`}`.quiet();
} catch { /* may not exist */ }

$.logStep("Running: css_itest benchmark");
console.log(await $`${rcon} -a ${config.GS_HOST}:${config.GS_PORT} -p ${config.GS_PASS} -T 120s "css_itest benchmark"`.text());

// Async benchmarks (entity creation) span multiple frames, so the RCON
// command returns before results are written. Poll until the file appears.
await poll(300, 5_000, "Waiting for results", async () => {
  await $`lftp -u ${config.SFTP_USER},${config.SFTP_PASS} ${config.SFTP_HOST} -e ${`cat ${remoteJson}; bye`}`.quiet();
});

await $.sleep(2_000); // let the .md flush too

// ── Download results ────────────────────────────────────────────────────

$.logStep("Downloading results...");
await Deno.mkdir(paths.results.toString(), { recursive: true });

const localJson = paths.results.join("benchmark-results.json").toString();
const localMd = paths.results.join("benchmark-results.md").toString();

await $`lftp -u ${config.SFTP_USER},${config.SFTP_PASS} ${config.SFTP_HOST} -e ${`set xfer:clobber on; get ${remoteJson} -o ${localJson}; get ${remoteMd} -o ${localMd}; bye`}`.quiet();

// ── Capture bench-host environment (best-effort) ──────────────────────────
// Benchmarks run on the remote CS2 server, so CPU/OS/memory must come from
// THAT box, not the build machine. We read them over SFTP. Jailed SFTP
// accounts (rooted at the game dir) can't see /proc or /etc — tolerate that
// and fall back to "unknown" so a missing header never aborts the run.

async function fetchRemote(remotePath: string): Promise<string | null> {
  const tmp = await Deno.makeTempFile();
  try {
    await $`lftp -u ${config.SFTP_USER},${config.SFTP_PASS} ${config.SFTP_HOST} -e ${`set xfer:clobber on; get ${remotePath} -o ${tmp}; bye`}`.quiet();
    const text = await Deno.readTextFile(tmp);
    return text.length ? text : null;
  } catch {
    return null;
  } finally {
    await Deno.remove(tmp).catch(() => {});
  }
}

$.logStep("Capturing bench-host environment...");
const cpuinfo = await fetchRemote("/proc/cpuinfo");
const osRelease = await fetchRemote("/etc/os-release");
const meminfo = await fetchRemote("/proc/meminfo");

const cpuModel = cpuinfo?.match(/^model name\s*:\s*(.+)$/m)?.[1].trim() ?? "unknown";
const cpuCount = cpuinfo ? (cpuinfo.match(/^processor\s*:/gm)?.length ?? 0) : 0;
const osName = osRelease?.match(/^PRETTY_NAME="?(.*?)"?$/m)?.[1].trim() ?? "unknown";
const memKb = parseInt(meminfo?.match(/^MemTotal:\s*(\d+)\s*kB/m)?.[1] ?? "0", 10);
const memTotal = memKb ? `${(memKb / 1024 / 1024).toFixed(1)} GB` : "unknown";
const runtimeTfm = "net10.0"; // managed bench host target framework

if (cpuModel === "unknown") {
  $.logLight("  CPU/OS unavailable (SFTP account likely jailed to game dir)");
}

// Stamp git metadata into the results (only available on the build machine)
const gitBranch = (await $`git -C ${ROOT} rev-parse --abbrev-ref HEAD`.text()).trim();
const gitCommit = (await $`git -C ${ROOT} rev-parse --short HEAD`.text()).trim();

const report = JSON.parse(await Deno.readTextFile(localJson));
report.gitBranch = gitBranch;
report.gitCommit = gitCommit;
report.host = { cpuModel, cpuCount, os: osName, memTotal, runtimeTfm };
await Deno.writeTextFile(localJson, JSON.stringify(report) + "\n");

// Prepend git + host info to the markdown too
let md = await Deno.readTextFile(localMd);
md = md.replace(
  "# Benchmark Results\n",
  `# Benchmark Results\n\n- **Branch:** ${gitBranch}\n- **Commit:** ${gitCommit}\n` +
    `- **CPU:** ${cpuModel} (${cpuCount} threads)\n- **OS:** ${osName}\n` +
    `- **Memory:** ${memTotal}\n- **Runtime:** ${runtimeTfm}\n`,
);
await Deno.writeTextFile(localMd, md);

$.logLight(`  JSON: ${localJson}`);
$.logLight(`  MD:   ${localMd}`);
console.log("\n" + md);
