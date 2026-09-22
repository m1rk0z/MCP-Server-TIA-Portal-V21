// Panel.cs - l'astrazione sulle due famiglie WinCC.
//
// In TIA Portal convivono due modelli a oggetti HMI che non si somigliano:
//
//   WinCC classico (Comfort, Advanced, Professional)   Siemens.Engineering.Hmi
//     - HmiTarget, schermate in cartelle di sistema e utente
//     - le schermate si trattano SOLO per export/import di XML: non esiste
//       Screens.Create(), e non tutti gli oggetti si lasciano esportare
//     - allarmi e classi di allarme NON sono esposti da Openness
//
//   WinCC Unified                                      Siemens.Engineering.HmiUnified
//     - HmiSoftware, schermate in una composizione piatta
//     - le schermate si CREANO e si modificano a oggetti: Screens.Create(nome),
//       ScreenItems.Create<T>(nome), proprieta scrivibili una per una
//     - non esiste import/export di schermate
//     - allarmi discreti, analogici e classi di allarme sono esposti
//
// Ogni famiglia sa fare cose che l'altra non sa. Qui non si finge una simmetria
// che non c'e: cio che una famiglia non puo fare risponde con il motivo, non con
// un errore di riferimento nullo.

using System;
using System.Collections.Generic;
using System.IO;

namespace TiaMcp
{
    public enum HmiFamily { Classic, Unified }

    public abstract class Panel
    {
        public SoftwareRef Ref { get; protected set; }

        public abstract HmiFamily Family { get; }
        public abstract string Name { get; }

        public string FamilyLabel
        {
            get { return Family == HmiFamily.Unified ? "WinCC Unified" : "WinCC classic (Comfort/Advanced/Professional)"; }
        }

        public string DeviceName { get { return Ref.Device.Name; } }

        /// <summary>
        /// Il codice del pannello, es. "OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0".
        /// Sul DeviceItem che ospita il software non c'e quasi mai: sta sulla testa
        /// del dispositivo, e va cercato scendendo.
        /// </summary>
        protected string DeviceType()
        {
            string t = TiaSession.Attr(Ref.Item, "TypeIdentifier");
            if (!string.IsNullOrEmpty(t)) return t;

            t = TiaSession.Attr(Ref.Device, "TypeIdentifier");
            if (!string.IsNullOrEmpty(t)) return t;

            foreach (Siemens.Engineering.HW.DeviceItem it in Ref.Device.DeviceItems)
            {
                t = TiaSession.Attr(it, "TypeIdentifier");
                if (!string.IsNullOrEmpty(t)) return t;
            }
            return null;
        }

        /// <summary>Riconosce la famiglia dal tipo di Software e costruisce il wrapper giusto.</summary>
        public static Panel Wrap(SoftwareRef r)
        {
            if (r.Software is Siemens.Engineering.Hmi.HmiTarget)
                return new ClassicPanel(r);
            if (r.Software is Siemens.Engineering.HmiUnified.HmiSoftware)
                return new UnifiedPanel(r);
            return null;
        }

        // --------------------------------------------------------- descrizione

        public abstract JObj Info();

        public JObj Identity()
        {
            return new JObj()
                .Set("panel", Name)
                .Set("device", DeviceName)
                .Set("family", Family == HmiFamily.Unified ? "unified" : "classic")
                .Set("family_label", FamilyLabel);
        }

        // ------------------------------------------------------------ schermate

        // 'details' esiste per una ragione sola, e riguarda il solo classico: li
        // GetAttributeInfos() su una schermata o su un tag restituisce UN attributo,
        // Name. Numero di schermata, dimensioni, indirizzo PLC, tipo di dato,
        // connessione: niente di tutto questo e nel modello a oggetti, sta solo
        // nell'XML esportato. Con details=true il server esporta in una cartella
        // temporanea, legge i valori veri e la cancella. Costa tempo, e l'unica via.
        // Su Unified e ignorato: li le proprieta ci sono davvero.
        public abstract List<JObj> Screens(List<string> names, bool details);
        public abstract List<JObj> Connections();
        public abstract List<JObj> TagTables();
        public abstract List<JObj> Tags(string contains, int limit, bool details);
        public abstract List<JObj> TextLists();
        public abstract List<JObj> Alarms();

        public abstract JObj ExportScreens(string outDir, List<string> names, bool withDefaults);
        public abstract JObj ImportScreens(List<string> files, bool overwrite, List<string> keepSafe, string folder);
        public abstract JObj DeleteScreens(List<string> names, List<string> keepSafe);

        public abstract JObj ExportTags(string outDir, List<string> tables);
        public abstract JObj ImportTags(List<string> files, bool overwrite);

        public abstract JObj ExportTextLists(string outDir, List<string> names);
        public abstract JObj ImportTextLists(List<string> files, bool overwrite);

        /// <summary>Solo Unified: crea una schermata vuota. Nel classico non esiste la primitiva.</summary>
        public virtual JObj CreateScreen(string name, int number)
        {
            throw Unsupported("hmi_create_screen",
                "In classic WinCC, Openness has no primitive for creating a screen: " +
                "the only route is importing an XML with hmi_import_screens.");
        }

        /// <summary>Solo Unified: elenca gli oggetti dentro una schermata.</summary>
        public virtual List<JObj> ScreenItems(string screen)
        {
            throw Unsupported("hmi_screen_items",
                "In classic WinCC the objects inside a screen are not reachable through the API: " +
                "export the screen with hmi_export_screens and read the XML.");
        }

        protected McpError Unsupported(string tool, string why)
        {
            return new McpError(tool + " is not available on " + FamilyLabel + ". " + why);
        }

        // ------------------------------------------------------------- utilita

        protected static string EnsureDir(string path, string argName)
        {
            if (string.IsNullOrEmpty(path)) throw new McpError("Missing parameter " + argName + ".");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        protected static bool Matches(List<string> wanted, string name)
        {
            if (wanted == null || wanted.Count == 0) return true;
            foreach (string w in wanted)
                if (string.Equals(w, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        protected static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (haystack == null) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Cartella temporanea usa e getta per gli export di servizio.</summary>
        protected static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(),
                                    "tiamcp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        protected static void Discard(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* una cartella temporanea rimasta non e un motivo per fallire */ }
        }

        protected static string Flat(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            while (ex != null)
            {
                sb.Append(ex.Message.Replace("\r\n", " | ").Replace("\n", " | "));
                ex = ex.InnerException;
                if (ex != null) sb.Append("  <<  ");
            }
            string s = sb.ToString();
            return s.Length > 600 ? s.Substring(0, 600) + " [...]" : s;
        }
    }
}
