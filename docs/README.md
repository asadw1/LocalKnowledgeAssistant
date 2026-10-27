# Docs

Documentation for the Local Knowledge & File Assistant.

| Document | Contents |
|---|---|
| [TAD.md](TAD.md) | Technical Architecture Document: components, runtime flow, contracts, decisions, risks |
| [ROADMAP.md](ROADMAP.md) | Phased delivery plan with stages and exit criteria |
| [PROMPT-SPEC.md](PROMPT-SPEC.md) | Model settings, system prompt versioning and structure, tool exposure, token budget |
| [MCP-SPEC.md](MCP-SPEC.md) | MCP server configuration, process lifecycle, tool-call validation contract, SqliteMcp tools |
| [DATA-SPEC.md](DATA-SPEC.md) | Metadata schema, migrations, incremental indexing, scheduling, retention |
| [API-CONTRACT.md](API-CONTRACT.md) | HTTP endpoints, SSE event schemas, client behaviour, error cases |
| [SECURITY.md](SECURITY.md) | Threat model, read-only controls by boundary, prompt-injection posture, change checklist |

## Project summary

A single-agent assistant that answers questions about local files and metadata. It runs a ReAct (Reason + Act) loop on a local open-weights model (Ollama, `qwen2.5:7b-instruct`), calls tools exposed by local MCP servers (filesystem, SQLite), and streams tokens and tool activity to a React dashboard over SSE.

**Learning goals:** core ReAct loop, tool calling, local MCP integration, strict validation of malformed tool calls, clean .NET/React layering.

## Prerequisites

- .NET SDK 10 (the project targets `net10.0`; the machine has no ASP.NET Core 9 runtime)
- Node.js 20+ and npm
- [Ollama](https://ollama.com) with `ollama pull qwen2.5:7b-instruct`
- Node.js on PATH for launching the stdio MCP servers (`npx`)

## Run locally

```
ollama serve
dotnet run --project src/Assistant.Api --urls http://localhost:5080
cd web && npm install && npm run dev      # http://localhost:5173
```

## Test

```
dotnet test
cd web && npm run build
```

## Status

Working end to end: streaming chat, MCP filesystem tools, the ReAct loop with tool-call validation and loop guards, the SQLite metadata index (rebuilt incrementally in the background) and a read-only SQLite MCP server. Not yet done: prompt and tool steering between filesystem and SQLite tools (Phase 4.4 onward), the full dashboard and the evaluation work; see [ROADMAP.md](ROADMAP.md).
