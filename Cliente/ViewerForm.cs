using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace CompuHelpRemoto;

/// <summary>Ventana del técnico: muestra la pantalla remota y envía mouse y teclado.</summary>
public class ViewerForm : Form, IMessageFilter
{
    readonly SecureChannel _ch;
    readonly RemoteCanvas _canvas;
    readonly Label _lblStatus;
    readonly Panel _bar;
    readonly CancellationTokenSource _cts = new();
    readonly FileTransfer _files;
    readonly string _remoteName;
    FilesForm _filesForm;
    DateTime _infoUntil;
    int _frames;
    bool _closing;
    FormWindowState _prevState;

    public ViewerForm(ViewerConnector.Session s)
    {
        _ch = s.Channel;
        _remoteName = s.RemoteName;
        _files = new FileTransfer(_ch, allowRootedPaths: false);
        _files.Progress += (text, pct) => BeginInvokeSafe(() => { _lblStatus.Text = pct >= 0 && pct < 100 ? $"{text} {pct}%" : text; _infoUntil = DateTime.Now.AddSeconds(4); });
        _files.Received += path => BeginInvokeSafe(() =>
        {
            if (_filesForm != null && !_filesForm.IsDisposed) return;   // ya se ve el progreso en la ventana de archivos
            if (MessageBox.Show(this, $"Recibiste el archivo:\n{path}\n\n¿Abrir la carpeta?", "Archivo recibido",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        });
        Text = $"CompuHelp Remoto – {s.RemoteName}";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 800);
        WindowState = FormWindowState.Maximized;
        KeyPreview = false;

        _bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(15, 32, 85) };
        var title = new Label
        {
            Text = "  🖥  " + s.RemoteName,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(8, 10),
        };
        _lblStatus = new Label
        {
            ForeColor = Color.FromArgb(170, 190, 230),
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Location = new Point(260, 12),
        };
        var btnClose = BarButton("Desconectar", Color.FromArgb(220, 38, 38));
        btnClose.Click += (_, _) => Close();
        var btnFull = BarButton("Pantalla completa", Color.FromArgb(37, 99, 235));
        btnFull.Click += (_, _) => ToggleFullScreen();
        var btnFiles = BarButton("📁 Archivos", Color.FromArgb(22, 163, 74));
        btnFiles.Click += (_, _) => OpenFiles();
        var flow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, Width = 420, Padding = new Padding(0, 5, 8, 0), BackColor = Color.Transparent };
        flow.Controls.Add(btnClose);
        flow.Controls.Add(btnFull);
        flow.Controls.Add(btnFiles);
        _bar.Controls.Add(title);
        _bar.Controls.Add(_lblStatus);
        _bar.Controls.Add(flow);

        _canvas = new RemoteCanvas(s.Width, s.Height) { Dock = DockStyle.Fill };
        _canvas.MouseMove += (_, e) => SendMouseMove(e.Location);
        _canvas.MouseDown += (_, e) => { _canvas.Focus(); SendMouseMove(e.Location); SendButton(e.Button, true); };
        _canvas.MouseUp += (_, e) => { SendMouseMove(e.Location); SendButton(e.Button, false); };
        _canvas.MouseWheel += (_, e) => _ch.Post(Msg.MouseWheel, BitConverter.GetBytes(e.Delta));
        // arrastrar archivos sobre la pantalla remota = enviarlos a Descargas\CompuHelp del otro equipo
        _canvas.AllowDrop = true;
        _canvas.DragEnter += (_, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        _canvas.DragDrop += (_, e) =>
        {
            var list = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(File.Exists).ToList();
            _ = Task.Run(async () => { foreach (var f in list) await _files.SendFileAsync(f, Path.GetFileName(f)); });
        };

        Controls.Add(_canvas);
        Controls.Add(_bar);

        var fpsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        fpsTimer.Tick += (_, _) => { if (DateTime.Now > _infoUntil) _lblStatus.Text = $"{_frames} fps  •  {s.Width}×{s.Height}  •  cifrado AES-256"; _frames = 0; };
        fpsTimer.Start();
    }

    static Button BarButton(string text, Color c)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = c,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = true,
            Height = 30,
            Margin = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void OpenFiles()
    {
        if (_filesForm == null || _filesForm.IsDisposed)
        {
            _filesForm = new FilesForm(_ch, _files, _remoteName);
            _filesForm.Show(this);
        }
        else _filesForm.Activate();
    }

