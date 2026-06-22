# Session Checkpoint — Cursor Gating, Sandbox & Usage (June 16, 2026)

**How to use this file:** distribute the three sections below, then delete this staging file.
- **Section 1** → append to `M:\Projects\cursor.md` as a new section (§11).
- **Section 2** → paste into Cursor **Customize → User Rules** (auto-loads every session; highest priority).
- **Section 3** → outstanding TODOs for the next chat.

The point of this file: so the next agent does NOT have to re-derive any of tonight's friction.

---

## Section 1 — Append to `M:\Projects\cursor.md` (§11: Cursor Sandbox, Permissions & Usage)

### Write-sandbox reality (how Cursor actually fences the agent)
- The agent's **write scope is set at session launch** and fenced to the opened project/workspace root — **NOT** the whole drive. Reads are broad; writes are fenced.
- "Opened folder" ≠ "agent write scope." Rule of thumb: **I read by the folder, I write by the project, and the fence is frozen at launch.**
- **Two permission layers:** (1) Dave's authorization (chat approval) and (2) OS/sandbox write permission. Chat approval **cannot move the sandbox fence mid-session** — by design, so a confused/rogue agent can't write anywhere just by claiming it has permission.
- `move_agent_to_root` changes the conversation root but does **NOT** lift the live write fence. A **fresh agent** is required for a new fence to take effect.
- Cursor is **stricter** than other AI IDEs (which hand the agent the whole opened workspace). The UI does not surface the write fence anywhere — verify empirically.

### Permissions setup (applied June 16, 2026)
- `C:\Users\Dave\.cursor\sandbox.json`:
  ```json
  { "type": "workspace_readwrite", "additionalReadwritePaths": ["M:\\"] }
  ```
  → grants the agent read/write across all of M: while keeping the sandbox backstop intact. It also provides write access to the desktop. "C:/Users/Dave/Desktop"
