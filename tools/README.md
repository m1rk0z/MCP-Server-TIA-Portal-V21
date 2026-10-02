# Strumenti

Piccoli programmi e script scritti durante lavori reali su TIA Portal (Openness), STEP 7 V5 e
progetti HMI. Ognuno fa una cosa sola; quasi tutti hanno in testa un commento che spiega cosa fanno
e **perché**. Sono complementari al server MCP: coprono quello che il server non espone
(grafiche di progetto, reflection sulle API, STEP 7 V5) o servono da riga di comando.

Sono stati ripuliti per la pubblicazione: niente percorsi, nomi o indirizzi di impianti. Dove
c'erano, ora c'è un argomento o una variabile d'ambiente. Sono stati esclusi gli strumenti che
scrivono nella CPU e quelli legati a un impianto preciso (generatori di schermate da un modello di
tag, prove di sequenze di comando).

```
.\tools\build.ps1              compila i .cs in tools\bin\ (Openness V21; -Portal V19 per la V19)
```

## Da sapere prima di usarli

- **Versione di TIA.** Gli strumenti Openness hanno nel codice il percorso standard della **V21**
  (`C:\Program Files\Siemens\Automation\Portal V21\...`); `GraphicsTool` quello della **V19**.
  Per un'altra versione si cambia il percorso nel sorgente e si ricompila.
- **Una sola sessione Openness.** Ogni `Attach()` chiede una conferma manuale a chi sta davanti a
  TIA Portal: gli strumenti fanno più operazioni in un'unica sessione. Per un lavoro continuativo
  conviene il server MCP, che si aggancia una volta sola.
- **Output.** Gli strumenti `Tia*` scrivono i log nella cartella corrente, o in `TIA_TOOLS_OUT`.
- **Strumenti che modificano il progetto:** `TiaProbe` (importa e poi rimuove schermate di prova in
  `ZZ_PROBE`), `ImportTl`, `TestFolders`, `s7_replace_compile.ps1`. Usarli su una copia del progetto.

## tia\ — TIA Portal Openness

| Strumento | Cosa fa |
|---|---|
| `setup_openness_permissions.ps1` | Da amministratore, dopo l'installazione di TIA: aggiunge l'utente ai gruppi Siemens TIA Openness / Engineer |
| `inspect_tia.ps1` | Ispezione rapida da PowerShell: si aggancia alla prima istanza ed elenca dispositivi e moduli |
| `TiaStato`, `TiaStato2` | Fotografia in sola lettura del progetto HMI: PLC e indirizzo a cui punta il pannello, dispositivo, schermata iniziale. `TiaStato2` esporta anche la connessione in XML, l'unico posto dove Openness mette l'indirizzo del partner |
| `TiaInspector` | Ispezione del progetto aperto |
| `TiaProbe` | Sonda dei tipi di oggetto accettati dall'import XML delle schermate (scrive, in `ZZ_PROBE`) |
| `TiaSchemaDump` | Esporta una schermata di riferimento (`ZZ_SCHEMA`) e le tabelle tag di sistema per ricavare lo schema XML vero. Sola lettura |
| `TiaCompileDiag`, `CompileHmi` | Compilano l'HMI e stampano ogni proprietà dei messaggi (spesso `Description` è vuota e il testo utile sta altrove). Nessun salvataggio |
| `ImportTl` | Importa una lista testi da un file XML (`ImportTl <file.xml>`) |
| `TestFolders` | Prova la creazione di cartelle schermate (`ZZ_TEST_FOLDER`) |
| `Reflect*` | Esplorano con la reflection tipi e metodi delle API Openness (WinCC, schermate, liste testi, cartelle): utili per scoprire cosa offre una versione nuova |

## hmi\ — schermate WinCC

| Strumento | Cosa fa |
|---|---|
| `ExportScreen` | Esporta una schermata in XML nella cartella corrente |
| `InspectHmi2` | Ispeziona tabelle tag, schermate, liste testi e modelli |
| `GraphicsTool` | Esporta e importa le grafiche di progetto (`list`, `export`, `import`, `archive`), che il server MCP non espone. V19 |
| `mockup.py` | Disegna le schermate esportate in un PDF **in scala reale** (TP700: 152,4 × 91,4 mm per 800 × 480 px): stampato al 100% è il pannello. `python mockup.py <prefisso> <uscita.pdf> [cartella xml]`, richiede `reportlab` |
| `render_screen.py` | Anteprima schematica PNG di una schermata esportata (riquadri, testi, tag). Richiede `Pillow` |
| `screen_inventory.py` | Inventario delle schermate esportate in una cartella (`<cartella> [filtro]`) |
| `read_map.py` | Legge i fogli di un `.xlsx` senza librerie esterne (`read_map.py <file.xlsx>`) |
| `mcp_call.py` | Esegue una sequenza di chiamate al server MCP da un file JSON, con `stop_on_error`. Server in `TIA_MCP_SERVER`, predefinito `bin\V21\TiaMcpServer.exe` |
| `tia_export.py` | Esporta schermate e tag via server MCP in una cartella (`TIA_EXPORT_OUT`). Predefinito il server V19 |

## step7\ — STEP 7 V5 (interfaccia COM, 32 bit)

| Strumento | Cosa fa | Scrive? |
|---|---|---|
| `S7Tool` | Legge un progetto STEP 7 V5 tramite l'interfaccia di automazione COM. Compilato x86, fa da ponte per chiamanti a 64 bit | No |
| `s7_inspect.ps1` | Simboli e sorgenti di un progetto (`<cartella> <regex>`) | No |
| `s7_symbols.ps1` | Simboli che corrispondono a un filtro su nome, indirizzo o commento (`<cartella> <programma> <regex>`) | No |
| `s7_export_symbols.ps1` | Esporta in `.sdf` la tabella simboli di ogni programma (`-OutDir <cartella> <progetto>...`) | No |
| `s7_gen_block.ps1` | Rigenera il sorgente AWL di uno o più blocchi (`<cartella> <programma> <out> <blocco>...`) | No |
| `s7_probe_source.ps1` | Mostra tipo e metodi COM di un sorgente | No |
| `s7_replace_compile.ps1` | Esporta il sorgente attuale (`_prima.awl`), lo sostituisce da file e compila | **Sì, il progetto** |
| `s7client.ps1` | Client S7comm minimale in PowerShell (`-IP -Rack -Slot -Port`), sola lettura | No |

Gli script PowerShell COM vanno eseguiti con **PowerShell a 32 bit**
(`C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`).

## office\ — documenti e dati

| Strumento | Cosa fa |
|---|---|
| `dump.ps1` | Estrae in testo tabellato i fogli di un file Excel (`-File -Sheet -Out`) |
| `word.ps1` | Converte un documento Word in testo |
| `topdf.ps1` | Converte in PDF tutti i documenti Word ed Excel di una cartella |
| `DbfDump` | Lettore dBASE III minimale (`--campi` elenca i campi). Sola lettura |
