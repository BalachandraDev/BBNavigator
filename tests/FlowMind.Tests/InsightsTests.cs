using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using FlowMind.Infrastructure.Insights;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowMind.Tests;

/// <summary>Unanswered-question report, period report and the support inbox, built from the chats that are already stored.</summary>
public class InsightsTests
{
    private sealed class Scoped : ITenantContext { public Guid? TenantId { get; } public Scoped(Guid t) => TenantId = t; }

    private readonly Guid _tenant = Guid.NewGuid(), _app = Guid.NewGuid();
    private readonly FlowMindDbContext _db;
    private readonly BotLink _link;
    private string LinkOwner(string visitor) => $"bot:{_link.Id:N}:{visitor}";

    public InsightsTests()
    {
        _db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Scoped(_tenant));
        _db.Applications.Add(new TenantApplication { Id = _app, TenantId = _tenant, Name = "HMIS" });
        _link = new BotLink { TenantId = _tenant, ApplicationId = _app, Name = "Hospital site", Title = "Help", TokenHash = Guid.NewGuid().ToString("N") };
        _db.BotLinks.Add(_link);
        _db.SaveChanges();
    }

    private InsightsService Svc() => new(_db, new Scoped(_tenant));
    private SupportService Support() => new(_db);

    /// <summary>One question and its stored answer, as the chat service stores them.</summary>
    private Guid Ask(string owner, string question, bool grounded, DateTime? at = null, string? citations = null, Guid? conversation = null)
    {
        var when = at ?? DateTime.UtcNow.AddMinutes(-5);
        var convo = conversation == null ? null : _db.Conversations.Find(conversation);
        if (convo == null)
        {
            convo = new Conversation { TenantId = _tenant, ApplicationId = _app, OwnerId = owner, Title = question };
            _db.Conversations.Add(convo);
        }
        _db.ConversationMessages.Add(new ConversationMessage { TenantId = _tenant, ConversationId = convo.Id, Role = "user", Content = question, CreatedUtc = when });
        _db.ConversationMessages.Add(new ConversationMessage { TenantId = _tenant, ConversationId = convo.Id, Role = "assistant", Content = grounded ? "answer" : "not documented", Grounded = grounded, CitationsJson = citations, CreatedUtc = when });
        _db.SaveChanges();
        return convo.Id;
    }

    private const string Near = "[{\"Document\":\"discharge-guide.pdf\",\"Page\":3}]";
    private const string Near2 = "[{\"Document\":\"bed-board.pdf\",\"Page\":1}]";

    [Fact]
    public async Task Unanswered_questions_are_grouped_by_meaning_masked_and_point_to_the_closest_section()
    {
        Ask(LinkOwner("visitor-1"), "How do I print the discharge summary?", false, citations: Near);
        Ask("key:abc", "how do i print the DISCHARGE summary", false, citations: Near);
        Ask(LinkOwner("visitor-2"), "Where is bed 12 for patient 9876543210 and mail me at nurse@hospital.org?", false, citations: Near2);
        Ask(LinkOwner("visitor-3"), "Can u suggest one song", false);              // nothing in the documents comes close: not about this product, so not a gap
        Ask(LinkOwner("visitor-1"), "Is the full name mandatory?", true);          // answered: not a gap
        Ask(LinkOwner("visitor-1"), "hello", true);                                // small talk: not a question at all

        var r = await Svc().GapsAsync(_app, 30, default);

        Assert.Equal(2, r.Items.Count);
        Assert.Equal(2, r.Open);
        var first = r.Items[0];
        Assert.Equal(2, first.Count);                                              // same question, two wordings, two channels
        Assert.Contains("Hospital site", first.Channels); Assert.Contains("Signed-in users", first.Channels);
        Assert.Equal("discharge-guide.pdf", first.Closest[0].Document); Assert.Equal(3, first.Closest[0].Page); Assert.Equal(2, first.Closest[0].Times);
        Assert.Equal("open", first.State);

        var masked = r.Items[1].Question;
        Assert.Contains("[number]", masked); Assert.Contains("[email]", masked);
        Assert.DoesNotContain("9876543210", masked); Assert.DoesNotContain("nurse@hospital.org", masked);
        Assert.Contains("bed 12", masked);                                          // short numbers are ordinary words
        Assert.Equal("bed-board.pdf", r.Items[1].Closest[0].Document);
    }

    [Fact]
    public async Task A_question_typed_in_capitals_is_grouped_but_never_shown_in_capitals()
    {
        Ask(LinkOwner("v1"), "How do I cancel a bill?", false, DateTime.UtcNow.AddMinutes(-20), Near);
        Ask(LinkOwner("v2"), "HOW DO I CANCEL A BILL?", false, DateTime.UtcNow.AddMinutes(-5), Near);      // the newest wording, but shouting
        var item = (await Svc().GapsAsync(_app, 30, default)).Items.Single();
        Assert.Equal(2, item.Count);
        Assert.Equal("How do I cancel a bill?", item.Question);
    }

    [Fact]
    public async Task A_handled_question_stays_closed_until_it_is_asked_again_and_a_deleted_chat_leaves_the_report()
    {
        var chat = Ask(LinkOwner("visitor-1"), "How do I cancel a bill?", false, DateTime.UtcNow.AddHours(-2), Near);
        var key = (await Svc().GapsAsync(_app, 30, default)).Items[0].Key;

        Assert.False(await Svc().SetGapStateAsync(_app, "not-a-key", "handled", default));
        Assert.False(await Svc().SetGapStateAsync(_app, key, "wrong", default));
        Assert.True(await Svc().SetGapStateAsync(_app, key, "handled", default));
        var r = await Svc().GapsAsync(_app, 30, default);
        Assert.Equal("handled", r.Items[0].State); Assert.Equal(0, r.Open); Assert.Equal(1, r.Handled);
        Assert.DoesNotContain(_db.QuestionGapStatuses.ToList(), s => s.Key.Contains("cancel"));   // only a hash is stored, never the text

        _db.QuestionGapStatuses.Single().UpdatedUtc = DateTime.UtcNow.AddHours(-1); _db.SaveChanges();   // "handled an hour ago"
        Ask(LinkOwner("visitor-9"), "How do I cancel a bill?", false, DateTime.UtcNow.AddMinutes(-30), Near);   // asked again after that
        r = await Svc().GapsAsync(_app, 30, default);
        Assert.Equal("open", r.Items[0].State); Assert.True(r.Items[0].Reopened); Assert.Equal(2, r.Items[0].Count);

        Assert.True(await Svc().SetGapStateAsync(_app, key, "ignored", default));
        Assert.Equal(1, (await Svc().GapsAsync(_app, 30, default)).Ignored);
        Assert.True(await Svc().SetGapStateAsync(_app, key, "open", default));
        Assert.Empty(_db.QuestionGapStatuses);

        // The visitor deletes their chat: the question is gone from the report with it.
        _db.ConversationMessages.RemoveRange(_db.ConversationMessages.Where(m => m.ConversationId == chat));
        _db.Conversations.RemoveRange(_db.Conversations.Where(c => c.Id == chat)); _db.SaveChanges();
        Assert.Equal(1, (await Svc().GapsAsync(_app, 30, default)).Items.Single().Count);
    }

    [Fact]
    public async Task Questions_older_than_the_period_and_other_applications_are_left_out()
    {
        Ask(LinkOwner("v1"), "Old question about refunds?", false, DateTime.UtcNow.AddDays(-45), Near);
        var other = new Conversation { TenantId = _tenant, ApplicationId = Guid.NewGuid(), OwnerId = "key:x", Title = "q" };
        _db.Conversations.Add(other);
        _db.ConversationMessages.Add(new ConversationMessage { TenantId = _tenant, ConversationId = other.Id, Role = "user", Content = "Question about another product?" });
        _db.ConversationMessages.Add(new ConversationMessage { TenantId = _tenant, ConversationId = other.Id, Role = "assistant", Content = "no", Grounded = false });
        _db.SaveChanges();

        Assert.Empty((await Svc().GapsAsync(_app, 30, default)).Items);
        Assert.Single((await Svc().GapsAsync(_app, 60, default)).Items);
    }

    [Theory]
    [InlineData("call 98765 43210 now", "call [number] now")]
    [InlineData("UHID 20240001234 please", "UHID [number] please")]
    [InlineData("mail a.b+c@x-y.co.in today", "mail [email] today")]
    [InlineData("admitted on 12-11-2025", "admitted on 12-11-2025")]
    [InlineData("step 1 2 3 then 45", "step 1 2 3 then 45")]
    public void Phone_numbers_ids_and_emails_are_masked_but_dates_and_short_numbers_are_kept(string input, string expected) =>
        Assert.Equal(expected, PersonalData.Mask(input));

    [Fact]
    public async Task The_period_report_counts_answers_gaps_top_questions_channels_and_knowledge_changes()
    {
        static DateTime Day(int back) => DateTime.UtcNow.Date.AddDays(-back).AddHours(12);   // midday: never straddles a date boundary
        for (var i = 0; i < 3; i++) Ask(LinkOwner("v" + i), "How do I register a patient?", true, Day(1).AddMinutes(i));
        Ask("key:abc", "Which roles can approve records?", true, Day(2));
        Ask(LinkOwner("v1"), "How do I print the discharge summary?", false, Day(1), Near);
        Ask(LinkOwner("v1"), "வணக்கம்", true);                                      // greeting: not counted
        Ask(LinkOwner("v1"), "நோயாளியை எப்படிப் பதிவு செய்வது?", true, Day(3));
        Ask(LinkOwner("v7"), "Something asked in the previous period?", true, Day(40));

        var doc = new Document { TenantId = _tenant, ApplicationId = _app, FileName = "guide.pdf", ContentType = "application/pdf" };
        _db.Documents.Add(doc);
        _db.AuditLogs.Add(new AuditLog { TenantId = _tenant, Action = "document.upload", EntityId = doc.Id.ToString() });
        _db.AuditLogs.Add(new AuditLog { TenantId = _tenant, Action = "document.update", EntityId = doc.Id.ToString() });
        _db.AuditLogs.Add(new AuditLog { TenantId = _tenant, Action = "document.update", EntityId = Guid.NewGuid().ToString() });   // some other application's document
        _db.AuditLogs.Add(new AuditLog { TenantId = Guid.NewGuid(), Action = "document.update", EntityId = doc.Id.ToString() });       // another company
        _db.SupportRequests.Add(new SupportRequest { TenantId = _tenant, ApplicationId = _app, Channel = "x", Question = "q", SenderHash = "h", Status = "open" });
        _db.SaveChanges();

        var r = (await Svc().UsageAsync(_app, 30, default))!;

        Assert.Equal("HMIS", r.Application);
        Assert.Equal(6, r.Questions); Assert.Equal(5, r.Answered); Assert.Equal(1, r.NotDocumented); Assert.Equal(83, r.AnswerRatePercent);
        Assert.Equal(1, r.PreviousQuestions);
        Assert.Equal(30, r.Daily.Count); Assert.Equal(6, r.Daily.Sum(d => d.Questions)); Assert.Equal(3, r.ActiveDays);
        Assert.Equal(1, r.TamilQuestions);
        Assert.Equal("How do I register a patient?", r.TopQuestions[0].Question); Assert.Equal(3, r.TopQuestions[0].Count); Assert.True(r.TopQuestions[0].AllAnswered);
        Assert.Equal("Hospital site", r.Channels[0].Name); Assert.Equal(5, r.Channels[0].Questions);
        Assert.Equal(1, r.OpenGaps); Assert.Equal(1, r.SupportOpenNow); Assert.Equal(1, r.SupportReceived);
        Assert.Equal(2, r.DocumentsChanged);                                         // this application's upload + update, in this company only
        Assert.False(r.Truncated);
        Assert.Null(await Svc().UsageAsync(Guid.NewGuid(), 30, default));
    }

    // ---- support inbox ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_support_request_takes_its_question_from_the_senders_own_chat_not_from_the_sender()
    {
        var owner = LinkOwner("visitor-1");
        var chat = Ask(owner, "How do I print the discharge summary?", false);

        var r = await Support().CreateAsync(_tenant, "Hospital site", owner, chat, "  Need it\u0007 today \n please ", " 98765 43210 ", default);

        Assert.Equal("How do I print the discharge summary?", r.Question);
        Assert.False(r.WasAnswered);
        Assert.Equal(_app, r.ApplicationId); Assert.Equal("open", r.Status);
        Assert.Equal("Need it today \n please", r.Note); Assert.Equal("98765 43210", r.Contact);
        Assert.Equal(16, r.SenderHash.Length); Assert.DoesNotContain("visitor-1", r.SenderHash);

        var others = await Assert.ThrowsAsync<SupportRejectedException>(() => Support().CreateAsync(_tenant, "x", LinkOwner("someone-else"), chat, null, null, default));
        Assert.Equal(404, others.StatusCode);                                        // somebody else's chat cannot be escalated
        var missing = await Assert.ThrowsAsync<SupportRejectedException>(() => Support().CreateAsync(_tenant, "x", owner, Guid.NewGuid(), null, null, default));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task Pressing_the_button_twice_sends_one_request_and_one_sender_is_capped_per_day()
    {
        var owner = LinkOwner("visitor-1");
        var chat = Ask(owner, "Question one?", true);
        var a = await Support().CreateAsync(_tenant, "site", owner, chat, null, null, default);
        var b = await Support().CreateAsync(_tenant, "site", owner, chat, "again", null, default);
        Assert.Equal(a.Id, b.Id); Assert.Equal(1, _db.SupportRequests.Count());
        Assert.True(a.WasAnswered);                                                  // the bot did answer; the user wanted a person anyway

        for (var i = 2; i <= SupportService.DailyPerSender; i++) { Ask(owner, "Question " + i + "?", false, conversation: chat); await Support().CreateAsync(_tenant, "site", owner, chat, null, null, default); }
        Ask(owner, "One too many?", false, conversation: chat);
        var capped = await Assert.ThrowsAsync<SupportRejectedException>(() => Support().CreateAsync(_tenant, "site", owner, chat, null, null, default));
        Assert.Equal(429, capped.StatusCode);

        var other = await Support().CreateAsync(_tenant, "site", LinkOwner("visitor-2"), Ask(LinkOwner("visitor-2"), "Mine?", false), null, null, default);
        Assert.NotEqual(a.Id, other.Id);                                             // another visitor is not affected
    }
}
