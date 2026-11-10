using System.Security.Cryptography;
using System.Text.Json;

namespace ShowControl;

internal sealed record AccessView(bool Configured, string? Role);
internal sealed record PasswordSettings(string? AdminPassword, string? OperatorPassword, bool RemoveOperator);
internal sealed record UnlockRequest(string Role, string Password);

internal sealed class AccessControl
{
    private readonly object gate = new();
    private readonly string file;
    private Credentials? credentials;
    private string? session;
    private string? role;
    private int failures;
    private DateTimeOffset retryAfter;

    public AccessControl(string directory)
    {
        file = Path.Combine(directory, "access.json");
        if (File.Exists(file))
            credentials = JsonSerializer.Deserialize<Credentials>(File.ReadAllText(file))
                ?? throw new InvalidDataException("无法读取权限配置。");
    }

    public AccessView View(string? token)
    {
        lock (gate)
            return new(credentials is not null, Role(token));
    }

    private string? Role(string? token) =>
        credentials is null ? "admin" : session is not null && token == session ? role : null;

    public string Configure(string? token, PasswordSettings settings)
    {
        lock (gate)
        {
            if (Role(token) != "admin")
                throw new UnauthorizedAccessException();
            var admin = string.IsNullOrEmpty(settings.AdminPassword) ? credentials?.Admin : Hash(settings.AdminPassword);
            if (admin is null)
                throw new ArgumentException("首次设置须填写管理员密码。");
            var oper = settings.RemoveOperator ? null : string.IsNullOrEmpty(settings.OperatorPassword)
                ? credentials?.Operator : Hash(settings.OperatorPassword);
            var next = new Credentials(admin, oper);
            LocalSettings.Write(file, next);
            credentials = next;
            return Issue("admin");
        }
    }

    public (string? Token, bool Limited) Unlock(UnlockRequest request)
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow < retryAfter)
                return (null, true);
            var password = request.Role switch
            {
                "admin" => credentials?.Admin,
                "operator" => credentials?.Operator,
                _ => null
            };
            if (password is not null && request.Password is { Length: >= 8 and <= 128 } && password.Matches(request.Password))
            {
                failures = 0;
                return (Issue(request.Role), false);
            }
            if (++failures >= 5)
            {
                failures = 0;
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            return (null, false);
        }
    }

    public bool Lock(string? token)
    {
        lock (gate)
        {
            if (credentials is null || Role(token) is null)
                return false;
            session = role = null;
            return true;
        }
    }

    private string Issue(string nextRole)
    {
        role = nextRole;
        return session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static PasswordHash Hash(string password)
    {
        if (password.Length is < 8 or > 128)
            throw new ArgumentException("密码须为 8–128 个字符。");
        var salt = RandomNumberGenerator.GetBytes(16);
        return new(salt, Derive(password, salt));
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);

    private sealed record Credentials(PasswordHash Admin, PasswordHash? Operator);
    private sealed record PasswordHash(byte[] Salt, byte[] Digest)
    {
        public bool Matches(string password) => CryptographicOperations.FixedTimeEquals(Digest, Derive(password, Salt));
    }
}

internal sealed record PanelSize(int? Width = null, int? Height = null, int? InsetX = null, int? InsetY = null);
internal sealed record LayoutState(string[] Order, Dictionary<string, PanelSize>? Sizes = null);

internal sealed class WorkspaceLayout(string directory)
{
    private static readonly LayoutState Default = new(["project", "workspace", "properties", "log"]);
    private readonly string file = Path.Combine(directory, "layout.json");
    private readonly object gate = new();

    public LayoutState Get()
    {
        lock (gate)
        {
            if (!File.Exists(file)) return Default;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                return Parse(document.RootElement);
            }
            catch (Exception error) when (error is JsonException or ArgumentException) { return Default; }
        }
    }

    public LayoutState Save(JsonElement data)
    {
        var layout = Parse(data);
        lock (gate)
            LocalSettings.Write(file, layout);
        return layout;
    }

    private static LayoutState Parse(JsonElement data)
    {
        var layout = data.ValueKind == JsonValueKind.Array
            ? new LayoutState(data.Deserialize<string[]>()!)
            : data.Deserialize<LayoutState>(JsonSerializerOptions.Web);
        if (layout?.Order?.Length != Default.Order.Length || !Default.Order.ToHashSet().SetEquals(layout.Order))
            throw new ArgumentException("面板顺序无效。");
        if (layout.Sizes?.Any(panel => !Default.Order.Contains(panel.Key) || panel.Value is null ||
            panel.Value.Width is < 140 or > 2400 || panel.Value.Height is < 110 or > 2400 ||
            panel.Value.InsetX is < 0 or > 2400 || panel.Value.InsetY is < 0 or > 2400) == true)
            throw new ArgumentException("面板尺寸超出允许范围。");
        return layout;
    }
}

internal static class LocalSettings
{
    public static void Write<T>(string file, T settings)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings));
            File.Move(temp, file, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
