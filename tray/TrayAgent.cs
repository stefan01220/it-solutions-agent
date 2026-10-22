// IT-Solutions Agent - Symbol im Infobereich (Windows, .NET Framework 4.8, C# 5).
//
// Zeigt den Zustand des Asset-Collectors an. Das Programm liest NUR die Statusdatei
// %ProgramData%\IT-Solutions-Status\status.json (schreibt der Collector, enthaelt kein Token)
// und sendet selbst nichts ans CRM. "Jetzt melden" startet den vorhandenen Collector-Task.
//
// Bauen: tray\build.cmd (csc.exe aus .NET Framework 4.x, kein SDK noetig).
// Testen ohne Installation: IT-Solutions-Agent.exe --details  (oeffnet sofort das Detailfenster)
// --start-sessions (nur als SYSTEM, ruft der Collector nach einer Installation/Aktualisierung auf):
//   startet das Symbol sofort in allen angemeldeten Benutzersitzungen, statt erst bei der naechsten Anmeldung.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("IT-Solutions Agent")]
[assembly: AssemblyProduct("IT-Solutions Agent")]
[assembly: AssemblyCompany("IT-Solutions by Stefan Regenfelder")]
[assembly: AssemblyCopyright("(c) 2026 Stefan Regenfelder, Villach - MIT-Lizenz")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace ItSolutions.Agent
{
    enum Health { Ok, Late, Error, Unknown }

    class Provider
    {
        public string Name = "IT-Solutions by Stefan Regenfelder";
        public string Street = "Dr.-Schärf-Straße 7a";
        public string Zip = "9524";
        public string City = "Villach-St. Magdalen";
        public string Phone = "+43 676 521 67 75";
        public string Email = "support@it-villach.at";
        public string Website = "www.it-villach.at";
    }

    class AgentStatus
    {
        public int AgentVersion;
        public string Hostname, Ip, CrmHost, LastResult, LastMessage, CustomerName;
        public DateTime? LastRunAt, LastSuccessAt;
        public Provider Provider = new Provider();
        public bool Present;

        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IT-Solutions-Status", "status.json");

        public static AgentStatus Load()
        {
            var s = new AgentStatus();
            s.Hostname = Environment.MachineName;
            s.CrmHost = "managed.it-villach.at";
            try
            {
                if (!File.Exists(FilePath)) return s;
                string json;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) json = sr.ReadToEnd();
                var d = new JavaScriptSerializer().DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                if (d == null) return s;
                s.Present = true;
                s.AgentVersion = Int(d, "agentVersion");
                s.Hostname = Str(d, "hostname") ?? s.Hostname;
                s.Ip = Str(d, "ip");
                s.CrmHost = Str(d, "crmHost") ?? s.CrmHost;
                s.LastResult = Str(d, "lastResult");
                s.LastMessage = Str(d, "lastMessage");
                s.CustomerName = Str(d, "customerName");
                s.LastRunAt = Date(d, "lastRunAt");
                s.LastSuccessAt = Date(d, "lastSuccessAt");
                var p = d.ContainsKey("provider") ? d["provider"] as System.Collections.Generic.Dictionary<string, object> : null;
                if (p != null)
                {
                    s.Provider.Name = Str(p, "name") ?? s.Provider.Name;
                    s.Provider.Street = Str(p, "street") ?? s.Provider.Street;
                    s.Provider.Zip = Str(p, "zip") ?? s.Provider.Zip;
                    s.Provider.City = Str(p, "city") ?? s.Provider.City;
                    s.Provider.Phone = Str(p, "phone") ?? s.Provider.Phone;
                    s.Provider.Email = Str(p, "email") ?? s.Provider.Email;
                    s.Provider.Website = Str(p, "website") ?? s.Provider.Website;
                }
            }
            catch { }
            return s;
        }

        static string Str(System.Collections.Generic.Dictionary<string, object> d, string k)
        {
            object v; if (!d.TryGetValue(k, out v) || v == null) return null;
            var t = Convert.ToString(v).Trim(); return t.Length == 0 ? null : t;
        }
        static int Int(System.Collections.Generic.Dictionary<string, object> d, string k)
        {
            int i; return int.TryParse(Str(d, k), out i) ? i : 0;
        }
        static DateTime? Date(System.Collections.Generic.Dictionary<string, object> d, string k)
        {
            DateTime t;
            return DateTime.TryParse(Str(d, k), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t)
                ? (DateTime?)t.ToLocalTime() : null;
        }

        public Health Health
        {
            get
            {
                if (!Present || !LastSuccessAt.HasValue) return Present && LastResult == "error" ? Health.Error : Health.Unknown;
                var age = DateTime.Now - LastSuccessAt.Value;
                if (age.TotalHours > 24) return Health.Error;
                if (LastResult == "error" || age.TotalHours > 3) return Health.Late;
                return Health.Ok;
            }
        }

        public string AgeText()
        {
            if (!LastSuccessAt.HasValue) return "noch kein Bericht";
            var m = (int)Math.Max(0, (DateTime.Now - LastSuccessAt.Value).TotalMinutes);
            if (m < 1) return "gerade eben";
            if (m < 60) return "vor " + m + " Min.";
            if (m < 48 * 60) return "vor " + (m / 60) + " Std.";
            return "vor " + (m / 1440) + " Tagen";
        }
    }

    static class Brand
    {
        public static readonly Color Green = Color.FromArgb(0x69, 0xB9, 0x18);
        public static readonly Color GreenDark = Color.FromArgb(0x2F, 0x5A, 0x08);
        public static readonly Color GreenSoft = Color.FromArgb(0xE9, 0xF4, 0xDC);
        public static readonly Color Amber = Color.FromArgb(0xE3, 0xA0, 0x1C);
        public static readonly Color AmberSoft = Color.FromArgb(0xFB, 0xF0, 0xD9);
        public static readonly Color AmberDark = Color.FromArgb(0x7A, 0x52, 0x06);
        public static readonly Color Red = Color.FromArgb(0xD9, 0x48, 0x48);
        public static readonly Color RedSoft = Color.FromArgb(0xFB, 0xE6, 0xE6);
        public static readonly Color RedDark = Color.FromArgb(0x8A, 0x22, 0x22);
        public static readonly Color Gray = Color.FromArgb(0x8A, 0x8F, 0x84);
        public static readonly Color Ink = Color.FromArgb(0x1B, 0x21, 0x16);
        public static readonly Color Ink2 = Color.FromArgb(0x5B, 0x64, 0x52);
        public static readonly Color Line = Color.FromArgb(0xDF, 0xE3, 0xD8);

        public static Color Dot(Health h)
        {
            switch (h) { case Health.Ok: return Color.FromArgb(0x8F, 0xD1, 0x4F); case Health.Late: return Amber; case Health.Error: return Red; default: return Gray; }
        }

        // Symbol zur Laufzeit zeichnen: gruenes "IT"-Quadrat + Statuspunkt rechts unten.
        public static Icon MakeIcon(Health h, int size)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float r = size * 0.22f, pad = size * 0.04f, box = size - 2 * pad;
                    using (var path = Rounded(new RectangleF(pad, pad, box, box), r))
                    using (var b = new SolidBrush(h == Health.Unknown ? Gray : Green)) g.FillPath(b, path);
                    using (var f = new Font("Segoe UI", size * 0.40f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("IT", f, Brushes.White, new RectangleF(0, -size * 0.04f, size * 0.92f, size), sf);
                    float d = size * 0.42f;
                    var dot = new RectangleF(size - d, size - d, d, d);
                    using (var b = new SolidBrush(Dot(h))) g.FillEllipse(b, dot);
                    using (var p = new Pen(Color.White, Math.Max(1f, size / 16f))) g.DrawEllipse(p, dot);
                }
                IntPtr hIcon = bmp.GetHicon();
                var ico = (Icon)Icon.FromHandle(hIcon).Clone();
                DestroyIcon(hIcon);
                return ico;
            }
        }

        public static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }

    // Detailfenster ueber dem Infobereich (rahmenlos, schliesst beim Verlassen).
    class DetailsForm : Form
    {
        readonly TrayApp app;
        AgentStatus st;

        public DetailsForm(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.None;   // Positionen werden selbst nach DPI skaliert (Scale)
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += (s, e) => { if (!app.KeepDetailsOpen) Close(); };
        }

        public void Fill(AgentStatus s)
        {
            st = s;
            Controls.Clear();
            int w = Scale(340), x = Scale(16), y = Scale(14), inner = w - 2 * x;

            var logo = new PictureBox { Image = DrawLogo(Scale(36)), Location = new Point(x, y), Size = new Size(Scale(36), Scale(36)) };
            Controls.Add(logo);
            var title = Lbl("IT-Solutions Agent", x + Scale(46), y - Scale(1), inner - Scale(46), 10.5f, FontStyle.Bold, Brand.Ink);
            Controls.Add(title);
            Controls.Add(Lbl("Managed by " + s.Provider.Name.Replace("IT-Solutions by ", "") + " IT · " + CityShort(s.Provider.City), x + Scale(46), y - Scale(1) + title.Height, inner - Scale(46), 8.5f, FontStyle.Regular, Brand.Ink2));
            y += Math.Max(Scale(36), title.Height * 2) + Scale(12);

            Color bg, fg; string txt;
            switch (s.Health)
            {
                case Health.Ok: bg = Brand.GreenSoft; fg = Brand.GreenDark; txt = "Verbunden · letzter Bericht " + s.AgeText(); break;
                case Health.Late: bg = Brand.AmberSoft; fg = Brand.AmberDark; txt = "Bericht verspätet · zuletzt " + s.AgeText(); break;
                case Health.Error: bg = Brand.RedSoft; fg = Brand.RedDark; txt = s.LastSuccessAt.HasValue ? "Keine Verbindung · zuletzt " + s.AgeText() : "Bericht fehlgeschlagen"; break;
                default: bg = Color.FromArgb(0xF0, 0xF1, 0xEE); fg = Brand.Ink2; txt = "Noch nicht eingerichtet"; break;
            }
            var pf = new Font("Segoe UI", 9f, FontStyle.Bold);
            var pill = new Label { Text = "●  " + txt, BackColor = bg, ForeColor = fg, Location = new Point(x, y), Size = new Size(inner, TextH(pf) + Scale(10)), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Scale(8), 0, 0, 0), Font = pf, AutoEllipsis = true };
            Controls.Add(pill);
            y += pill.Height + Scale(10);

            y = Row("Rechner", s.Hostname, x, y, inner);
            y = Row("IP-Adresse", s.Ip ?? "–", x, y, inner);
            if (s.CustomerName != null) y = Row("Kunde", s.CustomerName, x, y, inner);
            y = Row("Skript-Version", s.AgentVersion > 0 ? "v" + s.AgentVersion : "–", x, y, inner);
            y = Row("Verbunden mit", s.CrmHost, x, y, inner);
            if (s.Health == Health.Error && s.LastMessage != null) { var m = Lbl(s.LastMessage, x, y, inner, 8f, FontStyle.Regular, Brand.RedDark); m.Height = m.Height * 2; Controls.Add(m); y += m.Height + Scale(2); }

            y += Scale(6);
            Controls.Add(new Label { BackColor = Brand.Line, Location = new Point(x, y), Size = new Size(inner, 1) });
            y += Scale(8);
            foreach (var line in new[] { "Support", s.Provider.Phone, s.Provider.Email, s.Provider.Website })
            {
                var l = Lbl(line, x, y, inner, 9f, line == "Support" ? FontStyle.Bold : FontStyle.Regular, line == "Support" ? Brand.Ink : Brand.Ink2);
                Controls.Add(l); y += l.Height + Scale(1);
            }
            y += Scale(10);

            int bw = (inner - Scale(8)) / 2;
            var report = Btn("Jetzt melden", x, y, bw, true); report.Click += (a, b) => app.ReportNow();
            var support = Btn("Fernwartung", x + bw + Scale(8), y, bw, false); support.Click += (a, b) => app.StartSupport();
            Controls.Add(report); Controls.Add(support);
            y += report.Height + Scale(14);

            ClientSize = new Size(w, y);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Scale(12), wa.Bottom - Height - Scale(12));
        }

        public void KeepDetailsOpenForRender() { app.KeepDetailsOpen = true; }

        static string CityShort(string city) { var i = (city ?? "").IndexOf('-'); return i > 0 ? city.Substring(0, i) : city; }

        Bitmap DrawLogo(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var path = Brand.Rounded(new RectangleF(0, 0, size - 1, size - 1), size * 0.24f))
                using (var b = new SolidBrush(Brand.Green)) g.FillPath(b, path);
                using (var f = new Font("Segoe UI", size * 0.38f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("IT", f, Brushes.White, new RectangleF(0, 0, size, size), sf);
            }
            return bmp;
        }

        int Row(string k, string v, int x, int y, int w)
        {
            Controls.Add(Lbl(k, x, y, w / 2, 9f, FontStyle.Regular, Brand.Ink2));
            var val = Lbl(v, x + w / 3, y, w - w / 3, 9f, FontStyle.Regular, Brand.Ink); val.TextAlign = ContentAlignment.TopRight;
            Controls.Add(val);
            return y + TextH(new Font("Segoe UI", 9f)) + Scale(3);
        }

        Label Lbl(string t, int x, int y, int w, float pt, FontStyle fs, Color c)
        {
            var f = new Font("Segoe UI", pt, fs);
            return new Label { Text = t, Location = new Point(x, y), Size = new Size(w, TextH(f) + Scale(1)), Font = f, ForeColor = c, AutoEllipsis = true, UseMnemonic = false };
        }

        Button Btn(string t, int x, int y, int w, bool primary)
        {
            var bf = new Font("Segoe UI", 9f, FontStyle.Bold);
            var b = new Button { Text = t, Location = new Point(x, y), Size = new Size(w, TextH(bf) + Scale(14)), FlatStyle = FlatStyle.Flat, Font = bf, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = primary ? Brand.Green : Brand.Line;
            b.BackColor = primary ? Brand.Green : Color.White;
            b.ForeColor = primary ? Color.White : Brand.Ink;
            return b;
        }

        // Control.DeviceDpi liefert in .NET 4.8 ohne Per-Monitor-Konfiguration immer 96 - die echte System-DPI kommt aus GDI.
        static float dpi;
        static float Dpi { get { if (dpi <= 0) { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; } return dpi; } }
        static int Scale(int px) { return (int)Math.Round(px * Dpi / 96.0); }
        static int TextH(Font f) { return TextRenderer.MeasureText("Ag", f).Height; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Brand.Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    class TrayApp : ApplicationContext
    {
        public const string TaskName = "IT-Solutions Asset-Collector";
        readonly NotifyIcon icon;
        readonly System.Windows.Forms.Timer timer;
        FileSystemWatcher watcher;
        DetailsForm details;
        Health lastHealth = (Health)(-1);
        AgentStatus status;
        public bool KeepDetailsOpen;

        public TrayApp(bool showDetails)
        {
            icon = new NotifyIcon { Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDetails(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Details anzeigen", null, (s, e) => ShowDetails());
            menu.Items.Add("Jetzt melden", null, (s, e) => ReportNow());
            menu.Items.Add("Fernwartung starten", null, (s, e) => StartSupport());
            menu.Items.Add("Support-Mail schreiben", null, (s, e) => Open("mailto:" + status.Provider.Email + "?subject=" + Uri.EscapeDataString("Support " + status.Hostname)));
            menu.Items.Add("Website öffnen", null, (s, e) => Open("https://" + status.Provider.Website.Replace("https://", "").Replace("http://", "")));
            if (IsAdmin())
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Symbol vorübergehend ausblenden", null, (s, e) => ExitThread());
            }
            icon.ContextMenuStrip = menu;

            Refresh();
            timer = new System.Windows.Forms.Timer { Interval = 30000 };
            timer.Tick += (s, e) => Refresh();
            timer.Start();
            try
            {
                var dir = Path.GetDirectoryName(AgentStatus.FilePath);
                if (Directory.Exists(dir))
                {
                    watcher = new FileSystemWatcher(dir, "status.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
                    var ctx = SynchronizationContext.Current;
                    FileSystemEventHandler h = (s, e) => ctx.Post(_ => Refresh(), null);
                    watcher.Changed += h; watcher.Created += h; watcher.Renamed += (s, e) => ctx.Post(_ => Refresh(), null);
                }
            }
            catch { }
            if (showDetails) { KeepDetailsOpen = true; ShowDetails(); }
        }

        // Der Collector ersetzt die EXE im laufenden Betrieb (alte Datei wird umbenannt). Liegt unter dem
        // Startpfad eine neuere Version, startet sich das Symbol einmal neu - sonst bliebe die alte bis zur Abmeldung.
        static readonly string ExePath = Assembly.GetExecutingAssembly().Location;
        static readonly Version RunningVersion = Assembly.GetExecutingAssembly().GetName().Version;
        bool restarting;

        void CheckForUpdate()
        {
            if (restarting || KeepDetailsOpen) return;
            try
            {
                var onDisk = new Version(FileVersionInfo.GetVersionInfo(ExePath).FileVersion);
                if (onDisk <= RunningVersion) return;
                restarting = true;
                Program.ReleaseMutex();
                Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false });
                ExitThread();
            }
            catch { }
        }

        void Refresh()
        {
            CheckForUpdate();
            if (restarting) return;
            status = AgentStatus.Load();
            var h = status.Health;
            if (h != lastHealth)
            {
                var old = icon.Icon;
                icon.Icon = Brand.MakeIcon(h, Math.Max(16, SystemInformation.SmallIconSize.Width));
                if (old != null) old.Dispose();
                lastHealth = h;
            }
            SetTooltip(TooltipText(status));
            if (details != null && !details.IsDisposed && details.Visible) details.Fill(status);
        }

        // Windows-Tooltip: max. 127 Zeichen (NotifyIcon.Text erlaubt in .NET Framework nur 63 - daher per Reflection).
        public static string TooltipText(AgentStatus s)
        {
            var t = "IT-Solutions Agent" + (s.AgentVersion > 0 ? " v" + s.AgentVersion : "") + "\n"
                  + (s.Ip != null ? "IP " + s.Ip + "\n" : "")
                  + "Managed by Stefan Regenfelder IT, Villach\n"
                  + "linked to " + s.CrmHost;
            return t.Length > 127 ? t.Substring(0, 127) : t;
        }

        void SetTooltip(string text)
        {
            try
            {
                var t = typeof(NotifyIcon); var f = BindingFlags.NonPublic | BindingFlags.Instance;
                t.GetField("text", f).SetValue(icon, text);
                if ((bool)t.GetField("added", f).GetValue(icon)) t.GetMethod("UpdateIcon", f).Invoke(icon, new object[] { true });
            }
            catch { icon.Text = text.Length > 63 ? text.Substring(0, 63) : text; }
        }

        void ShowDetails()
        {
            if (details == null || details.IsDisposed) { details = new DetailsForm(this); details.FormClosed += (s, e) => { if (KeepDetailsOpen) ExitThread(); }; }
            details.Fill(AgentStatus.Load());
            details.Show();
            details.Activate();
        }

        public void ReportNow()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"" + TaskName + "\"") { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(15000);
                if (p.ExitCode == 0) Balloon("Bericht wird gesendet", "Der Zustand dieses Rechners wird jetzt an IT-Solutions gemeldet.", ToolTipIcon.Info);
                else Balloon("Melden nicht möglich", "Der Collector-Task konnte nicht gestartet werden (Code " + p.ExitCode + ").", ToolTipIcon.Warning);
            }
            catch (Exception ex) { Balloon("Melden nicht möglich", ex.Message, ToolTipIcon.Warning); }
        }

        // Fernwartung: installierten TeamViewer starten, sonst TeamViewer QuickSupport laden (Signatur wird geprueft).
        public void StartSupport()
        {
            var tv = FindTeamViewer();
            if (tv != null) { Open(tv); return; }
            Balloon("Fernwartung", "TeamViewer QuickSupport wird geladen …", ToolTipIcon.Info);
            var t = new Thread(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    var file = Path.Combine(Path.GetTempPath(), "TeamViewerQS.exe");
                    using (var wc = new WebClient()) wc.DownloadFile("https://download.teamviewer.com/download/TeamViewerQS.exe", file);
                    string why;
                    if (!Authenticode.IsTrustedTeamViewer(file, out why)) { try { File.Delete(file); } catch { } throw new Exception("Datei nicht vertrauenswürdig: " + why); }
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                }
                catch (Exception ex) { Balloon("Fernwartung nicht möglich", ex.Message + " – bitte " + status.Provider.Phone + " anrufen.", ToolTipIcon.Warning); }
            });
            t.IsBackground = true; t.Start();
        }

        static string FindTeamViewer()
        {
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                var p = Path.Combine(pf, "TeamViewer", "TeamViewer.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        void Balloon(string title, string text, ToolTipIcon i) { try { icon.ShowBalloonTip(5000, title, text, i); } catch { } }

        static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } }

        static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; }
        }

        protected override void ExitThreadCore()
        {
            icon.Visible = false; icon.Dispose();
            if (watcher != null) watcher.Dispose();
            base.ExitThreadCore();
        }
    }

    // Authenticode-Pruefung (WinVerifyTrust) + Herausgeber muss TeamViewer sein.
    static class Authenticode
    {
        public static bool IsTrustedTeamViewer(string file, out string why)
        {
            why = null;
            if (!Verify(file)) { why = "keine gültige Signatur"; return false; }
            try
            {
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
                var subject = cert.GetNameInfo(X509NameType.SimpleName, false) ?? "";
                if (subject.IndexOf("TeamViewer", StringComparison.OrdinalIgnoreCase) < 0) { why = "Herausgeber ist " + subject; return false; }
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice;
            public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction;
            public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        static bool Verify(string file)
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = file };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
            IntPtr pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var wd = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)), dwUIChoice = 2 /* WTD_UI_NONE */,
                    fdwRevocationChecks = 0 /* WTD_REVOKE_NONE */, dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFile, dwStateAction = 0, dwProvFlags = 0
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(wd));
                Marshal.StructureToPtr(wd, pData, false);
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(IntPtr.Zero, action, pData) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            }
        }
    }

    // Symbol in allen aktiven Benutzersitzungen starten (aus dem SYSTEM-Kontext des Collectors).
    // WTSQueryUserToken braucht das TCB-Recht - das hat nur SYSTEM. Doppelte Instanzen verhindert der Mutex je Sitzung.
    static class SessionLauncher
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonation, int tokenType, out IntPtr newToken);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr env);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public static int StartInAllSessions()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            IntPtr info; int count; int started = 0;
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out info, out count)) return -1;
            try
            {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var si = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                    if (si.State != 0 /* WTSActive */ || si.SessionId == 0) continue;
                    IntPtr user, primary = IntPtr.Zero, env = IntPtr.Zero;
                    if (!WTSQueryUserToken(si.SessionId, out user)) continue;
                    try
                    {
                        if (!DuplicateTokenEx(user, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* Impersonation */, 1 /* Primary */, out primary)) continue;
                        CreateEnvironmentBlock(out env, primary, false);
                        var st = new STARTUPINFO(); st.cb = Marshal.SizeOf(typeof(STARTUPINFO)); st.lpDesktop = "winsta0\\default";
                        PROCESS_INFORMATION pi;
                        if (CreateProcessAsUser(primary, exe, "\"" + exe + "\"", IntPtr.Zero, IntPtr.Zero, false, 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, env, Path.GetDirectoryName(exe), ref st, out pi))
                        {
                            started++; CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                        }
                    }
                    finally
                    {
                        if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                        if (primary != IntPtr.Zero) CloseHandle(primary);
                        CloseHandle(user);
                    }
                }
            }
            finally { WTSFreeMemory(info); }
            return started;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        static Mutex mutex;
        public static void ReleaseMutex() { try { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); mutex = null; } } catch { } }

        static void Render(string file)
        {
            var app = new TrayApp(false);
            var f = new DetailsForm(app);
            f.Fill(AgentStatus.Load());
            f.KeepDetailsOpenForRender();
            f.Location = new Point(-10000, -10000);
            f.Show();
            Application.DoEvents();
            int pad = 24, iconRow = 56;
            using (var bmp = new Bitmap(f.Width + 2 * pad, f.Height + 2 * pad + iconRow))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(0xC9, 0xCF, 0xC1));
                    using (var fb = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(fb, new Rectangle(0, 0, f.Width, f.Height)); g.DrawImage(fb, pad, pad); }
                    int x = pad;
                    foreach (Health h in new[] { Health.Ok, Health.Late, Health.Error, Health.Unknown })
                    {
                        using (var ico = Brand.MakeIcon(h, 32)) g.DrawIcon(ico, new Rectangle(x, f.Height + 2 * pad, 32, 32));
                        x += 48;
                    }
                }
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            f.Close();
            app.ExitThread();
        }

        [STAThread]
        static void Main(string[] args)
        {
            // --render <datei.png>: Detailfenster + Symbole als Bild speichern (Doku/Test, ohne Bildschirm)
            if (Array.IndexOf(args, "--start-sessions") >= 0) { Environment.ExitCode = Math.Max(0, SessionLauncher.StartInAllSessions()); return; }
            int ri = Array.IndexOf(args, "--render");
            if (ri >= 0 && ri + 1 < args.Length) { try { SetProcessDPIAware(); } catch { } Render(args[ri + 1]); return; }
            bool showDetails = Array.Exists(args, a => a == "--details");
            bool created;
            // Ein Symbol je Benutzersitzung
            mutex = new Mutex(true, "Local\\ITSolutionsAgentTray", out created);
            if (!created && !showDetails)
            {
                // Direkt nach einem Update-Neustart haelt die alte Instanz den Mutex evtl. noch kurz.
                try { created = mutex.WaitOne(5000); } catch (AbandonedMutexException) { created = true; }
                if (!created) return;
            }
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(showDetails));
            ReleaseMutex();
        }
    }
}
