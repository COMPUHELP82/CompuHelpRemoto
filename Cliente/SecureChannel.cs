using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace CompuHelpRemoto;

public enum Msg : byte
{
    Hello = 1,       // visor -> equipo: nombre del técnico
    Welcome = 2,     // equipo -> visor: nombre del equipo
    ScreenInfo = 3,  // ancho, alto
    Tile = 4,        // x, y, jpeg
    FrameEnd = 5,
    FrameAck = 6,
    MouseMove = 10,  // float x, float y (0..1)
    MouseButton = 11,// botón (0 izq, 1 der, 2 medio), down(1/0)
    MouseWheel = 12, // int delta
    Key = 13,        // ushort vk, flags (bit0 down, bit1 extendida)
    Bye = 20,        // texto con motivo
    FileStart = 30,  // int id, long tamaño, destino (utf8)
    FileChunk = 31,  // int id, datos
    FileEnd = 32,    // int id
    ListDir = 33,    // ruta (utf8) – "" = unidades y carpetas comunes
    DirListing = 34, // JSON
    GetFile = 35,    // ruta (utf8)
    Error = 36,      // texto
}

public class AuthFailedException : Exception
{
    public AuthFailedException() : base("Contraseña incorrecta.") { }
}

/// <summary>
/// Canal cifrado AES-256-GCM. La clave se deriva de la contraseña (PBKDF2) con una sal aleatoria
/// que envía el equipo controlado. Si la contraseña no coincide, el primer mensaje no se puede descifrar.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public static readonly byte[] Magic = "CHR2"u8.ToArray();
    const int MaxMessage = 32 * 1024 * 1024;

    readonly Stream _s;
    readonly AesGcm _aes;
    readonly byte _sendDir, _recvDir;
    long _sendCtr, _recvCtr;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly Channel<(Msg, byte[])> _queue = Channel.CreateUnbounded<(Msg, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    bool _disposed;

    public SecureChannel(Stream s, byte[] key, bool isHost, long recvStart = 0)
    {
        _recvCtr = recvStart;
        _s = s;
        _aes = new AesGcm(key, 16);
        _sendDir = isHost ? (byte)1 : (byte)2;
        _recvDir = isHost ? (byte)2 : (byte)1;
        _ = Task.Run(PumpAsync);
    }

    /// <summary>Clave base a partir de la contraseña (lenta a propósito, contra ataques de fuerza bruta).</summary>
    public static byte[] BaseKey(string password, byte[] keySalt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Trim()), keySalt, 100_000, HashAlgorithmName.SHA256, 32);

    /// <summary>Clave única de cada sesión.</summary>
    public static byte[] SessionKey(byte[] baseKey, byte[] sessionSalt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, baseKey, 32, sessionSalt, "CompuHelp v2"u8.ToArray());

    /// <summary>
    /// Lado equipo: lee el primer mensaje del técnico y prueba con cada clave aceptada
    /// (contraseña temporal o contraseña fija). Devuelve el índice de la clave que funcionó.
    /// </summary>
    public static async Task<(int index, byte[] plain)> ReadFirstAsync(Stream s, IList<byte[]> keys, CancellationToken ct)
    {
        var hdr = new byte[4];
        await s.ReadExactlyAsync(hdr, ct);
        int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
        if (len < 17 || len > 64 * 1024) throw new CryptographicException();
        var buf = new byte[len];
        await s.ReadExactlyAsync(buf, ct);
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] == null) continue;
            try
            {
                using var aes = new AesGcm(keys[i], 16);
                var plain = new byte[len - 16];
                aes.Decrypt(Nonce(2, 0), buf.AsSpan(0, len - 16), buf.AsSpan(len - 16), plain);
                return (i, plain);
            }
            catch (CryptographicException) { }
        }
        throw new CryptographicException();
    }

    static byte[] Nonce(byte dir, long ctr)
    {
        var n = new byte[12];
        n[0] = dir;
        BinaryPrimitives.WriteInt64LittleEndian(n.AsSpan(4), ctr);
        return n;
    }

    public async Task SendAsync(Msg type, byte[] payload, CancellationToken ct = default)
    {
        payload ??= Array.Empty<byte>();
        var plain = new byte[1 + payload.Length];
        plain[0] = (byte)type;
        Buffer.BlockCopy(payload, 0, plain, 1, payload.Length);
        var packet = new byte[4 + plain.Length + 16];

        await _writeLock.WaitAsync(ct);
        try
        {
            var nonce = Nonce(_sendDir, _sendCtr++);
            _aes.Encrypt(nonce, plain, packet.AsSpan(4, plain.Length), packet.AsSpan(4 + plain.Length, 16));
            BinaryPrimitives.WriteInt32LittleEndian(packet, plain.Length + 16);
            await _s.WriteAsync(packet, ct);
            await _s.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Envío en orden sin bloquear (para mouse y teclado desde la interfaz).</summary>
    public void Post(Msg type, byte[] payload = null)
    {
        if (!_disposed) _queue.Writer.TryWrite((type, payload));
    }

    async Task PumpAsync()
    {
        try
        {
            await foreach (var (t, p) in _queue.Reader.ReadAllAsync(_cts.Token))
                await SendAsync(t, p, _cts.Token);
        }
        catch { }
    }

    public async Task<(Msg type, byte[] payload)> ReceiveAsync(CancellationToken ct = default)
    {
        var hdr = new byte[4];
        await _s.ReadExactlyAsync(hdr, ct);
        int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
        if (len == 0) throw new AuthFailedException();
        if (len < 17 || len > MaxMessage) throw new IOException("Mensaje inválido.");
        var buf = new byte[len];
        await _s.ReadExactlyAsync(buf, ct);
        var plain = new byte[len - 16];
        _aes.Decrypt(Nonce(_recvDir, _recvCtr++), buf.AsSpan(0, len - 16), buf.AsSpan(len - 16), plain);
        return ((Msg)plain[0], plain[1..]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _s.Dispose(); } catch { }
        _aes.Dispose();
    }

    // ---------- utilidades de protocolo ----------

    public static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (sb.Length < 1024)
        {
            int n = await s.ReadAsync(b, ct);
            if (n == 0) throw new IOException("Conexión cerrada.");
            if (b[0] == '\n') return sb.ToString().Trim();
            sb.Append((char)b[0]);
        }
        throw new IOException("Línea demasiado larga.");
    }

    public static Task WriteLineAsync(Stream s, string line, CancellationToken ct) =>
        s.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct).AsTask();

    public static void EnableKeepAlive(System.Net.Sockets.Socket sock)
    {
        try
        {
            sock.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
            sock.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 30);
            sock.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 10);
        }
        catch { }
        sock.NoDelay = true;
    }
}
