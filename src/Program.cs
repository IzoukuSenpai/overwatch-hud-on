// AutoOverlay - overlays PNG affiches automatiquement par-dessus un jeu.
// Aucune injection, aucune lecture memoire : simple fenetre transparente au premier plan.
// Compile avec le csc.exe du .NET Framework 4 (C# 5), voir build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoOverlay
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int CX; public int CY; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
            WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        public const int SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public const int WM_HOTKEY = 0x0312;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
    }

    class ImageSettings
    {
        public bool Enabled = true;
        public int OffsetX = 0;
        public int OffsetY = 0;
        public double Scale = 1.0;
        public int Opacity = 100;
    }

    class Config
    {
        public List<string> Processes = new List<string>();
        public bool HideOnCursor = true;
        public string Hotkey = "F8";
        public string HotkeyNext = "";
        public bool HudDetection = true;
        public int HideDelayMs = 100;
        public int DebounceMs = 120;
        public int PollMs = 33;
        public Dictionary<string, ImageSettings> Images = new Dictionary<string, ImageSettings>(StringComparer.OrdinalIgnoreCase);

        public Config() { Processes.Add("Overwatch"); }

        public ImageSettings For(string fileName)
        {
            ImageSettings s;
            if (!Images.TryGetValue(fileName, out s)) { s = new ImageSettings(); Images[fileName] = s; }
            return s;
        }

        static bool ParseBool(string v, bool def)
        {
            v = v.Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "oui" || v == "yes") return true;
            if (v == "false" || v == "0" || v == "non" || v == "no") return false;
            return def;
        }

        static int ParseInt(string v, int def, int min, int max)
        {
            int r;
            if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return def;
            return Math.Max(min, Math.Min(max, r));
        }

        static double ParseDouble(string v, double def, double min, double max)
        {
            double r;
            if (!double.TryParse(v.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return def;
            return Math.Max(min, Math.Min(max, r));
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) return c;
            string section = "general";
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                if (string.Equals(section, "general", StringComparison.OrdinalIgnoreCase))
                {
                    if (key == "processus")
                    {
                        c.Processes.Clear();
                        foreach (string p in val.Split(','))
                        {
                            string n = p.Trim();
                            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                            if (n.Length > 0) c.Processes.Add(n);
                        }
                    }
                    else if (key == "masquer_si_curseur_visible") c.HideOnCursor = ParseBool(val, true);
                    else if (key == "raccourci") c.Hotkey = val;
                    else if (key == "raccourci_suivant") c.HotkeyNext = val;
                    else if (key == "detection_hud") c.HudDetection = ParseBool(val, true);
                    else if (key == "delai_masquage_ms") c.HideDelayMs = ParseInt(val, 100, 0, 5000);
                    else if (key == "delai_ms") c.DebounceMs = ParseInt(val, 120, 0, 2000);
                    else if (key == "intervalle_ms") c.PollMs = ParseInt(val, 33, 15, 1000);
                }
                else
                {
                    ImageSettings s = c.For(section);
                    if (key == "actif") s.Enabled = ParseBool(val, true);
                    else if (key == "decalage_x") s.OffsetX = ParseInt(val, 0, -20000, 20000);
                    else if (key == "decalage_y") s.OffsetY = ParseInt(val, 0, -20000, 20000);
                    else if (key == "echelle") s.Scale = ParseDouble(val, 1.0, 0.05, 10.0);
                    else if (key == "opacite") s.Opacity = ParseInt(val, 100, 1, 100);
                }
            }
            return c;
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; AutoOverlay - configuration");
            sb.AppendLine("; Apres modification : clic droit sur l'icone de la zone de notification > Recharger.");
            sb.AppendLine("[general]");
            sb.AppendLine("; Noms des processus de jeu, separes par des virgules (sans .exe)");
            sb.AppendLine("processus=" + string.Join(",", Processes.ToArray()));
            sb.AppendLine("; Masque l'overlay quand le curseur de la souris est visible (menus)");
            sb.AppendLine("masquer_si_curseur_visible=" + (HideOnCursor ? "true" : "false"));
            sb.AppendLine("; Raccourci pour inverser l'affichage. Exemples : F8, Ctrl+F8, Alt+Shift+O. Vide = aucun");
            sb.AppendLine("raccourci=" + Hotkey);
            sb.AppendLine("; Raccourci pour passer a l'overlay suivant (un seul actif a la fois). Vide = aucun");
            sb.AppendLine("raccourci_suivant=" + HotkeyNext);
            sb.AppendLine("; N'affiche l'overlay que si le HUD d'Overwatch est a l'ecran (badge sous la barre de vie)");
            sb.AppendLine("detection_hud=" + (HudDetection ? "true" : "false"));
            sb.AppendLine("; Delai avant affichage / avant masquage (evite le clignotement)");
            sb.AppendLine("delai_ms=" + DebounceMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("delai_masquage_ms=" + HideDelayMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("intervalle_ms=" + PollMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("; Une section par image du dossier overlays.");
            sb.AppendLine("; decalage_x / decalage_y : pixels par rapport au centre de la fenetre du jeu.");
            sb.AppendLine("; echelle : 1.0 = taille d'origine. opacite : 1 a 100.");
            foreach (KeyValuePair<string, ImageSettings> kv in Images)
            {
                sb.AppendLine("[" + kv.Key + "]");
                sb.AppendLine("actif=" + (kv.Value.Enabled ? "true" : "false"));
                sb.AppendLine("decalage_x=" + kv.Value.OffsetX.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("decalage_y=" + kv.Value.OffsetY.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("echelle=" + kv.Value.Scale.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("opacite=" + kv.Value.Opacity.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // Fenetre a alpha par pixel, toujours au premier plan, traversee par les clics, jamais activee.
    class OverlayWindow : Form
    {
        public string FileName;
        public ImageSettings Settings;
        public int ImgWidth;
        public int ImgHeight;
        public int FullWidth, FullHeight, CropX, CropY;
        bool shown;
        byte[] alpha;
        int lastX = int.MinValue, lastY = int.MinValue;

        public OverlayWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST
                    | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void SetBitmap(Bitmap bmp, int opacityPercent)
        {
            ImgWidth = bmp.Width;
            ImgHeight = bmp.Height;
            alpha = new byte[ImgWidth * ImgHeight];
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, ImgWidth, ImgHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[ImgWidth];
                for (int y = 0; y < ImgHeight; y++)
                {
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, ImgWidth);
                    for (int x = 0; x < ImgWidth; x++) alpha[y * ImgWidth + x] = (byte)((uint)row[x] >> 24);
                }
            }
            finally { bmp.UnlockBits(bd); }
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBitmap);
                Native.SIZE size = new Native.SIZE(); size.CX = bmp.Width; size.CY = bmp.Height;
                Native.POINT src = new Native.POINT();
                Native.POINT dst = new Native.POINT(); dst.X = Left; dst.Y = Top;
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.SourceConstantAlpha = (byte)(255 * opacityPercent / 100);
                blend.AlphaFormat = 1;
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBitmap);
                }
                Native.DeleteDC(memDc);
            }
        }

        public void Place(int x, int y, bool reassert)
        {
            if (shown && !reassert && x == lastX && y == lastY) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            shown = true; lastX = x; lastY = y;
        }

        // Vrai si cette fenetre, actuellement affichee, recouvre ce pixel de l'ecran.
        public bool Covers(int sx, int sy)
        {
            if (!shown || alpha == null) return false;
            int x = sx - lastX, y = sy - lastY;
            if (x < 0 || y < 0 || x >= ImgWidth || y >= ImgHeight) return false;
            return alpha[y * ImgWidth + x] != 0;
        }

        public void HideOverlay()
        {
            if (!shown) return;
            Native.ShowWindow(Handle, 0);
            shown = false;
        }
    }

    class HotkeyWindow : NativeWindow
    {
        public event Action<int> Pressed;
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    class App : ApplicationContext
    {
        const string AppName = "AutoOverlay";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const long MaxFileBytes = 64L * 1024 * 1024;
        const int MaxDimension = 8192;

        readonly string baseDir, overlayDir, configPath;
        readonly string logPath;
        Config cfg;
        readonly List<OverlayWindow> windows = new List<OverlayWindow>();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly HotkeyWindow hotkeyWindow = new HotkeyWindow();
        bool hotkeyRegistered;
        Bitmap probe;
        int[] probePixels;
        int lastSolidHud;

        bool candidate, autoState, anyShown;
        int candidateSince;
        bool? overrideState;
        bool gameActive;
        int previewUntil;
        bool previewOn;
        int lastReassert;
        Native.RECT lastRect;
        IntPtr cachedHwnd = IntPtr.Zero;
        bool cachedIsGame;
        string lastStatus = "";

        public App(int previewSeconds)
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            overlayDir = Path.Combine(baseDir, "overlays");
            configPath = Path.Combine(baseDir, "config.ini");
            Directory.CreateDirectory(overlayDir);
            logPath = Path.Combine(baseDir, "AutoOverlay.log");
            try { if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024) File.Delete(logPath); } catch { }

            tray.Icon = MakeIcon();
            tray.Text = AppName;
            tray.Visible = true;
            hotkeyWindow.Pressed += delegate(int id) { if (id == 1) OnHotkey(); else if (id == 2) NextOverlay(); };

            Reload();

            timer.Tick += delegate { Tick(); };
            timer.Start();

            if (previewSeconds > 0) StartPreview(previewSeconds);
            else if (windows.Count == 0)
                tray.ShowBalloonTip(6000, AppName, "Aucune image. Depose tes PNG dans le dossier overlays puis clique sur Recharger.", ToolTipIcon.Info);
        }

        static Icon MakeIcon()
        {
            using (Bitmap b = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (Pen p = new Pen(Color.FromArgb(255, 250, 130, 30), 3f))
                    {
                        g.DrawEllipse(p, 5, 5, 22, 22);
                        g.DrawLine(p, 16, 1, 16, 11);
                        g.DrawLine(p, 16, 21, 16, 31);
                        g.DrawLine(p, 1, 16, 11, 16);
                        g.DrawLine(p, 21, 16, 31, 16);
                    }
                }
                IntPtr h = b.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }

        void Reload()
        {
            foreach (OverlayWindow w in windows) { w.HideOverlay(); w.Close(); w.Dispose(); }
            windows.Clear();
            anyShown = false;

            try { cfg = Config.Load(configPath); }
            catch (Exception ex) { cfg = new Config(); Warn("Configuration illisible : " + ex.Message); }

            string[] files = Directory.GetFiles(overlayDir, "*.png", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                ImageSettings s = cfg.For(name);
                try
                {
                    if (new FileInfo(f).Length > MaxFileBytes) throw new InvalidDataException("fichier trop volumineux");
                    using (Bitmap full = LoadScaled(f, s.Scale))
                    {
                        // On ne garde que la zone non transparente : fenetre plus petite, moins d'impact en jeu.
                        Rectangle box = OpaqueBounds(full);
                        if (box.Width <= 0 || box.Height <= 0) throw new InvalidDataException("image entierement transparente");
                        using (Bitmap bmp = full.Clone(box, PixelFormat.Format32bppArgb))
                        {
                            OverlayWindow w = new OverlayWindow();
                            w.FileName = name;
                            w.Settings = s;
                            w.FullWidth = full.Width;
                            w.FullHeight = full.Height;
                            w.CropX = box.X;
                            w.CropY = box.Y;
                            w.SetBitmap(bmp, s.Opacity);
                            windows.Add(w);
                        }
                    }
                }
                catch (Exception ex) { Warn("Image ignoree (" + name + ") : " + ex.Message); }
            }

            try { cfg.Save(configPath); } catch (Exception ex) { Warn("Ecriture de la configuration impossible : " + ex.Message); }

            timer.Interval = cfg.PollMs;
            RegisterHotkey();
            BuildMenu();
        }

        static Bitmap LoadScaled(string path, double scale)
        {
            byte[] data = File.ReadAllBytes(path);
            using (MemoryStream ms = new MemoryStream(data))
            using (Image img = Image.FromStream(ms, false, true))
            {
                int w = Math.Max(1, (int)Math.Round(img.Width * scale));
                int h = Math.Max(1, (int)Math.Round(img.Height * scale));
                if (w > MaxDimension || h > MaxDimension) throw new InvalidDataException("dimensions trop grandes");
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, new Rectangle(0, 0, w, h));
                }
                return bmp;
            }
        }

        static Rectangle OpaqueBounds(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), row, 0, w);
                    for (int x = 0; x < w; x++)
                    {
                        if ((row[x] >> 24) == 0) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            finally { bmp.UnlockBits(d); }
            if (maxX < 0) return Rectangle.Empty;
            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        void Warn(string msg) { tray.ShowBalloonTip(6000, AppName, msg, ToolTipIcon.Warning); }

        void RegisterHotkey()
        {
            Native.UnregisterHotKey(hotkeyWindow.Handle, 1);
            Native.UnregisterHotKey(hotkeyWindow.Handle, 2);
            hotkeyRegistered = RegisterOne(1, cfg.Hotkey);
            RegisterOne(2, cfg.HotkeyNext);
        }

        bool RegisterOne(int id, string spec)
        {
            string hk = (spec ?? "").Trim();
            if (hk.Length == 0) return false;
            uint mods = 0x4000; // MOD_NOREPEAT
            Keys key = Keys.None;
            foreach (string part in hk.Split('+'))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= 2;
                else if (p == "alt") mods |= 1;
                else if (p == "shift" || p == "maj") mods |= 4;
                else if (p == "win") mods |= 8;
                else
                {
                    Keys k;
                    if (Enum.TryParse<Keys>(part.Trim(), true, out k)) key = k;
                }
            }
            if (key == Keys.None) { Warn("Raccourci invalide : " + hk); return false; }
            bool ok = Native.RegisterHotKey(hotkeyWindow.Handle, id, mods, (uint)key);
            if (!ok) Warn("Raccourci " + hk + " deja utilise par une autre application.");
            return ok;
        }

        // Active l'overlay suivant et desactive les autres.
        void NextOverlay()
        {
            if (windows.Count == 0) return;
            int current = -1;
            for (int i = 0; i < windows.Count; i++) if (windows[i].Settings.Enabled) { current = i; break; }
            int next = (current + 1) % windows.Count;
            for (int i = 0; i < windows.Count; i++)
            {
                windows[i].Settings.Enabled = (i == next);
                if (i != next) windows[i].HideOverlay();
            }
            lastReassert = 0;
            anyShown = false;
            SaveConfig();
            BuildMenu();
        }

        // Le HUD d'Overwatch est-il a l'ecran ? On regarde le badge de niveau sous la barre de vie
        // (bleu equipe), ou le bandeau orange "Select perk" qui le remplace par moments.
        // Coordonnees de reference en 1080p, ancrees en bas a gauche de la fenetre du jeu.
        bool HudPresent(Native.RECT rect)
        {
            try
            {
                double s = (rect.Bottom - rect.Top) / 1080.0;
                int x0 = rect.Left + (int)Math.Round(170 * s);
                int x1 = rect.Left + (int)Math.Round(196 * s);
                int y0 = rect.Bottom - (int)Math.Round(106 * s);
                int y1 = rect.Bottom - (int)Math.Round(83 * s);
                int w = x1 - x0, h = y1 - y0;
                if (w < 6 || h < 6) return true;
                if (probe == null || probe.Width != w || probe.Height != h)
                {
                    if (probe != null) probe.Dispose();
                    probe = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    probePixels = new int[w * h];
                }
                using (Graphics g = Graphics.FromImage(probe))
                    g.CopyFromScreen(x0, y0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                BitmapData d = probe.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), probePixels, y * w, w);
                }
                finally { probe.UnlockBits(d); }

                int gapL = (int)Math.Round(8 * s), gapR = (int)Math.Round(16 * s); // chiffre blanc au centre du badge
                int total = 0, blue = 0, orange = 0, white = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (x >= gapL && x < gapR) continue;
                        bool covered = false;
                        foreach (OverlayWindow ow in windows)
                            if (ow.Covers(x0 + x, y0 + y)) { covered = true; break; }
                        if (covered) continue;
                        int p = probePixels[y * w + x];
                        int r = (p >> 16) & 255, gr = (p >> 8) & 255, b = p & 255;
                        total++;
                        if (r < 150 && b > 200 && b - r > 90 && gr > 120 && gr < 235) blue++;
                        else if (r > 215 && gr > 90 && gr < 175 && b < 90) orange++;
                        else if (r > 232 && gr > 232 && b > 232) white++;
                    }
                }
                if (total < w * h / 5) return true; // zone masquee par l'overlay : detection impossible
                int now = Environment.TickCount;
                if (blue * 100 >= total * 45 || orange * 100 >= total * 50) { lastSolidHud = now; return true; }
                // Le badge clignote en blanc lors d'un gain de niveau : on tolere ce flash
                // pendant 1,5 s au plus, et seulement si le HUD etait visible juste avant.
                return autoState && white * 100 >= total * 60 && unchecked(now - lastSolidHud) < 1500;
            }
            catch { return true; }
        }

        void BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem(AppName + " - raccourci : " + (hotkeyRegistered ? cfg.Hotkey : "aucun"));
            title.Enabled = false;
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());

            if (windows.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("(aucune image dans overlays)");
                none.Enabled = false;
                menu.Items.Add(none);
            }
            foreach (OverlayWindow w in windows)
            {
                OverlayWindow win = w;
                ToolStripMenuItem it = new ToolStripMenuItem(win.FileName);
                it.Checked = win.Settings.Enabled;
                it.Click += delegate
                {
                    win.Settings.Enabled = !win.Settings.Enabled;
                    it.Checked = win.Settings.Enabled;
                    if (!win.Settings.Enabled) win.HideOverlay();
                    lastReassert = 0;
                    SaveConfig();
                };
                menu.Items.Add(it);
            }
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem cursor = new ToolStripMenuItem("Masquer dans les menus (curseur visible)");
            cursor.Checked = cfg.HideOnCursor;
            cursor.Click += delegate { cfg.HideOnCursor = !cfg.HideOnCursor; cursor.Checked = cfg.HideOnCursor; SaveConfig(); };
            menu.Items.Add(cursor);

            ToolStripMenuItem startup = new ToolStripMenuItem("Lancer au demarrage de Windows");
            startup.Checked = IsStartupEnabled();
            startup.Click += delegate { SetStartup(!IsStartupEnabled()); startup.Checked = IsStartupEnabled(); };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("Ajouter des overlays...", null, delegate { AddOverlays(); });
            menu.Items.Add("Apercu 5 secondes", null, delegate { StartPreview(5); });
            menu.Items.Add("Ouvrir le dossier overlays", null, delegate { Open(overlayDir); });
            menu.Items.Add("Ouvrir la configuration", null, delegate { Open(configPath); });
            menu.Items.Add("Recharger", null, delegate { Reload(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quitter", null, delegate { Quit(); });

            ContextMenuStrip old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        // Copie les PNG choisis dans le dossier overlays puis recharge.
        void AddOverlays()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Ajouter des overlays";
                dlg.Filter = "Images PNG (*.png)|*.png";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                foreach (string f in dlg.FileNames)
                {
                    try
                    {
                        string dest = Path.Combine(overlayDir, Path.GetFileName(f));
                        if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;
                        File.Copy(f, dest, false);
                    }
                    catch (Exception ex) { Warn("Ajout impossible (" + Path.GetFileName(f) + ") : " + ex.Message); }
                }
                Reload();
            }
        }

        void SaveConfig()
        {
            try { cfg.Save(configPath); } catch (Exception ex) { Warn("Ecriture de la configuration impossible : " + ex.Message); }
        }

        static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex) { Warn("Demarrage automatique impossible : " + ex.Message); }
        }

        void StartPreview(int seconds)
        {
            previewOn = true;
            previewUntil = Environment.TickCount + seconds * 1000;
        }

        void OnHotkey()
        {
            if (!gameActive) return;
            overrideState = !(overrideState ?? autoState);
        }

        bool IsGameWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == cachedHwnd) return cachedIsGame;
            bool isGame = false;
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid != 0)
                {
                    using (Process p = Process.GetProcessById((int)pid))
                    {
                        string name = p.ProcessName;
                        foreach (string wanted in cfg.Processes)
                            if (string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase)) { isGame = true; break; }
                    }
                }
            }
            catch { isGame = false; }
            cachedHwnd = hwnd;
            cachedIsGame = isGame;
            return isGame;
        }

        static bool IsCursorVisible()
        {
            Native.CURSORINFO ci = new Native.CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
            if (!Native.GetCursorInfo(ref ci)) return false;
            return (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero;
        }

        void Tick()
        {
            int now = Environment.TickCount;
            IntPtr fg = Native.GetForegroundWindow();
            Native.RECT rect = new Native.RECT();
            gameActive = IsGameWindow(fg) && !Native.IsIconic(fg) && GetGameRect(fg, out rect);

            bool raw = gameActive && !(cfg.HideOnCursor && IsCursorVisible())
                && (!cfg.HudDetection || HudPresent(rect));
            if (raw != candidate) { candidate = raw; candidateSince = now; }
            if (candidate != autoState && unchecked(now - candidateSince) >= (candidate ? cfg.DebounceMs : cfg.HideDelayMs))
            {
                autoState = candidate;
                overrideState = null;
            }

            if (previewOn && unchecked(now - previewUntil) >= 0) previewOn = false;

            bool visible;
            if (previewOn)
            {
                Rectangle b = Screen.PrimaryScreen.Bounds;
                rect.Left = b.Left; rect.Top = b.Top; rect.Right = b.Right; rect.Bottom = b.Bottom;
                visible = true;
            }
            else visible = gameActive && (overrideState ?? autoState);

            if (visible)
            {
                bool reassert = !anyShown || unchecked(now - lastReassert) >= 1000
                    || rect.Left != lastRect.Left || rect.Top != lastRect.Top
                    || rect.Right != lastRect.Right || rect.Bottom != lastRect.Bottom;
                if (reassert) lastReassert = now;
                foreach (OverlayWindow w in windows)
                {
                    if (!w.Settings.Enabled) continue;
                    int x = rect.Left + (rect.Right - rect.Left - w.FullWidth) / 2 + w.CropX + w.Settings.OffsetX;
                    int y = rect.Top + (rect.Bottom - rect.Top - w.FullHeight) / 2 + w.CropY + w.Settings.OffsetY;
                    w.Place(x, y, reassert);
                }
                lastRect = rect;
                anyShown = true;
            }
            else if (anyShown)
            {
                foreach (OverlayWindow w in windows) w.HideOverlay();
                anyShown = false;
            }

            // Hors jeu, on ralentit la surveillance pour ne rien consommer.
            int wanted = (gameActive || previewOn) ? cfg.PollMs : 250;
            if (timer.Interval != wanted) timer.Interval = wanted;

            string status = !gameActive ? "en attente du jeu"
                : visible ? (overrideState.HasValue ? "affiche (force)" : "affiche")
                : (overrideState.HasValue ? "masque (force)" : "masque (hors jeu)");
            if (status != lastStatus)
            {
                lastStatus = status;
                tray.Text = AppName + " - " + status;
                try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + status + "\r\n"); } catch { }
            }
        }

        static bool GetGameRect(IntPtr hwnd, out Native.RECT rect)
        {
            rect = new Native.RECT();
            Native.RECT client;
            if (!Native.GetClientRect(hwnd, out client)) return false;
            Native.POINT origin = new Native.POINT();
            if (!Native.ClientToScreen(hwnd, ref origin)) return false;
            rect.Left = origin.X; rect.Top = origin.Y;
            rect.Right = origin.X + client.Right; rect.Bottom = origin.Y + client.Bottom;
            return client.Right > 0 && client.Bottom > 0;
        }

        void Quit()
        {
            timer.Stop();
            Native.UnregisterHotKey(hotkeyWindow.Handle, 1);
            Native.UnregisterHotKey(hotkeyWindow.Handle, 2);
            foreach (OverlayWindow w in windows) { w.HideOverlay(); w.Close(); }
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\AutoOverlay_SingleInstance", out created))
            {
                if (!created) return;
                try { Native.SetProcessDpiAwareness(2); } catch { }
                int preview = 0;
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "--preview") int.TryParse(args[i + 1], out preview);
                preview = Math.Max(0, Math.Min(60, preview));
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App(preview));
                GC.KeepAlive(mutex);
            }
        }
    }
}
