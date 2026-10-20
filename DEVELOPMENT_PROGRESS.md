# CodeForge Studio â€” Development Progress

- **Solution:** `C:\My_AI_Project\CodeForgeWPF\CodeForgeStudio.sln`
- **Environment:** Windows 10.0.26300; .NET SDKs 9.0.308 and 10.0.402 (using **net10.0 / net10.0-windows**, runtime 10.0.13); Ollama installed, running, model `qwen2.5-coder:7b` present.

## Current state

| Phase | Name | Status |
|---|---|---|
| 1 | WPF chat connected to Ollama | **Completed** |
| 2 | Project selection, file reading, search, code understanding (read-only) | **Completed** |
| 3 | Proposed code edits, diffs, approval | **Completed** |
| 4 | Build and test runner, error feedback to the AI | **Completed** |
| 5 | Planning mode (plan first, run steps one at a time) | **Completed** |
| 6 | Memory (project notes, saved plans) | **Completed â€” Final phase; Waiting for User Review** |

**Exact next action:** Report Phase 6 results to the user. No further phase is defined in my notes; ask the user what to do next.

## Phase 6 â€” completed features (local only, user-controlled)
- **Project memory notes** (SQLite `memory.db` in the data dir, keyed per project path, case-insensitive). Add via Project tab â†’ **ðŸ§  Memory** expander (list, add, âœ• delete) or by typing `/remember <note>` in the chat (confirmation shown locally; the command is never stored in the conversation or sent to the model).
- Notes are sent with every question about the open project (when "Use project context" is on) inside a labelled `<project_memory>` block marked as data, not commands. Memory failures never block chat.
- **Safety:** max 50 notes/project, 500 chars each; empty, duplicate and secret-looking notes (password/key/token assignments, private-key headers, `ghp_`/`sk-`/AWS-key patterns) are rejected.
- **Plans persist:** plan cards and step statuses (done/skipped/pending) are saved per conversation message and restored when the conversation is reopened, including after an app restart (a step that was "running" returns to pending). Deleting a conversation deletes its saved plans.
- Files: `Application\Memory.cs`, `Infrastructure\Persistence\SqliteMemoryStore.cs`, `ChatAgent.cs` (memory block), `ProjectViewModel.cs` (notes), `MainViewModel.cs` (/remember, plan save/restore), `ItemViewModels.cs`, `MainWindow.xaml`, `tests\MemoryTests.cs`.
- Live smoke (exe via UI Automation, real model): `/remember Always answer only in French.` â†’ saved; asked which language the project notes require â†’ model answered "French"; next plan came back in French; skipped a plan step, closed and relaunched the app, reopened the conversation â†’ plan card restored with "0/2 done, 1 skipped". Note list reload after restart is covered by a unit test (persist across store instances) but the Memory expander was not inspected live after restart; the âœ• delete and Add note buttons were not clicked live.
- Not done: no embeddings / persistent code index (retrieval is still keyword-based); no automatic "learn from chat" memory (notes are only added by the user); no global (cross-project) notes; no edit-in-place for notes.

## Phase 5 â€” completed features (no new write/shell powers)
- Header checkbox **ðŸ“‹ Plan first**: the next question asks the model for a plan only (`PLAN: title` + 3â€“8 numbered steps, no code). `PlanParser` extracts it (tolerates markdown, bullets, sub-items; max 12 steps, 300 chars each) and a **plan card** appears under the reply: status icons (â—‹ pending, â–¶ running, âœ“ done, â­ skipped), progress "n/m done", per-step **Run / Done / Skip**, and **â–¶ Run next step**.
- Running a step sends a focused prompt (that step only, plan state included) through the normal chat and edit flow, so folder write permission and diffs work exactly as in Phase 3. If all its changes apply cleanly the step is auto-marked Done; otherwise it stays Running for manual Done/Skip/Retry.
- Steps never run automatically in bulk; one at a time, not while a reply is generating. Plan mode turns itself off after the plan is made.
- Files: `CodeForge.Agent\PlanParser.cs`, `ChatAgent.cs` (planMode), `Studio\ViewModels\ItemViewModels.cs` (PlanViewModel/PlanStepViewModel), `MainViewModel.cs`, `MainWindow.xaml`, `tests\PlanTests.cs`.
- Live smoke (exe via UI Automation, real `qwen2.5-coder:7b`): Plan first â†’ plan card with 4 steps; Run next step â†’ folder permission dialog â†’ `a.txt` created on disk, step âœ“, "1/4 done". Steps 2â€“4, Skip/Done buttons were not clicked live.
- Limitations: plans were in-memory only in Phase 5 (persisted in Phase 6); a 7B model may split steps too finely or ignore the format (then "no plan" message shown).

