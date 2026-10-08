# Docs

Documentation for the Local Knowledge & File Assistant.

| Document | Contents |
|---|---|
| [TAD.md](TAD.md) | Technical Architecture Document: components, runtime flow, contracts, decisions, risks |
| [ROADMAP.md](ROADMAP.md) | Phased delivery plan with stages and exit criteria |

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

Streaming chat against Ollama works end to end. MCP wiring, the ReAct loop with tool validation and the full dashboard are not yet implemented; see [ROADMAP.md](ROADMAP.md).
