# Otto codebase audit

Audit and cleanup of the whole repository, 2026-10-02, on top of commit `dbce969` ("Quiet update checker (1.1.0)").
Another Claude session committed `dbce969` while this audit was running; its files (Updater.cs, and its changes to
Program.cs, ChatPanel.cs, Providers.cs) are included in the audit and none of its work was overwritten.

Each change below is labelled with its reason, so "objectively wrong" can be told apart from "tidier":

- **BUG**: produced wrong behaviour; reproduced or shown by a test where possible.
- **DUP**: duplicated logic merged into one authoritative definition. Behaviour kept, except where noted.
- **DEAD**: code nothing uses.
- **CLARITY**: a name or comment that hid or misstated what the code does.

Nothing was changed for style alone.

---

## 1. Architecture (as found)

| Subsystem | Files | Role |
|---|---|---|
| Host | Program.cs (`Program`, `TrayApp`) | Entry point, debug switches, tray, hotkeys, wires every part together, owns the request lifecycle (`cts`) |
| Agent loop | Agent.cs | Tool-use loop on a worker thread; history; cheap/strong model escalation; history pruning and summarising; cost reporting |
| Model transport | Llm.cs | Anthropic and OpenAI-style streaming; history is kept in Anthropic format and translated per request; retries; transcription |
| Tools | Tools.cs, Apps.cs, Memory.cs, Graph.cs | web/files/PowerShell, opening apps and windows, notes and routines, Outlook mail and calendar |
| Screen control | Desktop.cs, Input.cs, UiTree.cs | Screenshots, UI Automation listings, SendInput mouse and keyboard, "did anything change" detection |
| UI | ChatPanel.cs, ChatView.cs, Overlay.cs | Slide-out panel, owner-drawn chat, glow, banner, ripple, Stop pill |
| Settings | Providers.cs (`Providers`, `SettingsWindow`, `Prefs`), Platform.cs (`KeyStore`, `Migration`, `Hotkeys`, `Icons`) | Provider presets and registry config, Credential Manager keys, preferences |
| Persistence | ChatStore.cs, Memory.cs, Tools.cs (backups) | `%LOCALAPPDATA%\Otto` |
| Other | Voice.cs, Sfx.cs, Updater.cs | Dictation and read-aloud, synthesised UI sounds, GitHub update check and install |
| New in this audit | Common.cs, Win32.cs, tests/Otto.Tests | Shared helpers (below), unit tests |

**Data flow of a request.** ChatPanel → `TrayApp.Run` → `Agent.RunAsync` (worker thread). The loop is
`Llm.CompleteAsync` → tool calls (`Tools.Run`, `Desktop.Run`, `Memory.RunRoutine`, `Graph.Run`) → results appended
to the history → repeat. Output goes back to the UI only through the Agent's callbacks (`OnText`, `OnStream`,
`OnTool`, `OnUsage`, `OnControl`, `Confirm`). Confirm blocks the worker thread on a `TaskCompletionSource` that
the panel completes.

**Shared mutable state and its owners.**
- `Agent.messages`: written by the worker thread during a request. The UI thread reads or replaces it only between
  requests; that's enforced by `TrayApp` checking `cts` before clear, retry, edit and open-chat, **not** by the
  lock. The lock covers only some accesses. This is now documented at the field.
- Desktop's static "last look" state (`lastUi`, `lastThumb`, `readings`, `repeats`) has to agree with what
  `Agent.PruneObservations` keeps in the history. This contract was implicit and broken; it's now explicit (see B6).
- Registry (`HKCU\Software\Otto`), Credential Manager (`Otto`, `Otto:<id>`, `Otto:microsoft`) and the files in
  `%LOCALAPPDATA%\Otto`.

**Areas needing care.** Win32 interop (SendInput, layered windows, UIA). Anything that changes the shape of the
history, because the API rejects unpaired `tool_use`. The prompt-cache-friendly ordering in Agent. Two Claude
sessions editing this repo at once.

**Over-engineering.** No significant cases. The code is mostly static classes with direct calls: no factories,
no single-implementation interfaces, no event buses, no manager layers. The real debt was duplication and
unchecked contracts between files, not abstraction.

---

## 2. Fixed

### Correctness

