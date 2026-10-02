# s7_probe_source.ps1 - mostra tipo e metodi COM di un sorgente S7 (per capire come reimportarlo e compilarlo).
param([string]$ProjectDir, [string]$Program, [string]$SourceName)
$s = New-Object -ComObject Simatic.Simatic.1
$s.UnattendedServerMode = $true
$prj = $null
foreach ($p in $s.Projects) { if ($p.LogPath -eq $ProjectDir) { $prj = $p } }
foreach ($x in $prj.Programs) { if ($x.Name -eq $Program -and $x.Type -eq 1327361) { $pg = $x } }
foreach ($c in $pg.Next) { if ($c.Name -match "Sorgenti|Sources") { $srcs = $c } }
"container: $($srcs.Name) type=$($srcs.Type)"
foreach ($x in $srcs.Next) {
    if ($x.Name -eq $SourceName) {
        "source: $($x.Name) type=$($x.Type) concrete=$($x.ConcreteType)"
        $x | Get-Member | Format-Table -AutoSize Name, MemberType, Definition | Out-String -Width 200
    }
}
"--- container.Next members"
$srcs.Next | Get-Member -MemberType Method | Format-Table -AutoSize Name, Definition | Out-String -Width 200
