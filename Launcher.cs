// undr.wlrd launcher - a small splash window styled after the old Roblox "Launching..." dialog.
//
// What it actually does, beyond looking the part:
//   1. Reads appsettings.xml next to the exe for the <BaseUrl> to launch (see ClientConfig /
//      ConfigReader below). Missing/unreadable -> downloads a fresh client package from the
//      CDN and retries. Present but pointed at a server we don't recognise -> refuses to run.
//   2. Probes that URL.
//   3. If that's not answering yet and a local server.js is next to this exe (or one/two
//      folders up - this .exe normally lives in launcher\dist\), starts it and waits for it
//      to come up.
//   4. Opens the URL in the default browser, then closes itself.
//   5. Cancel aborts the wait (and the server process it started, if any) instead of launching.
//
// Compiled with csc (ships with .NET Framework - see build.cmd), no external packages.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using System.Text;

namespace UndrWlrdLauncher
{
    /// <summary>
    /// Appends a timestamped line per event to logs\launcher.log next to the exe - same idea
    /// as server.js's logs/ folder, so a run can be looked at after the fact instead of only
    /// through the error dialog. Never throws: a logging failure (read-only install folder,
    /// etc.) just means no log, not a broken launcher.
    /// </summary>
    internal static class Log
    {
        static readonly object Gate = new object();
        static string _path;

        public static void Init(string baseDir)
        {
            try
            {
                var dir = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "launcher.log");
                Write("=== undr.wlrd launcher started ===");
            }
            catch
            {
                _path = null;
            }
        }