| # | Issue | Files | What changed | Validation |
|---|---|---|---|---|
| B1 | **Outlook sign-in deadlock.** The settings click handler called `Graph.StartSignIn().GetAwaiter().GetResult()` on the UI thread, and StartSignIn's awaits resume on that same thread (WinForms sync context), so it could never finish. | Graph.cs, Providers.cs | `GraphSignIn.Show` → `ShowAsync`, awaited from an async click handler. The button is disabled while signing in, and the handler is guarded against the settings window closing mid-request. | Same await pattern reproduced in a scratch program: **still blocked after 15 s**. Fixed path not run live (needs an Azure client ID). |
| B2 | **Every OpenAI-style request with a screenshot in history threw** "The node already has a parent". Images were collected in a `JsonArray` and then added to a second array. This affected Gemini, OpenAI, Grok and OpenRouter with vision on, from the first commit. | Llm.cs | Collect into a `List<JsonObject>`. | Found by the new test `Screenshots_in_tool_results_follow_as_a_user_image_when_the_model_can_see`, which now passes. |
| B3 | **Reading the front window's controls could fail as a whole.** UIA can report a vanished control as a raw `COMException` 0x80040201 (`UIA_E_ELEMENTNOTAVAILABLE`); only the managed `ElementNotAvailableException` was caught, so the model got "One or more errors occurred." | UiTree.cs | Skip that one element for either form. | Hit 1 in 7 runs of `--bench`; 0 in 6 runs after the fix. Intermittent by nature, so this isn't proof. |
| B4 | **Backups lost the original.** Two `write_file`s to one file within the same second wrote one backup name with `overwrite: true`, replacing the original (the version Undo needs) with the intermediate one. | Tools.cs | Millisecond timestamp plus a counter; never overwrite. | Test `Two_backups_in_the_same_second_keep_both_versions`; it fails with the old code (mutation check). |
| B5 | **A damaged routines.json was silently wiped.** `LoadRoutines` turned any read or parse error into "no routines", and the next `save_routine` or `forget_routine` wrote that empty set over the file. | Memory.cs | Readers treat damage as "no routines" (prompt unaffected). Writers move the damaged file to `routines.damaged-<time>.json` and say so in the tool result. A locked file (IOException) now errors instead of being overwritten. | Tests `A_damaged_routines_file_is_moved_aside_not_overwritten`, `..._doesnt_break_the_prompt`, `Routines_round_trip`. |
| B6 | **"Nothing changed since your last look" could point at a pruned reading.** Desktop counted looks (ui and screenshot, not zoom), while Agent kept the newest 2 of *all* computer and run_routine results ("Done.", errors, "Nothing changed" included). A few short results pushed the referenced reading out of the history. | Agent.cs, Desktop.cs, UiTree.cs | One definition, `Desktop.IsReading` (a UiTree listing, found by `UiTree.ListingMarker`, or anything with an image). One constant, `Desktop.KeptReadings = 2`, used by both sides. Zoom now counts as a reading. Short results don't count and are never pruned (they're short). | Tests `Short_results_never_push_a_reading_out` (fails with the old counting) and 4 other prune tests. |
| B7 | **The long-chat summary was treated as something you typed.** It's a plain-text user message, so `IsTurnStart` matched it. The history title became the summary text (cut at its first "] "), and the turn counter included it. | Agent.cs | `IsTurnStart` excludes messages that start with `Agent.SummaryHeader`. | Tests `Typed_messages_are_turn_starts...` and `A_summarised_chat_is_titled_by_what_you_asked...`; both fail with the old code. |
| B8 | **Cost shown in dollars for models whose price isn't known.** `model.Contains("sonnet")` priced every Sonnet at the $2/$10 entry; for example claude-sonnet-4-5 is $3/$15, a 1.5x under-report. | Agent.cs | Exact model ids, with an optional date suffix. Unknown models show a token count instead of a dollar figure. Added claude-sonnet-4-5 at $3/$15. The $2/$10 entry for claude-sonnet-5-5 is kept but marked UNSOURCED (see R1). | Tests `Haiku_cost_uses_list_price_and_cache_multipliers` and `Unknown_models_have_no_cost_rather_than_a_guess`. Live: Haiku call reported $0.0025. |
| B9 | Rate-limit fallback found a 429 by checking that the error message started with "API 429". | Llm.cs, Agent.cs, Common.cs | `ApiException` carries `Status`; Agent catches `e.Status == 429`. The message format is unchanged. | Build. Both providers tested live. |
| B10 | Compiler warning CS0108: `ChatView.Layout(Graphics)` hid `Control.Layout`. | ChatView.cs | Renamed to `LayoutItems`. | Build now has 0 warnings (was 1). |

