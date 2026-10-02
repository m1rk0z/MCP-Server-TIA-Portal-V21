# remote.ps1 - prova agente e client senza TIA Portal, con un finto server (FakeServer.cs).
#
#   .\build.ps1 ; .\test\remote.ps1
#
# Avvia TiaAgent su localhost con dati in una cartella temporanea (TIA_AGENT_DATA), un finto
# TiaMcpServer come "V99", e pilota TiaMcpClient via stdin/stdout come farebbe Claude Code.
# Non serve essere amministratori: l'agente ascolta solo su localhost.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ("tia-remote-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
# Porta libera fuori dagli intervalli che Windows riserva (Hyper-V, WinNAT): li dentro HttpListener fallisce.
$excluded = netsh int ipv4 show excludedportrange protocol=tcp | Select-String '^\s+\d+\s+\d+' |
    ForEach-Object { $n = ($_ -split '\s+' | Where-Object { $_ }); , @([int]$n[0], [int]$n[1]) }
$port = 28766
while (($excluded | Where-Object { $port -ge $_[0] -and $port -le $_[1] }) -or (Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue)) { $port++ }
$token = 'test-' + [Guid]::NewGuid().ToString('N')
$failures = 0

function Check($name, [bool] $ok, $detail) {
    if ($ok) { Write-Host "  OK    $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

# --- agente in una cartella temporanea, con un finto server TIA "V99"
$agentDir = Join-Path $work 'agent'
Copy-Item (Join-Path $root 'bin\agent') $agentDir -Recurse
New-Item -ItemType Directory (Join-Path $agentDir 'V99') | Out-Null
& $csc -nologo -target:exe "-out:$(Join-Path $agentDir 'V99\TiaMcpServer.exe')" (Join-Path $root 'test\FakeServer.cs') (Join-Path $root 'src\Json.cs')
if ($LASTEXITCODE -ne 0) { throw 'compilazione del finto server fallita' }

$data = Join-Path $work 'data'
New-Item -ItemType Directory $data | Out-Null
@{ Port = $port; ListenHost = 'localhost'; Token = $token; AccessMode = 'read-only'; AllowedClients = @(); WorkDir = (Join-Path $data 'work'); KeepAliveSeconds = 6 } |
    ConvertTo-Json | Set-Content (Join-Path $data 'agent.json') -Encoding utf8
$env:TIA_AGENT_DATA = $data
$agent = Start-Process (Join-Path $agentDir 'TiaAgent.exe') -PassThru
for ($i = 0; $i -lt 20 -and -not (Get-ChildItem "$data\logs\*.log" -ErrorAction SilentlyContinue | Select-String 'listening on|start failed' -Quiet); $i++) { Start-Sleep -Milliseconds 500 }
$startLog = Get-Content "$data\logs\*.log" -ErrorAction SilentlyContinue
if (-not ($startLog -match 'listening on')) {
    Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue
    Write-Host "L'agente non e partito:" -ForegroundColor Red
    $startLog | ForEach-Object { "  $_" }
    exit 1
}

try {
    Write-Host "== agente"
    $h = @{ Authorization = "Bearer $token" }
    $health = Invoke-RestMethod "http://localhost:$port/api/health" -Headers $h
    Check 'health' ($health.success -and ($health.result.servers -contains 'V99')) ($health | ConvertTo-Json -Depth 5)
    $code = try { Invoke-WebRequest "http://localhost:$port/api/health" -UseBasicParsing | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
    Check 'senza token -> 401' ($code -eq 401) $code
    $code = try { Invoke-WebRequest "http://localhost:$port/api/health" -Headers @{ Authorization = 'Bearer sbagliato' } -UseBasicParsing | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
    Check 'token errato -> 401' ($code -eq 401) $code

    Write-Host "== client (come Claude Code)"
    $local = Join-Path $work 'pc'
    New-Item -ItemType Directory "$local\da_importare" | Out-Null
    Set-Content "$local\da_importare\A.xml" '<Screen Name="A" />' -Encoding utf8
    Set-Content "$local\da_importare\B.xml" '<Screen Name="B" />' -Encoding utf8

    $requests = @(
        @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 't'; version = '1' } } },
        @{ jsonrpc = '2.0'; method = 'notifications/initialized' },
        @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' },
        @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'fake_info'; arguments = @{} } },
        @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'fake_export'; arguments = @{ out_dir = "$local\export"; names = @('HOME', 'ALLARMI') } } },
        @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{ name = 'fake_import'; arguments = @{ files = @("$local\da_importare\A.xml") } } },
        @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'fake_import'; arguments = @{ dir = "$local\da_importare" } } },
        @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'fake_value'; arguments = @{ value = '2024-05-01T10:00:00' } } }
    )
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $root 'bin\client\TiaMcpClient.exe'), "--agent http://localhost:$port --version V99"
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['TIA_MCP_AGENT_TOKEN'] = $token
    $psi.StandardOutputEncoding = New-Object Text.UTF8Encoding $false
    $client = [Diagnostics.Process]::Start($psi)
    $replies = @{}
    foreach ($r in $requests) {
        $client.StandardInput.WriteLine(($r | ConvertTo-Json -Depth 10 -Compress))
        $client.StandardInput.Flush()
        if ($r.ContainsKey('id')) { $o = $client.StandardOutput.ReadLine() | ConvertFrom-Json; $replies[[int]$o.id] = $o }
    }
    $client.StandardInput.Close()
    $client.WaitForExit(15000) | Out-Null

    function Payload($id) { $replies[$id].result.content[0].text | ConvertFrom-Json }

    Check 'initialize' ($replies[1].result.serverInfo.name -eq 'fake-tia') ($replies[1] | ConvertTo-Json -Depth 5)
    Check 'tools/list' (@($replies[2].result.tools).Count -eq 4) ''
    $info = Payload 3
    Check 'server avviato in sola lettura dall agente' ($info.read_only -eq $true) ($info | ConvertTo-Json)
    $exp = Payload 4
    Check 'export scaricato sul PC' ((Test-Path "$local\export\HOME.xml") -and (Test-Path "$local\export\ALLARMI.xml")) ''
    Check 'percorsi riscritti verso il PC' (($exp.out_dir -eq "$local\export") -and ($exp.files[0] -like "$local\export\*")) ($exp | ConvertTo-Json)
    $imp = Payload 5
    Check 'file caricato sulla VM e letto' (($imp.imported[0].content -like '*Name="A"*') -and ($imp.imported[0].path -eq "$local\da_importare\A.xml")) ($imp | ConvertTo-Json -Depth 5)
    $imp = Payload 6
    Check 'cartella caricata (dir)' (@($imp.imported).Count -eq 2) ($imp | ConvertTo-Json -Depth 5)
    Check 'valori conservati' ((Payload 7).value -eq '2024-05-01T10:00:00') ((Payload 7) | ConvertTo-Json)

    Start-Sleep 1
    $health = Invoke-RestMethod "http://localhost:$port/api/health" -Headers $h
    Check 'sessione chiusa alla fine del client' (@($health.result.sessions).Count -eq 0) ($health.result.sessions | ConvertTo-Json)
    Check 'processo del server terminato' (-not (Get-Process -Id $info.pid -ErrorAction SilentlyContinue)) $info.pid
    Check 'cartella della sessione eliminata' (-not (Test-Path $info.cwd)) $info.cwd

    Write-Host "== keepalive (client chiuso a forza, come fa Claude Code)"
    $psi.EnvironmentVariables['TIA_MCP_PING_SECONDS'] = '2'
    $client = [Diagnostics.Process]::Start($psi)
    function Call($r) {
        $client.StandardInput.WriteLine(($r | ConvertTo-Json -Depth 10 -Compress)); $client.StandardInput.Flush()
        $client.StandardOutput.ReadLine() | ConvertFrom-Json
    }
    $first = Call @{ jsonrpc = '2.0'; id = 1; method = 'tools/call'; params = @{ name = 'fake_info'; arguments = @{} } }
    $info = $first.result.content[0].text | ConvertFrom-Json
    Start-Sleep 10   # oltre KeepAliveSeconds (6): resta viva solo grazie ai ping
    $again = Call @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'fake_info'; arguments = @{} } }
    $info2 = $again.result.content[0].text | ConvertFrom-Json
    Check 'sessione viva con i ping, oltre il timeout' ($info2.pid -eq $info.pid) "$($info.pid) -> $($info2.pid)"
    $client.Kill()   # niente EOF su stdin: il client non chiude la sessione
    $client.WaitForExit(5000) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep 1
        $health = Invoke-RestMethod "http://localhost:$port/api/health" -Headers $h
        if (@($health.result.sessions).Count -eq 0) { break }
    }
    Check 'sessione abbandonata chiusa dall agente' (@($health.result.sessions).Count -eq 0) ($health.result.sessions | ConvertTo-Json)
    Check 'server della sessione abbandonata terminato' (-not (Get-Process -Id $info.pid -ErrorAction SilentlyContinue)) $info.pid
}
finally {
    Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\TIA_AGENT_DATA
    Start-Sleep 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failures -eq 0) { Write-Host "Tutto OK" -ForegroundColor Green; exit 0 }
Write-Host "$failures verifiche fallite" -ForegroundColor Red; exit 1
