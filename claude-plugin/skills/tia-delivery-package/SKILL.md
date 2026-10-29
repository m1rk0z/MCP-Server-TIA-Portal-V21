---
name: tia-delivery-package
description: Prepare a dated delivery folder for a PLC/HMI change made with TIA Portal or STEP 7 - archived projects, sources, variable tables, exported screens, panel backups and a README listing what to download and what not to. Use it at the end of a job, before a site visit or a download, or when the user asks to package, hand over or zip a project.
---

# Delivery package

One folder per delivery, named `DELIVERY_<yyyymmdd>` (or the customer's convention), next to the
project documentation. Never overwrite an older delivery: it is the record of what was on site.

## Contents

| Item | How |
|---|---|
| TIA project archive | `.zap19`/`.zap21` from TIA Portal (Project > Archive), dated name |
| STEP 7 V5 project archive | zip of the project folder, or Archive from SIMATIC Manager |
| Changed sources | AWL/SCL of every block touched, with version in the header, plus the original as `_before` |
| Variable tables | the VAT text files used to test the change |
| Exported screens | `hmi_export_screens` of the changed screens, if any |
| Panel backups | `.brf` full backup, recipes and users, taken **before** the download |
| README | see below |

## Zip only with the project closed

A TIA project that is open (in a UI or headless session) keeps its files locked: zipping it then
produces an archive of a few bytes, without any error. Before zipping:

1. `tia_save` if needed, then `tia_detach` / close TIA Portal.
2. Create the zip.
3. **Check the size** and open the archive listing. An archive under a few hundred KB for a real
   project is wrong.

## README

Short, for the person who will do the download:

- what changed and why, one line per block or screen, with versions;
- **blocks to download** and **blocks not to download** (for example DBs whose structure did not
  change but whose current values must be kept);
- panel: whether recipes and users on the panel must be kept when loading;
- how to verify: which VAT to open, what values to expect, which test to run on the machine;
- how to go back: which archive and which backup restore the previous state.

## Rules

- Customer names, IPs and plant paths stay inside the delivery folder and the customer's systems:
  never in public repositories.
- The download itself is done by a person, after reading the README.
