# Changelog

## 0.4.2 - easier-to-read answers, a panel that fits

- **Answers are structured.** The Claude panel now draws headings, bullet and numbered lists,
  bold and `code`, and markdown tables as real tables. Claude is told to lead with the answer, keep
  paragraphs short, and put lists of tags, modules, rungs or findings in tables.
- **Rungs are drawn as ladder by default** wherever they appear - in a code block, on a line of
  their own, after a "Rung 3:" label, or quoted inside a sentence. A comment above a rung becomes
  its caption (where it is and what it does); its neutral text is one click away to copy. Claude
  is told to show rungs rather than describe them, old and new side by side for a change.
- **The panel no longer runs off the right edge.** It shrinks to fit the window (the navigator
  narrows before the tabs do), and wide rungs and tables scroll sideways inside the chat instead
  of being cut off. A Copy button under each answer copies it as text.

## 0.4.1 - Claude on your Claude plan

- **The Claude panel runs on your Claude plan.** It now drives your own Claude Code (signed in
  with your Claude subscription) in the background, so no API key or credits are needed. Claude's
  tools still run in the open window - it reads the project on screen and its drafts land in the
  Develop tab as it works. The panel finds Claude Code, says if it needs installing or signing in,
  and opens the sign-in. An API key is still an option in the panel's list.

## 0.4.0 - edit in place with a revision history, the system view, more findings

- **Edit a routine in place.** Edit on the Logic tab (or double-click a rung) opens the routine in
  the Develop tab; the Logic tab then draws your version with every rung marked - green added,
  amber changed, faded removed - and the project's text on hover. Show original flips back;
  Discard edits drops the draft. Findings and cross-references still land on the right rung.
- **Revision history.** Every step of the work is a revision: adding, deleting, generating, each
  change Claude makes, a revert - and typing, in bursts per draft. History (Develop tab) lists them
  newest first, shows what each one changed rung by rung, what has changed since, or what the
  drafts change in the open project, and goes back to any revision. A revert is itself a revision,
  so it can be undone. The history is saved in the .lcdev file, also by `--mcp`.
- **Changes vs project**: the whole set of drafts against the open project before exporting.
- **System tab**: the controller drawn as a system - controller, bridges, devices - with I/O
  connections, produced/consumed tags and messages as coloured links. Add the other controllers'
  exports and they are joined into one plant, with six new checks only the join can make
  (LC-PLT-001..006: consumed tag nobody produces, RPI outside the producer's range, type mismatch,
  too many consumers, a message to a tag that is not there, two owners of one device).
- **Findings open their rung**: every rung or routine in the Where column is a link; double-click a
  finding or a cross-reference row to open the Logic tab on it.
- **Five new findings**: RPI slower than the periodic task reading it (LC-HW-006), a GSV status
  nobody reads (LC-COM-003), a message whose .DN/.ER nothing checks (LC-MSG-003), unused data types
  and Add-Ons (LC-LOG-009), a tag written from two tasks (LC-LOG-010).
- **Generic Ethernet modules** as drafts: new, or Edit module... on the Hardware tab; checked
  (parent port, address clashes, sizes) and written into a project copy.
- Warnings are errors in shipping code.

## 0.3.0 - Claude in LogicControl

- Claude assistant panel (Ctrl+Shift+A, or the Claude button): ask about the open project and get
  logic drafted. Claude reads routines, tags, cross-references, hardware, communications and
  findings through LogicControl's tools, and writes data types, tags, routines and AOIs into the
  Develop tab, where the checker reviews them. Rungs in answers are drawn as ladder.
- Answers stream; every tool call is listed and can be expanded; Stop, New chat, model choice
  (Sonnet, Opus, Haiku), token count.
- Uses an Anthropic API key, stored encrypted for the Windows user (or ANTHROPIC_API_KEY).
- "Ask Claude" on the Logic tab explains the routine on screen.
- `LogicControl.exe --mcp` serves the same tools to Claude in VS Code or the Claude desktop app,
  on your Claude plan with no API key; drafts land in a .lcdev file the app opens.

## 0.2.0 - ladder, writing logic, and an installer

- Logic tab draws ladder: rails, contacts, coils, instruction boxes with named operands, nested
  branches, outputs against the right rail. AOI calls show their parameter names. Click an
  operand to open its tag; Ctrl+L switches to the neutral text.
- New Develop tab: draft UDTs, AOIs, programs, ladder routines and tags; paste member and tag lists
  from Excel; every rung drawn and checked as it is typed.
- Templates: motor starter (rungs or AOI), two-position valve, latched alarm, analog input,
  heartbeat - for a list or range of instances.
- Edit a copy of any routine or AOI in the open project.
- Export Studio 5000 import files (data type, AOI, program, routine) with their context, or write
  the drafts into a copy of a whole-controller export.
- Development sets save as .lcdev.
- Packaging as NetControl: icon, per-user Inno Setup installer, GitHub release workflow, in-app
  update check and self-update.

## 0.1.0 - first cut (not released)

- L5X reader: controller, modules, ports, connections, UDTs, AOIs, tags (alias, produced,
  consumed, MESSAGE), programs, routines (ladder, ST; FBD/SFC counted), tasks.
- Analysis: cross-reference with Logix scoping, I/O tree, communications map, 15 findings rules.
- WPF shell in the family design system (11 themes, 6 accents, 3 densities; default Violet).
