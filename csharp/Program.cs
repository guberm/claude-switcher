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

record Account(int Num, string Email, bool Active);

// ══════════════════════════════════════════════════════════════════════════════

class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly AccountStore _accounts = new();
    private readonly RateLimitStore _rateStore = new();
    private readonly LogWatcher _watcher;
    private List<Account> _cached = [];

    private static readonly string[] Palette =
        ["#4A90D9", "#E8754A", "#5CB85C", "#9B59B6", "#F39C12", "#E74C3C"];

    // ── bootstrap ──────────────────────────────────────────────────────────

    public TrayContext()
    {
        _tray = new NotifyIcon { Visible = true };
        _watcher = new LogWatcher(OnRateLimitDetected);
        _watcher.Start();
        Refresh();
    }

    // ── rate limit callback ────────────────────────────────────────────────

    void OnRateLimitDetected()
    {
        if (Application.OpenForms.Count > 0)
            Application.OpenForms[0]!.BeginInvoke(HandleRateLimit);
        else
            HandleRateLimit();
    }

    void HandleRateLimit()
    {
        var accounts = _accounts.List();
        var active = accounts.FirstOrDefault(a => a.Active);
        if (active is null || _rateStore.IsLimited(active.Email)) return;

        _rateStore.MarkLimited(active.Email);
        Refresh();

        if (_rateStore.AutoSwitch)
        {
            var next = accounts.FirstOrDefault(a => !a.Active && !_rateStore.IsLimited(a.Email));
            if (next is not null) { DoSwitch(next.Email, autoRestart: true); return; }
        }

        _tray.ShowBalloonTip(5000, "Rate limit hit",
            $"{active.Email} is rate limited.\nSwitch accounts from the tray.", ToolTipIcon.Warning);
    }

    // ── refresh ────────────────────────────────────────────────────────────

    void Refresh()
    {
        _cached = _accounts.List();
        UpdateUI(_cached);
    }

    void UpdateUI(List<Account> accounts)
    {
        var active = accounts.FirstOrDefault(a => a.Active);
        bool limited = active is not null && _rateStore.IsLimited(active.Email);

        foreach (var acc in accounts)
            if (_rateStore.IsExpired(acc.Email)) _rateStore.ClearLimit(acc.Email);

        var oldIcon = _tray.Icon;
        var newIcon = BuildTrayIcon(active, limited);
        _tray.Icon = newIcon;
        oldIcon?.Dispose();

        _tray.Visible = false;
        _tray.Visible = true;

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

        // header
        bool activeLimited = active is not null && _rateStore.IsLimited(active.Email);
        menu.Items.Add(new ToolStripMenuItem(
            active is not null
                ? $"{(activeLimited ? "⚠" : "●")}  {active.Email}"
                : "No active account")
        {
            Enabled = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = activeLimited ? Color.OrangeRed : SystemColors.ControlText,
        });

        menu.Items.Add(new ToolStripSeparator());

        // account list
        foreach (var acc in accounts)
        {
            bool limited = _rateStore.IsLimited(acc.Email);
            string prefix = acc.Active ? "✓  " : "     ";
            var mi = new ToolStripMenuItem(prefix + acc.Email);
            if (limited) mi.ForeColor = Color.OrangeRed;
            if (acc.Active)
            {
                mi.Enabled = false;
            }
            else
            {
                var email = acc.Email;
                mi.Click += (_, _) => DoSwitch(email, autoRestart: true);
            }

            var markItem = new ToolStripMenuItem("Mark as rate limited") { Enabled = !limited };
            markItem.Click += (_, _) => { _rateStore.MarkLimited(acc.Email); Refresh(); };
            var clearItem = new ToolStripMenuItem(limited
                ? $"Clear  (limited since {_rateStore.LimitedAt(acc.Email):HH:mm})"
                : "Clear rate limit") { Enabled = limited };
            clearItem.Click += (_, _) => { _rateStore.ClearLimit(acc.Email); Refresh(); };
            mi.DropDownItems.Add(markItem);
            mi.DropDownItems.Add(clearItem);
            menu.Items.Add(mi);
            menu.Items.Add(MakeBarItem(acc.Email, limited));

            if (limited)
            {
                var email2 = acc.Email;
                var since = _rateStore.LimitedAt(acc.Email);
                var clearBtn = new ToolStripMenuItem(
                    $"     ✕  Clear  (marked {since:HH:mm})")
                {
                    ForeColor = Color.Gray,
                    Font = new Font("Segoe UI", 8f),
                };
                clearBtn.Click += (_, _) => { _rateStore.ClearLimit(email2); Refresh(); };
                menu.Items.Add(clearBtn);
            }
        }

        menu.Items.Add(new ToolStripSeparator());

        // auto-switch toggle
        var autoSwitch = new ToolStripMenuItem("Auto-switch on rate limit")
        {
            Checked = _rateStore.AutoSwitch,
            CheckOnClick = true,
        };
        autoSwitch.CheckedChanged += (_, _) => { _rateStore.AutoSwitch = autoSwitch.Checked; };
        menu.Items.Add(autoSwitch);

        menu.Items.Add(new ToolStripSeparator());

        // account management
        var add = new ToolStripMenuItem("Save current session as account…");
        add.Click += (_, _) => AddAccount();
        menu.Items.Add(add);

        if (accounts.Count > 0)
        {
            var removeMenu = new ToolStripMenuItem("Remove account");
            foreach (var acc in accounts)
            {
                var email = acc.Email;
                var ri = new ToolStripMenuItem(email);
                ri.Click += (_, _) => RemoveAccount(email);
                removeMenu.DropDownItems.Add(ri);
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

    // ── usage bar ──────────────────────────────────────────────────────────

    ToolStripItem MakeBarItem(string email, bool limited)
    {
        const int total = 20;
        string barText;
        Color barColor;

        if (!limited)
        {
            barText = new string('█', total) + "  ✓ available";
            barColor = Color.SeaGreen;
        }
        else
        {
            var since = _rateStore.LimitedAt(email);
            if (!since.HasValue)
            {
                barText = new string('░', total) + "  ⚠ limited";
                barColor = Color.OrangeRed;
            }
            else
            {
                var window = TimeSpan.FromHours(_rateStore.ResetHours);
                var elapsed = DateTime.Now - since.Value;
                if (elapsed > window) elapsed = window;
                int filled = (int)(elapsed.TotalSeconds / window.TotalSeconds * total);
                filled = Math.Clamp(filled, 0, total);
                barText = new string('█', filled) + new string('░', total - filled)
                          + $"  ⚠ {FormatRemaining(email)}";
                barColor = Color.OrangeRed;
            }
        }

        var lbl = new Label
        {
            Text = barText,
            Font = new Font("Consolas", 8f),
            ForeColor = barColor,
            AutoSize = false,
            Width = 260,
            Height = 15,
            BackColor = SystemColors.Menu,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        return new ToolStripControlHost(lbl)
        {
            Margin = new Padding(28, 0, 4, 3),
        };
    }

    string FormatRemaining(string email)
    {
        var since = _rateStore.LimitedAt(email);
        if (!since.HasValue) return "";
        var rem = TimeSpan.FromHours(_rateStore.ResetHours) - (DateTime.Now - since.Value);
        if (rem <= TimeSpan.Zero) return "resets soon";
        return rem.TotalHours >= 1
            ? $"{(int)rem.TotalHours}h {rem.Minutes:D2}m"
            : $"{(int)rem.TotalMinutes}m";
    }

    // ── actions ────────────────────────────────────────────────────────────

    void DoSwitch(string email, bool autoRestart = false)
    {
        var err = _accounts.SwitchTo(email);
        if (err is not null)
        {
            _tray.ShowBalloonTip(4000, "Switch failed", err, ToolTipIcon.Error);
            return;
        }

        _cached = _cached.Select(a => a with { Active = a.Email == email }).ToList();
        UpdateUI(_cached);

        _tray.ShowBalloonTip(4000, "Switched",
            $"Switched to {email}.\nRestarting Claude Code…", ToolTipIcon.Info);
        Thread.Sleep(1500);
        RestartClaude();
        Refresh();
    }

    void AddAccount()
    {
        var email = ShowInputDialog(
            "Save the currently logged-in Claude session as a named account.\n\n" +
            "Enter the email address for this session:",
            "Save Account");
        if (string.IsNullOrWhiteSpace(email)) return;

        var err = _accounts.AddCurrent(email.Trim());
        if (err is not null)
        {
            MessageBox.Show(err, "Save Account", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Refresh();
        _tray.ShowBalloonTip(3000, "Account saved", $"Saved {email}.", ToolTipIcon.Info);
    }

    void RemoveAccount(string email)
    {
        if (MessageBox.Show($"Remove account {email}?", "Remove Account",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _accounts.Remove(email);
        _rateStore.ClearLimit(email);
        Refresh();
        _tray.ShowBalloonTip(2000, "Remove Account", $"Removed {email}.", ToolTipIcon.Info);
    }

    void RestartClaude()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
            try { p.Kill(); } catch { }
        Thread.Sleep(1200);
        try
        {
            Process.Start(new ProcessStartInfo("claude")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
        }
        catch { }
    }

    static string? ShowInputDialog(string prompt, string title)
    {
        var form = new Form
        {
            Text = title, Width = 420, Height = 170,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
        };
        var lbl = new Label { Text = prompt, Left = 12, Top = 12, Width = 385, Height = 56, AutoSize = false };
        var txt = new TextBox { Left = 12, Top = 72, Width = 385 };
        var ok = new Button { Text = "OK", Left = 220, Top = 100, Width = 84, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 313, Top = 100, Width = 84, DialogResult = DialogResult.Cancel };
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Controls.AddRange([lbl, txt, ok, cancel]);
        return form.ShowDialog() == DialogResult.OK ? txt.Text : null;
    }

    // ── tray icon ──────────────────────────────────────────────────────────

    private static readonly string ClaudeExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "bin", "claude.exe");

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
// Account store — manages ~/.claude/accounts/*.json, swaps .credentials.json
// ══════════════════════════════════════════════════════════════════════════════

class AccountStore
{
    private static readonly string AccountsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "accounts");

    private static readonly string CredPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "claude-switcher.json");

    private string? _activeEmail;

    public AccountStore()
    {
        _activeEmail = LoadActiveEmail() ?? DetectActiveEmail();
    }

    // ── public API ──────────────────────────────────────────────────────────

    public List<Account> List()
    {
        var files = GetAccountFiles();
        var result = new List<(string Email, string File)>();

        foreach (var f in files)
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(f));
                var email = node?["email"]?.GetValue<string>();
                if (email is not null) result.Add((email, f));
            }
            catch { }
        }

        result.Sort((a, b) => string.Compare(a.Email, b.Email, StringComparison.OrdinalIgnoreCase));

        // Always detect from actual credentials file so the display stays accurate
        // even if the user switched accounts outside of this tool.
        var activeEmail = DetectActiveEmail() ?? _activeEmail;

        return result
            .Select((r, i) => new Account(i + 1, r.Email, r.Email == activeEmail))
            .ToList();
    }

    // Returns null on success, error message on failure.
    public string? SwitchTo(string email)
    {
        try
        {
            var file = FindFile(email) ?? throw new FileNotFoundException($"Account not found: {email}");
            var node = JsonNode.Parse(File.ReadAllText(file))
                ?? throw new InvalidOperationException("Could not parse account file");
            var cred = node["credentials"]
                ?? throw new InvalidOperationException("Account file has no 'credentials' field");

            File.WriteAllText(CredPath,
                cred.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            SaveActiveEmail(email);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // Saves the currently active .credentials.json under the given email.
    public string? AddCurrent(string email)
    {
        try
        {
            if (!File.Exists(CredPath))
                return "No active session found (~/.claude/.credentials.json is missing).\n" +
                       "Log into Claude Code first.";

            var cred = JsonNode.Parse(File.ReadAllText(CredPath))
                       ?? throw new InvalidOperationException("Could not parse credentials");

            Directory.CreateDirectory(AccountsDir);

            var obj = new JsonObject
            {
                ["email"] = email,
                ["credentials"] = JsonNode.Parse(cred.ToJsonString()),
            };
            File.WriteAllText(GetFilePath(email),
                obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            SaveActiveEmail(email);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Remove(string email)
    {
        var file = FindFile(email);
        if (file is not null) File.Delete(file);
        if (_activeEmail == email) SaveActiveEmail(null);
    }

    // ── internals ──────────────────────────────────────────────────────────

    private string[] GetAccountFiles()
    {
        if (!Directory.Exists(AccountsDir)) return [];
        return Directory.GetFiles(AccountsDir, "*.json");
    }

    private string? FindFile(string email)
    {
        foreach (var f in GetAccountFiles())
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(f));
                if (node?["email"]?.GetValue<string>() == email) return f;
            }
            catch { }
        }
        return null;
    }

    // Compare refreshToken to detect which stored account is currently active.
    private string? DetectActiveEmail()
    {
        try
        {
            if (!File.Exists(CredPath)) return null;
            var current = JsonNode.Parse(File.ReadAllText(CredPath));
            var currentRefresh = current?["claudeAiOauth"]?["refreshToken"]?.GetValue<string>();
            if (currentRefresh is null) return null;

            foreach (var f in GetAccountFiles())
            {
                try
                {
                    var stored = JsonNode.Parse(File.ReadAllText(f));
                    var storedRefresh = stored?["credentials"]?
                        ["claudeAiOauth"]?["refreshToken"]?.GetValue<string>();
                    if (storedRefresh == currentRefresh)
                        return stored?["email"]?.GetValue<string>();
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private string? LoadActiveEmail()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            return JsonNode.Parse(File.ReadAllText(StorePath))?["activeEmail"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private void SaveActiveEmail(string? email)
    {
        _activeEmail = email;
        try
        {
            JsonNode node = File.Exists(StorePath)
                ? JsonNode.Parse(File.ReadAllText(StorePath)) ?? new JsonObject()
                : new JsonObject();
            node["activeEmail"] = email;
            File.WriteAllText(StorePath,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string GetFilePath(string email) =>
        Path.Combine(AccountsDir,
            Regex.Replace(email, @"[^\w@.\-]", "_") + ".json");
}

// ══════════════════════════════════════════════════════════════════════════════
// Rate limit store  (~/.claude/claude-switcher.json)
// ══════════════════════════════════════════════════════════════════════════════

class RateLimitStore
{
    private static readonly string Path_ = Path.Combine(
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
    private readonly List<FileSystemWatcher> _watchers = [];
    private DateTime _lastFired = DateTime.MinValue;

    private static readonly string[] WatchDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude"),
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
            var ext = Path.GetExtension(e.FullPath).ToLower();
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
