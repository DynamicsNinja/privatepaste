using System.Globalization;

namespace PrivatePaste.Server.Config;

public sealed record ServerOptions(string DbKey, string Password, string DataDir, long MaxImageBytes, long MaxTextBytes)
{
    private const int MinDbKeyLength = 16;

    /// <summary>
    /// Behind a Cloudflare tunnel, take the visitor's IP from CF-Connecting-IP and the scheme from Cf-Visitor.
    /// Only safe when the server cannot be reached except through Cloudflare, since anyone else could set these headers.
    /// </summary>
    public bool TrustCloudflare { get; init; }

    public static ServerOptions FromConfiguration(IConfiguration config)
    {
        var dbKey = config["PP_DB_KEY"];
        if (string.IsNullOrEmpty(dbKey))
            throw new InvalidOperationException("PP_DB_KEY is required.");
        if (dbKey.Length < MinDbKeyLength)
            throw new InvalidOperationException($"PP_DB_KEY must be at least {MinDbKeyLength} characters.");

        var password = config["PP_PASSWORD"];
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("PP_PASSWORD is required.");

        var dataDir = config["PP_DATA_DIR"];
        if (string.IsNullOrWhiteSpace(dataDir)) dataDir = "/data";

        var imageMb = ReadPositive(config, "PP_MAX_IMAGE_MB", 20);
        var textKb = ReadPositive(config, "PP_MAX_TEXT_KB", 1024);

        return new ServerOptions(dbKey, password, dataDir, imageMb * 1024 * 1024, textKb * 1024)
        {
            TrustCloudflare = ReadBool(config, "PP_TRUST_CLOUDFLARE"),
        };
    }

    private static bool ReadBool(IConfiguration config, string name)
    {
        var raw = config[name];
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!bool.TryParse(raw, out var value))
            throw new InvalidOperationException($"{name} must be 'true' or 'false', got '{raw}'.");
        return value;
    }

    private static long ReadPositive(IConfiguration config, string name, long defaultValue)
    {
        var raw = config[name];
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new InvalidOperationException($"{name} must be a positive integer, got '{raw}'.");
        return value;
    }
}
