using FlowMind.Application.Interfaces;
using FlowMind.Domain.Entities;
using FlowMind.Domain.Enums;
using FlowMind.Infrastructure.Chat;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowMind.Tests;

/// <summary>
/// "I did X, what next?" must name the step after X in the reviewed workflow, decided by the server and not left to the model.
/// Shapes below mirror a real application with several overlapping workflows (a main flow, a longer variant and small sub-flows).
/// </summary>
public class WorkflowNavigationTests
{
    private sealed class Unscoped : ITenantContext { public Guid? TenantId => null; }
    private static readonly Guid Tenant = Guid.NewGuid(), App = Guid.NewGuid();

    private static FlowMindDbContext SeedBmw()
    {
        var db = new FlowMindDbContext(new DbContextOptionsBuilder<FlowMindDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new Unscoped());
        var doc = new Document { TenantId = Tenant, ApplicationId = App, FileName = "BMW_Role_and_Page_Flow_Guide.pdf", ContentType = "application/pdf", Status = DocumentStatus.Approved };
        var ver = new DocumentVersion { TenantId = Tenant, DocumentId = doc.Id, VersionNumber = 1, ContentHash = "h", StoragePath = "p" };
        doc.CurrentVersionId = ver.Id; db.Documents.Add(doc); db.DocumentVersions.Add(ver);

        var pages = new Dictionary<string, AppPage>(StringComparer.OrdinalIgnoreCase);
        AppPage Page(string name) { if (!pages.TryGetValue(name, out var p)) { p = new AppPage { TenantId = Tenant, ApplicationId = App, Name = name, NormalizedName = name.ToLowerInvariant(), Review = ReviewStatus.Approved }; pages[name] = p; db.AppPages.Add(p); } return p; }
        void Flow(string name, int sourcePage, params (string Step, string? Page, string Desc)[] steps)
        {
            var wf = new Workflow { TenantId = Tenant, ApplicationId = App, Name = name, NormalizedName = name.ToLowerInvariant(), Review = ReviewStatus.Approved, SourceDocumentVersionId = ver.Id, SourcePage = sourcePage };
            db.Workflows.Add(wf);
            for (int i = 0; i < steps.Length; i++)
                db.WorkflowSteps.Add(new WorkflowStep
                {
                    TenantId = Tenant, ApplicationId = App, WorkflowId = wf.Id, Name = steps[i].Step, NormalizedName = steps[i].Step.ToLowerInvariant(), Order = i + 1,
                    AppPageId = steps[i].Page == null ? null : Page(steps[i].Page!).Id, Description = steps[i].Desc, Review = ReviewStatus.Approved
                });
        }

        Flow("Biomedical Waste Management Flow", 18,
            ("Collection and Stickers", "Collection and Stickers", "Count waste, confirm, and generate stickers."), ("Dispensing", "Dispensing", "Receive and weigh bags."),
            ("BMW Review", "BMW Review", "Perform checks, resolve exceptions, and finalize."), ("Handover Register", "Handover Register", "Hand over bags to CBWTF."),
            ("Corporate Dashboard", "Corporate Dashboard", "Submit report to corporate."));
        Flow("Daily BMW Workflow", 3,
            ("BMW Masters", "BMW Masters", "Set up waste categories and vendors."), ("Collection and Stickers", "Collection and Stickers", "Choose location and date, count bags, confirm counts, and print stickers."),
            ("Dispensing", "Dispensing", "Confirm receipt of stickered bags and enter weights."), ("BMW Review", "BMW Review", "Perform checks, review and finalize report."),
            ("Handover Register", "Handover Register", "Hand over bags to CBWTF."), ("Corporate Dashboard", "Corporate Dashboard", "View finalized days and submit to corporate."));
        Flow("Daily Waste Life Cycle", 2,
            ("Collection and Stickers", "Collection and Stickers", "Count bags and generate stickers."), ("Dispensing", "Dispensing (receive and weigh)", "Receive and weigh bags."),
            ("BMW Review", "BMW Review", "Check day and finalize report."), ("Handover", "Handover Register", "Hand bags to CBWTF."), ("Corporate Submission", "Corporate Dashboard", "Submit finalized report."));
        Flow("Dispensing Workflow", 7,
            ("Open Dispensing", "Dispensing", "Open the page."), ("Receive", "Dispensing", "Confirm bags."), ("Weigh", "Dispensing", "Enter weights."), ("Save", "Dispensing", "Save weights."));
        Flow("Handover Process", 12, ("Create handover", "Handover Register", "New handover."), ("Record acknowledgement", "Handover Register", "Record it."), ("Confirm disposal", "Handover Register", "Confirm."));
        db.SaveChanges();
        return db;
    }

    private static async Task<string> Outline(FlowMindDbContext db, string question) =>
        (await WorkflowOutline.BuildAsync(db, App, question, default))?.Text ?? "";

