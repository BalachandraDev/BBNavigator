using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Retrieval;

/// <summary>
/// The single path to the embedding provider: checks the daily neuron cap first, logs every call
/// (success, failure or quota stop) and never falls back to another provider.
/// </summary>
public sealed class EmbeddingService
{
    private readonly IEmbeddingProvider _provider;
    private readonly IAiUsageService _usage;
    private readonly CloudflareOptions _opt;

    public EmbeddingService(IEmbeddingProvider provider, IAiUsageService usage, IOptions<AiProvidersOptions> opt)
    {
        _provider = provider; _usage = usage; _opt = opt.Value.Cloudflare;
    }

    public int BatchSize => Math.Max(1, _opt.BatchSize);
    public int Dimensions => _provider.Dimensions;

    /// <summary>Rough token estimate (≥ chars/4 for English; deliberately a bit high so the cap is conservative).</summary>
    public static int EstimateTokens(IEnumerable<string> texts) => texts.Sum(t => (t.Length + 2) / 3);

    public int EstimateNeurons(int tokens) => (int)Math.Ceiling(tokens * (_opt.NeuronsPerMillionTokens / 1_000_000.0));

    public async Task<IReadOnlyList<float[]>> EmbedAsync(Guid tenantId, IReadOnlyList<string> texts, string purpose, CancellationToken ct)
    {
        var tokens = EstimateTokens(texts);
        var neurons = Math.Max(1, EstimateNeurons(tokens));

        AIUsageLog Log(AiRequestStatus status, string? error = null) => new()
        {
            TenantId = tenantId, ProviderKind = AiProviderKind.Embedding, Provider = _provider.Name, Model = _opt.Model,
            Operation = "embed", Purpose = purpose, InputTokens = status == AiRequestStatus.Success ? tokens : 0,
            EmbeddingUnits = status == AiRequestStatus.Success ? neurons : 0, Status = status,
            ErrorMessage = error is { Length: > 1000 } ? error[..1000] : error
        };

        try
        {
            await _usage.EnsureWithinLimitAsync(AiProviderKind.Embedding, _provider.Name, neurons, ct);
            var vectors = await _provider.EmbedAsync(texts, ct);
            await _usage.RecordAsync(Log(AiRequestStatus.Success), ct);
            return vectors;
        }
        catch (QuotaExceededException ex)
        {
            await _usage.RecordAsync(Log(AiRequestStatus.QuotaExceeded, ex.Message), ct);
            throw;
        }
        catch (ProviderNotConfiguredException ex)
        {
            await _usage.RecordAsync(Log(AiRequestStatus.Failed, ex.Message), ct);
            throw;
        }
        catch (Exception ex) when (ex is TransientProviderException or InvalidAnalysisOutputException)
        {
            await _usage.RecordAsync(Log(AiRequestStatus.Failed, ex.Message), ct);
            throw;
        }
    }
}
