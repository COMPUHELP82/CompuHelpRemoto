namespace CompuHelpRemoto;

/// <summary>Aviso visible (siempre arriba) mientras alguien controla este equipo.</summary>
public class SessionBanner : Form
{
    readonly Label _lbl;
    readonly string _defaultText;
    readonly System.Windows.Forms.Timer _revert = new() { Interval = 5000 };

    public SessionBanner(string viewer, Action onDisconnect, Action<string> onSendFile)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(185, 28, 28);
        Size = new Size(600, 44);
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top);

        _defaultText = $"●  {viewer} está controlando este equipo";
        _lbl = new Label
        {
            Text = _defaultText,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(12, 0),
            Size = new Size(340, 44),
        };
        var btnFile = SmallButton("Enviar archivo", 358);
        btnFile.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Enviar archivos al técnico" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                foreach (var f in dlg.FileNames) onSendFile(f);
        };
        var btn = SmallButton("Desconectar", 482);
        btn.Click += (_, _) => { onDisconnect(); Close(); };
        Controls.Add(_lbl);
        Controls.Add(btnFile);
        Controls.Add(btn);

        _revert.Tick += (_, _) => { _revert.Stop(); _lbl.Text = _defaultText; };
    }

    Button SmallButton(string text, int x)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(185, 28, 28),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(116, 30),
            Location = new Point(x, 7),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    /// <summary>Muestra un mensaje temporal (por ejemplo, progreso de archivos).</summary>
    public void ShowInfo(string text)
    {
        if (IsDisposed) return;
        _lbl.Text = "●  " + text;
        _revert.Stop();
        _revert.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _revert.Dispose();
        base.Dispose(disposing);
    }
}
