# plc-write.ps1 - prova degli import PLC su una COPIA usa e getta di un progetto.
#
# Il progetto indicato viene copiato in una cartella temporanea e si lavora solo
# sulla copia, aperta senza interfaccia (nessuna conferma in TIA Portal). Non si
# salva mai: alla fine la copia viene cancellata. L'originale non viene aperto.
#
# Cosa controlla, per blocchi, tabelle di variabili, tipi di dati e sorgenti:
#   - senza overwrite un oggetto che esiste gia viene rifiutato;
#   - keep_safe vince su overwrite;
#   - con overwrite l'oggetto viene sostituito dov'era;
#   - un XML del genere sbagliato non arriva a TIA;
#   - una sorgente SCL nuova genera il suo blocco.
#
#   .\test\plc-write.ps1 -Project C:\percorso\Progetto\Progetto.ap21
#   .\test\plc-write.ps1 -Project ... -Plc PLC_1 -Keep     (lascia la copia e gli export)

param(
    [Parameter(Mandatory = $true)][string] $Project,
    [string] $Plc,
    [string] $Work = (Join-Path $env:TEMP 'tiamcp-plcwrite'),
    [switch] $Keep
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Project)) { throw "Progetto non trovato: $Project" }

# il server deve essere quello della versione del progetto (.ap19 -> bin\V19)
$exe = $env:TIA_MCP_SERVER
if (-not $exe) {
    $version = 'V' + ([System.IO.Path]::GetExtension($Project) -replace '\D', '')
    $exe = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) "bin\$version\TiaMcpServer.exe"
}
if (-not (Test-Path $exe)) { throw "Non compilato: manca $exe" }

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory $Work | Out-Null
$copyDir = Join-Path $Work 'project'
Write-Host "Copio il progetto in $copyDir ..."
Copy-Item (Split-Path -Parent $Project) $copyDir -Recurse
$copy = Join-Path $copyDir (Split-Path -Leaf $Project)
$out = Join-Path $Work 'export'

$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$proc = [System.Diagnostics.Process]::Start($psi)

$script:id = 0
$script:failed = 0

function Rpc([string] $json) {
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
    $line = $proc.StandardOutput.ReadLine()
    if ($null -eq $line) { throw 'Il server ha chiuso la connessione.' }
    return ($line | ConvertFrom-Json)
}

function Call([string] $name, [hashtable] $toolArgs) {
    $script:id++
    $payload = @{ jsonrpc = '2.0'; id = $script:id; method = 'tools/call'; params = @{ name = $name; arguments = $toolArgs } }
    $o = Rpc ($payload | ConvertTo-Json -Compress -Depth 8)
    if ($o.error) { throw "$name : $($o.error.message)" }
    $text = $o.result.content[0].text
    if ($o.result.isError) { return [pscustomobject]@{ isError = $true; text = $text } }
    return ($text | ConvertFrom-Json)
}

function Check([string] $what, [bool] $ok, $detail) {
    if ($ok) { Write-Host "  OK    $what" -ForegroundColor Green }
    else {
        $script:failed++
        Write-Host "  FAIL  $what" -ForegroundColor Red
        if ($detail) { Write-Host ("        " + ($detail | ConvertTo-Json -Compress -Depth 6)) -ForegroundColor DarkGray }
    }
}

