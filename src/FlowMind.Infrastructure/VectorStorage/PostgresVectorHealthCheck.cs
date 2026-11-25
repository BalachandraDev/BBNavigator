using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace FlowMind.Infrastructure.VectorStorage;

/// <summary>Verifies PostgreSQL is reachable and the pgvector extension is installed.</summary>
public sealed class PostgresVectorHealthCheck : IHealthCheck
{
    private readonly string _connectionString;
    public PostgresVectorHealthCheck(string connectionString) => _connectionString = connectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", conn);
            var version = await cmd.ExecuteScalarAsync(ct) as string;
            return version is null
                ? HealthCheckResult.Unhealthy("PostgreSQL reachable but pgvector extension is not installed (CREATE EXTENSION vector).")
                : HealthCheckResult.Healthy($"pgvector {version}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable.", ex);
        }
    }
}