    void BeginInvokeSafe(Action a)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    void ToggleFullScreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _prevState;
        }
        else
        {
            _prevState = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Application.AddMessageFilter(this);
        _canvas.Focus();
        _ = Task.Run(ReceiveLoop);
    }

    async Task ReceiveLoop()
    {
        string reason = "Se perdió la conexión con el equipo remoto.";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (t, p) = await _ch.ReceiveAsync(_cts.Token);
                switch (t)
                {
                    case Msg.ScreenInfo:
                        _canvas.SetRemoteSize(BinaryPrimitives.ReadInt32LittleEndian(p), BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(4)));
                        break;
                    case Msg.Tile:
                        _canvas.DrawTile(BinaryPrimitives.ReadInt32LittleEndian(p), BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(4)), p, 8);
                        break;
                    case Msg.FrameEnd:
                        _frames++;
                        BeginInvoke(() => _canvas.Invalidate());
                        _ch.Post(Msg.FrameAck);
                        break;
                    case Msg.Bye:
                        reason = Encoding.UTF8.GetString(p);
                        return;
                    case Msg.DirListing:
                        var listing = FileTransfer.ParseListing(p);
                        BeginInvokeSafe(() => { if (_filesForm != null && !_filesForm.IsDisposed) _filesForm.ShowListing(listing); });
                        break;
                    case Msg.Error:
                        var err = Encoding.UTF8.GetString(p);
                        BeginInvokeSafe(() => { if (_filesForm != null && !_filesForm.IsDisposed) _filesForm.ShowError(err); else _lblStatus.Text = err; });
                        break;
                    default:
                        _files.Handle(t, p);
                        break;
                }
            }
        }
        catch { }
        finally
        {
            if (!_closing && !IsDisposed)
                BeginInvoke(() =>
                {
                    if (_closing) return;
                    MessageBox.Show(this, reason, "Sesión terminada", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                });
        }
    }

    // --------- mouse ---------
    void SendMouseMove(Point p)
    {
        var (nx, ny) = _canvas.ToRemote(p);
        var b = new byte[8];
        BitConverter.TryWriteBytes(b.AsSpan(0), nx);
        BitConverter.TryWriteBytes(b.AsSpan(4), ny);
        _ch.Post(Msg.MouseMove, b);
    }

    void SendButton(MouseButtons mb, bool down)
    {
        byte btn = mb switch { MouseButtons.Right => 1, MouseButtons.Middle => 2, _ => 0 };
        _ch.Post(Msg.MouseButton, new[] { btn, (byte)(down ? 1 : 0) });
    }

    // --------- teclado: se capturan todas las teclas (incluido Alt+Tab dentro de la app, Alt+F4, etc.) ---------
    public bool PreFilterMessage(ref Message m)
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        if (ActiveForm != this) return false;
        if (m.Msg != WM_KEYDOWN && m.Msg != WM_KEYUP && m.Msg != WM_SYSKEYDOWN && m.Msg != WM_SYSKEYUP) return false;

        ushort vk = (ushort)((long)m.WParam & 0xFFFF);
        bool down = m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN;
        bool ext = (((long)m.LParam >> 24) & 1) == 1;

        // F11 local = pantalla completa
        if (vk == (ushort)Keys.F11) { if (down) ToggleFullScreen(); return true; }

        _ch.Post(Msg.Key, new byte[] { (byte)(vk & 0xFF), (byte)(vk >> 8), (byte)((down ? 1 : 0) | (ext ? 2 : 0)) });
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        Application.RemoveMessageFilter(this);
        try { _ch.Post(Msg.Bye, Encoding.UTF8.GetBytes("El técnico cerró la sesión.")); } catch { }
        _cts.Cancel();
        Task.Delay(150).ContinueWith(_ => { _ch.Dispose(); _files.Dispose(); });
        base.OnFormClosing(e);
    }
}

/// <summary>Lienzo que dibuja la pantalla remota ajustada a la ventana.</summary>
public class RemoteCanvas : Control
{
    readonly object _lock = new();
    Bitmap _bmp;

    public RemoteCanvas(int w, int h)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Black;
        SetRemoteSize(w, h);
    }

    public void SetRemoteSize(int w, int h)
    {
        lock (_lock)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        }
    }

    public void DrawTile(int x, int y, byte[] data, int offset)
    {
        using var ms = new MemoryStream(data, offset, data.Length - offset);
        using var tile = new Bitmap(ms);
        lock (_lock)
        {
            using var g = Graphics.FromImage(_bmp);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(tile, new Rectangle(x, y, tile.Width, tile.Height));
        }
    }

    Rectangle ImageRect()
    {
        int bw, bh;
        lock (_lock) { bw = _bmp.Width; bh = _bmp.Height; }
        float scale = Math.Min((float)ClientSize.Width / bw, (float)ClientSize.Height / bh);
        int w = (int)(bw * scale), h = (int)(bh * scale);
        return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }

    public (float, float) ToRemote(Point p)
    {
        var r = ImageRect();
        if (r.Width <= 0 || r.Height <= 0) return (0, 0);
        return (Math.Clamp((p.X - r.X) / (float)r.Width, 0f, 1f), Math.Clamp((p.Y - r.Y) / (float)r.Height, 0f, 1f));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        var r = ImageRect();
        lock (_lock)
        {
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(_bmp, r);
        }
    }

    protected override bool IsInputKey(Keys keyData) => true;
}
