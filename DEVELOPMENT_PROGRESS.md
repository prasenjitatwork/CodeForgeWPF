# CodeForge Studio — Development Progress

- **Solution:** `C:\My_AI_Project\CodeForgeWPF\CodeForgeStudio.sln`
- **Environment:** Windows 10.0.26300; .NET SDKs 9.0.308 and 10.0.402 (using **net10.0 / net10.0-windows**, runtime 10.0.13); Ollama installed, running, model `qwen2.5-coder:7b` present.

## Current state

| Phase | Name | Status |
|---|---|---|
| 1 | WPF chat connected to Ollama | **Completed** |
| 2 | Project selection, file reading, search, code understanding (read-only) | **Completed — Waiting for User Approval of Phase 3** |
| 3–6 | Edits/diffs/approval, build/test, planning, memory | Not Started |

**Exact next action:** Ask the user whether to proceed to Phase 3 (Proposed Code Edits, Diffs, and Approval). **User approval is required** — do not start Phase 3 without it.

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
- `dotnet test CodeForgeStudio.sln` → **77 passed, 0 failed** (21 from Phase 1 + 56 new: path guard incl. real junction escape, file policy, locator, workspace read/search/summary/outline/context with secrets never leaking, agent prompt injection).
- Two test failures found and fixed during development: drive-root detection and expression-bodied C# methods missing from the outline.
- App smoke test (exe driven through UI Automation, isolated data dir): last project auto-reopened ("CodeForgeWPF (Solution)", 48 files, tree shown); **Explain project** sent a question; Ollama streamed a coherent answer about CodeForge Studio; the message showed "Project context: MainViewModel.cs, ProjectViewModel.cs, DEVELOPMENT_PROGRESS.md, App.xaml.cs"; header checkbox shown.
- Not manually exercised in the live UI: Open folder/file dialogs, search box, Outline button (covered by unit tests at the workspace level only).

## Known limitations
- Retrieval is keyword-based (no embeddings/persistent index — planned for Phase 6); a question using different words than the code may miss the relevant file.
- Outline is regex-based (C# and Dart only), not a real parser.
- Index is in-memory and rebuilt on open/Refresh; no file watcher.
- A link whose intermediate directories are links is not fully resolved; links are skipped during enumeration.
- 7B local model answers can be partly inaccurate; context is limited by `num_ctx`.
- No ViewModel/UI automated tests.

## Decisions affecting future work
- All file access goes through `ProjectSession`/`ProjectPathGuard`; Phase 3 writes must reuse the guard and add explicit user approval and diff preview.
- `Application` namespace clashes with `System.Windows.Application` inside Studio — use the fully qualified name.
- Settings changes use `AppSettings.Clone()` so unrelated fields are preserved.
