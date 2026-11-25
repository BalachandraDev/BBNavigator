using FlowMind.Infrastructure.Chat;
using Xunit;

namespace FlowMind.Tests;

public class AnswerFormatterTests
{
    [Fact]
    public void Citation_markers_and_source_lines_from_the_screenshot_are_removed_but_the_content_is_not()
    {
        var raw = """
            **Full BMW flow – simple steps**

            1. **Set up the system (once)** – a BMW Manager creates the waste categories and CBWTF vendors. [2]

            *Source: [2]*

            2. **Collect waste and generate stickers**
               - Open the *Collection and Stickers* page. [2][3]
               - Count bags per colour and press **Confirm**. (Source 2)

            *Sources: 2, 3*
            These steps follow the order shown in the workflow outline. [1]
            """;

        var clean = AnswerFormatter.Clean(raw);

        Assert.DoesNotContain("Source", clean);
        Assert.DoesNotContain("[", clean);
        Assert.Contains("**Full BMW flow – simple steps**", clean);                                          // Markdown kept
        Assert.Contains("1. **Set up the system (once)** – a BMW Manager creates the waste categories and CBWTF vendors.", clean);
        Assert.Contains("   - Open the *Collection and Stickers* page.", clean);
        Assert.Contains("   - Count bags per colour and press **Confirm**.", clean);
        Assert.EndsWith("These steps follow the order shown in the workflow outline.", clean);
        Assert.DoesNotContain("\n\n\n", clean);
    }

    [Theory]
    [InlineData("Press Save [1].", "Press Save.")]
    [InlineData("Press Save [1, 2] now", "Press Save now")]
    [InlineData("Press Save 【3】.", "Press Save.")]
    [InlineData("Press Save 【3†L4-L6】.", "Press Save.")]
    [InlineData("Press Save (Source 2).", "Press Save.")]
    [InlineData("Press Save [Source 1].", "Press Save.")]
    [InlineData("Press Save (see source 3).", "Press Save.")]
    [InlineData("Print the stickers. *Source: [2][3]*", "Print the stickers.")]
    [InlineData("Print the stickers. _Sources: 2, 3_", "Print the stickers.")]
    public void Every_citation_style_disappears_and_the_spacing_is_tidied(string raw, string expected) =>
        Assert.Equal(expected, AnswerFormatter.Clean(raw));

    [Fact]
    public void Things_that_only_look_like_citations_are_left_alone()
    {
        Assert.Equal("The year [2024] is invalid.", AnswerFormatter.Clean("The year [2024] is invalid."));
        Assert.Equal("Sources are listed below the answer.", AnswerFormatter.Clean("Sources are listed below the answer."));   // starts with the word but is a normal sentence
        Assert.Equal("Sourcing rules apply to purchase orders.", AnswerFormatter.Clean("Sourcing rules apply to purchase orders."));   // a normal sentence that starts with "Source..." letters
        Assert.Equal("", AnswerFormatter.Clean("   "));
    }

    [Fact]
    public void Cleaning_twice_changes_nothing()
    {
        var once = AnswerFormatter.Clean("Step one [1].\n\n*Source: [1]*\n\nStep two (Source 2).");
        Assert.Equal(once, AnswerFormatter.Clean(once));
    }
}
