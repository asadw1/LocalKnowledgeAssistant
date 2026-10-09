# Local Knowledge & File Assistant

Single-agent ReAct loop with tool calling over local MCP servers, running against a local Ollama model.

```
[React Dashboard] <-- SSE --> [ASP.NET Core Minimal API]
                                        |
                              [Semantic Kernel Engine]
                                        |
                       +----------------+----------------+
                [Ollama / qwen2.5:7b-instruct]   [Local MCP servers: filesystem, SQLite]
```

## Layout

| Path | Purpose |
|---|---|
| `src/Assistant.Core` | Agent loop, LLM abstraction, tool registry, MCP wiring (Semantic Kernel) |
| `src/Assistant.Api` | Minimal API, DI composition, SSE endpoint `/api/chat` |
| `src/Assistant.SqliteMcp` | Our own read-only SQLite MCP server (stdio), launched by the API |
| `data/` | SQL migrations (`data/migrations`) and the generated metadata database (git-ignored) |
| `tests/Assistant.Tests` | xUnit tests (agent loop, tool validation, indexer, read-only SQL server) |
| `web` | React + TypeScript + Vite + Tailwind v4 dashboard |

## Run

```
ollama pull qwen2.5:7b-instruct
dotnet run --project src/Assistant.Api --urls http://localhost:5080
cd web && npm run dev
```

Targets .NET 10 (the installed runtime) rather than .NET 9.

## Roadmap

Work is planned in seven phases (0–6), each broken into stages: scaffold, streaming chat, MCP filesystem tools, ReAct loop with validation, SQLite metadata, dashboard UI, and hardening. Phases 0–3 (scaffold, streaming chat, MCP filesystem tools, ReAct loop with validation) are done. Phase 4 (SQLite metadata) is in progress: the schema, the incremental indexer and the read-only SQLite MCP server are built; steering the model between filesystem and SQLite tools (4.4) is next.

See the full plan in [docs/ROADMAP.md](docs/ROADMAP.md). Architecture details are in [docs/TAD.md](docs/TAD.md).
