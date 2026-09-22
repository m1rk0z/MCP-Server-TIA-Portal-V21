# Tool reference

35 tools. Arguments marked **required** must be present; everything else has a
default. Tools marked **writes** are refused when the server runs with
`--read-only`.

Every tool returns JSON as a single text content block.

---

## Session

Nothing else works until there is a session. Start with `tia_instances`, which
attaches to nothing and therefore raises no dialog.

### `tia_instances`
Running TIA Portal instances and the project open in each.

Returns `pid`, `project_path`, `mode`, `has_project` per instance.

### `tia_attach`
Attach to a running instance and hold the session.

| argument | type | |
|---|---|---|
| `pid` | integer | process id; omit for the first instance with a project open |

**This is the one manual confirmation.** Whoever is at TIA Portal accepts once,
and the session then holds for the life of the server process. Do not detach and
re-attach between operations — each round trip costs that person another click.

### `tia_open_project` — *writes*
Open a project file in a new instance.

| argument | type | |
|---|---|---|
| `path` | string | **required** — `.ap21`, `.zap21`, … |
| `with_ui` | boolean | default `false` (headless: faster, no dialog, nothing on screen) |

### `tia_session`
Session state, plus which folder the Openness assemblies were loaded from and
where else the server looked. First thing to check when something will not load.

### `tia_detach`
Release the session. TIA Portal stays open; a project opened headless by this
server closes with it.

### `tia_save` — *writes*
Save the project. Nothing else saves implicitly.

### `tia_compile` — *writes*
Compile and return the message tree, flattened with nesting depth.

| argument | type | |
|---|---|---|
| `target` | string | device or software name; `*` (default) for all |

Returns `state`, `errors`, `warnings` and every message with `state`, `text`,
`path`. Warnings with no text are reported with the object that raised them —
otherwise they are impossible to find.

---

## Project

### `tia_devices`
Every device: type, order number, firmware, hosted software, and network nodes
with their subnet.

Type, order number and firmware are read from the `DeviceItem`s, not from the
`Device` — on the `Device` they always come back null.

### `tia_browse`
Walk the object model.

| argument | type | |
|---|---|---|
| `path` | string | empty for the project root |
| `attributes` | boolean | default `false` |

Path segments are separated by `/`. Each segment is a **property name**, or an
**element name** when you are standing on a collection, or `[n]` for an index.

Two shortcuts reach software objects, which are services rather than properties
and are otherwise unreachable:

```
Hmi/HMI_RT_1/ScreenFolder/Screens
Plc/PLC_1/BlockGroup/Blocks
Devices/HMI_1/DeviceItems/[1]
```

Returns `items` (when standing on a collection, capped at 500), `properties`
(the navigable ones), and optionally `attributes`.

### `tia_get_attributes`
Every readable attribute of the object at the end of the path.

| argument | type | |
|---|---|---|
| `path` | string | **required** |

Openness publishes no single list of attribute names — they are read off the
live object via `GetAttributeInfos()`. So this is also the tool for finding out
what an object actually exposes.

### `tia_set_attributes` — *writes*

| argument | type | |
|---|---|---|
| `path` | string | **required** |
| `values` | object | **required** — name/value pairs |

Values are coerced to the attribute's declared type before writing, since JSON
only carries numbers and strings. Attributes that refuse the write are reported
individually rather than failing the whole call.

Many attributes are read-only. An HMI tag's `LogicalAddress` is the one you will
hit first: the route there is `hmi_export_tags` → edit the XML →
`hmi_import_tags`.

---

## HMI — both families

### `hmi_panels`
The panels in the project, each with its family. **Ask this first** — the two
families differ enough that the answer changes what else will work.

Returns `panel`, `device`, `family` (`classic` | `unified`), `family_label`.

### `hmi_info`
Fact sheet: device type, counts, every readable attribute, and `notes` spelling
out what this family can and cannot do.

| argument | type | |
|---|---|---|
| `panel` | string | omit when there is only one |

### `hmi_screens`

| argument | type | |
|---|---|---|
| `names` | array | restrict to these screens; empty means all |
| `details` | boolean | default `false` |

Without `details` you get names and folders — because on classic WinCC that is
genuinely all the object model holds. `GetAttributeInfos()` on a screen returns
exactly one attribute, `Name`.

With `details: true` each matching screen is exported to a temporary file, and
`number`, `width`, `height`, `background` and the screen item count are read out
of the XML, which is the only place they exist. That is one export per screen,
so narrow it with `names` on a large panel.

On Unified `details` is ignored: those are live properties there.

### `hmi_export_screens` — classic only
One XML per screen.

| argument | type | |
|---|---|---|
| `out_dir` | string | **required** — created if missing |
| `names` | array | empty means all |
| `with_defaults` | boolean | default `true` |

`with_defaults` writes the default values too. That is the export you want if
the file is going back in — a lean export loses attributes the import then
cannot restore.

Names that match nothing are reported in `errors` rather than silently ignored.

### `hmi_import_screens` — classic only, *writes*

| argument | type | |
|---|---|---|
| `files` | array | paths of XML files |
| `dir` | string | instead of `files` |
| `pattern` | string | inside `dir`, default `*.xml` |
| `folder` | string | destination screen folder, created if missing; empty = root |
| `overwrite` | boolean | `true` = `Override` (default), `false` = `None` |
| `keep_safe` | array | screens that must never be touched |

