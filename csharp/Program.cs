using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using System.Windows.Forms;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new TrayContext());

class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;

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
        Refresh();
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

        _tray.Icon?.Dispose();
        _tray.Icon = BuildTrayIcon(active);
        _tray.Text = active is not null
            ? $"Claude: {active.Email}"
            : "Claude Account Switcher";

        _tray.ContextMenuStrip = BuildMenu(accounts, active);
    }

    // ── menu ───────────────────────────────────────────────────────────────

    ContextMenuStrip BuildMenu(List<Account> accounts, Account? active)
    {
        var menu = new ContextMenuStrip();

        // header
        var header = new ToolStripMenuItem(
            active is not null ? $"● {active.Email}" : "No active account")
        {
            Enabled = false,
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        // accounts — switch
        foreach (var acc in accounts)
        {
            var label = (acc.Active ? "✓  " : "    ") + acc.Email;
            var mi = new ToolStripMenuItem(label);
            if (acc.Active)
            {
                mi.Enabled = false;
            }
            else
            {
                var num = acc.Num; var email = acc.Email;
                mi.Click += (_, _) => SwitchTo(num, email);
            }
            menu.Items.Add(mi);
        }

        menu.Items.Add(new ToolStripSeparator());

        // add account
        var add = new ToolStripMenuItem("Add account…");
        add.Click += (_, _) => AddAccount();
        menu.Items.Add(add);

        // remove account (submenu)
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

        // restart
        var restart = new ToolStripMenuItem("Restart Claude Code");
        restart.Click += (_, _) => RestartClaude();
        menu.Items.Add(restart);

        menu.Items.Add(new ToolStripSeparator());

        // quit
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => { _tray.Visible = false; Application.Exit(); };
        menu.Items.Add(quit);

        return menu;
    }

    // ── actions ────────────────────────────────────────────────────────────

    void SwitchTo(int num, string email)
    {
        RunCmd("cswap", $"--switch-to {num}");
        Refresh();
        _tray.ShowBalloonTip(3000, "Claude Account Switcher",
            $"Switched to {email}\nRestart Claude Code to apply.", ToolTipIcon.Info);
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
        Refresh();
        _tray.ShowBalloonTip(2000, "Remove Account",
            $"Removed {email}.", ToolTipIcon.Info);
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

    Icon BuildTrayIcon(Account? active)
    {
        const int size = 32;
        var claudeIcon = TryLoadClaudeIcon();

        if (claudeIcon is null)
        {
            // fallback: colored circle with initial
            var color = active is not null
                ? ParseHex(Palette[(active.Num - 1) % Palette.Length])
                : Color.FromArgb(0x44, 0x44, 0x44);
            var letter = active is not null
                ? active.Email[0].ToString().ToUpper()
                : "C";
            return MakeLetterIcon(letter, color, size);
        }

        if (active is null)
            return claudeIcon;

        // overlay: small colored badge (bottom-right) with account initial
        var badgeColor = ParseHex(Palette[(active.Num - 1) % Palette.Length]);
        return OverlayBadge(claudeIcon, active.Email[0].ToString().ToUpper(), badgeColor, size);
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

        // badge circle — bottom-right quadrant
        const int b = 14;
        int x = size - b - 1, y = size - b - 1;
        g.FillEllipse(Brushes.Black, x - 1, y - 1, b + 2, b + 2); // outline
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
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
