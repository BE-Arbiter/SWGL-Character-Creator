using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class TextDiffTests
{
    private static string Render(IEnumerable<DiffLine> lines) =>
        string.Join("|", lines.Select(l => (l.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", _ => " " }) + l.Text));

    [Fact] public void Identical_AllSame()
    {
        var d = TextDiff.Compute("a\nb\n", "a\nb\n");
        Assert.All(d, l => Assert.Equal(DiffKind.Same, l.Kind));
        Assert.Equal(2, d.Count);
    }

    [Fact] public void ChangedLine_RemovedThenAdded()
    {
        Assert.Equal(" a|-b|+B| c", Render(TextDiff.Compute("a\nb\nc", "a\nB\nc")));
    }

    [Fact] public void InsertAndDelete()
    {
        Assert.Equal(" a|+x| b|-c| d", Render(TextDiff.Compute("a\nb\nc\nd", "a\nx\nb\nd")));
    }

    [Fact] public void NewFile_AllAdded()
    {
        Assert.Equal("+a|+b", Render(TextDiff.Compute("", "a\nb")));
    }

    [Fact] public void CrLf_IsIgnored()
    {
        Assert.All(TextDiff.Compute("a\r\nb\r\n", "a\nb\n"), l => Assert.Equal(DiffKind.Same, l.Kind));
    }

    [Fact] public void LineNumbers()
    {
        var d = TextDiff.Compute("a\nb\nc", "a\nB\nc");
        Assert.Equal((2, 0), (d[1].OldLine, d[1].NewLine));
        Assert.Equal((0, 2), (d[2].OldLine, d[2].NewLine));
        Assert.Equal((3, 3), (d[3].OldLine, d[3].NewLine));
    }

    [Fact] public void Context_CollapsesGaps()
    {
        var old = string.Join("\n", Enumerable.Range(1, 20));
        var neu = old.Replace("\n10\n", "\nTEN\n");
        var ctx = TextDiff.WithContext(TextDiff.Compute(old, neu), 1);
        Assert.Null(ctx[0]);
        Assert.Equal(new[] { "9", "10", "TEN", "11" }, ctx.Skip(1).Take(4).Select(l => l!.Value.Text));
        Assert.Null(ctx[^1]);
    }
}
