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
using Xunit.Abstractions;

namespace FlowMind.Tests;

public class RequestCondenserTests
{
    private static string C(string s, int max = 400) => RequestCondenser.Condense(s, max).Text;

    [Fact]
    public void A_normal_short_question_is_left_exactly_as_asked()
    {
        var r = RequestCondenser.Condense("What validations apply on the registration page?");
        Assert.Equal("What validations apply on the registration page?", r.Text);
        Assert.False(r.Shortened);
    }

    [Fact]
    public void Greetings_politeness_and_thanks_are_removed_but_the_ask_is_kept()
    {
        Assert.Equal("Explain how the discharge date validation works?", C("Hi team, could you please explain how the discharge date validation works? Thanks in advance!"));
        Assert.Equal("What is the maximum discount?", C("Hello,\nI was wondering, what is the maximum discount?\nThank you,\nPriya"));
    }

    [Fact]
    public void Meaning_carrying_words_numbers_and_identifiers_are_never_dropped()
    {
        Assert.Equal("Why can't the user edit record PAT-2041 after 24 hours? It is not allowed.", C("Why can't the user edit record PAT-2041 after 24 hours? It is not allowed."));
        Assert.Equal("Can I edit a record after discharge?", C("Can I edit a record after discharge?"));   // permission, not a polite request
        Assert.Equal("Explain the discharge flow", C("Explain the discharge flow"));
        Assert.Equal("¿Cuál es el límite de descuento?", C("¿Cuál es el límite de descuento?"));
    }

    [Fact]
    public void A_long_rambling_email_is_reduced_to_the_actual_question()
    {
        var mail = string.Join("\n", new[]
        {
            "Hi support team,", "",
            "I hope you are doing well. We have been using the billing module for about three months now and the finance team has some questions.",
            "Last week one of our users got a very strange message while processing an invoice and we are not sure why it happened.",
            "Which roles can approve a discount over 20 percent?",
            "We had a long internal discussion about this and nobody could agree on who is allowed to do it in the system.",
            "> On Mon, 3 Nov, Raj wrote:", "> I think the manager can do it but I am not sure at all", "> maybe ask support",
            "I also wanted to mention that the quarterly report was late again this time and management is unhappy about it.",
            "Which roles can approve a discount over 20 percent?",
            "Thanks a lot,", "Best regards,", "John", "Sent from my iPhone",
        });
        var r = RequestCondenser.Condense(mail);

        Assert.True(r.Text.Length <= 400);
        Assert.Contains("Which roles can approve a discount over 20 percent?", r.Text);
        Assert.DoesNotContain("Best regards", r.Text);
        Assert.DoesNotContain("not sure at all", r.Text);                       // quoted reply
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(r.Text, "Which roles can approve").Count);   // repeated sentence collapsed
        Assert.True(r.Shortened);
        Assert.True(r.Text.Length < mail.Length / 3);
    }

    [Fact]
    public void A_pasted_log_blob_does_not_crowd_out_the_question()
    {
        var blob = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"ERR{i}:0x{i:X4}"));
        var r = RequestCondenser.Condense("Why do I get this error? " + blob + " What does it mean for the billing module?");
        Assert.True(r.Text.Length <= 400);
        Assert.Contains("Why do I get this error?", r.Text);
        Assert.Contains("billing module", r.Text);
    }

    [Fact]
    public void Nothing_is_ever_reduced_to_an_empty_question()
    {
        Assert.Equal("thanks", C("thanks"));
        Assert.Equal("ok", C("ok"));
        Assert.NotEmpty(C(new string('x', 5000)));
        Assert.Equal("", C("   "));
    }

    [Theory]
    [InlineData("Hi, could you please tell me what validations apply on the discharge page? Thanks!")]
    [InlineData("Which roles can approve a discount over 20 percent? Which roles can approve a discount over 20 percent?")]
    [InlineData("so basically I'd like to know about the refund rules. Regards, Sam")]
    public void Condensing_twice_gives_the_same_result(string input)
    {
        var once = C(input);
        Assert.Equal(once, C(once));
    }

    [Fact]
    public void Control_characters_and_hidden_text_are_removed()
    {
        Assert.Equal("What is the limit?", C("What​ is\u0007 the limit?"));
    }
}

