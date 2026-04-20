#!/usr/bin/env node
//
// @ytsuda/ripple meta-package launcher.
//
// On install, npm inspects each optionalDependency's "os" and "cpu"
// fields and only installs the subpackage that matches the current
// platform. This script figures out which one that is and spawns
// its binary with stdio inherited.
//
// MCP clients talk to ripple over stdio pipes. The Node process here
// is a transparent relay: process.argv is forwarded verbatim, stdio
// is inherited so pipes pass through, and SIGTERM / SIGINT / SIGHUP
// are forwarded to the child so client-initiated shutdown propagates.

import { spawn } from "node:child_process";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);

// process.platform is one of 'win32' / 'linux' / 'darwin' / …
// process.arch is one of 'x64' / 'arm64' / …
// Keep this map in sync with the meta package.json's
// optionalDependencies list and with the workflow matrix.
const PLATFORM_PACKAGES = {
  "win32-x64":    { pkg: "@ytsuda/ripple-win32-x64",    exe: "ripple.exe" },
  "linux-x64":    { pkg: "@ytsuda/ripple-linux-x64",    exe: "ripple" },
  "darwin-arm64": { pkg: "@ytsuda/ripple-darwin-arm64", exe: "ripple" },
};

const key = `${process.platform}-${process.arch}`;
const entry = PLATFORM_PACKAGES[key];

if (!entry) {
  const supported = Object.keys(PLATFORM_PACKAGES).join(", ");
  console.error(
    `ripple: unsupported platform ${key}. Supported: ${supported}.\n` +
      `If you need another platform, please open an issue at https://github.com/yotsuda/ripple/issues`
  );
  process.exit(1);
}

let binPath;
try {
  binPath = require.resolve(`${entry.pkg}/bin/${entry.exe}`);
} catch (err) {
  console.error(
    `ripple: ${entry.pkg} is not installed (${err && err.code ? err.code : err}).\n` +
      `This usually means the optional dependency for your platform was skipped at install time.\n` +
      `Try reinstalling: npm i -g @ytsuda/ripple`
  );
  process.exit(1);
}

const child = spawn(binPath, process.argv.slice(2), {
  stdio: "inherit",
  windowsHide: false,
});

// Forward signals so clients (including MCP hosts) that SIGTERM /
// SIGINT the wrapper reach the actual ripple process. Without this,
// Ctrl+C from an MCP host would kill only the Node wrapper and leave
// a zombie ripple process holding the PTY open.
for (const sig of ["SIGTERM", "SIGINT", "SIGHUP", "SIGQUIT"]) {
  process.on(sig, () => {
    try { child.kill(sig); } catch { /* child may have already exited */ }
  });
}

child.on("exit", (code, signal) => {
  if (signal) {
    // Re-raise the signal on the wrapper so the exit status mirrors
    // a direct invocation (shells inspect $? / %ERRORLEVEL% and
    // $status accordingly).
    process.kill(process.pid, signal);
  } else {
    process.exit(code ?? 0);
  }
});

child.on("error", (err) => {
  console.error(`ripple: failed to launch ${binPath}: ${err.message}`);
  process.exit(1);
});
