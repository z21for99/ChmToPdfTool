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
