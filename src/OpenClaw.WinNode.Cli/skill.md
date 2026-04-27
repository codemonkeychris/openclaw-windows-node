# winnode skill reference

`winnode.exe` invokes OpenClaw Windows-node commands on the local tray over a
loopback MCP HTTP endpoint (default `http://127.0.0.1:8765/`). Enable
**Local MCP Server** in the tray's Settings → Advanced before calling.

This document is the agent-facing reference: every supported command, its
argument shape, and the A2UI v0.8 JSONL grammar. It is shipped alongside
`winnode.exe` so an agent can read it once and emit token-efficient calls.

---

## Invocation shape

```
winnode --command <name> [--params '<json-object>'] [--invoke-timeout <ms>]
```

- `--command` (required) — node command (e.g. `system.which`, `canvas.a2ui.push`).
- `--params` — single JSON **object** string, default `{}`. Must be a JSON object,
  not an array or scalar.
- `--invoke-timeout` — milliseconds, default 15000. HTTP timeout adds a 5s buffer.
- `--node`, `--idempotency-key` — accepted for parity with `openclaw nodes
  invoke`; **ignored** locally. Safe to copy/paste from gateway-side commands.
- `--mcp-url <url>` / `--mcp-port <port>` — override the endpoint. Falls back to
  `OPENCLAW_MCP_PORT` env var, then port 8765.
- `--verbose` — log endpoint + ignored flags to stderr.

**Output contract:** stdout receives the capability payload as pretty-printed
JSON (matches `openclaw nodes invoke`). stderr receives errors. Exit code:

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Tool error, JSON-RPC error, transport failure, or HTTP non-2xx |
| 2    | Argument error (missing/invalid flags, bad `--params` JSON) |

---

## Commands

### system.notify
Show a Windows toast notification.
```
{"title": "OpenClaw", "body": "string", "subtitle": "string", "sound": true}
```
Returns `{ "sent": true }`. All fields optional except `body` in practice.

### system.run
Execute a shell command. Subject to the local exec approval policy at
`%LOCALAPPDATA%\OpenClawTray\exec-policy.json`.
```
{
  "command": "string OR string[]",   // required
  "args":    ["string", ...],         // optional, appended to command
  "shell":   "powershell|pwsh|cmd|bash",
  "cwd":     "string",
  "timeoutMs": 30000,
  "env":     { "KEY": "VALUE" }
}
```
Returns `{ stdout, stderr, exitCode, timedOut, durationMs }`.

### system.run.prepare
Pre-flight a `system.run` invocation. Same args as `system.run`. Returns the
parsed plan (`argv`, `cwd`, `rawCommand`, `agentId`, `sessionKey`) without
executing.

### system.which
Resolve binary names to absolute paths.
```
{"bins": ["git", "node", "powershell"]}
```
Returns `{ "bins": { "git": "C:\\...", ... } }`. Names not found are omitted.

### system.execApprovals.get
No params. Returns the active exec policy:
`{ enabled, defaultAction, rules: [{pattern, action, shells?, description?, enabled}] }`.

### system.execApprovals.set
Replace the exec policy.
```
{
  "rules": [{"pattern": "echo *", "action": "allow"}, ...],
  "defaultAction": "allow|deny|prompt"
}
```

### canvas.present
Open the WebView2 canvas window.
```
{
  "url":   "string",          // OR "html": "string"
  "html":  "string",
  "width": 800, "height": 600,
  "x": -1, "y": -1,           // -1 centers
  "title": "Canvas",
  "alwaysOnTop": false
}
```
Returns `{ "presented": true }`.

### canvas.hide
No params. Hides the canvas without destroying state.

### canvas.navigate
```
{"url": "https://..."}    // also accepts file:// or local canvas paths
```

### canvas.eval
```
{"script": "document.title"}    // also accepts "javaScript" or "javascript"
```
Returns the evaluated result.

### canvas.snapshot
```
{"format": "png|jpeg", "maxWidth": 1200, "quality": 80}
```
Returns `{ format, base64 }`.

### canvas.a2ui.push
Render an A2UI v0.8 surface in the canvas. The canvas window opens
automatically — no `canvas.present` required.
```
{
  "jsonl":     "string",   // OR jsonlPath
  "jsonlPath": "string",   // must live under %TEMP%
  "props":     {}           // optional
}
```
Returns `{ "pushed": true }`. **See A2UI grammar below.**

### canvas.a2ui.reset
No params. Clears any rendered surfaces. Returns `{ "reset": true }`.

### canvas.a2ui.dump
No params. Returns the current surface graph for introspection.

### canvas.caps
No params. Returns renderer capabilities (renderer, snapshot, a2ui version).

### screen.capture
```
{
  "format": "png|jpeg", "maxWidth": 1920, "quality": 80,
  "monitor": 0, "screenIndex": 0,    // 0 = primary
  "includePointer": true
}
```
Returns `{ format, width, height, base64, image }` (image is a `data:` URL).

### screen.list
No params. Returns `{ screens: [{ index, name, primary, bounds, workingArea }] }`.

### camera.list
No params. Returns `{ cameras: [{ deviceId, name, isDefault }] }`.

