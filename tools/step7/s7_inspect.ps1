# s7_inspect.ps1 - legge da un progetto STEP 7 V5 (COM Simatic.Simatic.1) simboli e sorgenti.
# Uso: powershell -File s7_inspect.ps1 <cartella progetto> <regex simboli>
param([string]$ProjectDir, [string]$SymbolFilter = ".")

$s = New-Object -ComObject Simatic.Simatic.1
$s.UnattendedServerMode = $true
$prj = $null
foreach ($p in $s.Projects) { if ($p.LogPath -eq $ProjectDir) { $prj = $p } }
if ($prj -eq $null) {
    $s7p = Get-ChildItem -Path $ProjectDir -Filter *.s7p | Select-Object -First 1
    $prj = $s.Projects.Add($s7p.FullName)
}
"PROGETTO: $($prj.Name)  ($($prj.LogPath))"
foreach ($pg in $prj.Programs) {
    "PROGRAMMA: $($pg.Name)  type=$($pg.Type)"
    try {
        foreach ($sym in $pg.SymbolTable.Symbols) {
            if ($sym.Symbol -match $SymbolFilter -or $sym.Comment -match $SymbolFilter) {
                "  SYM  {0,-28} {1,-10} {2}" -f $sym.Symbol, $sym.Address, $sym.Comment
            }
        }
    } catch { "  (tabella simboli non leggibile: $($_.Exception.Message))" }
    foreach ($c in $pg.Next) {
        "  CONTAINER: $($c.Name)"
        if ($c.Name -match "Sorgenti|Sources|Quellen") {
            foreach ($src in $c.Next) { "    SRC  $($src.Name)  file=$($src.Filename)" }
        }
    }
}
