using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Protocol;

public class FileNamesTests
{
    // Shared with the cross-client vector file (VaultCryptoTests writes them as "safeFileName" and "fileRowText").
    public static readonly (string In, string Out)[] SafeCases =
    [
        ("report.pdf", "report.pdf"),
        ("../../evil.exe", "evil.exe"),
        ("C:\\x\\a.txt", "a.txt"),
        ("CON.txt", "_CON.txt"),
        ("a<b>.txt", "ab.txt"),
        ("...", "file"),
        ("", "file"),
        ("name. ", "name"),
        ("a\tb\u007f.txt", "ab.txt"),
        ("dir/", "file"),
        ("com1", "_com1"),
        ("lpt9.tar.gz", "_lpt9.tar.gz"),
        ("console.txt", "console.txt"),
        (new string('a', 300) + ".pdf", new string('a', 196) + ".pdf"),
        (new string('b', 250), new string('b', 200)),
        (new string('b', 199) + " " + new string('c', 50), new string('b', 199)),
        (new string('a', 195) + "." + new string('b', 10) + ".pdf", new string('a', 195) + ".pdf"),
        (new string('a', 199) + "\U0001F600z", new string('a', 199)),
        (new string('a', 195) + "\U0001F600.pdf", new string('a', 195) + ".pdf"),
        (new string('a', 197) + "\U0001F600", new string('a', 197) + "\U0001F600"),
    ];

    public static readonly (long Bytes, string Text)[] SizeCases =
    [
        (0, "0 B"), (1023, "1023 B"), (1024, "1 KB"), (1536, "1.5 KB"), (10188, "9.9 KB"), (10240, "10 KB"), (12288, "12 KB"),
        (1048064, "1 MB"), (3565158, "3.4 MB"), (10433331, "9.9 MB"), (10433332, "10 MB"), (26214400, "25 MB"),
    ];

    public static readonly (ClipFileInfo[] Files, string Text)[] RowCases =
    [
        ([new("report.pdf", 3565158, "application/pdf")], "report.pdf · 3.4 MB"),
        ([new("report.pdf", 1000000, "application/pdf"), new("b.txt", 2000000, "text/plain"), new("c", 565158, "application/octet-stream")],
            "report.pdf +2 more · 3.4 MB"),
        ([new("empty", 0, "application/octet-stream")], "empty · 0 B"),
        ([new("a", 12 * 1024, "application/octet-stream")], "a · 12 KB"),
    ];

    public static IEnumerable<object[]> Safe => SafeCases.Select(c => new object[] { c.In, c.Out });

    [Theory]
    [MemberData(nameof(Safe))]
    public void Safe_table(string input, string expected) => Assert.Equal(expected, FileNames.Safe(input));

    [Fact]
    public void Safe_cut_is_200_utf16_units_ending_in_the_extension()
    {
        var s = FileNames.Safe(new string('x', 300) + ".pdf");
        Assert.Equal(200, s.Length);
        Assert.EndsWith(".pdf", s);
    }

    [Fact]
    public void Unique_adds_a_counter_before_the_extension()
    {
        Assert.Equal("report.pdf", FileNames.Unique("report.pdf", _ => false));
        Assert.Equal("report (2).pdf", FileNames.Unique("report.pdf", n => n == "report.pdf"));
        var taken = new HashSet<string> { "report.pdf", "report (2).pdf" };
        Assert.Equal("report (3).pdf", FileNames.Unique("report.pdf", taken.Contains));
        Assert.Equal("README (2)", FileNames.Unique("README", n => n == "README"));
    }

    [Fact]
    public void MimeFor_uses_the_extension_table_case_insensitively()
    {
        Assert.Equal("application/pdf", FileNames.MimeFor("x.PDF"));
        Assert.Equal("image/jpeg", FileNames.MimeFor("a.jpeg"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileNames.MimeFor("a.docx"));
        Assert.Equal("application/octet-stream", FileNames.MimeFor("x.unknownext"));
        Assert.Equal("application/octet-stream", FileNames.MimeFor("noext"));
    }

    public static IEnumerable<object[]> Sizes => SizeCases.Select(c => new object[] { c.Bytes, c.Text });

    [Theory]
    [MemberData(nameof(Sizes))]
    public void FormatSize_table(long bytes, string expected) => Assert.Equal(expected, FileNames.FormatSize(bytes));

    [Fact]
    public void Safe_never_leaves_a_lone_surrogate_or_trailing_dot()
    {
        for (var pad = 190; pad <= 200; pad++)
            foreach (var tail in new[] { ".pdf", "", " x" })
            {
                var s = FileNames.Safe(new string('a', pad) + "\U0001F600\U0001F600" + tail);
                Assert.True(s.Length <= 200);
                for (var i = 0; i < s.Length; i++)
                    if (char.IsHighSurrogate(s[i])) Assert.True(i + 1 < s.Length && char.IsLowSurrogate(s[++i]));
                    else Assert.False(char.IsLowSurrogate(s[i]));
                Assert.False(s.EndsWith('.') || s.EndsWith(' '));
            }
    }

    public static IEnumerable<object[]> RowIndexes => Enumerable.Range(0, RowCases.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(RowIndexes))]
    public void RowText_table(int i) => Assert.Equal(RowCases[i].Text, FileNames.RowText(RowCases[i].Files));
}