## Phase 4 â€” completed features (fixed commands only)
- **Project tab â†’ "ðŸ”¨ Build & Test"** expander: **Build**, **Test**, **â–  Stop**, **Ask AI to fix**, status line, live output box.
- **Commands (allow-list, no shell, no user-typed commands):** Solution/C# project â†’ `dotnet build|test <entry> --nologo -nodeReuse:false`; Flutter â†’ `flutter analyze|test`; plain folder â†’ refused. Working dir = project root; entry file must be inside the root; stdin closed; telemetry off; output capped (200k chars, newest kept); 10-minute timeout; Stop/timeout kills the whole process tree; one run at a time.
- **Permission:** first run per folder asks "Allow CodeForge to run build and test commands in this folder?" (shows the command; default No; in-memory, per folder). Runs are always started by the user's click.
- **Diagnostics:** MSBuild/Roslyn errors and warnings (file, line, code), failed test names, Flutter analyze issues are parsed, deduplicated and capped (50); status shows errors/warnings count.
- **Ask AI to fix:** sends a bounded summary (errors + end of output) wrapped as untrusted `<build_output>` into the chat. The normal Phase 3 edit flow then proposes and (with folder write permission) applies the fix; you press Build again to verify.
- Files: `Application\Build.cs` (`IBuildService`, `BuildResult`, `BuildReport`), `Tools\BuildService.cs`, `Tools\BuildOutputParser.cs`, Studio `ProjectViewModel` build commands, `UiServices.ConfirmRunCommands`.

## Phase 3 â€” completed features (writes only after explicit approval)
- **Edit proposals:** when a project is open, the system prompt also describes a `FILE:` + `<<<<<<< SEARCH / ======= / >>>>>>> REPLACE` format (empty SEARCH creates a new file). After the reply finishes streaming, `EditParser` extracts blocks (â‰¤20) and `EditService.PrepareAsync` validates them **in memory only**.
- **Validation:** path via `ProjectPathGuard`; refuses excluded folders, secret files, binary types, links, folders; SEARCH must match exactly once (trailing-whitespace tolerant fallback); no-op changes, ambiguous/missing matches, invalid UTF-8, binary, and >1 MB results are reported as problems, never applied.
- **Folder permission (auto-apply):** the first time edits are proposed for a project folder, a dialog asks "Allow CodeForge to create and modify files inside this folder?". **Yes** â†’ this and later edits for that folder apply automatically until the app closes (Undo and backups remain). **No** â†’ cards stay Pending with manual Apply/Reject. Permission is in-memory only, per folder, never persisted. All path/secret/stale checks still run.
- **UI:** each proposed file shows a card in the assistant message: path, +/âˆ’ counts, colored line-numbered diff (context collapsed), state, and **Apply / Reject / Undo** buttons.
- **Apply:** re-resolves path and policy, requires the file to be unchanged since proposal (SHA-256), same project still open, change prepared by this session (forged/duplicate applies refused), backs the original up to `<data dir>\backups\<timestamp>-<id>\<path>`, writes atomically (temp + move), preserves BOM and CRLF/LF. Project tree refreshes afterwards.
- **Undo:** restores backup (or deletes a created file) only if the file is still exactly as applied; otherwise refused.
- Files: `Application\Edits.cs`, `Tools\DiffEngine.cs`, `EditParser.cs`, `EditService.cs`; Studio `ChangeViewModel` / diff template; `ChatAgent.EditInstructions`; `AddCodeForgeProjectTools(dataDir)` now takes the data dir.