### camera.snap
```
{"deviceId": "string", "format": "jpeg|png", "maxWidth": 1280, "quality": 80}
```
Returns `{ format, width, height, base64 }`. `deviceId` defaults to system
default camera.

---

## A2UI v0.8 grammar (for canvas.a2ui.push)

The `jsonl` argument is a string of newline-separated JSON-RPC-like messages.
Three message kinds are supported. **createSurface and v0.9 messages are
rejected.**

### Message kinds

```jsonc
// 1. Declare components for a surface (creates the surface if new).
{"surfaceUpdate": {
  "surfaceId": "string",
  "components": [ ComponentDef, ... ]
}}

// 2. Pick the root component and (optionally) styles. Send AFTER surfaceUpdate.
{"beginRendering": {
  "surfaceId": "string",
  "root": "componentId",
  "styles": { "primaryColor": "#FF6F61", "radius": 8.0, "spacing": 12.0 }
}}

// 3. Seed/update the data model bound by Path() values.
{"dataModelUpdate": {
  "surfaceId": "string",
  "contents": [
    {"key": "headline", "valueString":  "Hi"},
    {"key": "agreed",   "valueBoolean": false},
    {"key": "volume",   "valueNumber":  20.0}
  ]
}}

// 4. (Optional) Remove a surface.
{"deleteSurface": {"surfaceId": "string"}}
```

### ComponentDef

```jsonc
{"id": "uniqueId", "component": {"<ComponentName>": { ...props }}}
```

### Value bindings (inside a component prop)

| Form                          | Meaning                                |
|-------------------------------|----------------------------------------|
| `{"literalString": "x"}`      | Literal string                         |
| `{"path": "/key"}`            | Read/write the data model              |
| Plain string `"x"`            | Component-id reference (e.g. `child`)  |
| Plain number / bool           | Used directly for numeric/bool props   |

### Component catalog

| Category     | Name          | Notable props |
|--------------|---------------|---------------|
| Container    | `Row`         | `children: {explicitList: ["id", ...]}` |
| Container    | `Column`      | `children: {explicitList: ["id", ...]}` |
| Container    | `List`        | `children`, `dataBinding` |
| Container    | `Card`        | `child: "id"` |
| Container    | `Tabs`        | `tabItems: [{title, child}]` |
| Container    | `Modal`       | `child` |
| Container    | `Divider`     | `axis: "horizontal" | "vertical"` |
| Display      | `Text`        | `text: Lit/Path`, `usageHint: "h1|h2|h3|h4|h5|body|caption"` |
| Display      | `Image`       | `url: Lit/Path`, `fit: "contain|cover|fill|none"`, `usageHint` |
| Display      | `Icon`        | `name: Lit("settings"\|...)` |
| Display      | `Video`       | `url`, `autoplay`, `controls` |
| Display      | `AudioPlayer` | `url`, `controls` |
| Interactive  | `Button`      | `child`, `primary: bool`, `action: {name, ...context}` |
| Interactive  | `CheckBox`    | `label: Lit/Path`, `value: Path` |
| Interactive  | `TextField`   | `value: Path`, `textFieldType: "shortText|longText|obscured"` |
| Interactive  | `DateTimeInput` | `value: Path`, `mode: "date|time|datetime"` |
| Interactive  | `MultipleChoice` | `value: Path`, `options: [{value, label}]` |
| Interactive  | `Slider`      | `value: Path`, `minValue`, `maxValue`, `step` |

Lit/Path = the value-binding shapes from the previous section.

### Minimal "hello world" payload

```
{"surfaceUpdate":{"surfaceId":"hello","components":[{"id":"helloText","component":{"Text":{"text":{"literalString":"Hello, world!"},"usageHint":"h1"}}}]}}
{"beginRendering":{"surfaceId":"hello","root":"helloText"}}
```

Pass this as the `jsonl` value (a single JSON string with `\n` between messages).

---

## Token-efficient call patterns

1. **Skip `--node` / `--idempotency-key`** — they're ignored locally; including
   them just costs tokens. Only include if you're literally pasting a gateway
   command.
2. **Omit `--params` when the command takes no args** (`screen.list`,
   `camera.list`, `canvas.hide`, `canvas.a2ui.reset`, `canvas.a2ui.dump`,
   `canvas.caps`, `system.execApprovals.get`).
3. **Large A2UI payloads** — write the JSONL to a file under the system temp
   directory and pass `{"jsonlPath": "<path>"}`. The capability rejects paths
   outside `%TEMP%`.
4. **Big binary results (snapshots, captures)** — output is base64 in stdout.
   Pipe to a file (`> capture.json`) instead of letting the agent read it
   inline.
5. **Errors are exit-code-driven** — check `$LASTEXITCODE` (or `$?` in bash)
   first, then read stderr only on non-zero. Exit 2 = your call is malformed.

## What's NOT exposed
- Pairing / device approval (gateway concept; doesn't apply locally).
- `chat.send`, `sessions.list`, `usage.list`, `node.list` — these belong to the
  operator-side `OpenClaw.Cli.exe`, not `winnode.exe`.
- Wildcards in `--command`. The MCP server has an explicit allowlist; unknown
  commands return `Unknown tool: <name>`.