- **External-File Protection: OFF** (Settings → Cursor Settings → Agents → Run Mode → Protection settings).
- Both take effect on a **fresh agent/restart**. Windows/WSL2 mapping of `M:\` inside the sandbox is **unverified** → **FIRST MOVE in every new session: test-write to `M:\` root and `M:\Code_Archive\`, then delete, to confirm the fence is wide before trusting it.**

### Sandbox philosophy (decision)
- Keep the sandbox (mechanical backstop); just widen it to M:. **Do NOT use `insecure_none`.**
- Why: **rules are a *cooperative* control** — they can fail via context loss/compaction, misinterpretation, prompt-injection, or plain model error producing confident-but-wrong output. **The sandbox is a *mechanical* control** that catches those regardless of what the agent thinks. Git remotes = *recovery*, not *prevention*; loose M: files (V-Prompts vault, Code_Archive, governance) aren't all backed up, so the mechanical layer still earns its keep.

### Golden-standard usage workflow
1. Open the project repo via **File → Open Folder** (NOT the Repositories "+" sidebar, which can create AWS cloud clones; NOT the drive root unless you intend drive-wide work).
2. Confirm **"Local"** (bottom bar) for proprietary work — cloud-icon agents run on remote clones.
3. Durable rules only auto-load if placed in **Customize → User Rules** — keep governance there.
4. **Read-only git check → confirm/create branch BEFORE edits.** Never develop on main/master.
5. **Plan → you approve → "go."** Explicit OK for destructive/git actions, every time.
6. **One task = one chat;** watch the context %; checkpoint before it fills.
7. MCP servers (Notion, Stripe, Vercel, GitLab, PostHog, Azure, etc.) are wired up — ask me to use them.

### Superpowers plugin (auto-loads; decision)
- `superpowers` is a third-party plugin whose **session-start hook auto-injects** "use skills, act before asking" (an autonomy push). Your governance overrides it (plugin's own priority order: user instructions > superpowers > system default).
- **DECISION:** adopt ~95% of the methodology (brainstorm → plan → TDD → review → verify) but **stay human-in-the-loop**. Prefer `executing-plans` (batch + human checkpoints) over `subagent-driven-development` (autonomous). Keep approval gates; no long autonomous runs without explicit OK.

---

## Section 2 — Paste into Customize → User Rules (condensed, auto-loads every session)

- Talk first; act only when asked. No proactive file reads, searches, or filesystem exploration without a request.
- Write only to M: drive. Read everywhere except B: (BitLocker). Never write to `C:\The-Ossuary`.
- Destructive actions (delete, move, rename, edit/append existing files) AND commit/push/merge require explicit approval every time. No implied permission — ever.
- Project dev: read-only git check, then confirm/create a branch (`cursor/<type>/<short-desc>`) BEFORE any edit. Never work on main/master.
- Adopt the superpowers methodology but human-in-the-loop: brainstorm → plan → I approve → "go". Prefer batch execution with checkpoints over autonomous subagents. Stop for approval at each stage.
- First move in any new session that needs M: writes: test-write to `M:\` root, confirm success, then proceed. The write sandbox is fenced at launch and the UI won't show it.
- Before substantive dev work, read: `M:\Projects\cursor.md`, `C:\Users\Dave\AGENTS.md`, and `M:\Revenant_Progress_Report.md` → "Why we're here (founder narrative)".

---

## Section 3 — Outstanding TODOs (next session)

1. **Founder narrative still needs merging** (if not already done by drag-drop): insert the `### Why we're here (founder narrative)` block into `M:\Revenant_Progress_Report.md`, after the "Founder context (Dave Fisher)" bullets, before "What Revenant Systems LLC is (definitive)". Source block: `C:\Users\Dave\Desktop\Orientation-Founder-Narrative-Append.md`. (The `cursor.md` §8 compaction bullet it references is ALREADY present — skip that part.)
2. **Verify the permission setup actually works:** with the new `sandbox.json` + External-File Protection off, test-write to `M:\` root and `M:\Code_Archive\`. If denied, the `M:\` path format in `sandbox.json` (WSL2 mapping) is the prime suspect.
3. **Recover any Valerie cloud work (decide FIRST):** a drafted PR/branch may exist on the OLD public GitHub repo from an accidental cloud-clone. If Dave wants it, grab the repo URL + branch name and `git fetch` it BEFORE `.git` is deleted. If local is the source of truth, skip. Deleting `.git` drops the remote link, so this decision comes before the migration.

4. **Repo migration — TWO SEPARATE PRIVATE REPOS. Never a single delete-and-move-on.**
   - **Current state:** ONE repo on GitHub that is PUBLIC and has the "V" persona all through it; local `M:\Projects\Valerie` still contains V files. Dave will NOT pre-delete `.git` himself — the agent does it in step (b).
   - **Goal:** V preserved privately forever (V is explicitly NOT the product), Valerie clean and on its own separate repo.
   - **a. V backup FIRST — while V is still in the folder:** copy the entire `Valerie` folder (`.git` and all) to a separate location Dave chooses (he is naming the repo `Valerie`; it is NOT inside `M:\V-Prompts\`, which is a separate prompt knowledge-base). Create a NEW **private** GitHub repo named **`Valerie`** — but CHECK FIRST for an existing old `Valerie` repo on GitHub from a prior attempt; avoid name collision / do not overwrite it. Push the copy. V is now backed up, private, full history, nothing to re-add later.
   - **b. Clean Valerie SECOND:** in the original folder — delete `.git` (`Remove-Item -Recurse -Force .git`), move ALL V files OUT, add a `.gitignore`, `git init`, commit the clean version. Create a SECOND NEW **private** GitHub repo for clean Valerie. Push.
   - **c. Delete the OLD PUBLIC repo LAST** — only after both new private repos exist and are verified. This is the ONLY deletion, and it only removes the exposed public copy.

5. **Then start actual Valerie development:** per `agent.md` next steps, the top item is the full voice I/O layer (STT + streaming sentence-by-sentence TTS), pulling proven patterns from the Revenant-Echo project.
