// CompuHelp Relay – servidor de enlace para conectar equipos por ID a través de internet.
// Funciona en Windows o Linux (VPS). Uso:  CompuHelpRelay [puerto]   (por defecto 5651)
//
// Protocolo (texto, una línea por comando):
//   Equipo:  REG <id> <clavePublicaBase64> [usuario]  ->  NONCE <base64>  ->  SIG <firmaBase64>  ->  WAIT [ALIAS_TAKEN] ... PAIRED
//   Técnico: CON <id o usuario>                       ->  PAIRED | NOTFOUND
// Cada usuario queda ligado al primer equipo (ID) que lo registra; se guarda en usuarios.json.
// Después de PAIRED el servidor solo reenvía bytes; el contenido va cifrado de extremo a extremo
// con la contraseña de la sesión, así que el servidor no puede ver la pantalla ni las teclas.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5651;
var waiting = new ConcurrentDictionary<string, TcpClient>();
var aliasFile = Path.Combine(AppContext.BaseDirectory, "usuarios.json");
var aliases = new ConcurrentDictionary<string, string>(
    File.Exists(aliasFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(aliasFile)) : new());
var aliasLock = new object();
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Log($"CompuHelp Relay escuchando en el puerto {port}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Handle(client));
}

async Task Handle(TcpClient client)
{
    bool keep = false;
    try
    {
        KeepAlive(client.Client);
        var s = client.GetStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var line = await ReadLine(s, cts.Token);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if ((parts.Length == 3 || parts.Length == 4) && parts[0] == "REG")
        {
            string id = parts[1];
            byte[] pub = Convert.FromBase64String(parts[2]);
            if (IdFromPublicKey(pub) != id) { await WriteLine(s, "BADID"); return; }

            var nonce = RandomNumberGenerator.GetBytes(32);
            await WriteLine(s, "NONCE " + Convert.ToBase64String(nonce));
            var sigLine = await ReadLine(s, cts.Token);
            if (!sigLine.StartsWith("SIG ")) return;
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(pub, out _);
            if (!ec.VerifyData(nonce, Convert.FromBase64String(sigLine[4..]), HashAlgorithmName.SHA256))
            {
                await WriteLine(s, "BADSIG");
                return;
            }

            string extra = "";
            if (parts.Length == 4)
            {
                var alias = parts[3].ToLowerInvariant();
                bool ok = false;
                if (Regex.IsMatch(alias, "^[a-z0-9][a-z0-9._-]{2,31}$") && !Regex.IsMatch(alias, "^[0-9]+$"))
                    lock (aliasLock)
                    {
                        if (!aliases.TryGetValue(alias, out var owner)) { aliases[alias] = id; SaveAliases(); ok = true; Log($"Usuario nuevo: {alias} -> {id}"); }
                        else ok = owner == id;
                    }
                if (!ok) extra = " ALIAS_TAKEN";
            }
            await WriteLine(s, "WAIT" + extra);
            if (waiting.TryGetValue(id, out var old) && old != client) old.Dispose();
            waiting[id] = client;
            keep = true;
            Log($"Equipo en línea: {id}");
        }
        else if (parts.Length == 2 && parts[0] == "CON")
        {
            string id = parts[1].ToLowerInvariant();
            if (!Regex.IsMatch(id, "^[0-9]{9}$") && aliases.TryGetValue(id, out var byAlias)) id = byAlias;
            if (!waiting.TryRemove(id, out var host))
            {
                await WriteLine(s, "NOTFOUND");
                return;
            }
            try { await WriteLine(host.GetStream(), "PAIRED"); }
            catch { host.Dispose(); await WriteLine(s, "NOTFOUND"); return; }
            await WriteLine(s, "PAIRED");
            Log($"Sesión iniciada con {id} desde {client.Client.RemoteEndPoint}");
            keep = true;
            _ = Pipe(host, client, id);
        }
    }
    catch { }
    finally { if (!keep) client.Dispose(); }
}

async Task Pipe(TcpClient a, TcpClient b, string id)
{
    var sa = a.GetStream(); var sb = b.GetStream();
    try { await Task.WhenAny(sa.CopyToAsync(sb), sb.CopyToAsync(sa)); }
    catch { }
    finally
    {
        a.Dispose(); b.Dispose();
        Log($"Sesión terminada con {id}");
    }
}

void SaveAliases()
{
    try { File.WriteAllText(aliasFile, JsonSerializer.Serialize(new Dictionary<string, string>(aliases), new JsonSerializerOptions { WriteIndented = true })); }
    catch (Exception ex) { Log("No se pudo guardar usuarios.json: " + ex.Message); }
}

static string IdFromPublicKey(byte[] pub)
{
    var h = SHA256.HashData(pub);
    ulong v = BitConverter.ToUInt64(h, 0);
    return (100_000_000UL + v % 900_000_000UL).ToString();
}

static async Task<string> ReadLine(Stream s, CancellationToken ct)
{
    var sb = new StringBuilder();
    var b = new byte[1];
    while (sb.Length < 1024)
    {
        if (await s.ReadAsync(b, ct) == 0) throw new IOException();
        if (b[0] == '\n') return sb.ToString().Trim();
        sb.Append((char)b[0]);
    }
    throw new IOException();
}

static Task WriteLine(Stream s, string line) => s.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).AsTask();

static void KeepAlive(Socket sock)
{
    try
    {
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
    }
    catch { }
    sock.NoDelay = true;
}

static void Log(string m) => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {m}");
