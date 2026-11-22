using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CompuHelpRemoto;

public class MainForm : Form
{
    static readonly Color Navy1 = Color.FromArgb(8, 20, 66);
    static readonly Color Navy2 = Color.FromArgb(27, 63, 160);
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color Muted = Color.FromArgb(100, 110, 130);
    static readonly Color Card = Color.FromArgb(244, 246, 250);

    readonly AppConfig _cfg = AppConfig.Load();
    HostService _host;

    Label _lblId, _lblPass, _lblStatus, _lblLocalIp;
    Panel _dot;
    TextBox _txtTarget;
    Button _btnConnect;

    public MainForm()
    {
        Text = "CompuHelp Soporte Remoto";
        Size = new Size(1000, 620);
        MinimumSize = new Size(900, 580);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        BuildUi();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartHost();
        if (Environment.GetCommandLineArgs().Contains("--minimizado")) WindowState = FormWindowState.Minimized;
    }

    void StartHost()
    {
        _host?.Stop();
        _host = new HostService(this, _cfg);
        _host.StatusChanged += (t, ok) => { _lblStatus.Text = t; _dot.BackColor = ok ? Color.FromArgb(34, 197, 94) : Color.FromArgb(234, 179, 8); };
        _host.SessionStarted += v => { _lblStatus.Text = $"Sesión activa con {v}"; _dot.BackColor = Color.FromArgb(220, 38, 38); };
        _host.SessionEnded += () => { _lblStatus.Text = "Sesión finalizada. Listo para conectar."; _dot.BackColor = Color.FromArgb(34, 197, 94); };
        _lblId.Text = AppConfig.FormatId(_cfg.Id);
        _lblPass.Text = _host.Password;
        var info = "IP en red local: " + string.Join(", ", LocalIps());
        if (_cfg.FixedEnabled && AppConfig.ValidAlias(_cfg.Alias))
            info = $"Usuario fijo: {_cfg.Alias}  (acceso sin confirmación activado)   •   " + info;
        _lblLocalIp.Text = info;
        _host.Start();
    }

    // ------------------------------------------------------------------ UI
    void BuildUi()
    {
        var left = new Panel { Dock = DockStyle.Left, Width = 440 };
        left.Paint += (_, e) =>
        {
            using var br = new LinearGradientBrush(left.ClientRectangle, Navy1, Navy2, 60f);
            e.Graphics.FillRectangle(br, left.ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var glow = new SolidBrush(Color.FromArgb(25, 255, 255, 255));
            e.Graphics.FillEllipse(glow, -120, left.Height - 260, 420, 420);
        };
        left.Resize += (_, _) => left.Invalidate();

        var logo = new Label { Text = "◈ CompuHelp", ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI", 20f, FontStyle.Bold), AutoSize = true, Location = new Point(32, 28) };
        var headline = new Label
        {
            Text = "Soporte remoto\nrápido y seguro",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 26f),
            AutoSize = true,
            Location = new Point(32, 190),
        };
        var sub = new Label
        {
            Text = "Comparte tu ID y contraseña con el técnico\npara recibir ayuda, o escribe el ID de otro\nequipo para darle soporte.",
            ForeColor = Color.FromArgb(190, 205, 240),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 11f),
            AutoSize = true,
            Location = new Point(34, 310),
        };
        left.Controls.AddRange(new Control[] { logo, headline, sub });

        // ---- panel derecho
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(40, 24, 40, 0) };

        var gear = new Button { Text = "⚙", Font = new Font("Segoe UI Symbol", 14f), FlatStyle = FlatStyle.Flat, Size = new Size(40, 40), Anchor = AnchorStyles.Top | AnchorStyles.Right, Cursor = Cursors.Hand, TabStop = false };
        gear.FlatAppearance.BorderSize = 0;
        gear.Click += (_, _) => OpenSettings();

