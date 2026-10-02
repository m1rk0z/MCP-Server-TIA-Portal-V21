# tools\build.ps1 - compila gli strumenti C# di tools\ con il csc di .NET Framework (niente SDK).
#
#   .\tools\build.ps1                 Openness della V21
#   .\tools\build.ps1 -Portal V19     Openness della V19 (per gli strumenti scritti su V19, es. GraphicsTool)
#
# Gli eseguibili finiscono in tools\bin\. Gli strumenti che usano Openness vanno compilati
# sulla macchina con TIA Portal; S7Tool va a 32 bit, perche l'interfaccia COM di STEP 7 V5 e a 32 bit.

[CmdletBinding()]
param([string] $Portal = 'V21')

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Openness-Refs([string] $version) {
    foreach ($candidate in @("$env:ProgramFiles\Siemens\Automation\Portal $version\PublicAPI\$version\net48",
                             "$env:ProgramFiles\Siemens\Automation\Portal $version\PublicAPI\$version")) {
        if (Test-Path $candidate) { return @(Get-ChildItem $candidate -Filter 'Siemens.Engineering*.dll' | ForEach-Object { "-r:$($_.FullName)" }) }
    }
    return $null
}

$ok = 0; $failed = @()
foreach ($cs in Get-ChildItem $here -Recurse -Filter *.cs | Where-Object { $_.FullName -notlike "$out*" }) {
    $exe = Join-Path $out ($cs.BaseName + '.exe')
    $text = Get-Content $cs.FullName -Raw
    # Openness referenziato con using o con nomi completi (es. Siemens.Engineering.TiaPortal)
    $usesOpenness = $text -match 'Siemens\.Engineering\.'
    # Uno strumento scritto per una versione precisa ne ha il percorso nel sorgente (Portal V19, Portal V21)
    $version = if ($text -match 'Portal (V\d+)') { $Matches[1] } else { $Portal }
    $r = @()
    if ($usesOpenness) {
        $r = Openness-Refs $version
        if (-not $r) { $failed += "$($cs.Name) (Openness $version non trovato)"; continue }
    }
    $platform = if ($cs.BaseName -eq 'S7Tool') { 'x86' } else { 'x64' }
    $log = & $csc -nologo -target:exe "-platform:$platform" -optimize+ "-out:$exe" -r:Microsoft.CSharp.dll $r $cs.FullName 2>&1
    if ($LASTEXITCODE -eq 0) { $ok++; Write-Host "  OK    $($cs.Name)" }
    else { $failed += $cs.Name; Write-Host "  ERR   $($cs.Name)" -ForegroundColor Red; $log | Select-String 'error' | Select-Object -First 3 | ForEach-Object { "        $_" } }
}
Write-Host ""
Write-Host "compilati: $ok   falliti: $($failed.Count)   (in $out)"
if ($failed.Count) { $failed | ForEach-Object { "  - $_" } }
