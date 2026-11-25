using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.AI;
using FlowMind.Infrastructure.Chat;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Retrieval;
using FlowMind.Infrastructure.VectorStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

/// <summary>Tamil and English: questions and answers in the language the person wrote in.</summary>
public class MultilingualTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private sealed class FakeEmbedder : IEmbeddingProvider
    {
        public string Name => "cloudflare"; public int Dimensions => 4;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> t, CancellationToken ct) => Task.FromResult<IReadOnlyList<float[]>>(t.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
    }
    private sealed class StubStore : IVectorStore
    {
        public List<ChunkHit> Hits = new();
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>());
        public Task UpsertAsync(Guid t, IReadOnlyList<ChunkRecord> c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChunkHit>>(Hits);
    }
    private class FakeChat : IChatProvider
    {
        public virtual string Name => "groq"; public int Calls; public int LastMaxTokens; public List<ChatMessageDto> Last = new(); public string Reply = "Full name is mandatory [1].";
        public Exception? Throw;
        public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> m, int max, CancellationToken ct)
        { Calls++; Last = m.ToList(); LastMaxTokens = max; if (Throw != null) throw Throw; return Task.FromResult(new ChatCompletionResult(Reply, 100, 20)); }
    }
    private sealed class FakeTamilChat : FakeChat, IMultilingualChatProvider
    {
        public override string Name => "gemini"; public string Model => "gemini-test";
    }

    private static readonly Guid Tenant = Guid.NewGuid(), App = Guid.NewGuid();
    private static ChunkHit Hit(string text, double sim) => new(Guid.NewGuid(), Guid.NewGuid(), "BMW_Guide.pdf", 3, text, 0.03, sim);

    private static (ChatService Svc, FlowMindDbContext Db, FakeChat Groq, FakeTamilChat Gemini, StubStore Store) Make(int geminiLimit = 400, bool withTamilProvider = true, string tamilProvider = "gemini", int chatReq = 150, int chatTokens = 300000)
    {
        var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var providers = Options.Create(new AiProvidersOptions
        {
            Groq = new GroqOptions { DailyRequestLimit = 1000, MaxOutputTokens = 1200 }, Gemini = new GeminiOptions { DailyRequestLimit = geminiLimit, ChatDailyRequestLimit = chatReq, ChatDailyTokenLimit = chatTokens },
            Cloudflare = new CloudflareOptions { DailyNeuronLimit = 100000, Dimensions = 4 }
        });
        var usage = new AiUsageService(db, providers); var store = new StubStore(); var groq = new FakeChat(); var gemini = new FakeTamilChat { Reply = "முழுப் பெயர் கட்டாயம் [1]." };
        var retrieval = new RetrievalService(new EmbeddingService(new FakeEmbedder(), usage, providers), store, Options.Create(new RetrievalOptions()));
        var svc = new ChatService(db, retrieval, groq, usage, Options.Create(new ChatOptions { TamilProvider = tamilProvider }), providers, NullLogger<ChatService>.Instance, withTamilProvider ? gemini : null);
        return (svc, db, groq, gemini, store);
    }

    // ---- language detection ----

    [Theory]
    [InlineData("What is the next step after dispensing?", "en")]
    [InlineData("ஸ்டிக்கர்கள் உருவாக்கிய பிறகு அடுத்த படி என்ன?", "ta")]
    [InlineData("Dispensing பக்கத்தில் எடைகளை எப்படி உள்ளிடுவது?", "ta")]       // Tamil sentence with an English page name
    [InlineData("Where do I confirm the தமிழ் counts for the Dispensing page and the BMW Review page?", "en")]   // a stray Tamil word in an English sentence
    [InlineData("", "en")]
    [InlineData("12345 ???", "en")]
    public void The_language_is_taken_from_the_script_the_person_wrote_in(string text, string expected) =>
        Assert.Equal(expected, Language.Detect(text));

    // ---- small talk ----

    [Theory]
    [InlineData("வணக்கம்", "வணக்கம்!")]
    [InlineData("வணக்கம் சார்", "வணக்கம்!")]
    [InlineData("நீங்கள் யார்?", "வணக்கம்!")]
    [InlineData("எப்படி இருக்கீங்க?", "நன்றாக")]
    [InlineData("நன்றி", "மகிழ்ச்சி")]
    [InlineData("மிக்க நன்றி!", "மகிழ்ச்சி")]
    [InlineData("சரி", "மகிழ்ச்சி")]
    [InlineData("பை", "நன்றி, மீண்டும்")]
    [InlineData("vanakkam", "வணக்கம்!")]                                         // Tamil typed in English letters
    public void Tamil_greetings_and_thanks_are_answered_in_Tamil_without_any_AI_call(string message, string expectedStart)
    {
        var reply = SmallTalk.TryReply(message, "Hospital System");
        Assert.NotNull(reply);
        Assert.StartsWith(expectedStart, reply);
        Assert.True(Language.HasTamil(reply));
    }

    [Fact]
    public void English_small_talk_is_unchanged_and_a_real_Tamil_question_is_not_small_talk()
    {
        Assert.StartsWith("Hello!", SmallTalk.TryReply("hello", "X"));
        Assert.StartsWith("You're welcome", SmallTalk.TryReply("thanks!", "X"));
        Assert.Null(SmallTalk.TryReply("அறிக்கையை யார் இறுதி செய்யலாம்?", "X"));
        Assert.Null(SmallTalk.TryReply("வணக்கம் அறிக்கையை யார் இறுதி செய்யலாம் என்று சொல்லுங்கள் தயவு செய்து இப்போது", "X"));   // too long to be a greeting
    }

    // ---- routing and the Tamil similarity cut-off ----

    [Fact]
    public async Task A_Tamil_question_is_answered_in_Tamil_by_the_Tamil_model_and_told_to_reply_in_Tamil()
    {
        var (svc, db, groq, gemini, store) = Make();
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));

        var a = await svc.AskAsync(Tenant, "u1", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default);

        Assert.True(a.Grounded);
        Assert.Equal("முழுப் பெயர் கட்டாயம்.", a.Answer);
        Assert.Equal(1, gemini.Calls); Assert.Equal(0, groq.Calls);
        Assert.Contains("REPLY LANGUAGE: Tamil", gemini.Last[^1].Content);
        Assert.Contains("Tamil", gemini.Last[0].Content);                                  // the system prompt carries the language rule
        Assert.True(gemini.LastMaxTokens >= 3000);                                         // Tamil needs a bigger output budget
        Assert.Contains(db.AiUsageLogs, l => l.Provider == "gemini" && l.Model == "gemini-test" && l.ProviderKind == AiProviderKind.Chat && l.Status == AiRequestStatus.Success);
    }

    [Fact]
    public async Task An_English_question_still_goes_to_the_default_model_and_asks_for_English()
    {
        var (svc, _, groq, gemini, store) = Make();
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));
        groq.Reply = "Full name is mandatory [1].";

        var a = await svc.AskAsync(Tenant, "u1", App, null, "Is full name mandatory on the registration page?", default);

        Assert.Equal("Full name is mandatory.", a.Answer);
        Assert.Equal(1, groq.Calls); Assert.Equal(0, gemini.Calls);
        Assert.Contains("REPLY LANGUAGE: English", groq.Last[^1].Content);
    }

    [Fact]
    public async Task Tamil_uses_its_own_stricter_relevance_cutoff()
    {
        // 0.38 is above the English cut-off (0.35) but below the Tamil one (0.42), where unrelated Tamil questions were measured at 0.31-0.40.
        var (svc, _, groq, gemini, store) = Make();
        store.Hits.Add(Hit("Something loosely related.", 0.38));

        var tamil = await svc.AskAsync(Tenant, "u1", App, null, "வாகன நிறுத்துமிடக் கட்டணம் இன்று எவ்வளவு?", default);
        Assert.True(tamil.Grounded);                                                        // unrelated: a redirect, not a documentation gap
        Assert.Contains("what can I ask?", tamil.Answer);
        Assert.True(Language.HasTamil(tamil.Answer));                                       // the Tamil "outside what I can help with" reply
        Assert.Equal(0, gemini.Calls + groq.Calls);                                         // rejected locally, no AI call

        var english = await svc.AskAsync(Tenant, "u1", App, null, "What is the visitor parking fee today?", default);
        Assert.Equal(1, groq.Calls);                                                        // English at 0.38 still reaches the model (unchanged behaviour)
        _ = english;
    }

    [Fact]
    public async Task A_model_refusal_in_Tamil_gets_the_Tamil_not_documented_reply()
    {
        var (svc, _, _, gemini, store) = Make();
        store.Hits.Add(Hit("Unrelated text.", 0.5));
        gemini.Reply = "NOT_DOCUMENTED";

        var a = await svc.AskAsync(Tenant, "u1", App, null, "வாகன நிறுத்துமிடக் கட்டணம் யார் செலுத்துவார்?", default);

        Assert.False(a.Grounded);
        Assert.Equal(SmallTalk.NotDocumented(Language.Tamil), a.Answer);
        Assert.True(Language.HasTamil(a.Answer));
    }

    [Fact]
    public async Task When_the_Tamil_model_has_no_free_quota_the_default_free_model_answers_instead()
    {
        var (svc, db, groq, gemini, store) = Make();
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));
        gemini.Throw = new QuotaExceededException("gemini", "Gemini daily free quota exhausted.");
        groq.Reply = "முழுப் பெயர் கட்டாயம் [1].";

        var a = await svc.AskAsync(Tenant, "u1", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default);

        Assert.True(a.Grounded);
        Assert.Equal(1, gemini.Calls); Assert.Equal(1, groq.Calls);
        Assert.Contains(db.AiUsageLogs, l => l.Provider == "gemini" && l.Status == AiRequestStatus.QuotaExceeded);
        Assert.Contains(db.AiUsageLogs, l => l.Provider == "groq" && l.Status == AiRequestStatus.Success);
    }

    [Fact]
    public async Task Tamil_can_be_pointed_at_the_default_model_or_work_without_a_second_provider()
    {
        var (svc, _, groq, gemini, store) = Make(tamilProvider: "groq");
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));
        groq.Reply = "முழுப் பெயர் கட்டாயம் [1].";
        await svc.AskAsync(Tenant, "u1", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default);
        Assert.Equal(1, groq.Calls); Assert.Equal(0, gemini.Calls);
        Assert.True(groq.LastMaxTokens >= 3000);

        var (svc2, _, groq2, _, store2) = Make(withTamilProvider: false);
        store2.Hits.Add(Hit("Full name is mandatory.", 0.6));
        groq2.Reply = "முழுப் பெயர் கட்டாயம் [1].";
        await svc2.AskAsync(Tenant, "u1", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default);
        Assert.Equal(1, groq2.Calls);
    }

    // ---- shared free quota ----

    [Fact]
    public async Task Tamil_chat_and_document_analysis_share_the_same_free_Gemini_daily_limit()
    {
        var (svc, db, groq, gemini, store) = Make(geminiLimit: 1);
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));
        db.AiUsageLogs.Add(new AIUsageLog { TenantId = Tenant, ProviderKind = AiProviderKind.DocumentAnalysis, Provider = "gemini", Model = "m", Operation = "analyze", Purpose = "x", Status = AiRequestStatus.Success });
        await db.SaveChangesAsync();
        groq.Reply = "முழுப் பெயர் கட்டாயம் [1].";

        var a = await svc.AskAsync(Tenant, "u1", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default);

        Assert.Equal(0, gemini.Calls);                       // the one allowed request was already used by analysis: Gemini is not called
        Assert.Equal(1, groq.Calls);                         // so the free default model answered
        Assert.True(a.Grounded);
    }

    // ---- search + provider details ----

    [Fact]
    public void The_keyword_search_only_gets_Latin_words_from_a_Tamil_question()
    {
        Assert.Equal("dispensing", PgVectorStore.BuildKeywordQuery("Dispensing பக்கத்தில் எடைகளை எப்படி உள்ளிடுவது?"));
        Assert.Equal("", PgVectorStore.BuildKeywordQuery("பக்கத்தில் எடைகளை எப்படி உள்ளிடுவது?"));
        Assert.Equal("what | next", PgVectorStore.BuildKeywordQuery("what next"));
    }

    [Fact]
    public void Gemini_chat_requests_merge_neighbouring_turns_and_map_roles()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(GeminiChatProvider.BuildBody(new[]
        {
            new ChatMessageDto("system", "RULES"), new ChatMessageDto("user", "q1"), new ChatMessageDto("assistant", "a1"),
            new ChatMessageDto("user", "q2"), new ChatMessageDto("user", "SOURCES"),
        }, 3000));
        Assert.Contains("RULES", body);
        Assert.Contains("\"role\":\"model\"", body);
        Assert.Contains("q2\\n\\nSOURCES", body);                       // two user turns in a row become one
        Assert.Contains("\"maxOutputTokens\":3000", body);
    }

    [Fact]
    public void Gemini_chat_answers_are_read_and_empty_or_blocked_ones_are_reported()
    {
        var ok = GeminiChatProvider.Parse("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"முழுப் பெயர் கட்டாயம் [1].\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":321,\"candidatesTokenCount\":45}}");
        Assert.Equal("முழுப் பெயர் கட்டாயம் [1].", ok.Content); Assert.Equal(321, ok.InputTokens); Assert.Equal(45, ok.OutputTokens);
        Assert.Throws<InvalidAnalysisOutputException>(() => GeminiChatProvider.Parse("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}"));
        var cut = Assert.Throws<InvalidAnalysisOutputException>(() => GeminiChatProvider.Parse("{\"candidates\":[{\"content\":{\"parts\":[]},\"finishReason\":\"MAX_TOKENS\"}]}"));
        Assert.Contains("TamilMaxOutputTokens", cut.Message);
    }

    // ---- the SELECTED language decides the reply language ----

    [Fact]
    public async Task The_selected_language_decides_the_reply_language_whatever_the_question_is_written_in()
    {
        var (svc, _, groq, gemini, store) = Make();
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));

        // Tamil selected, English question -> Tamil answer from the Tamil model
        await svc.AskAsync(Tenant, "u1", App, null, "Is full name mandatory on the registration page?", default, null, "ta");
        Assert.Equal(1, gemini.Calls); Assert.Equal(0, groq.Calls);
        Assert.Contains("REPLY LANGUAGE: Tamil", gemini.Last[^1].Content);

        // English selected, Tamil question -> English answer from the default model
        groq.Reply = "Full name is mandatory [1].";
        var a = await svc.AskAsync(Tenant, "u2", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default, null, "en");
        Assert.Equal(1, gemini.Calls); Assert.Equal(1, groq.Calls);
        Assert.Contains("REPLY LANGUAGE: English", groq.Last[^1].Content);
        Assert.Equal("Full name is mandatory.", a.Answer);

        // nothing selected -> follows the question (unchanged behaviour for API callers)
        await svc.AskAsync(Tenant, "u3", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default);
        Assert.Equal(2, gemini.Calls);
    }

    [Fact]
    public async Task Greetings_and_fixed_replies_follow_the_selected_language_too()
    {
        var (svc, _, groq, gemini, store) = Make();
        var ta = await svc.AskAsync(Tenant, "u1", App, null, "hello", default, null, "ta");
        Assert.StartsWith("வணக்கம்!", ta.Answer);
        var en = await svc.AskAsync(Tenant, "u2", App, null, "வணக்கம்", default, null, "en");
        Assert.StartsWith("Hello!", en.Answer);
        Assert.Equal(0, groq.Calls + gemini.Calls);                      // still no AI call

        store.Hits.Add(Hit("Something loosely related.", 0.2));         // below every cut-off: the off-topic reply, in the selected language
        var off = await svc.AskAsync(Tenant, "u3", App, null, "What is the weather today?", default, null, "ta");
        Assert.True(Language.HasTamil(off.Answer));
        var off2 = await svc.AskAsync(Tenant, "u4", App, null, "இன்று வானிலை எப்படி?", default, null, "en");
        Assert.False(Language.HasTamil(off2.Answer));
    }

    // ---- Tamil chat has its own caps (and its own dashboard line) ----

    [Fact]
    public async Task Tamil_chat_stops_at_its_own_request_cap_and_the_default_model_answers_instead()
    {
        var (svc, db, groq, gemini, store) = Make(chatReq: 2);
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));
        groq.Reply = "முழுப் பெயர் கட்டாயம் [1].";
        for (int i = 0; i < 2; i++)
            db.AiUsageLogs.Add(new AIUsageLog { TenantId = Tenant, ProviderKind = AiProviderKind.Chat, Provider = "gemini", Model = "gemini-test", Operation = "chat", Purpose = "rag-answer", Status = AiRequestStatus.Success, InputTokens = 10, OutputTokens = 5 });
        await db.SaveChangesAsync();

        var a = await svc.AskAsync(Tenant, "u1", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default, null, "ta");

        Assert.Equal(0, gemini.Calls);                                    // the Tamil cap (2/2) is reached: Gemini is not called
        Assert.Equal(1, groq.Calls); Assert.True(a.Grounded);
        Assert.Contains(db.AiUsageLogs, l => l.Provider == "gemini" && l.Status == AiRequestStatus.QuotaExceeded && l.ErrorMessage!.Contains("Tamil chat request limit"));
    }

    [Fact]
    public async Task Tamil_chat_stops_at_its_own_token_cap_but_analysis_usage_does_not_count_against_it()
    {
        var (svc, db, groq, gemini, store) = Make(chatTokens: 1000);
        store.Hits.Add(Hit("Full name is mandatory.", 0.6));
        // 5,000 tokens of document analysis do not touch the Tamil chat token cap
        db.AiUsageLogs.Add(new AIUsageLog { TenantId = Tenant, ProviderKind = AiProviderKind.DocumentAnalysis, Provider = "gemini", Model = "m", Operation = "analyze", Purpose = "x", Status = AiRequestStatus.Success, InputTokens = 4000, OutputTokens = 1000 });
        await db.SaveChangesAsync();
        await svc.AskAsync(Tenant, "u1", App, null, "பதிவுப் பக்கத்தில் முழுப் பெயர் கட்டாயமா?", default, null, "ta");
        Assert.Equal(1, gemini.Calls);

        db.AiUsageLogs.Add(new AIUsageLog { TenantId = Tenant, ProviderKind = AiProviderKind.Chat, Provider = "gemini", Model = "gemini-test", Operation = "chat", Purpose = "rag-answer", Status = AiRequestStatus.Success, InputTokens = 700, OutputTokens = 400 });
        await db.SaveChangesAsync();
        groq.Reply = "முழுப் பெயர் கட்டாயம் [1].";
        await svc.AskAsync(Tenant, "u2", App, null, "பதிவுப் பக்கத்தில் வேறு புலங்கள் கட்டாயமா?", default, null, "ta");
        Assert.Equal(1, gemini.Calls);                                    // 1,100+ chat tokens used of 1,000: blocked
        Assert.Equal(1, groq.Calls);
    }}
