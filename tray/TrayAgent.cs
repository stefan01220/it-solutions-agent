// IT-Solutions Agent - Symbol im Infobereich (Windows, .NET Framework 4.8, C# 5).
//
// Zeigt den Zustand des Asset-Collectors an. Das Programm liest NUR die Statusdatei
// %ProgramData%\IT-Solutions-Status\status.json (schreibt der Collector, enthaelt kein Token)
// und sendet selbst nichts ans CRM. "Status senden" startet den vorhandenen Collector-Task.
//
// Bauen: tray\build.cmd (csc.exe aus .NET Framework 4.x, kein SDK noetig).
// Testen ohne Installation: IT-Solutions-Agent.exe --details  (oeffnet sofort das Detailfenster),
//   dazu --status <datei.json> fuer eine eigene Statusdatei und --theme light|dark fuer die Darstellung.
// --start-sessions (nur als SYSTEM, ruft der Collector nach einer Installation/Aktualisierung auf):
//   startet das Symbol sofort in allen angemeldeten Benutzersitzungen, statt erst bei der naechsten Anmeldung.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

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
        public string TeamViewerId, Manufacturer, Model, Serial, Domain, OsName, OsProduct, ContractNo;
        public DateTime? LastRunAt, LastSuccessAt, OsSupportEnd, LastPatchAt;
        public Provider Provider = new Provider();
        public bool Present;
        /** "Problem melden" fuer diesen Kunden freigeschaltet (Standard: nur mit Wartungsvertrag) */
        public bool TicketsEnabled;

        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IT-Solutions-Status", "status.json");

        // Nur zum Ausprobieren (--status <datei>): Statusdatei von woanders lesen, z.B. mit freigeschaltetem "Problem melden".
        public static string OverridePath;

        public static AgentStatus Load() { return Load(OverridePath ?? FilePath); }

        public static AgentStatus Load(string filePath)
        {
            var s = new AgentStatus();
            s.Hostname = Environment.MachineName;
            s.CrmHost = "managed.it-villach.at";
            try
            {
                if (!File.Exists(filePath)) return s;
                string json;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
                s.TeamViewerId = Str(d, "teamviewerId");
                s.Manufacturer = Str(d, "manufacturer");
                s.Model = Str(d, "model");
                s.Serial = Str(d, "serial");
                s.Domain = Str(d, "domain");
                s.OsName = Str(d, "osName");
                s.OsProduct = Str(d, "osProduct");
                s.OsSupportEnd = Date(d, "osSupportEnd");
                s.ContractNo = Str(d, "contractNo");
                s.LastPatchAt = Date(d, "lastPatchAt");
                object te; s.TicketsEnabled = d.TryGetValue("ticketsEnabled", out te) && te is bool && (bool)te;
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

        public static string ReadTeamViewerId()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = b.OpenSubKey("SOFTWARE\\TeamViewer"))
                    {
                        var v = k == null ? null : k.GetValue("ClientID");
                        if (v != null && Convert.ToString(v) != "0") return Convert.ToString(v);
                    }
                }
                catch { }
            }
            return null;
        }

        public static string FormatTeamViewerId(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            var t = id.Trim(); var sb = new System.Text.StringBuilder();
            for (int i = 0; i < t.Length; i++) { if (i > 0 && (t.Length - i) % 3 == 0) sb.Append(' '); sb.Append(t[i]); }
            return sb.ToString();
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

    // Farben und Zeichen wie im CRM (tailwind.config "brand", Statusfarben aus globals.css, public/favicon.svg).
    static class Brand
    {
        public static readonly Color Green = Color.FromArgb(0x74, 0xAD, 0x2F);       // brand-500
        public static readonly Color GreenLight = Color.FromArgb(0x96, 0xCC, 0x4A);  // brand-300
        public static readonly Color Amber = Color.FromArgb(0xF5, 0x9E, 0x0B);
        public static readonly Color Red = Color.FromArgb(0xEF, 0x44, 0x44);
        public static readonly Color Header = Color.FromArgb(0x0C, 0x11, 0x0B);      // dunkles Band wie Anmeldung und /betreuung
        public static readonly Color HeaderMuted = Color.FromArgb(0xA4, 0xAE, 0x9B);

        // Hauptknopf: derselbe Verlauf wie im CRM (tailwind.config "brand-gradient", seit 11/2026 das dunklere Gruen,
        // damit weisse Schrift lesbar ist). Hier aendern, wenn das CRM ihn aendert.
        public static readonly Color PrimaryTop = Color.FromArgb(0x5F, 0x9A, 0x27);
        public static readonly Color PrimaryBottom = Color.FromArgb(0x4F, 0x82, 0x20);    // brand-600
        public static readonly Color PrimaryHover = Color.FromArgb(0x3F, 0x6A, 0x1A);     // brand-700
        public static readonly Color PrimaryText = Color.White;

        // Flaechen und Schrift gibt es hell und dunkel. Theme.Apply() waehlt die Fassung (vor dem Aufbau der Fenster).
        public static Color Surface, Ink, Ink2, Gray, GraySoft, Hover, Line, GreenText, GreenDark, GreenSoft, AmberSoft, AmberDark, RedSoft, RedDark;
        public static bool Dark;

        static Brand() { UseLight(); }

        public static void UseLight()
        {
            Dark = false;
            Surface = Color.White;
            Ink = Color.FromArgb(0x1B, 0x21, 0x16);
            Ink2 = Color.FromArgb(0x5B, 0x64, 0x52);
            Gray = Color.FromArgb(0x8A, 0x8F, 0x84);
            GraySoft = Color.FromArgb(0xF0, 0xF1, 0xEE);
            Hover = Color.FromArgb(0xF6, 0xF7, 0xF3);
            Line = Color.FromArgb(0xDF, 0xE3, 0xD8);
            GreenText = Color.FromArgb(0x4F, 0x82, 0x20);   // brand-600, gruener Text auf der Flaeche
            GreenDark = Color.FromArgb(0x3F, 0x6A, 0x1A);   // brand-700, Text auf hellgruener Karte
            GreenSoft = Color.FromArgb(0xE4, 0xF2, 0xCF);   // brand-100
            AmberSoft = Color.FromArgb(0xFE, 0xF3, 0xC7);
            AmberDark = Color.FromArgb(0x92, 0x40, 0x0E);
            RedSoft = Color.FromArgb(0xFE, 0xE2, 0xE2);
            RedDark = Color.FromArgb(0x99, 0x1B, 0x1B);
        }

        // Dunkle Fassung (Farben wie der dunkle Modus des CRM).
        public static void UseDark()
        {
            Dark = true;
            Surface = Color.FromArgb(0x15, 0x1B, 0x14);
            Ink = Color.FromArgb(0xEE, 0xF2, 0xEA);
            Ink2 = Color.FromArgb(0xA4, 0xAE, 0x9B);
            Gray = Color.FromArgb(0x7C, 0x85, 0x74);
            GraySoft = Color.FromArgb(0x20, 0x28, 0x1E);
            Hover = Color.FromArgb(0x26, 0x2F, 0x23);
            Line = Color.FromArgb(0x2C, 0x36, 0x29);
            GreenText = Color.FromArgb(0x8D, 0xC5, 0x3F);
            GreenDark = Color.FromArgb(0xB9, 0xE5, 0x82);
            GreenSoft = Color.FromArgb(0x1E, 0x33, 0x10);
            AmberSoft = Color.FromArgb(0x3A, 0x2A, 0x0B);
            AmberDark = Color.FromArgb(0xFC, 0xD3, 0x4D);
            RedSoft = Color.FromArgb(0x3B, 0x16, 0x16);
            RedDark = Color.FromArgb(0xFC, 0xA5, 0xA5);
        }
        public static Color Dot(Health h)
        {
            switch (h) { case Health.Ok: return Green; case Health.Late: return Amber; case Health.Error: return Red; default: return Gray; }
        }

        // Symbol fuer den Infobereich.
        public static Icon MakeIcon(Health h, int size)
        {
            using (var bmp = DrawBadge(h, size, h == Health.Late || h == Health.Error))
            {
                IntPtr hIcon = bmp.GetHicon();
                var ico = (Icon)Icon.FromHandle(hIcon).Clone();
                DestroyIcon(hIcon);
                return ico;
            }
        }

        // Das App-Zeichen des CRM (public/favicon.svg, Raster 512): gruenes Quadrat, "IT" aus Balken, darunter die Puls-Linie.
        // Alles in Ordnung = das Zeichen ohne Zusatz. Verspaetet/Fehler = Punkt rechts unten (gelb/rot). Nicht eingerichtet = grau.
        // Gezeichnet wird vierfach und dann verkleinert - so bleiben die Kanten auch bei 16 px sauber (Schrift taete das nicht).
        public static Bitmap DrawBadge(Health h, int size, bool withDot)
        {
            const int ss = 4;
            int big = size * ss;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var hi = new Bitmap(big, big, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(hi))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float u = big / 512f;
                    using (var path = Rounded(new RectangleF(0, 0, big, big), 112 * u))
                    {
                        if (h == Health.Unknown) { using (var b = new SolidBrush(Gray)) g.FillPath(b, path); }
                        else { using (var b = new LinearGradientBrush(new PointF(0, 0), new PointF(big, big), Color.FromArgb(0x8C, 0xC6, 0x3F), Color.FromArgb(0x64, 0xA8, 0x1F))) g.FillPath(b, path); }
                    }
                    // Puls-Linie erst ab 24 px (darunter nur Matsch) und nie unter dem Punkt.
                    bool pulse = !withDot && size >= 24;
                    // Ohne Puls-Linie steht "IT" mittig; mit Punkt rueckt es nach links oben, damit der Punkt nichts verdeckt.
                    float dx = withDot ? -34f : 0f, dy = pulse ? 0f : withDot ? 12f : 39f;
                    using (var w = new SolidBrush(Color.White))
                    {
                        FillRound(g, w, (140 + dx) * u, (122 + dy) * u, 64 * u, 190 * u, 14 * u);
                        FillRound(g, w, (232 + dx) * u, (122 + dy) * u, 188 * u, 64 * u, 14 * u);
                        FillRound(g, w, (294 + dx) * u, (160 + dy) * u, 64 * u, 152 * u, 14 * u);
                    }
                    if (pulse)
                    {
                        using (var p = new Pen(Color.FromArgb(235, 255, 255, 255), 26 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                            g.DrawLines(p, new[] { new PointF(86 * u, 396 * u), new PointF(186 * u, 396 * u), new PointF(220 * u, 344 * u), new PointF(262 * u, 436 * u), new PointF(296 * u, 396 * u), new PointF(426 * u, 396 * u) });
                    }
                    if (withDot)
                    {
                        float d = big * 0.46f, ring = big * 0.06f;
                        var dot = new RectangleF(big - d, big - d, d, d);
                        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, dot);
                        dot.Inflate(-ring, -ring);
                        using (var b = new SolidBrush(Dot(h))) g.FillEllipse(b, dot);
                    }
                }
                using (var g = Graphics.FromImage(bmp))
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);   // sonst dunkelt der Rand beim Verkleinern ab
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(hi, new Rectangle(0, 0, size, size), 0, 0, big, big, GraphicsUnit.Pixel, ia);
                }
            }
            return bmp;
        }

        static void FillRound(Graphics g, Brush b, float x, float y, float w, float h, float r)
        {
            using (var p = Rounded(new RectangleF(x, y, w, h), r)) g.FillPath(b, p);
        }

        // Weisses Logo fuer das dunkle Kopfband (eingebettet: tray\logo-white.png). Fehlt es, steht der Name als Text da.
        public static Bitmap Logo(int height)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo-white.png"))
                {
                    if (s != null)
                        using (var src = new Bitmap(s))
                        {
                            int w = (int)Math.Round(src.Width * (double)height / src.Height);
                            var bmp = new Bitmap(w, height, PixelFormat.Format32bppArgb);
                            using (var g = Graphics.FromImage(bmp))
                            using (var ia = new ImageAttributes())
                            {
                                ia.SetWrapMode(WrapMode.TileFlipXY);
                                g.Clear(Header);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(src, new Rectangle(0, 0, w, height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                            }
                            return bmp;
                        }
                }
            }
            catch { }
            var f = Ui.F(12f, FontStyle.Bold);
            var size = TextRenderer.MeasureText("IT-Solutions", f);
            var t = new Bitmap(size.Width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(t))
            {
                g.Clear(Header);
                TextRenderer.DrawText(g, "IT-Solutions", f, new Rectangle(0, 0, size.Width, height), Color.White, Header, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            return t;
        }

        public static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }

    // Darstellung hell/dunkel. "auto" folgt Windows (Einstellung "App-Modus"), "light"/"dark" legt der Benutzer fest -
    // im Kopf des Fensters (Sonne/Mond) oder im Menue des Symbols. Gemerkt je Benutzer in der Registry.
    static class Theme
    {
        const string KeyPath = "Software\\IT-Solutions\\Agent";
        public static string Mode = "auto";

        public static void Load()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    var v = k == null ? null : k.GetValue("Theme") as string;
                    if (v == "light" || v == "dark" || v == "auto") Mode = v;
                }
            }
            catch { }
            Apply();
        }

        public static void Set(string mode)
        {
            Mode = mode;
            try { using (var k = Registry.CurrentUser.CreateSubKey(KeyPath)) k.SetValue("Theme", mode); } catch { }
            Apply();
        }

        public static bool WindowsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static bool WantsDark { get { return Mode == "dark" || (Mode == "auto" && WindowsDark()); } }

        // Stellt die Farben um; true, wenn sich die Fassung geaendert hat.
        public static bool Apply()
        {
            bool dark = WantsDark;
            if (dark == Brand.Dark) return false;
            if (dark) Brand.UseDark(); else Brand.UseLight();
            return true;
        }
    }

    // Masse und Schriften. Positionen werden selbst nach der Bildschirm-Skalierung gerechnet (AutoScaleMode.None).
    static class Ui
    {
        // Control.DeviceDpi liefert in .NET 4.8 ohne Per-Monitor-Konfiguration immer 96 - die echte System-DPI kommt aus GDI.
        static float dpi;
        static float Dpi { get { if (dpi <= 0) { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; } return dpi; } }
        public static int S(int px) { return (int)Math.Round(px * Dpi / 96.0); }
        public static int Radius { get { return S(8); } }

        static readonly System.Collections.Generic.Dictionary<string, Font> fonts = new System.Collections.Generic.Dictionary<string, Font>();
        public static Font F(float pt) { return F(pt, FontStyle.Regular); }
        public static Font F(float pt, FontStyle fs) { return F("Segoe UI", pt, fs); }
        public static Font F(string family, float pt, FontStyle fs)
        {
            var k = family + "|" + pt + "|" + (int)fs;
            Font f;
            if (!fonts.TryGetValue(k, out f)) { f = new Font(family, pt, fs); fonts[k] = f; }
            return f;
        }
        public static int TextH(Font f) { return TextRenderer.MeasureText("Ag", f).Height; }

        // Zeichen-Schrift von Windows 10/11 (Telefon, Brief, Weltkugel). Auf aelteren Servern fehlt sie - dann ohne Zeichen.
        public const string IconFamily = "Segoe MDL2 Assets";
        static int hasIcons = -1;
        public static bool HasIconFont
        {
            get
            {
                if (hasIcons < 0) { try { using (var f = new Font(IconFamily, 9f)) hasIcons = f.Name == IconFamily ? 1 : 0; } catch { hasIcons = 0; } }
                return hasIcons == 1;
            }
        }

        public const ControlStyles OwnPaint = ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw;
    }

    // Knopf mit runden Ecken wie im CRM: Hauptknopf im gruenen Verlauf, sonst weiss mit Rand.
    class RoundButton : Button
    {
        public bool Primary;
        bool hover, down;

        public RoundButton()
        {
            SetStyle(Ui.OwnPaint, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
            Font = Ui.F(9f, FontStyle.Bold);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Parent != null ? Parent.BackColor : Brand.Surface);
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Color text;
            using (var path = Brand.Rounded(r, Ui.Radius))
            {
                if (!Enabled)
                {
                    using (var b = new SolidBrush(Brand.GraySoft)) g.FillPath(b, path);
                    text = Brand.Gray;
                }
                else if (Primary)
                {
                    Color top = down ? Brand.PrimaryBottom : Brand.PrimaryTop, bottom = hover || down ? Brand.PrimaryHover : Brand.PrimaryBottom;
                    using (var b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height + 1), top, bottom, 90f)) g.FillPath(b, path);
                    text = Brand.PrimaryText;
                }
                else
                {
                    using (var b = new SolidBrush(down ? Brand.GraySoft : hover ? Brand.Hover : Brand.Surface)) g.FillPath(b, path);
                    using (var p = new Pen(Brand.Line)) g.DrawPath(p, path);
                    text = Brand.Ink;
                }
            }
            if (Focused && ShowFocusCues)
            {
                var fr = new RectangleF(2.5f, 2.5f, Width - 5f, Height - 5f);
                using (var path = Brand.Rounded(fr, Math.Max(1, Ui.Radius - 2)))
                using (var p = new Pen(Primary ? Color.White : Brand.GreenText, 1f) { DashStyle = DashStyle.Dot }) g.DrawPath(p, path);
            }
            PaintLabel(g, text);
        }

        // Beschriftung. Im Zeichnen nie Eigenschaften setzen (Text, Font ...): jede Aenderung fordert ein neues Zeichnen an.
        protected virtual void PaintLabel(Graphics g, Color color)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Kachel: Zeichen oben, Beschriftung darunter - fuer die Aktionen im kompakten Aufbau.
    class TileButton : RoundButton
    {
        public string Glyph = "";

        // Flaeche, Rand und Fokus zeichnet der normale Knopf; hier nur Zeichen und Beschriftung.
        protected override void PaintLabel(Graphics g, Color fg)
        {
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            int th = Ui.TextH(Font), gh = Ui.HasIconFont ? Ui.S(22) : 0, top = (Height - gh - th) / 2;
            if (gh > 0)
                TextRenderer.DrawText(g, Glyph, Ui.F(Ui.IconFamily, 13f, FontStyle.Regular), new Rectangle(0, top, Width, gh), Primary || !Enabled ? fg : Brand.GreenText, flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(2), top + gh + Ui.S(2), Width - Ui.S(4), th), fg, flags);
        }
    }

    // Zustand als Karte: Punkt, fette Zeile, darunter eine ruhige zweite Zeile.
    class StatusCard : Control
    {
        string title = "", sub = "";
        Color bg = Brand.GraySoft, fg = Brand.Ink2, dot = Brand.Gray;

        public StatusCard() { SetStyle(Ui.OwnPaint, true); }

        /** Zweite Zeile ueber bis zu zwei Zeilen umbrechen statt abschneiden. */
        public bool WrapSub;

        public static int HeightFor(bool twoLines) { return HeightFor(twoLines ? 1 : 0); }

        public static int HeightFor(int subLines)
        {
            return Ui.TextH(Ui.F(9.5f, FontStyle.Bold)) + subLines * Ui.TextH(Ui.F(8.5f)) + Ui.S(18);
        }

        public void Set(string title, string sub, Color bg, Color fg, Color dot)
        {
            this.title = title ?? ""; this.sub = sub ?? ""; this.bg = bg; this.fg = fg; this.dot = dot;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Parent != null ? Parent.BackColor : Brand.Surface);
            using (var path = Brand.Rounded(new RectangleF(0, 0, Width, Height), Ui.Radius))
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            int pad = Ui.S(12), d = Ui.S(10);
            Font tf = Ui.F(9.5f, FontStyle.Bold), sf = Ui.F(8.5f);
            int tx = pad + d + Ui.S(9), tw = Width - tx - pad;
            var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            var wrap = TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int th = Ui.TextH(tf), line = Ui.TextH(sf), sh = 0;
            if (sub.Length > 0) sh = WrapSub ? Math.Min(TextRenderer.MeasureText(g, sub, sf, new Size(tw, 0), wrap).Height, line * 2) : line;
            int top = (Height - th - sh) / 2;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, pad, top + (th - d) / 2f + 1, d, d);
            TextRenderer.DrawText(g, title, tf, new Rectangle(tx, top, tw, th), fg, bg, flags);
            if (sh > 0) TextRenderer.DrawText(g, sub, sf, new Rectangle(tx, top + th, tw, sh), fg, bg, WrapSub ? wrap : flags);
        }
    }

    // Detailfenster ueber dem Infobereich (rahmenlos, schliesst beim Verlassen).
    // Aufbau von oben: Kopfband mit Logo, Zustand, drei Aktionen als Kacheln, das Wichtigste fuer das Telefonat
    // (Rechner, TeamViewer-ID), alle weiteren Angaben erst auf Wunsch, zuletzt der Kontakt. Hell und dunkel (Theme).
    class DetailsForm : Form
    {
        readonly TrayApp app;
        AgentStatus st;
        StatusCard card;
        readonly ToolTip tips = new ToolTip();
        bool rounded;
        // Kompakter Aufbau: alle Angaben aufgeklappt? (startet zu)
        public bool Expanded;

        // Hinweis in der Zustandskarte (z.B. Update-Pruefung). kind: 0 = neutral, 1 = ok, 2 = Warnung
        public void SetNotice(string text, int kind)
        {
            if (card == null || card.IsDisposed) return;
            card.Set(text, "",
                kind == 1 ? Brand.GreenSoft : kind == 2 ? Brand.AmberSoft : Brand.GraySoft,
                kind == 1 ? Brand.GreenDark : kind == 2 ? Brand.AmberDark : Brand.Ink2,
                kind == 1 ? Brand.Green : kind == 2 ? Brand.Amber : Brand.Gray);
        }

        public DetailsForm(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Brand.Surface;
            AutoScaleMode = AutoScaleMode.None;   // Positionen werden selbst nach DPI skaliert (Ui.S)
            Font = Ui.F(9f);
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += (s, e) => { if (!app.KeepDetailsOpen) Close(); };
        }

        // Schatten (alle Windows-Versionen) und runde Fensterecken (Windows 11; aeltere Versionen lehnen das still ab).
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; rounded = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0; } catch { rounded = false; }
        }
        public bool Rounded { get { return rounded; } }

        public void Fill(AgentStatus s)
        {
            st = s;
            SuspendLayout();
            Controls.Clear();
            BackColor = Brand.Surface;
            Invalidate();
            int w = Scale(348), x = Scale(18), inner = w - 2 * x, y;

            // Kopfband: Logo links (Klick = auf Updates pruefen), rechts "Agent" und die Version
            int headH = Scale(62);
            var head = new Panel { BackColor = Brand.Header, Location = new Point(0, 0), Size = new Size(w, headH) };
            var logoImg = Brand.Logo(Scale(28));
            var logo = new PictureBox { Image = logoImg, BackColor = Brand.Header, Size = logoImg.Size, Location = new Point(x, (headH - logoImg.Height) / 2), Cursor = Cursors.Hand };
            tips.SetToolTip(logo, "Auf Updates prüfen");
            logo.Click += (a, b) => app.CheckForUpdatesNow(this);
            head.Controls.Add(logo);
            Font nameFont = Ui.F(9.5f, FontStyle.Bold), verFont = Ui.F(8f);
            int tog = Ui.HasIconFont ? Scale(30) : 0;
            if (tog > 0)
            {
                // Sonne = auf hell stellen, Mond = auf dunkel stellen
                var sw = new Label { Text = Brand.Dark ? "\uE706" : "\uE708", Font = Ui.F(Ui.IconFamily, 11f, FontStyle.Regular), ForeColor = Brand.HeaderMuted, BackColor = Brand.Header, Location = new Point(w - x - tog + Scale(6), 0), Size = new Size(tog, headH), TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand, UseMnemonic = false };
                tips.SetToolTip(sw, Brand.Dark ? "Helle Darstellung" : "Dunkle Darstellung");
                sw.MouseEnter += (a, b) => sw.ForeColor = Color.White;
                sw.MouseLeave += (a, b) => sw.ForeColor = Brand.HeaderMuted;
                sw.Click += (a, b) => { Theme.Set(Brand.Dark ? "light" : "dark"); Fill(st); };
                head.Controls.Add(sw);
            }
            int nameH = TextH(nameFont), verH = TextH(verFont), rightW = Scale(90), top = (headH - nameH - verH) / 2;
            head.Controls.Add(new Label { Text = "Agent", Font = nameFont, ForeColor = Color.White, BackColor = Brand.Header, Location = new Point(w - x - tog - rightW, top), Size = new Size(rightW, nameH), TextAlign = ContentAlignment.TopRight, UseMnemonic = false });
            var ver = new Label { Text = s.AgentVersion > 0 ? "Version " + s.AgentVersion : "nicht eingerichtet", Font = verFont, ForeColor = Brand.HeaderMuted, BackColor = Brand.Header, Location = new Point(w - x - tog - rightW, top + nameH), Size = new Size(rightW, verH), TextAlign = ContentAlignment.TopRight, UseMnemonic = false };
            tips.SetToolTip(ver, "Skript v" + s.AgentVersion + " · Symbol " + TrayApp.RunningVersionText);
            head.Controls.Add(ver);
            Controls.Add(head);
            y = headH + Scale(14);

            // Zustand
            string title, sub; Color bg, fg;
            switch (s.Health)
            {
                case Health.Ok: bg = Brand.GreenSoft; fg = Brand.GreenDark; title = "Verbunden"; sub = "Letzter Bericht " + s.AgeText(); break;
                case Health.Late: bg = Brand.AmberSoft; fg = Brand.AmberDark; title = "Bericht verspätet"; sub = "Zuletzt " + s.AgeText(); break;
                case Health.Error:
                    bg = Brand.RedSoft; fg = Brand.RedDark;
                    title = s.LastSuccessAt.HasValue ? "Keine Verbindung" : "Bericht fehlgeschlagen";
                    sub = s.LastMessage ?? (s.LastSuccessAt.HasValue ? "Zuletzt " + s.AgeText() : "Bitte IT-Solutions verständigen");
                    break;
                default: bg = Brand.GraySoft; fg = Brand.Ink2; title = "Noch nicht eingerichtet"; sub = "Der erste Bericht steht noch aus"; break;
            }
            card = new StatusCard { Location = new Point(x, y), Size = new Size(inner, StatusCard.HeightFor(true)) };
            card.Set(title, sub, bg, fg, Brand.Dot(s.Health));
            Controls.Add(card);
            y += card.Height + Scale(12);

            FillBody(s, w, x, inner, y);
        }
        // Aktionen als Kacheln, das Wichtigste in zwei Zeilen, alle Angaben erst auf Wunsch.
        void FillBody(AgentStatus s, int w, int x, int inner, int y)
        {
            int gap = Scale(8), n = s.TicketsEnabled ? 3 : 2, th = Scale(64), tx = x;
            // Mit drei Kacheln bekommt der Hauptknopf etwas mehr Breite, sonst passt "Problem melden" nicht.
            int tw = (inner - (n - 1) * gap) / n, first = n == 3 ? tw + Scale(14) : tw, other = n == 3 ? (inner - 2 * gap - first) / 2 : tw;
            if (s.TicketsEnabled)
            {
                var problem = Tile("\uE8BD", "Problem melden", tx, y, first, th, true); problem.Click += (a, b) => app.ShowProblemForm();
                Controls.Add(problem); tx += first + gap;
            }
            var support = Tile("\uE8AF", "Fernwartung", tx, y, other, th, !s.TicketsEnabled); support.Click += (a, b) => app.StartSupport();
            Controls.Add(support); tx += other + gap;
            var report = Tile("\uE724", "Status senden", tx, y, x + inner - tx, th, false); report.Click += (a, b) => app.ReportNow();
            tips.SetToolTip(report, "Meldet den Zustand dieses Rechners jetzt an IT-Solutions");
            Controls.Add(report);
            y += th + Scale(14);

            if (Expanded) y = Facts(s, x, y, inner);
            else
            {
                // Das fragt der Techniker am Telefon: Name und Adresse, TeamViewer-ID
                y = Row("Rechner", s.Hostname + (s.Ip != null ? " \u00b7 " + s.Ip : ""), x, y, inner);
                var tv = AgentStatus.ReadTeamViewerId() ?? s.TeamViewerId;
                if (tv != null) y = TeamViewerRow(tv, x, y, inner);
            }
            var more = new Label { Text = Expanded ? "Weniger anzeigen" : "Alle Angaben anzeigen", Font = Ui.F(8.5f), ForeColor = Brand.GreenText, Location = new Point(x, y + Scale(2)), Size = new Size(inner, TextH(Ui.F(8.5f)) + Scale(6)), Cursor = Cursors.Hand, UseMnemonic = false };
            more.Click += (a, b) => { Expanded = !Expanded; Fill(st); };
            Controls.Add(more);
            y += more.Height + Scale(8);

            y = Contact(s, x, y, inner);
            Finish(w, y);
        }

        TileButton Tile(string glyph, string text, int x, int y, int w, int h, bool primary)
        {
            return new TileButton { Glyph = glyph, Text = text, Primary = primary, Location = new Point(x, y), Size = new Size(w, h), Font = Ui.F(8f, FontStyle.Bold) };
        }

        int TeamViewerRow(string tv, int x, int y, int inner)
        {
            y = Row("TeamViewer-ID", AgentStatus.FormatTeamViewerId(tv), x, y, inner, Brand.GreenText);
            var val = Controls[Controls.Count - 1];
            val.Cursor = Cursors.Hand;
            tips.SetToolTip(val, "Klicken zum Kopieren");
            val.Click += (a, b) => { try { Clipboard.SetText(tv); SetNotice("TeamViewer-ID kopiert: " + AgentStatus.FormatTeamViewerId(tv), 1); } catch { } };
            return y;
        }

        // Alle Angaben: "Dieser Rechner" und "Betreuung".
        int Facts(AgentStatus s, int x, int y, int inner)
        {
            // Dieser Rechner - das fragt der Techniker am Telefon
            y = Section("Dieser Rechner", x, y, inner);
            y = Row("Name", s.Hostname + (s.Domain != null ? " (" + s.Domain + ")" : ""), x, y, inner);
            y = Row("Benutzer", Environment.UserDomainName + "\\" + Environment.UserName, x, y, inner);
            y = Row("IP-Adresse", s.Ip ?? "–", x, y, inner);
            var device = ((s.Manufacturer ?? "") + " " + (s.Model ?? "")).Trim();
            if (device.Length > 0) y = Row("Gerät", device, x, y, inner);
            if (s.Serial != null) y = Row("Seriennummer", s.Serial, x, y, inner);
            var tv = AgentStatus.ReadTeamViewerId() ?? s.TeamViewerId;
            if (tv != null) y = TeamViewerRow(tv, x, y, inner);

            // Betreuung
            y += Scale(8);
            y = Section("Betreuung", x, y, inner);
            if (s.CustomerName != null) y = Row("Kunde", s.CustomerName, x, y, inner);
            if (s.ContractNo != null) y = Row("Wartungsvertrag", s.ContractNo, x, y, inner);
            var osText = s.OsProduct ?? (s.OsName != null ? s.OsName.Replace("Microsoft ", "") : null);
            if (osText != null) y = Row("Betriebssystem", osText, x, y, inner);
            if (s.OsSupportEnd.HasValue)
            {
                var days = (s.OsSupportEnd.Value.Date - DateTime.Today).TotalDays;
                var txtEnd = days < 0 ? "beendet seit " + s.OsSupportEnd.Value.ToString("dd.MM.yyyy") : "bis " + s.OsSupportEnd.Value.ToString("dd.MM.yyyy");
                y = Row("Sicherheitsupdates", txtEnd, x, y, inner, days < 0 ? Brand.RedDark : days < 180 ? Brand.AmberDark : (Color?)null);
            }
            if (s.LastPatchAt.HasValue)
            {
                var age = (DateTime.Now - s.LastPatchAt.Value).TotalDays;
                y = Row("Letztes Update", s.LastPatchAt.Value.ToString("dd.MM.yyyy"), x, y, inner, age > 60 ? Brand.AmberDark : (Color?)null);
            }
            return y;
        }

        // Kontakt: Telefon (Klick kopiert die Nummer) und Mail nebeneinander.
        int Contact(AgentStatus s, int x, int y, int inner)
        {
            Controls.Add(new Label { BackColor = Brand.Line, Location = new Point(x, y), Size = new Size(inner, 1) });
            y += Scale(10);
            var phoneNo = s.Provider.Phone; var mail = s.Provider.Email;
            int half = inner * 46 / 100;
            LinkRow("\uE717", phoneNo, "Klicken zum Kopieren", x, y, half, () => { try { Clipboard.SetText(phoneNo); SetNotice("Telefonnummer kopiert: " + phoneNo, 1); } catch { } });
            y = LinkRow("\uE715", mail, "Mail an IT-Solutions schreiben", x + half, y, inner - half, () => TrayApp.Open("mailto:" + mail + "?subject=" + Uri.EscapeDataString("Support " + s.Hostname)));
            y += Scale(6);
            var foot = Lbl("Verbunden mit " + s.CrmHost, x, y, inner, 7.5f, FontStyle.Regular, Brand.Gray);
            Controls.Add(foot);
            return y + foot.Height + Scale(12);
        }
        void Finish(int w, int y)
        {
            ClientSize = new Size(w, y);
            ResumeLayout();
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Scale(12), wa.Bottom - Height - Scale(12));
        }

        public void KeepDetailsOpenForRender() { app.KeepDetailsOpen = true; }

        // Abschnittstitel: klein, fett, Grossbuchstaben
        int Section(string title, int x, int y, int w)
        {
            var l = Lbl(title.ToUpperInvariant(), x, y, w, 7.5f, FontStyle.Bold, Brand.Gray);
            Controls.Add(l);
            return y + l.Height + Scale(3);
        }

        int Row(string k, string v, int x, int y, int w) { return Row(k, v, x, y, w, null); }

        // Bezeichnung links (so breit wie noetig), Wert rechtsbuendig daneben. Lange Werte (Kundennamen)
        // brechen in bis zu zwei Zeilen um statt abgeschnitten zu werden.
        int Row(string k, string v, int x, int y, int w, Color? color)
        {
            var f = Ui.F(9f);
            int keyW = TextRenderer.MeasureText(k, f).Width + Scale(10);
            int valW = w - keyW;
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.Right;
            int lineH = TextH(f);
            int h = TextRenderer.MeasureText(v, f, new Size(valW, 0), flags).Height;
            h = Math.Min(Math.Max(h, lineH), lineH * 2);
            Controls.Add(Lbl(k, x, y, keyW, 9f, FontStyle.Regular, Brand.Ink2));
            var val = new Label { Text = v, Location = new Point(x + keyW, y), Size = new Size(valW, h + Scale(1)), Font = f, ForeColor = color ?? Brand.Ink,
                                  TextAlign = ContentAlignment.TopRight, AutoEllipsis = true, UseMnemonic = false };
            Controls.Add(val);
            return y + h + Scale(4);
        }

        // Kontaktzeile: Zeichen + gruener Text, die ganze Zeile ist anklickbar.
        int LinkRow(string glyph, string text, string tip, int x, int y, int w, Action onClick)
        {
            var f = Ui.F(9f);
            int h = TextH(f) + Scale(5), gw = Ui.HasIconFont ? Scale(24) : 0;
            EventHandler click = (a, b) => onClick();
            if (gw > 0)
            {
                var gl = new Label { Text = glyph, Font = Ui.F(Ui.IconFamily, 9.5f, FontStyle.Regular), ForeColor = Brand.Ink2, Location = new Point(x, y), Size = new Size(gw, h), TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Cursor = Cursors.Hand };
                gl.Click += click; tips.SetToolTip(gl, tip);
                Controls.Add(gl);
            }
            var l = new Label { Text = text, Font = f, ForeColor = Brand.GreenText, Location = new Point(x + gw, y), Size = new Size(w - gw, h), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Cursor = Cursors.Hand };
            l.MouseEnter += (a, b) => l.Font = Ui.F(9f, FontStyle.Underline);
            l.MouseLeave += (a, b) => l.Font = f;
            l.Click += click; tips.SetToolTip(l, tip);
            Controls.Add(l);
            return y + h;
        }

        Label Lbl(string t, int x, int y, int w, float pt, FontStyle fs, Color c)
        {
            var f = Ui.F(pt, fs);
            return new Label { Text = t, Location = new Point(x, y), Size = new Size(w, TextH(f) + Scale(1)), Font = f, ForeColor = c, AutoEllipsis = true, UseMnemonic = false };
        }

        static int Scale(int px) { return Ui.S(px); }
        static int TextH(Font f) { return Ui.TextH(f); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Rand: folgt den runden Ecken, wenn Windows sie zeichnet (11), sonst ein gerader Rahmen.
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Brand.Line))
            {
                if (rounded) { using (var path = Brand.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Scale(8))) e.Graphics.DrawPath(p, path); }
                else e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
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
            // Bei jedem Oeffnen neu aufbauen: "Problem melden" haengt von der Freischaltung des Kunden ab (status.json).
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                menu.Items.Add("Details anzeigen", null, (a, b) => ShowDetails());
                if (status != null && status.TicketsEnabled) menu.Items.Add("Problem melden", null, (a, b) => ShowProblemForm());
                menu.Items.Add("Fernwartung starten", null, (a, b) => StartSupport());
                menu.Items.Add("Status senden", null, (a, b) => ReportNow());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Mail an IT-Solutions", null, (a, b) => Open("mailto:" + status.Provider.Email + "?subject=" + Uri.EscapeDataString("Support " + status.Hostname)));
                menu.Items.Add("Website öffnen", null, (a, b) => Open("https://" + status.Provider.Website.Replace("https://", "").Replace("http://", "")));
                var view = new ToolStripMenuItem("Darstellung");
                foreach (var m in new[] { new[] { "auto", "Wie Windows" }, new[] { "light", "Hell" }, new[] { "dark", "Dunkel" } })
                {
                    var mode = m[0];
                    var it = new ToolStripMenuItem(m[1]) { Checked = Theme.Mode == mode };
                    it.Click += (a, b) => { Theme.Set(mode); if (details != null && !details.IsDisposed && details.Visible) details.Fill(status ?? AgentStatus.Load()); };
                    view.DropDownItems.Add(it);
                }
                menu.Items.Add(view);
                if (IsAdmin())
                {
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("Symbol vorübergehend ausblenden", null, (a, b) => ExitThread());
                }
                e.Cancel = false;   // ein leeres Menue wuerde Windows sonst gar nicht oeffnen
            };
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
            string state;
            switch (s.Health)
            {
                case Health.Ok: state = "Verbunden · letzter Bericht " + s.AgeText(); break;
                case Health.Late: state = "Bericht verspätet · zuletzt " + s.AgeText(); break;
                case Health.Error: state = "Keine Verbindung"; break;
                default: state = "Noch nicht eingerichtet"; break;
            }
            var t = "IT-Solutions Agent" + (s.AgentVersion > 0 ? " v" + s.AgentVersion : "") + "\n"
                  + state + "\n"
                  + s.Hostname + (s.Ip != null ? " · " + s.Ip : "");
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
            Theme.Apply();   // "Wie Windows": eine Umstellung in Windows greift beim naechsten Oeffnen
            details.Fill(AgentStatus.Load());
            details.Show();
            details.Activate();
        }

        public static string RunningVersionText { get { return RunningVersion.Major + "." + RunningVersion.Minor; } }

        // Klick auf das Logo: aktiv beim Server nachfragen. Gibt es eine neuere Skript-Version oder ein neues Symbol,
        // wird der Collector-Task sofort gestartet - er laedt das Update (mit Signaturpruefung) und das Symbol startet neu.
        public void CheckForUpdatesNow(DetailsForm form)
        {
            form.SetNotice("Suche nach Updates …", 0);
            var ctx = SynchronizationContext.Current;
            var st = AgentStatus.Load();
            var t = new Thread(() =>
            {
                string msg; int kind;
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    string baseUrl = "https://" + st.CrmHost + "/agent/";
                    int remote = 0;
                    using (var wc = new WebClient())
                    {
                        wc.Encoding = System.Text.Encoding.UTF8;
                        var head = wc.DownloadString(baseUrl + "collector.ps1");
                        var mm = System.Text.RegularExpressions.Regex.Match(head, @"collector-version:\s*(\d+)");
                        if (mm.Success) remote = int.Parse(mm.Groups[1].Value);
                        string remoteSig = wc.DownloadString(baseUrl + "IT-Solutions-Agent.exe.sig").Trim();
                        string localSig = "";
                        try { localSig = File.ReadAllText(ExePath + ".sig").Trim(); } catch { }
                        bool scriptNew = remote > st.AgentVersion;
                        bool trayNew = localSig.Length > 0 && remoteSig != localSig;
                        if (scriptNew || trayNew)
                        {
                            bool started = RunCollectorTask();
                            msg = started
                                ? "Update wird installiert … (" + (scriptNew ? "Skript v" + remote : "neues Symbol") + ")"
                                : "Update vorhanden, Start nicht möglich – kommt beim nächsten Lauf";
                            kind = started ? 0 : 2;
                        }
                        else { msg = "Alles aktuell (v" + st.AgentVersion + ")"; kind = 1; }
                    }
                }
                catch (Exception ex) { msg = "Prüfung nicht möglich: " + ex.Message; kind = 2; }
                ctx.Post(_ => { if (!form.IsDisposed) form.SetNotice(msg, kind); }, null);
            });
            t.IsBackground = true; t.Start();
        }

        public static readonly string OutboxDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IT-Solutions-Outbox");

        public void ShowProblemForm()
        {
            // Im Testaufruf (--details) beendet das Schliessen des Detailfensters das Programm - hier erst nach dem Meldefenster.
            bool test = KeepDetailsOpen;
            KeepDetailsOpen = false;
            if (details != null && !details.IsDisposed) details.Close();
            using (var f = new ProblemForm(status ?? AgentStatus.Load())) f.ShowDialog();
            if (test) ExitThread();
        }

        public static bool StartCollector() { return RunCollectorTask(); }

        static bool RunCollectorTask()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"" + TaskName + "\"") { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(15000);
                return p.ExitCode == 0;
            }
            catch { return false; }
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

        public static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } }

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
    // "Problem melden": Meldung schreiben, als Datei in den Ausgangsordner legen, Collector anstossen und auf die
    // Quittung (Ticketnummer) warten. Das Token bleibt beim Collector (SYSTEM) - das Symbol kennt es nicht.
    // Eingabefeld mit runden Ecken: das eigentliche Textfeld sitzt randlos in einer selbst gezeichneten Flaeche,
    // der Rand wird beim Schreiben gruen.
    class InputBox : Panel
    {
        public readonly TextBox Box;

        static Color Fill { get { return Brand.Dark ? Brand.GraySoft : Color.White; } }

        public InputBox(bool multiline, int maxLength)
        {
            SetStyle(Ui.OwnPaint, true);
            Padding = new Padding(Ui.S(11), Ui.S(8), Ui.S(multiline ? 6 : 11), Ui.S(8));
            BackColor = Fill;
            Cursor = Cursors.IBeam;
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None, Multiline = multiline, ScrollBars = ScrollBars.None,
                MaxLength = maxLength, Dock = DockStyle.Fill, BackColor = Fill, ForeColor = Brand.Ink, Font = Ui.F(9.5f),
            };
            Box.GotFocus += (a, b) => Invalidate();
            Box.LostFocus += (a, b) => Invalidate();
            Controls.Add(Box);
            Click += (a, b) => Box.Focus();
            if (!multiline) Height = Box.PreferredHeight + Padding.Vertical;
            else Box.TextChanged += (a, b) => FitScrollBar();
        }

        // Der Rollbalken erscheint erst, wenn der Text laenger ist als das Feld (ein leerer Balken wirkt wie ein Fremdkoerper).
        void FitScrollBar()
        {
            int visible = Math.Max(1, Box.ClientSize.Height / Math.Max(1, Box.Font.Height));
            int lines = Box.GetLineFromCharIndex(Box.TextLength) + 1;
            var want = lines > visible ? ScrollBars.Vertical : ScrollBars.None;
            if (Box.ScrollBars == want) return;
            int at = Box.SelectionStart;
            Box.ScrollBars = want;      // erzeugt das Feld neu - Schreibmarke wiederherstellen
            Box.SelectionStart = at;
            Box.ScrollToCaret();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Parent != null ? Parent.BackColor : Brand.Surface);
            bool focus = Box.Focused;
            float inset = focus ? 1f : 0.5f;
            using (var path = Brand.Rounded(new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset), Ui.Radius))
            {
                using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
                using (var p = new Pen(focus ? Brand.GreenText : Brand.Line, focus ? 2f : 1f)) g.DrawPath(p, path);
            }
        }
    }

    // "Problem melden": eigenes Fenster im Stil des Detailfensters - dunkles Kopfband (daran laesst es sich ziehen),
    // runde Eingabefelder, Rueckmeldung als Karte, Knoepfe rechts unten. Strg+Eingabe sendet, Esc schliesst.
    class ProblemForm : Form
    {
        readonly AgentStatus st;
        readonly InputBox text, phone;
        readonly StatusCard info;
        readonly RoundButton send, cancel;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 2000 };
        string waitingFor; DateTime waitStart;
        bool rounded;

        static int S(int px) { return Ui.S(px); }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; rounded = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0; } catch { rounded = false; }
        }
        public bool Rounded { get { return rounded; } }

        public ProblemForm(AgentStatus s)
        {
            st = s;
            Text = "Problem melden – IT-Solutions";
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.None; BackColor = Brand.Surface; ForeColor = Brand.Ink; Font = Ui.F(9.5f);
            DoubleBuffered = true; KeyPreview = true;
            int w = S(460), x = S(22), inner = w - 2 * x, y;

            // Kopfband: Logo, Titel, Schliessen. Am Band laesst sich das Fenster verschieben.
            int headH = S(56), closeW = S(46);
            var head = new Panel { BackColor = Brand.Header, Location = new Point(0, 0), Size = new Size(w, headH) };
            var logoImg = Brand.Logo(S(24));
            var logo = new PictureBox { Image = logoImg, BackColor = Brand.Header, Size = logoImg.Size, Location = new Point(x, (headH - logoImg.Height) / 2) };
            var close = new Label
            {
                Text = Ui.HasIconFont ? "" : "x", Font = Ui.HasIconFont ? Ui.F(Ui.IconFamily, 8f, FontStyle.Regular) : Ui.F(10f, FontStyle.Bold),
                ForeColor = Brand.HeaderMuted, BackColor = Brand.Header, Location = new Point(w - closeW, 0), Size = new Size(closeW, headH),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, UseMnemonic = false,
            };
            close.MouseEnter += (a, b) => { close.ForeColor = Color.White; close.BackColor = Color.FromArgb(0xC4, 0x2B, 0x1C); };
            close.MouseLeave += (a, b) => { close.ForeColor = Brand.HeaderMuted; close.BackColor = Brand.Header; };
            close.Click += (a, b) => Close();
            int titleX = x + logo.Width + S(10);
            var title = new Label { Text = "Problem melden", Font = Ui.F(10f, FontStyle.Bold), ForeColor = Color.White, BackColor = Brand.Header, Location = new Point(titleX, 0), Size = new Size(w - closeW - titleX - S(2), headH), TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false };
            MouseEventHandler drag = (a, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };
            head.MouseDown += drag; logo.MouseDown += drag; title.MouseDown += drag;
            head.Controls.Add(logo); head.Controls.Add(title); head.Controls.Add(close);
            Controls.Add(head);
            y = headH + S(18);

            var hf = Ui.F(12f, FontStyle.Bold);
            Controls.Add(new Label { Text = "Was ist passiert?", Font = hf, ForeColor = Brand.Ink, Location = new Point(x, y), Size = new Size(inner, Ui.TextH(hf) + S(2)), UseMnemonic = false });
            y += Ui.TextH(hf) + S(6);
            var df = Ui.F(9f);
            Controls.Add(new Label { Text = "Beschreiben Sie kurz, was nicht funktioniert. IT-Solutions meldet sich bei Ihnen.", Font = df, ForeColor = Brand.Ink2, Location = new Point(x, y), Size = new Size(inner, Ui.TextH(df) * 2 + S(2)), UseMnemonic = false });
            y += Ui.TextH(df) * 2 + S(10);

            text = new InputBox(true, 3500) { Location = new Point(x, y), Size = new Size(inner, S(128)) };
            Controls.Add(text); y += text.Height + S(14);

            var lf = Ui.F(8.5f, FontStyle.Bold);
            Controls.Add(new Label { Text = "Rückrufnummer", Font = lf, ForeColor = Brand.Ink, Location = new Point(x, y), Size = new Size(S(110), Ui.TextH(lf) + S(2)), UseMnemonic = false });
            Controls.Add(new Label { Text = "optional", Font = Ui.F(8.5f), ForeColor = Brand.Gray, Location = new Point(x + S(110), y), Size = new Size(inner - S(110), Ui.TextH(lf) + S(2)), TextAlign = ContentAlignment.TopRight, UseMnemonic = false });
            y += Ui.TextH(lf) + S(6);
            phone = new InputBox(false, 50) { Location = new Point(x, y), Width = inner };
            Controls.Add(phone); y += phone.Height + S(12);

            // Offen sagen, was mitgeht
            var nf = Ui.F(8f);
            Controls.Add(new Label { Text = "Mitgeschickt werden Rechnername und Benutzer: " + s.Hostname + " · " + Environment.UserName, Font = nf, ForeColor = Brand.Gray, Location = new Point(x, y), Size = new Size(inner, Ui.TextH(nf) + S(2)), AutoEllipsis = true, UseMnemonic = false });
            y += Ui.TextH(nf) + S(12);

            // Rueckmeldung: erscheint erst beim Senden, das Fenster waechst dann um ihre Hoehe (Say)
            info = new StatusCard { WrapSub = true, Location = new Point(x, y), Size = new Size(inner, StatusCard.HeightFor(2)), Visible = false };
            Controls.Add(info); y += S(4);

            int bw = S(124), gap = S(8), bh = S(38);
            cancel = new RoundButton { Text = "Abbrechen", Location = new Point(x + inner - 2 * bw - gap, y), Size = new Size(bw, bh), DialogResult = DialogResult.Cancel };
            send = new RoundButton { Text = "Senden", Primary = true, Location = new Point(x + inner - bw, y), Size = new Size(bw, bh) };
            Controls.Add(cancel); Controls.Add(send);
            CancelButton = cancel;
            ClientSize = new Size(w, y + bh + S(20));

            send.Click += (a, b) => Send();
            cancel.Click += (a, b) => Close();
            KeyDown += (a, e) => { if (e.Control && e.KeyCode == Keys.Enter && send.Enabled) { e.SuppressKeyPress = true; Send(); } };
            Shown += (a, b) => text.Box.Focus();
            poll.Tick += (a, b) => CheckReceipt();
        }

        // kind: 0 = neutral, 1 = gut, 2 = Hinweis, 3 = Fehler
        void Say(int kind, string title, string sub)
        {
            info.Set(title, sub,
                kind == 1 ? Brand.GreenSoft : kind == 2 ? Brand.AmberSoft : kind == 3 ? Brand.RedSoft : Brand.GraySoft,
                kind == 1 ? Brand.GreenDark : kind == 2 ? Brand.AmberDark : kind == 3 ? Brand.RedDark : Brand.Ink2,
                kind == 1 ? Brand.Green : kind == 2 ? Brand.Amber : kind == 3 ? Brand.Red : Brand.Gray);
            if (info.Visible) return;
            int grow = info.Height + S(12);
            cancel.Top += grow; send.Top += grow;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + grow);
            info.Visible = true;
            Invalidate();
        }

        // Fuer das Entwurfsbild (--render --demo): ausgefuellt und mit der Rueckmeldung nach dem Senden
        public void Demo()
        {
            text.Box.Text = "Der Drucker am Empfang druckt seit heute früh nur leere Seiten. Neustart hat nichts gebracht.";
            phone.Box.Text = "0664 123 45 67";
            text.Box.Select(0, 0);
            Say(1, "Danke! Ticket T-2026-0412 ist angekommen", "Wir melden uns bei Ihnen.");
            cancel.Text = "Schließen";
            send.Enabled = false;
        }

        void Send()
        {
            var t = text.Box.Text.Trim();
            if (t.Length < 3) { Say(3, "Bitte beschreiben Sie kurz das Problem", ""); text.Box.Focus(); return; }
            if (!Directory.Exists(TrayApp.OutboxDir))
            {
                Say(3, "Senden ist auf diesem Rechner noch nicht eingerichtet", "Bitte rufen Sie an: " + st.Provider.Phone);
                return;
            }
            try
            {
                var msg = new System.Collections.Generic.Dictionary<string, object> {
                    { "user", Environment.UserDomainName + "\\" + Environment.UserName },
                    { "phone", phone.Box.Text.Trim() },
                    { "text", t },
                    { "createdAt", DateTime.UtcNow.ToString("o") } };
                var name = DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
                File.WriteAllText(Path.Combine(TrayApp.OutboxDir, name), new JavaScriptSerializer().Serialize(msg), new System.Text.UTF8Encoding(false));
                waitingFor = Path.Combine(TrayApp.OutboxDir, name + ".done");
            }
            catch (Exception ex)
            {
                Say(3, "Meldung konnte nicht gespeichert werden", ex.Message + " – bitte rufen Sie an: " + st.Provider.Phone);
                return;
            }
            send.Enabled = false; text.Box.ReadOnly = true; phone.Box.ReadOnly = true;
            Say(0, "Wird gesendet …", "");
            TrayApp.StartCollector();
            waitStart = DateTime.Now; poll.Start();
        }

        void CheckReceipt()
        {
            if (waitingFor != null && File.Exists(waitingFor))
            {
                poll.Stop();
                string r = ""; try { r = File.ReadAllText(waitingFor).Trim(); } catch { }
                if (r.StartsWith("OK"))
                {
                    var no = r.Length > 3 ? r.Substring(3).Trim() : "";
                    Say(1, no.Length > 0 ? "Danke! Ticket " + no + " ist angekommen" : "Danke! Ihre Meldung ist angekommen", "Wir melden uns bei Ihnen.");
                }
                else Say(3, "Meldung nicht angenommen", "Bitte rufen Sie an: " + st.Provider.Phone);
                cancel.Text = "Schließen";
                return;
            }
            if ((DateTime.Now - waitStart).TotalSeconds > 120)
            {
                poll.Stop();
                Say(2, "Meldung gespeichert", "Sie wird gesendet, sobald die Verbindung steht (spätestens in 10 Minuten).");
                cancel.Text = "Schließen";
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Brand.Line))
            {
                if (rounded) { using (var path = Brand.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), S(8))) e.Graphics.DrawPath(p, path); }
                else e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { poll.Stop(); base.OnFormClosed(e); }
    }


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

        // Protokoll fuer die Fehlersuche (SYSTEM schreibt, Benutzer duerfen lesen; wird klein gehalten).
        static void Log(string msg)
        {
            try
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IT-Solutions-Status", "launcher.log");
                if (File.Exists(f) && new FileInfo(f).Length > 64 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("s") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        public static int StartInAllSessions()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            IntPtr info; int count; int started = 0;
            Log("Start als " + WindowsIdentity.GetCurrent().Name + ", exe " + exe);
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out info, out count)) { Log("WTSEnumerateSessions Fehler " + Marshal.GetLastWin32Error()); return -1; }
            try
            {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var si = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                    Log("Sitzung " + si.SessionId + " Zustand " + si.State);
                    if (si.State != 0 /* WTSActive */ || si.SessionId == 0) continue;
                    IntPtr user, primary = IntPtr.Zero, env = IntPtr.Zero;
                    if (!WTSQueryUserToken(si.SessionId, out user)) { Log("  WTSQueryUserToken Fehler " + Marshal.GetLastWin32Error()); continue; }
                    try
                    {
                        if (!DuplicateTokenEx(user, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* Impersonation */, 1 /* Primary */, out primary)) { Log("  DuplicateTokenEx Fehler " + Marshal.GetLastWin32Error()); continue; }
                        CreateEnvironmentBlock(out env, primary, false);
                        var st = new STARTUPINFO(); st.cb = Marshal.SizeOf(typeof(STARTUPINFO)); st.lpDesktop = "winsta0\\default";
                        PROCESS_INFORMATION pi;
                        if (CreateProcessAsUser(primary, exe, "\"" + exe + "\"", IntPtr.Zero, IntPtr.Zero, false, 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, env, Path.GetDirectoryName(exe), ref st, out pi))
                        {
                            started++; CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                            Log("  gestartet, PID " + pi.dwProcessId);
                        }
                        else Log("  CreateProcessAsUser Fehler " + Marshal.GetLastWin32Error());
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
            Log("Ende: " + started + " gestartet");
            return started;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        static Mutex mutex;
        public static void ReleaseMutex() { try { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); mutex = null; } } catch { } }

        static bool RenderExpanded, RenderDemo;

        // Bild fuer Doku und Entwurf: Detailfenster, Meldefenster und alle Symbole in den Groessen des Infobereichs.
        static void Render(string file, string statusFile)
        {
            var app = new TrayApp(false);
            var st = statusFile != null ? AgentStatus.Load(statusFile) : AgentStatus.Load();
            var f = new DetailsForm(app);
            f.Expanded = RenderExpanded;
            f.Fill(st);
            f.KeepDetailsOpenForRender();
            f.Location = new Point(-10000, -10000);
            f.Show();
            var pf = new ProblemForm(st) { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) };
            if (RenderDemo) pf.Demo();
            pf.Show();
            Application.DoEvents();
            int pad = 24, iconRow = 72, top = Math.Max(f.Height, pf.Height);
            int[] sizes = { 16, 20, 24, 32, 48 };
            using (var bmp = new Bitmap(f.Width + pf.Width + 3 * pad, top + 2 * pad + iconRow))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(0xC9, 0xCF, 0xC1));
                    using (var fb = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(fb, new Rectangle(0, 0, f.Width, f.Height));
                        var state = g.Save();
                        using (var clip = Brand.Rounded(new RectangleF(pad, pad, f.Width, f.Height), Ui.S(8))) g.SetClip(clip);
                        g.DrawImage(fb, pad, pad);
                        g.Restore(state);
                    }
                    using (var pb = new Bitmap(pf.Width, pf.Height))
                    {
                        pf.DrawToBitmap(pb, new Rectangle(0, 0, pf.Width, pf.Height));
                        var state = g.Save();
                        using (var clip = Brand.Rounded(new RectangleF(f.Width + 2 * pad, pad, pf.Width, pf.Height), Ui.S(8))) g.SetClip(clip);
                        g.DrawImage(pb, f.Width + 2 * pad, pad);
                        g.Restore(state);
                    }
                    // Symbole auf dunklem Streifen (wie die Taskleiste): in Ordnung, verspaetet, Fehler, nicht eingerichtet
                    int y0 = top + 2 * pad, x = pad;
                    using (var b = new SolidBrush(Color.FromArgb(0x1F, 0x1F, 0x1F))) g.FillRectangle(b, 0, y0 - 12, bmp.Width, iconRow + 12);
                    foreach (Health h in new[] { Health.Ok, Health.Late, Health.Error, Health.Unknown })
                    {
                        foreach (int sz in sizes)
                        {
                            using (var ico = Brand.MakeIcon(h, sz)) g.DrawIcon(ico, new Rectangle(x, y0 + (48 - sz) / 2, sz, sz));
                            x += sz + 10;
                        }
                        x += 22;
                    }
                }
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            pf.Close();
            f.Close();
            app.ExitThread();
        }

        [STAThread]
        static void Main(string[] args)
        {
            // --render <datei.png>: Detailfenster + Symbole als Bild speichern (Doku/Test, ohne Bildschirm)
            if (Array.IndexOf(args, "--start-sessions") >= 0) { Environment.ExitCode = Math.Max(0, SessionLauncher.StartInAllSessions()); return; }
            // Darstellung: gemerkte Wahl des Benutzers, sonst wie Windows. Fuer --render: --theme light|dark, --expanded, --demo
            Theme.Load();
            int ti = Array.IndexOf(args, "--theme");
            if (ti >= 0 && ti + 1 < args.Length && (args[ti + 1] == "dark" || args[ti + 1] == "light")) { Theme.Mode = args[ti + 1]; Theme.Apply(); }
            RenderExpanded = Array.IndexOf(args, "--expanded") >= 0;
            RenderDemo = Array.IndexOf(args, "--demo") >= 0;
            int si = Array.IndexOf(args, "--status");
            if (si >= 0 && si + 1 < args.Length && File.Exists(args[si + 1])) AgentStatus.OverridePath = args[si + 1];
            int ri = Array.IndexOf(args, "--render");
            if (ri >= 0 && ri + 1 < args.Length) { try { SetProcessDPIAware(); } catch { } Render(args[ri + 1], ri + 2 < args.Length && !args[ri + 2].StartsWith("--") ? args[ri + 2] : null); return; }
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