    [Theory]
    [InlineData("sticker collected what next?")]                    // the exact wording that was answered wrongly
    [InlineData("after collection sticker which process")]
    [InlineData("I have counted and confirmed the stickers, what do I do next?")]
    [InlineData("stickers are generated, then what")]
    public async Task After_the_stickers_the_next_step_is_Dispensing_never_Handover(string question)
    {
        var text = await Outline(SeedBmw(), question);
        var line = text.Split('\n').First(l => l.StartsWith("NEXT STEP"));
        Assert.Contains("\"Dispensing\"", line);
        Assert.DoesNotContain("Handover", line);
        Assert.StartsWith("NEXT STEP:", line);                      // every workflow agrees, so no "differ" wording
    }

    [Fact]
    public async Task The_main_flow_beats_a_sub_flow_that_shares_a_word()
    {
        // "Open Dispensing" (sub-flow) also mentions dispensing, but the step called exactly "Dispensing" is the better match.
        var line = (await Outline(SeedBmw(), "dispensing is done, what next?")).Split('\n').First(l => l.StartsWith("NEXT STEP"));
        Assert.Contains("\"BMW Review\"", line);
        Assert.DoesNotContain("Receive", line);
    }

    [Fact]
    public async Task What_comes_before_names_the_previous_step()
    {
        var line = (await Outline(SeedBmw(), "what comes before BMW Review?")).Split('\n').First(l => l.StartsWith("PREVIOUS STEP"));
        Assert.Contains("\"Dispensing\"", line);
    }

    [Fact]
    public async Task The_last_and_first_steps_are_reported_as_such()
    {
        var db = SeedBmw();
        Assert.Contains("LAST STEP", await Outline(db, "corporate dashboard submitted, what next?"));
        Assert.Contains("FIRST STEP", await Outline(db, "what comes before BMW Masters?"));
    }

    [Fact]
    public async Task Without_a_recognisable_step_there_is_no_next_step_line_so_nothing_is_guessed()
    {
        var db = SeedBmw();
        Assert.DoesNotContain("NEXT STEP", await Outline(db, "what is next?"));
        Assert.DoesNotContain("NEXT STEP", await Outline(db, "what happens next with the parking permit?"));
    }

    [Fact]
    public async Task A_link_limited_to_other_documents_never_receives_workflows_from_this_one()
    {
        var db = SeedBmw();
        var guide = db.Documents.First().Id;
        var other = Guid.NewGuid();

        // mapped to this document: the outline and the answer line are available
        var mapped = (await WorkflowOutline.BuildAsync(db, App, "sticker collected what next?", default, new[] { guide }))?.Text ?? "";
        Assert.Contains("NEXT STEP", mapped);
        // mapped to some other document: nothing from this document's workflows
        Assert.Null(await WorkflowOutline.BuildAsync(db, App, "sticker collected what next?", default, new[] { other }));
        // no restriction: as before
        Assert.NotNull(await WorkflowOutline.BuildAsync(db, App, "sticker collected what next?", default));
    }

    [Fact]
    public async Task A_Tamil_order_question_finds_the_workflow_through_the_retrieved_English_text_or_an_English_page_name()
    {
        var db = SeedBmw();
        // No English words in the question: the retrieved excerpts (English) point at the right workflow.
        var viaContext = (await WorkflowOutline.BuildAsync(db, App, "ஸ்டிக்கர்கள் உருவாக்கிய பிறகு அடுத்த படி என்ன?", default, null,
            "Collection and Stickers: count bags, confirm counts and generate stickers for each bag."))?.Text ?? "";
        Assert.Contains("1. Collection and Stickers", viaContext);
        Assert.Contains("2. Dispensing", viaContext);

        // An English page name inside a Tamil sentence is enough for the exact "next step" line.
        var line = (await Outline(db, "Dispensing முடிந்தது, அடுத்து என்ன?")).Split('\n').First(l => l.StartsWith("NEXT STEP"));
        Assert.Contains("\"BMW Review\"", line);
        var before = (await Outline(db, "BMW Review க்கு முன்பு என்ன?")).Split('\n').First(l => l.StartsWith("PREVIOUS STEP"));
        Assert.Contains("\"Dispensing\"", before);
    }

    [Fact]
    public async Task The_outline_still_lists_the_whole_flow_in_order_after_the_answer_line()
    {
        var text = await Outline(SeedBmw(), "sticker collected what next?");
        Assert.True(text.IndexOf("NEXT STEP", StringComparison.Ordinal) < text.IndexOf("1. Collection and Stickers", StringComparison.Ordinal));
        Assert.True(text.IndexOf("2. Dispensing", StringComparison.Ordinal) < text.IndexOf("4. Handover Register", StringComparison.Ordinal));
    }
}
