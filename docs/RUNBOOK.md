# Operational Runbook and Troubleshooting Guide

**Status:** Current for Windows 11 with PowerShell or Git Bash, .NET 10. Everything runs on one machine; there is no deployment beyond local development. Setup basics are in [README.md](README.md); this document covers operating and debugging.

## 1. Processes at a glance

| Process | Started by | Port or transport | Stopped by |
|---|---|---|---|
| Ollama | `scripts/ollama-start.ps1` (or `ollama serve`) | `127.0.0.1:11434` | `scripts/ollama-stop.ps1` |
| `Assistant.Api` | `dotnet run` | `localhost:5080` | Ctrl+C |
| `Assistant.SqliteMcp` | API, as a child (`dotnet Assistant.SqliteMcp.dll`) | stdio | Ends with the API |
| Filesystem MCP server | API, as a child via `npx` (Node) | stdio | Ends with the API |
| Vite dev server | `npm run dev` in `web/` | `localhost:5173` | Ctrl+C |

If the API is killed instead of stopped cleanly, its children can be left running (section 5).

## 2. Starting and stopping

### Ollama

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ollama-start.ps1
```

The script does nothing if Ollama already answers. Otherwise it launches `ollama serve` hidden, polls `/api/tags` for up to 30 seconds, and warns if `qwen2.5:7b-instruct` is not pulled. Parameters: `-BaseUrl`, `-Model`, `-TimeoutSeconds`. It uses `127.0.0.1`, not `localhost`: Ollama listens on IPv4 only and Windows loses about 2 seconds failing over from `::1`.

Pull the model once: `ollama pull qwen2.5:7b-instruct`.

Stop it when done (it holds GBs of RAM or VRAM while a model is loaded):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ollama-stop.ps1
```

This stops `ollama` and the tray app `ollama app`; the tray app would otherwise restart the server.

### API

```powershell
cd src/Assistant.Api
dotnet run --no-launch-profile --urls http://localhost:5080
```

**Run from `src/Assistant.Api`.** The relative paths in `appsettings.json` (`../../sample-docs`, `../../data/assistant.db`) assume that working directory. From the repo root, `dotnet run --project src/Assistant.Api` resolves them one level too high, which silently indexes and serves the wrong folder or creates the database in the wrong place.

On start the API launches both MCP servers (first run of the filesystem server downloads it through `npx`, so allow time and network) and starts the indexer. Look for these log lines:

```
MCP server filesystem ready with tools: list_allowed_directories, list_directory, ...
MCP server sqlite ready with tools: describe_schema, query
Indexed <root>: N added, N updated, N unchanged, N removed, N skipped
```

### Frontend

```powershell
cd web
npm ci
npm run dev
```

### Smoke check

```powershell
curl.exe http://127.0.0.1:5080/api/health
curl.exe -N -X POST http://127.0.0.1:5080/api/chat -H "Content-Type: application/json" -d "{\"prompt\":\"What is the largest file?\"}"
```

Expect `{"status":"ok"}`, then a stream of `event:` and `data:` frames ending in `DoneEvent`. Event format: [API-CONTRACT.md](API-CONTRACT.md).

## 3. Configuration reference

All in `src/Assistant.Api/appsettings.json`; any key can be overridden by environment variable (`Agent__MaxIterations=4`) or command-line argument.

| Section | Keys | Notes |
|---|---|---|
| `Ollama` | `BaseUrl`, `Model`, `SystemPrompt` | Validated at start; a bad URL fails fast |
| `Agent` | `MaxIterations` (8), `RunTimeoutSeconds` (120), `MaxToolResultChars` (8000), `MaxIdenticalCalls` (2) | Range-checked at start |
| `Indexing` | `Enabled`, `Root`, `DatabasePath`, `IntervalMinutes` | Details: [DATA-SPEC.md](DATA-SPEC.md) section 5 |
| `Mcp:Servers` | `Name`, `Command`, `Args`, `ExcludeTools` | Details: [MCP-SPEC.md](MCP-SPEC.md) section 3 |

## 4. Live verification with Ollama

Unit tests use fakes and do not prove the model behaves. After a change to the agent, prompt or tool selection, run two or three representative prompts ([PROMPT-SPEC.md](PROMPT-SPEC.md) section 6) and record which tools were called.

