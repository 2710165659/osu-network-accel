using System.Diagnostics;

namespace OsuNetworkAccel;

internal sealed class MainForm : Form
{
    private readonly AccelService service = new();
    private readonly Button accelerateButton;
    private readonly Button restoreButton;
    private readonly Button openStateButton;
    private readonly Label statusLabel;
    private readonly Label selectedIpLabel;
    private readonly Label reportPathLabel;
    private readonly TextBox logTextBox;

    public MainForm()
    {
        Text = "osu! Network Accel";
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;

        service.LogEmitted += appendLog;

        var introLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "通过修改 Windows hosts 为 osu! 选择当前更快的 Cloudflare IP。"
        };

        statusLabel = new Label
        {
            AutoSize = true,
            Text = "状态：待命"
        };

        selectedIpLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "上次优选 IP：暂无"
        };

        reportPathLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = $"测速报告：{service.LastResultPath}"
        };

        accelerateButton = new Button
        {
            AutoSize = true,
            Text = "测速并加速",
            Padding = new Padding(10, 6, 10, 6)
        };
        accelerateButton.Click += async (_, _) => await runOperationAsync(service.AccelerateAsync, "正在测速并写入 hosts...");

        restoreButton = new Button
        {
            AutoSize = true,
            Text = "恢复原本网络",
            Padding = new Padding(10, 6, 10, 6)
        };
        restoreButton.Click += async (_, _) => await runOperationAsync(service.RestoreAsync, "正在恢复 hosts...");

        openStateButton = new Button
        {
            AutoSize = true,
            Text = "打开报告目录",
            Padding = new Padding(10, 6, 10, 6)
        };
        openStateButton.Click += (_, _) => openStateDirectory();

        logTextBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10),
            BackColor = SystemColors.Window
        };

        var buttonFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        buttonFlow.Controls.Add(accelerateButton);
        buttonFlow.Controls.Add(restoreButton);
        buttonFlow.Controls.Add(openStateButton);

        var topLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        topLayout.Controls.Add(introLabel);
        topLayout.Controls.Add(statusLabel);
        topLayout.Controls.Add(selectedIpLabel);
        topLayout.Controls.Add(reportPathLabel);
        topLayout.Controls.Add(buttonFlow);

        var logGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            Text = "日志"
        };
        logGroup.Controls.Add(logTextBox);

        Controls.Add(logGroup);
        Controls.Add(topLayout);

        Load += (_, _) => refreshLastResult();
    }

    private async Task runOperationAsync(Func<CancellationToken, Task<OperationResult>> action, string busyText)
    {
        setBusy(true, busyText);
        appendLog(new string('-', 56));

        try
        {
            OperationResult result = await action(CancellationToken.None).ConfigureAwait(true);
            statusLabel.Text = result.Success ? "状态：完成" : "状态：失败";
            appendLog(result.Message);
            refreshLastResult();

            MessageBox.Show(
                this,
                result.Message,
                result.Success ? "完成" : "失败",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception error)
        {
            string message = $"执行失败：{error.Message}";
            statusLabel.Text = "状态：失败";
            appendLog(message);

            MessageBox.Show(
                this,
                message,
                "失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            setBusy(false, "状态：待命");
        }
    }

    private void setBusy(bool isBusy, string statusText)
    {
        accelerateButton.Enabled = !isBusy;
        restoreButton.Enabled = !isBusy;
        openStateButton.Enabled = !isBusy;
        statusLabel.Text = isBusy ? $"状态：{statusText}" : statusText;
        UseWaitCursor = isBusy;
    }

    private void refreshLastResult()
    {
        SpeedTestReport? report = service.TryLoadLastReport();
        selectedIpLabel.Text = report == null
            ? "上次优选 IP：暂无"
            : $"上次优选 IP：{report.SelectedIp}  |  {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";

        reportPathLabel.Text = $"测速报告：{service.LastResultPath}";
    }

    private void appendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => appendLog(message));
            return;
        }

        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (logTextBox.TextLength > 0)
            logTextBox.AppendText(Environment.NewLine);

        logTextBox.AppendText(line);
        logTextBox.SelectionStart = logTextBox.TextLength;
        logTextBox.ScrollToCaret();
    }

    private void openStateDirectory()
    {
        Directory.CreateDirectory(service.StateDirectory);

        Process.Start(new ProcessStartInfo
        {
            FileName = service.StateDirectory,
            UseShellExecute = true
        });
    }
}
