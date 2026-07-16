using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ChmToPdfTool;

public sealed class MainForm : Form
{
    private readonly TextBox _inputPath = new();
    private readonly TextBox _outputPath = new();
    private readonly Button _convertButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _openButton = new();
    private readonly PauseController _pauseController = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly TextBox _log = new();
    private readonly Panel _dropZone = new();
    private CancellationTokenSource? _cancellation;

    public MainForm()
    {
        Text = "CHM 转 PDF";
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 247, 250);
        Font = new Font("Microsoft YaHei UI", 9F);
        AllowDrop = true;

        BuildInterface();
        DragEnter += HandleDragEnter;
        DragDrop += HandleDragDrop;
        _dropZone.DragEnter += HandleDragEnter;
        _dropZone.DragDrop += HandleDragDrop;
        _dropZone.AllowDrop = true;
    }

    private void BuildInterface()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 112,
            BackColor = Color.FromArgb(32, 45, 64),
        };
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Location = new Point(28, 22),
            Text = "CHM 转 PDF",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 23F, FontStyle.Bold),
        });
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Location = new Point(31, 70),
            Text = "选择或拖入 CHM，一键生成带目录书签的 PDF",
            ForeColor = Color.FromArgb(190, 204, 221),
            Font = new Font("Microsoft YaHei UI", 10F),
        });
        Controls.Add(header);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 20, 28, 22),
        };
        Controls.Add(content);
        content.BringToFront();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(layout);

        _dropZone.Dock = DockStyle.Fill;
        _dropZone.Margin = new Padding(0, 0, 0, 12);
        _dropZone.BackColor = Color.White;
        _dropZone.Paint += (_, eventArgs) => PaintDropZone(eventArgs.Graphics);
        _dropZone.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "将 .chm 文件拖到这里",
            ForeColor = Color.FromArgb(80, 98, 123),
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
        });
        layout.Controls.Add(_dropZone, 0, 0);

        layout.Controls.Add(CreatePathRow("CHM 文件", _inputPath, BrowseInput), 0, 1);
        layout.Controls.Add(CreatePathRow("输出 PDF", _outputPath, BrowseOutput), 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 0),
        };
        ConfigureButton(_convertButton, "开始转换", Color.FromArgb(38, 112, 234), Color.White, 130);
        ConfigureButton(_pauseButton, "暂停", Color.FromArgb(252, 211, 77), Color.FromArgb(45, 55, 72), 80);
        ConfigureButton(_cancelButton, "取消", Color.FromArgb(224, 228, 235), Color.FromArgb(45, 55, 72), 80);
        ConfigureButton(_openButton, "打开 PDF", Color.FromArgb(224, 228, 235), Color.FromArgb(45, 55, 72), 105);
        _pauseButton.Enabled = false;
        _openButton.Enabled = false;
        _convertButton.Click += async (_, _) => await StartConversionAsync();
        _pauseButton.Click += (_, _) => TogglePause();
        _cancelButton.Click += (_, _) => _cancellation?.Cancel();
        _openButton.Click += (_, _) => OpenOutput();
        buttons.Controls.AddRange([_convertButton, _pauseButton, _cancelButton, _openButton]);
        layout.Controls.Add(buttons, 0, 3);

        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Continuous;
        layout.Controls.Add(_progress, 0, 4);

        _status.Dock = DockStyle.Fill;
        _status.Text = "等待选择文件";
        _status.ForeColor = Color.FromArgb(88, 101, 120);
        layout.Controls.Add(_status, 0, 6);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.White;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9F);
        layout.Controls.Add(_log, 0, 7);
    }

    private static void ConfigureButton(
        Button button,
        string text,
        Color background,
        Color foreground,
        int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 38;
        button.Margin = new Padding(0, 0, 10, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    }

    private Control CreatePathRow(string labelText, TextBox textBox, EventHandler browseHandler)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = new Padding(0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));

        panel.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.FromArgb(55, 67, 85),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        panel.SetColumnSpan(panel.Controls[^1], 3);

        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 4, 10, 0);
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Font = new Font("Microsoft YaHei UI", 10F);
        panel.Controls.Add(textBox, 0, 1);
        panel.SetColumnSpan(textBox, 2);

        var browse = new Button
        {
            Text = "浏览...",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(45, 55, 72),
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(190, 198, 210);
        browse.Click += browseHandler;
        panel.Controls.Add(browse, 2, 1);
        return panel;
    }

    private void PaintDropZone(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(136, 157, 187), 2F)
        {
            DashStyle = DashStyle.Dash,
        };
        var rectangle = _dropZone.ClientRectangle;
        rectangle.Inflate(-2, -2);
        graphics.DrawRoundedRectangle(pen, rectangle, 10);
    }

    private void BrowseInput(object? sender, EventArgs eventArgs)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 CHM 文件",
            Filter = "CHM 帮助文件 (*.chm)|*.chm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetInput(dialog.FileName);
        }
    }

    private void BrowseOutput(object? sender, EventArgs eventArgs)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "保存 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            AddExtension = true,
            DefaultExt = "pdf",
            FileName = Path.GetFileNameWithoutExtension(_inputPath.Text) + ".pdf",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputPath.Text = dialog.FileName;
        }
    }

    private void HandleDragEnter(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.Effect = TryGetChm(eventArgs.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void HandleDragDrop(object? sender, DragEventArgs eventArgs)
    {
        if (TryGetChm(eventArgs.Data, out var path))
        {
            SetInput(path);
        }
    }

    private static bool TryGetChm(IDataObject? data, out string path)
    {
        path = string.Empty;
        if (data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        path = files[0];
        return string.Equals(Path.GetExtension(path), ".chm", StringComparison.OrdinalIgnoreCase);
    }

    private void SetInput(string path)
    {
        _inputPath.Text = path;
        _outputPath.Text = Path.Combine(
            Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.GetFileNameWithoutExtension(path) + ".pdf");
        _openButton.Enabled = false;
        _status.Text = "文件已选择，可以开始转换";
        AppendLog($"已选择：{path}");
    }

    private async Task StartConversionAsync()
    {
        if (!File.Exists(_inputPath.Text))
        {
            MessageBox.Show(this, "请选择有效的 CHM 文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputPath.Text))
        {
            MessageBox.Show(this, "请选择 PDF 输出路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cancellation = new CancellationTokenSource();
        _pauseController.Resume();
        _pauseButton.Text = "暂停";
        SetBusy(true);
        _progress.Value = 0;
        _log.Clear();
        AppendLog("开始转换...");

        var progress = new Progress<ConversionProgress>(update =>
        {
            _status.Text = update.Message;
            _progress.Maximum = Math.Max(1, update.Total);
            _progress.Value = Math.Min(_progress.Maximum, Math.Max(0, update.Completed));
            AppendLog(update.Message);
        });

        try
        {
            await new ChmConverter().ConvertAsync(
                _inputPath.Text,
                _outputPath.Text,
                progress,
                _pauseController,
                _cancellation.Token);
            _progress.Value = _progress.Maximum;
            _openButton.Enabled = true;
            _status.Text = "转换完成";
            MessageBox.Show(
                this,
                $"PDF 已生成：\n{_outputPath.Text}",
                "转换完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "转换已取消";
            AppendLog("用户取消了转换。");
        }
        catch (Exception exception)
        {
            _status.Text = "转换失败";
            AppendLog("错误：" + exception.Message);
            MessageBox.Show(
                this,
                exception.Message,
                "转换失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _pauseController.Resume();
            SetBusy(false);
        }
    }

    private void TogglePause()
    {
        if (_pauseController.IsPaused)
        {
            _pauseController.Resume();
            _pauseButton.Text = "暂停";
            _status.Text = "转换已继续";
            AppendLog("用户继续了转换。");
        }
        else
        {
            _pauseController.Pause();
            _pauseButton.Text = "继续";
            _status.Text = "转换已暂停";
            AppendLog("用户暂停了转换。");
        }
    }

    private void SetBusy(bool busy)
    {
        _convertButton.Enabled = !busy;
        _pauseButton.Enabled = busy;
        _cancelButton.Enabled = busy;
        _inputPath.Enabled = !busy;
        _outputPath.Enabled = !busy;
        _openButton.Enabled = !busy && File.Exists(_outputPath.Text);
    }

    private void OpenOutput()
    {
        if (!File.Exists(_outputPath.Text))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _outputPath.Text,
            UseShellExecute = true,
        });
    }

    private void AppendLog(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(
        this Graphics graphics,
        Pen pen,
        Rectangle bounds,
        int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }
}
