using SwSearch.WebUi;

using Forms = System.Windows.Forms;

namespace SwSearch.App;

/// <summary>
/// A WinForms-native header for the hidden-header ExplorerBrowser below it.
/// Its order and widths come from the exact same standard Shell property
/// contract as the real DetailsView rows. Column labels localize at paint
/// time through <see cref="AppUiText"/> ("nativeColumn." + id); the contract
/// Label stays the zh-CN fallback.
/// </summary>
internal sealed class NativeShellResultsColumnHeader : Forms.Control
{
    private const int DividerHitWidth = 5;
    private readonly IReadOnlyList<SwSearch.WebUi.Native.NativeShellColumnDefinition> _columns;
    private readonly int[] _widths;
    private double[] _ratios;
    private int _resizeBoundary = -1;
    private int _resizeStartX;
    private int _resizeLeftWidth;
    private int _resizeRightWidth;
    private bool _layoutConstrained;
    private int _pressedColumn = -1;
    private string? _sortColumn;
    private string? _sortDirection;
    private string? _language;

    internal NativeShellResultsColumnHeader()
    {
        _columns = SwSearch.WebUi.Native.NativeShellColumnContract.Columns;
        _widths = _columns.Select(column => column.MinimumWidth).ToArray();
        _ratios = NativeShellColumnRatioDefaults.Normalize(null).ToArray();
        Dock = Forms.DockStyle.Top;
        Height = 30;
        BackColor = System.Drawing.SystemColors.Control;
        ForeColor = System.Drawing.SystemColors.ControlText;
        TabStop = true;
        SetStyle(
            Forms.ControlStyles.UserPaint |
            Forms.ControlStyles.AllPaintingInWmPaint |
            Forms.ControlStyles.OptimizedDoubleBuffer |
            Forms.ControlStyles.ResizeRedraw,
            true);
    }

    internal event EventHandler<NativeShellColumnInvokedEventArgs>? ColumnInvoked;

    internal event EventHandler? ColumnWidthsChanged;

    internal event EventHandler? ColumnResizeCompleted;

    internal int[] SnapshotWidths() => (int[])_widths.Clone();

    internal double[] SnapshotRatios() => (double[])_ratios.Clone();

    internal void SetRatios(IEnumerable<double>? ratios)
    {
        var normalized = NativeShellColumnRatioDefaults.Normalize(ratios).ToArray();
        if (_ratios.SequenceEqual(normalized))
        {
            return;
        }

        _ratios = normalized;
        FitColumnsToClient();
    }

    internal void SetSortState(string? column, string? direction)
    {
        _sortColumn = direction is null ? null : column;
        _sortDirection = direction;
        Invalidate();
    }

    /// <summary>Repaints with column labels resolved for a new UI language.</summary>
    internal void ApplyLanguage(string language)
    {
        _language = language;
        Invalidate();
    }

    private string ColumnLabel(SwSearch.WebUi.Native.NativeShellColumnDefinition column)
    {
        var key = "nativeColumn." + column.Id;
        var localized = AppUiText.Get(_language ?? UiLanguage.Chinese, key);
        return string.Equals(localized, key, StringComparison.Ordinal) ? column.Label : localized;
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        FitColumnsToClient();
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.Clear(BackColor);

        var x = 0;
        for (var index = 0; index < _columns.Count; index++)
        {
            var width = _widths[index];
            var cell = new System.Drawing.Rectangle(x, 0, width, Math.Max(1, Height - 1));
            using var background = new System.Drawing.SolidBrush(
                index == _pressedColumn
                    ? System.Drawing.SystemColors.ControlLight
                    : System.Drawing.SystemColors.Control);
            eventArgs.Graphics.FillRectangle(background, cell);

            var label = ColumnLabel(_columns[index]);
            if (string.Equals(_sortColumn, _columns[index].Id, StringComparison.Ordinal))
            {
                label += string.Equals(_sortDirection, "desc", StringComparison.Ordinal) ? "  ↓" : "  ↑";
            }

            var textBounds = new System.Drawing.Rectangle(
                cell.Left + 9,
                cell.Top + 1,
                Math.Max(0, cell.Width - 18),
                Math.Max(0, cell.Height - 2));
            Forms.TextRenderer.DrawText(
                eventArgs.Graphics,
                label,
                Font,
                textBounds,
                ForeColor,
                Forms.TextFormatFlags.Left |
                Forms.TextFormatFlags.VerticalCenter |
                Forms.TextFormatFlags.EndEllipsis |
                Forms.TextFormatFlags.NoPrefix);

            eventArgs.Graphics.DrawLine(
                System.Drawing.SystemPens.ControlDark,
                cell.Right - 1,
                cell.Top,
                cell.Right - 1,
                cell.Bottom);
            x += width;
        }

        eventArgs.Graphics.DrawLine(
            System.Drawing.SystemPens.ControlDark,
            0,
            Height - 1,
            Width,
            Height - 1);
    }

