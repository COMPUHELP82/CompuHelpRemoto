using System.Diagnostics;
using System.Text;

namespace CompuHelpRemoto;

/// <summary>Explorador de archivos del equipo remoto (lado técnico): navegar, descargar y subir.</summary>
public class FilesForm : Form
{
    readonly SecureChannel _ch;
    readonly FileTransfer _ft;
    readonly ListView _list;
    readonly TextBox _txtPath;
    readonly Label _lblStatus;
    readonly ProgressBar _bar;
    string _current = "";
    string _parent;

    public FilesForm(SecureChannel ch, FileTransfer ft, string remoteName)
    {
        _ch = ch; _ft = ft;
        Text = $"Archivos – {remoteName}";
        Size = new Size(820, 560);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);

        var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8) };
        var btnHome = ToolButton("Inicio");
        btnHome.Click += (_, _) => Navigate("");
        var btnUp = ToolButton("↑ Subir nivel");
        btnUp.Click += (_, _) => { if (_parent != null) Navigate(_parent); };
        _txtPath = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10.5f) };
        _txtPath.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(_txtPath.Text.Trim()); } };
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 190, WrapContents = false };
        left.Controls.Add(btnHome);
        left.Controls.Add(btnUp);
        top.Controls.Add(_txtPath);
        top.Controls.Add(left);

        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, AllowDrop = true, MultiSelect = true };
        _list.Columns.Add("Nombre", 380);
        _list.Columns.Add("Tamaño", 110, HorizontalAlignment.Right);
        _list.Columns.Add("Modificado", 160);
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) OpenSelected();
            if (e.KeyCode == Keys.Back && _parent != null) Navigate(_parent);
        };
        _list.DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        _list.DragDrop += (_, e) => Upload((string[])e.Data.GetData(DataFormats.FileDrop));

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(8) };
        var btnDownload = ActionButton("⬇ Descargar a mi PC", Color.FromArgb(37, 99, 235));
        btnDownload.Click += (_, _) => DownloadSelected();
        var btnUpload = ActionButton("⬆ Enviar archivos aquí", Color.FromArgb(22, 163, 74));
        btnUpload.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Archivos para enviar al equipo remoto" };
            if (dlg.ShowDialog(this) == DialogResult.OK) Upload(dlg.FileNames);
        };
        var btnOpen = ActionButton("Abrir mis descargas", Color.FromArgb(100, 110, 130));
        btnOpen.Click += (_, _) =>
        {
            Directory.CreateDirectory(_ft.DefaultFolder);
            Process.Start("explorer.exe", _ft.DefaultFolder);
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        buttons.Controls.AddRange(new Control[] { btnDownload, btnUpload, btnOpen });
        _bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 8 };
        _lblStatus = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Color.FromArgb(80, 90, 110), Text = "Arrastra archivos aquí para enviarlos a la carpeta abierta." };
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_lblStatus);
        bottom.Controls.Add(_bar);

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bottom);

        _ft.Progress += OnProgress;
    }

    static Button ToolButton(string t) => new() { Text = t, AutoSize = true, Height = 30, FlatStyle = FlatStyle.System };

    static Button ActionButton(string t, Color c)
    {
        var b = new Button { Text = t, AutoSize = true, Height = 32, BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0) };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Navigate("");
    }

    void Navigate(string path)
    {
        _lblStatus.Text = "Cargando…";
        _ch.Post(Msg.ListDir, Encoding.UTF8.GetBytes(path ?? ""));
    }

    /// <summary>Llamado por la ventana remota cuando llega el listado.</summary>
    public void ShowListing(FileTransfer.Listing l)
    {
        _current = l.Path;
        _parent = string.IsNullOrEmpty(l.Path) ? null : l.Parent;
        _txtPath.Text = string.IsNullOrEmpty(l.Path) ? "Este equipo" : l.Path;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var it in l.Items)
        {
            var row = new ListViewItem((it.IsDir ? "📁  " : "📄  ") + it.Name) { Tag = it };
            row.SubItems.Add(it.IsDir ? "" : FormatSize(it.Size));
            row.SubItems.Add(it.Modified == DateTime.MinValue ? "" : it.Modified.ToString("dd/MM/yyyy HH:mm"));
            _list.Items.Add(row);
        }
        _list.EndUpdate();
        _lblStatus.Text = $"{l.Items.Count} elementos";
    }

    public void ShowError(string text) => _lblStatus.Text = "⚠ " + text;

    void OpenSelected()
    {
        if (_list.SelectedItems.Count != 1) return;
        var it = (FileTransfer.Item)_list.SelectedItems[0].Tag;
        if (it.IsDir) Navigate(it.FullPath);
        else DownloadSelected();
    }

    void DownloadSelected()
    {
        var files = _list.SelectedItems.Cast<ListViewItem>().Select(i => (FileTransfer.Item)i.Tag).Where(i => !i.IsDir).ToList();
        if (files.Count == 0) { _lblStatus.Text = "Selecciona uno o más archivos (las carpetas no se descargan completas)."; return; }
        foreach (var f in files) _ch.Post(Msg.GetFile, Encoding.UTF8.GetBytes(f.FullPath));
    }

    void Upload(string[] paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) { _lblStatus.Text = "Solo se pueden enviar archivos (no carpetas)."; return; }
        string folder = _current;
        _ = Task.Run(async () =>
        {
            foreach (var f in files)
            {
                var target = string.IsNullOrEmpty(folder) ? Path.GetFileName(f) : Path.Combine(folder, Path.GetFileName(f));
                await _ft.SendFileAsync(f, target);
            }
            if (!string.IsNullOrEmpty(folder)) BeginInvoke(() => Navigate(_current));
        });
    }

    void OnProgress(string text, int pct)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _lblStatus.Text = text;
            if (pct >= 0) _bar.Value = Math.Clamp(pct, 0, 100);
        });
    }

    static string FormatSize(long b) =>
        b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.#} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ft.Progress -= OnProgress;
        base.OnFormClosed(e);
    }
}
