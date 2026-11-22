using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CompuHelpRemoto;

/// <summary>
/// Lado "equipo a controlar": escucha conexiones directas (IP) y, si hay servidor de enlace,
/// se registra con su ID para recibir conexiones desde internet.
/// </summary>
public sealed class HostService
{
    readonly MainForm _ui;
    readonly AppConfig _cfg;
    CancellationTokenSource _cts;
    int _busy;
    int _failures;
    DateTime _lockedUntil = DateTime.MinValue;

    string _password;
    byte[] _tempBaseKey;

    /// <summary>Contraseña temporal (cambia cada vez que se abre el programa).</summary>
    public string Password
    {
        get => _password;
        set { _password = value; _tempBaseKey = SecureChannel.BaseKey(value, _cfg.KeySaltBytes); }
    }
    public event Action<string, bool> StatusChanged;   // texto, ok
    public event Action<string> SessionStarted;
    public event Action SessionEnded;

    public HostService(MainForm ui, AppConfig cfg)
    {
        _ui = ui; _cfg = cfg;
        Password = AppConfig.NewPassword();
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cts.Token));
        if (!string.IsNullOrWhiteSpace(_cfg.RelayServer))
            _ = Task.Run(() => RelayLoop(_cts.Token));
        else
            Status($"Listo para conectar en red local (puerto {_cfg.Port})", true);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    void Status(string t, bool ok) => _ui.BeginInvoke(() => StatusChanged?.Invoke(t, ok));

    // ---------------- conexiones directas ----------------
    async Task ListenLoop(CancellationToken ct)
    {
        TcpListener listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, _cfg.Port);
            listener.Start();
            using var reg = ct.Register(() => listener.Stop());
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                SecureChannel.EnableKeepAlive(client.Client);
                _ = Task.Run(() => HandleIncoming(client.GetStream(), ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Status($"No se pudo abrir el puerto {_cfg.Port}: {ex.Message}", false);
        }
        finally { try { listener?.Stop(); } catch { } }
    }

    // ---------------- servidor de enlace (internet) ----------------
    async Task RelayLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client = null;
            try
            {
                var (host, port) = ParseHostPort(_cfg.RelayServer, 5651);
                client = new TcpClient();
                await client.ConnectAsync(host, port, ct);
                SecureChannel.EnableKeepAlive(client.Client);
                var s = client.GetStream();
                var alias = AppConfig.ValidAlias(_cfg.Alias) ? " " + _cfg.Alias : "";
                await SecureChannel.WriteLineAsync(s, $"REG {_cfg.Id} {Convert.ToBase64String(_cfg.PublicKey)}{alias}", ct);
                var nonce = await SecureChannel.ReadLineAsync(s, ct);          // "NONCE <base64>"
                if (!nonce.StartsWith("NONCE ")) throw new IOException("El servidor rechazó el registro: " + nonce);
                var sig = _cfg.Sign(Convert.FromBase64String(nonce[6..]));
                await SecureChannel.WriteLineAsync(s, "SIG " + Convert.ToBase64String(sig), ct);
                var resp = await SecureChannel.ReadLineAsync(s, ct);
                if (!resp.StartsWith("WAIT")) throw new IOException("El servidor rechazó el registro: " + resp);
                if (resp.Contains("ALIAS_TAKEN"))
                    Status($"Listo por ID, pero el usuario «{_cfg.Alias}» ya lo usa otro equipo. Elige otro en ⚙.", false);
                else
                    Status("Listo para conectar (conexión segura)", true);

                var paired = await SecureChannel.ReadLineAsync(s, ct);   // espera a un técnico
                if (paired != "PAIRED") throw new IOException("Respuesta inesperada del servidor.");
                var stream = s; client = null;                             // la sesión se queda con el socket
                _ = Task.Run(() => HandleIncoming(stream, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Status("Sin conexión al servidor: " + ex.Message + " (reintentando…)", false);
                try { await Task.Delay(5000, ct); } catch { break; }
            }
            finally { client?.Dispose(); }
        }
    }

    public static (string host, int port) ParseHostPort(string s, int defPort)
    {
        s = s.Trim();
        int i = s.LastIndexOf(':');
        if (i > 0 && int.TryParse(s[(i + 1)..], out int p)) return (s[..i], p);
        return (s, defPort);
    }

    // ---------------- sesión ----------------
    async Task HandleIncoming(Stream stream, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { stream.Dispose(); return; }
        SecureChannel ch = null;
        try
        {
            if (DateTime.Now < _lockedUntil) { stream.Dispose(); return; }

            var sessionSalt = RandomNumberGenerator.GetBytes(16);
            await stream.WriteAsync(SecureChannel.Magic.Concat(_cfg.KeySaltBytes).Concat(sessionSalt).ToArray(), ct);

            // claves aceptadas: [0] contraseña temporal, [1] contraseña fija (usuario y contraseña)
            var keys = new List<byte[]>
            {
                SecureChannel.SessionKey(_tempBaseKey, sessionSalt),
                _cfg.FixedEnabled ? SecureChannel.SessionKey(Convert.FromBase64String(_cfg.FixedKey), sessionSalt) : null,
            };

            string viewer;
            bool usedFixed;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var (idx, plain) = await SecureChannel.ReadFirstAsync(stream, keys, timeout.Token);
                    if ((Msg)plain[0] != Msg.Hello) throw new CryptographicException();
                    viewer = Encoding.UTF8.GetString(plain, 1, plain.Length - 1);
                    usedFixed = idx == 1;
                    ch = new SecureChannel(stream, keys[idx], isHost: true, recvStart: 1);
                }
                catch (CryptographicException)
                {
                    // contraseña incorrecta
                    if (++_failures >= 5) { _lockedUntil = DateTime.Now.AddMinutes(1); _failures = 0; }
                    await Task.Delay(2000);
                    try { await stream.WriteAsync(new byte[4]); } catch { }
                    return;
                }
            }
            _failures = 0;

            // con la contraseña fija no se pregunta (acceso desatendido, como en TeamViewer)
            if (_cfg.AskBeforeAccept && !usedFixed)
            {
                bool ok = (bool)_ui.Invoke(() =>
                {
                    using var top = new Form { TopMost = true };
                    return MessageBox.Show(top, $"«{viewer}» quiere conectarse y controlar este equipo.\n\n¿Permitir el acceso?",
                        "CompuHelp – Solicitud de conexión", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                });
                if (!ok)
                {
                    await ch.SendAsync(Msg.Bye, Encoding.UTF8.GetBytes("El usuario rechazó la conexión."), ct);
                    return;
                }
            }

            await RunSession(ch, viewer, ct);
        }
        catch { }
        finally
        {
            ch?.Dispose();
            stream.Dispose();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    async Task RunSession(SecureChannel ch, string viewer, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;
        SessionBanner banner = null;
        using var files = new FileTransfer(ch, allowRootedPaths: true);
        _ui.Invoke(() =>
        {
            banner = new SessionBanner(viewer, () => cts.Cancel(),
                path => _ = Task.Run(() => files.SendFileAsync(path, Path.GetFileName(path), ct)));
            files.Progress += (text, pct) => _ui.BeginInvoke(() => banner.ShowInfo(text));
            banner.Show();
            SessionStarted?.Invoke(viewer);
        });

        try
        {
            using var cap = new ScreenCapturer(_cfg.Quality);
            await ch.SendAsync(Msg.Welcome, Encoding.UTF8.GetBytes(Environment.MachineName), ct);
            await ch.SendAsync(Msg.ScreenInfo, Size(cap.Bounds), ct);

            var acks = new SemaphoreSlim(2);

            var recv = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var (t, p) = await ch.ReceiveAsync(ct);
                    switch (t)
                    {
                        case Msg.FrameAck: acks.Release(); break;
                        case Msg.MouseMove: InputInjector.MoveTo(BitConverter.ToSingle(p, 0), BitConverter.ToSingle(p, 4)); break;
                        case Msg.MouseButton: InputInjector.Button(p[0], p[1] == 1); break;
                        case Msg.MouseWheel: InputInjector.Wheel(BitConverter.ToInt32(p, 0)); break;
                        case Msg.Key: InputInjector.Key(BitConverter.ToUInt16(p, 0), (p[2] & 1) != 0, (p[2] & 2) != 0); break;
                        case Msg.Bye: return;
                        case Msg.ListDir:
                        {
                            var path = Encoding.UTF8.GetString(p);
                            try { await ch.SendAsync(Msg.DirListing, FileTransfer.ListDirectory(path), ct); }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            { await ch.SendAsync(Msg.Error, Encoding.UTF8.GetBytes($"No se pudo abrir {path}: {ex.Message}"), ct); }
                            break;
                        }
                        case Msg.GetFile:
                        {
                            var path = Encoding.UTF8.GetString(p);
                            _ = Task.Run(() => files.SendFileAsync(path, Path.GetFileName(path), ct));
                            break;
                        }
                        default: files.Handle(t, p); break;
                    }
                }
            }, ct);

            var send = Task.Run(async () =>
            {
                bool full = true;
                while (!ct.IsCancellationRequested)
                {
                    await acks.WaitAsync(ct);
                    var start = Environment.TickCount64;
                    if (cap.CheckResolutionChanged())
                    {
                        await ch.SendAsync(Msg.ScreenInfo, Size(cap.Bounds), ct);
                        full = true;
                    }
                    foreach (var (x, y, jpg) in cap.Capture(full))
                    {
                        var payload = new byte[8 + jpg.Length];
                        BinaryPrimitives.WriteInt32LittleEndian(payload, x);
                        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), y);
                        Buffer.BlockCopy(jpg, 0, payload, 8, jpg.Length);
                        await ch.SendAsync(Msg.Tile, payload, ct);
                    }
                    full = false;
                    await ch.SendAsync(Msg.FrameEnd, null, ct);
                    var wait = 66 - (int)(Environment.TickCount64 - start);   // ~15 cuadros por segundo
                    if (wait > 0) await Task.Delay(wait, ct);
                }
            }, ct);

            await Task.WhenAny(recv, send);
            cts.Cancel();
            try { await Task.WhenAll(recv, send); } catch { }   // esperar a que paren antes de liberar la captura
            try { await ch.SendAsync(Msg.Bye, Encoding.UTF8.GetBytes("Sesión finalizada por el equipo remoto."), CancellationToken.None); } catch { }
        }
        catch { }
        finally
        {
            cts.Cancel();
            _ui.BeginInvoke(() =>
            {
                banner?.Close();
                SessionEnded?.Invoke();
            });
        }
    }

    static byte[] Size(Rectangle r)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(b, r.Width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), r.Height);
        return b;
    }
}
