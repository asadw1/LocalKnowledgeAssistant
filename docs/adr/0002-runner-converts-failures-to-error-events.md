# 0002. The runner turns unexpected failures into `ErrorEvent` and `DoneEvent`

**Status:** Accepted, 2026-11-11 (ROADMAP 5.1). Built.

## Context

`POST /api/chat` sends its headers before the run starts, so an exception during the run cannot change the status code. Before this decision, an unexpected exception (Ollama not running, a missing model, a tool registry failure) propagated out of the runner and the stream just ended with no `ErrorEvent` and no `DoneEvent`. The dashboard could not tell that from a normal finish. Only the run timeout and the iteration cap produced an `ErrorEvent`.

## Decision

- `SemanticKernelAgentRunner.RunAsync` catches an unexpected failure at its loop boundary, logs it at Error with the run ID, and emits `ErrorEvent` then `DoneEvent`. Tokens already sent stay.
- The `ErrorEvent` message is written for the user and never quotes the exception text, which can contain URLs, paths or arguments. An `HttpRequestException` or a `TaskCanceledException` (an `HttpClient` timeout) gets "Could not reach the model server. Check that Ollama is running and the configured model is pulled." Anything else gets "The run failed unexpectedly. See the server log for details."
- A cancellation requested by the caller (Stop or a client disconnect) is not caught here and still propagates, so nothing more is sent to a client that has gone. A timeout of the run's own budget keeps its existing "timed out" handling.
- The broad catch is intentional and is a true failure boundary in the sense of CLAUDE.md: it turns a failure into something the system can act on.
- The client reports a stream that ends without `DoneEvent` as an error ("The connection closed before the run finished."), because every completed run now ends with one.

## Alternatives considered

- **Catch in the endpoint.** It would also cover failures outside the runner, but it sits above the runner's timeout and iteration handling, would have to know about HTTP framing, and would need its own `ErrorEvent` construction. The runner already owns the event vocabulary and the timeout case.
- **Check Ollama before streaming and return a real HTTP status.** Gives a cleaner status code, but it adds a probe to every request, races with the actual call, and does not cover failures that happen mid-run. A readiness check belongs in the status endpoint (ADR 0004).
- **Include the exception message in `ErrorEvent`.** More helpful to a developer, but exception text is not written for display and may expose local paths or URLs. The full exception is in the server log instead, tied to the run ID (ADR 0001).

## Consequences

- A model name Ollama does not have (a 404) is reported the same way as an unreachable server. The UI cannot tell them apart until the status panel (ROADMAP 5.5) can say which it is.
- A failure that happens outside the runner (for example a dropped connection) can still end the stream without `DoneEvent`. The client's missing-`DoneEvent` check is the backstop.
- Checked live against a stopped Ollama and against a missing model, and by unit tests with a throwing fake model. See [API-CONTRACT.md](../API-CONTRACT.md) section 5.