try {
    [void](Rpc '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"plc-write","version":"1"}}}')

    Write-Host "== apertura della copia (senza interfaccia)"
    $r = Call 'tia_open_project' @{ path = $copy; with_ui = $false }
    Check 'progetto aperto' (-not $r.isError) $r
    if ($r.isError) { throw 'Impossibile aprire la copia.' }

    $p = @{}
    if ($Plc) { $p['plc'] = $Plc }
    else {
        $list = Call 'plc_list' @{}
        if (-not $list.plcs -or $list.plcs.Count -eq 0) { throw 'Il progetto non contiene PLC.' }
        $p['plc'] = $list.plcs[0].device
    }
    Write-Host "   PLC: $($p['plc'])"

    $blocks = (Call 'plc_blocks' ($p + @{ limit = 0 })).blocks | Where-Object { -not $_.know_how_protected }
    $block = $blocks | Where-Object { $_.type -eq 'FC' } | Select-Object -First 1
    if (-not $block) { $block = $blocks | Select-Object -First 1 }
    $scl = $blocks | Where-Object { $_.language -eq 'SCL' -and $_.type -in 'FC', 'FB' } | Select-Object -First 1

    Write-Host "== blocchi (XML): $($block.name)"
    $r = Call 'plc_export_blocks' ($p + @{ out_dir = (Join-Path $out 'blocks'); names = @($block.name) })
    Check 'export del blocco' ($r.exported -eq 1) $r
    $blockFile = $r.files[0].file

    $r = Call 'plc_import_blocks' ($p + @{ files = @($blockFile) })
    Check 'senza overwrite: rifiutato' ($r.imported -eq 0 -and $r.refused -eq 1) $r
    $r = Call 'plc_import_blocks' ($p + @{ files = @($blockFile); overwrite = $true; keep_safe = @($block.name) })
    Check 'keep_safe vince su overwrite' ($r.imported -eq 0 -and $r.refused -eq 1) $r
    $r = Call 'plc_import_blocks' ($p + @{ files = @($blockFile); overwrite = $true })
    Check 'con overwrite: sostituito' ($r.imported -eq 1 -and $r.objects[0].replaced -and $r.objects[0].group -eq $block.group) $r

    Write-Host "== tabelle di variabili"
    $r = Call 'plc_export_tag_tables' ($p + @{ out_dir = (Join-Path $out 'tags') })
    Check 'export delle tabelle' ($r.exported -ge 1 -and $r.failed -eq 0) $r
    if ($r.exported -ge 1) {
        $tableFile = $r.files[0].file
        $r = Call 'plc_import_blocks' ($p + @{ files = @($tableFile); overwrite = $true })
        Check 'XML di una tabella dato come blocco: rifiutato' ($r.imported -eq 0 -and $r.refused -eq 1) $r
        $r = Call 'plc_import_tag_tables' ($p + @{ files = @($tableFile) })
        Check 'senza overwrite: rifiutata' ($r.imported -eq 0 -and $r.refused -eq 1) $r
        $r = Call 'plc_import_tag_tables' ($p + @{ files = @($tableFile); overwrite = $true })
        Check 'con overwrite: sostituita' ($r.imported -eq 1 -and $r.objects[0].replaced) $r
    }

    Write-Host "== tipi di dati"
    $types = (Call 'plc_types' ($p + @{ limit = 0 })).types | Where-Object { -not $_.know_how_protected }
    if ($types) {
        $type = $types | Select-Object -First 1
        $r = Call 'plc_export_types' ($p + @{ out_dir = (Join-Path $out 'types'); names = @($type.name) })
        Check "export del tipo $($type.name)" ($r.exported -eq 1) $r
        $typeFile = $r.files[0].file
        $r = Call 'plc_import_types' ($p + @{ files = @($typeFile) })
        Check 'senza overwrite: rifiutato' ($r.imported -eq 0 -and $r.refused -eq 1) $r
        $r = Call 'plc_import_types' ($p + @{ files = @($typeFile); overwrite = $true })
        Check 'con overwrite: sostituito' ($r.imported -eq 1 -and $r.objects[0].replaced) $r
    } else { Write-Host '   nessun tipo di dati nel progetto: saltato' -ForegroundColor Yellow }

    Write-Host "== sorgenti"
    if ($scl) {
        $r = Call 'plc_export_sources' ($p + @{ out_dir = (Join-Path $out 'sources'); names = @($scl.name) })
        Check "sorgente generata da $($scl.name)" ($r.exported -eq 1) $r
        if ($r.exported -eq 1) {
            $sclFile = $r.files[0].file
            $r = Call 'plc_import_sources' ($p + @{ files = @($sclFile) })
            Check 'senza overwrite: rifiutata' ($r.imported -eq 0 -and $r.refused -eq 1) $r
            $r = Call 'plc_import_sources' ($p + @{ files = @($sclFile); overwrite = $true })
            Check 'con overwrite: blocco rigenerato' ($r.imported -eq 1) $r
        }
    } else { Write-Host '   nessun FC/FB in SCL nel progetto: giro export-import saltato' -ForegroundColor Yellow }

    $newDir = Join-Path $Work 'new'
    New-Item -ItemType Directory $newDir | Out-Null
    $newFile = Join-Path $newDir 'McpTest_FC.scl'
    @'
FUNCTION "McpTest_FC" : Void
VERSION : 0.1
   VAR_INPUT
      in1 : Int;
   END_VAR
   VAR_OUTPUT
      out1 : Int;
   END_VAR
BEGIN
	#out1 := #in1 + 1;
END_FUNCTION
'@ | Set-Content $newFile -Encoding utf8
    $r = Call 'plc_import_sources' ($p + @{ files = @($newFile) })
    Check 'sorgente SCL nuova: blocco creato' ($r.imported -eq 1 -and $r.sources[0].objects[0].name -eq 'McpTest_FC') $r
    $r = Call 'plc_blocks' ($p + @{ contains = 'McpTest_FC' })
    Check 'il blocco nuovo e nel progetto' ($r.count -eq 1) $r
    $r = Call 'plc_import_sources' ($p + @{ files = @($newFile) })
    Check 'seconda volta senza overwrite: rifiutata' ($r.imported -eq 0 -and $r.refused -eq 1) $r

    Write-Host "== compilazione (solo informativa)"
    $r = Call 'tia_compile' @{ target = $p['plc'] }
    if ($r.isError) { Write-Host "   $($r.text)" -ForegroundColor Yellow }
    else {
        foreach ($c in $r.results) {
            if ($c.skipped) { Write-Host "   $($c.target): saltato, $($c.skipped)" -ForegroundColor Yellow }
            else { Write-Host "   $($c.target): stato $($c.state), errori $($c.errors), avvisi $($c.warnings)" }
        }
    }

    Write-Host "== chiusura senza salvare"
    [void](Call 'tia_detach' @{})
}
finally {
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.WaitForExit(60000)) { $proc.Kill() }
    if (-not $Keep) {
        # TIA rilascia i file del progetto qualche secondo dopo la chiusura
        for ($i = 0; $i -lt 10 -and (Test-Path $Work); $i++) {
            Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $Work) { Start-Sleep -Seconds 3 }
        }
        if (Test-Path $Work) { Write-Host "Copia non cancellata, toglila a mano: $Work" -ForegroundColor Yellow }
    }
}

Write-Host ""
if ($script:failed -eq 0) { Write-Host 'Tutto OK' -ForegroundColor Green; exit 0 }
Write-Host "$($script:failed) controlli falliti" -ForegroundColor Red
exit 1
