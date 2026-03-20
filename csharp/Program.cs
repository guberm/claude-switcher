using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ── single instance ────────────────────────────────────────────────────────
using var mutex = new Mutex(true, "Global\\ClaudeTray_SingleInstance", out bool isFirst);
if (!isFirst)
{
    MessageBox.Show("Claude Switcher is already running.", "Claude Switcher",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new TrayContext());

// ══════════════════════════════════════════════════════════════════════════════

class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly RateLimitStore _store = new();
    private readonly LogWatcher _watcher;

    private static readonly string[] Palette =
        ["#4A90D9", "#E8754A", "#5CB85C", "#9B59B6", "#F39C12", "#E74C3C"];

    private static readonly string ClaudeExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "bin", "claude.exe");

    record Account(int Num, string Email, bool Active);

    // ── bootstrap ──────────────────────────────────────────────────────────

    public TrayContext()
    {
        _tray = new NotifyIcon { Visible = true };
        _watcher = new LogWatcher(OnRateLimitDetected);
        _watcher.Start();
        Refresh();
    }

    // ── rate limit callback (background thread) ────────────────────────────

    void OnRateLimitDetected()
    {
        if (Application.OpenForms.Count > 0)
            Application.OpenForms[0]!.BeginInvoke(HandleRateLimit);
        else
            HandleRateLimit();
    }

    void HandleRateLimit()
    {
        var accounts = ParseAccounts();
        var active = accounts.FirstOrDefault(a => a.Active);
        if (active is null || _store.IsLimited(active.Email)) return;

        _store.MarkLimited(active.Email);
        Refresh();

        if (_store.AutoSwitch)
        {
            var next = accounts.FirstOrDefault(a => !a.Active && !_store.IsLimited(a.Email));
            if (next is not null) { SwitchTo(next.Num, next.Email, autoRestart: true); return; }
        }

        _tray.ShowBalloonTip(5000, "Rate limit hit",
            $"{active.Email} is rate limited.\nSwitch accounts from the tray.", ToolTipIcon.Warning);
    }

    // ── cswap ──────────────────────────────────────────────────────────────

    static string RunCmd(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    List<Account> ParseAccounts()
    {
        var result = new List<Account>();
        try
        {
            foreach (var line in RunCmd("cswap", "--list").Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^(\d+):\s+(.+?)(\s+\(active\))?\s*$");
                if (m.Success)
                    result.Add(new(int.Parse(m.Groups[1].Value),
                        m.Groups[2].Value.Trim(), m.Groups[3].Success));
            }
        }
        catch { }
        return result;
    }

    // ── refresh ────────────────────────────────────────────────────────────

    void Refresh()
    {
        var accounts = ParseAccounts();
        var active = accounts.FirstOrDefault(a => a.Active);
        bool limited = active is not null && _store.IsLimited(active.Email);

        // auto-clear expired limits
        foreach (var acc in accounts)
            if (_store.IsExpired(acc.Email)) _store.ClearLimit(acc.Email);

        _tray.Icon?.Dispose();
        _tray.Icon = BuildTrayIcon(active, limited);
        _tray.Text = active is not null
            ? $"Claude: {active.Email}{(limited ? " ⚠ rate limited" : "")}"
            : "Claude Account Switcher";
        _tray.ContextMenuStrip = BuildMenu(accounts, active);
    }

    // ── menu ───────────────────────────────────────────────────────────────

    ContextMenuStrip BuildMenu(List<Account> accounts, Account? active)
    {
        var menu = new ContextMenuStrip();
        menu.Font = new Font("Segoe UI", 9f);

        // ── active account header ──
        bool activeLimited = active is not null && _store.IsLimited(active.Email);
        string headerIcon = activeLimited ? "⚠" : "●";
        string headerText = active is not null
            ? $"{headerIcon}  {active.Email}"
            : "No active account";

        var header = new ToolStripMenuItem(headerText)
        {
            Enabled = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = activeLimited ? Color.OrangeRed : SystemColors.ControlText,
        };
        menu.Items.Add(header);

        // ── rate limit progress bar (only when limited) ──
        if (activeLimited && active is not null)
        {
            var since = _store.LimitedAt(active.Email);
            if (since.HasValue)
            {
                var elapsed = DateTime.Now - since.Value;
                var window = TimeSpan.FromHours(_store.ResetHours);
                var remaining = window - elapsed;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                int pct = (int)Math.Min(100, elapsed.TotalSeconds / window.TotalSeconds * 100);

                // label
                string remainStr = remaining.TotalMinutes < 1
                    ? "resets soon"
                    : remaining.TotalHours >= 1
                        ? $"resets in {(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
                        : $"resets in {(int)remaining.TotalMinutes}m";

                var lbl = new ToolStripMenuItem(remainStr)
                {
                    Enabled = false,
                    ForeColor = Color.OrangeRed,
                    Font = new Font("Segoe UI", 8f),
                    Padding = new Padding(22, 0, 4, 0),
                };
                menu.Items.Add(lbl);

                // progress bar (fills left→right as time-to-reset counts down)
                var pb = new ToolStripProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 100 - pct,   // full = just limited, empty = about to reset
                    Size = new Size(160, 14),
                    Margin = new Padding(26, 2, 8, 4),
                    Style = ProgressBarStyle.Continuous,
                };
                menu.Items.Add(pb);
            }
        }

        menu.Items.Add(new ToolStripSeparator());

        // ── account list ──
        foreach (var acc in accounts)
        {
            bool limited = _store.IsLimited(acc.Email);
            string status = limited
                ? $"  ⚠ {FormatRemaining(acc.Email)}"
                : "";

            string label = (acc.Active ? "✓  " : "     ") + acc.Email + status;
            var mi = new ToolStripMenuItem(label);

            if (limited) mi.ForeColor = Color.OrangeRed;

            if (acc.Active)
            {
                mi.Enabled = false;
            }
            else
            {
                var num = acc.Num; var email = acc.Email;
                mi.Click += (_, _) => SwitchTo(num, email);
            }

            // right-click submenu: mark / clear limit
            var markItem = new ToolStripMenuItem("Mark as rate limited");
            markItem.Enabled = !limited;
            markItem.Click += (_, _) => { _store.MarkLimited(acc.Email); Refresh(); };

            var clearItem = new ToolStripMenuItem(limited
                ? $"Clear rate limit  (since {_store.LimitedAt(acc.Email):HH:mm})"
                : "Clear rate limit");
            clearItem.Enabled = limited;
            clearItem.Click += (_, _) => { _store.ClearLimit(acc.Email); Refresh(); };

            mi.DropDownItems.Add(markItem);
            mi.DropDownItems.Add(clearItem);

            menu.Items.Add(mi);
        }

        menu.Items.Add(new ToolStripSeparator());

        // ── auto-switch toggle ──
        var autoSwitch = new ToolStripMenuItem("Auto-switch on rate limit")
        {
            Checked = _store.AutoSwitch,
            CheckOnClick = true,
        };
        autoSwitch.CheckedChanged += (_, _) => { _store.AutoSwitch = autoSwitch.Checked; };
        menu.Items.Add(autoSwitch);

        menu.Items.Add(new ToolStripSeparator());

        // ── account management ──
        var add = new ToolStripMenuItem("Add account…");
        add.Click += (_, _) => AddAccount();
        menu.Items.Add(add);

        if (accounts.Count > 0)
        {
            var removeMenu = new ToolStripMenuItem("Remove account");
            foreach (var acc in accounts)
            {
                var num = acc.Num; var email = acc.Email;
                var mi = new ToolStripMenuItem(email);
                mi.Click += (_, _) => RemoveAccount(num, email);
                removeMenu.DropDownItems.Add(mi);
            }
            menu.Items.Add(removeMenu);
        }

        menu.Items.Add(new ToolStripSeparator());

        var restart = new ToolStripMenuItem("Restart Claude Code");
        restart.Click += (_, _) => RestartClaude();
        menu.Items.Add(restart);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => { _tray.Visible = false; Application.Exit(); };
        menu.Items.Add(quit);

        return menu;
    }

    string FormatRemaining(string email)
    {
        var since = _store.LimitedAt(email);
        if (!since.HasValue) return "";
        var rem = TimeSpan.FromHours(_store.ResetHours) - (DateTime.Now - since.Value);
        if (rem <= TimeSpan.Zero) return "resets soon";
        return rem.TotalHours >= 1
            ? $"{(int)rem.TotalHours}h {rem.Minutes:D2}m"
            : $"{(int)rem.TotalMinutes}m";
    }

    // ── actions ────────────────────────────────────────────────────────────

    void SwitchTo(int num, string email, bool autoRestart = false)
    {
        RunCmd("cswap", $"--switch-to {num}");
        Refresh();
        if (autoRestart)
        {
            _tray.ShowBalloonTip(4000, "Auto-switched",
                $"Switched to {email}.\nRestarting Claude Code…", ToolTipIcon.Info);
            Thread.Sleep(1500);
            RestartClaude();
        }
        else
        {
            _tray.ShowBalloonTip(3000, "Switched",
                $"Now using {email}\nRestart Claude Code to apply.", ToolTipIcon.Info);
        }
    }

    void AddAccount()
    {
        if (MessageBox.Show(
            "Log into the new account in Claude Code first.\n\n" +
            "  claude /logout\n  claude /login\n\n" +
            "Click OK when done.",
            "Add Account", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        var output = RunCmd("cswap", "--add-account").Trim();
        Refresh();
        _tray.ShowBalloonTip(3000, "Add Account", output.Length > 0 ? output : "Done.", ToolTipIcon.Info);
    }

    void RemoveAccount(int num, string email)
    {
        if (MessageBox.Show($"Remove account {email}?", "Remove Account",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        RunCmd("cswap", $"--remove-account {num}");
        _store.ClearLimit(email);
        Refresh();
        _tray.ShowBalloonTip(2000, "Remove Account", $"Removed {email}.", ToolTipIcon.Info);
    }

    void RestartClaude()
    {
        RunCmd("taskkill", "/F /IM claude.exe");
        Thread.Sleep(800);
        Process.Start(new ProcessStartInfo("claude")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        });
    }

    // ── tray icon ──────────────────────────────────────────────────────────

    Icon BuildTrayIcon(Account? active, bool limited)
    {
        const int size = 32;
        var base_ = TryLoadClaudeIcon();
        var badgeColor = limited ? Color.OrangeRed
            : active is not null ? ParseHex(Palette[(active.Num - 1) % Palette.Length])
            : Color.FromArgb(0x66, 0x66, 0x66);
        var badgeLetter = limited ? "!" : active is not null ? active.Email[0].ToString().ToUpper() : "C";

        if (base_ is null) return MakeLetterIcon(badgeLetter, badgeColor, size);
        if (active is null) return base_;
        return OverlayBadge(base_, badgeLetter, badgeColor, size);
    }

    static Icon? TryLoadClaudeIcon()
    {
        try { return File.Exists(ClaudeExe) ? Icon.ExtractAssociatedIcon(ClaudeExe) : null; }
        catch { return null; }
    }

    static Icon OverlayBadge(Icon baseIcon, string letter, Color color, int size)
    {
        using var bmp = new Bitmap(baseIcon.ToBitmap(), size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        const int b = 14;
        int x = size - b - 1, y = size - b - 1;
        g.FillEllipse(Brushes.Black, x - 1, y - 1, b + 2, b + 2);
        g.FillEllipse(new SolidBrush(color), x, y, b, b);
        using var font = new Font("Arial", 7, FontStyle.Bold);
        var sz = g.MeasureString(letter, font);
        g.DrawString(letter, font, Brushes.White,
            x + (b - sz.Width) / 2f, y + (b - sz.Height) / 2f - 0.5f);
        return Icon.FromHandle(bmp.GetHicon());
    }

    static Icon MakeLetterIcon(string letter, Color color, int size)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.FillEllipse(new SolidBrush(color), 1, 1, size - 2, size - 2);
        using var font = new Font("Arial", 14, FontStyle.Bold);
        var sz = g.MeasureString(letter, font);
        g.DrawString(letter, font, Brushes.White,
            (size - sz.Width) / 2f - 1f, (size - sz.Height) / 2f);
        return Icon.FromHandle(bmp.GetHicon());
    }

    static Color ParseHex(string hex) => Color.FromArgb(
        Convert.ToInt32(hex[1..3], 16),
        Convert.ToInt32(hex[3..5], 16),
        Convert.ToInt32(hex[5..7], 16));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _watcher.Dispose(); _tray.Icon?.Dispose(); _tray.Dispose(); }
        base.Dispose(disposing);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// Rate limit store  (~/.claude/claude-switcher.json)
// ══════════════════════════════════════════════════════════════════════════════

class RateLimitStore
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "claude-switcher.json");

    private Dictionary<string, DateTime> _limits = new();
    private bool _autoSwitch = true;
    private double _resetHours = 5.0;

    public RateLimitStore() => Load();

    public bool AutoSwitch   { get => _autoSwitch;  set { _autoSwitch = value;  Save(); } }
    public double ResetHours { get => _resetHours;  set { _resetHours = value;  Save(); } }

    public bool IsLimited(string email) => _limits.ContainsKey(email);
    public DateTime? LimitedAt(string email) => _limits.TryGetValue(email, out var t) ? t : null;

    public bool IsExpired(string email) =>
        _limits.TryGetValue(email, out var t) &&
        (DateTime.Now - t).TotalHours >= _resetHours;

    public void MarkLimited(string email) { _limits[email] = DateTime.Now; Save(); }
    public void ClearLimit(string email)  { _limits.Remove(email); Save(); }

    void Load()
    {
        try
        {
            if (!File.Exists(Path_)) return;
            var node = JsonNode.Parse(File.ReadAllText(Path_));
            if (node is null) return;
            _autoSwitch = node["autoSwitch"]?.GetValue<bool>() ?? true;
            _resetHours = node["resetHours"]?.GetValue<double>() ?? 5.0;
            var lim = node["rateLimits"]?.AsObject();
            if (lim is not null)
                foreach (var kv in lim)
                    if (DateTime.TryParse(kv.Value?.GetValue<string>(), out var dt))
                        _limits[kv.Key] = dt;
        }
        catch { }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            var obj = new JsonObject
            {
                ["autoSwitch"] = _autoSwitch,
                ["resetHours"] = _resetHours,
                ["rateLimits"] = new JsonObject(
                    _limits.Select(kv =>
                        KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value.ToString("o"))))
            };
            File.WriteAllText(Path_, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// Log watcher  — polls ~/.claude/ for rate-limit keywords
// ══════════════════════════════════════════════════════════════════════════════

class LogWatcher : IDisposable
{
    private readonly Action _callback;
    private readonly List<FileSystemWatcher> _watchers = new();
    private DateTime _lastFired = DateTime.MinValue;

    private static readonly string[] WatchDirs =
    [
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude"),
    ];

    private static readonly Regex RlPattern = new(
        @"rate.?limit|too many requests|429|quota exceeded|capacity reached|claude is at capacity",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LogWatcher(Action callback) => _callback = callback;

    public void Start()
    {
        foreach (var dir in WatchDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                w.Changed += OnChanged;
                _watchers.Add(w);
            }
            catch { }
        }
    }

    void OnChanged(object _, FileSystemEventArgs e)
    {
        if ((DateTime.Now - _lastFired).TotalSeconds < 10) return;
        try
        {
            if (!File.Exists(e.FullPath)) return;
            var ext = System.IO.Path.GetExtension(e.FullPath).ToLower();
            if (ext is not (".log" or ".txt" or ".json" or "")) return;

            using var fs = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, fs.Length - 8192), SeekOrigin.Begin);
            var tail = new StreamReader(fs).ReadToEnd();

            if (RlPattern.IsMatch(tail))
            {
                _lastFired = DateTime.Now;
                _callback();
            }
        }
        catch { }
    }

    public void Dispose() { foreach (var w in _watchers) w.Dispose(); }
}
