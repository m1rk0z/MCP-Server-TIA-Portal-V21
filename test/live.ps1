# live.ps1 - prova completa contro il progetto aperto in TIA Portal.
#
# Tutto in UNA sola esecuzione del server, quindi UNA sola conferma manuale in
# TIA Portal: e il punto dell'intero progetto, e vale anche per le sue prove.
#
# Gira in sola lettura (--read-only): esporta in una cartella temporanea, non
# importa e non salva niente.

param([string] $Panel, [string] $OutDir = (Join-Path $env:TEMP 'tiamcp-live'))

$ErrorActionPreference = 'Stop'
$exe = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'bin\TiaMcpServer.exe'
if (-not (Test-Path $exe)) { throw "Non compilato: manca $exe" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$id = 0
$msgs = New-Object System.Collections.ArrayList
function Send([string] $name, [hashtable] $toolArgs) {
    $script:id++
    $payload = @{ jsonrpc = '2.0'; id = $script:id; method = 'tools/call'
                  params = @{ name = $name; arguments = $toolArgs } }
    [void]$msgs.Add(($payload | ConvertTo-Json -Compress -Depth 8))
    [void]$msgs.Add("# $($script:id) $name")   # segnalino, tolto piu sotto
}

[void]$msgs.Add('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"live","version":"1"}}}')

$p = @{}
if ($Panel) { $p['panel'] = $Panel }

Send 'tia_attach'          @{}
Send 'tia_session'         @{}
Send 'tia_devices'         @{}
Send 'hmi_panels'          @{}
Send 'hmi_info'            $p
Send 'hmi_screens'         $p
Send 'hmi_screens'         ($p + @{ names = @('HOME','ARRESTO_TOTALE'); details = $true })
Send 'hmi_connections'     $p
Send 'hmi_tag_tables'      $p
Send 'hmi_tags'            ($p + @{ limit = 4; details = $true })
Send 'hmi_tags'            ($p + @{ limit = 3; details = $false })
Send 'hmi_text_lists'      $p
Send 'hmi_alarms'          $p
Send 'plc_list'            @{}
Send 'plc_blocks'          @{ limit = 5 }
Send 'plc_tags'            @{ limit = 5 }
Send 'tia_browse'          @{ path = '' }
Send 'tia_browse'          @{ path = 'Devices' }
Send 'hmi_export_screens'  ($p + @{ out_dir = $OutDir; names = @('__non_esiste__') })
Send 'tia_save'            @{}                       # deve essere rifiutato: sola lettura

$labels = @{}
$lines  = New-Object System.Collections.ArrayList
foreach ($m in $msgs) {
    if ($m.StartsWith('#')) {
        $parts = $m.Substring(2).Split(' ', 2)
        $labels[[int]$parts[0]] = $parts[1]
    } else {
        [void]$lines.Add($m)
    }
}

$in  = [System.IO.Path]::GetTempFileName()
$out = [System.IO.Path]::GetTempFileName()
$err = [System.IO.Path]::GetTempFileName()
Set-Content -Path $in -Value ($lines -join "`n") -Encoding utf8

Write-Host "Accetta la richiesta di accesso in TIA Portal (una sola volta)..." -ForegroundColor Yellow
$proc = Start-Process -FilePath $exe -ArgumentList '--read-only' -NoNewWindow -PassThru -Wait `
            -RedirectStandardInput $in -RedirectStandardOutput $out -RedirectStandardError $err

Write-Host ""
foreach ($line in Get-Content $out) {
    if (-not $line.Trim()) { continue }
    $o = $line | ConvertFrom-Json
    if ($o.id -eq 0) { Write-Host "initialize: ok" -ForegroundColor Green; continue }
    $name = $labels[[int]$o.id]

    if ($o.error) { Write-Host ("{0,-22} ERRORE {1}" -f $name, $o.error.message) -ForegroundColor Red; continue }

    $text = $o.result.content[0].text
    if ($o.result.isError) {
        Write-Host ("{0,-22} rifiutato: {1}" -f $name, $text) -ForegroundColor Yellow
    } else {
        if ($text.Length -gt 400) { $text = $text.Substring(0, 400) + ' [...]' }
        Write-Host ("{0,-22} {1}" -f $name, $text) -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "--- stderr ---" -ForegroundColor DarkGray
Get-Content $err | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
Remove-Item $in, $out, $err -ErrorAction SilentlyContinue
