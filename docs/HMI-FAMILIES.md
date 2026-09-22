# The two WinCC families, and why one server has to know the difference

TIA Portal ships two HMI object models that share a vendor and almost nothing
else. A tool written for one silently misbehaves on the other. This page is the
map — everything here was read off the **V21 Openness assemblies** by
reflection, not inferred.

## Where they live

| | classic | Unified |
|---|---|---|
| assembly | `Siemens.Engineering.WinCC.dll` | `Siemens.Engineering.WinCCUnified.dll` |
| namespace | `Siemens.Engineering.Hmi` | `Siemens.Engineering.HmiUnified` |
| root | `HmiTarget` | `HmiSoftware` |
| panels | Comfort, Advanced, Professional | Unified Comfort, Unified PC |

Both hang off a `DeviceItem` as a **service**, not a property:

```csharp
var container = deviceItem.GetService<SoftwareContainer>();
var software   = container.Software;      // HmiTarget oppure HmiSoftware
```

This matters more than it looks. No path built out of property names can reach
an HMI panel, which is why this server's `tia_browse` has the `Hmi/<name>` and
`Plc/<name>` shortcuts.

## Screens: the models are inverted

**Classic** has no way to create a screen. `ScreenComposition` offers
`Import`, `Find`, `CreateFrom(MasterCopy)` — and no `Create(name)`. A screen is
born from an XML file or from a master copy, never from code. To change one you
export it, edit the XML, import it back with `ImportOptions.Override`.

**Unified** is the opposite. `HmiScreenComposition.Create(name)` makes a screen,
`HmiScreen` exposes `Width`, `Height`, `ScreenNumber`, `BackColor` and the rest
as ordinary writable properties, and `ScreenItems.Create<T>(name)` adds objects
to it. And there is **no import or export of screens at all** — the
composition does not implement file-based data exchange.

So a generator that produces XML works only on classic, and a generator that
builds objects works only on Unified. They are not two dialects of one job.

| | classic | Unified |
|---|---|---|
| `Screens.Create(name)` | — | yes |
| `Screens.Import(file, options)` | yes | — |
| `screen.Export(file, options)` | yes | — |
| `screen.Delete()` | yes | yes |
| screen items reachable as objects | — | yes, `ScreenItems` |
| folders | `ScreenFolder` + nested user folders | flat, plus `ScreenGroups` |

## Import and export: even the signatures disagree

Classic works on **one file** and takes `ImportOptions`:

```csharp
IList<Screen> Import(FileInfo path, ImportOptions options);   // None | Override
void Export(FileInfo path, ExportOptions options);            // None | WithDefaults
```

Unified works on a **folder**, returns the files it wrote, and has no options at
all:

```csharp
IEnumerable<FileInfo> Export(DirectoryInfo destination);
IEnumerable<FileInfo> Export(DirectoryInfo destination, string name);
bool Import(DirectoryInfo source);
bool Import(DirectoryInfo source, string name);
```

And in Unified only these support it: `Tags`, `HmiTextLists`,
`HmiGraphicLists`, `HmiSystemTextLists`, `Scripts`. Nothing else — verified by
enumerating every exported type that implements `IChromDataExchangeExport`,
which comes back with exactly one hit: `HmiScriptModule`.

## Alarms: present on one side only

`HmiSoftware` exposes `DiscreteAlarms`, `AnalogAlarms`, `AlarmClasses`,
`AlarmLogs`, `OpcUaAlarmTypes`.

`HmiTarget` exposes **none of them**. The `Siemens.Engineering.Hmi.Alarm`
namespace exists but contains only enumerations — `TriggerMode`, `LimitMode`,
`SystemBlockId` and friends — no objects and no compositions. Classic alarms
are configured in TIA Portal's own editor, or imported there from xlsx.

That is a hard limit of the API, not of this server, and it is why `hmi_alarms`
on a classic panel answers with an explanation rather than an empty list.

### If you do drive classic alarms through the xlsx route

Three constraints, each found by having TIA reject the import:

1. **The trigger tag name must not contain a dot.** A dot is read as a struct
   member separator: the trigger goes pink and the address comes out empty.
2. **One `(tag, bit)` pair drives exactly one discrete alarm.** Reuse the pair
   and the Trigger bit cell turns pink.
3. **Bit 0 is rejected** — `The trigger bit of the discrete alarm N is invalid`.

And a numbering trap worth writing down, confirmed against the addresses TIA
itself resolves: in a `Word`, **bits 8–15 live in byte *n*, bits 0–7 in byte
*n+1***. Bit 11 resolves to `%DB126.DBX320.3`, bit 1 to `%DB5.DBX11.1`, bit 9 to
`%DB5.DBX10.1`.

