namespace FlowMind.Infrastructure;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Private root folder for original documents. Must not be web-accessible.</summary>
    public string RootPath { get; set; } = "";
    /// <summary>Operator switch (not a company setting). false = a company admin may only point storage at a public https address; true = also allow private/LAN addresses such as a MinIO server on this network.</summary>
    public bool AllowPrivateEndpoints { get; set; }
    /// <summary>
    /// Operator switch for "Folder on a server" storage (for example a folder on the IIS machine or a file share such as \\fileserver\FlowMind).
    /// A company admin may only choose a folder INSIDE one of these roots. Empty (the default) = the folder option is switched off, so a typed path
    /// can never reach system folders or other applications' files. Set as Storage__AllowedFolderRoots__0, __1 ... in the environment.
    /// </summary>
    public string[] AllowedFolderRoots { get; set; } = Array.Empty<string>();
}

public sealed class ProcessingOptions
{
    public const string Section = "Processing";
    public int MaxPages { get; set; } = 500;
    /// <summary>A page with fewer extracted characters than this AND at least one image is treated as scanned.</summary>
    public int OcrMinChars { get; set; } = 25;
    public int OcrBatchPages { get; set; } = 50;
    public int MaxAttempts { get; set; } = 3;
    public int StaleJobMinutes { get; set; } = 15;
}

public sealed class AiProvidersOptions
{
    public const string Section = "AiProviders";
    public MistralOcrOptions MistralOcr { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
    public CloudflareOptions Cloudflare { get; set; } = new();
    public GroqOptions Groq { get; set; } = new();
}

public sealed class GroqOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "openai/gpt-oss-20b";
    public string BaseUrl { get; set; } = "https://api.groq.com/";
    /// <summary>Hard daily request cap enforced by FlowMind (UTC day). 0 (default) = chat disabled until you set it from your Groq limits page.</summary>
    public int DailyRequestLimit { get; set; }
    /// <summary>Optional daily cap on input+output tokens. 0 = not enforced.</summary>
    public int DailyTokenLimit { get; set; }
    /// <summary>Client-side pacing; set to your model's real RPM (or lower).</summary>
    public int RequestsPerMinute { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 1200;
    /// <summary>gpt-oss reasoning effort: low | medium | high. Low keeps free-tier token use down.</summary>
    public string ReasoningEffort { get; set; } = "low";
}

public sealed class ChatOptions
{
    public const string Section = "Chat";
    /// <summary>Longest message accepted. Longer pastes are fine: the request layer condenses them before any AI call.</summary>
    public int MaxMessageChars { get; set; } = 8000;

    // ---- token-saving request layer (all local; none of this calls an AI provider) ----
    /// <summary>The condensed question sent to retrieval and the model is cut down to at most this many characters.</summary>
    public int MaxQuestionChars { get; set; } = 400;
    /// <summary>Previous messages sent with a follow-up. Self-contained questions send none.</summary>
    public int HistoryMessages { get; set; } = 4;
    public int HistoryUserChars { get; set; } = 200;
    public int HistoryAssistantChars { get; set; } = 350;
    /// <summary>Total characters of document excerpts the model reads (about 4 per token), and the most any one excerpt may use. Excerpts are sent whole; only the last may be cut, at a line end.</summary>
    public int SourceChars { get; set; } = 6000;
    public int SourceCharsEach { get; set; } = 1600;
    /// <summary>Identical questions over identical excerpts reuse the earlier answer instead of calling the model.</summary>
    public bool CacheAnswers { get; set; } = true;
    public int CacheDays { get; set; } = 14;

    public int TopK { get; set; } = 6;
    /// <summary>
    /// Best vector similarity below which retrieved text is not considered relevant: no AI call is made and the reply is "not documented".
    /// Measured on a real 20-page document with bge-m3: unrelated questions score 0.25-0.41, genuine questions 0.42-0.56.
    /// </summary>
    public double MinSimilarity { get; set; } = 0.35;
    /// <summary>A "not documented" reply lists the closest sections only when they are at least this similar (so nonsense questions get no suggestions).</summary>
    public double RelatedMinSimilarity { get; set; } = 0.45;

