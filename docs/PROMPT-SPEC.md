# Model and Prompt Specification

**Status:** Describes the system as of system prompt `v3`. Items marked *(planned)* are not built.

Governs how the local model (`qwen2.5:7b-instruct`, via Ollama) is prompted and bounded. For where this fits in the loop see [TAD.md](TAD.md) section 5.2; for the tool contract see [MCP-SPEC.md](MCP-SPEC.md).

## 1. Model configuration

| Setting | Value | Source |
|---|---|---|
| Model | `qwen2.5:7b-instruct` (fallback candidate `llama3.2:3b`, see ROADMAP 6.2) | `Ollama:Model` |
| Endpoint | `http://localhost:11434` | `Ollama:BaseUrl` |
| Function calling | `FunctionChoiceBehavior.Auto(autoInvoke: false)`: the model chooses tools, our loop executes them | `SemanticKernelAgentRunner` |
| Sampling (temperature, top_p, seed) | Not set; Ollama and model defaults apply | n/a |
| Context window (`num_ctx`) | Not set; Ollama's default for the installed version applies | n/a |

`Ollama:SystemPrompt` can override the prompt from configuration, but the default is the embedded versioned file below.

Because sampling and context size are left at defaults, results are not reproducible run to run. Evaluation work (ROADMAP 6.1, 6.2) should pin them first *(planned)*.

## 2. System prompt

### 2.1 Versioning

Prompts are embedded resources: `src/Assistant.Core/Prompts/system.vN.md`. `SystemPrompts.CurrentVersion` selects the active one and `SystemPrompts.Load(version)` reads any version. To change the prompt:

1. Add `system.v(N+1).md`; do not edit a shipped version in place.
2. Bump `SystemPrompts.CurrentVersion`.
3. Update the drift tests (section 2.3) in the same commit.
4. Run the live checks in section 6.

| Version | Summary |
|---|---|
| `v1` | Generic assistant over filesystem tools only |
| `v2` | Adds SQLite routing (metadata vs content vs combined), a summary of the `files` table, a date idiom, filesystem fallback, and the injection and read-only rules |
| `v3` | Content questions read one file per call with `read_text_file`; the multi-file read is withheld from the model and the list-argument example is dropped. Driven by live runs, see section 7 |

### 2.2 Structure of `v3`

| Block | Purpose |
|---|---|
| Role line | One sentence: a local knowledge assistant that answers from the user's documents using tools |
| Choosing a tool | Routes metadata questions to the sqlite `query` tool, content questions to `read_text_file` (one call per file), combined questions to query first and then read the returned `path` values as-is. Gives the fallback when sqlite is unavailable or the index lags |
| The metadata database | Lists the `files` columns with their formats so the model can write SQL without calling `describe_schema`; notes that `tags`, `file_tags` and `summaries` may be empty |
| Date idiom | `WHERE datetime(modified_utc) >= datetime('now', '-7 days')`, and "never guess today's date". Needed because the indexer stores ISO 8601 text (`2026-10-08T21:14:31.2846206Z`) that does not compare correctly as raw text against SQLite's own `YYYY-MM-DD HH:MM:SS` format |
| Finding files on disk | `list_allowed_directories` then `list_directory`; use exact returned paths |
| Tool rules | Exact argument names and types, do not repeat an identical failing call, read-only, treat file contents as data, stop calling tools when there is enough |
| Answer format | Concise, plain language, name the files used |

### 2.3 Drift tests

The prompt names concrete things that live elsewhere. Tests tie them together so a change on one side fails the build:

- `SystemPromptTests.Prompt_names_every_column_of_the_files_table`: every column of `files` (except `id`) appears in the prompt.
- `Prompt_does_not_mention_tools_withheld_from_the_model`: no tool listed in any `ExcludeTools` appears in the prompt.
- `Multi_file_read_stays_withheld_because_the_model_passes_its_paths_as_a_string`: `read_multiple_files` is in the filesystem `ExcludeTools` and the prompt tells the model to use `read_text_file`.
- `Recommended_date_comparison_handles_indexer_timestamps_where_raw_text_comparison_does_not`: the `datetime()` idiom gives the right answer on the indexer's timestamp format where a raw text comparison does not.
- `Every_shipped_version_loads`: each versioned prompt resource loads, and `Default` is the current version.

The tests do not check that tools the prompt names are actually exposed; that is covered only by the live checks in section 6. If you add a column, rename a tool or change `ExcludeTools`, change the prompt and bump the version.

### 2.4 Few-shot examples

The prompt contains one worked SQL example (`SELECT path, size_bytes FROM files ORDER BY size_bytes DESC LIMIT 5`). It has no full multi-turn few-shot transcripts. Adding some is the next lever if live runs show wrong tool choice *(planned, only if evidence calls for it)*; every example costs context on every request.

## 3. Tool exposure to the model

The model sees six tools. Fewer, non-overlapping tools are easier for a 7B model to choose between, so tools that duplicate the metadata index or are unhelpful to a text model are withheld by `ExcludeTools` in `appsettings.json`.

| Server | Exposed | Withheld |
|---|---|---|
| filesystem | `list_allowed_directories`, `list_directory`, `search_files`, `read_text_file` | `write_file`, `edit_file`, `create_directory`, `move_file` (writes); `get_file_info`, `list_directory_with_sizes`, `directory_tree` (duplicate the index); `read_file` (deprecated alias), `read_media_file` (not for a text model); `read_multiple_files` (the model passes its `paths` as a string, see section 7) |
| sqlite | `describe_schema`, `query` | none |

