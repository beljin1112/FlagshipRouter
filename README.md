# FlagshipRouter Desktop

**Free AI models for every coding tool — now a native Windows desktop app.**

FlagshipRouter is a local AI gateway that connects Claude Code, Codex, Cursor, Cline, OpenCode and other coding tools to free AI models through one OpenAI-compatible endpoint. The desktop app bundles the full dashboard (same UI as the old web version) inside a native window.

## Quick start

1. Open `windows/dist/`
2. Double-click **FlagshipRouter.exe**
3. The dashboard opens inside the app on port **20120** — no login required

### App behavior

| Action | Result |
|---|---|
| Launch EXE | Starts the server + shows the dashboard GUI |
| Close window (X) | Minimizes to system tray; server keeps running |
| Tray icon double-click | Reopen the dashboard window |
| Tray menu → Restart Server | Hard-restart of the bundled server |
| Tray menu → Quit | Stops the server and exits the app |
| First launch / Windows boot | Auto-start is registered automatically (toggle via tray menu) |

### Files

```
windows/
  FlagshipRouter.cs      # tray + WebView2 shell (C# / WinForms / .NET 8)
  FlagshipRouter.csproj  # publish config (single-file, self-contained, root icon.ico)
  package.mjs            # assembles windows/dist from EXE + server build
  bin/                   # dotnet publish output
  dist/                  # ready-to-run app: EXE + server/ + node/ + icon.ico
icon.ico, icon.png       # app icons (used by EXE + tray)
```

### Runtime locations

- App data / database: `%APPDATA%\FlagshipRouter`
- Server logs: `%APPDATA%\FlagshipRouter\logs\server.log`
- Port default: `20120` (override with `FlagshipRouter.exe --port <port>`)

### About the removed web version

The web/dev source tree (Next.js dashboard, CLI npm package, Docker, CI) was removed per request; this repo now ships only the desktop app. The ready-to-run EXE in `windows/dist` is self-contained and needs nothing else. If a future rebuild or dashboard change is ever needed, the full previous state — including all sources and the exact build recipe (`npm run build` → `.next/standalone`, then `npm run package`) — is preserved in git commit `49bf7b8`.

Default CLI-tool endpoint: `http://localhost:20120/v1` — point Claude Code, Codex, Cline, OpenCode and friends there (or use the built-in **CLI Tools** page to configure them with one click).
