# attrs.ps1 - scopre i nomi veri degli attributi di un oggetto.
#
# Openness non documenta un elenco unico: i nomi si leggono da GetAttributeInfos
# sull'oggetto vivo. Questo script usa gli strumenti generici del server
# (tia_browse e tia_get_attributes) per farlo in una sola sessione.
#
#   .\test\attrs.ps1 'Hmi/HMI_RT_1/ScreenFolder/Screens/HOME' 'Hmi/HMI_RT_1/Connections/PLC'

param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Paths)

$ErrorActionPreference = 'Stop'
$exe = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'bin\TiaMcpServer.exe'
if (-not $Paths -or $Paths.Count -eq 0) { throw "Indica almeno un percorso." }

$lines = New-Object System.Collections.ArrayList
[void]$lines.Add('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"attrs","version":"1"}}}')
[void]$lines.Add('{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tia_attach","arguments":{}}}')

$labels = @{}
$id = 1
foreach ($path in $Paths) {
    $id++
    $labels[$id] = $path
    $m = @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'
            params = @{ name = 'tia_get_attributes'; arguments = @{ path = $path } } }
    [void]$lines.Add(($m | ConvertTo-Json -Compress -Depth 8))
}

$in  = [System.IO.Path]::GetTempFileName()
$out = [System.IO.Path]::GetTempFileName()
$err = [System.IO.Path]::GetTempFileName()
Set-Content -Path $in -Value ($lines -join "`n") -Encoding utf8

Start-Process -FilePath $exe -ArgumentList '--read-only' -NoNewWindow -Wait `
    -RedirectStandardInput $in -RedirectStandardOutput $out -RedirectStandardError $err | Out-Null

foreach ($line in Get-Content $out) {
    if (-not $line.Trim()) { continue }
    $o = $line | ConvertFrom-Json
    if ($o.id -eq 0) { continue }
    if ($o.id -eq 1) { Write-Host ("attach: " + $o.result.content[0].text.Substring(0, [Math]::Min(200, $o.result.content[0].text.Length))) -ForegroundColor Magenta; continue }
    Write-Host ""
    Write-Host $labels[[int]$o.id] -ForegroundColor Cyan
    $text = $o.result.content[0].text
    if ($o.result.isError) { Write-Host "  $text" -ForegroundColor Yellow; continue }
    $parsed = $text | ConvertFrom-Json
    $parsed.attributes.PSObject.Properties | ForEach-Object {
        Write-Host ("  {0,-34} {1}" -f $_.Name, $_.Value)
    }
}
Remove-Item $in, $out, $err -ErrorAction SilentlyContinue
