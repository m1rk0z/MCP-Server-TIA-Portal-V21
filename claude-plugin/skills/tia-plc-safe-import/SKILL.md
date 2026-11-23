---
name: tia-plc-safe-import
description: Safe import of PLC blocks, SCL/STL/DB/UDT sources, PLC tag tables and data types into the offline TIA Portal project through the TIA Portal MCP server, without overwriting work by mistake and without ever downloading to a CPU. Use it whenever plc_import_blocks, plc_import_sources, plc_import_tag_tables or plc_import_types will be called, when a block must be changed in a project already running on a plant, or when the user asks to bring generated or edited PLC code into TIA Portal.
---

# Safe PLC import into the offline project

The `plc_import_*` tools change the **offline project in memory**. They never compile, save or
download. A block replaced by mistake and then downloaded is a plant standing still, so the order
below is not optional.

## Before importing

1. **Know what is in the CPU.** If the plant is reachable, run `plc_compare_online` first. Blocks
   that differ, or that exist only in the CPU, mean somebody changed the program on site: stop and
   tell the user. Importing on top of an outdated offline project loses that work at the next
   download.
2. **Go offline.** Every import is refused while TIA Portal is online with the CPU.
3. **Take the way back.** Export what you are about to replace into a dated folder, outside the
   project: `plc_export_blocks` (XML, every language), `plc_export_sources` (text, SCL/STL/DB/UDT
   only), `plc_export_tag_tables`, `plc_export_types`. Without that export there is no undo once
   the project is saved.
4. **Ask the user** before the first call with `overwrite=true`, naming the objects that will be
   replaced. An approval covers those objects, not later ones.

## Importing

- Start with `overwrite=false`. The reply lists in `refusals` every object that already exists:
  show that list to the user, then repeat with `overwrite=true` only for what they approved.
- List in `keep_safe` anything that must survive whatever the files contain.
- Order matters: data types first, then tag tables, then DBs, then FBs and FCs, then OBs. A block
  imported before the type it uses is rejected or left inconsistent.
- `plc_import_sources` reads the declarations in each file and treats them as overwrites. One file
  may declare several blocks: all of them are replaced together.
- The batch stops at the first failure. Read `errors`, fix the file, run again. Do not retry the
  same file unchanged: a rejected XML can take TIA Portal down.
- Know-how protected objects are never replaced. Do not look for a way around it.

## After importing

1. `tia_compile` on the PLC and read every error and warning. Replacing a data type or an FB
   interface makes its users inconsistent until they are compiled.
2. `plc_blocks` on the touched names: `consistent` must be true.
3. Only then `tia_save`. To discard everything instead, `tia_detach` a headless project without
   saving, or close TIA Portal without saving.
4. Tell the user exactly which objects changed and that the **download is theirs to do** from TIA
   Portal. Never download, upload, start or stop a CPU, with these tools or with any other script.

## Limits

- LAD, FBD and GRAPH blocks have no text source: use the XML route (`plc_export_blocks` and
  `plc_import_blocks`).
- An XML exported from a newer TIA Portal version does not import into an older one.
- Instance DBs follow their FB: after replacing an FB interface, compile and check the instance
  DBs, whose actual values may be reset at the next download.
- Safety (F) blocks and the safety administration are out of scope: leave them to TIA Portal.