        var h1 = SectionTitle("Permitir control remoto", 40);
        var card = new RoundedPanel { BackColor = Card, Location = new Point(40, 78), Size = new Size(470, 170), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        card.Controls.Add(new Label { Text = "Tu ID", ForeColor = Muted, AutoSize = true, Location = new Point(18, 14), BackColor = Color.Transparent });
        _lblId = new Label { Font = new Font("Segoe UI Semibold", 22f), AutoSize = true, Location = new Point(14, 34), BackColor = Color.Transparent };
        card.Controls.Add(_lblId);
        card.Controls.Add(new Label { Text = "Contraseña", ForeColor = Muted, AutoSize = true, Location = new Point(18, 90), BackColor = Color.Transparent });
        _lblPass = new Label { Font = new Font("Consolas", 20f), AutoSize = true, Location = new Point(14, 110), BackColor = Color.Transparent };
        card.Controls.Add(_lblPass);

        var copyId = IconButton("⧉", "Copiar ID");
        copyId.Location = new Point(410, 38);
        copyId.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        copyId.Click += (_, _) => Clipboard.SetText(_cfg.Id);
        var copyPass = IconButton("⧉", "Copiar contraseña");
        copyPass.Location = new Point(410, 112);
        copyPass.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        copyPass.Click += (_, _) => Clipboard.SetText(_host.Password);
        var newPass = IconButton("↻", "Nueva contraseña");
        newPass.Location = new Point(370, 112);
        newPass.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        newPass.Click += (_, _) => { _host.Password = AppConfig.NewPassword(); _lblPass.Text = _host.Password; };
        card.Controls.AddRange(new Control[] { copyId, copyPass, newPass });

        _lblLocalIp = new Label { ForeColor = Muted, AutoSize = true, Location = new Point(42, 254), Font = new Font("Segoe UI", 8.5f) };

        var h2 = SectionTitle("Controlar un equipo remoto", 300);
        var hint = new Label { Text = "ID o usuario del socio (o IP si está en tu misma red)", ForeColor = Muted, AutoSize = true, Location = new Point(42, 336) };
        _txtTarget = new TextBox { Font = new Font("Segoe UI", 14f), Location = new Point(40, 360), Size = new Size(330, 34), PlaceholderText = "Ej.: 123 456 789  u  oficina-juan", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _txtTarget.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = Connect(); } };
        _btnConnect = new Button
        {
            Text = "Conectar",
            BackColor = Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Location = new Point(380, 359),
            Size = new Size(130, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Cursor = Cursors.Hand,
        };
        _btnConnect.FlatAppearance.BorderSize = 0;
        _btnConnect.Click += async (_, _) => await Connect();

        right.Controls.AddRange(new Control[] { h1, card, _lblLocalIp, h2, hint, _txtTarget, _btnConnect, gear });
        right.Resize += (_, _) => gear.Location = new Point(right.ClientSize.Width - 52, 10);

        // ---- barra de estado
        var status = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(18, 36, 96) };
        _dot = new Panel { Size = new Size(10, 10), Location = new Point(16, 12), BackColor = Color.FromArgb(234, 179, 8) };
        var round = new GraphicsPath(); round.AddEllipse(0, 0, 10, 10); _dot.Region = new Region(round);
        _lblStatus = new Label { Text = "Iniciando…", ForeColor = Color.White, AutoSize = true, Location = new Point(34, 8) };
        status.Controls.Add(_dot);
        status.Controls.Add(_lblStatus);

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(status);
    }

    static Label SectionTitle(string t, int y) =>
        new() { Text = t, Font = new Font("Segoe UI Semibold", 13f), AutoSize = true, Location = new Point(38, y) };

    Button IconButton(string glyph, string tip)
    {
        var b = new Button { Text = glyph, Font = new Font("Segoe UI Symbol", 12f), FlatStyle = FlatStyle.Flat, Size = new Size(36, 36), Cursor = Cursors.Hand, BackColor = Color.Transparent, TabStop = false };
        b.FlatAppearance.BorderSize = 0;
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    static IEnumerable<string> LocalIps()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .DefaultIfEmpty("—")
                .ToList();
        }
        catch { return new[] { "—" }; }
    }

    // ------------------------------------------------------------------ acciones
    async Task Connect()
    {
        var target = _txtTarget.Text.Trim();
        if (target.Length == 0) { _txtTarget.Focus(); return; }
        if (new string(target.Where(char.IsDigit).ToArray()) == _cfg.Id)
        {
            MessageBox.Show(this, "Ese es el ID de este mismo equipo.", Text);
            return;
        }

        var pwd = PasswordPrompt.Ask(this, target);
        if (pwd == null) return;

        _btnConnect.Enabled = false;
        _btnConnect.Text = "Conectando…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var session = await Task.Run(() => ViewerConnector.ConnectAsync(_cfg, target, pwd, cts.Token));
            new ViewerForm(session).Show();
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "No hubo respuesta del equipo remoto.", "No se pudo conectar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudo conectar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnConnect.Enabled = true;
            _btnConnect.Text = "Conectar";
        }
    }

    void OpenSettings()
    {
        using var dlg = new SettingsForm(_cfg);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _cfg.Save();
            StartHost();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _host?.Stop();
        base.OnFormClosing(e);
    }
}

/// <summary>Panel con esquinas redondeadas.</summary>
public class RoundedPanel : Panel
{
    public RoundedPanel() { DoubleBuffered = true; }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var p = new GraphicsPath();
        int r = 16;
        p.AddArc(0, 0, r, r, 180, 90);
        p.AddArc(Width - r - 1, 0, r, r, 270, 90);
        p.AddArc(Width - r - 1, Height - r - 1, r, r, 0, 90);
        p.AddArc(0, Height - r - 1, r, r, 90, 90);
        p.CloseFigure();
        Region = new Region(p);
    }
}

/// <summary>Diálogo para pedir la contraseña del equipo remoto.</summary>
public static class PasswordPrompt
{
    public static string Ask(IWin32Window owner, string target)
    {
        using var f = new Form
        {
            Text = "Contraseña",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 150),
            Font = new Font("Segoe UI", 10f),
        };
        var lbl = new Label { Text = $"Contraseña del equipo {target}:", AutoSize = true, Location = new Point(16, 16) };
        var txt = new TextBox { Location = new Point(18, 44), Width = 324, UseSystemPasswordChar = true, Font = new Font("Segoe UI", 12f) };
        var ok = new Button { Text = "Iniciar sesión", DialogResult = DialogResult.OK, Location = new Point(132, 98), Size = new Size(120, 34) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(258, 98), Size = new Size(84, 34) };
        f.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK && txt.Text.Trim().Length > 0 ? txt.Text.Trim() : null;
    }
}

