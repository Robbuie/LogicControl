# LogicControl

Reads a Rockwell Studio 5000 L5X export and shows the controller as a system - and writes logic
back as L5X for Studio 5000 to import.

**Reading**

- **Hardware** - the I/O tree rebuilt with slots, IP addresses, revisions, keying and RPIs.
- **Communications** - owned I/O connections, produced and consumed tags, every MSG with where its
  path ends and which rung fires it, and GSV module-status reads.
- **Tags** - every tag with read/write counts and a cross-reference to each rung or ST line.
- **Logic** - ladder drawn with rails, contacts, coils, boxes and branches (or the neutral text,
  Ctrl+L). Click an operand to open its tag.
- **Findings** - duplicate IPs, inhibited modules, consumed tags from nowhere, messages nothing
  fires, double coils, unscheduled programs, uncalled routines, undeclared tags and more.

**Writing** (the Develop tab)

- Draft **data types**, **Add-On Instructions**, **programs**, **ladder routines** and **tags** -
  or paste a member or tag list straight out of Excel.
- **Generate** from templates: motor starter (as rungs or as an AOI), two-position valve, latched
  alarm, analog input, heartbeat - one UDT, a tag per instance, the rungs for each.
- **Edit a copy** of any routine or AOI in the open project.
- Every rung is drawn as it is typed and **checked** against the instruction set, the operand
  counts, and the tags, members and AOIs the project and the drafts declare.
- **Export import files** - one L5X per item, numbered in import order, with what each needs
  carried as context - or **write into a copy of the project** and open that in Studio 5000.
- Save the drafts as a `.lcdev` file and carry on later.

It never connects to a controller. Everything it writes goes through Studio 5000's import and
verify, and a person, before it gets near one.

## Getting an L5X

Studio 5000: **File > Save As**, set the type to **Logix Designer XML File (*.L5X)**. The .ACD
project file is Rockwell's own binary format and is not read directly.

## Getting what LogicControl wrote into Studio 5000

- **Import files** (Export import files...): in Studio 5000's Controller Organizer, right-click
  where each belongs and use its Import command - *Data Types > User-Defined* for a data type,
  *Add-On Instructions* for an AOI, a task for a program, a program for a routine. Do them in the
  files' number order; the import dialog shows what each file creates or overwrites.
- **Project copy** (Write into project copy...): needs a whole-controller export open. Studio 5000
  *File > Open*, type L5X, picks the file and builds a new .ACD from it.

## Install

Download `LogicControl-Setup-<version>.exe` from the
[latest release](https://github.com/Robbuie/LogicControl/releases/latest) - a per-user install, no
administrator prompt - or the loose `LogicControl.exe` to run from anywhere. The app tells you when
a newer build is published and updates itself from the status bar.

## Build

```
dotnet build LogicControl.slnx
dotnet test LogicControl.slnx
```

Releases are built by GitHub Actions on a tag - see [RELEASING.md](RELEASING.md).
