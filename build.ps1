# build.ps1 - compila server, agente e client senza SDK e senza NuGet.
#
# Il compilatore C# usato e quello che sta dentro .NET Framework 4.8, presente su
# ogni macchina di engineering. Niente da scaricare, niente da ripristinare: e la
# ragione per cui il progetto non ha un .csproj come unica via di build.
#
#   .\build.ps1                    server per ogni TIA Portal installato + agente + client
#   .\build.ps1 -Portal V21        server solo per la V21 (bin\V21\TiaMcpServer.exe)
#   .\build.ps1 -OpennessPath 'D:\...\PublicAPI\V21\net48' -Portal V21
#   .\build.ps1 -Clean             cancella bin\ e ricompila
#
# Uscite:
#   bin\V19\TiaMcpServer.exe, bin\V21\TiaMcpServer.exe   server MCP (stdio), uno per versione di Openness
#   bin\agent\TiaAgent.exe                              agente per la macchina con TIA Portal (VM)
#   bin\client\TiaMcpClient.exe                         server MCP sul PC di Claude Code che inoltra all'agente
#
# Il server serve TIA Portal installato (le assembly di Openness); agente e client no:
# su un PC senza TIA si compilano solo loro.

[CmdletBinding()]
param(
    [string] $Portal,
    [string] $OpennessPath,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin  = Join-Path $root 'bin'

# ------------------------------------------------------------------ compilatore

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "csc.exe di .NET Framework 4.x non trovato. Installa .NET Framework 4.8."
}
Write-Host "csc      : $csc"

if ($Clean -and (Test-Path $bin)) { Remove-Item -Recurse -Force $bin }

function Write-Config($exe) {
    # Il .exe.config fissa il runtime a .NET Framework 4.8: Openness non gira su altro.
    $config = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
  <runtime>
    <gcServer enabled="false" />
  </runtime>
</configuration>
'@
    Set-Content -Path "$exe.config" -Value $config -Encoding utf8
}

function Compile($exe, $target, $platform, $sources, $refs, $defines) {
    $dir = Split-Path -Parent $exe
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $opts = @('-nologo', "-target:$target", "-platform:$platform", '-optimize+', "-out:$exe")
    if ($defines) { $opts += "-define:$defines" }
    & $csc @opts $refs $sources
    if ($LASTEXITCODE -ne 0) { throw "Compilazione di $(Split-Path -Leaf $exe) fallita (codice $LASTEXITCODE)." }
    Write-Config $exe
    Write-Host "Fatto    : $exe"
}

# -------------------------------------------------------- assembly di Openness

# Cartella delle assembly di una versione: PublicAPI\Vxx\net48 (V20+) oppure PublicAPI\Vxx (V19).
function Find-Openness([string] $version) {
    foreach ($base in @("$env:ProgramFiles\Siemens\Automation", "${env:ProgramFiles(x86)}\Siemens\Automation")) {
        $api = Join-Path $base "Portal $version\PublicAPI\$version"
        foreach ($candidate in @((Join-Path $api 'net48'), $api)) {
            if (Test-Path (Join-Path $candidate 'Siemens.Engineering.Base.dll')) { return $candidate }
            if (Test-Path (Join-Path $candidate 'Siemens.Engineering.dll'))      { return $candidate }
        }
    }
    return $null
}

function Installed-Portals {
    $found = @()
    foreach ($base in @("$env:ProgramFiles\Siemens\Automation", "${env:ProgramFiles(x86)}\Siemens\Automation")) {
        if (-not (Test-Path $base)) { continue }
        foreach ($d in Get-ChildItem -Path $base -Directory) {
            if ($d.Name -notmatch '^Portal (V\d+)$') { continue }
            $v = $Matches[1]   # letto subito dopo il -match di questa cartella
            if ((Find-Openness $v) -and ($found -notcontains $v)) { $found += $v }
        }
    }
    return $found | Sort-Object
}