        public static void Write(string message)
        {
            if (_path == null) return;
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message.Replace("\r", " ").Replace("\n", " | ") + Environment.NewLine;
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            lock (Gate)
            {
                try
                {
                    // FileShare.ReadWrite (not File.AppendAllText's default sharing) so a second
                    // launcher instance - or this same process's own rapid-fire writes - can't
                    // hit a sharing violation and silently lose a line.
                    using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        fs.Write(bytes, 0, bytes.Length);
                }
                catch { /* best effort - logging must never be why the launcher fails */ }
            }
        }
    }

    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(0x0A, 0x13, 0x1E);
        public static readonly Color Track = Color.FromArgb(0x20, 0x28, 0x33);
        public static readonly Color Indicator = Color.FromArgb(0x04, 0x15, 0x3B);
        public static readonly Color IndicatorDone = Color.FromArgb(0x1F, 0x6B, 0x3A);
        public static readonly Color IndicatorError = Color.FromArgb(0x6B, 0x1F, 0x24);
        public static readonly Color ButtonBorder = Color.FromArgb(0x2C, 0x37, 0x45);
        public static readonly Color TextMuted = Color.FromArgb(0x9A, 0xA7, 0xB8);
    }

    /// <summary>
    /// Where appsettings.xml is expected to point, and where to fetch a replacement client
    /// package from when it doesn't. Edit CdnUrlDev / CdnUrlProd once the real CDN exists -
    /// these are placeholders and will currently fail to resolve, which is expected.
    /// </summary>
    internal static class ClientConfig
    {
        public const string CdnUrlDev = "https://undrwrld-dev.perfectgoddess69.workers.dev/cdn/latest.zip";
        public const string CdnUrlProd = "https://undrwlrd.perfectgoddess69.workers.dev/cdn/latest.zip";
        public const string Md5Url = "47cd76e43f74bbc2e1baaf194d07e1fa";

        // A <BaseUrl> is only trusted if it contains one of these. This is what stops a copy
        // of this launcher being quietly re-pointed at someone else's server while still
        // wearing the undr.wlrd branding.
        public static readonly string[] ValidUrlSubstrings =
        {
            "undrwrld.perfectgoddess69.workers.dev",
            "undrwrld-dev.perfectgoddess69.workers.dev",
            "undr.wlrd",
            "undrwrld.ct.ws",
            "127.0.0.1:8231",
            "undrwrld-dev.ct.ws"
        };
    }

    internal enum ConfigReadResult { Ok, Unreadable, UntrustedUrl }

    /// <summary>Reads &lt;BaseUrl&gt; out of appsettings.xml and checks it against ClientConfig.</summary>
    internal static class ConfigReader
    {
        public static ConfigReadResult TryRead(string path, out string url, out string error)
        {
            url = null;
            error = null;
            Log.Write("config: reading " + path);
            try
            {
                if (!File.Exists(path))
                {
                    error = "appsettings.xml was not found at " + path;
                    Log.Write("config: " + error);
                    return ConfigReadResult.Unreadable;
                }

                var doc = new XmlDocument();
                doc.Load(path);
                var node = doc.SelectSingleNode("//BaseUrl");
                if (node == null || string.IsNullOrWhiteSpace(node.InnerText))
                {
                    error = "appsettings.xml has no <BaseUrl> value";
                    Log.Write("config: " + error);
                    return ConfigReadResult.Unreadable;
                }

                var candidate = node.InnerText.Trim();
                foreach (var allowed in ClientConfig.ValidUrlSubstrings)
                {
                    if (candidate.IndexOf(allowed, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        url = candidate;
                        Log.Write("config: ok, BaseUrl=" + url);
                        return ConfigReadResult.Ok;
                    }
                }
                url = candidate; // returned so the caller can show what it actually found
                Log.Write("config: UNTRUSTED BaseUrl=" + url);
                return ConfigReadResult.UntrustedUrl;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("config: unreadable - " + error);
                return ConfigReadResult.Unreadable;
            }
        }
    }

    internal enum ProbeState { Reachable, Maintenance, Failed }

    internal struct ProbeResult
    {
        public ProbeState State;
        public string Error;
        public static ProbeResult Reachable() { return new ProbeResult { State = ProbeState.Reachable }; }
        public static ProbeResult Maintenance() { return new ProbeResult { State = ProbeState.Maintenance }; }
        public static ProbeResult Failed(string error) { return new ProbeResult { State = ProbeState.Failed, Error = error }; }
    }

    internal struct DownloadResult
    {
        public bool Success;
        public string Error;
        public string ClientRoot;
        public static DownloadResult Ok(string clientRoot) { return new DownloadResult { Success = true, ClientRoot = clientRoot }; }
        public static DownloadResult Failed(string error) { return new DownloadResult { Success = false, Error = error }; }
    }

    /// <summary>Fetches the client package zip from the CDN and unpacks it next to the exe.</summary>
    internal static class ClientUpdater
    {
        public static async Task<DownloadResult> DownloadAsync(string channel, string destDir, CancellationToken token)
        {
            string cdnUrl = string.Equals(channel, "prod", StringComparison.OrdinalIgnoreCase)
                ? ClientConfig.CdnUrlProd
                : ClientConfig.CdnUrlDev;
            string tmpZip = Path.Combine(Path.GetTempPath(), "undrwlrd-client-" + Guid.NewGuid().ToString("N") + ".zip");
            Log.Write("download: channel=" + channel + " cdn=" + cdnUrl + " -> " + destDir);

            try
            {
                var downloaded = false;
                try
                {
                    var handler = new HttpClientHandler { AllowAutoRedirect = true };
                    using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
                    using (var resp = await http.GetAsync(cdnUrl, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        Log.Write("download: managed final response url=" + resp.RequestMessage.RequestUri + " status=" + (int)resp.StatusCode);
                        resp.EnsureSuccessStatusCode();
                        using (var file = File.Create(tmpZip))
                            await resp.Content.CopyToAsync(file);
                    }
                    downloaded = true;
                    Log.Write("download: managed download succeeded");
                }
                catch (Exception ex)
                {
                    Log.Write("download: managed download failed, falling back to curl - " + ex.Message);
                }

                if (!downloaded)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "curl.exe",
                        Arguments = "-L --fail --silent --show-error -o \"" + tmpZip + "\" \"" + cdnUrl + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    Log.Write("download: invoking curl.exe " + psi.Arguments);
                    using (var curl = Process.Start(psi))
                    {
                        var stderrTask = curl.StandardError.ReadToEndAsync();
                        var stdoutTask = curl.StandardOutput.ReadToEndAsync();
                        while (!curl.HasExited)
                        {
                            if (token.IsCancellationRequested)
                            {
                                try { curl.Kill(); } catch { }
                                token.ThrowIfCancellationRequested();
                            }
                            await Task.Delay(100, token);
                        }
                        var stderr = await stderrTask;
                        var stdout = await stdoutTask;
                        if (!string.IsNullOrWhiteSpace(stdout)) Log.Write("download: curl stdout=" + stdout.Trim());
                        if (!string.IsNullOrWhiteSpace(stderr)) Log.Write("download: curl stderr=" + stderr.Trim());
                        if (curl.ExitCode != 0) throw new Exception("curl exited with code " + curl.ExitCode + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : ": " + stderr.Trim()));
                    }
                }
                Log.Write("download: fetched zip ok, extracting");

                Directory.CreateDirectory(destDir);
                string destRoot = Path.GetFullPath(destDir);
                int extracted = 0;
                string clientRoot = destRoot;
                using (var archive = ZipFile.OpenRead(tmpZip))
                {
                    string commonRoot = null;
                    bool singleRoot = true;
                    foreach (var entry in archive.Entries)
                    {
                        var normalized = entry.FullName.Replace('\\', '/').Trim('/');
                        if (normalized.Length == 0) continue;
                        var slash = normalized.IndexOf('/');
                        if (slash < 0) { singleRoot = false; break; }
                        var root = normalized.Substring(0, slash);
                        if (commonRoot == null) commonRoot = root;
                        else if (!string.Equals(commonRoot, root, StringComparison.OrdinalIgnoreCase)) { singleRoot = false; break; }
                    }
                    foreach (var entry in archive.Entries)
                    {
                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
                        if (relative.Length == 0) continue;

                        var destPath = Path.GetFullPath(Path.Combine(destRoot, relative));
                        // guard against a zip entry trying to write outside the launcher folder
                        if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Write("download: blocked path-traversal entry " + entry.FullName);
                            continue;
                        }

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destPath);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        entry.ExtractToFile(destPath, true); // overwrite - this is how a stale client gets replaced
                        extracted++;
                    }

                    var knownClientRoot = Path.Combine(destRoot, "latest2018client");
                    if (Directory.Exists(knownClientRoot))
                    {
                        clientRoot = knownClientRoot;
                        Log.Write("download: using known client root 'latest2018client'");
                    }
                    else if (singleRoot && !string.IsNullOrEmpty(commonRoot))
                    {
                        clientRoot = Path.Combine(destRoot, commonRoot);
                        Log.Write("download: preserving common top-level folder '" + commonRoot + "' as client root");
                    }
                }
                Log.Write("download: extracted " + extracted + " file(s) into client root " + clientRoot);
                return DownloadResult.Ok(clientRoot);
            }
            catch (Exception ex)
            {
                Log.Write("download: FAILED - " + ex);
                return DownloadResult.Failed(ex.ToString());
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* best effort cleanup */ }
            }
        }
    }

    /// <summary>Launch state, driving both the on-screen text and the indicator colour.</summary>
    internal enum LaunchState { Working, Done, Failed }

    internal static class Drawing
    {
        // ---- rounded rect helper -------------------------------------------------------
        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---- the "U" mark, drawn from launcherlogo.png (embedded as a resource at build time
        //      so the exe stays a single self-contained file) ------------------------------
        public static void DrawLogo(Graphics g, Rectangle bounds, Image logo)
        {
            if (logo == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // contain-fit: scale to the larger dimension fitting inside bounds, never crop,
            // never exceed - this is the bug the hand-drawn version had (the outline stroke
            // was painted outside the box it had just been scaled to fit).
            float scale = Math.Min((float)bounds.Width / logo.Width, (float)bounds.Height / logo.Height);
            int w = (int)Math.Round(logo.Width * scale);
            int h = (int)Math.Round(logo.Height * scale);
            int x = bounds.X + (bounds.Width - w) / 2;
            int y = bounds.Y + (bounds.Height - h) / 2;
            g.DrawImage(logo, new Rectangle(x, y, w, h));
        }

        // ---- the loading track + animated (or settled) indicator -----------------------
        public static void DrawProgress(Graphics g, Rectangle track, float phase, LaunchState state)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float radius = track.Height / 2f;
            using (var trackPath = RoundedRect(track, radius))
            using (var trackBrush = new SolidBrush(Theme.Track))
                g.FillPath(trackBrush, trackPath);

            Color fill = state == LaunchState.Done ? Theme.IndicatorDone
                       : state == LaunchState.Failed ? Theme.IndicatorError
                       : Theme.Indicator;
            int inset = 5;
            var inner = new Rectangle(track.X + inset, track.Y + inset, track.Width - inset * 2, track.Height - inset * 2);

            Rectangle bar;
            if (state != LaunchState.Working)
            {
                bar = inner; // settled: fill the whole track so it reads as "done" / "failed"
            }
            else
            {
                int barWidth = Math.Max(40, inner.Width / 5);
                // bounce back and forth, 0..1..0, across the inner track
                float t = phase % 2f;
                float f = t <= 1f ? t : 2f - t;
                int x = inner.X + (int)(f * (inner.Width - barWidth));
                bar = new Rectangle(x, inner.Y, barWidth, inner.Height);
            }
            using (var barPath = RoundedRect(bar, bar.Height / 2f))
            using (var barBrush = new SolidBrush(fill))
                g.FillPath(barBrush, barPath);
        }

        public static void DrawButtonFace(Graphics g, Rectangle bounds)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundedRect(bounds, 6))
            {
                using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, path);
                using (var p = new Pen(Theme.ButtonBorder)) g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Loads launcherlogo.png, embedded into the exe at build time (see build.cmd).</summary>
    internal static class Resources
    {
        static Image _logo;
        public static Image Logo
        {
            get
            {
                if (_logo == null)
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("UndrWlrdLauncher.launcherlogo.png"))
                    {
                        _logo = s != null ? Image.FromStream(s) : null;
                    }
                }
                return _logo;
            }
        }
    }

    internal sealed class LogoPanel : Panel
    {
        public LogoPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.Background;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Drawing.DrawLogo(e.Graphics, new Rectangle(8, 0, Width - 16, Height), Resources.Logo);
        }
    }

    internal sealed class ProgressPanel : Panel
    {
        public float Phase;
        public LaunchState State = LaunchState.Working;
        public ProgressPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.Background;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Drawing.DrawProgress(e.Graphics, new Rectangle(0, 0, Width, Height), Phase, State);
        }
    }

    internal sealed class FlatPanelButton : Button
    {
        public FlatPanelButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Theme.Background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Drawing.DrawButtonFace(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Enabled ? Color.White : Theme.TextMuted))
                e.Graphics.DrawString(Text, Font, b, ClientRectangle, sf);
        }
    }

    public sealed class LauncherForm : Form
    {
        readonly LogoPanel _logo = new LogoPanel();
        readonly ProgressPanel _progress = new ProgressPanel();
        readonly Label _status = new Label();
        readonly FlatPanelButton _cancel = new FlatPanelButton();
        readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        string _url; // not readonly: may be overwritten by appsettings.xml's <BaseUrl> in RunAsync
        readonly bool _urlExplicit;
        readonly string _siteName;
        readonly string _serverScript;
        readonly int _timeoutSeconds;
        string _configPath;
        readonly string _channel;
        readonly int _placeId;
        string _clientRoot;
        string _client2026ExePath;
        string _client2018ExePath;
        string _onSiteMessage;
        bool _showPbb2026;
        bool _robloxMoments2026Uploading;
        bool _allow2026Launch = true;
        Process _spawnedServer;
        float _phase;

        public LauncherForm(LaunchOptions opts)
        {
            _url = opts.Url;
            _urlExplicit = opts.UrlExplicit;
            _configPath = opts.ConfigPath;
            _channel = opts.Channel;
            _placeId = opts.PlaceId;
            _clientRoot = Path.GetDirectoryName(_configPath);
            _client2026ExePath = Path.Combine(_clientRoot, "UndrwlrdClientVersion741", "RobloxPlayerBeta.exe");
            _client2018ExePath = Path.Combine(_clientRoot, "RobloxPlayerBeta.exe");
            _siteName = opts.SiteName;
            _serverScript = opts.ServerScript;
            _timeoutSeconds = opts.TimeoutSeconds;

            Log.Write("window: url=" + _url + " urlExplicit=" + _urlExplicit + " config=" + _configPath +
                " clientRoot=" + _clientRoot + " channel=" + _channel + " placeId=" + _placeId + " server=" + (_serverScript ?? "(none found)") + " timeout=" + _timeoutSeconds + "s");

            Text = "undr.wrld";
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(534, 307);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Background;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* fine without one */ }

            _logo.Bounds = new Rectangle(197, 14, 140, 132);
            _progress.Bounds = new Rectangle(18, 173, 496, 40);
            _status.Bounds = new Rectangle(0, 222, ClientSize.Width, 24);
            _status.TextAlign = ContentAlignment.MiddleCenter;
            _status.ForeColor = Color.White;
            _status.BackColor = Theme.Background;
            _status.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            _status.Text = "Launching " + _siteName + "...";
            _cancel.Bounds = new Rectangle(169, 256, 196, 34);
            _cancel.Text = "Cancel";
            _cancel.Click += OnCancelClicked;

            Controls.Add(_logo);
            Controls.Add(_progress);
            Controls.Add(_status);
            Controls.Add(_cancel);

            _anim.Interval = 16;
            _anim.Tick += (s, e) => { _phase += 0.018f; _progress.Phase = _phase; _progress.Invalidate(); };
            _anim.Start();

            MouseDown += DragMove;
            _logo.MouseDown += DragMove;
            _status.MouseDown += DragMove;

            Load += async (s, e) => await RunAsync();
            FormClosing += (s, e) => { KillSpawnedServer(); };
        }

        // Borderless forms have no title bar to drag by, so let the whole background do it.
        void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HT_CAPTION, 0);
        }

        void OnCancelClicked(object sender, EventArgs e)
        {
            Log.Write("user clicked " + _cancel.Text + " (state=" + _progress.State + ")");
            if (_progress.State == LaunchState.Working)
            {
                _cts.Cancel();
                Close();
            }
            else
            {
                Close();
            }
        }

        async Task RunAsync()
        {
            Log.Write("RunAsync: starting");
            try
            {
                // --url= on the command line is an explicit override (used for local testing) -
                // skip appsettings.xml entirely when it's set.
                if (!_urlExplicit && !await ResolveConfigAsync()) return;
                if (_cts.IsCancellationRequested) return;

                await FetchLauncherFastFlagsIfPresent();
                if (_cts.IsCancellationRequested || IsDisposed) return;

                var has2026Client = File.Exists(_client2026ExePath);
                var has2018Client = File.Exists(_client2018ExePath);
                Log.Write("client2026: path=" + _client2026ExePath + " exists=" + has2026Client);
                Log.Write("client2018: path=" + _client2018ExePath + " exists=" + has2018Client);
                if (has2026Client && !_allow2026Launch)
                {
                    Fail((string.IsNullOrWhiteSpace(_onSiteMessage) ? "The 2026 client is currently disabled." : _onSiteMessage) + "\n\nPlease try again later.");
                    return;
                }

                var probe = await ProbeAsync(_url, TimeSpan.FromSeconds(2), _cts.Token);
                Log.Write("probe: " + _url + " state=" + probe.State + " (first attempt)");

                if (probe.State == ProbeState.Maintenance)
                {
                    Fail("undr.wlrd is currently undergoing maintenance.\n\nPlease try again later.");
                    return;
                }

                if (probe.State == ProbeState.Failed && !string.IsNullOrEmpty(_serverScript) && File.Exists(_serverScript))
                {
                    StartServer();
                }

                var deadline = DateTime.UtcNow.AddSeconds(_timeoutSeconds);
                while (probe.State == ProbeState.Failed && DateTime.UtcNow < deadline)
                {
                    if (_cts.IsCancellationRequested) return;
                    await Task.Delay(400, _cts.Token);
                    probe = await ProbeAsync(_url, TimeSpan.FromSeconds(2), _cts.Token);
                    if (probe.State == ProbeState.Maintenance)
                    {
                        Fail("undr.wlrd is currently undergoing maintenance.\n\nPlease try again later.");
                        return;
                    }
                }

                if (_cts.IsCancellationRequested) return;

                if (probe.State != ProbeState.Reachable)
                {
                    Log.Write("probe: gave up after " + _timeoutSeconds + "s, " + _url + " never answered");
                    Fail("Couldn't reach " + _siteName + " (" + _url + ").\n\nCheck that undr.wrld is currently not undergoing maintenance.");
                    return;
                }

                Log.Write("probe: " + _url + " is up, launching");
                _progress.State = LaunchState.Done;
                _status.Text = "Launched " + _siteName;
                _cancel.Text = "Close";
                _progress.Invalidate();

                Process launched = null;
                if (has2026Client)
                {
                    Log.Write("client2026: launching " + _client2026ExePath);
                    launched = Process.Start(new ProcessStartInfo(_client2026ExePath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(_client2026ExePath),
                    });
                }
                else if (has2018Client)
                {
                    var args = "--play -a \"https://undrwrld-dev.perfectgoddess69.workers.dev/Login/Negotiate.ashx\" -t \":3\" -j \"https://undrwrld-dev.perfectgoddess69.workers.dev/game/PlaceLauncher.ashx?request=RequestGame&placeId=" + _placeId + "\"";
                    Log.Write("client2018: launching " + _client2018ExePath + " placeId=" + _placeId + " args=" + args);
                    launched = Process.Start(new ProcessStartInfo(_client2018ExePath)
                    {
                        Arguments = args,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(_client2018ExePath),
                    });
                }
                else
                {
                    Log.Write("client: no local client present, opening site URL instead");
                    Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
                    if (!_cts.IsCancellationRequested) Close();
                    return;
                }

                if (launched == null)
                {
                    Log.Write("client: Process.Start returned null, closing launcher");
                    if (!_cts.IsCancellationRequested) Close();
                    return;
                }

                _status.Text = "Client running...";
                _status.Invalidate();
                _cancel.Text = "Close";
                while (!_cts.IsCancellationRequested && !launched.HasExited)
                {
                    await Task.Delay(500, _cts.Token).ContinueWith(_ => { });
                }
                Log.Write("client: launched process exited");
                if (!_cts.IsCancellationRequested) Close();
            }
            catch (TaskCanceledException) { Log.Write("RunAsync: cancelled"); /* window is already closing */ }
            catch (OperationCanceledException) { Log.Write("RunAsync: cancelled"); /* same */ }
            catch (Exception ex)
            {
                Fail("Launch error:\n\n" + ex);
            }
        }

        // Reads appsettings.xml and sets _url from its <BaseUrl>. That BaseUrl is treated as
        // the launcher's own base URL too: probes, maintenance detection, and launcher fast
        // flags all go to the same host the client is configured for. If the file is missing or
        // unreadable, downloads a fresh client package from the CDN and tries again once.
        // Returns false (having already shown an error and started closing the window) if the
        // launch shouldn't proceed.
        async Task<bool> ResolveConfigAsync()
        {
            string url, error;
            var result = ConfigReader.TryRead(_configPath, out url, out error);

            if (result == ConfigReadResult.UntrustedUrl)
            {
                FailUntrustedUrl(url);
                return false;
            }

            if (result != ConfigReadResult.Ok)
            {
                Log.Write("config: needs a (re)download before launching");
                _status.Text = "Downloading " + _siteName + "...";
                _status.Invalidate();

                var download = await ClientUpdater.DownloadAsync(_channel, Path.GetDirectoryName(_configPath), _cts.Token);
                if (_cts.IsCancellationRequested) return false;
                if (!download.Success)
                {
                    Fail("Couldn't download the client from the CDN:\n\n" + download.Error);
                    return false;
                }

                _clientRoot = download.ClientRoot;
                _configPath = Path.Combine(_clientRoot, "appsettings.xml");
                _client2026ExePath = Path.Combine(_clientRoot, "UndrwlrdClientVersion741", "RobloxPlayerBeta.exe");
                _client2018ExePath = Path.Combine(_clientRoot, "RobloxPlayerBeta.exe");
                Log.Write("config: switched client root to " + _clientRoot + " after download");

                result = ConfigReader.TryRead(_configPath, out url, out error);
                if (result == ConfigReadResult.UntrustedUrl)
                {
                    FailUntrustedUrl(url);
                    return false;
                }
                if (result != ConfigReadResult.Ok)
                {
                    Fail("Downloaded the client, but appsettings.xml still couldn't be read:\n\n" + error);
                    return false;
                }
            }

            _url = url;
            _status.Text = "Launching " + _siteName + "...";
            _status.Invalidate();
            return true;
        }

        async Task FetchLauncherFastFlagsIfPresent()
        {
            if (!File.Exists(_client2026ExePath))
            {
                Log.Write("fflags: 2026 client not present at " + _client2026ExePath + ", skipping launcher fast flags fetch");
                return;
            }

            var endpoint = new Uri(new Uri(_url.EndsWith("/") ? _url : _url + "/"), "fflags/Launcher/version-" + ClientConfig.Md5Url).ToString();
            Log.Write("fflags: fetching " + endpoint);
            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (m, c, ch, e) => true,
                };
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var resp = await client.GetAsync(endpoint, HttpCompletionOption.ResponseContentRead, _cts.Token);
                    var body = await resp.Content.ReadAsStringAsync();
                    Log.Write("fflags: status=" + (int)resp.StatusCode + " body=" + body);
                    var shapeError = ApplyLauncherFastFlags(body);
                    if (shapeError != null)
                    {
                        Fail("The launcher fast-flags response was not in the shape we expected.\n\n" + shapeError);
                    }
                }
            }
            catch (TaskCanceledException) { Log.Write("fflags: cancelled"); }
            catch (OperationCanceledException) { Log.Write("fflags: cancelled"); }
            catch (Exception ex)
            {
                Log.Write("fflags: FAILED - " + ex.Message);
            }
        }

        string ApplyLauncherFastFlags(string body)
        {
            try
            {
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (json == null || !json.ContainsKey("fflags"))
                {
                    var error = "Missing top-level 'fflags' object.";
                    Log.Write("fflags: " + error);
                    return error;
                }

                var flags = json["fflags"] as Dictionary<string, object>;
                if (flags == null)
                {
                    var error = "Top-level 'fflags' value was not an object.";
                    Log.Write("fflags: " + error);
                    return error;
                }

                _onSiteMessage = GetStringFlag(flags, "OnSiteMessage");
                _showPbb2026 = GetBoolFlag(flags, "FFlag_ShowPBB2026");
                _robloxMoments2026Uploading = GetBoolFlag(flags, "FFlags_RobloxMoments2026_Uploading");
                _allow2026Launch = GetBoolFlag(flags, "FFlags_Allow2026Launch", true);

                Log.Write("fflags: parsed OnSiteMessage=" + (_onSiteMessage ?? "(none)") +
                    " FFlag_ShowPBB2026(PBBgameshowing)=" + _showPbb2026 +
                    " FFlags_RobloxMoments2026_Uploading(allows upload)=" + _robloxMoments2026Uploading +
                    " FFlags_Allow2026Launch(allows launch)=" + _allow2026Launch);
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("fflags: parse FAILED - " + ex.Message);
                return ex.Message;
            }
        }

        static string GetStringFlag(Dictionary<string, object> flags, string name)
        {
            object value;
            return flags.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : null;
        }

        static bool GetBoolFlag(Dictionary<string, object> flags, string name, bool fallback = false)
        {
            object value;
            if (!flags.TryGetValue(name, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
        }

        // The "please dont skid launchers" line is deliberate: this is the one error message
        // aimed at someone who copied the launcher and repointed it at an unofficial server,
        // not at a legitimate user hitting a real problem.
        void FailUntrustedUrl(string url)
        {
            Fail(
                "please dont skid launchers");
        }

        // Shows a real Windows error dialog (separate from the splash window) and then closes
        // the launcher - matches how a failed launch should be reported, instead of burying it
        // in the status label underneath the progress bar.
        void Fail(string message)
        {
            Log.Write("FAIL: " + message);
            _progress.State = LaunchState.Failed;
            _status.Text = "Launch failed";
            _cancel.Text = "Close";
            _progress.Invalidate();
            MessageBox.Show(this, message, _siteName + " Launcher - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }

        void StartServer()
        {
            Log.Write("server: starting node " + _serverScript);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = "\"" + _serverScript + "\"",
                    WorkingDirectory = Path.GetDirectoryName(_serverScript),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _spawnedServer = Process.Start(psi);
                Log.Write("server: started pid=" + (_spawnedServer != null ? _spawnedServer.Id.ToString() : "?"));
            }
            catch (Exception ex)
            {
                Log.Write("server: failed to start - " + ex.Message);
                _spawnedServer = null; // no node on PATH, or script failed to start - keep polling the URL anyway
            }
        }

        void KillSpawnedServer()
        {
            if (_spawnedServer == null || _spawnedServer.HasExited) return;
            Log.Write("server: stopping pid=" + _spawnedServer.Id);
            try { _spawnedServer.Kill(); } catch { /* best effort */ }
        }

        static async Task<ProbeResult> ProbeAsync(string url, TimeSpan timeout, CancellationToken token)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (m, c, ch, e) => true, // local self-signed cert
            };
            using (var client = new HttpClient(handler) { Timeout = timeout })
            {
                try
                {
                    var resp = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, token);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (body.IndexOf("<p>undr.wrld | roblox revival real | <a href=\"discord.gg\">discord</a></p>", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log.Write("probe: maintenance marker detected in BaseUrl response");
                        return ProbeResult.Maintenance();
                    }
                    return ProbeResult.Reachable(); // any non-maintenance response means something is listening
                }
                catch (Exception ex)
                {
                    return ProbeResult.Failed(ex.Message);
                }
            }
        }
    }

    internal static class NativeMethods
    {
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    }

    public sealed class LaunchOptions
    {
        public string Url = "https://undr.wrld/";
        public bool UrlExplicit; // --url= was passed on the command line - skip appsettings.xml entirely
        public string SiteName = "undr.wrld";
        public string ServerScript;
        public int TimeoutSeconds = 12;
        public string ConfigPath;
        public string Channel = "dev"; // which CDN to pull from if a redownload is needed: dev|prod
        public int PlaceId = 1;

        public static LaunchOptions FromArgs(string[] args)
        {
            var o = new LaunchOptions();
            foreach (var a in args)
            {
                if (a.StartsWith("--url=")) { o.Url = a.Substring(6); o.UrlExplicit = true; }
                else if (a.StartsWith("--name=")) o.SiteName = a.Substring(7);
                else if (a.StartsWith("--server=")) o.ServerScript = a.Substring(9);
                else if (a.StartsWith("--timeout=")) int.TryParse(a.Substring(10), out o.TimeoutSeconds);
                else if (a.StartsWith("--config=")) o.ConfigPath = a.Substring(9);
                else if (a.StartsWith("--channel=")) o.Channel = a.Substring(10);
                else if (a.StartsWith("--placeid=")) int.TryParse(a.Substring(10), out o.PlaceId);
                else if (a.StartsWith("undrwlrd:", StringComparison.OrdinalIgnoreCase)) ParseProtocolUri(a, o);
            }
            if (o.ServerScript == null) o.ServerScript = FindServerScript();
            if (o.ConfigPath == null) o.ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.xml");
            return o;
        }

        static void ParseProtocolUri(string raw, LaunchOptions o)
        {
            try
            {
                var normalized = raw.Replace("undrwlrd:\\", "undrwlrd://");
                var uri = new Uri(normalized);
                var path = (uri.Host + uri.AbsolutePath).Trim('/');
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("launch", StringComparison.OrdinalIgnoreCase))
                {
                    int placeId;
                    if (int.TryParse(parts[1], out placeId)) o.PlaceId = placeId;
                }
            }
            catch { }
        }

        // The exe ships in launcher\dist\; server.js lives at the repo root, two levels up.
        static string FindServerScript()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4; i++)
            {
                var candidate = Path.Combine(dir, "server.js");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // .NET Framework 4.x can otherwise stick to old OS defaults and fail modern HTTPS
            // handshakes with "Could not create SSL/TLS secure channel" against Cloudflare/CDN
            // endpoints. Opt into TLS 1.2 explicitly before any HttpClient use.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            Log.Init(AppDomain.CurrentDomain.BaseDirectory);
            Log.Write("args: " + string.Join(" ", args));
            Log.Write("tls: SecurityProtocol=" + ServicePointManager.SecurityProtocol);

            foreach (var a in args)
            {
                if (a.StartsWith("--preview="))
                {
                    RenderPreview(a.Substring("--preview=".Length));
                    return 0;
                }
                // Non-interactive checks for appsettings.xml handling - no window is ever
                // shown, nothing is launched. Results are written to a file (same pattern as
                // --preview=) since a /target:winexe build has no reliable console output.
                if (a.StartsWith("--selftest-config="))
                {
                    var configPath = a.Substring("--selftest-config=".Length);
                    SelfTestConfig(configPath, configPath + ".selftest.txt");
                    return 0;
                }
                if (a.StartsWith("--selftest-download="))
                {
                    // --selftest-download=<channel>:<output-dir>
                    var rest = a.Substring("--selftest-download=".Length);
                    var sep = rest.IndexOf(':');
                    var channel = sep < 0 ? rest : rest.Substring(0, sep);
                    var destDir = sep < 0 ? Path.Combine(Path.GetTempPath(), "undrwlrd-selftest-download") : rest.Substring(sep + 1);
                    SelfTestDownload(channel, destDir, destDir + ".selftest.txt");
                    return 0;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Replace WinForms' default crash dialog with the same error box the launcher
            // uses for a failed launch, so every error - not just launch failures - shows up
            // as a proper Windows message box instead of being silently lost or looking like
            // an unhandled-exception debugger prompt.
            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception);

            Application.Run(new LauncherForm(LaunchOptions.FromArgs(args)));
            Log.Write("=== launcher exiting ===");
            return 0;
        }

        static void SelfTestConfig(string configPath, string outPath)
        {
            string url, error;
            var result = ConfigReader.TryRead(configPath, out url, out error);
            var lines = new List<string>
            {
                "config : " + configPath,
                "result : " + result,
                "url    : " + (url ?? "(none)"),
                "error  : " + (error ?? "(none)"),
            };
            File.WriteAllLines(outPath, lines.ToArray());
        }

        static void SelfTestDownload(string channel, string destDir, string outPath)
        {
            var result = ClientUpdater.DownloadAsync(channel, destDir, CancellationToken.None).GetAwaiter().GetResult();
            var lines = new List<string>
            {
                "channel : " + channel,
                "destDir : " + destDir,
                "success : " + result.Success,
                "error   : " + (result.Error ?? "(none)"),
            };
            File.WriteAllLines(outPath, lines.ToArray());
        }

        static void ReportCrash(Exception ex)
        {
            Log.Write("UNHANDLED EXCEPTION: " + (ex != null ? ex.ToString() : "(none)"));
            MessageBox.Show(
                ex != null ? ex.ToString() : "An unknown error occurred.",
                "undr.wrld Launcher - Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        // Renders the exact draw routines used by the real window to a PNG, with no display
        // required - used to check the look matches the reference image.
        static void RenderPreview(string path)
        {
            using (var bmp = new Bitmap(534, 307))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Background);
                Drawing.DrawLogo(g, new Rectangle(197, 14, 140, 132), Resources.Logo);
                Drawing.DrawProgress(g, new Rectangle(18, 173, 496, 40), 0.42f, LaunchState.Working);

                using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (var b = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center };
                    g.DrawString("Launching undr.wrld...", f, b, new RectangleF(0, 222, 534, 24), sf);
                }

                var buttonRect = new Rectangle(169, 256, 196, 34);
                Drawing.DrawButtonFace(g, buttonRect);
                using (var f = new Font("Segoe UI", 10f, FontStyle.Bold))
                using (var b = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("Cancel", f, b, buttonRect, sf);
                }

                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