### Duplicate logic consolidated (DUP)

| Concept | Copies before | Now | Behaviour change |
|---|---|---|---|
| Clip a string with "…" | 8 (Agent, Desktop, UiTree, Graph, Program.Shorten, ChatStore, Providers, Llm error) | `StringExtensions.Clip` (Common.cs) | none |
| HTML → text | 2 near-identical (Tools, Graph) | `Html.ToText(html, paragraphs)` | fetch_page now collapses `&nbsp;` runs; email also strips `<noscript>`/`<svg>` |
| Strip tags | Tools | `Html.StripTags` | none |
| Required tool argument `S()` | Tools, Graph | `Tools.S` | none |
| JPEG encoding | Desktop (q75), Attachment (q85) | `Jpeg.Encode(img, quality)` | none (codec now looked up once) |
| `%LOCALAPPDATA%\Otto` path | 6 places | `Paths.Data` (settable, so tests use a temp folder) | none |
| Window process name, title, list of open windows, foreground window, SetForeground, PostMessage | 2–3 copies each (Apps, UiTree, Desktop, ChatPanel) | `Win32` (Win32.cs) | UiTree's "Other windows" now dedupes after clipping titles to 50 chars |
| Exclude a window from capture | LayeredWindow, plus StopPill with a bare `0x11` | `Win32.ExcludeFromCapture` | none |
| Windows-blue fallback colour | Overlay literal | `ChatPanel.Accent` | none |
| On/off registry preference | 5 (Prefs ×3, Sfx.Enabled, Updater.Enabled) | `Prefs.Get/Set`, plus `Prefs.Sounds` and `Prefs.CheckUpdates` | none (same value names) |
| API key lookup with the `ANTHROPIC_API_KEY` override | 3 | `Provider.SavedKey()` / `Provider.KeyEnvVar` | none |
| Anthropic request headers | 2 | `Llm.AnthropicHeaders` | the model-list request now also sends `anthropic-workspace-id` when that env var is set |
| "Nothing changed" marker | 2 independent string constants (Agent, Desktop) that had to match | `Desktop.Unchanged` | none |
| Routine placeholder filling | inline in RunRoutine | `Memory.Fill` (extracted to test it) | none |

### Dead code removed (DEAD)

- Unused `using`s: Platform.cs (4, including `System.Speech.Recognition`), ChatStore, Providers, ChatPanel, Graph (2), Apps. Found with IDE0005 enforced in a build.
- **`System.Speech` package reference.** Nothing uses it since dictation moved to WinRT and provider transcription. Publish output has no `System.Speech.dll`.
- `ChatPanel.PaintBackdrop`'s unused `offset` parameter (IDE0060).
- The third `email_list` branch, whose condition was always true when reached.
- `FrontIsOtto` matching a process named "Jarvis". The old app is removed by setup.ps1, and it was never Otto's own panel. The check still matches any "Otto" process on purpose: a headless `--api-test` once typed into the user's panel.
- `Agent.IsStub` (superseded by `Desktop.IsReading`).

### Names and comments (CLARITY)

