// Xml.cs - legge i documenti che TIA scrive negli export.
//
// Serve per il solo WinCC classico, e per un motivo preciso: li il modello a
// oggetti di Openness espone UN attributo per schermata e per tag, cioe Name.
// Verificato su un progetto vero: GetAttributeInfos() su Hmi.Screen.Screen
// restituisce solo Name, e altrettanto su Hmi.Tag.Tag e su Connection.
//
// Numero di schermata, larghezza, altezza, indirizzo PLC, tipo di dato,
// connessione, ciclo di acquisizione e commento esistono solo nell'XML. Quindi
// per rispondere davvero a "che indirizzo ha questo tag" non c'e scelta:
// si esporta e si legge.
//
// Qui non si scrive mai niente: modificare un documento di export e un'altra
// faccenda, e passa dagli strumenti di import.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace TiaMcp
{
    public static class Xml
    {
        /// <summary>Gli attributi del primo elemento del tipo indicato: &lt;AttributeList&gt; figlia diretta.</summary>
        public static JObj FirstAttributeList(string file, string elementName)
        {
            var result = new JObj();
            var doc = new XmlDocument();
            doc.Load(file);

            XmlNodeList nodes = doc.GetElementsByTagName(elementName);
            if (nodes.Count == 0) return result;

            XmlNode list = null;
            foreach (XmlNode child in nodes[0].ChildNodes)
                if (child.Name == "AttributeList") { list = child; break; }
            if (list == null) return result;

            foreach (XmlNode a in list.ChildNodes)
                if (a.NodeType == XmlNodeType.Element) result.Set(a.Name, a.InnerText);
            return result;
        }

        /// <summary>Quante volte compare un frammento. Grezzo di proposito: conta gli oggetti di schermata senza costruire l'albero.</summary>
        public static int CountElements(string file, string fragment)
        {
            string text = File.ReadAllText(file);
            int n = 0, at = 0;
            while (true)
            {
                at = text.IndexOf(fragment, at, StringComparison.Ordinal);
                if (at < 0) break;
                n++;
                at += fragment.Length;
            }
            return n;
        }

        /// <summary>
        /// I tag di una tabella esportata, per nome. Indirizzo e lunghezza stanno
        /// in AttributeList; tipo di dato, connessione e ciclo stanno invece in
        /// LinkList, che e un'altra sezione: sono riferimenti a oggetti, non valori.
        /// </summary>
        public static Dictionary<string, JObj> TagsOf(string file)
        {
            var result = new Dictionary<string, JObj>(StringComparer.Ordinal);
            var doc = new XmlDocument();
            doc.Load(file);

            foreach (XmlNode tag in doc.GetElementsByTagName("Hmi.Tag.Tag"))
            {
                string name = null;
                var j = new JObj();

                foreach (XmlNode section in tag.ChildNodes)
                {
                    if (section.Name == "AttributeList")
                    {
                        foreach (XmlNode a in section.ChildNodes)
                        {
                            if (a.NodeType != XmlNodeType.Element) continue;
                            switch (a.Name)
                            {
                                case "Name": name = a.InnerText; break;
                                case "LogicalAddress": j.Set("address", a.InnerText); break;
                                case "Length": j.Set("length", a.InnerText); break;
                                case "Coding": j.Set("coding", a.InnerText); break;
                                case "AddressAccessMode": j.Set("address_mode", a.InnerText); break;
                                case "StartValue": if (a.InnerText.Length > 0) j.Set("start_value", a.InnerText); break;
                            }
                        }
                    }
                    else if (section.Name == "LinkList")
                    {
                        foreach (XmlNode link in section.ChildNodes)
                        {
                            if (link.NodeType != XmlNodeType.Element) continue;
                            string value = NameOf(link);
                            if (value == null) continue;
                            switch (link.Name)
                            {
                                case "DataType": j.Set("data_type", value); break;
                                case "HmiDataType": j.Set("hmi_data_type", value); break;
                                case "Connection": j.Set("connection", value); break;
                                case "AcquisitionCycle": j.Set("acquisition_cycle", value); break;
                            }
                        }
                    }
                    else if (section.Name == "ObjectList")
                    {
                        string comment = MultilingualText(section, "Comment");
                        if (!string.IsNullOrEmpty(comment)) j.Set("comment", comment);
                    }
                }

                if (name != null) result[name] = j;
            }
            return result;
        }

        static string NameOf(XmlNode link)
        {
            foreach (XmlNode c in link.ChildNodes)
                if (c.Name == "Name") return c.InnerText;
            return null;
        }

        /// <summary>
        /// Il testo di un MultilingualText con il CompositionName dato. Si prende la
        /// prima lingua che ha qualcosa scritto: quale sia dipende dal progetto, e
        /// un commento in una lingua sola non deve sparire perche non e l'inglese.
        /// </summary>
        static string MultilingualText(XmlNode objectList, string composition)
        {
            foreach (XmlNode node in objectList.ChildNodes)
            {
                if (node.Name != "MultilingualText") continue;
                XmlAttribute comp = node.Attributes == null ? null : node.Attributes["CompositionName"];
                if (comp == null || comp.Value != composition) continue;

                foreach (XmlNode items in node.ChildNodes)
                {
                    if (items.Name != "ObjectList") continue;
                    foreach (XmlNode item in items.ChildNodes)
                    {
                        foreach (XmlNode attrs in item.ChildNodes)
                        {
                            if (attrs.Name != "AttributeList") continue;
                            foreach (XmlNode a in attrs.ChildNodes)
                                if (a.Name == "Text" && a.InnerText.Length > 0) return a.InnerText;
                        }
                    }
                }
            }
            return null;
        }
    }
}
