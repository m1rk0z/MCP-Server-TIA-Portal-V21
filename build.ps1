# build.ps1 - compila il server senza SDK e senza NuGet.
#
# Serve solo Windows con TIA Portal installato: il compilatore C# usato e quello
# che sta dentro .NET Framework 4.8, presente su ogni macchina di engineering.
# Niente da scaricare, niente da ripristinare: e la ragione per cui il progetto
# non ha un .csproj come unica via di build.
#
#   .\build.ps1                 compila
#   .\build.ps1 -Clean          cancella bin\ e ricompila
#   .\build.ps1 -OpennessPath 'D:\...\PublicAPI\V21\net48'

[CmdletBinding()]
param(
    [string] $OpennessPath,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root 'bin'
$exe  = Join-Path $out 'TiaMcpServer.exe'

# ------------------------------------------------------------------ compilatore

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "csc.exe di .NET Framework 4.x non trovato. Installa .NET Framework 4.8."
}

# -------------------------------------------------------- assembly di Openness

function Find-Openness {
    if ($OpennessPath) {
        if (-not (Test-Path $OpennessPath)) { throw "Percorso Openness inesistente: $OpennessPath" }
        return $OpennessPath
    }
    if ($env:TIA_OPENNESS_PATH) {
        $first = ($env:TIA_OPENNESS_PATH -split ';')[0]
        if (Test-Path $first) { return $first }
    }

    $roots = @()
    foreach ($base in @("$env:ProgramFiles\Siemens\Automation", "${env:ProgramFiles(x86)}\Siemens\Automation")) {
        if (Test-Path $base) {
            $roots += Get-ChildItem -Path $base -Directory |
                      Where-Object { $_.Name -like 'Portal*' } |
                      Sort-Object Name -Descending
        }
    }

    foreach ($r in $roots) {
        $api = Join-Path $r.FullName 'PublicAPI'
        if (-not (Test-Path $api)) { continue }
        $versions = Get-ChildItem -Path $api -Directory | Sort-Object Name -Descending
        foreach ($v in $versions) {
            foreach ($candidate in @((Join-Path $v.FullName 'net48'), $v.FullName)) {
                if (Test-Path (Join-Path $candidate 'Siemens.Engineering.Base.dll')) { return $candidate }
                if (Test-Path (Join-Path $candidate 'Siemens.Engineering.dll'))      { return $candidate }
            }
        }
    }
    throw "Assembly Openness non trovate. Indica la cartella con -OpennessPath."
}

$api = Find-Openness
Write-Host "Openness : $api"
Write-Host "csc      : $csc"

# Dalla V21 Siemens.Engineering.dll e spezzata in piu assembly. Sulle versioni
# precedenti c'e un unico file: si referenzia quello che si trova.
$wanted = @(
    'Siemens.Engineering.Base.dll',
    'Siemens.Engineering.WinCC.dll',
    'Siemens.Engineering.WinCC.Extension.dll',
    'Siemens.Engineering.WinCCUnified.dll',
    'Siemens.Engineering.Step7.dll',
    'Siemens.Engineering.dll',
    'Siemens.Engineering.Hmi.dll'
)
$refs = @()
foreach ($w in $wanted) {
    $p = Join-Path $api $w
    if (Test-Path $p) { $refs += "-r:$p" }
}
if ($refs.Count -eq 0) { throw "Nessuna assembly Siemens.Engineering trovata in $api" }

# ------------------------------------------------------------------ compilazione

if ($Clean -and (Test-Path $out)) { Remove-Item -Recurse -Force $out }
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }

$sources = Get-ChildItem -Path (Join-Path $root 'src') -Filter *.cs -Recurse |
           ForEach-Object { $_.FullName }
Write-Host ("sorgenti : {0} file" -f $sources.Count)

& $csc -nologo -target:exe -platform:x64 -optimize+ "-out:$exe" $refs $sources
if ($LASTEXITCODE -ne 0) { throw "Compilazione fallita (codice $LASTEXITCODE)." }

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

Write-Host ""
Write-Host "Fatto: $exe"
Write-Host "Provalo con:  .\test\smoke.ps1"