# Dalla V21 Siemens.Engineering.dll e spezzata in piu assembly. Sulle versioni
# precedenti c'e un unico file: si referenzia quello che si trova.
$wanted = @(
    'Siemens.Engineering.Base.dll',
    'Siemens.Engineering.WinCC.dll',
    'Siemens.Engineering.WinCC.Extension.dll',
    'Siemens.Engineering.WinCCUnified.dll',
    'Siemens.Engineering.HmiUnified.dll',
    'Siemens.Engineering.Step7.dll',
    'Siemens.Engineering.dll',
    'Siemens.Engineering.Hmi.dll'
)

$serverSources = Get-ChildItem -Path (Join-Path $root 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName }

function Build-Server([string] $version, [string] $api) {
    Write-Host ""
    Write-Host "== server $version  (Openness: $api)"
    $refs = @()
    foreach ($w in $wanted) {
        $p = Join-Path $api $w
        if (Test-Path $p) { $refs += "-r:$p" }
    }
    if ($refs.Count -eq 0) { throw "Nessuna assembly Siemens.Engineering trovata in $api" }
    # PORTAL_Vxx: la build cerca prima le assembly della propria versione (Openness.cs).
    Compile (Join-Path $bin "$version\TiaMcpServer.exe") 'exe' 'x64' $serverSources $refs "PORTAL_$version"
}

# ------------------------------------------------------------------ server

if ($OpennessPath) {
    if (-not $Portal) { throw "Con -OpennessPath indica anche -Portal (es. -Portal V21)." }
    if (-not (Test-Path $OpennessPath)) { throw "Percorso Openness inesistente: $OpennessPath" }
    Build-Server $Portal $OpennessPath
}
elseif ($Portal) {
    $api = Find-Openness $Portal
    if (-not $api) { throw "Openness di TIA Portal $Portal non trovato. Indica la cartella con -OpennessPath." }
    Build-Server $Portal $api
}
else {
    $portals = @(Installed-Portals)
    if ($portals.Count -eq 0) {
        Write-Warning "Nessun TIA Portal installato: compilo solo agente e client (il server va compilato sulla macchina con TIA)."
    }
    foreach ($v in $portals) { Build-Server $v (Find-Openness $v) }
}

# ------------------------------------------------------- agente e client

$json = Join-Path $root 'src\Json.cs'

Write-Host ""
Write-Host "== agente"
$agentSources = @(Get-ChildItem -Path (Join-Path $root 'agent') -Filter *.cs | ForEach-Object { $_.FullName }) + $json
Compile (Join-Path $bin 'agent\TiaAgent.exe') 'winexe' 'anycpu' $agentSources @('-r:System.Windows.Forms.dll', '-r:System.Drawing.dll') $null
Copy-Item (Join-Path $root 'agent\dist\*') (Join-Path $bin 'agent') -Force

Write-Host ""
Write-Host "== client"
$clientSources = @(Get-ChildItem -Path (Join-Path $root 'client') -Filter *.cs | ForEach-Object { $_.FullName }) + $json
Compile (Join-Path $bin 'client\TiaMcpClient.exe') 'exe' 'anycpu' $clientSources @() $null

# ------------------------------------------------- pacchetto per la VM

# Agente + server di ogni versione compilata: e la cartella che setup.cmd installa.
$servers = @(Get-ChildItem -Path $bin -Directory | Where-Object { $_.Name -match '^V\d+$' -and (Test-Path (Join-Path $_.FullName 'TiaMcpServer.exe')) })
if ($servers.Count -gt 0) {
    $stage = Join-Path $bin 'package\TiaAgent'
    if (Test-Path (Join-Path $bin 'package')) { Remove-Item (Join-Path $bin 'package') -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item (Join-Path $bin 'agent\*') $stage -Recurse
    foreach ($s in $servers) { Copy-Item $s.FullName (Join-Path $stage $s.Name) -Recurse }
    $zip = Join-Path $bin 'TiaAgent.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host ""
    Write-Host "Pacchetto: $zip  (server: $(($servers | ForEach-Object Name) -join ', '))"
}

Write-Host ""
Write-Host "Provalo con:  .\test\smoke.ps1   (server)   .\test\remote.ps1   (agente + client)"
