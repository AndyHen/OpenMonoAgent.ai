# Classic Mode Markdown Rendering

**Date:** 2026-05-13  
**Status:** Approved

## Problem

The `--classic` terminal mode (`TerminalRenderer`) streams the assistant's response as raw text, showing all markdown symbols (`` ``` ``, `**`, `*`, `#`, etc.) instead of rendering them as formatted output. The TUI mode already has `AnsiMarkdown.Render()` for this — classic mode just isn't using it.

## Goal

Render markdown in classic mode so users see formatted output: styled headings, bullet points, code blocks with borders, bold/italic text.

## Approach

**Stream raw → re-render formatted at end.**

Stream text as-is (users see output live), buffer everything in a `StringBuilder`, then on `EndAssistantResponse` restore the cursor to just before the streamed text and re-render the full buffer with `AnsiMarkdown.Render()`.

Cursor positioning uses ANSI save/restore (`\x1b[s` / `\x1b[u`) rather than line-counting, because line-counting breaks when terminal wrapping occurs.

## Changes to `TerminalRenderer`

### New field

```csharp
private readonly StringBuilder _streamBuffer = new();
```

### `StartAssistantResponse`

After writing the `◆ Assistant` header lines, emit `\x1b[s` to save the cursor position. This marks the top of the streamed text region.

### `StreamText`

Unchanged output behavior — write characters with the existing 4-space indent logic. Additionally append each character to `_streamBuffer`.

### `EndAssistantResponse`

1. Emit `\x1b[u\x1b[J` — restore saved cursor position and clear from there to end of screen (erases all streamed raw text).
2. Call `AnsiMarkdown.Render(_streamBuffer.ToString(), Console.WindowWidth - 6)`.
3. Write each rendered line with `"    "` (4-space) prefix using `Console.WriteLine`.
4. Write the existing footer (separator + chunk/time stats).
5. Clear `_streamBuffer`.

### `WriteMarkdown`

Replace the current `_console.WriteLine(markdown)` with:
1. `AnsiMarkdown.Render(markdown, Console.WindowWidth - 6)`
2. Write each rendered line with `"    "` prefix.

This fixes markdown in `/help` output, playbook output, and tool output.

## Scope

- `src/OpenMono.Cli/Rendering/TerminalRenderer.cs` — only file changed.
- No changes to `AnsiMarkdown.cs`, `AnsiTuiRenderer.cs`, or any other file.

## Testing

Run the app with `--classic`, send a prompt that returns markdown (code blocks, bold text, bullet lists). Verify:
- Symbols are not shown raw during streaming (raw text visible briefly, then replaced).
- Final output shows styled code blocks, bold, bullets.
- `/help` command output is formatted.
- Footer (chunk count / timing) still appears.
