---
name: tia-hmi-tag-address
description: Change the PLC address, data type or connection of WinCC HMI tags in TIA Portal through the TIA Portal MCP server (export tag table, edit LogicalAddress, import, compile), and check every HMI tag against the PLC it points at. Use it when HMI tags must follow a moved DB or merker, when the user asks which PLC address a panel value really reads, or when a panel shows wrong or "##" values after a PLC change.
---

# Changing HMI tag addresses

An HMI tag's `LogicalAddress` is read-only through the object model (`tia_set_attributes` refuses
it). The supported route is: export the tag table, edit the XML, import it back.

## Procedure

1. `hmi_panels` and `hmi_tag_tables` → which panel and which table holds the tags.
2. `hmi_tags` with `contains` and `details: true` → current `address`, `data_type`,
   `hmi_data_type`, `connection` and `acquisition_cycle` of each tag.
3. **Check the PLC side.** `plc_tags` (TIA PLC) or the symbol table of the STEP 7 project: confirm the
   new address exists, has the expected type and is not already used for something else.
   Remember S7 byte order in words: bits 8..15 are in byte n, bits 0..7 in byte n+1 - this matters
   for alarm words and bit-in-word tags.
4. `hmi_export_tags` for that table into a work folder. Keep an untouched copy of the file.
5. Edit `<LogicalAddress>` (and type or length if needed) only for the tags in scope. Show the user
   a before/after table and get an OK.
6. `hmi_import_tags` with `overwrite: true`. On classic panels the table goes back into the folder it
   already lives in; do not import into the root, or a second table with the same name appears.
7. `hmi_tags` again to confirm the new addresses, then `tia_compile` on the HMI and `tia_save` after
   the user's OK.

## Things to watch

- Values shown as `##` on the panel usually mean the value does not fit the field format: check the
  data type and the PLC value before changing the address.
- A text list is the only way to show a word instead of a number (`hmi_text_lists`).
- A non-retentive tag is 0 after a download or restart of the panel.
- Recipes on the panel are not part of the tag table: back them up separately before a download.