public class SourcePackerTests
{
    private static ChunkHit Hit(string text, string doc = "guide.pdf", int page = 1, double sim = 0.6) => new(Guid.NewGuid(), Guid.NewGuid(), doc, page, text, 0.03, sim);
    private static string Para(string tag, int lines) => string.Join("\n", Enumerable.Range(1, lines).Select(i => $"{tag} line {i}: some descriptive sentence about the application behaviour here."));

    [Fact]
    public void Excerpts_are_kept_whole_in_rank_order_and_stay_inside_the_budget()
    {
        var hits = Enumerable.Range(1, 6).Select(i => Hit(Para("Alpha" + i, 18), page: i)).ToList();   // ~1,400 characters each
        var packed = SourcePacker.Pack(hits, totalChars: 4500, eachChars: 1600);

        Assert.True(packed.Sum(p => p.Text.Length) <= 4500);
        Assert.Equal(new[] { 1, 2, 3 }, packed.Select(p => p.Hit.PageNumber).ToArray());          // best first; the 4th would only fit as a sliver, so it is left out
        Assert.All(packed, p => Assert.Equal(p.Hit.Content, p.Text));                              // nothing removed from inside an excerpt
    }

    /// <summary>
    /// The real failure: a 35-line overview table whose rows for "BMW Review" and "Handover" do not repeat the question's words
    /// ("generated stickers and verified the bags... what next?"). Picking only matching lines deleted them.
    /// </summary>
    [Fact]
    public void A_table_split_across_many_short_lines_is_never_thinned_out()
    {
        var table = string.Join("\n", new[]
        {
            "BMW module - flow guide Page 2", "1. The life of one day of waste (end to end)",
            "Waste is generated in locations, collected in coloured bags, counted, stickered, received and weighed at",
            "the storage point, reviewed and finalized as a daily report, handed to the CBWTF.",
            "Ste", "p", "Page", "Who (typical)", "What happens", "Result",
            "1 Collection and", "Stickers", "Collector / ward", "staff", "Count bags per waste colour for a location and",
            "day, confirm counts, generate and print one", "sticker per bag.", "Bags with unique sticker", "numbers (status 3)",
            "2 Dispensing", "Storage /", "weighing staff", "Confirm each sticker bag is received, then enter", "weights (colour totals or bag by bag).",
            "Bags received (5) and", "weighed (7)",
            "3 BMW Review", "BMW Officer /", "reviewer", "Check the day, raise or close exceptions, Mark", "as reviewed, then Finalize report.",
            "4 Handover Register", "Store / BMW staff", "Hand finalized bags to the CBWTF, record acknowledgement.",
        });
        var packed = SourcePacker.Pack(new[] { Hit(table, page: 2) }, 6000, 1600);

        var text = packed.Single().Text;
        Assert.Contains("3 BMW Review", text);                      // the answer to "what comes next"
        Assert.Contains("4 Handover Register", text);
        Assert.Contains("2 Dispensing", text);
        Assert.Equal(table.Split('\n').Length, text.Split('\n').Length);
    }

    [Fact]
    public void Only_the_last_excerpt_can_be_cut_and_only_at_the_end_of_a_line()
    {
        var first = Hit(Para("First", 12), page: 1);                 // ~950 characters
        var second = Hit(Para("Second", 18), page: 2);                // ~1,400 characters
        var packed = SourcePacker.Pack(new[] { first, second }, totalChars: 2000, eachChars: 1600);

        Assert.Equal(2, packed.Count);
        Assert.Equal(first.Content, packed[0].Text);
        var kept = packed[1].Text.Split('\n');
        Assert.True(kept.Length < 18);
        Assert.Equal(second.Content.Split('\n').Take(kept.Length), kept);   // a clean prefix of whole lines
        Assert.True(packed.Sum(p => p.Text.Length) <= 2000);
    }

