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
| `tests/Assistant.Tests` | xUnit tests (tool payload validation, agent loop) |
| `web` | React + TypeScript + Vite + Tailwind v4 dashboard |

## Run

```
ollama pull qwen2.5:7b-instruct
dotnet run --project src/Assistant.Api --urls http://localhost:5080
cd web && npm run dev
```

Targets .NET 10 (the installed runtime) rather than .NET 9.

## Roadmap

Work is planned in seven phases (0–6), each broken into stages: scaffold, streaming chat, MCP filesystem tools, ReAct loop with validation, SQLite metadata, dashboard UI, and hardening. Phases 0–2 (scaffold, streaming chat, MCP filesystem tools) are done; Phase 3 (ReAct loop with validation) is next.

See the full plan in [docs/ROADMAP.md](docs/ROADMAP.md). Architecture details are in [docs/TAD.md](docs/TAD.md).