/// <summary>Configuración: servidor de enlace, puerto, calidad, confirmación.</summary>
public class SettingsForm : Form
{
    public SettingsForm(AppConfig cfg)
    {
        Text = "Configuración";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 590);
        Font = new Font("Segoe UI", 10f);

        int y = 16;
        Label L(string t) { var l = new Label { Text = t, AutoSize = true, Location = new Point(16, y) }; y += 24; return l; }

        var l1 = L("Servidor de enlace (para conectar por ID desde internet):");
        var txtRelay = new TextBox { Text = cfg.RelayServer, Location = new Point(18, y), Width = 400, PlaceholderText = "ej.: soporte.midominio.com:5651  (vacío = solo red local)" };
        y += 42;
        var l2 = L("Puerto para conexiones directas por IP:");
        var numPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = cfg.Port, Location = new Point(18, y), Width = 120 };
        y += 42;
        var l3 = L("Calidad de imagen (más alta = más datos):");
        var numQ = new NumericUpDown { Minimum = 30, Maximum = 90, Increment = 10, Value = Math.Clamp(cfg.Quality, 30, 90), Location = new Point(18, y), Width = 120 };
        y += 42;
        var chk = new CheckBox { Text = "Preguntar antes de aceptar conexiones con la contraseña temporal", Checked = cfg.AskBeforeAccept, AutoSize = true, Location = new Point(18, y) };
        y += 40;

        var sep = new Label { Text = "Acceso con usuario y contraseña fijos (sin confirmación)", Font = new Font("Segoe UI Semibold", 10.5f), AutoSize = true, Location = new Point(16, y) };
        y += 30;
        var l4 = L("Usuario (minúsculas, números, - . _ ; mínimo 3):");
        var txtAlias = new TextBox { Text = cfg.Alias, Location = new Point(18, y), Width = 260, PlaceholderText = "ej.: oficina-juan" };
        y += 40;
        var l5 = L(cfg.FixedEnabled ? "Nueva contraseña fija (vacío = dejar la actual):" : "Contraseña fija (mínimo 8 caracteres):");
        var txtPwd = new TextBox { Location = new Point(18, y), Width = 260, UseSystemPasswordChar = true };
        y += 34;
        var txtPwd2 = new TextBox { Location = new Point(18, y), Width = 260, UseSystemPasswordChar = true, PlaceholderText = "repetir contraseña" };
        y += 34;
        var chkRemove = new CheckBox { Text = "Desactivar el acceso con contraseña fija", AutoSize = true, Location = new Point(18, y), Visible = cfg.FixedEnabled };
        y += 30;
        var chkStartup = new CheckBox { Text = "Iniciar CompuHelp Remoto con Windows", AutoSize = true, Location = new Point(18, y), Checked = Startup.IsEnabled() };
        y += 40;
        var ok = new Button { Text = "Guardar", DialogResult = DialogResult.OK, Location = new Point(236, y), Size = new Size(90, 34) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(332, y), Size = new Size(90, 34) };
        ok.Click += (_, _) =>
        {
            var alias = txtAlias.Text.Trim().ToLowerInvariant();
            if (alias.Length > 0 && !AppConfig.ValidAlias(alias))
            { MessageBox.Show(this, "El usuario solo puede tener minúsculas, números, guion, punto o guion bajo (3 a 32 caracteres)."); DialogResult = DialogResult.None; return; }
            if (txtPwd.Text.Length > 0)
            {
                if (txtPwd.Text.Trim().Length < 8) { MessageBox.Show(this, "La contraseña fija debe tener al menos 8 caracteres."); DialogResult = DialogResult.None; return; }
                if (txtPwd.Text != txtPwd2.Text) { MessageBox.Show(this, "Las contraseñas no coinciden."); DialogResult = DialogResult.None; return; }
            }
            cfg.Alias = alias;
            Startup.Set(chkStartup.Checked);
            if (chkRemove.Checked) cfg.SetFixedPassword(null);
            else if (txtPwd.Text.Length > 0) cfg.SetFixedPassword(txtPwd.Text);
            cfg.RelayServer = txtRelay.Text.Trim();
            cfg.Port = (int)numPort.Value;
            cfg.Quality = (int)numQ.Value;
            cfg.AskBeforeAccept = chk.Checked;
        };
        Controls.AddRange(new Control[] { l1, txtRelay, l2, numPort, l3, numQ, chk, sep, l4, txtAlias, l5, txtPwd, txtPwd2, chkRemove, chkStartup, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
    }
}

/// <summary>Inicio automático con Windows (registro del usuario actual).</summary>
public static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "CompuHelpRemoto";

    public static bool IsEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) != null;
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimizado");
            else k.DeleteValue(Name, false);
        }
        catch { }
    }
}
