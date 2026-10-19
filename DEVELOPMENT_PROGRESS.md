# CodeForge Studio — Development Progress

- **Solution:** `C:\My_AI_Project\CodeForgeWPF\CodeForgeStudio.sln`
- **Environment:** Windows 10.0.26300; .NET SDKs 9.0.308 and 10.0.402 (using **net10.0 / net10.0-windows**, runtime 10.0.13); Ollama installed, running, model `qwen2.5-coder:7b` present.

## Current state

| Phase | Name | Status |
|---|---|---|
| 1 | WPF chat connected to Ollama | **Completed** |
| 2 | Project selection, file reading, search, code understanding (read-only) | **Completed** |
| 3 | Proposed code edits, diffs, approval | **Completed** |
| 4 | Build and test runner, error feedback to the AI | **Completed** |
| 5 | Planning mode (plan first, run steps one at a time) | **Completed — Waiting for User Approval of Phase 6** |
| 6 | Memory | Not Started |

**Exact next action:** Ask the user whether to proceed to Phase 6. **User approval is required** — do not start Phase 6 without it.

## Phase 5 — completed features (no new write/shell powers)
- Header checkbox **📋 Plan first**: the next question asks the model for a plan only (`PLAN: title` + 3–8 numbered steps, no code). `PlanParser` extracts it (tolerates markdown, bullets, sub-items; max 12 steps, 300 chars each) and a **plan card** appears under the reply: status icons (○ pending, ▶ running, ✓ done, ⏭ skipped), progress "n/m done", per-step **Run / Done / Skip**, and **▶ Run next step**.
- Running a step sends a focused prompt (that step only, plan state included) through the normal chat and edit flow, so folder write permission and diffs work exactly as in Phase 3. If all its changes apply cleanly the step is auto-marked Done; otherwise it stays Running for manual Done/Skip/Retry.
- Steps never run automatically in bulk; one at a time, not while a reply is generating. Plan mode turns itself off after the plan is made.
- Files: `CodeForge.Agent\PlanParser.cs`, `ChatAgent.cs` (planMode), `Studio\ViewModels\ItemViewModels.cs` (PlanViewModel/PlanStepViewModel), `MainViewModel.cs`, `MainWindow.xaml`, `tests\PlanTests.cs`.
- Live smoke (exe via UI Automation, real `qwen2.5-coder:7b`): Plan first → plan card with 4 steps; Run next step → folder permission dialog → `a.txt` created on disk, step ✓, "1/4 done". Steps 2–4, Skip/Done buttons were not clicked live.
- Limitations: plans are in-memory only (lost on restart; persistence belongs to Phase 6); a 7B model may split steps too finely or ignore the format (then "no plan" message shown).

## Phase 4 — completed features (fixed commands only)
- **Project tab → "🔨 Build & Test"** expander: **Build**, **Test**, **■ Stop**, **Ask AI to fix**, status line, live output box.
- **Commands (allow-list, no shell, no user-typed commands):** Solution/C# project → `dotnet build|test <entry> --nologo -nodeReuse:false`; Flutter → `flutter analyze|test`; plain folder → refused. Working dir = project root; entry file must be inside the root; stdin closed; telemetry off; output capped (200k chars, newest kept); 10-minute timeout; Stop/timeout kills the whole process tree; one run at a time.
- **Permission:** first run per folder asks "Allow CodeForge to run build and test commands in this folder?" (shows the command; default No; in-memory, per folder). Runs are always started by the user's click.
- **Diagnostics:** MSBuild/Roslyn errors and warnings (file, line, code), failed test names, Flutter analyze issues are parsed, deduplicated and capped (50); status shows errors/warnings count.
- **Ask AI to fix:** sends a bounded summary (errors + end of output) wrapped as untrusted `<build_output>` into the chat. The normal Phase 3 edit flow then proposes and (with folder write permission) applies the fix; you press Build again to verify.
- Files: `Application\Build.cs` (`IBuildService`, `BuildResult`, `BuildReport`), `Tools\BuildService.cs`, `Tools\BuildOutputParser.cs`, Studio `ProjectViewModel` build commands, `UiServices.ConfirmRunCommands`.

