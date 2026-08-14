# CLAUDE.md

## Project Guidelines

These instructions apply to the entire repository.

Lumisense is a Windows desktop audio player. Keep the existing architecture, behavior, UI style, and project conventions unless the user explicitly asks to change them.

---

# 1. General Development Rules

- Understand the existing code before modifying it.
- Make the smallest reasonable change that solves the requested problem.
- Do not refactor unrelated code.
- Do not redesign existing architecture unless explicitly requested or clearly required.
- Do not rename classes, methods, properties, fields, files, or namespaces without a reason.
- Do not introduce unnecessary abstractions, wrappers, helpers, or dependencies.
- Preserve existing behavior unless the requested change requires otherwise.
- Do not remove existing functionality just because it appears unused without confirming that it is safe to remove.
- Avoid speculative improvements that were not requested.
- Prefer simple, readable solutions over clever solutions.
- Follow the existing coding style of the project.
- Keep changes focused on the requested task.

If a task can be solved without changing unrelated code, do not change unrelated code.

---

# 2. Comments

Comments must be concise, meaningful, and useful.

## Main Rule

Comments should explain **WHY**, not **WHAT**.

Do not write a comment when the code, method name, variable name, or surrounding context already makes the behavior obvious.

### Good

```csharp
// Keep the previous value when loading a configuration created by an older version.
```

### Bad

```csharp
// Check if the setting exists.
if (settings.ContainsKey(key))
{
    // Get the setting.
    var value = settings[key];
}
```

The second example simply describes what the code already says.

---

## 2.1 When Comments Are Appropriate

Add a comment when it explains something that would otherwise be difficult to understand, such as:

- why a workaround is required;
- a Windows, WPF, .NET, or third-party library limitation;
- unusual API behavior;
- threading requirements;
- Dispatcher usage;
- race conditions;
- backwards compatibility;
- an important ordering requirement;
- a non-obvious architectural decision;
- a known platform-specific issue;
- a known limitation;
- a subtle side effect;
- why apparently unnecessary code must remain.

Example:

```csharp
// Dispatcher is required here because the callback may arrive on a background thread.
```

---

## 2.2 When Comments Are NOT Appropriate

Do not add comments for:

- simple assignments;
- simple `if`/`else` statements;
- null checks;
- simple loops;
- obvious method calls;
- property declarations;
- getters/setters;
- standard event handlers;
- simple validation;
- obvious UI operations;
- straightforward file operations;
- obvious conversions;
- code that is already self-explanatory.

Do not comment every step of a method.

---

## 2.3 Comment Length

Prefer one short sentence.

Avoid large explanatory paragraphs inside source files.

Do not write comments that explain an entire method line by line.

Bad:

```csharp
// First we check whether the player is currently playing.
// Then we get the current track.
// After that we check whether the track exists.
// If it exists, we update the UI.
// Finally, we refresh the controls.
```

If the code is understandable without these comments, remove them.

Prefer a single comment only when necessary:

```csharp
// Update the UI on the Dispatcher because playback callbacks run off the UI thread.
```

---

## 2.4 Existing Comments

When modifying existing code:

- check whether nearby comments are still accurate;
- update outdated comments;
- remove comments that no longer provide useful information;
- do not preserve a comment merely because it already exists;
- do not add a new comment just because code was changed.

Never mechanically add comments to every modified block.

---

## 2.5 XML Documentation

Do not add `///` documentation to every class, method, or property.

Use XML documentation only when it provides meaningful value, primarily for:

- public APIs;
- reusable components;
- complex public methods;
- non-obvious contracts.

Avoid XML documentation that merely repeats the name of the member.

---

## 2.6 Comment Style

Keep comments:

- short;
- precise;
- technically accurate;
- neutral;
- consistent with the existing project.

Do not write comments as essays.

Do not use comments to compensate for unclear naming when better naming would solve the problem.

Prefer clear code over explanatory comments.

---

# 3. Code Quality

Prefer:

- clear naming;
- small focused methods;
- existing project patterns;
- minimal dependencies;
- straightforward control flow;
- predictable behavior.

Avoid:

- unnecessary abstractions;
- premature optimization;
- duplicated logic;
- dead code;
- unused variables;
- unnecessary comments;
- unnecessary logging;
- unnecessary exception handling;
- overly complex solutions.

Do not introduce a new library when the existing framework or project code can reasonably solve the problem.

---

# 4. WPF and UI

Lumisense is a WPF desktop application.

When working with WPF:

