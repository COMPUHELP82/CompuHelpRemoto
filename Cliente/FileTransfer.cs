using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CompuHelpRemoto;

/// <summary>Envío y recepción de archivos por el canal cifrado (se usa en los dos lados).</summary>
public sealed class FileTransfer : IDisposable
{
    const int ChunkSize = 64 * 1024;

    class Incoming { public FileStream Fs; public string Path; public long Size, Done, LastReport; }

    readonly SecureChannel _ch;
    readonly bool _allowRootedPaths;
    readonly ConcurrentDictionary<int, Incoming> _incoming = new();
    int _nextId;

    public string DefaultFolder { get; }
    /// <summary>(texto, porcentaje 0..100 o -1)</summary>
    public event Action<string, int> Progress;
    public event Action<string> Received;

    /// <param name="allowRootedPaths">
    /// El equipo controlado acepta rutas completas (el técnico elige la carpeta).
    /// El técnico NO las acepta: lo que le envían siempre cae en su carpeta de descargas.
    /// </param>
    public FileTransfer(SecureChannel ch, bool allowRootedPaths)
    {
        _ch = ch;
        _allowRootedPaths = allowRootedPaths;
        DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "CompuHelp");
    }

    // ---------------- enviar ----------------
    public async Task SendFileAsync(string localPath, string target, CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _nextId);
        string name = Path.GetFileName(localPath);
        try
        {
            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ChunkSize, true);
            long size = fs.Length;
            var tgt = Encoding.UTF8.GetBytes(target ?? name);
            var start = new byte[12 + tgt.Length];
            BinaryPrimitives.WriteInt32LittleEndian(start, id);
            BinaryPrimitives.WriteInt64LittleEndian(start.AsSpan(4), size);
            tgt.CopyTo(start, 12);
            await _ch.SendAsync(Msg.FileStart, start, ct);

            var buf = new byte[4 + ChunkSize];
            BinaryPrimitives.WriteInt32LittleEndian(buf, id);
            long done = 0, last = 0;
            int n;
            while ((n = await fs.ReadAsync(buf.AsMemory(4, ChunkSize), ct)) > 0)
            {
                await _ch.SendAsync(Msg.FileChunk, buf[..(4 + n)], ct);
                done += n;
                if (done - last > 1024 * 1024 || done == size)
                {
                    last = done;
                    Progress?.Invoke($"Enviando {name}…", size == 0 ? 100 : (int)(done * 100 / size));
                }
            }
            await _ch.SendAsync(Msg.FileEnd, BitConverter.GetBytes(id), ct);
            Progress?.Invoke($"Enviado: {name}", 100);
        }
        catch (Exception ex)
        {
            Progress?.Invoke($"Error al enviar {name}: {ex.Message}", -1);
        }
    }

    // ---------------- recibir ----------------
    /// <summary>Devuelve true si el mensaje era de archivos.</summary>
    public bool Handle(Msg t, byte[] p)
    {
        switch (t)
        {
            case Msg.FileStart:
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(p);
                long size = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(4));
                string target = Encoding.UTF8.GetString(p, 12, p.Length - 12);
                try
                {
                    string path = _allowRootedPaths && Path.IsPathRooted(target)
                        ? target
                        : Path.Combine(DefaultFolder, SafeName(Path.GetFileName(target)));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    path = UniquePath(path);
                    _incoming[id] = new Incoming { Fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write), Path = path, Size = size };
                    Progress?.Invoke($"Recibiendo {Path.GetFileName(path)}…", 0);
                }
                catch (Exception ex)
                {
                    Progress?.Invoke($"No se pudo guardar {target}: {ex.Message}", -1);
                }
                return true;
            }
            case Msg.FileChunk:
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(p);
                if (_incoming.TryGetValue(id, out var f))
                {
                    try
                    {
                        f.Fs.Write(p, 4, p.Length - 4);
                        f.Done += p.Length - 4;
                        if (f.Done - f.LastReport > 1024 * 1024)
                        {
                            f.LastReport = f.Done;
                            Progress?.Invoke($"Recibiendo {Path.GetFileName(f.Path)}…", f.Size == 0 ? 100 : (int)(f.Done * 100 / f.Size));
                        }
                    }
                    catch (Exception ex)
                    {
                        _incoming.TryRemove(id, out _);
                        f.Fs.Dispose();
                        Progress?.Invoke($"Error al guardar {Path.GetFileName(f.Path)}: {ex.Message}", -1);
                    }
                }
                return true;
            }
            case Msg.FileEnd:
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(p);
                if (_incoming.TryRemove(id, out var f))
                {
                    f.Fs.Dispose();
                    Progress?.Invoke($"Recibido: {Path.GetFileName(f.Path)}", 100);
                    Received?.Invoke(f.Path);
                }
                return true;
            }
        }
        return false;
    }

    static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "archivo" : n;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public void Dispose()
    {
        foreach (var f in _incoming.Values)
        {
            try { f.Fs.Dispose(); File.Delete(f.Path); } catch { }   // borrar archivos incompletos
        }
        _incoming.Clear();
    }

    // ---------------- explorador remoto (lado equipo) ----------------
    public record Item(string Name, string FullPath, bool IsDir, long Size, DateTime Modified);
    public record Listing(string Path, string Parent, List<Item> Items);

    public static byte[] ListDirectory(string path)
    {
        var items = new List<Item>();
        string parent = null;
        if (string.IsNullOrEmpty(path))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            void Add(string label, string p) { if (Directory.Exists(p)) items.Add(new Item(label, p, true, 0, DateTime.MinValue)); }
            Add("Escritorio", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Add("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add("Descargas", Path.Combine(home, "Downloads"));
            Add("Imágenes", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                items.Add(new Item($"Disco {d.Name.TrimEnd('\\')} {d.VolumeLabel}".Trim(), d.RootDirectory.FullName, true, 0, DateTime.MinValue));
        }
        else
        {
            var di = new DirectoryInfo(path);
            parent = di.Parent?.FullName ?? "";
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            foreach (var d in di.EnumerateDirectories("*", opts).OrderBy(d => d.Name))
                items.Add(new Item(d.Name, d.FullName, true, 0, d.LastWriteTime));
            foreach (var f in di.EnumerateFiles("*", opts).OrderBy(f => f.Name))
                items.Add(new Item(f.Name, f.FullName, false, f.Length, f.LastWriteTime));
        }
        return JsonSerializer.SerializeToUtf8Bytes(new Listing(path ?? "", parent, items));
    }

    public static Listing ParseListing(byte[] p) => JsonSerializer.Deserialize<Listing>(p);
}
