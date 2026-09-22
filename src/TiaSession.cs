// TiaSession.cs - una sola sessione Openness, tenuta aperta per tutta la vita
// del server.
//
// Questo e il motivo per cui esiste questo server. Ogni Attach() a TIA Portal
// costa una conferma manuale a chi sta davanti al video: un programma a riga di
// comando per ogni operazione significa una finestra da accettare per ogni
// operazione. Qui si aggancia una volta e si lavora finche il client MCP resta
// aperto.
//
// Openness non e thread-safe e vuole un thread STA: tutto gira sul thread
// principale, una richiesta alla volta. Non e un limite, e la semantica giusta
// per un motore di engineering.

using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaMcp
{
    public class TiaSession : IDisposable
    {
        TiaPortal portal;
        Project project;

        public bool Attached { get { return portal != null; } }
        public TiaPortal Portal { get { return portal; } }
        public string Mode { get; private set; }
        public int ProcessId { get; private set; }

        public Project Project
        {
            get
            {
                if (project == null) throw new McpError("No project is open. Call tia_attach or tia_open_project first.");
                return project;
            }
        }

        public void RequireAttached()
        {
            if (portal == null)
                throw new McpError("Not attached to TIA Portal. Call tia_attach (for an instance already running) " +
                                   "or tia_open_project (to open one from file).");
        }

        // ------------------------------------------------------------- istanze

        /// <summary>
        /// Elenca le istanze di TIA Portal in esecuzione. NON aggancia nulla:
        /// questa chiamata non fa comparire la finestra di conferma.
        /// </summary>
        public static List<JObj> ListInstances()
        {
            var result = new List<JObj>();
            foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
            {
                string path = "";
                try { path = p.ProjectPath == null ? "" : p.ProjectPath.FullName; }
                catch { path = "(unreadable)"; }

                string mode = "";
                try { mode = p.Mode.ToString(); }
                catch { }

                result.Add(new JObj()
                    .Set("pid", p.Id)
                    .Set("project_path", path)
                    .Set("mode", mode)
                    .Set("has_project", path.Length > 0 && path != "(unreadable)"));
            }
            return result;
        }

        // ------------------------------------------------------------ aggancio

        /// <summary>Si aggancia a un'istanza gia aperta. Una conferma manuale, una sola volta.</summary>
        public JObj Attach(int pid)
        {
            Close();

            TiaPortalProcess chosen = null;
            var candidates = new List<TiaPortalProcess>();
            foreach (TiaPortalProcess p in TiaPortal.GetProcesses()) candidates.Add(p);

            if (candidates.Count == 0)
                throw new McpError("No TIA Portal instance is running.");

            if (pid > 0)
            {
                foreach (TiaPortalProcess p in candidates) if (p.Id == pid) chosen = p;
                if (chosen == null) throw new McpError("No TIA Portal instance with pid " + pid + ".");
            }
            else
            {
                // senza PID si prende la prima che ha davvero un progetto aperto:
                // agganciarsi a un Portal vuoto non serve a niente
                foreach (TiaPortalProcess p in candidates)
                {
                    string path;
                    try { path = p.ProjectPath == null ? "" : p.ProjectPath.FullName; }
                    catch { path = ""; }
                    if (path.Length > 0) { chosen = p; break; }
                }
                if (chosen == null)
                    throw new McpError("There are " + candidates.Count + " TIA Portal instances but none " +
                                       "has a project open. Open the project, or give an explicit pid.");
            }

            try { portal = chosen.Attach(); }
            catch (EngineeringSecurityException ex)
            {
                // Il messaggio nudo di Openness e "Security error / The operation has
                // timed out", che non dice niente a chi non sa che dall'altra parte
                // e comparsa una finestra. La finestra ha un timeout suo: se nessuno
                // la accetta entro quello, l'aggancio muore cosi.
                throw new McpError(
                    "TIA Portal refused the attach: " + Root(ex).Message + ". " +
                    "Openness raises this when the confirmation dialog in TIA Portal is declined, " +
                    "or when nobody accepts it before it times out. Ask whoever is at the machine to " +
                    "accept it, then call tia_attach again - and call it when they are actually there, " +
                    "because the dialog will not wait indefinitely. If no dialog appears at all, the " +
                    "user is probably not in the local 'Siemens TIA Openness' group.");
            }
            ProcessId = chosen.Id;
            Mode = "attach";
            project = portal.Projects.Count > 0 ? portal.Projects[0] : null;

            return Describe();
        }

        /// <summary>Apre un progetto in un'istanza nuova. Senza interfaccia e piu veloce e non chiede conferme.</summary>
        public JObj OpenProject(string path, bool withUserInterface)
        {
            if (string.IsNullOrEmpty(path)) throw new McpError("Missing project path (.ap* or .zap*).");
            var file = new FileInfo(path);
            if (!file.Exists) throw new McpError("Project not found: " + path);

            Close();

            portal = new TiaPortal(withUserInterface
                ? TiaPortalMode.WithUserInterface
                : TiaPortalMode.WithoutUserInterface);
            Mode = withUserInterface ? "open-with-ui" : "open-headless";
            try { ProcessId = portal.GetCurrentProcess().Id; }
            catch { ProcessId = 0; }

            project = portal.Projects.Open(file);
            return Describe();
        }

        public JObj Describe()
        {
            var j = new JObj()
                .Set("attached", portal != null)
                .Set("mode", Mode)
                .Set("pid", ProcessId)
                .Set("openness_assemblies", Openness.ResolvedFrom)
                .Set("portal_version", Openness.PortalVersion);

            if (project == null) { j.Set("project", null); return j; }

            j.Set("project", new JObj()
                .Set("name", Safe(delegate { return project.Name; }))
                .Set("path", Safe(delegate { return project.Path == null ? null : project.Path.FullName; }))
                .Set("author", Safe(delegate { return project.Author; }))
                .Set("version", Safe(delegate { return project.Version; }))
                .Set("last_modified", Safe(delegate { return project.LastModified.ToString("yyyy-MM-dd HH:mm:ss"); }))
                .Set("has_unsaved_changes", project.IsModified));
            return j;
        }

        public JObj Save()
        {
            RequireAttached();
            Project.Save();
            return new JObj().Set("saved", true).Set("project", Safe(delegate { return project.Name; }));
        }

        // ------------------------------------------------------- i dispositivi

        /// <summary>
        /// Scende in tutto l'albero dei dispositivi e raccoglie i software trovati.
        /// E la sola via affidabile: il pannello non sta mai sul Device, sta su un
        /// DeviceItem annidato che espone un SoftwareContainer.
        /// </summary>
        public List<SoftwareRef> Softwares()
        {
            RequireAttached();
            var found = new List<SoftwareRef>();
            foreach (Device dev in AllDevices()) WalkDevice(dev, found);
            return found;
        }

        /// <summary>
        /// Tutti i dispositivi, ovunque siano. Non basta Project.Devices: i gruppi
        /// utente e la cartella "Dispositivi non raggruppati" - dove finiscono le
        /// stazioni PC e spesso i pannelli - sono collezioni separate.
        /// </summary>
        public List<Device> AllDevices()
        {
            RequireAttached();
            var found = new List<Device>();
            foreach (Device dev in Project.Devices) AddOnce(found, dev);
            foreach (DeviceUserGroup g in Project.DeviceGroups) WalkGroup(g, found);
            try { foreach (Device dev in Project.UngroupedDevicesGroup.Devices) AddOnce(found, dev); }
            catch { }
            return found;
        }

        static void AddOnce(List<Device> into, Device dev)
        {
            foreach (Device d in into) if (ReferenceEquals(d, dev) || d.Equals(dev)) return;
            into.Add(dev);
        }

        void WalkGroup(DeviceUserGroup group, List<Device> found)
        {
            foreach (Device dev in group.Devices) AddOnce(found, dev);
            foreach (DeviceUserGroup sub in group.Groups) WalkGroup(sub, found);
        }

        void WalkDevice(Device dev, List<SoftwareRef> found)
        {
            foreach (DeviceItem item in dev.DeviceItems) WalkItem(dev, item, found);
        }

        void WalkItem(Device dev, DeviceItem item, List<SoftwareRef> found)
        {
            SoftwareContainer container = null;
            try { container = item.GetService<SoftwareContainer>(); }
            catch { }

            if (container != null && container.Software != null)
                found.Add(new SoftwareRef(dev, item, container.Software));

            foreach (DeviceItem sub in item.DeviceItems) WalkItem(dev, sub, found);
        }

        public List<JObj> DeviceOverview()
        {
            RequireAttached();
            var softs = Softwares();
            var result = new List<JObj>();
            foreach (Device dev in AllDevices()) result.Add(DescribeDevice(dev, softs));
            return result;
        }

        JObj DescribeDevice(Device dev, List<SoftwareRef> softs)
        {
            // Il confronto e per nome, non per riferimento: ogni enumerazione di
            // Project.Devices restituisce proxy nuovi, e due letture dello stesso
            // dispositivo non sono lo stesso oggetto .NET.
            var kinds = new List<JObj>();
            foreach (SoftwareRef s in softs)
                if (string.Equals(s.Device.Name, dev.Name, StringComparison.Ordinal))
                    kinds.Add(new JObj()
                        .Set("name", s.Software.Name)
                        .Set("kind", s.Software.GetType().Name)
                        .Set("hosted_by", s.Item.Name));

            // TypeIdentifier, numero d'ordine e firmware stanno sui DeviceItem, non
            // sul Device: sul Device tornano sempre null.
            string type = Attr(dev, "TypeIdentifier");
            string order = null, firmware = null;
            foreach (DeviceItem it in dev.DeviceItems)
            {
                if (type == null) type = Attr(it, "TypeIdentifier");
                if (order == null) order = Attr(it, "OrderNumber");
                if (firmware == null) firmware = Attr(it, "FirmwareVersion");
                if (order != null && firmware != null) break;
            }

            return new JObj()
                .Set("name", dev.Name)
                .Set("type_identifier", type)
                .Set("order_number", order)
                .Set("firmware", firmware)
                .Set("software", kinds)
                .Set("network", NetworkOf(dev));
        }

        List<JObj> NetworkOf(Device dev)
        {
            var nodes = new List<JObj>();
            foreach (DeviceItem item in dev.DeviceItems) CollectNodes(item, nodes);
            return nodes;
        }

        void CollectNodes(DeviceItem item, List<JObj> into)
        {
            NetworkInterface ni = null;
            try { ni = item.GetService<NetworkInterface>(); }
            catch { }

            if (ni != null)
            {
                foreach (Node n in ni.Nodes)
                {
                    string subnet;
                    try { subnet = n.ConnectedSubnet == null ? null : n.ConnectedSubnet.Name; }
                    catch { subnet = null; }
                    into.Add(new JObj()
                        .Set("interface", item.Name)
                        .Set("address", Attr(n, "Address"))
                        .Set("subnet", subnet));
                }
            }
            foreach (DeviceItem sub in item.DeviceItems) CollectNodes(sub, into);
        }

        public static string Attr(IEngineeringObject o, string name)
        {
            try
            {
                object v = o.GetAttribute(name);
                return v == null ? null : v.ToString();
            }
            catch { return null; }
        }

        static string Safe(Func<string> f)
        {
            try { return f(); }
            catch { return null; }
        }

        static Exception Root(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        // ------------------------------------------------------------- chiusura

        public JObj Detach()
        {
            bool was = portal != null;
            Close();
            return new JObj().Set("detached", was);
        }

        void Close()
        {
            project = null;
            if (portal == null) return;
            try { portal.Dispose(); }
            catch { }
            portal = null;
            Mode = null;
            ProcessId = 0;
        }

        public void Dispose() { Close(); }
    }

    /// <summary>Un software (PlcSoftware, HmiTarget, HmiSoftware) con il dispositivo che lo ospita.</summary>
    public class SoftwareRef
    {
        public readonly Device Device;
        public readonly DeviceItem Item;
        public readonly Software Software;

        public SoftwareRef(Device device, DeviceItem item, Software software)
        {
            Device = device;
            Item = item;
            Software = software;
        }
    }
}
