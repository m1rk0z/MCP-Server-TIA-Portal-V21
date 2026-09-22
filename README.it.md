# Server MCP per TIA Portal V21 (Openness)

Un server MCP che pilota **TIA Portal** attraverso Openness e funziona con
**tutte e due le famiglie HMI**: WinCC **classico** (Comfort, Advanced,
Professional) e WinCC **Unified**.

*[Read this file in English](README.md)*

---

## Perche esiste

Ogni `Attach()` a TIA Portal costa una **conferma manuale** a chi sta davanti al
video. Un programma a riga di comando per ogni operazione vuol dire una finestra
da accettare per ogni operazione: accetta, aspetta, accetta, aspetta.

Questo server si aggancia **una volta** e tiene la sessione viva finche il client
MCP resta collegato. Un clic, e poi un'intera sessione di lavoro: elenca le
schermate, esportale, correggi l'XML, reimportale, compila, salva.

Il resto viene da li.

## Cosa lo distingue

Quasi tutti i server MCP per TIA Portal coprono una sola famiglia HMI e si
rompono in silenzio sull'altra, perche i due modelli a oggetti non hanno quasi
niente in comune:

| | WinCC classico | WinCC Unified |
|---|---|---|
| namespace | `Siemens.Engineering.Hmi` | `Siemens.Engineering.HmiUnified` |
| oggetto radice | `HmiTarget` | `HmiSoftware` |
| creare una schermata | **impossibile**, non c'e `Screens.Create()` | `Screens.Create(nome)` |
| modificare una schermata | esporta XML, modifica, reimporta | a oggetti, proprieta per proprieta |
| import/export schermate | si, per file, con `ImportOptions` | **non esiste** |
| import/export tag | per file | per **cartella**, senza `ImportOptions` |
| allarmi via API | **non esposti affatto** | discreti, analogici e classi |
| schermate in cartelle | cartelle di sistema e utente, annidate | una collezione piatta |
| attributi leggibili | **`Name`, e nient'altro** | tutte le proprieta |

Il server implementa **entrambe** le strade, e dove una famiglia davvero non puo
fare una cosa lo dice e spiega perche, invece di restituire un elenco vuoto che
si potrebbe scambiare per una risposta.

## La cosa che nessuno dice del WinCC classico

`GetAttributeInfos()` su una schermata classica restituisce **un attributo**:
`Name`. Lo stesso su un tag. Lo stesso su una connessione. Misurato su un
progetto V21 vero, senza nessuna eccezione: gli attributi proprio non ci sono.

Numero di schermata, larghezza, altezza, `LogicalAddress` di un tag, tipo di
dato, connessione, ciclo di acquisizione: niente di tutto questo e raggiungibile
dal modello a oggetti. Sta solo nell'XML che scrive `Export()`.

Uno strumento che chiama `GetAttribute("Number")` si prende `null`, e uno
strumento che riporta quel `null` come "questa schermata non ha numero" ti sta
mentendo.

Percio `hmi_screens` e `hmi_tags` accettano `details: true`: esportano in una
cartella temporanea, leggono i valori veri e la cancellano. I tag si esportano
**per tabella**, quindi si paga un export per tabella e non uno per tag. Le
schermate una alla volta: conviene restringere con `names`.

Su Unified niente di tutto questo si applica: li sono proprieta normali
dell'oggetto vivo, e `details` viene ignorato.

## Cosa serve

- Windows con **TIA Portal V21** installato (opzione Openness, e il proprio
  utente nel gruppo locale **Siemens TIA Openness**)
- **.NET Framework 4.8**, presente su ogni macchina di engineering
- **Nessun SDK, nessun NuGet, niente da scaricare**: si compila con il
  compilatore C# che sta dentro .NET Framework

La V21 ha cambiato cose che rompono gli strumenti piu vecchi:
`Siemens.Engineering.dll` e stata spezzata in assembly per dominio, il public key
token e passato da `d29ec89bac048f84` a `29bfe5fdf4ba5d3b`, e le assembly si sono
spostate in `PublicAPI\V21\net48`. Questo server le risolve a runtime, partendo
dal Portal piu recente, quindi lo stesso eseguibile trova anche
un'installazione piu vecchia — ma e costruito e provato sulla V21.

