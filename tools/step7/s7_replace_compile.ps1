# s7_replace_compile.ps1 - sostituisce un sorgente AWL in un progetto STEP 7 V5 e lo compila (COM Simatic.Simatic.1).
#
# Uso (PowerShell 32 bit):
#   s7_replace_compile.ps1 <cartella progetto> <programma> <nome sorgente> <file.awl> <blocco da verificare> <cartella log>
#
# 1. esporta il sorgente attuale in <log>\<nome>_prima.awl
# 2. rimuove il sorgente e lo reinserisce dal file (tipo 65 = sorgente)
# 3. compila: il risultato sono i blocchi generati
# 4. rigenera il sorgente dal blocco compilato in <log>\<blocco>_da_blocco.awl, per verificare cosa c'e davvero nel blocco
#    (nel file rigenerato gli ingressi compaiono con il loro simbolo, es. "A1 scatto termico")
param([string]$ProjectDir, [string]$Program, [string]$SourceName, [string]$File, [string]$Block, [string]$LogDir)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $LogDir | Out-Null
$s = New-Object -ComObject Simatic.Simatic.1
$s.UnattendedServerMode = $true
$prj = $null
foreach ($p in $s.Projects) { if ($p.LogPath -eq $ProjectDir) { $prj = $p } }
if ($prj -eq $null) { throw "progetto non trovato: $ProjectDir" }
$pg = $null
foreach ($x in $prj.Programs) { if ($x.Name -eq $Program -and $x.Type -eq 1327361) { $pg = $x } }
if ($pg -eq $null) { throw "programma non trovato: $Program" }
foreach ($c in $pg.Next) {
    if ($c.Name -match "Sorgenti|Sources") { $srcs = $c }
    if ($c.Name -match "Blocchi|Blocks") { $blks = $c }
}

$old = $null
foreach ($x in $srcs.Next) { if ($x.Name -eq $SourceName) { $old = $x } }
if ($old -eq $null) { throw "sorgente non trovato: $SourceName" }
$old.Export((Join-Path $LogDir "$($SourceName)_prima.awl"))
"esportato sorgente attuale"
$old.Remove()
$new = $srcs.Next.Add($SourceName, 65, $File)
"reinserito da $File"

try {
    $res = $new.Compile()
    "COMPILAZIONE OK"
    foreach ($b in $res) { "  generato: $($b.Name)" }
} catch {
    "COMPILAZIONE KO: $($_.Exception.Message)"
    exit 1
}

foreach ($b in $blks.Next) {
    if ($b.Name -eq $Block) {
        "blocco $($b.Name) modificato: $($b.Modified)"
        # GenerateSource su un blocco scrive direttamente il file indicato
        $b.GenerateSource((Join-Path $LogDir "$($Block)_da_blocco.awl"))
        "sorgente rigenerato dal blocco: $(Join-Path $LogDir "$($Block)_da_blocco.awl")"
    }
}
