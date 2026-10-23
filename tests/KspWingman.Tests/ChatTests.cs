// KSP Wingman · tests/KspWingman.Tests/ChatTests.cs
// The bridge's chat numbering (src/KspWingman.Bridge/ChatLog.cs) and how the tools show chat lines.

using KspWingman.Bridge;
using KspWingman.Mcp;

namespace KspWingman.Tests;

public class ChatTests
{
    private sealed record Line(string From, string Text);

    [Fact]
    public void Numbers_each_line_once_as_lmps_queue_moves_on()
    {
        var log = new ChatLog();
        var a = new Line("Jeb", "hi");
        var b = new Line("Bill", "hello");
        log.Update([a, b], l => l.From, l => l.Text);
        log.Update([a, b], l => l.From, l => l.Text); // nothing new
        var c = new Line("Jeb", "hi"); // the same words again are a new line
        log.Update([b, c], l => l.From, l => l.Text); // LMP dropped the oldest
        Assert.Equal(["1 Jeb hi", "2 Bill hello", "3 Jeb hi"], log.After(0).Select(l => $"{l.Id} {l.From} {l.Text}"));
        Assert.Equal([3], log.After(2).Select(l => l.Id));
        Assert.Empty(log.After(3));
    }

    [Fact]
    public void Keeps_at_most_the_newest_lines()
    {
        var log = new ChatLog();
        for (var i = 0; i < ChatLog.Keep + 20; i++)
            log.Update([new Line("p", i.ToString())], l => l.From, l => l.Text);
        var kept = log.After(0);
        Assert.Equal(ChatLog.Keep, kept.Count);
        Assert.Equal(ChatLog.Keep + 20, kept[^1].Id);
    }

    [Fact]
    public void Shows_lines_as_id_name_and_text() =>
        Assert.Equal("#4 Jeb: going up\n#5 Bill: copy, see you there", Tools.Format(["4\tJeb\tgoing up", "5\tBill\tcopy, see you there"]));
}