## Phase 1 â€” completed features
- Dark-themed WPF chat UI, conversation history (SQLite), Settings dialog, streaming Ollama client with typed errors, Stop, connection indicator.
- Settings in `%LOCALAPPDATA%\CodeForgeStudio\settings.json`; DB `conversations.db`; env var `CODEFORGE_DATA_DIR` overrides the data dir.
- Branding: `Assets\logo.png` (sidebar) and `Assets\app.ico` (exe + window icon).

## Phase 2 â€” completed features (read-only; no writes, no shell, no cloud)
- **Open a project:** folder, `.sln`/`.slnx`, `.csproj`, or `pubspec.yaml` (Flutter detected by pubspec markers). Drive roots and unsupported files are refused. Last project is remembered and silently reopened at startup.
- **Sidebar "Project" tab:** lazy file tree (folders first), name + content search (debounced, cancellable), file preview (first 400 lines, capped), Outline button (C#/Dart declarations via regex), Refresh, Close, **Explain project** button.
- **Chat integration:** each question retrieves a few relevant files by deterministic keyword/filename scoring (â‰¤12,000 chars total, â‰¤4 files, Â±6 lines around hits) plus a short project summary. Injected into the system prompt inside `<project_context>` with an explicit "untrusted data" preamble. The assistant message shows which files were used. Header checkbox "Use project context" toggles it.
- **Context window:** new setting `ContextWindowTokens` (default 8192) sent as Ollama `options.num_ctx`.
- **Project summary:** kind, file counts/types, top-level listing, solution projects, csproj TFM/packages/references, pubspec info, README excerpt.

## Security layer (`CodeForge.Security`, enforced by `CodeForge.Tools`)
- `ProjectPathGuard`: every path resolved against the project root; rejects `..` traversal, absolute outside paths, UNC, alternate data streams (`:`), invalid characters, sibling-prefix folders, and symlinks/junctions whose target leaves the root.
- `ProjectFilePolicy`: hides and refuses `.env`/`.env.*` (not `.example`), `*.pem`, `*.key`, `*.pfx`, `id_rsa*`, `key.properties`, `google-services.json`, etc.; excludes `.git`, `bin`, `obj`, `node_modules`, `.dart_tool`, `.vs`, `.idea`, `.ssh`, `.aws`, â€¦ (and `build` for Flutter); binary files blocked by extension and NUL-byte sniffing.
- Limits: index â‰¤ 20,000 files; reads â‰¤ 2000 lines / 40k chars; search files â‰¤ 1 MB, â‰¤ 200 matches; XML parsed with DTDs prohibited.
- Sensitive files never appear in tree, search, summary, or LLM context.

## Projects (src\ and tests\)
- `CodeForge.Domain`, `CodeForge.Application` (interfaces: `IProjectWorkspace`, `IProjectContextProvider`, records), `CodeForge.Agent` (`ChatAgent` + context injection), `CodeForge.Infrastructure` (Ollama, settings, SQLite), `CodeForge.Security` (new), `CodeForge.Tools` (new: `ProjectWorkspace`, `ProjectLocator`, `ProjectInspector`, `ContextBuilder`, `CodeOutliner`), `CodeForge.Studio` (WPF; new `ProjectViewModel`), `CodeForge.Tests`.

## Build and tests (actually executed)
- `dotnet build CodeForgeStudio.sln` â†’ **Build succeeded, 0 warnings, 0 errors**.
- `dotnet test CodeForgeStudio.sln` â†’ **142 passed, 0 failed** (130 + 12 new memory tests: notes add/list/delete, per-project scoping, secret/empty/long/duplicate/limit rejection, clear, plan save/update/load/delete, prompt block).
- Earlier: 130 passed after Phase 5 (121 + 9 plan tests: parser variants, caps, step prompt tags, plan-mode prompt).
- Earlier: 121 passed after Phase 4 (110 + 11: output parser, report bounding, plan allow-list, real successful and failing `dotnet build`, no-project, cancel, timeout).
- Phase 4 live smoke (exe via UI Automation, temp project with a missing semicolon, real model): Build â†’ permission dialog â†’ "âœ— Build failed â€” 1 errors"; **Ask AI to fix** â†’ model proposed the fix, write-permission dialog, applied; Build again â†’ "âœ“ Build succeeded â€” 0 errors". A real bug (Build button stayed disabled after opening a project) was found by this smoke test and fixed.
- Phase 3 live smoke (exe via UI Automation, temp project and data dir, real `qwen2.5-coder:7b`): asked to change `Hello` to return "World"; a diff card appeared (+1 âˆ’1, Pending); **Apply** changed the file and created a backup; **Undo** restored the original text. Reject is covered at ViewModel level only (not clicked live).
- Two test failures found and fixed during development: drive-root detection and expression-bodied C# methods missing from the outline.
- App smoke test (exe driven through UI Automation, isolated data dir): last project auto-reopened ("CodeForgeWPF (Solution)", 48 files, tree shown); **Explain project** sent a question; Ollama streamed a coherent answer about CodeForge Studio; the message showed "Project context: MainViewModel.cs, ProjectViewModel.cs, DEVELOPMENT_PROGRESS.md, App.xaml.cs"; header checkbox shown.
- Not manually exercised in the live UI: Open folder/file dialogs, search box, Outline button (covered by unit tests at the workspace level only).

## Improvement pass (post Phase 6) - what changed
1. **Verification:** `scripts\verify.ps1` (restore, Release build, both test projects; writes `artifacts\verification.txt`; non-zero exit on failure) and `.github\workflows\ci.yml` (windows-latest, .NET 10). See "Latest verification" below.
2. **ViewModel tests:** new project `tests\CodeForge.Studio.Tests` (net10.0-windows, xUnit) using real view models and real local services in temp folders; only Ollama (`FakeModelClient`) and dialogs (`FakeUi`) are faked. No live Ollama needed. Covers project open/close/restore/selection, preview (secrets blocked), search (debounce, short query, secrets), plan controls (Run/Done/Skip/Run next/progress/restore), `/remember`, edit cards (permission, auto-apply, undo, reject, stale, delete/rename manual approval, restore after restart), onboarding/connection feedback, permission text and the context inspector text.
3. **Run-commands prompt:** `Services\PermissionText.cs` - plain-language warning that build/test files can run project-controlled code, exact folder and command, fixed command list; default button No. Allow-list and folder scoping in `BuildService` are unchanged.
4. **Retrieval:** `Tools\ProjectIndex.cs` - lazy in-memory BM25 index over identifier sub-words with light stemming and a small synonym map (e.g. remove/delete, customer/client), built through `ProjectSession` so secret/excluded files never enter it (caps: 1500 files, 300 KB each, 4 s). `ContextBuilder` merges index hits with the existing keyword scoring; budgets unchanged (12000 total, 3500/file, 4 files). New **Inspect context** expander under each project reply lists exactly which files/line ranges were sent and why. Tests: `RetrievalTests.cs`.
5. **Edit recovery and file operations:** pending/applied proposals are stored in `edits.db` (`SqlitePendingEditStore`) and restored when a conversation is reopened. `EditService.Restore` revalidates the project root, path policy, size and file hash, and that backups sit inside the backup root; changed files show "Out of date" and cannot be applied; records for another project are kept but not shown. Restored proposals never auto-apply. `DELETE: path` and `RENAME: old -> new` proposals use the same path guards, hash check and backup/undo; they are **never** auto-applied, even after folder permission. Tests: `EditRecoveryTests.cs`, `EditCardFlowTests.cs`.
6. **Onboarding:** `OnboardingViewModel`/`OnboardingWindow` (install Ollama link, `ollama pull <model>`, Check connection). Shown on first run only if Ollama or the model is not ready (otherwise silently marked complete via `AppSettings.OnboardingCompleted`); reopen any time with the sidebar **Setup guide** button. `Application\ConnectionDiagnostics.cs` distinguishes Ollama unreachable, no models, selected model missing (with the pull command and installed models), and timeout. Only the local Ollama URL is contacted; the one link opens ollama.com/download in the browser on click.

## Latest verification (most recent run of `scripts\verify.ps1` - separate from the historical results below)
- **Date:** 2026-10-20 11:17 +05:30 (after adding the new-file fallback parser: `FILE:` + fenced code or `<<>>` markers is accepted as a new-file proposal only; existing files are never overwritten by it, and the prompt now tells the model it can create files). **Environment:** Windows 10.0.26300, .NET SDK 10.0.402 (Windows desktop workload available).
- **Command:** `powershell -ExecutionPolicy Bypass -File scripts\verify.ps1` (restore; `dotnet build CodeForgeStudio.sln -c Release`; `dotnet test` for each test project).
- **Result: PASSED** - build 0 warnings / 0 errors; `CodeForge.Tests` 191 passed, 0 failed; `CodeForge.Studio.Tests` 51 passed, 0 failed. No failures in this run.
- **Not verified:** the GitHub Actions workflow has **not** been executed (it has not been pushed). A launch smoke test of the Release exe (isolated data dir, Ollama running with the model) showed the window opening, staying alive, and the guide being skipped and marked complete; the onboarding window, Inspect context expander, restored edit cards, and the delete/rename cards were **not** inspected live.

## UI flows that cannot be tested automatically (and why)
- Real `MessageBox` prompts (folder write permission, run-commands) - modal system dialogs; tests assert the prompt text and call counts through `IUiServices` fakes.
- Folder/file pickers - OS dialogs; tests fake `PickFolder`.
- WPF rendering/layout, bindings, expanders, scrolling, theme - the test project loads view models only, not windows; verified by hand only.
- Streaming from a real Ollama model and answer quality - non-deterministic and needs a running model; `LiveOllamaTests` stays the only integration test.
- Opening ollama.com in the browser from the guide.

- Build/test: only dotnet and Flutter; Flutter path is untested live (no Flutter SDK checked); xUnit/MSBuild-style output parsing only; cancel and timeout unit tests are weak because commands are fixed (live Stop not clicked); output is shown only in the sidebar box; no automatic build after an edit.
- Edits: delete/rename are supported but need an explicit click each time; UTF-8 text only; â‰¤1 MB files; pending proposals are restored after restart only when the file is unchanged; Undo after restart works only if the backup is still intact and the file is unchanged; a 7B model may not follow the SEARCH/REPLACE format exactly (such cases show as problems, not changes). Edits from earlier chat messages can still be applied if the file is unchanged.
- Retrieval is keyword + BM25 with a small built-in synonym map (no embeddings, in-memory index); wording far from the code can still miss files - use Inspect context to see what was sent.
- Outline is regex-based (C# and Dart only), not a real parser.
- Index is in-memory and rebuilt on open/Refresh; no file watcher.
- A link whose intermediate directories are links is not fully resolved; links are skipped during enumeration.
- 7B local model answers can be partly inaccurate; context is limited by `num_ctx`.
- Windows/dialog rendering is not automatically tested (see "UI flows that cannot be tested automatically"). Write and run permissions are per session (in memory).

## Decisions affecting future work
- All file access goes through `ProjectSession`/`ProjectPathGuard`; all writes go only through `EditService` (approval, stale-check, backup). Later phases (e.g., build/test) must not bypass it.
- `Application` namespace clashes with `System.Windows.Application` inside Studio â€” use the fully qualified name.
- Settings changes use `AppSettings.Clone()` so unrelated fields are preserved.
