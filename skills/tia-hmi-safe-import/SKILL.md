---
name: tia-hmi-safe-import
description: Safe export-edit-import of WinCC classic (Comfort/Advanced/Professional) screens through the TIA Portal MCP server, without losing alarm views, recipe views or trend views that Openness cannot export. Use it whenever screens will be imported, overwritten or deleted with hmi_import_screens or hmi_delete_screens, when fixing compile warnings across many screens, or when alarms or recipes have disappeared from a panel after an import.
---

# Safe screen import on WinCC classic

On WinCC classic, Openness does **not** export alarm views, recipe views and some trend views: the
exported XML simply does not contain them. An import with `Override` replaces the whole screen with
what is in the file, so those objects are deleted **without any error or warning**. The compile
stays clean, the panel looks fine, and alarms or recipes stop showing up on the machine weeks later.

## Before any import

1. `hmi_panels` → confirm the family is `classic`. (Unified has no screen import/export.)
2. **Archive the project** (`.zap` from TIA Portal, dated name) or ask the user to. No import without
   a backup.
3. **Build the `keep_safe` list.** Counting objects in the exported XML does not help: the alarm view
   is missing both before and after. Instead:
   - list every screen with `hmi_screens`;
   - take every screen whose name or number suggests alarms, alarm history, recipes, recipe data,
     trends or diagnostics (in Italian projects: *Allarmi*, *Storico*, *Ricette*, *Ricetta*, *Carico*,
     *Trend*), plus every pop-up that contains a recipe view;
   - **show the list to the user and ask them to complete it** - only someone who knows the panel can
     confirm it.
4. Export the screens to edit with `hmi_export_screens` and `with_defaults: true` (a lean export
   loses attributes the import cannot restore). Keep this export as the "before" copy.

## Import

- Edit only the screens that need it. Import only those files, never a whole folder by habit.
- `hmi_import_screens` with `keep_safe` = the confirmed list. Check `protected` in the result: every
  protected screen that was in the input must be listed there.
- The import stops at the first failure and a schema error can crash TIA Portal: read
  `stopped_on_first_error` and what was imported before it.
- Screen numbers must stay unique: `Override` and `None` are the only options, there is no
  rename-on-conflict.
- `tia_compile` on the HMI, report errors and warnings, then `tia_save` after the user's OK.
- Ask the user to open the alarm and recipe screens on the panel (or in simulation) before the
  download.

## If alarm or recipe views are already lost

The XML route cannot bring them back. What works is copying the whole screen object from an older
version of the project:

1. Find the last archive made before the bad import.
2. Retrieve it (headless) and create **master copies** of the affected screens in a global library.
3. In the current project delete the empty screen and create it again from the master copy in the
   same folder (`CreateFrom`). Screen numbers stay the same.
4. Compile, save, check the screens visually, then download the panel.

This needs an Openness script (library and master copy APIs are not exposed by the MCP server).
Back up the current project first.

## Not possible through Openness

- Pack&Go for Comfort panels: it is made by hand in TIA Portal (Online > HMI device maintenance).
- Alarms on classic panels: `hmi_alarms` works only on Unified.
