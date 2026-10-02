param([string]$SrcDir, [string]$OutDir)
$ErrorActionPreference='Continue'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$word = New-Object -ComObject Word.Application; $word.Visible=$false; $word.DisplayAlerts=0
$excel = New-Object -ComObject Excel.Application; $excel.Visible=$false; $excel.DisplayAlerts=$false
Get-ChildItem -Path $SrcDir -File | Where-Object { $_.Extension -match '^\.(doc|docx|xls|xlsx|rtf)$' } | ForEach-Object {
  $out = Join-Path $OutDir ($_.BaseName + '.pdf')
  if (Test-Path $out) { "gia fatto  $($_.Name)"; return }
  try {
    if ($_.Extension -match 'doc|rtf') {
      $d = $word.Documents.Open($_.FullName,$false,$true)
      $d.ExportAsFixedFormat($out, 17)
      $d.Close([ref]$false)
    } else {
      $wb = $excel.Workbooks.Open($_.FullName,0,$true)
      $wb.ExportAsFixedFormat(0, $out)
      $wb.Close($false)
    }
    "OK  {0}  ->  {1:N0} byte" -f $_.Name, (Get-Item $out).Length
  } catch { "ERRORE  {0}: {1}" -f $_.Name, $_.Exception.Message }
}
$word.Quit(); $excel.Quit()
