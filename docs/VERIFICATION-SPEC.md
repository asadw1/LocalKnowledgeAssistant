# Answer Verification and Confidence Spec

Status: **design only, nothing here is built.** Planned as roadmap stage 6.7 (backend) with the badge in the dashboard (5.3). Do not describe it as existing.

## 1. Goal

Show, next to each answer, a badge such as `Medium - 71%` that reflects how well the answer's claims **hold up against evidence**, not how sure the model sounds.

Rejected inputs, and why:

* **Model self-report** ("I am 80% confident"): not calibrated on a 7B model, and it measures tone.
* **Run signals alone** (a tool returned rows, no errors, iteration cap not hit): these show a tool ran, not that the answer is right. They may be used to *cap* a score, never to raise it.

## 2. What the number means

Confidence is the **weighted share of the answer's checkable claims that were verified**, with hard failures capping the label. It measures groundedness in tool evidence. It does **not** prove truth: if a tool returns wrong data, a faithful answer still verifies. This limit is shown in the UI help text.

## 3. Pipeline

Runs after the answer has streamed, as an `IAgentRunner` decorator (the runner itself stays free of extra work). The result arrives as a late `VerificationEvent` before `DoneEvent`, or after it if the dashboard keeps the stream open for it (decide in the API contract change).

1. **Claim extraction.** Split the final answer into atomic claims, each tagged `factual` (checkable against evidence) or `non-factual` (opinion, explanation, advice). A model call at temperature 0 with a strict JSON schema, validated like a tool call. Non-factual claims are excluded from the score.
2. **Evidence set.** The tool calls and results of this run (already bounded by `MaxToolResultChars`). Nothing else is evidence; the model's own prior text is not.
3. **Layer A, deterministic checks (no model).**
   * Paths and file names: must appear in tool output, or be confirmed by a read-only call.
   * Numbers, counts, sizes, dates: must equal a value in the evidence (normalise units and formats first).
   * Quoted text: must appear verbatim in evidence.
   * A mismatch is a **hard failure** (`contradicted`). A claim with no match is `unsupported`.
4. **Layer B, independent judge.** For factual claims Layer A could not settle, a fresh call sees one claim plus the relevant evidence and **not** the original reasoning. It returns `supported | contradicted | unsupported` and must quote the supporting or contradicting span. A verdict without a quote that is found verbatim in the evidence is downgraded to `unsupported`.
5. **Layer C, re-derivation.** Where a claim comes from a SQL result, re-run the query read-only through `ReadOnlyQueryEngine` and compare result sets. Where the question is answerable from files, an optional second agent run is compared on extracted facts (agreement raises the score, disagreement lowers it). Layer C is the most expensive and is configurable per claim type.
6. **Scoring.** See section 4.

## 4. Scoring

Per claim: `supported` = 1, `unsupported` = 0, `contradicted` = 0 plus a penalty.

* `score = supported_weight / checked_weight` over factual claims, expressed as a percentage.
* Any `contradicted` claim from Layer A, or from Layer B or C with a verified quote, **caps the label at Low** regardless of the percentage.
* Labels: `High` at 85% or more, `Medium` at 60-84%, `Low` below 60% or capped. Thresholds are configuration and are set from the evaluation data (section 7), not guessed.
* **No factual claims** (pure explanation): no percentage. The badge shows `Not verifiable`.
* **Factual answer with no tool evidence**: the badge shows `Unverified`, never a number.
* The badge always states coverage: `Medium - 71% (5 of 7 claims checked)`. Claims that could not be checked count against the score as `unsupported`; they are not silently dropped.

## 5. Event and API shape (proposed)

`VerificationEvent { Label, Percent?, Checked, Total, Claims[] }` with each claim carrying `Text`, `Verdict`, `Layer` and an `Evidence` excerpt. Add to `docs/API-CONTRACT.md` when built. Evidence excerpts are truncated and must not include the filesystem root or database path (see `SECURITY.md`).

Dashboard: a coloured badge (green, amber, red, grey for `Unverified` and `Not verifiable`) with an expandable list of claims and verdicts, reusing the 5.2 card style. The badge is hidden until the event arrives, with a "Verifying..." placeholder.

## 6. Failure and safety rules

* Verification failure (judge unavailable, malformed JSON, timeout) is logged with the run ID and shown as `Unverified`. It never fails or delays the chat run beyond its own timeout, and never produces a number.
* Claim text and evidence are untrusted: a file may contain instructions aimed at the judge. The judge prompt treats them as data, output is schema-validated, and the judge has **no tools**.
* Layer C only issues read-only calls through existing engines. It must not widen any read-only guarantee in `SECURITY.md`.
* Cost: extra model calls per answer. Bound claims per answer (default 10), judge calls per run and total verification time (`Verification:*` options, validated with DataAnnotations and `ValidateOnStart()`).

## 7. Calibration (required before the number is shown)

A percentage that does not track real accuracy is worse than none. Using the 6.1 evaluation set (with expected answers and labelled claims):

* Measure the judge's agreement with human labels per layer.
* Plot stated percentage against actual correctness; adjust weights and thresholds until each band is honest, and record the results in `docs/`.
* Until that is done, the badge ships behind a flag labelled "experimental".

## 8. Tests (when built)

* Layer A: each check type with matching and mismatching evidence, unit conversion, near misses.
* Judge: a scripted `IChatCompletionService` returning supported, contradicted, quote not in evidence, malformed JSON, and a hostile claim containing instructions.
* Scoring: boundaries at each threshold, the Low cap, `Not verifiable`, `Unverified`, unchecked claims counted against the score.
* Decorator: verification failure and cancellation leave the answer stream intact; the event carries the run ID.
* Layer C: re-run a query against a temp SQLite database; a write attempt through it still fails and the file is unchanged.
* Live check against Ollama with a deliberately wrong tool result and a deliberately wrong answer, reporting what the badge showed.

## 9. Open questions

* Whether the late event stays on the same stream (needs a longer-lived connection, see the SSE heartbeat target) or the dashboard polls `GET /api/runs/{id}` once 5.6 exists.
* Whether the judge should be a larger local model than the answering model. Same-model judging shares blind spots.
