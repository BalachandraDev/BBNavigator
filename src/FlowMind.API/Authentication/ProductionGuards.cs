namespace FlowMind.API.Authentication;

/// <summary>
/// Refuses to start outside Development with development secrets or weak settings, so a "works on my machine"
/// configuration can never silently reach production.
/// </summary>
public static class ProductionGuards
{
    public static IReadOnlyList<string> Check(IConfiguration c)
    {
        var problems = new List<string>();
        static bool Dev(string? v) =>
            v != null && (v.Contains("change-me", StringComparison.OrdinalIgnoreCase) || v.Contains("DEV-ONLY", StringComparison.OrdinalIgnoreCase));

        var signing = c["Auth:SigningKey"] ?? "";
        if (signing.Length < 32) problems.Add("Auth:SigningKey must be at least 32 characters.");
        if (Dev(signing)) problems.Add("Auth:SigningKey is a development placeholder. Generate a random value and set it via an environment variable.");

        var admin = c["Auth:PlatformAdminKey"] ?? "";
        if (admin.Length > 0 && (admin.Length < 24 || Dev(admin)))
            problems.Add("Auth:PlatformAdminKey is weak or a development placeholder (use 24+ random characters, or leave it empty to disable platform-admin login).");

        foreach (var name in new[] { "SqlServer", "Postgres" })
        {
            var cs = c.GetConnectionString(name) ?? "";
            if (cs.Length == 0) problems.Add($"ConnectionStrings:{name} is not set.");
            else if (cs.Contains("flowmind_dev_only", StringComparison.OrdinalIgnoreCase) || cs.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
                problems.Add($"ConnectionStrings:{name} points at a development database or uses the development password.");
        }

        var secrets = c["Security:SecretsKey"] ?? "";
        if (secrets.Length < 32 || Dev(secrets)) problems.Add("Security:SecretsKey must be 32+ random characters (not a placeholder): it encrypts the company keys the platform admin can view.");

        if (string.IsNullOrWhiteSpace(c["Storage:RootPath"])) problems.Add("Storage:RootPath is not set.");
        return problems;
    }

    public static void Enforce(IConfiguration c, IHostEnvironment env)
    {
        if (env.IsDevelopment()) return;
        var problems = Check(c);
        if (problems.Count > 0)
            throw new InvalidOperationException("Unsafe configuration for " + env.EnvironmentName + ":\n - " + string.Join("\n - ", problems));
    }
}