- preserve the existing UI architecture;
- follow the existing XAML styling conventions;
- avoid introducing a different UI pattern without a reason;
- keep UI updates on the UI thread;
- use `Dispatcher` where required;
- do not block the UI thread with long-running operations;
- preserve existing animations and visual behavior unless explicitly asked to change them;
- avoid unnecessary changes to unrelated XAML.

When changing UI behavior, check both XAML and the corresponding code-behind or view-model logic where applicable.

---

# 5. Async and Threading

Be careful with:

- `async`/`await`;
- background tasks;
- Dispatcher usage;
- cancellation;
- fire-and-forget operations;
- application shutdown;
- playback callbacks;
- concurrent state changes.

Do not introduce `.Result` or `.Wait()` on operations that may execute asynchronously on the UI thread unless there is a specific reason.

Do not silently convert asynchronous code into synchronous code.

If a threading decision is non-obvious, a short comment explaining WHY is appropriate.

---

# 6. Error Handling

Do not add broad exception handling without a reason.

Avoid:

```csharp
try
{
    ...
}
catch
{
}
```

Never silently swallow exceptions unless there is a documented and intentional reason.

When an exception is intentionally ignored, explain the reason briefly.

Prefer handling failures at the appropriate boundary rather than adding `try/catch` blocks everywhere.

---

# 7. Configuration and Compatibility

Be careful when modifying:

- application settings;
- configuration files;
- saved user preferences;
- stored playback state;
- update-related files;
- installer-related files;
- data formats.

Preserve backwards compatibility when existing users may have data created by older versions.

If compatibility logic is non-obvious, document the reason with a short comment.

---

# 8. Updates and Installer

When modifying update or installer functionality:

- preserve the existing update flow unless explicitly changing it;
- do not remove safety checks without understanding their purpose;
- consider application shutdown and restart behavior;
- consider partially completed updates;
- preserve compatibility with existing installations;
- keep update-related code isolated from unrelated functionality.

Do not modify installer configuration unless the task requires it.

---

# 9. Git

## Commit Language

All commit messages must be written in **English**.

Do not mix Russian and English in commit messages.

---

## Commit Format

Use:

```text
type: short description
```

Allowed types:

- `feat` — new functionality
- `fix` — bug fix
- `refactor` — code restructuring without behavior change
- `perf` — performance improvement
- `ui` — UI or visual changes
- `docs` — documentation changes
- `build` — build, packaging, or installer changes
- `test` — tests
- `chore` — maintenance

Examples:

```text
feat: add playlist sorting
fix: prevent duplicate playback
ui: improve mini player layout
refactor: simplify update handling
perf: reduce library loading time
build: update installer configuration
docs: update README
```

---

## Commit Message Style

Commit messages must be short and specific.

Describe the actual change, not the entire implementation process.

Good:

```text
feat: add playlist sorting
```

Bad:

```text
feat: implement a comprehensive playlist sorting system with multiple sorting modes and improved state management
```

Good:

```text
fix: prevent stale track playback
```

Bad:

```text
fix: fixed the issue where sometimes the player could incorrectly continue playing the previous track after changing the playlist
```

Do not use vague messages such as:

```text
update
changes
fixes
work
stuff
minor changes
```

---

## Commit Scope

Prefer one logical change per commit.

Do not combine unrelated changes into one commit.

Several modified files may belong to the same commit if they are part of the same logical change.

Do not create a commit automatically unless the user explicitly asks for a commit.

Before creating a commit, inspect the actual diff and write the commit message based on what was changed.

---

# 10. Git Changes

Do not modify files unrelated to the requested task.

Do not automatically reformat the entire project.

Do not automatically reorder unrelated code.

Do not change line endings or formatting across unrelated files.

Avoid generating large diffs for small changes.

A small task should normally produce a small, focused diff.

---

# 11. Before Finishing a Task

Before considering a task complete:

1. Review the changes.
2. Check for unintended modifications.
3. Remove unnecessary comments.
4. Check that existing comments still match the code.
5. Check for obvious code duplication introduced by the change.
6. Build the project when practical.
7. Fix compilation errors caused by the change.
8. Do not make unrelated improvements merely because they were noticed.

---

# 12. Final Response

When reporting completed work:

- be concise;
- summarize what changed;
- mention important files when useful;
- mention build/test status;
- mention any remaining issue that could not be verified.

Do not provide a long essay about simple changes.

For a straightforward task, a short summary is preferred.

---

# 13. Priority

When instructions conflict, follow this priority:

1. Explicit user request.
2. Safety and platform requirements.
3. Existing project behavior and architecture.
4. This `CLAUDE.md`.
5. General coding conventions.

Do not interpret these guidelines as a reason to avoid making requested changes.

The goal is simple, maintainable, production-quality code with focused changes, useful comments, and concise Git history.