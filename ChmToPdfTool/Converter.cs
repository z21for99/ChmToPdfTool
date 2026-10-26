using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SharpSevenZip;

namespace ChmToPdfTool;

public sealed record ConversionProgress(int Completed, int Total, string Message);

public sealed class ChmConverter
{
    public async Task ConvertAsync(
        string chmPath,
        string outputPdf,
        IProgress<ConversionProgress>? progress = null,
        PauseController? pauseController = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(chmPath, outputPdf);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var edgePath = FindEdge();
        var workDirectory = Path.Combine(Path.GetTempPath(), "ChmToPdf-" + Guid.NewGuid().ToString("N"));
        var extractedDirectory = Path.Combine(workDirectory, "extracted");
        var pagePdfDirectory = Path.Combine(workDirectory, "pages");
        Directory.CreateDirectory(extractedDirectory);
        Directory.CreateDirectory(pagePdfDirectory);

        try
        {
            progress?.Report(new ConversionProgress(0, 1, "正在解包 CHM..."));
            await ExtractAsync(chmPath, extractedDirectory, cancellationToken);

            var tocEntries = ReadToc(extractedDirectory);
            var pages = PageDiscovery.Discover(extractedDirectory, tocEntries.Select(entry => entry.Path).ToList());
            if (pages.Count == 0)
            {
                throw new InvalidOperationException("CHM 中没有找到可转换的 HTML 页面。");
            }

            var bookmarkTitles = BuildBookmarkTitles(extractedDirectory, tocEntries);

            await using var session = await EdgePdfSession.CreateAsync(
                edgePath,
                Path.Combine(workDirectory, "edge-profile"),
                cancellationToken);

            var renderedPdfs = new List<(string PdfPath, string Title)>();
            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pauseController is not null)
                {
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                }

                var page = pages[index];
                var pagePdf = Path.Combine(pagePdfDirectory, $"{index + 1:D5}.pdf");
                progress?.Report(new ConversionProgress(
                    index,
                    pages.Count,
                    $"正在转换 {index + 1}/{pages.Count}: {Path.GetFileName(page)}"));

                await session.PrintAsync(page, pagePdf, cancellationToken);
                renderedPdfs.Add((pagePdf, ResolveTitle(bookmarkTitles, page)));
            }

            progress?.Report(new ConversionProgress(pages.Count, pages.Count, "正在合并 PDF..."));
            MergePdfs(renderedPdfs, outputPdf);
            progress?.Report(new ConversionProgress(pages.Count, pages.Count, "转换完成。"));
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private static void ValidateInputs(string chmPath, string outputPdf)
    {
        if (!File.Exists(chmPath))
        {
            throw new FileNotFoundException("找不到 CHM 文件。", chmPath);
        }

        if (!string.Equals(Path.GetExtension(chmPath), ".chm", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("输入文件必须是 .chm 格式。", nameof(chmPath));
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPdf));
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("输出路径无效。", nameof(outputPdf));
        }

