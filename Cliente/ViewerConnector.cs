using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace CompuHelpRemoto;

/// <summary>Lado "técnico": se conecta por ID (vía servidor de enlace) o por IP directa.</summary>
public static class ViewerConnector
{
    public record Session(SecureChannel Channel, string RemoteName, int Width, int Height);

    public static async Task<Session> ConnectAsync(AppConfig cfg, string target, string password, CancellationToken ct)
    {
        target = target.Trim();
        string digits = new string(target.Where(char.IsDigit).ToArray());
        bool isId = digits.Length == 9 && target.All(c => char.IsDigit(c) || c == ' ');
        // usuario fijo (ej. "oficina-juan"): sin puntos ni dos puntos, así que no es una IP
        bool isAlias = !isId && !target.Contains('.') && !target.Contains(':') && AppConfig.ValidAlias(target.ToLowerInvariant());

        var client = new TcpClient();
        try
        {
            Stream s;
            if (isId || isAlias)
            {
                if (string.IsNullOrWhiteSpace(cfg.RelayServer))
                    throw new Exception("Para conectar por ID o usuario configura el servidor de enlace (⚙). En red local puedes escribir la IP del equipo.");
                var (h, p) = HostService.ParseHostPort(cfg.RelayServer, 5651);
                await client.ConnectAsync(h, p, ct);
                SecureChannel.EnableKeepAlive(client.Client);
                s = client.GetStream();
                await SecureChannel.WriteLineAsync(s, "CON " + (isId ? digits : target.ToLowerInvariant()), ct);
                var resp = await SecureChannel.ReadLineAsync(s, ct);
                if (resp == "NOTFOUND") throw new Exception(isId ? "El equipo con ese ID no está en línea." : $"No hay ningún equipo en línea con el usuario «{target}».");
                if (resp != "PAIRED") throw new Exception("Respuesta inesperada del servidor: " + resp);
            }
            else
            {
                var (h, p) = HostService.ParseHostPort(target, cfg.Port);
                await client.ConnectAsync(h, p, ct);
                SecureChannel.EnableKeepAlive(client.Client);
                s = client.GetStream();
            }

            var hello = new byte[36];
            try { await s.ReadExactlyAsync(hello, ct); }
            catch (EndOfStreamException) { throw new Exception("El equipo remoto está ocupado o rechazó la conexión."); }
            if (!hello.AsSpan(0, 4).SequenceEqual(SecureChannel.Magic))
                throw new Exception("El equipo remoto no es CompuHelp Remoto.");

            var key = SecureChannel.SessionKey(SecureChannel.BaseKey(password, hello[4..20]), hello[20..36]);
            var ch = new SecureChannel(s, key, isHost: false);
            await ch.SendAsync(Msg.Hello, Encoding.UTF8.GetBytes($"{Environment.UserName} ({Environment.MachineName})"), ct);

            string name = "";
            while (true)
            {
                (Msg t, byte[] p) msg;
                try { msg = await ch.ReceiveAsync(ct); }
                catch (EndOfStreamException) { ch.Dispose(); throw new Exception("La conexión se cerró."); }
                catch (AuthFailedException) { ch.Dispose(); throw; }

                if (msg.t == Msg.Bye) { ch.Dispose(); throw new Exception(Encoding.UTF8.GetString(msg.p)); }
                if (msg.t == Msg.Welcome) name = Encoding.UTF8.GetString(msg.p);
                if (msg.t == Msg.ScreenInfo)
                    return new Session(ch, name,
                        BinaryPrimitives.ReadInt32LittleEndian(msg.p),
                        BinaryPrimitives.ReadInt32LittleEndian(msg.p.AsSpan(4)));
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