## The classic object model exposes almost nothing

This one is worth its own heading, because it is the single biggest practical
difference and it is not in the manual.

In classic WinCC, `GetAttributeInfos()` returns **one attribute** on a screen,
on a tag and on a connection: `Name`. Measured on a real V21 project:

```
Hmi/HMI_RT_1/ScreenFolder/Screens/HOME                        →  Name
Hmi/HMI_RT_1/Connections/PLC                                  →  Name
Hmi/HMI_RT_1/TagFolder/.../SISTEMA_A_LINEE/Tags/[0]           →  Name
HmiTarget itself                                              →  Author, Name
```

No exception is raised — the attributes genuinely are not there. Screen number,
width, height, background colour, a tag's `LogicalAddress`, its data type, its
connection, its acquisition cycle, its comment: **none of it is reachable
through the object model.** All of it exists only in the XML that `Export()`
writes.

So in classic WinCC the object model is, in practice, a name index plus an
import/export port. Any tool that expects to read a screen's number by calling
`GetAttribute("Number")` gets `null`, and any tool that reports that `null` as
"no number" is lying to you.

This server handles it by exporting on demand: `hmi_screens` and `hmi_tags` take
`details: true`, which exports to a temporary folder, reads the real values and
deletes it. Tags export **per table**, so the cost is one export per table
rather than one per tag. Screens export one at a time, so narrow with `names`.

Unified has no such problem: `HmiScreen.ScreenNumber`, `HmiTag.Address`,
`HmiTag.DataType` are ordinary properties on the live object, and `details` is
ignored there.

Where the values live in an export:

```xml
<Hmi.Screen.Screen>
  <AttributeList>
    <Height>480</Height><Number>9000</Number><Width>800</Width>

<Hmi.Tag.Tag>
  <AttributeList>
    <LogicalAddress>%DB7.DBW36</LogicalAddress><Length>2</Length>
  <LinkList>
    <DataType><Name>Word</Name></DataType>
    <Connection><Name>PLC</Name></Connection>
    <AcquisitionCycle><Name>1 s</Name></AcquisitionCycle>
```

Note that data type, connection and acquisition cycle are in `LinkList`, not
`AttributeList` — they are references to other objects, not values.

## Tags

| | classic | Unified |
|---|---|---|
| type | `Siemens.Engineering.Hmi.Tag.Tag` | `HmiTag` |
| readable via API | **`Name` only** | every property |
| address | `<LogicalAddress>` in the export, **read-only** | property `Address` |
| tables | `TagFolder` + nested user folders | flat `TagTables` |
| structure members | — | `HmiTag.Members` |
| thresholds, logging | — | `Thresholds`, `LoggingTags` |

`SetAttribute("LogicalAddress", ...)` on a classic tag answers
`'set_LogicalAddress' is not supported`. To change an address: export the table,
rewrite the `<LogicalAddress>` element, import back with `Override`. Keep the
document TIA wrote — an XML you compose yourself is how an import gets rejected,
and a rejected import can take the Portal process down.

## What classic cannot export at all

Openness manual, table 8-5. These screen objects refuse both export and import:

> Alarm view · Alarm window · Alarm indicator · f(t) and f(x) trend views ·
> Recipe view · Table view · Value table · Screen window · Camera view · PDF
> view · HTML browser · Media player · Editable text field · List box · Combo
> box · Check box · Option buttons · Round button · Scroll bar · Pipe, T-piece,
> elbow, connector · Ellipse and circle segments and arcs

Consequences you have to plan around, because there is no workaround:

- A screen containing an alarm view **cannot be generated**. It is built by hand
  in TIA, once, and from then on protected with `keep_safe` on every import.
- The same goes for any screen with a trend view or a recipe view.

These do export and import cleanly: text field, rectangle, circle, line,
polyline, polygon, ellipse, I/O field, symbolic I/O field, graphic I/O field,
button, illuminated button, switch, slider, gauge, bar, date/time field, clock,
symbol library, groups, function keys, faceplate instances, graphic view — and
also the **permanent area** and the **screen template**. Importing a template
throws if its width or height do not match the device resolution.

## Practical consequence

If you are generating screens for a **classic** panel, you are writing an XML
generator, and you accept that a handful of screens will always be hand-built.
If you are generating for **Unified**, you are writing code against a live object
model, and there is no XML to get wrong.

This server covers both, and refuses clearly where the family cannot.
