namespace FlowMind.Domain.Enums;

public enum DocumentStatus
{
    Uploaded = 0,
    Queued = 1,
    Processing = 2,
    /// <summary>Text extracted from digital pages, but some pages still need OCR (quota/config blocked). Retry later.</summary>
    AwaitingOcr = 3,
    /// <summary>All pages have text. Ready for AI analysis (Phase 3).</summary>
    Extracted = 4,
    PendingReview = 5,
    Approved = 6,
    Failed = 7,
    Deleted = 8,
    /// <summary>AI analysis in progress (values appended to keep stored numbers stable).</summary>
    Analyzing = 9,
    /// <summary>Extraction finished but AI analysis is blocked (quota/limit/config). Retry later.</summary>
    AwaitingAnalysis = 10
}

public enum ReviewStatus { Pending = 0, Approved = 1, Rejected = 2 }

public enum JobStage { Extraction = 0, Ocr = 1, Analysis = 2, Chunking = 3, Embedding = 4 }

public enum JobStatus { Pending = 0, Running = 1, Succeeded = 2, Failed = 3, QuotaExceeded = 4 }

public enum PageTextSource { Digital = 0, Ocr = 1, NeedsOcr = 2 }

public enum AiProviderKind { DocumentAnalysis = 0, Ocr = 1, Embedding = 2, Chat = 3 }

public enum AiRequestStatus { Success = 0, Failed = 1, QuotaExceeded = 2, RateLimited = 3, Cached = 4 }
