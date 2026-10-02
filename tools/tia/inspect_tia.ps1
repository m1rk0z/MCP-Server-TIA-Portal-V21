$opennessPath = "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48"
$binPath = "C:\Program Files\Siemens\Automation\Portal V21\Bin"

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $args)
    $name = $args.Name.Split(',')[0]
    $path1 = Join-Path $opennessPath "$name.dll"
    if (Test-Path $path1) { return [System.Reflection.Assembly]::LoadFrom($path1) }
    $path2 = Join-Path $binPath "$name.dll"
    if (Test-Path $path2) { return [System.Reflection.Assembly]::LoadFrom($path2) }
    return $null
})

[System.Reflection.Assembly]::LoadFrom((Join-Path $opennessPath "Siemens.Engineering.Base.dll")) | Out-Null
[System.Reflection.Assembly]::LoadFrom((Join-Path $opennessPath "Siemens.Engineering.Step7.dll")) | Out-Null
[System.Reflection.Assembly]::LoadFrom((Join-Path $opennessPath "Siemens.Engineering.WinCC.dll")) | Out-Null

$processes = [Siemens.Engineering.TiaPortal]::GetProcesses()
if ($processes.Count -eq 0) {
    Write-Host "No TIA Portal process found!"
    exit 1
}

$proc = $processes[0]
Write-Host "Attaching to TIA Portal PID: $($proc.Id)..."
$tia = $proc.Attach()
$project = $tia.Projects[0]
Write-Host "Connected to Project: $($project.Name) (Path: $($project.Path))`n"

Write-Host "Devices in project:"
foreach ($dev in $project.Devices) {
    Write-Host " - Device: $($dev.Name) (Type: $($dev.TypeIdentifier))"
    foreach ($item in $dev.DeviceItems) {
        Write-Host "   * Item: $($item.Name) (Classification: $($item.Classification))"
    }
}
