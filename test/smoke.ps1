# smoke.ps1 - prova il server senza un client MCP.
#
# Gli manda in pasto una manciata di messaggi JSON-RPC e stampa le risposte.
# Non tocca nessun progetto: si ferma a tia_instances, che elenca le istanze di
# TIA Portal in esecuzione senza agganciarsi a nessuna, quindi non fa comparire
# nessuna finestra di conferma.
#
#   .\test\smoke.ps1                     prova di base
#   .\test\smoke.ps1 -Attach             aggancia davvero (UNA conferma in TIA)

param([switch] $Attach)

$ErrorActionPreference = 'Stop'
$exe = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'bin\TiaMcpServer.exe'
if (-not (Test-Path $exe)) { throw "Non compilato: manca $exe. Lancia prima .\build.cmd" }

$messages = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"tia_session","arguments":{}}}',
    '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"tia_instances","arguments":{}}}',
    '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"hmi_item_types","arguments":{"contains":"Rectangle"}}}',
    '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"hmi_panels","arguments":{}}}'
)

if ($Attach) {
    $messages += '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"tia_attach","arguments":{}}}'
    $messages += '{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"hmi_panels","arguments":{}}}'
    $messages += '{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"hmi_info","arguments":{}}}'
    $messages += '{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"tia_devices","arguments":{}}}'
}

$in  = [System.IO.Path]::GetTempFileName()
$out = [System.IO.Path]::GetTempFileName()
$err = [System.IO.Path]::GetTempFileName()
Set-Content -Path $in -Value ($messages -join "`n") -Encoding utf8

$p = Start-Process -FilePath $exe -ArgumentList '--read-only' -NoNewWindow -PassThru -Wait `
        -RedirectStandardInput $in -RedirectStandardOutput $out -RedirectStandardError $err

Write-Host "--- stderr ---" -ForegroundColor DarkGray
Get-Content $err | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

Write-Host ""
Write-Host "--- risposte ---" -ForegroundColor Cyan
$n = 0
foreach ($line in Get-Content $out) {
    if (-not $line.Trim()) { continue }
    $n++
    $o = $line | ConvertFrom-Json
    if ($o.error) {
        Write-Host ("id {0}  ERRORE {1}: {2}" -f $o.id, $o.error.code, $o.error.message) -ForegroundColor Red
        continue
    }
    if ($o.result.tools) {
        Write-Host ("id {0}  tools/list: {1} strumenti" -f $o.id, $o.result.tools.Count) -ForegroundColor Green
        $o.result.tools | ForEach-Object { Write-Host ("        {0}" -f $_.name) -ForegroundColor DarkGray }
        continue
    }
    if ($o.result.content) {
        $text = $o.result.content[0].text
        $flag = if ($o.result.isError) { 'RIFIUTATO' } else { 'ok' }
        $color = if ($o.result.isError) { 'Yellow' } else { 'Green' }
        if ($text.Length -gt 700) { $text = $text.Substring(0, 700) + ' [...]' }
        Write-Host ("id {0}  {1}: {2}" -f $o.id, $flag, $text) -ForegroundColor $color
        continue
    }
    Write-Host ("id {0}  {1}" -f $o.id, ($o.result | ConvertTo-Json -Compress -Depth 4)) -ForegroundColor Green
}

Write-Host ""
Write-Host ("{0} risposte, codice di uscita {1}" -f $n, $p.ExitCode)
Remove-Item $in, $out, $err -ErrorAction SilentlyContinue