1. Start Ollama (section 2).
2. Start the API from `src/Assistant.Api` with `--no-launch-profile`.
3. Send prompts with `curl` to `http://127.0.0.1:5080/api/chat` (use `127.0.0.1`, not `localhost`).
4. Read the `ToolCallEvent` frames for the tools used and the `TokenEvent` text for the answer. Field names are PascalCase (`Text`, `Tool`, `ArgumentsJson`, `Result`, `IsError`); tool names appear as `plugin-function`.
5. **Clean up:** stop the API, run `scripts/ollama-stop.ps1`, and kill leftovers (section 5).

Run Ollama, the API and curl **outside any sandbox**: inside a sandbox the server starts but nothing can reach it.

The opt-in filesystem-server integration test needs Node and network: set `ASSISTANT_MCP_INTEGRATION=1` and run `dotnet test`.

## 5. Process cleanup

After a crash or a forced kill, look for orphans and stop them. Orphaned `Assistant.SqliteMcp` processes also hold a read handle on `data/assistant.db`, which can block deleting or rebuilding it.

```powershell
# What is left over
Get-Process -Name Assistant.Api, Assistant.SqliteMcp -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
  Where-Object { $_.CommandLine -match 'server-filesystem' } |
  Select-Object ProcessId, CommandLine

# Stop them
Get-Process -Name Assistant.Api, Assistant.SqliteMcp -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
  Where-Object { $_.CommandLine -match 'server-filesystem' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

The match on `server-filesystem` avoids killing unrelated Node processes. If the API was started with `dotnet run`, the host process shows as `dotnet` rather than `Assistant.Api`; check `Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'"` and match on `Assistant.Api` or `Assistant.SqliteMcp` in the command line.

Port 5080 stuck in use: `Get-NetTCPConnection -LocalPort 5080 | Select-Object OwningProcess`, then stop that process.

## 6. Reading logs

Logs go to the console of the API process at Information level (`Logging` section). To see more, set `Logging:LogLevel:Default` to `Debug`; the runner then also logs the kernel function list. `Assistant.SqliteMcp` logs to stderr because its protocol traffic uses stdout, which must stay clean. Whether that stderr shows up in the API console depends on the MCP SDK's transport and has not been verified; to see it, run the server by hand (7.3).

Messages worth knowing:

| Log message | Meaning |
|---|---|
| `MCP server {Server} ready with tools: ...` | Server started; the list is what the model can use after `ExcludeTools` |
| `MCP server {Server} failed to start; its tools are unavailable` | Server failed or exceeded the 30 second startup timeout. The app runs without those tools; see 7.3 |
| `Iteration {n}: tool {Tool} ok` or `failed` | One tool call per line, with outcome |
| `Rejected tool call: ...` | `ToolCallValidator` caught a bad name, JSON or arguments; the text is what the model was told |
| `Tool {Tool} threw` | An exception from a tool call, returned to the model as an error result |
| `Agent run exceeded {n}s timeout` | The run hit `RunTimeoutSeconds` |
| `Agent hit the {n}-iteration cap` | No final answer within `MaxIterations` |
| `Indexed {Root}: ...` | A scan finished with its counts |
| `Skipping {Path}` | A locked or vanished file was skipped by the indexer |
| `Indexing {Root} failed; will retry` | A scan threw; retried next interval |
| `Could not prepare the metadata database ...; indexing is off` | Migration failed; indexing stays off until restart |

## 7. Troubleshooting

### 7.1 Chat returns immediately with a network error, or the stream ends with no `DoneEvent`

Most likely the model call failed: Ollama is not running, the URL is wrong, or the model is not pulled. The run throws after the response has started, so the client sees a truncated stream rather than a clear error (see [API-CONTRACT.md](API-CONTRACT.md) section 5).

1. `curl.exe http://127.0.0.1:11434/api/tags` should list `qwen2.5:7b-instruct`.
2. If it does not answer, run `scripts/ollama-start.ps1`; if the model is missing, `ollama pull qwen2.5:7b-instruct`.
3. Check the API console for the exception.

### 7.2 First token is slow, or every request is slow

