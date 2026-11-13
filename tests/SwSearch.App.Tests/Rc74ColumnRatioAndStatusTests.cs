using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SwSearch.App.Tests;

[TestClass]
public sealed class Rc74ColumnRatioAndStatusTests
{
    [TestMethod]
    public void ColumnWidths_UsePersistedPercentagesAndLastColumnAbsorbsRounding()
    {
        var widths = NativeShellColumnWidthLayout.CalculateWidths(
            1001,
            [0.42d, 0.18d, 0.40d],
            [180, 120, 220]);

        CollectionAssert.AreEqual(new[] { 420, 180, 401 }, widths);
        Assert.AreEqual(1001, widths.Sum());
    }

    [TestMethod]
    public void ColumnWidths_RespectMinimumsWithoutChangingTheStoredRatio()
    {
        var ratios = new[] { 0.50d, 0.20d, 0.30d };
        var narrow = NativeShellColumnWidthLayout.CalculateWidths(400, ratios, [180, 120, 220]);
        var restored = NativeShellColumnWidthLayout.CalculateWidths(1200, ratios, [180, 120, 220]);

        CollectionAssert.AreEqual(new[] { 180, 120, 220 }, narrow);
        CollectionAssert.AreEqual(new[] { 600, 240, 360 }, restored);
        CollectionAssert.AreEqual(new[] { 0.50d, 0.20d, 0.30d }, ratios);
    }

    [TestMethod]
    public void ColumnWidths_KeepTheExactAvailableWidthWhenTheLastColumnIsConstrained()
    {
        var widths = NativeShellColumnWidthLayout.CalculateWidths(
            700,
            [0.60d, 0.35d, 0.05d],
            [180, 120, 220]);

        CollectionAssert.AreEqual(new[] { 303, 177, 220 }, widths);
        Assert.AreEqual(700, widths.Sum());
    }

    [TestMethod]
    public void DividerClickOrConstrainedLayout_DoesNotCommitPixelWidthsAsRatios()
    {
        Assert.IsFalse(NativeShellColumnResizePolicy.ShouldCommit(
            widthsChanged: false,
            layoutConstrained: false));
        Assert.IsFalse(NativeShellColumnResizePolicy.ShouldCommit(
            widthsChanged: true,
            layoutConstrained: true));
        Assert.IsTrue(NativeShellColumnResizePolicy.ShouldCommit(
            widthsChanged: true,
            layoutConstrained: false));

        Assert.IsTrue(NativeShellColumnWidthLayout.IsConstrained(
            520,
            [0.42d, 0.18d, 0.40d],
            [180, 120, 220]));
        Assert.IsFalse(NativeShellColumnWidthLayout.IsConstrained(
            1000,
            [0.42d, 0.18d, 0.40d],
            [180, 120, 220]));
    }

    [TestMethod]
    public void UserWidths_RoundTripAsNormalizedRatios()
    {
        var ratios = NativeShellColumnWidthLayout.CalculateRatios([525, 225, 500]);

        Assert.AreEqual(0.42d, ratios[0], 0.000001d);
        Assert.AreEqual(0.18d, ratios[1], 0.000001d);
        Assert.AreEqual(0.40d, ratios[2], 0.000001d);
        CollectionAssert.AreEqual(
            new[] { 0.42d, 0.18d, 0.40d },
            NativeShellColumnRatioDefaults.Normalize(ratios).ToArray());
    }

    [TestMethod]
    public void ProgressText_IsCenteredAtNormalAndNarrowWidths()
    {
        var normal = NativeStatusBoundsLayout.Calculate(1000, 28, 400, 80);
        var narrow = NativeStatusBoundsLayout.Calculate(240, 28, 400, 80);

        Assert.AreEqual(500, normal.Progress.Left + normal.Progress.Width / 2);
        Assert.AreEqual(120, narrow.Progress.Left + narrow.Progress.Width / 2);
        Assert.AreEqual(228, narrow.Progress.Width);
        Assert.IsTrue(normal.Status.Right <= normal.Progress.Left);
        Assert.IsTrue(normal.ResultCount.Left >= normal.Progress.Right);
    }

    [TestMethod]
    public void NativeStatusPanel_ConstructsAndPerformsInitialLayout()
    {
        using var panel = new NativeStatusPanel
        {
            Size = new System.Drawing.Size(900, 28),
        };
        panel.ProgressLabel.Text = "文件 100.00% · 可读取缓存 99.86% · 属性处理 100.00%";

        panel.PerformLayout();

        Assert.AreEqual(450, panel.ProgressLabel.Left + panel.ProgressLabel.Width / 2);
        Assert.IsTrue(panel.ProgressLabel.Width > 0);
    }

    [TestMethod]
    public void InvalidOrLegacyRatios_FallBackToFortyTwoEighteenForty()
    {
        CollectionAssert.AreEqual(
            new[] { 0.42d, 0.18d, 0.40d },
            NativeShellColumnRatioDefaults.Normalize(null).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0.42d, 0.18d, 0.40d },
            NativeShellColumnRatioDefaults.Normalize([double.NaN, 1d, 1d]).ToArray());
    }
}
