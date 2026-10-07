# LogicControl plan

## Goal

Point it at a Rockwell project and understand the system in minutes: what hardware is configured,
how this controller talks to every device and every other controller, what the logic does with
that data, and what looks wrong. Later: write logic back as L5X for import into Studio 5000.

## What is realistic (and what is not)

| | |
|---|---|
| Read L5X exports (whole controller, program, routine, AOI) | Yes - done |
| Read .ACD directly | No. Proprietary binary. Export to L5X, or automate that with Rockwell's Logix Designer SDK (needs Studio 5000 installed and licensed) |
| Hardware tree, comms map, cross-reference, findings | Yes - done (first cut) |
| Draw ladder with rails and branches | Yes - done (LadderParser + LadderLayout + LadderRungView) |
| Function block / SFC | Not planned for now (not wanted) |
| Compare live network to the project | Not planned for now (not wanted) |
| System view: drawn topology, several controllers joined | Yes - done (0.4.0, PlantModel + System tab) |
| Edit in place with a revision history, diff and revert | Yes - done (0.4.0) |
| Generate rungs / UDTs / AOIs / programs as L5X for import | Yes - done (Authoring/), Studio 5000 validates on import |
| Write drafts into a copy of a whole-project L5X | Yes - done (ProjectMerger); open the copy in Studio 5000 |
| Convert structured text to ladder | Not planned - little ST in use; revisit if that changes |
| Modules (Generic Ethernet) as L5X | Done for Generic Ethernet (0.4.0, project copy only); other catalog numbers need module definitions |
| Download to a controller or online edit | No - out of scope on purpose |

## Steps

Done in 0.2.0: step 2's drawing (ladder view, operand click-through), step 6's first half
(drafts, checks, templates, import files, project merge) and step 7 (packaging). Still open from
those: click a finding's location to jump to the rung; module templates; LLM-assisted rung
drafting.

Done in 0.4.0: finding-to-rung links, step B (edit in place, plus a full revision history with
diff and revert), step 3 (system view), step 4 (the rules below, plus LC-PLT-001..006 across
controllers), Generic Ethernet module drafts, TreatWarningsAsErrors. Step 5 (live compare) and
function block / SFC are dropped for now.

**Next, in order:**

A. **First Windows build and a real import.** Push to GitHub; the `verify` run is the first time
   the WPF project compiles. Then import one file of each kind (data type, AOI, program, routine)
   into a scratch project in Studio 5000 and open one merged project copy - with a Generic
   Ethernet module in it, to confirm ModuleFormats (CommMethod numbers, sizes in bytes). Every
   complaint the importer makes is a fixture and a test.
B. **Editing in place**: change a rung in the open project rather than in a copy routine, with a
   diff of what changed before export.
C. Steps 3-5 below (system view, more rules, live compare).

0. **First Windows build.** `dotnet build`, fix XAML/code-behind, run on a real plant export,
   compare every number on the Overview with Studio 5000. Turn TreatWarningsAsErrors on.
   Add view-model tests (move the test project to net10.0-windows when it references the App).
1. **Real exports as fixtures.** Collect 3-5 sanitized plant L5X files (one big, one with
   GuardLogix safety, one with lots of MSG/produced-consumed). Every surprise becomes a test.
2. **Ladder view.** Parse branches into a tree; draw rails, contacts and coils. Click an operand to
   jump to its tag; click a finding's location to jump to the rung.
3. **System view.** A drawn topology: controller -> bridges -> devices, with link types coloured
   as in the comms grid. Open several L5X files at once and join them on produced/consumed tags
   and MSG paths - the plant as a whole.
4. **More rules.** RPI vs task period, consumed RPI vs producer limits, unused AOIs/UDTs, routines
   writing the same tag from two tasks, MSG without done/error handling, GSV status not acted on.
5. **Live compare.** Use NetControl.Core's scanner to list what answers on the I/O network and
   diff it against the I/O tree (missing, extra, wrong product code, wrong firmware).
6. **Writing.** L5X generation for UDTs, AOIs, routines and Generic Ethernet modules from
   templates; then LLM-assisted rung drafting that always goes through import and review.
7. **Packaging.** Same as NetControl: Inno Setup, per-user, GitHub releases, update check.