    protected override void OnMouseDown(Forms.MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        _resizeBoundary = FindResizeBoundary(eventArgs.X);
        if (_resizeBoundary >= 0)
        {
            _resizeStartX = eventArgs.X;
            _resizeLeftWidth = _widths[_resizeBoundary];
            _resizeRightWidth = _widths[_resizeBoundary + 1];
            Capture = true;
            Cursor = Forms.Cursors.VSplit;
            return;
        }

        _pressedColumn = FindColumn(eventArgs.X);
        Invalidate();
    }

    protected override void OnMouseMove(Forms.MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (_resizeBoundary >= 0 && Capture)
        {
            ResizeAdjacentColumns(eventArgs.X - _resizeStartX);
            return;
        }

        Cursor = FindResizeBoundary(eventArgs.X) >= 0
            ? Forms.Cursors.VSplit
            : Forms.Cursors.Default;
    }

    protected override void OnMouseUp(Forms.MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        if (_resizeBoundary >= 0)
        {
            var shouldCommit = NativeShellColumnResizePolicy.ShouldCommit(
                _widths[_resizeBoundary] != _resizeLeftWidth ||
                _widths[_resizeBoundary + 1] != _resizeRightWidth,
                _layoutConstrained);
            _resizeBoundary = -1;
            Capture = false;
            Cursor = Forms.Cursors.Default;
            if (shouldCommit)
            {
                _ratios = NativeShellColumnWidthLayout.CalculateRatios(_widths);
                ColumnResizeCompleted?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        var releasedColumn = FindColumn(eventArgs.X);
        var invokedColumn = releasedColumn == _pressedColumn ? releasedColumn : -1;
        _pressedColumn = -1;
        Invalidate();
        if (invokedColumn >= 0)
        {
            ColumnInvoked?.Invoke(
                this,
                new NativeShellColumnInvokedEventArgs(_columns[invokedColumn].Id));
        }
    }

    protected override bool IsInputKey(Forms.Keys keyData) =>
        keyData is Forms.Keys.Enter or Forms.Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(Forms.KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if ((eventArgs.KeyCode is Forms.Keys.Enter or Forms.Keys.Space) && _columns.Count > 0)
        {
            ColumnInvoked?.Invoke(this, new NativeShellColumnInvokedEventArgs(_columns[0].Id));
            eventArgs.Handled = true;
        }
    }

    private void FitColumnsToClient()
    {
        if (_columns.Count == 0)
        {
            return;
        }

        // Reserve the native vertical scrollbar lane so the Shell cell
        // boundaries and the custom header boundaries remain pixel-aligned.
        var available = Math.Max(
            _columns.Sum(column => column.MinimumWidth),
            ClientSize.Width - Forms.SystemInformation.VerticalScrollBarWidth);
        _layoutConstrained = NativeShellColumnWidthLayout.IsConstrained(
            available,
            _ratios,
            _columns.Select(column => column.MinimumWidth).ToArray());
        var next = NativeShellColumnWidthLayout.CalculateWidths(
            available,
            _ratios,
            _columns.Select(column => column.MinimumWidth).ToArray());
        if (_widths.SequenceEqual(next))
        {
            return;
        }

        Array.Copy(next, _widths, next.Length);
        Invalidate();
        ColumnWidthsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeAdjacentColumns(int requestedDelta)
    {
        var leftMinimum = _columns[_resizeBoundary].MinimumWidth;
        var rightMinimum = _columns[_resizeBoundary + 1].MinimumWidth;
        var delta = Math.Clamp(
            requestedDelta,
            leftMinimum - _resizeLeftWidth,
            _resizeRightWidth - rightMinimum);
        var left = _resizeLeftWidth + delta;
        var right = _resizeRightWidth - delta;
        if (_widths[_resizeBoundary] == left && _widths[_resizeBoundary + 1] == right)
        {
            return;
        }

        _widths[_resizeBoundary] = left;
        _widths[_resizeBoundary + 1] = right;
        Invalidate();
        ColumnWidthsChanged?.Invoke(this, EventArgs.Empty);
    }

    private int FindColumn(int x)
    {
        var right = 0;
        for (var index = 0; index < _widths.Length; index++)
        {
            right += _widths[index];
            if (x >= right - _widths[index] && x < right)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindResizeBoundary(int x)
    {
        var right = 0;
        for (var index = 0; index < _widths.Length - 1; index++)
        {
            right += _widths[index];
            if (Math.Abs(x - right) <= DividerHitWidth)
            {
                return index;
            }
        }

        return -1;
    }
}

internal static class NativeShellColumnWidthLayout
{
    internal static bool IsConstrained(
        int availableWidth,
        IEnumerable<double>? ratios,
        IReadOnlyList<int> minimumWidths)
    {
        ArgumentNullException.ThrowIfNull(minimumWidths);
        if (minimumWidths.Count == 0)
        {
            return false;
        }

        var minimums = minimumWidths.Select(static width => Math.Max(1, width)).ToArray();
        if (availableWidth <= minimums.Sum())
        {
            return true;
        }

        var normalized = NormalizeRatios(ratios, minimums.Length);
        return minimums
            .Select((minimum, index) => availableWidth * normalized[index] + 0.000001d < minimum)
            .Any(static constrained => constrained);
    }

    internal static int[] CalculateWidths(
        int availableWidth,
        IEnumerable<double>? ratios,
        IReadOnlyList<int> minimumWidths)
    {
        ArgumentNullException.ThrowIfNull(minimumWidths);
        if (minimumWidths.Count == 0)
        {
            return Array.Empty<int>();
        }

        var minimums = minimumWidths.Select(static width => Math.Max(1, width)).ToArray();
        var minimumTotal = minimums.Sum();
        if (availableWidth <= minimumTotal)
        {
            return minimums;
        }

        var normalized = NormalizeRatios(ratios, minimums.Length);
        var result = new int[minimums.Length];
        var active = Enumerable.Repeat(true, minimums.Length).ToArray();
        var remainingWidth = availableWidth;

        while (true)
        {
            var activeRatio = 0d;
            for (var index = 0; index < active.Length; index++)
            {
                if (active[index])
                {
                    activeRatio += normalized[index];
                }
            }

            var constrained = false;
            for (var index = 0; index < active.Length; index++)
            {
                if (!active[index])
                {
                    continue;
                }

                var proportional = remainingWidth * normalized[index] / activeRatio;
                if (proportional + 0.000001d < minimums[index])
                {
                    result[index] = minimums[index];
                    remainingWidth -= minimums[index];
                    active[index] = false;
                    constrained = true;
                }
            }

            if (!constrained)
            {
                break;
            }
        }

        var remainingRatio = 0d;
        for (var index = 0; index < active.Length; index++)
        {
            if (active[index])
            {
                remainingRatio += normalized[index];
            }
        }

        var activeAssigned = 0;
        var lastActive = Array.FindLastIndex(active, static value => value);
        for (var index = 0; index < active.Length; index++)
        {
            if (!active[index])
            {
                continue;
            }

            result[index] = index == lastActive
                ? remainingWidth - activeAssigned
                : Math.Max(minimums[index], (int)Math.Floor(remainingWidth * normalized[index] / remainingRatio));
            activeAssigned += result[index];
        }

        return result;
    }

    internal static double[] CalculateRatios(IReadOnlyList<int> widths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Count == 0)
        {
            return Array.Empty<double>();
        }

        var total = widths.Sum(static width => Math.Max(1, width));
        return widths.Select(width => (double)Math.Max(1, width) / total).ToArray();
    }

    private static double[] NormalizeRatios(IEnumerable<double>? ratios, int count)
    {
        var supplied = ratios?.ToArray();
        if (supplied is null || supplied.Length != count ||
            supplied.Any(static value => !double.IsFinite(value) || value <= 0d))
        {
            supplied = count == 3
                ? NativeShellColumnRatioDefaults.Normalize(null).ToArray()
                : Enumerable.Repeat(1d / count, count).ToArray();
        }

        var total = supplied.Sum();
        return supplied.Select(value => value / total).ToArray();
    }
}

internal static class NativeShellColumnResizePolicy
{
    internal static bool ShouldCommit(bool widthsChanged, bool layoutConstrained) =>
        widthsChanged && !layoutConstrained;
}

internal sealed class NativeShellColumnInvokedEventArgs(string columnId) : EventArgs
{
    internal string ColumnId { get; } = columnId;
}

internal static class NativeShellSortCycle
{
    internal static string? NextDirection(
        string? currentColumn,
        bool currentDescending,
        string invokedColumn)
    {
        if (!string.Equals(currentColumn, invokedColumn, StringComparison.Ordinal))
        {
            return "asc";
        }

        return currentDescending ? null : "desc";
    }
}