- Agent: the comment for `smart` had been pasted onto the `smartBlocked` line. The summariser said "last two turns" but keeps `KeepTurns` = 4. A comment said turn starts are "plain text" (they can be arrays since attachments). All three corrected.
- `StopPill` kept its "should be visible" state in the inherited `Tag` property; it's now a `wanted` field.
- `ChatPanel.rKey` → `rSettings` (it's the settings gear; the name was left over from the old key prompt).
- Voice: `16_000` and `16000 * 2 * 120` were byte counts standing for durations. They're now `SampleRate`, `BytesPerSecond`, `MaxSeconds`. Same values.
- Desktop.Difference: units (grey levels 0-255) and the fact that its thresholds are hand-tuned are now stated.
- Agent.messages: thread ownership documented (see Architecture).

### Tests added (tests/Otto.Tests, xunit, 56 cases)

Before this audit there were none. They cover behaviour, not implementation details:
- History invariants: every `tool_use` gets a result after an interruption; turn-start detection; the summary isn't a turn; pruning rules, including the B6 regression; `IsReading`.
- Provider translation: tool calls, Gemini thought-signature extras, screenshots with and without vision (B2), stripping extras for Anthropic, truncated tool arguments.
- Cost math against the price table and cache multipliers.
- The PowerShell confirm filter: 9 commands that must ask, 6 ordinary ones that must not.
- fetch_page `find`, HTML to text, the panel's markdown, dash and emoji filter, `Clip`.
- Key-name parsing, number coercion, step lists sent as a string or a single object.
- Routine placeholder escaping (quotes and backslashes).
- File-writing behaviour against a temp `Paths.Data`: backups (B4), damaged routines (B5), history titles (B7).

Run them with `dotnet test tests/Otto.Tests`. No API calls; nothing touches the real `%LOCALAPPDATA%\Otto`.

---

## 3. Validation performed

| Check | Result |
|---|---|
| `dotnet build` Debug and Release, after every change group | Succeeded, 0 warnings (baseline: 1 warning) |
| Unused-code analyzers (IDE0005/0051/0052/0059/0060 enforced in a one-off build) | Only the items removed above |
| `dotnet test` | 56 passed, 0 failed |
| Mutation check: put back the old code for B4, B6, B7 | Each one fails its test (1, 1 and 2 failures) |
| Deadlock repro (B1) | The old pattern was still blocked after 15 s |
| `Otto.exe --bench` (Release, 2560x1440), 6 runs | thumbnail ~31 ms, settle (still screen) ~240 ms, 1024px JPEG ~53 ms, UI listing ~59 ms. Same as before the changes; the B3 crash didn't recur |
| `--api-test` on Gemini (user's provider): 2-turn chat | Replies stream; the second turn remembers the first; history save and reload OK |
| `--api-test` on Anthropic (provider switched temporarily, then restored to gemini) | Reply OK; cost $0.0025 from the exact-id table |
| `dotnet publish` | 9 files; no test assemblies or System.Speech in the output |

**Not validated:** Outlook sign-in end to end (no client ID). Voice. Updater install. Screen-control tasks
end to end. Typing tests weren't run on purpose while the user may be at the PC.

---

## 4. Flagged for human review

| # | Item | Where | Why it's suspicious | What's missing |
|---|---|---|---|---|
| R1 | **UNSOURCED / UNVALIDATED:** claude-sonnet-5-5 priced at $2 in / $10 out per million tokens. | Agent.ClaudePrices | No source in the repo, git history or earlier sessions. | Check anthropic.com/pricing. If it's wrong, the "$ this chat" counter is wrong whenever Otto escalates. |
| R2 | **Effort setting may be backwards.** `CallAsync` passes `lowEffort: smart`, so effort "low" is sent exactly when the task was escalated for "careful planning, real reasoning" (the escalate tool's own description). The cheap model never gets it (Haiku doesn't accept it). If someone sets a Sonnet as the *everyday* model, it runs at default effort and the stronger model at low. | Agent.CallAsync, Llm.AnthropicAsync | Could be a deliberate cost cap; no rationale is recorded anywhere. | Decide the intent. Left unchanged because it can't be established. |
| R3 | Outlook: any OAuth error response while refreshing the token signs the user out, including transient ones. Per OAuth 2.0, `invalid_grant` is the error that means the refresh token is dead. | Graph.Token | Could log the user out on a server hiccup. | Test with a real account before narrowing it to `invalid_grant` / `interaction_required`. |
| R4 | Outlook unread-only listing sends no `$orderby`; the tool promises "newest first". | Graph.Run email_list | Graph's default order for a `$filter` query wasn't checked. | One real-mailbox test. |
| R5 | The PowerShell confirm regex is a convenience, not a security boundary. String-built commands (`& ("Remove-"+"Item") x`), other interpreters (`cmd /c del`: `del` is caught, but `python -c` isn't) and so on get past it. | Tools.RiskyCommand | The system prompt relies on it as the backstop for "permanently deleting". | A product decision: accept it, or move deletes to a dedicated tool. |
| R6 | **Hand-tuned constants, origin unrecorded:** change thresholds 0.4 / 0.6 / 24 grey levels; Settle max 1.4 s and 80 ms polls; UiTree 220 elements, 4,000 chars, 8 s timeout, 4 px dedupe grid; Agent MaxSteps 60, EscalateAfterSteps 15, SummariseAbove 80k tokens, KeepTurns 4, KeepImageTurns 3; Tools 6,000 / 5,000 output chars, 3 MB page cap, 150 dir entries, 200 backups; ChatStore 100 chats; Llm 3 retries, 20 s cap, max_tokens 2048; notes 3,000 chars. | various | These are design and tuning parameters, mostly commented with intent. None claims to be physical. Left as they are. | Only revisit with measurements. |
| R7 | Updater replaces only `Otto.exe`. An install made by setup.ps1 is framework-dependent (Otto.exe + Otto.dll + deps); the release exe is self-contained single-file, so the old DLLs are left beside it. The download isn't hash- or signature-checked (it relies on GitHub TLS). | Updater.InstallAsync | Written by the concurrent session; not exercised here. | One update from a setup.ps1 install; decide on checksum verification. |
| R8 | `UiTree.FindByName` (routine clicks by label) walks the tree with no timeout; `Describe` has an 8 s guard. | UiTree.cs | A huge page could hang a routine step. | Low risk; same guard if it's ever seen. |
| R9 | Chat, notes and routines files are written in place (not write-temp-then-rename). A crash mid-write corrupts that one file. Routines now survive that (B5); a damaged chat is skipped in the history list. | ChatStore, Memory | Rare. | Optional atomic writes. |
| R10 | Reopening a summarised chat shows the summariser's "Got it." as an Otto bubble. The summary itself is no longer shown as yours. | Program.OpenChat | Cosmetic. | — |
| R11 | Voice treats HRESULT 0x80045509 as "online speech recognition is off". | Voice.StartAsync | Can't verify here (no mic test). It was presumably observed in an earlier session. | Confirm on a machine with the setting off. |
| R12 | `uninstall.ps1 -All` hard-codes the Credential Manager targets (one per provider). | uninstall.ps1 | Has to be updated by hand if a provider is added. | — |
| R13 | `KeyStore.Save` stores `UserName = "anthropic"` for every provider's credential. | Platform.cs | Misleading metadata only; nothing reads it. Left to avoid touching stored credentials. | — |

---

## 5. Intentionally left unchanged

- **History in Anthropic format, translated for OpenAI-style providers (Llm).** It's the one real abstraction in
  the code and it earns its place: one history, several providers, round-tripped Gemini signatures.
- **`TrimMemory` (GC.Collect + EmptyWorkingSet when the panel hides).** Deliberate, for the low-RAM goal.
- **Empty or broad catches that are correct as best-effort:** Migration (one-time carry-over), Sfx and Speaker
  (no audio device), Llm.Warm (pre-connect), ChatStore.Save (documented: history must never break a request),
  clipboard paste, image decode fallback in Attachment, Thumbnail on the secure desktop, Voice stop, the Graph
  mark-as-read fire-and-forget, Updater cleanup retries.
- **`ChatPanel.Plain` style filter** (dashes, emoji): a product decision, not a workaround.
- **Thumbnail greyscale weights 3/6/1 ÷ 10:** an integer approximation of Rec.601 luma (0.299/0.587/0.114).
- **Screenshot width 1024 and its "~790 tokens" comment:** consistent with Anthropic's documented image cost
  (width × height / 750 ≈ 786 for 1024×576).
- **Cache pricing multipliers** (5-minute write 1.25×, 1-hour write 2×, read 0.1× input): Anthropic's published
  multipliers. Now commented where used.
- **Thumbnail capture performance (~31 ms).** A cheaper capture would change the sampling that the hand-tuned
  thresholds (R6) depend on, for a saving that's small next to model latency.
- **SendInput absolute-coordinate mapping (`× 65535 / (width − 1)`)**: one of the two conventions in common use.
  Not verified pixel-exact on multi-monitor setups; no reported problem.
- **Sound synthesis and overlay drawing constants:** aesthetic, not models of anything.
- **`Agent.messages` locking:** partial, but it isn't what makes access safe; TrayApp's single-request gate is.
  Documented rather than reworked.
