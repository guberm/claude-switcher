using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ── single instance mutex ──────────────────────────────────────────────────
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
    private readonly RateLimitStore _limits = new();
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

        _watcher = new LogWatcher(onRateLimitDetected: OnRateLimitDetected);
        _watcher.Start();

        Refresh();
    }

    // ── rate-limit event (called from background thread) ───────────────────

    void OnRateLimitDetected()
    {
        // marshal to UI thread
        if (Application.OpenForms.Count > 0)
            Application.OpenForms[0]!.BeginInvoke(HandleRateLimit);
        else
            HandleRateLimit();
    }

    void HandleRateLimit()
    {
        var accounts = ParseAccounts();
        var active = accounts.FirstOrDefault(a => a.Active);
        if (active is null) return;

        // already marked — avoid duplicate handling
        if (_limits.IsLimited(active.Email)) return;

        _limits.MarkLimited(active.Email);
        Refresh();

        if (_limits.AutoSwitch)
        {
            var next = accounts.FirstOrDefault(a => !a.Active && !_limits.IsLimited(a.Email));
            if (next is not null)
            {
                SwitchTo(next.Num, next.Email, autoRestart: true);
                return;
            }
        }

        _tray.ShowBalloonTip(5000, "Rate limit detected",
            $"{active.Email} hit its limit.\nSwitch accounts from the tray.",
            ToolTipIcon.Warning);
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
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    List<Account> ParseAccounts()
    {
        var result = new List<Account>();
        try
        {
            var text = RunCmd("cswap", "--list");
            foreach (var line in text.Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^(\d+):\s+(.+?)(\s+\(active\))?\s*$");
                if (m.Success)
                    result.Add(new(
                        int.Parse(m.Groups[1].Value),
                        m.Groups[2].Value.Trim(),
                        m.Groups[3].Success));
            }
        }
        catch { }
        return result;
    }

    // ── UI refresh ─────────────────────────────────────────────────────────

    void Refresh()
    {
        var accounts = ParseAccounts();
        var active = accounts.FirstOrDefault(a => a.Active);
        bool activeLimited = active is not null && _limits.IsLimited(active.Email);

        _tray.Icon?.Dispose();
        _tray.Icon = BuildTrayIcon(active, activeLimited);

        _tray.Text = active is not null
            ? $"Claude: {active.Email}{(activeLimited ? " ⚠" : "")}"
            : "Claude Account Switcher";

        _tray.ContextMenuStrip = BuildMenu(accounts, active);
    }

    // ── menu ───────────────────────────────────────────────────────────────

    ContextMenuStrip BuildMenu(List<Account> accounts, Account? active)
    {
        var menu = new ContextMenuStrip();

        // header
        string headerText = active is not null
            ? (_limits.IsLimited(active.Email) ? $"⚠  {active.Email}" : $"●  {active.Email}")
            : "No active account";
        menu.Items.Add(new ToolStripMenuItem(headerText)
        {
            Enabled = false,
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        });
        menu.Items.Add(new ToolStripSeparator());

        // accounts
        foreach (var acc in accounts)
        {
            bool limited = _limits.IsLimited(acc.Email);
            string prefix = acc.Active ? "✓  " : "    ";
            string suffix = limited ? "  ⚠" : "";
            var mi = new ToolStripMenuItem($"{prefix}{acc.Email}{suffix}");

            if (limited) mi.ForeColor = Color.OrangeRed;
            if (acc.Active) mi.Enabled = false;
            else { var num = acc.Num; var email = acc.Email; mi.Click += (_, _) => SwitchTo(num, email); }

            menu.Items.Add(mi);
        }

        menu.Items.Add(new ToolStripSeparator());

        // rate limit submenu per account
        if (accounts.Count > 0)
        {
            var limitsMenu = new ToolStripMenuItem("Rate limits");
            foreach (var acc in accounts)
            {
                bool limited = _limits.IsLimited(acc.Email);
                var sub = new ToolStripMenuItem(acc.Email);

                var mark = new ToolStripMenuItem("Mark as limited");
                mark.Enabled = !limited;
                mark.Click += (_, _) => { _limits.MarkLimited(acc.Email); Refresh(); };

                var clear = new ToolStripMenuItem(limited
                    ? $"Clear  (since {_limits.LimitedSince(acc.Email):HH:mm})"
                    : "Clear");
                clear.Enabled = limited;
                clear.Click += (_, _) => { _limits.ClearLimit(acc.Email); Refresh(); };

                sub.DropDownItems.Add(mark);
                sub.DropDownItems.Add(clear);
                limitsMenu.DropDownItems.Add(sub);
            }
            menu.Items.Add(limitsMenu);
        }

        // auto-switch toggle
        var autoSwitch = new ToolStripMenuItem("Auto-switch on rate limit")
        {
            Checked = _limits.AutoSwitch,
            CheckOnClick = true,
        };
        autoSwitch.CheckedChanged += (_, _) => { _limits.AutoSwitch = autoSwitch.Checked; };
        menu.Items.Add(autoSwitch);

        menu.Items.Add(new ToolStripSeparator());

        // add account
        var add = new ToolStripMenuItem("Add account…");
        add.Click += (_, _) => AddAccount();
        menu.Items.Add(add);

        // remove account submenu
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

    // ── actions ────────────────────────────────────────────────────────────

    void SwitchTo(int num, string email, bool autoRestart = false)
    {
        RunCmd("cswap", $"--switch-to {num}");
        Refresh();

        if (autoRestart)
        {
            _tray.ShowBalloonTip(4000, "Auto-switched",
                $"Rate limit detected → switched to {email}.\nRestarting Claude Code…",
                ToolTipIcon.Info);
            Thread.Sleep(1500);
            RestartClaude();
        }
        else
        {
            _tray.ShowBalloonTip(3000, "Claude Account Switcher",
                $"Switched to {email}\nRestart Claude Code to apply.", ToolTipIcon.Info);
        }
    }

    void AddAccount()
    {
        var dlg = MessageBox.Show(
            "Log into the new account in Claude Code first.\n\n" +
            "  claude /logout\n  claude /login\n\n" +
            "Click OK when done.",
            "Add Account", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (dlg != DialogResult.OK) return;

        var output = RunCmd("cswap", "--add-account").Trim();
        Refresh();
        _tray.ShowBalloonTip(3000, "Add Account",
            output.Length > 0 ? output : "Done.", ToolTipIcon.Info);
    }

    void RemoveAccount(int num, string email)
    {
        var dlg = MessageBox.Show(
            $"Remove account {email}?",
            "Remove Account", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (dlg != DialogResult.Yes) return;

        RunCmd("cswap", $"--remove-account {num}");
        _limits.ClearLimit(email);
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

    // ── icon building ──────────────────────────────────────────────────────

    Icon BuildTrayIcon(Account? active, bool limited)
    {
        const int size = 32;
        var claudeIcon = TryLoadClaudeIcon();

        if (claudeIcon is null)
        {
            var color = limited ? Color.OrangeRed
                : active is not null ? ParseHex(Palette[(active.Num - 1) % Palette.Length])
                : Color.FromArgb(0x44, 0x44, 0x44);
            var letter = limited ? "!" : active is not null ? active.Email[0].ToString().ToUpper() : "C";
            return MakeLetterIcon(letter, color, size);
        }

        if (active is null) return claudeIcon;

        var badgeColor = limited ? Color.OrangeRed : ParseHex(Palette[(active.Num - 1) % Palette.Length]);
        var badgeLetter = limited ? "!" : active.Email[0].ToString().ToUpper();
        return OverlayBadge(claudeIcon, badgeLetter, badgeColor, size);
    }

    static Icon? TryLoadClaudeIcon()
    {
        try
        {
            if (!File.Exists(ClaudeExe)) return null;
            return Icon.ExtractAssociatedIcon(ClaudeExe);
        }
        catch { return null; }
    }

    static Icon OverlayBadge(Icon baseIcon, string letter, Color badgeColor, int size)
    {
        using var bmp = new Bitmap(baseIcon.ToBitmap(), size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        const int b = 14;
        int x = size - b - 1, y = size - b - 1;
        g.FillEllipse(Brushes.Black, x - 1, y - 1, b + 2, b + 2);
        g.FillEllipse(new SolidBrush(badgeColor), x, y, b, b);

        using var font = new Font("Arial", 7, FontStyle.Bold);
        var sz = g.MeasureString(letter, font);
        g.DrawString(letter, font, Brushes.White,
            x + (b - sz.Width) / 2f,
            y + (b - sz.Height) / 2f - 0.5f);

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

    // ── cleanup ────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// Rate limit store — persisted to ~/.claude/claude-switcher.json
// ══════════════════════════════════════════════════════════════════════════════

class RateLimitStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "claude-switcher.json");

    private Dictionary<string, DateTime> _limits = new();
    private bool _autoSwitch = true;

    public RateLimitStore() => Load();

    public bool AutoSwitch
    {
        get => _autoSwitch;
        set { _autoSwitch = value; Save(); }
    }

    public bool IsLimited(string email) => _limits.ContainsKey(email);

    public DateTime? LimitedSince(string email) =>
        _limits.TryGetValue(email, out var t) ? t : null;

    public void MarkLimited(string email) { _limits[email] = DateTime.Now; Save(); }

    public void ClearLimit(string email) { _limits.Remove(email); Save(); }

    void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var node = JsonNode.Parse(File.ReadAllText(StorePath));
            if (node is null) return;

            _autoSwitch = node["autoSwitch"]?.GetValue<bool>() ?? true;

            var limits = node["rateLimits"]?.AsObject();
            if (limits is not null)
                foreach (var kv in limits)
                    if (DateTime.TryParse(kv.Value?.GetValue<string>(), out var dt))
                        _limits[kv.Key] = dt;
        }
        catch { }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var obj = new JsonObject
            {
                ["autoSwitch"] = _autoSwitch,
                ["rateLimits"] = new JsonObject(
                    _limits.Select(kv =>
                        KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value.ToString("o"))))
            };
            File.WriteAllText(StorePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// Log watcher — watches ~/.claude/ for rate-limit keywords in log files
// ══════════════════════════════════════════════════════════════════════════════

class LogWatcher : IDisposable
{
    private readonly Action _onRateLimitDetected;
    private readonly List<FileSystemWatcher> _watchers = new();
    private DateTime _lastFired = DateTime.MinValue;

    private static readonly string[] WatchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude"),
    ];

    private static readonly Regex RateLimitPattern = new(
        @"rate.?limit|too many requests|429|quota exceeded|capacity reached|claude is at capacity",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LogWatcher(Action onRateLimitDetected)
    {
        _onRateLimitDetected = onRateLimitDetected;
    }

    public void Start()
    {
        foreach (var path in WatchPaths)
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                var w = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                w.Changed += OnFileChanged;
                _watchers.Add(w);
            }
            catch { }
        }
    }

    void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // debounce: fire at most once per 10 seconds
        if ((DateTime.Now - _lastFired).TotalSeconds < 10) return;

        try
        {
            if (!File.Exists(e.FullPath)) return;
            var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            if (ext is not (".log" or ".txt" or ".json" or "")) return;

            // read last 8 KB
            using var fs = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long offset = Math.Max(0, fs.Length - 8192);
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var tail = reader.ReadToEnd();

            if (RateLimitPattern.IsMatch(tail))
            {
                _lastFired = DateTime.Now;
                _onRateLimitDetected();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
    }
}