        Directory.CreateDirectory(outputDirectory);
    }

    private static async Task ExtractAsync(
        string chmPath,
        string extractedDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                using var extractor = new SharpSevenZipExtractor(Path.GetFullPath(chmPath));
                extractor.ExtractArchive(extractedDirectory);
            }, cancellationToken);

            if (Directory.EnumerateFiles(extractedDirectory, "*", SearchOption.AllDirectories).Any())
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception sharpSevenZipError)
        {
            var hhError = await TryExtractWithWindowsHelpAsync(chmPath, extractedDirectory, cancellationToken);
            if (hhError is null)
            {
                return;
            }

            throw new InvalidOperationException(
                "CHM 解包失败。文件可能损坏、被加密或采用了不兼容格式。" +
                Environment.NewLine +
                $"7-Zip: {sharpSevenZipError.Message}" +
                (hhError is null ? string.Empty : Environment.NewLine + $"Windows Help: {hhError.Message}"),
                sharpSevenZipError);
        }

        throw new InvalidOperationException("CHM 解包完成，但没有提取到任何文件。");
    }

    private static async Task<Exception?> TryExtractWithWindowsHelpAsync(
        string chmPath,
        string extractedDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "hh.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-decompile");
            info.ArgumentList.Add(extractedDirectory);
            info.ArgumentList.Add(Path.GetFullPath(chmPath));
            await RunProcessOnceAsync(info, TimeSpan.FromMinutes(2), cancellationToken);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (Directory.EnumerateFiles(extractedDirectory, "*", SearchOption.AllDirectories).Any())
                    return null;
                await Task.Delay(250, cancellationToken);
            }

            return new InvalidOperationException("未提取到文件。");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IReadOnlyList<TocEntry> ReadToc(string extractedDirectory)
    {
        var hhc = Directory
            .EnumerateFiles(extractedDirectory, "*.hhc", SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();

        return hhc is null ? [] : TocParser.ParseEntries(SmartTextReader.ReadAllText(hhc));
    }

    /// <summary>
    /// Maps each page to the label the CHM's own table of contents uses. The HTML
    /// &lt;title&gt; is only a fallback: help compilers often emit a template
    /// placeholder there, and many topics repeat the same title.
    /// </summary>
    private static Dictionary<string, string> BuildBookmarkTitles(
        string extractedDirectory,
        IReadOnlyList<TocEntry> tocEntries)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in tocEntries)
        {
            if (entry.Title.Length == 0)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(extractedDirectory, entry.Path));
            titles.TryAdd(fullPath, entry.Title);
        }

        return titles;
    }

    private static string ResolveTitle(IReadOnlyDictionary<string, string> tocTitles, string page)
    {
        if (tocTitles.TryGetValue(Path.GetFullPath(page), out var title))
        {
            return title;
        }

        return HtmlMetadata.ReadTitle(page);
    }

    private static void MergePdfs(IReadOnlyList<(string PdfPath, string Title)> inputs, string outputPdf)
    {
        using var output = new PdfDocument();
        output.Info.Title = Path.GetFileNameWithoutExtension(outputPdf);
        output.Info.Creator = "CHM 转 PDF";

        foreach (var (pdfPath, title) in inputs)
        {
            using var input = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            PdfPage? firstPage = null;
            foreach (var page in input.Pages)
            {
                var imported = output.AddPage(page);
                firstPage ??= imported;
            }

            if (firstPage is not null && !string.IsNullOrWhiteSpace(title))
                output.Outlines.Add(title, firstPage, true);
        }

        if (output.PageCount == 0)
            throw new InvalidOperationException("没有可写入 PDF 的页面。");

        var tempOutput = outputPdf + ".partial";
        output.Save(tempOutput);
        File.Move(tempOutput, outputPdf, true);
    }

    private static async Task RunProcessOnceAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动：{info.FileName}");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try { await process.WaitForExitAsync(timeoutSource.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"执行超时：{Path.GetFileName(info.FileName)}");
        }

        if (cancellationToken.IsCancellationRequested) { TryKill(process); cancellationToken.ThrowIfCancellationRequested(); }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(info.FileName)} 执行失败，退出代码 {process.ExitCode}。");
    }

    private static string FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("未找到 Microsoft Edge。");
    }

    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch when (attempt < 3) { Thread.Sleep(400); }
        }
    }
}