    // ---- Tamil (questions in Tamil, answered in Tamil, over documents in English and/or Tamil) ----
    /// <summary>
    /// Same idea as <see cref="MinSimilarity"/> for Tamil questions. Measured with bge-m3 on a real document set: genuine Tamil questions scored
    /// 0.54-0.71, unrelated Tamil ones 0.31-0.40 (English: 0.55-0.69 and 0.27-0.38), so 0.42 separates them with margin.
    /// </summary>
    public double MinSimilarityTamil { get; set; } = 0.42;
    /// <summary>Which provider writes Tamil answers: "gemini" (recommended: strongest Tamil on a free tier) or "groq". Gemini falls back to Groq when its free quota is used up.</summary>
    public string TamilProvider { get; set; } = "gemini";
    /// <summary>Tamil takes several times more tokens than English for the same sentence, so its answers get a larger output budget.</summary>
    public int TamilMaxOutputTokens { get; set; } = 3000;
}

public sealed class CloudflareOptions
{
    public string AccountId { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public string Model { get; set; } = "@cf/baai/bge-m3";
    public string BaseUrl { get; set; } = "https://api.cloudflare.com/";
    /// <summary>Output size of the model; every returned vector is checked against it. Verify with a live call.</summary>
    public int Dimensions { get; set; } = 1024;
    /// <summary>Hard daily neuron cap enforced by FlowMind. 0 (default) = embedding disabled until you set it (free allowance is 10,000/day; use less).</summary>
    public int DailyNeuronLimit { get; set; }
    /// <summary>Estimate only: $0.0118 per M tokens / $0.011 per 1,000 neurons ≈ 1,073. Used to count usage before the call.</summary>
    public int NeuronsPerMillionTokens { get; set; } = 1075;
    public int BatchSize { get; set; } = 16;
}

public sealed class RetrievalOptions
{
    public const string Section = "Retrieval";
    public int ChunkChars { get; set; } = 1200;
    public int OverlapChars { get; set; } = 150;
    public int DefaultTopK { get; set; } = 6;
    public int MaxTopK { get; set; } = 20;
}

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    /// <summary>Verified stable Flash-Lite IDs (2026-11): gemini-3.1-flash-lite, gemini-3.5-flash-lite.</summary>
    public string Model { get; set; } = "gemini-3.1-flash-lite";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";
    /// <summary>Hard daily request cap enforced by FlowMind. 0 (default) = analysis disabled until you set it from your AI Studio limits.</summary>
    public int DailyRequestLimit { get; set; }
    /// <summary>Optional daily cap on input+output tokens. 0 = not enforced (the request cap still applies).</summary>
    public int DailyTokenLimit { get; set; }
    /// <summary>Client-side pacing so the free per-minute limit is not hit. Set to your model's real RPM (or lower).</summary>
    public int RequestsPerMinute { get; set; } = 5;
    public int MaxOutputTokens { get; set; } = 8192;
    /// <summary>Model that writes chat answers in Tamil. Empty = the same model as analysis. Uses the same free daily quota as analysis.</summary>
    public string ChatModel { get; set; } = "";
    /// <summary>
    /// Tamil chat has its own daily caps INSIDE the shared Gemini total (<see cref="DailyRequestLimit"/>), so Tamil questions can never use up the requests document analysis needs.
    /// When a cap is reached Tamil answers fall back to the default free chat model. 0 = no separate cap (only the shared total applies).
    /// </summary>
    public int ChatDailyRequestLimit { get; set; } = 150;
    public int ChatDailyTokenLimit { get; set; } = 300000;
}

public sealed class AnalysisOptions
{
    public const string Section = "Analysis";
    /// <summary>Max characters of document text per AI request (about 4 chars per token).</summary>
    public int WindowChars { get; set; } = 12000;
}

public sealed class MistralOcrOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "mistral-ocr-latest";
    public string BaseUrl { get; set; } = "https://api.mistral.ai/";
    /// <summary>Hard daily page cap enforced by FlowMind. 0 (default) = OCR disabled until you set it from your account's real free allowance.</summary>
    public int DailyPageLimit { get; set; }
    /// <summary>Estimated cost if the free allowance did not apply. Informational only; FlowMind never bills.</summary>
    public decimal EstimatedCostPer1000PagesUsd { get; set; }
}