## Compilare

```cmd
build.cmd
```

Tutto qui. Trova da solo `csc.exe` e le assembly di Openness, e scrive
`bin\TiaMcpServer.exe`. Per indicargliele:

```cmd
build.cmd -OpennessPath "D:\Siemens\Portal V21\PublicAPI\V21\net48"
```

Poi si controlla che risponda:

```cmd
powershell -ExecutionPolicy Bypass -File test\smoke.ps1
```

`smoke.ps1` si ferma a `tia_instances`, che elenca le istanze di TIA Portal in
esecuzione **senza agganciarsi**: nessuna finestra di conferma, nessun progetto
toccato.

## Configurare il client

### Claude Code

```cmd
claude mcp add tia -- "C:\percorso\bin\TiaMcpServer.exe"
```

### Qualunque client con configurazione JSON

```json
{
  "mcpServers": {
    "tia": {
      "command": "C:\\percorso\\bin\\TiaMcpServer.exe",
      "args": ["--read-only"]
    }
  }
}
```

Si toglie `--read-only` quando deve poter scrivere. In [examples/](examples/) ci
sono VS Code e Claude Desktop.

## Sicurezza

Far pilotare un sistema di engineering a un modello linguistico merita piu
attenzione di una API web. Quattro cose sono dentro il server:

**`--read-only`** rifiuta ogni strumento che modificherebbe il progetto. Ci si
aggancia al progetto di un cliente e il peggio che puo succedere e aver letto
qualcosa.

**`keep_safe`**, negli strumenti di import e di cancellazione, elenca cio che non
va mai toccato. Openness non sa rigenerare una vista messaggi ne una curva:
un import con `Override` distrugge senza avvisare le schermate fatte a mano.
Elencandole vengono saltate, e il salto viene riferito.

**L'import si ferma al primo errore.** Un XML rifiutato per schema puo far cadere
l'intero processo di TIA Portal. Il server riferisce quel che era riuscito a fare
e si ferma, invece di spingere i file rimanenti dentro una sessione che sta
morendo.

**Il lato PLC e in sola lettura per scelta.** Si elencano i blocchi, se ne
leggono le proprieta, si esportano in XML; niente riscrive un blocco. Un blocco
sovrascritto per errore e un impianto fermo.

Niente viene mai salvato da solo: le modifiche restano in memoria fino a
`tia_save`.

## Strumenti

35 strumenti. Riferimento completo in [docs/TOOLS.md](docs/TOOLS.md), e la
differenza fra le due famiglie in
[docs/HMI-FAMILIES.md](docs/HMI-FAMILIES.md).

**Sessione** — `tia_instances` `tia_attach` `tia_open_project` `tia_session`
`tia_detach` `tia_save` `tia_compile`

**Progetto** — `tia_devices` `tia_browse` `tia_get_attributes`
`tia_set_attributes`

**HMI, tutte e due le famiglie** — `hmi_panels` `hmi_info` `hmi_screens`
`hmi_export_screens` `hmi_import_screens` `hmi_delete_screens`
`hmi_tag_tables` `hmi_tags` `hmi_export_tags` `hmi_import_tags`
`hmi_text_lists` `hmi_export_text_lists` `hmi_import_text_lists`
`hmi_connections` `hmi_alarms`

**HMI, solo Unified** — `hmi_create_screen` `hmi_screen_items`
`hmi_create_screen_item` `hmi_item_types`

**PLC, sola lettura** — `plc_list` `plc_blocks` `plc_export_blocks`
`plc_tag_tables` `plc_tags`

### `tia_browse`, la via d'uscita

