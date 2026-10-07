# Changelog

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