**`keep_safe` is the important one.** Openness cannot regenerate alarm views or
trend views, so a screen containing one is built by hand in TIA — and an
`Override` import deletes it without asking. List those screens and they are
skipped, with the skip reported in `protected`.

**Stops at the first failure.** An XML rejected for schema reasons can take the
TIA Portal process down. The result carries `stopped_on_first_error` and what
had been imported up to that point.

`Override` and `None` are the only two options screens accept.
`RenameOnConflict` belongs to a different enum, so a screen number collision has
no API remedy — avoid it by numbering.

### `hmi_delete_screens` — *writes*

| argument | type | |
|---|---|---|
| `names` | array | screens to delete |
| `keep_safe` | array | refused rather than deleted |

Names that match nothing come back in `not_found`.

### `hmi_tag_tables`
Tag tables with folder and tag count.

### `hmi_tags`

| argument | type | |
|---|---|---|
| `contains` | string | case-insensitive filter |
| `limit` | integer | default 500, `0` = no limit |
| `details` | boolean | default `true` |

Same story as screens: a classic tag exposes only `Name` through the object
model. With `details` (on by default) the server exports each table **once** and
reads `address`, `length`, `coding`, `data_type`, `hmi_data_type`, `connection`,
`acquisition_cycle` and `comment` out of it — so the cost is one export per
table, not one per tag.

```json
{ "name": "A_LINEA_XILOLO.Sel", "table": "SISTEMA_A_LINEE",
  "address": "%DB151.DBW240", "data_type": "Word", "hmi_data_type": "UInt",
  "connection": "PLC", "acquisition_cycle": "1 s",
  "comment": "Selezione linea XILOLO" }
```

Note where those come from in the export: `address`, `length` and `coding` are
in `AttributeList`, while `data_type`, `connection` and `acquisition_cycle` are
in `LinkList` — they are references to other objects, not values.

### `hmi_export_tags`

| argument | type | |
|---|---|---|
| `out_dir` | string | **required** |
| `names` | array | tables; empty means all |

Classic writes one XML per table. Unified exports the whole tag collection into
the folder — that is the shape of its API, not a choice made here.

### `hmi_import_tags` — *writes*

| argument | type | |
|---|---|---|
| `files` / `dir` / `pattern` | | as in `hmi_import_screens` |
| `overwrite` | boolean | default `true` |

Classic re-imports each table **into the folder it already lives in**; importing
into the root instead would create a second table with the same name.

This is the supported way to change a tag address: export, rewrite
`<LogicalAddress>`, import back.

### `hmi_text_lists`, `hmi_export_text_lists`, `hmi_import_text_lists`
Text lists are the only way to show a word instead of a number. Without one, a
value can only be coloured.

### `hmi_connections`
Connections to the PLCs with every driver parameter.

### `hmi_alarms` — Unified only
Discrete alarms, analog alarms and alarm classes, each with all its attributes.

On a classic panel this returns an explanation, not an empty list: the
`HmiTarget` model contains no alarm composition at all. See
[HMI-FAMILIES.md](HMI-FAMILIES.md).

---

## HMI — Unified only

### `hmi_create_screen` — *writes*

| argument | type | |
|---|---|---|
| `name` | string | **required** |
| `number` | integer | screen number |

### `hmi_screen_items`

| argument | type | |
|---|---|---|
| `screen` | string | **required** |

Every object in the screen with its type and all readable attributes.

### `hmi_create_screen_item` — *writes*

| argument | type | |
|---|---|---|
| `screen` | string | **required** |
| `item_type` | string | **required** — e.g. `HmiRectangle` |
| `item_name` | string | **required** |
| `attributes` | object | set immediately after creation |

The type is resolved by name against the Unified assembly and created through
the generic `ScreenItems.Create<T>()`, so every type in the library is reachable
without this server enumerating them. Attributes that are refused are reported
one by one; the object still gets created.

### `hmi_item_types`

| argument | type | |
|---|---|---|
| `contains` | string | filter on the type name |

Works **without an attached project** — it reads the installed assemblies. Handy
for checking what is available before you commit to a design.

---

## PLC — read-only

Nothing here writes. A PLC block overwritten by mistake is a plant standing
still, and no amount of saved time justifies the risk.

### `plc_list`
PLCs with block and tag counts.

### `plc_blocks`

| argument | type | |
|---|---|---|
| `plc` | string | omit when there is only one |
| `contains` | string | filter on the name |
| `kind` | string | `OB`, `FB`, `FC`, `GlobalDB`, `InstanceDB` |
| `limit` | integer | default 500 |

Name, type, number, group, language, consistency, know-how protection, last
modified.

### `plc_export_blocks`

| argument | type | |
|---|---|---|
| `out_dir` | string | **required** |
| `names` | array | blocks to export |

Know-how protected blocks cannot be exported and are reported as such rather
than raising.

### `plc_tag_tables`, `plc_tags`
Tables and tags with absolute address and data type. Use them to check what an
HMI tag actually points at.

---

## Errors

A tool that is misused returns a normal result with `isError: true` and an
explanation, not a JSON-RPC error — so the model can read it and correct itself.
JSON-RPC errors are reserved for protocol-level problems.

Failures inside a batch (one screen out of two hundred) are reported per item in
`errors`, alongside what did succeed. A partial result is still a result.
