using System.Globalization;

namespace PrivatePaste.Server.Config;

public sealed record ServerOptions(string DbKey, string Password, string DataDir, long MaxImageBytes, long MaxTextBytes)
{
    private const int MinDbKeyLength = 16;

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

        return new ServerOptions(dbKey, password, dataDir, imageMb * 1024 * 1024, textKb * 1024);
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
