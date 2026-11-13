using Forms = System.Windows.Forms;

namespace SwSearch.App;

internal sealed class NativeStatusPanel : Forms.Panel
{
    private readonly System.Drawing.Font? _ownedProgressFont;
    private bool _layoutReady;

    internal NativeStatusPanel()
    {
        SuspendLayout();
        StatusLabel = CreateLabel(System.Drawing.ContentAlignment.MiddleLeft);
        ProgressLabel = CreateLabel(System.Drawing.ContentAlignment.MiddleCenter);
        ResultCountLabel = CreateLabel(System.Drawing.ContentAlignment.MiddleRight);
        if (System.Drawing.SystemFonts.StatusFont is { } statusFont)
        {
            _ownedProgressFont = new System.Drawing.Font(statusFont, System.Drawing.FontStyle.Bold);
            ProgressLabel.Font = _ownedProgressFont;
        }

        RetryButton = new Forms.Button
        {
            AutoSize = true,
            AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink,
            FlatStyle = Forms.FlatStyle.System,
            Text = "重试文件视图",
            AccessibleName = "重试原生文件视图",
            Visible = false,
        };

        Dock = Forms.DockStyle.Fill;
        BackColor = System.Drawing.SystemColors.Control;
        Padding = new Forms.Padding(6, 0, 6, 0);
        Controls.Add(StatusLabel);
        Controls.Add(ProgressLabel);
        Controls.Add(ResultCountLabel);
        Controls.Add(RetryButton);
        ProgressLabel.TextChanged += (_, _) => PerformLayout();
        RetryButton.VisibleChanged += (_, _) => PerformLayout();
        _layoutReady = true;
        ResumeLayout(performLayout: true);
    }

    internal Forms.Label StatusLabel { get; }

    internal Forms.Label ProgressLabel { get; }

    internal Forms.Label ResultCountLabel { get; }

    internal Forms.Button RetryButton { get; }

    protected override void OnLayout(Forms.LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        // WinForms can invoke virtual layout callbacks while a derived Control is
        // still being constructed. Do not touch child controls until every field
        // has been initialized and attached.
        if (!_layoutReady)
        {
            return;
        }

        var client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        var preferredProgressWidth = Forms.TextRenderer.MeasureText(
            ProgressLabel.Text,
            ProgressLabel.Font,
            new System.Drawing.Size(int.MaxValue, client.Height),
            Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix).Width + 18;
        var bounds = NativeStatusBoundsLayout.Calculate(
            client.Width,
            client.Height,
            preferredProgressWidth,
            RetryButton.Visible ? RetryButton.PreferredSize.Width : 0,
            Padding.Left,
            Padding.Right);

        StatusLabel.Bounds = bounds.Status;
        ProgressLabel.Bounds = bounds.Progress;
        ResultCountLabel.Bounds = bounds.ResultCount;
        RetryButton.Bounds = bounds.Retry;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ownedProgressFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Forms.Label CreateLabel(System.Drawing.ContentAlignment alignment) => new()
    {
        AutoEllipsis = true,
        BackColor = System.Drawing.Color.Transparent,
        TextAlign = alignment,
        UseCompatibleTextRendering = false,
    };
}

internal readonly record struct NativeStatusBounds(
    System.Drawing.Rectangle Status,
    System.Drawing.Rectangle Progress,
    System.Drawing.Rectangle Retry,
    System.Drawing.Rectangle ResultCount);

internal static class NativeStatusBoundsLayout
{
    internal static NativeStatusBounds Calculate(
        int width,
        int height,
        int preferredProgressWidth,
        int retryWidth,
        int leftPadding = 6,
        int rightPadding = 6)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        leftPadding = Math.Clamp(leftPadding, 0, width);
        rightPadding = Math.Clamp(rightPadding, 0, Math.Max(0, width - leftPadding));
        var contentWidth = Math.Max(0, width - leftPadding - rightPadding);
        var progressWidth = Math.Min(Math.Max(0, preferredProgressWidth), contentWidth);
        var progressLeft = leftPadding + Math.Max(0, (contentWidth - progressWidth) / 2);
        var progress = new System.Drawing.Rectangle(progressLeft, 0, progressWidth, height);

        var leftAvailable = Math.Max(0, progress.Left - leftPadding);
        var actualRetryWidth = Math.Min(Math.Max(0, retryWidth), leftAvailable);
        var retry = new System.Drawing.Rectangle(
            progress.Left - actualRetryWidth,
            0,
            actualRetryWidth,
            height);
        var status = new System.Drawing.Rectangle(
            leftPadding,
            0,
            Math.Max(0, retry.Left - leftPadding),
            height);
        var resultLeft = progress.Right;
        var resultCount = new System.Drawing.Rectangle(
            resultLeft,
            0,
            Math.Max(0, width - rightPadding - resultLeft),
            height);
        return new NativeStatusBounds(status, progress, retry, resultCount);
    }
}