Openness espone tutto attraverso un unico modello: `IEngineeringObject` con i
suoi attributi, e composizioni che si comportano da elenchi. Invece di scrivere
uno strumento per ogni editor di TIA Portal, `tia_browse`, `tia_get_attributes`
e `tia_set_attributes` lo navigano in modo generico: temi, ricette, script,
pianificatore, sicurezza runtime, tutto quello che questo server non ha previsto.

```
tia_browse   path: "Hmi/HMI_RT_1/ScreenFolder/Screens"
tia_browse   path: "Devices/PLC_1/DeviceItems/[1]"
```

Le due scorciatoie `Hmi/<pannello>` e `Plc/<plc>` servono perche un `HmiTarget`
non e una *proprieta* di niente: e un **servizio** su un `DeviceItem`
(`GetService<SoftwareContainer>()`), e nessun percorso fatto di nomi di proprieta
ci arriva.

`tia_get_attributes` e anche il modo per scoprire cosa espone davvero un oggetto:
Openness non pubblica un elenco unico dei nomi degli attributi, si leggono
dall'oggetto vivo.

## Cosa Openness non sa fare

Vale la pena saperlo prima di pianificare il lavoro. Dal manuale Openness,
tabella 8-5: nel WinCC classico questi oggetti di schermata **non si esportano e
non si importano**, quindi non si possono generare e vanno protetti con
`keep_safe`:

> Vista messaggi · Finestra messaggi · Indicatore messaggi · Curve f(t) e f(x) ·
> Vista ricette · Vista tabella · Finestra di schermata · Viste camera, PDF,
> HTML e media · Campo di testo modificabile · Casella di riepilogo · Casella
> combinata · Casella di controllo · Pulsanti di opzione · Pulsante rotondo ·
> Barra di scorrimento · Tubo, raccordo a T, gomito, connettore · Segmenti e
> archi di ellisse e cerchio

Questi invece si esportano e si importano: campo di testo, rettangolo, cerchio,
linea, polilinea, poligono, ellisse, campo I/O, campo I/O simbolico e grafico,
pulsante, pulsante luminoso, interruttore, cursore, strumento indicatore, barra,
campo data/ora, orologio, libreria di simboli, gruppi, tasti funzione, istanze di
faceplate, vista grafica — piu l'**area permanente** e il **template di
schermata**.

Due altri limiti, imparati sbattendoci contro:

- **`ImportOptions` per le schermate ammette solo `None` e `Override`.**
  `RenameOnConflict` appartiene a `DccImportOptions` (grafici di azionamento) e
  `IgnoreMissingReferencedObject` a `SWImportOptions` (blocchi PLC). Un conflitto
  di numero di schermata quindi **non ha rimedio via API**: va evitato numerando.
- **`LogicalAddress` di un tag HMI e in sola lettura.** `SetAttribute` risponde
  `'set_LogicalAddress' is not supported`. La via e esportare la tabella,
  correggere l'XML e reimportare: e a questo che servono `hmi_export_tags` e
  `hmi_import_tags`.

## Stato

Costruito e provato su TIA Portal V21 con un progetto WinCC **classico** - un
pannello TP700 Comfort, 200 schermate, 1279 tag su 34 tabelle - in una sola
sessione agganciata:

```
tia_attach      T26-044.19_SWHMI, pid 480, Openness V21
hmi_panels      HMI_RT_1 -> classic
hmi_screens     details=true -> HOME: numero 1, 800x480, 38 oggetti
hmi_tags        details=true -> %DB151.DBW240, Word/UInt, PLC, 1 s, col commento
hmi_alarms      rifiutato, con la spiegazione
tia_save        rifiutato, --read-only
```

La parte **Unified** e scritta sull'assembly `Siemens.Engineering.WinCCUnified`
della V21 - tipi e firme letti per riflessione, non indovinati - ma **non e
ancora stata provata su un progetto Unified vero**. Se ne avete uno,
segnalazioni e pull request sono benvenute.

## Licenza

MIT, vedi [LICENSE](LICENSE).

TIA Portal, WinCC, SIMATIC e Openness sono marchi di Siemens AG. Questo e un
progetto indipendente, non affiliato ne approvato da Siemens.
