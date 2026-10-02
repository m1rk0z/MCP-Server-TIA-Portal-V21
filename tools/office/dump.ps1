param([string]$File,[string]$Sheet,[string]$Out)
$ErrorActionPreference='Stop'
$ex = New-Object -ComObject Excel.Application
$ex.Visible=$false; $ex.DisplayAlerts=$false
$wb = $ex.Workbooks.Open($File,0,$true)
$sb = New-Object System.Text.StringBuilder
foreach($ws in $wb.Worksheets){
  if($Sheet -and $ws.Name -ne $Sheet){ continue }
  $u=$ws.UsedRange
  $r0=$u.Row; $c0=$u.Column; $nr=$u.Rows.Count; $nc=$u.Columns.Count
  [void]$sb.AppendLine("### FOGLIO [$($ws.Name)] r$r0 c$c0 $nr x $nc")
  $v = $u.Value2
  if($nr -eq 1 -and $nc -eq 1){ [void]$sb.AppendLine("1`t$v"); continue }
  for($i=1;$i -le $nr;$i++){
    $line = New-Object System.Text.StringBuilder
    $any=$false
    for($j=1;$j -le $nc;$j++){
      $c = $v[$i,$j]
      if($c -ne $null){ $any=$true; $c = ([string]$c) -replace "[`r`n`t]"," " }
      [void]$line.Append($c); [void]$line.Append("`t")
    }
    if($any){ [void]$sb.AppendLine("$($i+$r0-1)`t" + $line.ToString().TrimEnd("`t")) }
  }
}
$wb.Close($false); $ex.Quit()
[IO.File]::WriteAllText($Out,$sb.ToString(),[Text.Encoding]::UTF8)
"scritto $Out  $((Get-Item $Out).Length) byte"
