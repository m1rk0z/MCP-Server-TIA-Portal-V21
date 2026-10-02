# s7_export_symbols.ps1 - esporta in .sdf la tabella simboli di ogni programma S7 di uno o piu progetti STEP 7 V5.
# Uso: powershell -File s7_export_symbols.ps1 -OutDir <cartella> <cartella progetto> [<cartella progetto> ...]
# Sola lettura sul progetto. Richiede PowerShell a 32 bit (l'interfaccia COM di STEP 7 e a 32 bit).
param([Parameter(Mandatory=$true)][string]$OutDir, [Parameter(ValueFromRemainingArguments = $true)][string[]]$ProjectDirs)
New-Item -ItemType Directory -Force $OutDir | Out-Null
$s = New-Object -ComObject Simatic.Simatic.1
$s.UnattendedServerMode = $true
foreach ($dir in $ProjectDirs) {
  $prj = $null
  foreach ($p in $s.Projects) { if ($p.LogPath -eq $dir) { $prj = $p } }
  if ($prj -eq $null) { $prj = $s.Projects.Add((Get-ChildItem -Path $dir -Filter *.s7p | Select-Object -First 1).FullName) }
  foreach ($x in $prj.Programs) { if ($x.Type -eq 1327361) {
     $out = Join-Path $OutDir ($prj.Name + "_" + $x.Name + "_symbols_full.sdf")
     try { $x.SymbolTable.Export($out); "export ok: $out" } catch { "export KO $($x.Name): $($_.Exception.Message)" }
  } }
}
