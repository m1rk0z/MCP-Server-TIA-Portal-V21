# MCP Server for TIA Portal V21 (Openness)

An MCP server that drives **Siemens TIA Portal** through the Openness API, and
works with **both HMI families**: WinCC **classic** (Comfort, Advanced,
Professional) and WinCC **Unified**.

*[Leggi questo file in italiano](README.it.md)*

---

## Why this exists

Every `Attach()` to TIA Portal costs a **manual confirmation** from whoever is
sitting in front of it. A command-line tool per operation means a dialog per
operation — accept, wait, accept, wait.

This server attaches **once** and holds the session for as long as the MCP
client is connected. One click, then an entire working session: list screens,
export them, patch the XML, import them back, compile, save.

That is the whole point. Everything else follows from it.

## What makes it different

Most TIA Portal MCP servers target one HMI family and quietly break on the
other, because the two object models have almost nothing in common:

| | WinCC classic | WinCC Unified |
|---|---|---|
| namespace | `Siemens.Engineering.Hmi` | `Siemens.Engineering.HmiUnified` |
| root object | `HmiTarget` | `HmiSoftware` |
| create a screen | **impossible** — no `Screens.Create()` | `Screens.Create(name)` |
| edit a screen | export XML → edit → import | live object model, property by property |
| screen import/export | yes, per file, `ImportOptions` | **does not exist** |
| tag import/export | per file | per **folder**, no `ImportOptions` |
| alarms via API | **not exposed at all** | discrete, analog and alarm classes |
| screens in folders | system + user folders, nested | one flat collection |
| readable attributes | **`Name`, and nothing else** | every property |

This server implements **both** paths, and where a family genuinely cannot do
something it says so and explains why, instead of returning an empty list you
might mistake for an answer:

```
hmi_alarms  →  hmi_alarms is not available on WinCC classic
               (Comfort/Advanced/Professional). Openness exposes no discrete
               alarms, analog alarms or alarm classes for classic WinCC: the
               HmiTarget object model contains no alarm composition at all.
               They are configured in TIA Portal, or imported from xlsx in its
               alarm editor. Only WinCC Unified exposes them through the API.
```

## The thing nobody tells you about classic WinCC

`GetAttributeInfos()` on a classic screen returns **one attribute**: `Name`.
Same on a tag. Same on a connection. Measured on a live V21 project, no
exception raised — the attributes are simply not there.

Screen number, width, height, a tag's `LogicalAddress`, its data type, its
connection, its acquisition cycle: none of it is reachable through the object
model. All of it exists only in the XML that `Export()` writes.

A tool that calls `GetAttribute("Number")` gets `null`, and a tool that reports
that `null` as "this screen has no number" is lying to you.

So `hmi_screens` and `hmi_tags` take `details: true`, which exports to a
temporary folder, reads the real values and deletes it. Tags export **per
table**, so it costs one export per table rather than one per tag. Screens
export one at a time — narrow them with `names`.

On Unified none of this applies: those are ordinary properties on the live
object, and `details` is ignored.

## Requirements

- Windows with **TIA Portal V21** installed (the Openness option, and your user
  in the local **Siemens TIA Openness** group)
- **.NET Framework 4.8** — present on every engineering machine
- **No SDK, no NuGet, no downloads.** It builds with the C# compiler that ships
  inside .NET Framework.

V21 changed things that break older tools: `Siemens.Engineering.dll` was split
into per-domain assemblies, the public key token changed from
`d29ec89bac048f84` to `29bfe5fdf4ba5d3b`, and the assemblies moved to
`PublicAPI\V21\net48`. This server resolves them at runtime, newest Portal
first, so the same binary also finds an older installation — though it is built
and tested against V21.

## Build

```cmd
build.cmd
```

That is all. It finds `csc.exe` and the Openness assemblies by itself and writes
`bin\TiaMcpServer.exe`. To point it somewhere specific:

```cmd
build.cmd -OpennessPath "D:\Siemens\Portal V21\PublicAPI\V21\net48"
```

Then check it answers:

```cmd
powershell -ExecutionPolicy Bypass -File test\smoke.ps1
```

`smoke.ps1` stops at `tia_instances`, which lists running TIA Portal instances
**without attaching**, so it raises no confirmation dialog and touches no
project.

## Configure your MCP client

### Claude Code

```cmd
claude mcp add tia -- "C:\path\to\bin\TiaMcpServer.exe"
```

### Anything that reads a JSON config

```json
{
  "mcpServers": {
    "tia": {
      "command": "C:\\path\\to\\bin\\TiaMcpServer.exe",
      "args": ["--read-only"]
    }
  }
}
```

Drop `--read-only` when you want it to write. See
[examples/](examples/) for VS Code and Claude Desktop.

## Safety

Driving an engineering tool from a language model deserves more care than
driving a web API. Four things are built in:

**`--read-only`** refuses every tool that would modify the project. Attach to a
customer's project with it on and the worst that can happen is that you read
something.