- First request after Ollama starts loads the model into memory; expect many seconds. Later requests are faster until Ollama unloads it after its idle period.
- A model that does not fit in VRAM spills to CPU and is much slower. Check `ollama ps` for the processor split.
- Using `localhost` instead of `127.0.0.1` adds about 2 seconds per request on Windows.
- A run needs one model call per iteration. A prompt that takes four tool rounds is five model calls; a run that reaches 120 seconds stops with a timeout `ErrorEvent`.
- Large tool results make each following call slower. Lower `Agent:MaxToolResultChars` if answers over big files are slow.

### 7.3 A tool is missing, or "Unknown tool" errors

- Compare the `MCP server ... ready with tools` log lines against what you expect. A failed server logs the error with its exception.
- **filesystem fails to start:** Node and `npx` must be on `PATH`; the first launch needs network to fetch the package. Run `npx -y @modelcontextprotocol/server-filesystem ../../sample-docs` by hand from `src/Assistant.Api` to see its error.
- **sqlite fails to start:** `Assistant.SqliteMcp.dll` must sit beside the API. Rebuild with `dotnet build`. Running it by hand (`dotnet Assistant.SqliteMcp.dll <db>`) with no argument prints usage and exits 2.
- A server that **died after startup** is not restarted; tool calls return errors until the API restarts (watchdog is planned).
- "Unknown tool" in a tool result means the model invented or mis-spelled a name. The result lists the valid names; if it happens repeatedly the model is struggling and the prompt or tool set may need work.

### 7.4 "The metadata database does not exist yet"

The SQLite server opens the database read-only and never creates it. The indexer creates it on startup. If the message persists: confirm `Indexing:Enabled` is true, check for `Could not prepare the metadata database` in the log, and confirm you started the API from `src/Assistant.Api` so `../../data/assistant.db` lands in the repo's `data/` folder.

### 7.5 Questions about files return nothing or stale results

- The index can trail the disk by up to `IntervalMinutes` (default 5). Look for the latest `Indexed ...` line.
- `Indexing:Root` must equal the filesystem server's allow-listed root; they are separate settings.
- To rebuild from scratch: stop the API and any `Assistant.SqliteMcp` processes (section 5), delete `data/assistant.db`, `data/assistant.db-wal` and `data/assistant.db-shm`, and start again.

### 7.6 SQL errors from `query`

The error text goes back to the model, which usually corrects itself. Common ones: comparing `modified_utc` to `datetime('now')` without wrapping the column in `datetime()` (wrong results, not an error), a trailing `;` followed by more text, comments, or a query that is not a `SELECT`. The messages are listed in [MCP-SPEC.md](MCP-SPEC.md) section 6. A 5 second timeout means the query needs to be more selective.

### 7.7 Model forgets instructions, or ignores earlier tool results (token overflow)

Ollama truncates input silently when it exceeds the context window; there is no error. Symptoms: the answer ignores the system prompt's routing rules, repeats a tool call, or contradicts a result it just received. The app does not set `num_ctx`, so Ollama's default applies. Mitigations, in order: lower `Agent:MaxToolResultChars`, ask narrower questions, lower `MaxIterations`, and consider setting a larger `num_ctx` on the Ollama side. Budget analysis: [PROMPT-SPEC.md](PROMPT-SPEC.md) section 5.

### 7.8 Run stops with "Stopped after 8 iterations" or "timed out"

The model never produced a final answer. Read the tool activity: repeated failing calls point to a prompt or schema problem; a loop of different valid calls points to a vague question. Both are bounded on purpose. Raising `MaxIterations` or `RunTimeoutSeconds` rarely helps a stuck model.

### 7.9 Tests fail on file locks or hang

- SQLite temp databases need `SqliteConnection.ClearAllPools()` before deleting; a leftover pool holds the file.
- Stray `Assistant.SqliteMcp` or `node` processes from an earlier run can hold files; clean up (section 5), then rerun.
- Re-run the suite more than once after changing anything involving subprocesses or time.

## 8. Quick reference

```powershell
dotnet build; dotnet test                       # backend
dotnet test --filter "FullyQualifiedName~ReActLoopTests"
cd web; npm test; npm run lint; npm run build   # frontend
```
