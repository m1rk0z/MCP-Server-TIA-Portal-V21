// Forms.cs - finestra di installazione, configurazione per il client, icona nella tray.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TiaAgent
{
    public sealed class InstallForm : Form
    {
        readonly NumericUpDown port = new NumericUpDown { Minimum = Installer.MinPort, Maximum = Installer.MaxPort, Width = 90 };
        readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly TextBox clients = new TextBox { Width = 260 };
        readonly CheckBox newToken = new CheckBox { Text = "Genera un nuovo token (i PC gia' configurati andranno aggiornati)", AutoSize = true };
        readonly Button ok = new Button { Text = "Installa", Width = 100 };
        readonly Button cancel = new Button { Text = "Annulla", Width = 100, DialogResult = DialogResult.Cancel };
        readonly TableLayoutPanel grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };

        public InstallSettings Settings { get; private set; }

        public InstallForm(AgentConfig current)
        {
            Text = Installer.AppName + " - installazione";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            Font = SystemFonts.MessageBoxFont;

            mode.Items.AddRange(new object[] { "read-only  (sola lettura)", "read-write (lettura e scrittura)" });
            mode.SelectedIndex = current.IsReadWrite ? 1 : 0;
            port.Value = Math.Max(Installer.MinPort, Math.Min(Installer.MaxPort, current.Port));
            clients.Text = string.Join(", ", current.AllowedClients);
            newToken.Visible = !string.IsNullOrEmpty(current.Token);

            var intro = new Label
            {
                Text = "Agente per Claude Code: espone TIA Portal (" + string.Join(", ", AgentConfig.Servers().Keys) +
                       ") di questa macchina al client MCP sul PC.\n" +
                       "Installazione in " + Installer.InstallDir + ", avvio automatico all'accesso dell'utente.",
                AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 3, 3, 12),
            };
            grid.Controls.Add(intro);
            grid.SetColumnSpan(intro, 2);
            Row("Porta TCP:", port);
            Row("Modalita':", mode);
            Row("IP client consentiti:", clients);
            Row("", new Label { Text = "vuoto = qualsiasi PC (serve comunque il token); es. 192.168.56.1", AutoSize = true, ForeColor = Color.DimGray });
            Row("", newToken);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            grid.Controls.Add(buttons);
            grid.SetColumnSpan(buttons, 2);
            Controls.Add(grid);

            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += delegate { Confirm(); };
        }

        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) });
            grid.Controls.Add(c);
        }

        void Confirm()
        {
            try
            {
                if (mode.SelectedIndex == 1 &&
                    MessageBox.Show(this,
                        "In modalita' read-write Claude potra' importare schermate, tag e liste testi, cambiare attributi, " +
                        "compilare e salvare il progetto TIA (le scritture restano in memoria fino a tia_save).\n\n" +
                        "Usarla solo su copie del progetto o con un backup. Continuare?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                Settings = new InstallSettings
                {
                    Port = (int)port.Value,
                    AccessMode = mode.SelectedIndex == 1 ? "read-write" : "read-only",
                    AllowedClients = Installer.ParseClients(clients.Text),
                    NewToken = newToken.Checked,
                };
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>Dati di connessione e blocco da incollare in .claude.json sul PC con Claude Code.</summary>
    public sealed class ClientConfigForm : Form
    {
        public ClientConfigForm(AgentConfig cfg, string headline)
        {
            Text = Installer.AppName + " - configurazione client";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(820, 600);
            MinimumSize = new Size(500, 350);
            Font = SystemFonts.MessageBoxFont;

            var text = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9.5f), Text = Installer.ClientInstructions(cfg).Replace("\n", "\r\n").Replace("\r\r", "\r"),
                BackColor = SystemColors.Window,
            };
            var top = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = headline == null ? 0 : 32, Text = headline ?? "",
                ForeColor = Color.DarkGreen, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Padding = new Padding(8, 8, 8, 0),
            };
            var copySnippet = new Button { Text = "Copia blocco .claude.json", AutoSize = true };
            var copyToken = new Button { Text = "Copia token", AutoSize = true };
            var close = new Button { Text = "Chiudi", AutoSize = true, DialogResult = DialogResult.OK };
            copySnippet.Click += delegate { Clipboard.SetText(cfg.ClientSnippet(AgentConfig.LocalAddresses().FirstOrDefault())); };
            copyToken.Click += delegate { Clipboard.SetText(cfg.Token); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            buttons.Controls.AddRange(new Control[] { close, copyToken, copySnippet });

            Controls.Add(text);
            Controls.Add(buttons);
            Controls.Add(top);
            AcceptButton = close;
        }
    }

    /// <summary>Icona dell'agente nella tray: verde = sola lettura, arancione = lettura e scrittura, rosso = non attivo.</summary>
    public sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon icon;
        readonly AgentServer server;
        readonly AgentConfig config;

        public TrayApp(AgentConfig config)
        {
            this.config = config;
            string status;
            try
            {
                server = new AgentServer(config);
                server.Start();
                status = "porta " + config.Port + " - " + (config.IsReadWrite ? "lettura e scrittura" : "sola lettura");
            }
            catch (Exception ex)
            {
                if (server != null) server.Dispose();
                server = null;
                status = "NON ATTIVO: " + ex.Message;
                AgentLog.Write("start failed: " + ex);
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem(Installer.AppName + " - " + status) { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem("TIA Portal: " + string.Join(", ", AgentConfig.Servers().Keys)) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Configurazione per Claude Code...", null, delegate { ShowClientConfig(); });
            menu.Items.Add("Apri cartella di lavoro", null, delegate { Open(config.WorkDir); });
            menu.Items.Add("Apri cartella log", null, delegate { Open(AgentConfig.LogDir); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Esci", null, delegate { ExitThread(); });

            icon = new NotifyIcon
            {
                Icon = MakeIcon(server == null ? Color.Firebrick : config.IsReadWrite ? Color.DarkOrange : Color.SeaGreen),
                Text = Truncate("TIA MCP Agent - " + status, 63),
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.DoubleClick += delegate { ShowClientConfig(); };
            if (server == null) icon.ShowBalloonTip(10000, Installer.AppName, status, ToolTipIcon.Error);
        }

        void ShowClientConfig()
        {
            using (var f = new ClientConfigForm(config, null)) f.ShowDialog();
        }

        static void Open(string dir)
        {
            try { System.IO.Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        static string Truncate(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

        static Icon MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (var brush = new SolidBrush(color))
                using (var font = new Font("Arial", 6.5f, FontStyle.Bold, GraphicsUnit.Point))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillRectangle(brush, 0, 0, 16, 16);
                    TextRenderer.DrawText(g, "TIA", font, new Rectangle(0, 0, 16, 16), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void ExitThreadCore()
        {
            icon.Visible = false;
            icon.Dispose();
            if (server != null) server.Dispose();
            AgentLog.Write("agent stopped");
            base.ExitThreadCore();
        }
    }
}
