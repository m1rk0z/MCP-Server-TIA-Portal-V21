# s7_gen_block.ps1 - rigenera in un file il sorgente AWL di uno o piu blocchi (sola lettura sul progetto).
# Uso: s7_gen_block.ps1 <cartella progetto> <programma> <cartella out> <blocco> [<blocco> ...]
param([string]$ProjectDir, [string]$Program, [string]$OutDir, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Blocks)
New-Item -ItemType Directory -Force $OutDir | Out-Null
$s = New-Object -ComObject Simatic.Simatic.1
$s.UnattendedServerMode = $true
foreach ($p in $s.Projects) { if ($p.LogPath -eq $ProjectDir) { $prj = $p } }
foreach ($x in $prj.Programs) { if ($x.Name -eq $Program -and $x.Type -eq 1327361) { $pg = $x } }
foreach ($c in $pg.Next) { if ($c.Name -match "Blocchi|Blocks") { $blks = $c } }
$all = @($blks.Next | ForEach-Object { $_.Name })
if ($Blocks -contains "*LIST*") { "blocchi: " + ($all -join " "); return }
foreach ($b in $blks.Next) {
    if ($Blocks -contains $b.Name) {
        $f = Join-Path $OutDir "$($b.Name).awl"
        try { $b.GenerateSource($f); "generato $f" } catch { "ERR $($b.Name): $($_.Exception.Message)" }
    }
}
