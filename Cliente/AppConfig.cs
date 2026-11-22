using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompuHelpRemoto;

/// <summary>Configuración guardada en %AppData%\CompuHelpRemoto\config.json</summary>
public class AppConfig
{
    public string Secret { get; set; } = "";
    public string RelayServer { get; set; } = "";   // ej: "midominio.com:5651" (vacío = solo red local / IP)
    public int Port { get; set; } = 5650;           // puerto para conexiones directas
    public bool AskBeforeAccept { get; set; } = true;
    public int Quality { get; set; } = 60;           // calidad JPEG 30..90
    public string KeySalt { get; set; } = "";
    public string Alias { get; set; } = "";          // usuario fijo, ej. "oficina-juan"
    public string FixedKey { get; set; } = "";       // clave derivada de la contraseña fija (no se guarda la contraseña)

    [JsonIgnore] public bool FixedEnabled => !string.IsNullOrEmpty(FixedKey);
    [JsonIgnore] public byte[] KeySaltBytes => Convert.FromBase64String(KeySalt);

    public void SetFixedPassword(string pwd) =>
        FixedKey = string.IsNullOrWhiteSpace(pwd) ? "" : Convert.ToBase64String(SecureChannel.BaseKey(pwd, KeySaltBytes));

    public static bool ValidAlias(string a) =>
        System.Text.RegularExpressions.Regex.IsMatch(a ?? "", "^[a-z0-9][a-z0-9._-]{2,31}$");

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CompuHelpRemoto");
    static string FilePath => Path.Combine(Folder, "config.json");

    [JsonIgnore] public string Id => IdFromPublicKey(PublicKey);

    /// <summary>Clave pública del equipo (la privada se guarda en Secret y nunca sale de la PC).</summary>
    [JsonIgnore] public byte[] PublicKey
    {
        get { using var e = LoadKey(); return e.ExportSubjectPublicKeyInfo(); }
    }

    public byte[] Sign(byte[] data)
    {
        using var e = LoadKey();
        return e.SignData(data, HashAlgorithmName.SHA256);
    }

    ECDsa LoadKey()
    {
        var e = ECDsa.Create();
        e.ImportPkcs8PrivateKey(Convert.FromBase64String(Secret), out _);
        return e;
    }

    public static AppConfig Load()
    {
        AppConfig c = null;
        try { if (File.Exists(FilePath)) c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)); } catch { }
        c ??= new AppConfig();
        if (string.IsNullOrEmpty(c.KeySalt))
        {
            c.KeySalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            c.FixedKey = "";
            c.Save();
        }
        bool valid;
        try { using var k = c.LoadKey(); valid = true; } catch { valid = false; }
        if (!valid)
        {
            using var e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            c.Secret = Convert.ToBase64String(e.ExportPkcs8PrivateKey());
            c.Save();
        }
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>El ID de 9 dígitos se deriva de la clave pública: nadie puede registrar tu ID sin tu clave privada.</summary>
    public static string IdFromPublicKey(byte[] pub)
    {
        var h = SHA256.HashData(pub);
        ulong v = BitConverter.ToUInt64(h, 0);
        return (100_000_000UL + v % 900_000_000UL).ToString();
    }

    public static string FormatId(string id) =>
        id.Length == 9 ? $"{id[..3]} {id.Substring(3, 3)} {id[6..]}" : id;

    public static string NewPassword()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }
}
