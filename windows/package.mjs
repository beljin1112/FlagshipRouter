#!/usr/bin/env node
// Assemble windows/dist: single-file EXE + production server + bundled node + icon.
// Prerequisite: Next.js production build output at .next/standalone (from the git
// snapshot commit of the removed web sources — see README).
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const dist = path.join(root, "windows", "dist");
const standalone = path.join(root, ".next", "standalone");

if (!fs.existsSync(path.join(root, "windows", "bin", "FlagshipRouter.exe"))) {
  console.error("Missing windows/bin/FlagshipRouter.exe — run: npm run build:exe");
  process.exit(1);
}
if (!fs.existsSync(standalone)) {
  console.error("Missing .next/standalone — restore the web sources snapshot (git checkout 49bf7b8^ -- .) and run the Next.js build first.");
  process.exit(1);
}

if (fs.existsSync(dist)) fs.rmSync(dist, { recursive: true, force: true });
fs.mkdirSync(dist, { recursive: true });

function copyDir(src, dest) {
  fs.mkdirSync(dest, { recursive: true });
  for (const e of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, e.name);
    const d = path.join(dest, e.name);
    if (e.isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}

fs.copyFileSync(path.join(root, "windows", "bin", "FlagshipRouter.exe"), path.join(dist, "FlagshipRouter.exe"));
copyDir(standalone, path.join(dist, "server"));

const nodeDir = path.join(dist, "node");
fs.mkdirSync(nodeDir, { recursive: true });
fs.copyFileSync(process.execPath, path.join(nodeDir, "node.exe"));
console.log("   node.exe copied from current runtime");

fs.copyFileSync(path.join(root, "icon.ico"), path.join(dist, "icon.ico"));
fs.copyFileSync(path.join(root, "icon.png"), path.join(dist, "icon.png"));

fs.writeFileSync(path.join(dist, "README.txt"), [
  "FlagshipRouter Desktop (Windows)",
  "===============================",
  "1. Double-click FlagshipRouter.exe",
  "2. The dashboard opens inside the app window (port 20120)",
  "3. Closing the window minimizes to the system tray - the server keeps running",
  "4. Right-click the tray icon to Quit (stops the server)",
  "5. Starts with Windows automatically (toggle in the tray menu)",
  "",
  "Data: %APPDATA%\\FlagshipRouter",
  "Logs: %APPDATA%\\FlagshipRouter\\logs\\server.log",
  "",
].join("\n"));

let total = 0;
(function size(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) size(p);
    else total += fs.statSync(p).size;
  }
})(dist);
console.log("DONE. dist size: " + (total / 1048576).toFixed(1) + " MB");
