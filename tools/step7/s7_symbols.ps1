# s7_symbols.ps1 - elenca i simboli di un programma S7 che corrispondono a un filtro (su nome, indirizzo o commento).
# Uso: powershell -File s7_symbols.ps1 <cartella progetto> <nome programma> <regex>
param([string]$ProjectDir, [Parameter(Mandatory=$true)][string]$Program, [string]$Filter = ".")

$s = New-Object -ComObject Simatic.Simatic.1
$s.UnattendedServerMode = $true
$prj = $null
foreach ($p in $s.Projects) { if ($p.LogPath -eq $ProjectDir) { $prj = $p } }
if ($prj -eq $null) { $prj = $s.Projects.Add((Get-ChildItem -Path $ProjectDir -Filter *.s7p | Select-Object -First 1).FullName) }
$pg = $null
foreach ($x in $prj.Programs) { if ($x.Name -eq $Program -and $x.Type -eq 1327361) { $pg = $x } }
if ($pg -eq $null) { throw "programma $Program non trovato" }
$tab = $pg.SymbolTable
"simboli totali: $($tab.Symbols.Count)"
$n = 0
foreach ($sym in $tab.Symbols) {
    $line = "{0,-28} {1,-12} {2}" -f $sym.Symbol, $sym.Address, $sym.Comment
    if ($line -match $Filter) { $line; $n++ }
}
"trovati: $n"