internal sealed class EdgePdfSession : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);
    private readonly Process _process;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveLoop;
    private int _nextCommandId;

    private EdgePdfSession(Process process) { _process = process; }

    public static async Task<EdgePdfSession> CreateAsync(string edgePath, string profileDirectory, CancellationToken cancellationToken)
    {
        var port = GetFreePort();
        Directory.CreateDirectory(profileDirectory);
        var info = new ProcessStartInfo
        {
            FileName = edgePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        info.ArgumentList.Add("--headless");
        info.ArgumentList.Add("--disable-gpu");
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--allow-file-access-from-files");
        info.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        info.ArgumentList.Add($"--remote-debugging-port={port}");
        info.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        info.ArgumentList.Add("about:blank");

        var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Microsoft Edge。");
        var session = new EdgePdfSession(process);
        try
        {
            var wsUrl = await WaitForPageTargetAsync(port, process, cancellationToken);
            await session._socket.ConnectAsync(new Uri(wsUrl), cancellationToken);
            session._receiveLoop = session.ReceiveLoopAsync();
            await session.SendCommandAsync("Page.enable", null, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task PrintAsync(string htmlPath, string outputPdf, CancellationToken cancellationToken)
    {
        var url = new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri;
        await SendCommandAsync("Page.navigate", new { url }, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);

        var result = await SendCommandAsync("Page.printToPDF", new
        {
            printBackground = true,
            displayHeaderFooter = false,
            // Tell Chromium the paper explicitly: with preferCSSPageSize the page's
            // own @page rule still wins, but pages that declare nothing used to fall
            // back to US Letter (612x792pt). A4 is 8.27 x 11.69 inches.
            preferCSSPageSize = true,
            paperWidth = 8.27,
            paperHeight = 11.69,
        }, cancellationToken);

        if (!result.TryGetProperty("data", out var dataProp))
            throw new InvalidOperationException("Edge 未返回 PDF 数据。");

        await File.WriteAllBytesAsync(outputPdf, Convert.FromBase64String(dataProp.GetString()!), cancellationToken);

        if (!File.Exists(outputPdf) || new FileInfo(outputPdf).Length == 0)
            throw new InvalidOperationException($"页面打印失败：{Path.GetFileName(htmlPath)}");
    }

    private async Task WaitForPageReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await SendCommandAsync("Runtime.evaluate", new
                {
                    expression = "document.readyState === 'complete'",
                    returnByValue = true,
                }, cancellationToken);

                if (result.GetProperty("result").GetProperty("value").GetBoolean())
                {
                    await Task.Delay(120, cancellationToken);
                    return;
                }
            }
            catch (InvalidOperationException) { }
            await Task.Delay(60, cancellationToken);
        }

        throw new TimeoutException("等待页面加载超时。");
    }

    private async Task<JsonElement> SendCommandAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextCommandId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });

        await _sendLock.WaitAsync(cancellationToken);
        try { await _socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendLock.Release(); }

        try { return await completion.Task.WaitAsync(CommandTimeout, cancellationToken); }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[256 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open && !_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _lifetime.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;

                if (!root.TryGetProperty("id", out var idProp))
                    continue;

                if (!_pending.TryGetValue(idProp.GetInt32(), out var completion))
                    continue;

                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new InvalidOperationException(
                        error.TryGetProperty("message", out var msg) ? msg.GetString() : "Edge DevTools error"));
                }
                else
                {
                    completion.TrySetResult(root.GetProperty("result").Clone());
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            foreach (var completion in _pending.Values)
                completion.TrySetException(exception);
        }
    }

    private static async Task<string> WaitForPageTargetAsync(int port, Process process, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"Edge 启动失败，退出代码 {process.ExitCode}。");

            try
            {
                var json = await client.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken);
                using var document = JsonDocument.Parse(json);
                var target = document.RootElement.EnumerateArray()
                    .FirstOrDefault(item => item.TryGetProperty("type", out var type)
                        && string.Equals(type.GetString(), "page", StringComparison.Ordinal));
                if (target.ValueKind != JsonValueKind.Undefined
                    && target.TryGetProperty("webSocketDebuggerUrl", out var url))
                    return url.GetString()!;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            await Task.Delay(120, cancellationToken);
        }

        throw new TimeoutException("Edge DevTools 连接超时。");
    }

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_receiveLoop is not null) { try { await _receiveLoop; } catch { } }

        try { _socket.Dispose(); } catch { }
        try { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(2000); } } catch { }

        _sendLock.Dispose();
        _lifetime.Dispose();
    }
}

public static class SmartTextReader
{
    public static string ReadAllText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var match = Regex.Match(header, "charset\\s*=\\s*[\"']?([a-zA-Z0-9_-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (match.Success)
        {
            try { return Encoding.GetEncoding(match.Groups[1].Value).GetString(bytes); }
            catch (ArgumentException) { }
        }

        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(936).GetString(bytes); }
    }
}

public static class HtmlMetadata
{
    private static readonly Regex CommentPattern = new(
        "<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespacePattern = new(
        "\\s+",
        RegexOptions.CultureInvariant);

    public static string ReadTitle(string path)
    {
        try
        {
            var html = SmartTextReader.ReadAllText(path);
            // Help compilers (Sandcastle/MSHV) leave the title template in an HTML
            // comment, so the first <title> in the file is often a placeholder such
            // as %%ItemTitle%%. Blank out comments before looking for the real one.
            html = CommentPattern.Replace(html, " ");

            var match = Regex.Match(html, "<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var title = WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", " ")).Trim();
                title = WhitespacePattern.Replace(title, " ");
                if (title.Length > 0 && !title.Contains("%%", StringComparison.Ordinal))
                {
                    return title;
                }
            }
        }
        catch { }

        return Path.GetFileNameWithoutExtension(path);
    }
}
