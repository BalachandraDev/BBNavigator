using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure;
using FlowMind.Infrastructure.AI;
using FlowMind.Infrastructure.Chat;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowMind.Tests;

/// <summary>"I did X, what do I do next?" — questions about ORDER, which the reviewed workflows answer cleanly.</summary>
public class WhatNextTests
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
    private sealed class FakeChat : IChatProvider
    {
        public string Name => "groq"; public int Calls; public List<ChatMessageDto> Last = new(); public string Reply = "";
        public Queue<string> Replies = new(); public List<List<ChatMessageDto>> AllPrompts = new();
        public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> m, int max, CancellationToken ct)
        { Calls++; Last = m.ToList(); AllPrompts.Add(Last); return Task.FromResult(new ChatCompletionResult(Replies.Count > 0 ? Replies.Dequeue() : Reply, 100, 20)); }
    }

    private static readonly Guid Tenant = Guid.NewGuid(), App = Guid.NewGuid();
    private static ChunkHit Hit(string text, int page, double sim = 0.6) => new(Guid.NewGuid(), Guid.NewGuid(), "BMW_Flow_Guide.pdf", page, text, 0.03, sim);

    private static (ChatService Svc, FlowMindDbContext Db, FakeChat Chat, StubStore Store) Make()
    {
        var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var providers = Options.Create(new AiProvidersOptions { Groq = new GroqOptions { DailyRequestLimit = 1000 }, Cloudflare = new CloudflareOptions { DailyNeuronLimit = 100000, Dimensions = 4 } });
        var usage = new AiUsageService(db, providers); var store = new StubStore(); var chat = new FakeChat();
        var retrieval = new RetrievalService(new EmbeddingService(new FakeEmbedder(), usage, providers), store, Options.Create(new RetrievalOptions()));
        return (new ChatService(db, retrieval, chat, usage, Options.Create(new ChatOptions()), providers, NullLogger<ChatService>.Instance), db, chat, store);
    }

    /// <summary>A document with an administrator-approved "Daily Waste Life Cycle" workflow, like the real BMW guide.</summary>
    private static void SeedWorkflow(FlowMindDbContext db, ReviewStatus review = ReviewStatus.Approved, int sourcePage = 2)
    {
        var doc = new Document { TenantId = Tenant, ApplicationId = App, FileName = "BMW_Flow_Guide.pdf", ContentType = "application/pdf", Status = DocumentStatus.Approved };
        var ver = new DocumentVersion { TenantId = Tenant, DocumentId = doc.Id, VersionNumber = 1, ContentHash = "h", StoragePath = "p" };
        doc.CurrentVersionId = ver.Id;
        db.Documents.Add(doc); db.DocumentVersions.Add(ver);

        var wf = new Workflow { TenantId = Tenant, ApplicationId = App, Name = "Daily Waste Life Cycle", NormalizedName = "daily waste life cycle", Review = review, SourceDocumentVersionId = ver.Id, SourcePage = sourcePage };
        db.Workflows.Add(wf);
        var names = new[] { "Collection and Stickers", "Dispensing", "BMW Review", "Handover", "Corporate Submission" };
        for (int i = 0; i < names.Length; i++)
        {
            var page = new AppPage { TenantId = Tenant, ApplicationId = App, Name = names[i], NormalizedName = names[i].ToLowerInvariant(), Review = review };
            db.AppPages.Add(page);
            db.WorkflowSteps.Add(new WorkflowStep { TenantId = Tenant, ApplicationId = App, WorkflowId = wf.Id, Name = names[i], NormalizedName = names[i].ToLowerInvariant(), Order = i + 1, AppPageId = page.Id, Review = review });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task A_what_next_question_gets_the_reviewed_workflow_in_order_and_can_cite_it()
    {
        var (svc, db, chat, store) = Make();
        SeedWorkflow(db);
        store.Hits.Add(Hit("Dispensing: confirm each sticker bag is received, then enter weights.", page: 6));
        chat.Reply = "After Dispensing the next step is BMW Review [1].";

        var a = await svc.AskAsync(Tenant, "u1", App, null, "I generated stickers and verified the bags. is there anything next i need to do", default);

        var prompt = chat.Last[^1].Content;
        Assert.Contains("WORKFLOW OUTLINE", prompt);
        Assert.True(prompt.IndexOf("1. Collection and Stickers", StringComparison.Ordinal) < prompt.IndexOf("3. BMW Review", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("3. BMW Review", StringComparison.Ordinal) < prompt.IndexOf("5. Corporate Submission", StringComparison.Ordinal));
        Assert.True(a.Grounded);
        Assert.Equal(new Citation("BMW_Flow_Guide.pdf", 2), Assert.Single(a.Citations));          // the outline is cited at the page the workflow came from
    }

    [Fact]
    public async Task The_model_is_told_how_to_read_split_tables_and_answer_next_step_questions()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.Add(Hit("some text", page: 1));
        chat.Reply = "ok [1]";
        await svc.AskAsync(Tenant, "u1", App, null, "what do I do next after dispensing?", default);
        Assert.Contains("Tables in the sources may be split across lines", chat.Last[0].Content);
        Assert.Contains("what next", chat.Last[0].Content);
        _ = db;
    }

    [Fact]
    public async Task Workflows_that_are_not_approved_are_never_shown_to_the_model()
    {
        var (svc, db, chat, store) = Make();
        SeedWorkflow(db, ReviewStatus.Pending);
        store.Hits.Add(Hit("Dispensing: confirm each sticker bag is received.", page: 6));
        chat.Reply = "x [1]";
        await svc.AskAsync(Tenant, "u1", App, null, "what is the next step after dispensing?", default);
        Assert.DoesNotContain("WORKFLOW OUTLINE", chat.Last[^1].Content);
    }

    [Fact]
    public async Task A_question_that_is_not_about_order_and_shares_no_words_with_a_workflow_gets_no_outline()
    {
        var (svc, db, chat, store) = Make();
        SeedWorkflow(db);
        store.Hits.Add(Hit("Parking is free for staff on the second floor of the car park building.", page: 9));
        chat.Reply = "Parking is free for staff [1].";
        await svc.AskAsync(Tenant, "u1", App, null, "Who pays for the parking?", default);
        Assert.DoesNotContain("WORKFLOW OUTLINE", chat.Last[^1].Content);
    }

    [Fact]
    public async Task An_unanswerable_question_returns_the_closest_sections_instead_of_a_dead_end()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.AddRange(new[]
        {
            Hit("Collection and Stickers text", page: 5), Hit("Dispensing text", page: 6), Hit("More dispensing text", page: 6),
            Hit("Review text", page: 8), Hit("Weak match that should not be offered", page: 12, sim: 0.1),
        });
        chat.Reply = "NOT_DOCUMENTED";

        var a = await svc.AskAsync(Tenant, "u1", App, null, "Is there anything else I should look at?", default);

        Assert.False(a.Grounded);
        Assert.Equal(new[] { 5, 6, 8 }, a.Related!.Select(r => r.Page).ToArray());               // distinct, best first, at most three, weak matches left out
        Assert.Equal("BMW_Flow_Guide.pdf", a.Related![0].Document);
        Assert.Contains("\"Page\":5", db.ConversationMessages.First(m => m.Role == "assistant").CitationsJson);   // kept so a reopened conversation shows them
    }

    [Fact]
    public async Task When_nothing_relevant_was_found_there_is_nothing_to_suggest()
    {
        var (svc, _, chat, store) = Make();
        store.Hits.Add(Hit("Unrelated", page: 3, sim: 0.05));
        var a = await svc.AskAsync(Tenant, "u1", App, null, "How do I bake bread?", default);
        Assert.True(a.Grounded);                         // unrelated: a redirect, not a documentation gap
        Assert.Null(a.Related);
        Assert.Equal(0, chat.Calls);
    }

    // ---- uncited answers: repaired, not thrown away and not shown unchecked ----

    [Theory]
    [InlineData("The next step is BMW Review [Source 1].", "[1]")]
    [InlineData("The next step is BMW Review [1, 2].", "[1][2]")]
    [InlineData("The next step is BMW Review 【1】.", "[1]")]
    [InlineData("The next step is BMW Review 【1†L4-L6】.", "[1]")]
    [InlineData("The next step is BMW Review [sources 1; 2].", "[1][2]")]
    public void Common_citation_styles_are_understood(string raw, string expectedMarkers)
    {
        Assert.Contains(expectedMarkers, ChatService.NormalizeMarkers(raw));
    }

    [Fact]
    public void A_citation_must_still_point_at_a_real_source()
    {
        var hits = new[] { Hit("a", 1), Hit("b", 2) };
        Assert.False(ChatService.Interpret("Fine [Source 7].", hits).Grounded);
        var (answer, grounded, cites) = ChatService.Interpret("Fine [Source 2] and [9].", hits);
        Assert.True(grounded);
        Assert.DoesNotContain("[9]", answer);
        Assert.Equal(2, Assert.Single(cites).Page);
    }

    [Fact]
    public async Task An_answer_written_without_citations_is_repaired_instead_of_being_discarded()
    {
        var (svc, _, chat, store) = Make();
        store.Hits.Add(Hit("After dispensing, the BMW Officer reviews the day on the BMW Review page.", page: 8));
        chat.Replies.Enqueue("After dispensing the next step is the BMW Review page.");                 // good answer, forgot the citation
        chat.Replies.Enqueue("After dispensing the next step is the BMW Review page [1].");              // repaired

        var a = await svc.AskAsync(Tenant, "u1", App, null, "what is the next step after dispensing?", default);

        Assert.True(a.Grounded);
        Assert.Equal(new Citation("BMW_Flow_Guide.pdf", 8), Assert.Single(a.Citations));
        Assert.Equal(2, chat.Calls);
        Assert.Contains("no source citations", chat.AllPrompts[1][^1].Content);                         // the repair request
        Assert.Equal("assistant", chat.AllPrompts[1][^2].Role);                                         // it sees its own draft
    }

    [Fact]
    public async Task Repair_cannot_launder_an_invented_answer()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.Add(Hit("Dispensing: confirm each sticker bag is received.", page: 6));
        chat.Replies.Enqueue("Then you should email the regional director.");                           // invented, uncited
        chat.Replies.Enqueue("NOT_DOCUMENTED");                                                         // the check removes it

        var a = await svc.AskAsync(Tenant, "u1", App, null, "what should I do after dispensing?", default);

        Assert.False(a.Grounded);
        Assert.Equal(ChatService.NotDocumentedAnswer, a.Answer);
        Assert.Equal(2, chat.Calls);
        Assert.NotNull(a.Related);                                                                       // and the user is pointed at the closest sections
        Assert.Contains(db.AiUsageLogs, l => l.Purpose == "rag-citation-repair");
    }

    [Fact]
    public async Task A_repair_that_still_has_no_citation_ends_as_not_documented()
    {
        var (svc, _, chat, store) = Make();
        store.Hits.Add(Hit("Dispensing: confirm each sticker bag is received.", page: 6));
        chat.Replies.Enqueue("You should call the manager.");
        chat.Replies.Enqueue("Yes, call the manager.");
        var a = await svc.AskAsync(Tenant, "u1", App, null, "who do I call after dispensing?", default);
        Assert.False(a.Grounded);
        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task A_properly_cited_answer_costs_one_call_and_an_honest_refusal_is_not_second_guessed()
    {
        var (svc, _, chat, store) = Make();
        store.Hits.Add(Hit("Dispensing: confirm each sticker bag is received.", page: 6));
        chat.Replies.Enqueue("Confirm each bag [1].");
        await svc.AskAsync(Tenant, "u1", App, null, "what is the first thing in dispensing?", default);
        Assert.Equal(1, chat.Calls);

        chat.Replies.Enqueue("NOT_DOCUMENTED");
        await svc.AskAsync(Tenant, "u1", App, null, "what colour is the building?", default);
        Assert.Equal(2, chat.Calls);                                                                    // refusal: no repair pass
    }

    [Fact]
    public async Task Suggestions_are_only_offered_for_genuinely_close_sections_and_clearly_unrelated_questions_never_reach_the_model()
    {
        var (svc, _, chat, store) = Make();
        store.Hits.AddRange(new[] { Hit("Loosely related text one", page: 3, sim: 0.40), Hit("Loosely related text two", page: 4, sim: 0.38) });
        chat.Reply = "NOT_DOCUMENTED";
        var weak = await svc.AskAsync(Tenant, "u1", App, null, "what is the visitor parking fee today", default);   // reaches the model (>= 0.35) but nothing is close enough to suggest
        Assert.True(weak.Grounded);                      // the model refused and nothing is close (< 0.45): unrelated, so a redirect and no suggestions
        Assert.Contains("what can I ask?", weak.Answer);
        Assert.Null(weak.Related);
        Assert.Equal(1, chat.Calls);

        store.Hits.Clear(); store.Hits.Add(Hit("Nothing like it", page: 5, sim: 0.27));
        await svc.AskAsync(Tenant, "u1", App, null, "how many days are in a leap year", default);           // below 0.35: no model call at all
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Plainly_unrelated_questions_get_a_relevant_message_without_any_search_or_model_call()
    {
        var (svc, _, chat, store) = Make();
        store.Hits.Add(Hit("Registering a patient", page: 1, sim: 0.9));   // would match well if it were searched
        foreach (var q in new[] { "what is the weather today", "tell me a joke", "what is the capital of France" })
        {
            var a = await svc.AskAsync(Tenant, "u1", App, null, q, default);
            Assert.True(a.Grounded);                                       // answered, so it is not listed as a documentation gap
            Assert.Empty(a.Citations);
            Assert.Contains("what can I ask?", a.Answer);
        }
        Assert.Equal(0, chat.Calls);
    }
}