    [Fact]
    public void Lines_repeated_by_chunk_overlap_are_sent_only_once()
    {
        var shared = "The discharge summary can only be printed after the discharge date is saved.";
        var a = Hit("Page intro text for chunk A that is long enough to matter.\n" + shared, page: 1);
        var b = Hit(shared + "\nNext paragraph that starts chunk B and has fresh information in it.", page: 2);
        var packed = SourcePacker.Pack(new[] { a, b }, 4500, 1600);

        Assert.Equal(2, packed.Count);
        Assert.Contains(shared, packed[0].Text);
        Assert.DoesNotContain(shared, packed[1].Text);
        Assert.Contains("fresh information", packed[1].Text);
    }

    [Fact]
    public void There_is_always_at_least_one_source_even_when_a_single_line_is_huge()
    {
        var packed = SourcePacker.Pack(new[] { Hit(new string('z', 5000)) }, 4500, 1000);
        Assert.Single(packed);
        Assert.InRange(packed[0].Text.Length, 1, 1001);
    }

    [Fact]
    public void Stop_words_are_not_treated_as_content_terms()
    {
        var terms = SourcePacker.Terms("What is the maximum discount for the billing page?");
        Assert.Contains("maximum", terms); Assert.Contains("discount", terms); Assert.Contains("billing", terms);
        Assert.DoesNotContain("the", terms); Assert.DoesNotContain("what", terms); Assert.DoesNotContain("page", terms);
    }
}
/// <summary>The whole layer working through ChatService: what actually reaches the model.</summary>
public class TokenSavingChatTests
{
    private readonly ITestOutputHelper _out;
    public TokenSavingChatTests(ITestOutputHelper output) => _out = output;

    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private sealed class FakeEmbedder : IEmbeddingProvider
    {
        public string Name => "cloudflare"; public int Dimensions => 4;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> t, CancellationToken ct) => Task.FromResult<IReadOnlyList<float[]>>(t.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
    }
    private sealed class StubStore : IVectorStore
    {
        public List<ChunkHit> Hits = new(); public string? LastQuery;
        public Task<Dictionary<int, string>> GetIndexedHashesAsync(Guid t, Guid v, CancellationToken ct) => Task.FromResult(new Dictionary<int, string>());
        public Task UpsertAsync(Guid t, IReadOnlyList<ChunkRecord> c, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteVersionChunksNotInAsync(Guid t, Guid v, IReadOnlyCollection<int> k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteOtherVersionsAsync(Guid t, Guid d, Guid k, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteDocumentAsync(Guid t, Guid d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteTenantAsync(Guid t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ChunkHit>> SearchAsync(Guid t, Guid a, string q, float[] e, int k, CancellationToken ct) { LastQuery = q; return Task.FromResult<IReadOnlyList<ChunkHit>>(Hits); }
    }
    private sealed class FakeChat : IChatProvider
    {
        public string Name => "groq"; public int Calls; public List<ChatMessageDto> Last = new(); public string Reply = "Discount above 20 percent needs the Billing Manager [1].";
        public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessageDto> m, int max, CancellationToken ct) { Calls++; Last = m.ToList(); return Task.FromResult(new ChatCompletionResult(Reply, 100, 20)); }
    }

    private static readonly Guid Tenant = Guid.NewGuid(), App = Guid.NewGuid();
    private static ChunkHit Hit(string text, int page = 1) => new(Guid.NewGuid(), Guid.NewGuid(), "billing.pdf", page, text, 0.03, 0.7);
    private static string Para(string tag, int lines) => string.Join("\n", Enumerable.Range(1, lines).Select(i => $"{tag} line {i}: descriptive sentence about how the billing module behaves in daily use."));

    private static (ChatService Svc, FlowMindDbContext Db, FakeChat Chat, StubStore Store) Make()
    {
        var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var providers = Options.Create(new AiProvidersOptions { Groq = new GroqOptions { DailyRequestLimit = 1000 }, Cloudflare = new CloudflareOptions { DailyNeuronLimit = 100000, Dimensions = 4 } });
        var usage = new AiUsageService(db, providers); var store = new StubStore(); var chat = new FakeChat();
        var retrieval = new RetrievalService(new EmbeddingService(new FakeEmbedder(), usage, providers), store, Options.Create(new RetrievalOptions()));
        return (new ChatService(db, retrieval, chat, usage, Options.Create(new ChatOptions()), providers, NullLogger<ChatService>.Instance), db, chat, store);
    }

    private const string Rambling = """
        Hi support team,
        I hope you are doing well. We have been using the billing module for about three months and the finance team has several questions about it.
        Last week one of our users got a strange message while processing an invoice and we are not sure why it happened or whether it matters.
        Who approves a discount over 20 percent?
        We had a long internal discussion about this and nobody could agree on who is allowed to do it, which is why we are writing to you today.
        > On Mon Raj wrote: I think the manager can do it but I am not sure at all, maybe ask support or look at the old manual we had.
        The quarterly report was late again and management is unhappy about it, but that is a separate matter we will raise with our own team.
        Who approves a discount over 20 percent?
        Thanks a lot,
        Best regards,
        John
        """;

    private static int Chars(IEnumerable<ChatMessageDto> m) => m.Sum(x => x.Content.Length);

    [Fact]
    public async Task A_long_rambling_message_reaches_the_model_as_a_short_question_with_trimmed_sources()
    {
        var (svc, _, chat, store) = Make();
        var answerChunk = Para("Billing overview", 6) + "\nDiscount above 20 percent needs approval from the Billing Manager role.\n" + Para("Billing notes", 6);
        store.Hits.Add(Hit(answerChunk, page: 1));
        store.Hits.AddRange(Enumerable.Range(2, 5).Select(i => Hit(Para("Section" + i, 14), page: i)));   // five more ~1,000-character background excerpts
        var naive = Rambling.Length + store.Hits.Sum(h => h.Content.Length) + ChatService.SystemPrompt.Length;   // what was sent before the layer

        var a = await svc.AskAsync(Tenant, "u1", App, null, Rambling, default);
        var sent = Chars(chat.Last);

        _out.WriteLine($"before the layer ≈ {naive} chars (~{naive / 4} tokens); after ≈ {sent} chars (~{sent / 4} tokens); saved {100 - 100 * sent / naive}%");
        Assert.True(sent < naive * 0.85, $"expected the rambling message and the excerpt budget to save at least ~15%, sent {sent} of {naive}");
        Assert.Contains("Who approves a discount over 20 percent?", chat.Last[^1].Content);
        Assert.DoesNotContain("quarterly report", chat.Last[^1].Content);
        Assert.DoesNotContain("Best regards", chat.Last[^1].Content);
        Assert.Contains("Discount above 20 percent needs approval", chat.Last[^1].Content);   // the answer-bearing line is still sent
        Assert.True(a.Grounded);                                    // still answered and cited
        Assert.EndsWith("Who approves a discount over 20 percent?", a.Understood);   // the question, plus the short sentence before it as context
        Assert.True(a.Understood!.Length < 250);
    }

    [Fact]
    public async Task A_short_question_is_not_reported_as_condensed()
    {
        var (svc, _, chat, store) = Make();
        store.Hits.Add(Hit(Para("S", 3)));
        var a = await svc.AskAsync(Tenant, "u1", App, null, "Who approves a discount?", default);
        Assert.Null(a.Understood);
        Assert.Contains("QUESTION: Who approves a discount?", chat.Last[^1].Content);
        Assert.EndsWith("(Cite each statement as [n].)", chat.Last[^1].Content);
    }

    [Fact]
    public async Task Very_long_messages_are_accepted_and_the_stored_conversation_keeps_what_the_user_wrote()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.Add(Hit(Para("S", 3)));
        var huge = Rambling + "\n" + string.Concat(Enumerable.Repeat("Some more background text that nobody needs. ", 120));   // ~5,400 chars
        await svc.AskAsync(Tenant, "u1", App, null, huge, default);

        Assert.True(Chars(chat.Last) < 3300);   // the ~5,400-char message was condensed; the rest is the fixed rules plus the excerpts (rules grew by one short paragraph: no storage words)
        Assert.StartsWith("Hi support team", db.ConversationMessages.First(m => m.Role == "user").Content);
    }

    [Fact]
    public async Task A_self_contained_question_sends_no_history_but_a_follow_up_sends_a_shortened_one()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.Add(Hit(Para("S", 4)));
        chat.Reply = "The Billing Manager role approves discounts above 20 percent and can also issue refunds for any invoice in the system, as described in the billing module documentation for finance staff [1].";
        var first = await svc.AskAsync(Tenant, "u1", App, null, "Which role approves a discount above twenty percent in the billing module?", default);

        await svc.AskAsync(Tenant, "u1", App, first.ConversationId, "Which role is allowed to issue refunds for invoices in the billing module?", default);   // long, self-contained
        Assert.Equal(new[] { "system", "user" }, chat.Last.Select(m => m.Role));

        await svc.AskAsync(Tenant, "u1", App, first.ConversationId, "and who can refund it?", default);                  // follow-up
        Assert.Contains(chat.Last, m => m.Role == "assistant" && m.Content.Length <= new ChatOptions().HistoryAssistantChars + 1);
        Assert.DoesNotContain(chat.Last, m => m.Content.Contains("[1]") && m.Role == "assistant");
        _ = db;
    }

    [Fact]
    public async Task Asking_the_same_thing_again_reuses_the_answer_until_the_documents_change()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.Add(Hit(Para("S", 3)));

        var one = await svc.AskAsync(Tenant, "u1", App, null, "Who approves a discount?", default);
        var two = await svc.AskAsync(Tenant, "u2", App, null, "who  approves a DISCOUNT?", default);   // another user, different spacing/case
        Assert.Equal(1, chat.Calls);
        Assert.Equal(one.Answer, two.Answer);
        Assert.Equal(one.Citations, two.Citations);
        Assert.Contains(db.AiUsageLogs, l => l.ProviderKind == AiProviderKind.Chat && l.Status == AiRequestStatus.Cached);

        store.Hits[0] = Hit("A completely different paragraph about discount approval rules that was edited later on.");
        await svc.AskAsync(Tenant, "u1", App, null, "Who approves a discount?", default);
        Assert.Equal(2, chat.Calls);                                // the excerpts changed, so the old answer is not reused
    }

    [Fact]
    public async Task Cached_answers_never_cross_companies()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.Add(Hit(Para("S", 3)));
        await svc.AskAsync(Tenant, "u1", App, null, "Who approves a discount?", default);
        await svc.AskAsync(Guid.NewGuid(), "u9", Guid.NewGuid(), null, "Who approves a discount?", default);
        Assert.Equal(2, chat.Calls);
        Assert.Equal(2, db.ChatCache.Count());
    }

    [Fact]
    public async Task Invented_answers_are_not_cached()
    {
        var (svc, db, chat, store) = Make();
        store.Hits.Add(Hit(Para("S", 3)));
        chat.Reply = "It is sent by SMS.";                         // no citation → replaced by the fixed reply, and never cached
        await svc.AskAsync(Tenant, "u1", App, null, "How is it sent?", default);
        Assert.Empty(db.ChatCache);
    }
}