## Phase 3 — completed features (writes only after explicit approval)
- **Edit proposals:** when a project is open, the system prompt also describes a `FILE:` + `<<<<<<< SEARCH / ======= / >>>>>>> REPLACE` format (empty SEARCH creates a new file). After the reply finishes streaming, `EditParser` extracts blocks (≤20) and `EditService.PrepareAsync` validates them **in memory only**.
- **Validation:** path via `ProjectPathGuard`; refuses excluded folders, secret files, binary types, links, folders; SEARCH must match exactly once (trailing-whitespace tolerant fallback); no-op changes, ambiguous/missing matches, invalid UTF-8, binary, and >1 MB results are reported as problems, never applied.
- **Folder permission (auto-apply):** the first time edits are proposed for a project folder, a dialog asks "Allow CodeForge to create and modify files inside this folder?". **Yes** → this and later edits for that folder apply automatically until the app closes (Undo and backups remain). **No** → cards stay Pending with manual Apply/Reject. Permission is in-memory only, per folder, never persisted. All path/secret/stale checks still run.
- **UI:** each proposed file shows a card in the assistant message: path, +/− counts, colored line-numbered diff (context collapsed), state, and **Apply / Reject / Undo** buttons.
- **Apply:** re-resolves path and policy, requires the file to be unchanged since proposal (SHA-256), same project still open, change prepared by this session (forged/duplicate applies refused), backs the original up to `<data dir>\backups\<timestamp>-<id>\<path>`, writes atomically (temp + move), preserves BOM and CRLF/LF. Project tree refreshes afterwards.
- **Undo:** restores backup (or deletes a created file) only if the file is still exactly as applied; otherwise refused.
- Files: `Application\Edits.cs`, `Tools\DiffEngine.cs`, `EditParser.cs`, `EditService.cs`; Studio `ChangeViewModel` / diff template; `ChatAgent.EditInstructions`; `AddCodeForgeProjectTools(dataDir)` now takes the data dir.

## Phase 1 — completed features
- Dark-themed WPF chat UI, conversation history (SQLite), Settings dialog, streaming Ollama client with typed errors, Stop, connection indicator.
- Settings in `%LOCALAPPDATA%\CodeForgeStudio\settings.json`; DB `conversations.db`; env var `CODEFORGE_DATA_DIR` overrides the data dir.
- Branding: `Assets\logo.png` (sidebar) and `Assets\app.ico` (exe + window icon).