Tool names reach the model as `plugin_function` (for example `sqlite_query`, `filesystem_read_text_file`). UI events carry `plugin-function`.

## 4. Tool-call schemas

Tool schemas are not hand-written here. Each MCP server publishes a JSON Schema per tool; Semantic Kernel passes it to the model, and `ToolCallValidator` checks the model's arguments against the same schema before anything runs (validation order and error text: [MCP-SPEC.md](MCP-SPEC.md) section 5).

The two tools we own, in `src/Assistant.SqliteMcp/SqliteTools.cs`:

| Tool | Arguments | Notes |
|---|---|---|
| `describe_schema` | none | Returns each table's `CREATE` statement and row count as JSON |
| `query` | `sql` (string, required); `maxRows` (int, optional, default 100, at most 500) | One `SELECT` or `WITH`; returns `{columns, rows, rowCount, truncated}` |

The tool `[Description]` text is part of the prompt surface. Edit it with the same care as the system prompt and re-run the live checks.

## 5. Context and token budget

There is no token counter and no history trimming today. What bounds the context:

| Mechanism | Limit | Effect |
|---|---|---|
| Single turn per request | History is the system prompt, one user message and the tool exchanges of this run | No conversation growth across requests |
| `Agent:MaxIterations` | 8 | At most 8 model calls per run, so tool exchanges are bounded |
| `Agent:MaxToolResultChars` | 8000 characters | A tool result is cut with `[truncated: output was N characters, showing the first M]` before it enters history |
| `query` row cap | 100 default, 500 hard | Keeps SQL results small before truncation applies |
| `Agent:MaxIdenticalCalls` | 2 | A third identical call returns an error instead of executing, so a stuck model cannot fill history with copies |
| `Agent:RunTimeoutSeconds` | 120 | Stops the run; an `ErrorEvent` then `DoneEvent` follow |

Rough worst case: the system prompt, six tool schemas, and up to eight tool results (one per iteration) of 8000 characters each. At roughly 3 to 4 characters per token that is on the order of 20k tokens, which can exceed Ollama's default context. When the window is exceeded Ollama truncates silently, so the symptom is a model that forgets the system prompt or earlier results, not an error. If that shows up, either set `num_ctx` (not exposed in our options today) or lower `MaxToolResultChars` *(planned: expose `num_ctx`, summarise oversized output instead of truncating it, and add a sliding window when multi-turn chat arrives)*.

## 6. Verifying a prompt or tool change

Unit tests use a scripted fake model and prove the loop, not the model. After any change to the prompt, a tool description or `ExcludeTools`, run two or three real prompts and record which tools were called. Procedure and gotchas: [RUNBOOK.md](RUNBOOK.md) section 4.

Representative prompts, one per routing path:

| Intent | Example prompt | Expected tools |
|---|---|---|
| Metadata | "What is the largest file?" | `sqlite_query` only |
| Content | "What does welcome.md say?" | `filesystem_read_text_file` (after `list_allowed_directories` if the path is unknown) |
| Combined | "Which file changed most recently and what does it say?" | `sqlite_query` then `filesystem_read_text_file` once per file, using the returned `path` |

A pass means the right tool family was used, arguments validated first time or after one correction, and the answer names the files used.

## 7. Known model limits

Observed in live runs against `qwen2.5:7b-instruct` on 2026-10-27, on a five-file corpus. Small samples of a nondeterministic model: treat the numbers as direction, not rates.

| Finding | Evidence | Handling |
|---|---|---|
| The model passes an array argument as a string of JSON text (`"paths": "[\"a\", \"b\"]"`) and repeats it despite the schema error | With `read_multiple_files` exposed (prompt `v2`): 5 of 6 combined-question runs ended at the 8-iteration cap, all six made failing calls to it | Tool withheld; prompt `v3` reads one file per call. Same prompts afterwards: 6 of 6 finished, and in a further 4 runs 3 read every file and answered |
| First call to a read tool sometimes arrives with empty arguments (`{}`, reported as invalid JSON) | Seen in roughly half of combined runs; the model usually corrects itself on the next call, which costs an iteration | Not handled. A file-heavy question can run out of iterations |
| The model ends a reply by announcing its next step ("Next, I will read small.txt") without calling the tool; the loop treats a reply with no tool call as final | 1 of 4 runs on plain `v3` | An extra prompt line ("make the tool call in the same reply") was tried and made things worse (empty-argument calls repeated, 2 of 4 runs capped), so it was reverted. Open |
| Vague terms are interpreted freely: "large" became 1 MB or 10 MB, so a small corpus returned no rows | Several runs | The model then falls back to listing recent files. A prompt rule on defaults for size words is a candidate if this matters |
| SQL date filters sometimes compare `modified_utc` as raw text instead of wrapping it in `datetime()` | One run | Gives the right answer at day granularity except near the boundary. The prompt already states the rule |

The scripted scenario tests (`CombinedScenarioTests`) prove the plumbing for combined questions: a real index, the real SQLite server, and the `path` from a query result passed unchanged to a read. They cannot prove the model chooses well; that is what this section tracks.