**`keep_safe`** on the import and delete tools lists objects that must never be
touched. Openness cannot regenerate an alarm view or a trend view, so an
`Override` import silently destroys hand-built screens. Name them and they are
skipped, with the skip reported.

**Import stops at the first failure.** An XML rejected for schema reasons can
take the whole TIA Portal process down. The server reports what it managed to
do and stops rather than pushing the remaining files into a dying session.

**The PLC side is read-only by design.** You can list blocks, read their
properties and export them to XML; nothing writes a block back. A PLC block
overwritten by mistake is a plant standing still.

Nothing is ever saved implicitly: changes live in memory until `tia_save`.

## Tools

35 tools. Full reference in [docs/TOOLS.md](docs/TOOLS.md).

**Session** — `tia_instances` `tia_attach` `tia_open_project` `tia_session`
`tia_detach` `tia_save` `tia_compile`

**Project** — `tia_devices` `tia_browse` `tia_get_attributes`
`tia_set_attributes`

**HMI, both families** — `hmi_panels` `hmi_info` `hmi_screens`
`hmi_export_screens` `hmi_import_screens` `hmi_delete_screens`
`hmi_tag_tables` `hmi_tags` `hmi_export_tags` `hmi_import_tags`
`hmi_text_lists` `hmi_export_text_lists` `hmi_import_text_lists`
`hmi_connections` `hmi_alarms`

**HMI, Unified only** — `hmi_create_screen` `hmi_screen_items`
`hmi_create_screen_item` `hmi_item_types`

**PLC, read-only** — `plc_list` `plc_blocks` `plc_export_blocks`
`plc_tag_tables` `plc_tags`

### `tia_browse` — the escape hatch

Openness exposes everything through one uniform model: `IEngineeringObject`
with attributes, and compositions that behave like lists. So rather than
writing a tool for every editor in TIA Portal, `tia_browse`,
`tia_get_attributes` and `tia_set_attributes` navigate it generically — themes,
recipes, scripts, the scheduler, runtime security, whatever this server never
anticipated.

```
tia_browse   path: "Hmi/HMI_RT_1/ScreenFolder/Screens"
tia_browse   path: "Devices/PLC_1/DeviceItems/[1]"
```

Two shortcuts, `Hmi/<panel>` and `Plc/<plc>`, reach the software objects. They
are needed because an `HmiTarget` is not a *property* of anything — it is a
**service** on a `DeviceItem` (`GetService<SoftwareContainer>()`), and no path
made of property names can ever reach it.

`tia_get_attributes` is also how you discover what an object actually exposes:
Openness publishes no single list of attribute names, they are read off the live
object.

## What Openness cannot do

Worth knowing before you plan work around this server. From the Openness manual,
table 8-5, these screen objects **cannot be exported or imported** in classic
WinCC — so they cannot be generated, and they must be protected with
`keep_safe`:

> Alarm view · Alarm window · Alarm indicator · f(t) and f(x) trend views ·
> Recipe view · Table view · Screen window · Camera, PDF, HTML and media views ·
> Editable text field · List box · Combo box · Check box · Option buttons ·
> Round button · Scroll bar · Pipe, T-piece, elbow, connector · Ellipse and
> circle segments and arcs

These do export and import: text field, rectangle, circle, line, polyline,
polygon, ellipse, I/O field, symbolic and graphic I/O field, button, illuminated
button, switch, slider, gauge, bar, date/time field, clock, symbol library,
groups, function keys, faceplate instances, graphic view — plus the **permanent
area** and the **screen template**.

Two more limits found the hard way:

- **Screen `ImportOptions` accepts only `None` and `Override`.**
  `RenameOnConflict` belongs to `DccImportOptions` (drive charts), and
  `IgnoreMissingReferencedObject` to `SWImportOptions` (PLC blocks). A screen
  number collision therefore has **no API remedy** — it has to be avoided by
  numbering.
- **An HMI tag's `LogicalAddress` is read-only.** `SetAttribute` answers
  `'set_LogicalAddress' is not supported`. The route is export the table, edit
  the XML, import it back — which is what `hmi_export_tags` /
  `hmi_import_tags` are for.

## Status

Built and exercised against TIA Portal V21 on a **classic** WinCC project — one
TP700 Comfort panel, 200 screens, 1279 tags across 34 tables — in a single
attached session:

```
tia_attach      T26-044.19_SWHMI, pid 480, Openness V21
hmi_panels      HMI_RT_1 → classic
hmi_screens     details=true → HOME: number 1, 800x480, 38 objects
hmi_tags        details=true → %DB151.DBW240, Word/UInt, PLC, 1 s, with comment
hmi_alarms      refused, with the reason
tia_save        refused, --read-only
```

The **Unified** code path is written against the V21
`Siemens.Engineering.WinCCUnified` assembly — types and signatures read by
reflection, not guessed — but it is **not yet exercised against a real Unified
project**. If you have one, issues and pull requests are very welcome.

## Licence

MIT. See [LICENSE](LICENSE).

TIA Portal, WinCC, SIMATIC and Openness are trademarks of Siemens AG. This is an
independent project, not affiliated with or endorsed by Siemens.
