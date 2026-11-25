namespace FlowMind.Application.Interfaces;

// Provider-independent contracts. Business logic depends only on these; concrete
// providers (Gemini, Mistral, Cloudflare, Groq, or an internal model) live in Infrastructure/AI.

public sealed record OcrPageResult(int PageNumber, string Markdown);

public sealed record ChatMessageDto(string Role, string Content);

public sealed record ChatCompletionResult(string Content, int InputTokens, int OutputTokens);

public sealed record DocumentAnalysisResult(string Json, int InputTokens, int OutputTokens);

public interface IDocumentAnalysisProvider
{
    string Name { get; }
    /// <summary>Returns structured JSON (modules/pages/validations/workflows) for a bounded text slice.</summary>
    Task<DocumentAnalysisResult> AnalyzeAsync(string systemPrompt, string content, string jsonSchema, CancellationToken ct);
}

public interface IOcrProvider
{
    string Name { get; }
    /// <summary>OCR only the requested pages of a PDF/image.</summary>
    Task<IReadOnlyList<OcrPageResult>> OcrAsync(Stream document, string contentType, IReadOnlyCollection<int>? pages, CancellationToken ct);
}

public interface IEmbeddingProvider
{
    string Name { get; }
    int Dimensions { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

public interface IChatProvider
{
    string Name { get; }
    Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> messages, int maxOutputTokens, CancellationToken ct);
}

/// <summary>A chat provider chosen for languages the default chat model writes poorly (Tamil). Separate type so it can be registered next to the default one.</summary>
public interface IMultilingualChatProvider : IChatProvider
{
    /// <summary>The model that will be used, for usage records.</summary>
    string Model { get; }
}

/// <summary>Thrown by providers/guards when a free quota is exhausted. Never retried, never falls back to a paid model.</summary>
public sealed class QuotaExceededException : Exception
{
    public string Provider { get; }
    public DateTime? RetryAfterUtc { get; }
    public QuotaExceededException(string provider, string message, DateTime? retryAfterUtc = null) : base(message)
    {
        Provider = provider;
        RetryAfterUtc = retryAfterUtc;
    }
}