## Phase 2 — completed features (read-only; no writes, no shell, no cloud)
- **Open a project:** folder, `.sln`/`.slnx`, `.csproj`, or `pubspec.yaml` (Flutter detected by pubspec markers). Drive roots and unsupported files are refused. Last project is remembered and silently reopened at startup.
- **Sidebar "Project" tab:** lazy file tree (folders first), name + content search (debounced, cancellable), file preview (first 400 lines, capped), Outline button (C#/Dart declarations via regex), Refresh, Close, **Explain project** button.
- **Chat integration:** each question retrieves a few relevant files by deterministic keyword/filename scoring (≤12,000 chars total, ≤4 files, ±6 lines around hits) plus a short project summary. Injected into the system prompt inside `<project_context>` with an explicit "untrusted data" preamble. The assistant message shows which files were used. Header checkbox "Use project context" toggles it.
- **Context window:** new setting `ContextWindowTokens` (default 8192) sent as Ollama `options.num_ctx`.
- **Project summary:** kind, file counts/types, top-level listing, solution projects, csproj TFM/packages/references, pubspec info, README excerpt.

## Security layer (`CodeForge.Security`, enforced by `CodeForge.Tools`)
- `ProjectPathGuard`: every path resolved against the project root; rejects `..` traversal, absolute outside paths, UNC, alternate data streams (`:`), invalid characters, sibling-prefix folders, and symlinks/junctions whose target leaves the root.
- `ProjectFilePolicy`: hides and refuses `.env`/`.env.*` (not `.example`), `*.pem`, `*.key`, `*.pfx`, `id_rsa*`, `key.properties`, `google-services.json`, etc.; excludes `.git`, `bin`, `obj`, `node_modules`, `.dart_tool`, `.vs`, `.idea`, `.ssh`, `.aws`, … (and `build` for Flutter); binary files blocked by extension and NUL-byte sniffing.
- Limits: index ≤ 20,000 files; reads ≤ 2000 lines / 40k chars; search files ≤ 1 MB, ≤ 200 matches; XML parsed with DTDs prohibited.
- Sensitive files never appear in tree, search, summary, or LLM context.

## Projects (src\ and tests\)
- `CodeForge.Domain`, `CodeForge.Application` (interfaces: `IProjectWorkspace`, `IProjectContextProvider`, records), `CodeForge.Agent` (`ChatAgent` + context injection), `CodeForge.Infrastructure` (Ollama, settings, SQLite), `CodeForge.Security` (new), `CodeForge.Tools` (new: `ProjectWorkspace`, `ProjectLocator`, `ProjectInspector`, `ContextBuilder`, `CodeOutliner`), `CodeForge.Studio` (WPF; new `ProjectViewModel`), `CodeForge.Tests`.

## Build and tests (actually executed)
- `dotnet build CodeForgeStudio.sln` → **Build succeeded, 0 warnings, 0 errors**.
- `dotnet test CodeForgeStudio.sln` → **130 passed, 0 failed** (121 + 9 new plan tests: parser variants, caps, step prompt tags, plan-mode prompt).
- Earlier: 121 passed after Phase 4 (110 + 11: output parser, report bounding, plan allow-list, real successful and failing `dotnet build`, no-project, cancel, timeout).
- Phase 4 live smoke (exe via UI Automation, temp project with a missing semicolon, real model): Build → permission dialog → "✗ Build failed — 1 errors"; **Ask AI to fix** → model proposed the fix, write-permission dialog, applied; Build again → "✓ Build succeeded — 0 errors". A real bug (Build button stayed disabled after opening a project) was found by this smoke test and fixed.
- Phase 3 live smoke (exe via UI Automation, temp project and data dir, real `qwen2.5-coder:7b`): asked to change `Hello` to return "World"; a diff card appeared (+1 −1, Pending); **Apply** changed the file and created a backup; **Undo** restored the original text. Reject is covered at ViewModel level only (not clicked live).
- Two test failures found and fixed during development: drive-root detection and expression-bodied C# methods missing from the outline.
- App smoke test (exe driven through UI Automation, isolated data dir): last project auto-reopened ("CodeForgeWPF (Solution)", 48 files, tree shown); **Explain project** sent a question; Ollama streamed a coherent answer about CodeForge Studio; the message showed "Project context: MainViewModel.cs, ProjectViewModel.cs, DEVELOPMENT_PROGRESS.md, App.xaml.cs"; header checkbox shown.
- Not manually exercised in the live UI: Open folder/file dialogs, search box, Outline button (covered by unit tests at the workspace level only).

## Known limitations
- Build/test: only dotnet and Flutter; Flutter path is untested live (no Flutter SDK checked); xUnit/MSBuild-style output parsing only; cancel and timeout unit tests are weak because commands are fixed (live Stop not clicked); output is shown only in the sidebar box; no automatic build after an edit.
- Edits: no delete/rename; UTF-8 text only; ≤1 MB files; pending proposals are not persisted across restarts; Undo works only in the same session; a 7B model may not follow the SEARCH/REPLACE format exactly (such cases show as problems, not changes). Edits from earlier chat messages can still be applied if the file is unchanged.
- Retrieval is keyword-based (no embeddings/persistent index — planned for Phase 6); a question using different words than the code may miss the relevant file.
- Outline is regex-based (C# and Dart only), not a real parser.
- Index is in-memory and rebuilt on open/Refresh; no file watcher.
- A link whose intermediate directories are links is not fully resolved; links are skipped during enumeration.
- 7B local model answers can be partly inaccurate; context is limited by `num_ctx`.
- No ViewModel/UI automated tests.

## Decisions affecting future work
- All file access goes through `ProjectSession`/`ProjectPathGuard`; all writes go only through `EditService` (approval, stale-check, backup). Later phases (e.g., build/test) must not bypass it.
- `Application` namespace clashes with `System.Windows.Application` inside Studio — use the fully qualified name.
- Settings changes use `AppSettings.Clone()` so unrelated fields are preserved.
