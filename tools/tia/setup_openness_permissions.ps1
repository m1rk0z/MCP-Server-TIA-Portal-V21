# Script di configurazione permessi Siemens TIA Portal Openness per TIA Portal V21
# Eseguire questo script in PowerShell avviato "Come Amministratore" dopo aver installato TIA Portal V21.

Write-Host "=== Configurazione Permessi TIA Portal Openness ===" -ForegroundColor Cyan

# 1. Aggiunta dell'utente corrente al gruppo Siemens TIA Openness
$currentUser = $env:USERNAME
try {
    Add-LocalGroupMember -Group "Siemens TIA Openness" -Member $currentUser -ErrorAction Stop
    Write-Host "[OK] Utente '$currentUser' aggiunto con successo al gruppo 'Siemens TIA Openness'." -ForegroundColor Green
} catch {
    Write-Host "[INFO] Utente '$currentUser' gia presente o gruppo non ancora creato: $($_.Exception.Message)" -ForegroundColor Yellow
}

try {
    Add-LocalGroupMember -Group "Siemens TIA Engineer" -Member $currentUser -ErrorAction Stop
    Write-Host "[OK] Utente '$currentUser' aggiunto con successo al gruppo 'Siemens TIA Engineer'." -ForegroundColor Green
} catch {
    Write-Host "[INFO] Gruppo 'Siemens TIA Engineer': $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "`nVerifica gruppi attuali:" -ForegroundColor Cyan
Get-LocalGroupMember -Group "Siemens TIA Openness" -ErrorAction SilentlyContinue | Format-Table -AutoSize

Write-Host "`nNOTA IMPORTANTE:" -ForegroundColor Magenta
Write-Host "Dopo aver eseguito questo script, e NECESSARIO riavviare il PC (o fare Logoff/Login) per rendere effettivi i permessi di gruppo." -ForegroundColor Magenta
