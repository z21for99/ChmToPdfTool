using ChmToPdfTool;

var failures = new List<string>();

Run("HHC order and duplicate removal", () =>
{
    const string hhc = """
        <UL>
          <LI><OBJECT type="text/sitemap">
            <param name="Name" value="Intro">
            <param name="Local" value="intro.htm">
          </OBJECT></LI>
          <LI><OBJECT type="text/sitemap">
            <param name="Name" value="Chapter">
            <param name="Local" value="chapters/one.html#top">
          </OBJECT></LI>
          <LI><OBJECT type="text/sitemap">
            <param name="Local" value="intro.htm">
          </OBJECT></LI>
          <LI><OBJECT type="text/sitemap">
            <param value="appendix.htm" name="Local">
          </OBJECT></LI>
        </UL>
        """;

    var result = TocParser.ParseLocalPaths(hhc);
    AssertSequence(result, ["intro.htm", "chapters/one.html", "appendix.htm"]);
});

Run("TOC entry titles", () =>
{
    const string hhc = """
        <UL>
          <LI><OBJECT type="text/sitemap">
            <param name="Name" value="Intro &amp; Overview">
            <param name="Local" value="intro.htm">
          </OBJECT></LI>
          <LI><OBJECT type="text/sitemap">
            <param value="chapters/one.html" name="Local">
            <param value="Chapter One" name="Name">
          </OBJECT></LI>
        </UL>
        """;

    var entries = TocParser.ParseEntries(hhc);
    AssertSequence(entries.Select(entry => entry.Title).ToList(), ["Intro & Overview", "Chapter One"]);
    AssertSequence(entries.Select(entry => entry.Path).ToList(), ["intro.htm", "chapters/one.html"]);
});

Run("Page title ignores commented template", () =>
{
    var path = Path.Combine(Path.GetTempPath(), "chm-title-" + Guid.NewGuid().ToString("N") + ".html");
    File.WriteAllText(path, """
        <!--DXMETADATA start type="ItemTitle" format="<title>%%ItemTitle%%</title>" --><title>Introduction</title><!--DXMETADATA end-->
        """);
    try
    {
        var title = HtmlMetadata.ReadTitle(path);
        if (title != "Introduction")
        {
            throw new InvalidOperationException($"Expected 'Introduction', got '{title}'.");
        }
    }
    finally
    {
        File.Delete(path);
    }
});

Run("Fallback page discovery", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "chm-pdf-tests-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "guide"));
        File.WriteAllText(Path.Combine(root, "index.htm"), "index");
        File.WriteAllText(Path.Combine(root, "guide", "02.html"), "two");
        File.WriteAllText(Path.Combine(root, "guide", "01.htm"), "one");
        File.WriteAllText(Path.Combine(root, "style.css"), "body{}");

        var result = PageDiscovery.Discover(root, []);
        AssertSequence(
            result.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToList(),
            ["guide/01.htm", "guide/02.html", "index.htm"]);
    }
    finally
    {
        Directory.Delete(root, true);
    }
});

Run("Pause gate blocks and resumes", () =>
{
    using var pause = new PauseController();
    pause.Pause();
    var wait = pause.WaitIfPausedAsync(CancellationToken.None);
    if (wait.IsCompleted)
    {
        throw new InvalidOperationException("Pause gate did not block.");
    }

    pause.Resume();
    if (!wait.Wait(TimeSpan.FromSeconds(1)))
    {
        throw new InvalidOperationException("Pause gate did not resume.");
    }
});

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("All converter core tests passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL: {name}: {exception.Message}");
    }
}

static void AssertSequence(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
{
    if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }
}
