# CLAUDE.md

Guidance for working in this repository.

## What this is

**LogicControl** - a Windows desktop tool that reads a Rockwell Studio 5000 **L5X** export and
shows the controller as a system: the I/O tree, every way it communicates (owned I/O, produced
and consumed tags, MSG instructions, GSV status reads), tags with cross-references, the logic
drawn as ladder, and a list of findings. The Develop tab writes logic: UDTs, AOIs, programs,
routines and tags as drafts, checked as they are typed, exported as L5X import files or merged
into a copy of the open export. It never connects to a controller - everything it writes goes
through Studio 5000.

Sibling of NetControl (same stack, same layout, same appearance system) and part of the family
with Redline PDF, the DWG viewer and File Manager.

## Stack

- .NET 10, C#, `net10.0` for the engine, `net10.0-windows` + WPF for the app and the tests.
- **No third-party packages in src/.** The engine reads L5X with System.Xml.Linq. The app's MVVM
  plumbing is `Composition/ObservableObject.cs` and `Composition/RelayCommand.cs` instead of
  CommunityToolkit.Mvvm, so the view models compile and run anywhere (see the note in
  ObservableObject).
- xUnit for tests.

## Commands

```
dotnet build LogicControl.slnx
dotnet test tests/LogicControl.Tests
dotnet run --project src/LogicControl.App -- "C:\path\to\export.L5X"
```

## Layout

```
src/LogicControl.Core/        engine - MUST NOT reference any UI assembly
    Model/                    plain records for what an export contains
    L5x/L5xReader.cs          L5X -> PlcProject. Tolerant: every attribute optional
    Logic/                    rung parsing, operand -> tag names, instruction tables, rung colouring,
                              LadderParser (branch tree) and LadderLayout (geometry for the view)
    Authoring/                drafts, DraftChecker, L5xWriter (import files), ProjectMerger
                              (write into a whole-project copy), LogicTemplates, DeclarationText
    Analysis/                 cross-reference, hardware tree, comms map, findings, ProjectAnalysis
    Assistant/                LogicTools (the AI's tool table), ClaudeClient (Messages API, SSE),
                              ConversationSession (tool-use loop), AssistantPrompt, McpServer
    Diagnostics/              rolling trace log, ported from NetControl
src/LogicControl.App/         WPF shell
    Appearance/               NetControl's design system + this app's additions (bottom of Controls.xaml)
    Composition/              paths, MVVM plumbing
    Diagnostics/              build info, settings.json, update check/download/apply (from NetControl)
    ViewModels/               WPF-free; everything the window shows. Develop/ is the Develop tab,
                              Assistant/ the Claude panel
    Views/                    MainWindow, AppearanceWindow, UpdateWindow, LadderRungView
tests/LogicControl.Tests/     xUnit, net10.0-windows (references the App); Fixtures/Line3.L5X
installer/                    Inno Setup script; tools/publish.ps1 builds exe + installer
.github/workflows/            verify (every push) and release (on a v* tag) - see RELEASING.md
```

## Rules

- **Findings have stable ids** (LC-HW-001...). Never renumber one; retire it and add a new id.
  Each planted fault in `Fixtures/Line3.L5X` is marked with the rule it trips, and
  `AnalysisTests.EachPlantedFaultTripsItsRuleExactly` holds every rule to its count.
- **Under-report rather than invent.** An instruction missing from `InstructionCatalog` reads
  its operands; it never guesses a write.
- **Appearance:** every colour in XAML is a `{DynamicResource}` token. The only literal allowed is
  `#ffffff` on accent fills. `Controls.xaml` above the "LOGICCONTROL ADDITIONS" line is a copy of
  NetControl's - keep it diffable. Default accent is Violet (Blue = DWG viewer, Red = PDF,
  Cyan = NetControl).
- **Logix names are case-insensitive** - every lookup uses OrdinalIgnoreCase.
- `UseWPF` drops `System.IO` from implicit usings; App files that touch files add `using System.IO;`.
- **Writing never overwrites what was read.** ProjectMerger refuses its source path; exports go to
  new files. A merge edits the original XML in place so everything the model does not understand
  (modules, safety, tag values) survives untouched.
- **Write what Studio 5000 writes.** L5xWriter's output must read back through L5xReader to the
  same drafts (AuthoringTests); when Studio 5000 rejects a file, the fix is a test first.
- **The checker warns when it does not know** and errors only on what Studio 5000 would refuse. An
  instruction outside InstructionSignatures is a warning, never an error. `?` for a timer's
  Preset/Accum is what Studio exports and is not flagged.
- `LadderRungView` draws in OnRender; its colours are dependency properties fed from tokens by the
  `LadderRung` style. Never look a brush up inside OnRender.
- **The assistant can only change drafts.** LogicTools has no tool that touches a controller,
  edits the opened project or writes any file other than the drafts (the --mcp host saves its own
  .lcdev after each change); draft tools write the DevelopmentSet and return the checker's verdict. Keep it that way - new tools that act outside the drafts need a person in the loop.
- **One tool table, two front ends**: the panel (ConversationSession over the API) and
  `LogicControl.exe --mcp` (McpServer). A tool added to LogicTools appears in both. Nothing may
  write to stdout in --mcp mode except protocol messages.
- The API key is DPAPI-encrypted (Composition/DpapiKeyStore, P/Invoke - no package). Never log it.
- The installer `AppId` GUID and `InstallLocation.UninstallKey` are the same GUID. Never change it.

## Status

0.2.0 built and ran on Windows (the user opened projects with it). The engine, view models,
updater and assistant are tested on Linux (.NET 10.0.112) with an offline xunit stand-in: 178
tests pass. **The 0.3.0 XAML - the Claude panel and its code-behind - has not been compiled
yet**; the next `verify` run on GitHub is that compile. Still open: import each kind of export
file into a scratch Studio 5000 project (PLAN.md step A), then flip TreatWarningsAsErrors on.
