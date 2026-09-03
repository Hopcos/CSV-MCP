# csv-mcp

[English](./README.md) | [中文](./README.zh.md)

An **MCP (Model Context Protocol) server** that lets AI agents create, read, append to, list,
download and delete CSV files over a plain **HTTP transport**. It solves a common agent
frustration: the agent has tabular data (for example query results) but no file-write or
download tool, so it cannot hand the user a real `.csv` file. With csv-mcp the agent calls
`create_csv` once and the user gets a direct download URL.

Built with **.NET 10**, the official **MCP SDK 2.2.0** and **Streamable HTTP** transport
(JSON-RPC over POST, SSE-framed responses — the same transport Claude Desktop and most MCP
clients speak by default).

---

## Table of contents

- [Why csv-mcp](#why-csv-mcp)
- [Features](#features)
- [Architecture](#architecture)
- [Project layout](#project-layout)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [MCP tools](#mcp-tools)
  - [Typical agent workflow](#typical-agent-workflow)
  - [create_csv](#create_csv)
  - [append_csv_rows](#append_csv_rows)
  - [list_csv_files](#list_csv_files)
  - [get_csv_content](#get_csv_content)
  - [get_csv_download](#get_csv_download)
  - [delete_csv_file](#delete_csv_file)
- [Raw download endpoint](#raw-download-endpoint)
- [Result envelope and error handling](#result-envelope-and-error-handling)
- [Design principles and patterns](#design-principles-and-patterns)
- [Extending the server](#extending-the-server)
- [Testing](#testing)
- [Security considerations](#security-considerations)

---

## Why csv-mcp

MCP servers expose *tools* — named, JSON-in/JSON-out capabilities an agent can call. A typical
gap: the agent can *query* data (SQL, log search, API calls) but cannot persist or deliver it.
csv-mcp fills that gap with a tiny, focused tool surface:

| Capability | Tool |
|---|---|
| Create a CSV from headers + rows | `create_csv` |
| Append rows to an existing CSV | `append_csv_rows` |
| List stored files | `list_csv_files` |
| Read back a slice of a file | `get_csv_content` |
| Get a download URL (with optional inline Base64) | `get_csv_download` |
| Delete a file | `delete_csv_file` |

Everything is validated server-side (never trust the agent), every write is atomic, and the
global **Instructions** guide served during `initialize` tells the agent exactly how to use
every tool, generated from the same catalog that registers the tools — one source of truth.

## Features

- **HTTP transport** (Streamable HTTP, stateless): one URL (`http://localhost:5080/mcp`) for
  the MCP client; no stdio process management.
- **Agent-friendly outputs**: every tool returns a JSON envelope with a `downloadUrl` the agent
  can embed in its reply; raw download works in any browser (`GET /download/{fileId}`).
- **Real CSV semantics**: RFC 4180 escaping, CRLF line endings, UTF-8 without BOM, configurable
  delimiter (comma / semicolon / tab / pipe).
- **Safe by design**: size/row/column limits enforced before anything touches disk; path
  traversal blocked; file names sanitized; conflicts auto-suffixed (`-1`, `-2`, …) so existing
  files are never overwritten.
- **Robust storage**: each file is committed via a temp file + atomic rename; a JSON sidecar
  (`{fileId}.meta.json`) records the delimiter, row/column counts, timestamps and file name, so
  appends keep escaping consistently even after a restart.
- **Concurrency aware**: per-file `SemaphoreSlim` locks serialize appends to the same file;
  parallel rendering speeds up large exports; a background service sweeps stale temp files.
- **Self-describing**: the server advertises at least one tool `description` for every tool and
  serves a complete usage guide as MCP `instructions`.

## Architecture

```
MCP client (agent)                     Browser / download manager
      │  JSON-RPC (POST /mcp)                │   GET /download/{fileId}
      ▼                                      ▼
┌─────────────────────── Streamable HTTP ───────────────────────┐
│  csv-mcp (ASP.NET Core, .NET 10)                               │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ MCP layer (ModelContextProtocol.AspNetCore)              │  │
│  │  • tools/list, tools/call → ToolCollection               │  │
│  │  • initialize → serverInfo + global instructions         │  │
│  └──────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Tools (thin orchestration, constructor DI)               │  │
│  │  create_csv  append_csv_rows  list_csv_files             │  │
│  │  get_csv_content  get_csv_download  delete_csv_file      │  │
│  └──────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Domain + Services                                        │  │
│  │  CsvFormatter         (RFC 4180 + parallel rendering)    │  │
│  │  CsvFileRepository    (atomic IO, sidecars, per-file     │  │
│  │                         locks, streaming reads)          │  │
│  │  CsvFileNamePolicy    (sanitize + unique names)          │  │
│  │  TempFileCleanupService (background maintenance)         │  │
│  └──────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Storage                                                   │  │
│  │  data/csv/{fileId}.csv        committed files             │  │
│  │  data/csv/{fileId}.meta.json  sidecar metadata            │  │
│  │  data/tmp/                    staging + temp files        │  │
│  └──────────────────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────┘
```

## Project layout

```
csv-mcp/
├── Directory.Build.props            # shared C# settings (nullable, warnings-as-errors, docs)
├── CsvMcp.slnx                      # solution file
├── .gitignore                       # bin/obj, data/, temp files, IDE files
├── README.md / README.zh.md         # this documentation
├── scripts/
│   └── smoke-test.ps1               # 11-step end-to-end test against a running server
└── src/
    └── CsvMcp.Server/
        ├── CsvMcp.Server.csproj
        ├── appsettings.json         # all tuning knobs (see Configuration)
        ├── Program.cs               # composition root: DI, MCP registration, endpoints
        ├── Configuration/CsvStorageOptions.cs
        ├── Domain/                  # CsvMetadata, CsvReadResult, exceptions, delimiters
        ├── Services/
        │   ├── Csv/                 # formatter, file-name policy
        │   ├── Storage/             # repository + sidecar model + path resolution
        │   └── Maintenance/         # temp-file cleanup background service
        ├── Instructions/            # ToolCatalog + GlobalUsageGuide (served as MCP instructions)
        ├── Tools/                   # one file per MCP tool
        └── Web/                     # download endpoint + URL builder
```

## Quick start

Prerequisites: .NET 10 SDK.

```bash
dotnet build src/CsvMcp.Server -c Release
dotnet run --project src/CsvMcp.Server -c Release --no-build
```

The server listens on `http://localhost:5080` (override with the `Urls` setting).

Verify:

```bash
# Root endpoint
Invoke-RestMethod http://localhost:5080/          # or curl http://localhost:5080/

# End-to-end check
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

### Connecting an MCP client

Point your MCP client at the Streamable HTTP endpoint:

- URL: `http://localhost:5080/mcp`
- Type: `streamable-http` (HTTP + SSE)
- No authentication (add your own middleware/reverse proxy for production)

Example client config (Claude-style):

```json
{
  "mcpServers": {
    "csv-mcp": {
      "type": "http",
      "url": "http://localhost:5080/mcp"
    }
  }
}
```

## Configuration

All knobs live in `appsettings.json` under the `CsvMcp` section:

| Key | Default | Meaning |
|---|---|---|
| `Urls` (top level) | `http://localhost:5080` | HTTP listen address |
| `RootDirectory` | `data` | Base directory (resolved against the content root) |
| `CsvSubdirectory` | `csv` | Where committed files + sidecars live |
| `TempSubdirectory` | `tmp` | Where staging/temp files live |
| `PublicBaseUrl` | `""` | Optional fixed base URL for download links (when empty, the request's scheme/host is used) |
| `MaxRowsPerFile` | `1000000` | Hard cap on data rows per file |
| `MaxColumns` | `10000` | Hard cap on columns per file |
| `MaxCellLength` | `1000000` | Hard cap on characters in a single cell |
| `MaxFileSizeBytes` | `52428800` (50 MB) | Hard cap on a committed file's size |
| `DefaultContentRowLimit` | `200` | `get_csv_content` default page size |
| `MaxContentRowLimit` | `100000` | `get_csv_content` hard page-size cap |
| `MaxBase64DownloadBytes` | `5242880` (5 MB) | Cap for `includeBase64` downloads |
| `TempFileMaxAge` | `01:00:00` | Temp files older than this are deleted |
| `CleanupInterval` | `00:10:00` | How often the cleanup background task runs (plus a sweep at startup) |

## MCP tools

The full, always-up-to-date usage guide is served to clients as MCP `instructions` during
`initialize` — an agent can read it and use every tool correctly without external docs.

Every tool returns the same JSON envelope, and tool results are **stringified JSON** in the
MCP `text` content block:

### Typical agent workflow

1. The agent collects tabular data (its own query/tool results).
2. It calls `create_csv` with headers + rows, e.g.:

   ```json
   {
     "jsonrpc": "2.0",
     "id": 1,
     "method": "tools/call",
     "params": {
       "name": "create_csv",
       "arguments": {
         "headers": ["domain_id", "year_month", "user_id", "spins_in_month", "spins_remaining"],
         "rows": [
           ["1978", "2025-11", "3380506", "0", "8"],
           ["1978", "2025-11", "3389568", "0", "2"],
           ["1978", "2025-11", "4192031", "0", "25"],
           ["1978", "2025-11", "5151275", "0", "62"]
         ]
       }
     }
   }
   ```

3. It receives `fileId`, metadata and a `downloadUrl` and simply tells the user: *"Here's your
   CSV: http://localhost:5080/download/…"* — no file tool needed.
4. For a dataset that arrives in chunks, the agent uses `append_csv_rows`; it can verify with
   `get_csv_content`, list with `list_csv_files`, and clean up with `delete_csv_file`.

### create_csv

Creates a new CSV file from headers + rows. Returns metadata and a download URL.
If `fileName` is already taken, a `-1`, `-2`, … suffix is added automatically and the real name
is reported, so existing files are never overwritten.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `headers` | `string[]` | yes | Column headers, e.g. `["domain_id", "year_month"]` |
| `rows` | `string[][]` | yes | One array of cell strings per row, e.g. `[["1978", "2025-11"]]` |
| `fileName` | `string?` | no | Suggested name; stripped of invalid characters, `.csv` guaranteed; default `export-yyyyMMdd-HHmmss.csv` |
| `delimiter` | `string` | no | `comma` (default), `semicolon`, `tab` or `pipe` |

Example request:

```json
{
  "jsonrpc": "2.0", "id": 1, "method": "tools/call",
  "params": { "name": "create_csv", "arguments": { "fileName": "member-spins.csv", "headers": ["a", "b"], "rows": [["1", "2"]] } }
}
```

Example response (content text, pretty-printed here):

```json
{
  "ok": true,
  "tool": "create_csv",
  "data": {
    "fileId": "e958b77d7e3f49d4af9440ac7662907a",
    "fileName": "member-spins.csv",
    "rowCount": 1,
    "columnCount": 2,
    "sizeBytes": 9,
    "createdAtUtc": "2026-09-03T02:51:11.7762499+00:00",
    "updatedAtUtc": "2026-09-03T02:51:11.7762499+00:00",
    "downloadUrl": "http://localhost:5080/download/e958b77d7e3f49d4af9440ac7662907a"
  }
}
```

### append_csv_rows

Appends rows to an existing file. Each row must have exactly as many cells as the file's
columns. Appends are serialized per file (safe under concurrent calls).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `fileId` | `string` | yes | Existing file's ID |
| `rows` | `string[][]` | yes | Rows to append, same shape as `create_csv` |

### list_csv_files

Lists committed files, newest first.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `namePattern` | `string?` | no | Case-insensitive substring filter on the file name |
| `offset` | `int?` | no | 0-based pagination offset, default 0 |
| `limit` | `int?` | no | Max items (capped at 1000); default: all |

### get_csv_content

Reads a window of a file's content as plain CSV text, so the agent or user can verify data.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `fileId` | `string` | yes | Existing file's ID |
| `rowOffset` | `int` | no | 0-based index of the first data row, default 0 |
| `maxRows` | `int?` | no | Page size (clamped to `[1, MaxContentRowLimit]`), default 200 |
| `includeHeader` | `bool` | no | Prefix the header row, default false |

Response includes `rowsReturned`, `hasMore`, `totalDataRows` and the `content` text.

### get_csv_download

Returns the download URL (and, optionally, an inline Base64 payload + data URI for small files).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `fileId` | `string` | yes | Existing file's ID |
| `includeBase64` | `bool` | no | Embed the file as Base64 + `data:text/csv;base64,…` URI; refused above `MaxBase64DownloadBytes` |

### delete_csv_file

Deletes a file and its sidecar. Idempotent: deleting an unknown ID returns `ok: false`
("not found") instead of failing loudly.

## Raw download endpoint

Outside the MCP protocol, for browsers and download managers:

```
GET /download/{fileId}            → attachment (Content-Disposition: attachment; filename=…)
GET /download/{fileId}?inline=true → inline preview
```

- `404` JSON body when the file does not exist
- `text/csv`, RFC 5987-encoded file names, range requests enabled (resumable downloads)

## Result envelope and error handling

Every tool text result follows one of:

```json
{ "ok": true,  "tool": "<toolName>", "data": { … } }
{ "ok": false, "tool": "<toolName>", "error": "Human-readable, safe-to-surface message." }
```

Conventions:

- Validation problems (bad shapes, unknown files, limits exceeded) are returned as
  `ok: false` tool results — the agent can read `error` and self-correct.
- Messages never leak paths or stack traces.
- The envelope is at the top level of the MCP `text` content, so clients can render or parse it
  directly.

## Design principles and patterns

| Principle / pattern | Where it lives |
|---|---|
| **Single source of truth** | One `ToolCatalog` drives both tool registration and the generated global usage guide; tool descriptions are `[Description]` attributes on the same methods |
| **Dependency inversion** | Tools depend on interfaces (`ICsvFileRepository`, `ICsvFormatter`, `ICsvStoragePaths`); implementations are swappable and mockable |
| **Composition root** | All wiring (options, services, tools, hosted services) is done in `Program.cs` via `IServiceCollection`; tool classes are plain DI-constructed POCOs |
| **Async everywhere** | All I/O is `async`/`await` with `CancellationToken` threading; no blocking calls on request paths |
| **Concurrency control** | Per-file `SemaphoreSlim` gates serialize appends to the same file while reads proceed; `ConcurrentDictionary` for the gate registry |
| **Parallelism for throughput** | `CsvFormatter` renders rows with `Parallel.For` chunks, then writes sequentially (ordered, deterministic output) |
| **Atomic commits** | Write to a temp file in `data/tmp`, `File.Move` into place, update the sidecar atomically — a crash never leaves a half-written file as the committed version |
| **Sidecar metadata** | The delimiter (and counts) are persisted so appends escape consistently even across restarts; listing uses the sidecar fast path and rebuilds metadata for orphans |
| **Background maintenance** | `TempFileCleanupService` (hosted `BackgroundService`) sweeps stale temp files at startup and on an interval |
| **Fail-safe validation** | All limits enforced before any disk touch; explicit `CsvValidationException` with safe messages; `warnings-as-errors` in builds |
| **Path security** | Centralized path resolution with a `fileId` format check and traversal guard |

## Extending the server

Adding a new capability (for example `update_csv` or `replace_csv`) takes four steps:

1. **Service** — add the operation to the storage contract (`ICsvFileRepository`) and implement
   it (respect the per-file lock + atomic-write pattern).
2. **Tool** — a new class in `src/CsvMcp.Server/Tools/`, e.g. `UpdateCsvTool`, with an
   `[McpServerTool(Name = "update_csv", Title = "…")]` method, `[Description]` on the method and
   every parameter, throwing/returning `CsvValidationException` messages.
3. **Wire it** — register the new service in `Program.cs` DI and add the tool type to the
   `toolTypes` collection (keep the static type `IEnumerable<Type>` — see the comment in
   `Program.cs` about `WithTools` overload resolution).
4. **Document** — add a `ToolExamples` entry in `GlobalUsageGuide` so the generated
   instructions show a worked example, and mention the tool in `README.md`.

Rebuild, restart, and the tool appears in `tools/list` and in the instructions automatically.

## Testing

`scripts/smoke-test.ps1` drives a running server end-to-end over the real MCP protocol:

1. `initialize` (checks server name + instructions coverage)
2. `tools/list` (checks all 6 tools + descriptions)
3. `create_csv` with the sample dataset
4. unique-name resolution on collisions
5. raw download (exact byte match, headers)
6. paged `get_csv_content`
7. `list_csv_files`
8. `append_csv_rows`
9. `get_csv_download` with Base64 (decodes and verifies content)
10. validation + 404 behavior
11. `delete_csv_file` (and 404 afterwards)

Run it (PowerShell 5.1 or 7):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

Expect `ALL CHECKS PASSED` and exit code 0.

## Security considerations

- The server binds to `localhost` by default; for real deployments put it behind a reverse
  proxy and add authentication (the MCP transport itself carries none).
- File names are sanitized and validated; `fileId` is restricted to `[0-9a-fA-F-]{1,64}` and
  extra traversal attempts are rejected.
- Upload sizes are capped (rows, columns, cell length, total file size, Base64 payload).
- Delete requires knowing the exact `fileId`; IDs are 128-bit random values, so they are not
  enumerable in practice.
- The global instructions never reveal filesystem paths or server internals.
