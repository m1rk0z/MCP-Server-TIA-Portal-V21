param([string]$File,[string]$Out)
$ErrorActionPreference='Stop'
$w = New-Object -ComObject Word.Application
$w.Visible=$false; $w.DisplayAlerts=0
$d = $w.Documents.Open($File,$false,$true)
$d.SaveAs([ref]$Out,[ref]2)   # 2 = wdFormatText
$d.Close([ref]$false); $w.Quit()
"scritto $Out $((Get-Item $Out).Length) byte"
