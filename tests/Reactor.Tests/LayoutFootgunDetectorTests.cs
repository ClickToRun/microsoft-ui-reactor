using System;
using System.Collections.Generic;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Diagnostics;
using Xunit;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Issue #345 — debug-time warning when an <c>HStack</c>/<c>VStack</c> is placed in a
/// <c>Grid</c> <c>Auto</c> track with no explicit size and no explicitly-sized children
/// (the silent 0×0 collapse footgun).
///
/// <para>These tests drive <see cref="LayoutFootgunDetector.InspectGrid"/> directly against
/// the element tree — no WinUI control mount is required, so they stay in the headless unit
/// tier.</para>
/// </summary>
public sealed class LayoutFootgunDetectorTests : IDisposable
{
    private readonly List<string> _warnings = new();

    public LayoutFootgunDetectorTests()
    {
        LayoutFootgunDetector.ResetForTests();
        LayoutFootgunDetector.Sink = _warnings.Add;
    }

    public void Dispose()
    {
        LayoutFootgunDetector.Sink = null;
        LayoutFootgunDetector.ResetForTests();
    }

    // ── Should warn ────────────────────────────────────────────────────────

    [Fact]
    public void BareHStack_InAutoColumn_NoExplicitSize_Warns()
    {
        var grid = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star() },
            rows: new[] { GridSize.Star() },
            HStack(TextBlock("A"), TextBlock("B")).Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        var msg = Assert.Single(_warnings);
        Assert.Contains("HStack", msg);
        Assert.Contains("column 0 (Auto)", msg);
    }

    [Fact]
    public void BorderWrappedHStack_InAutoColumn_StillWarns()
    {
        // Wrapping in a Border does NOT fix the collapse (the Border sizes to its 0-sized child).
        var grid = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star() },
            rows: new[] { GridSize.Star() },
            Border(HStack(TextBlock("A"), TextBlock("B"))).Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        var msg = Assert.Single(_warnings);
        Assert.Contains("HStack", msg);
    }

    [Fact]
    public void VStack_InAutoRow_NoExplicitSize_Warns()
    {
        var grid = Grid(
            columns: new[] { GridSize.Star() },
            rows: new[] { GridSize.Star(), GridSize.Auto },
            VStack(TextBlock("A"), TextBlock("B")).Grid(row: 1, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        var msg = Assert.Single(_warnings);
        Assert.Contains("VStack", msg);
        Assert.Contains("row 1 (Auto)", msg);
    }

    // ── Should NOT warn ────────────────────────────────────────────────────

    [Fact]
    public void HStack_InStarColumn_DoesNotWarn()
    {
        var grid = Grid(
            columns: new[] { GridSize.Star(), GridSize.Star() },
            rows: new[] { GridSize.Star() },
            HStack(TextBlock("A"), TextBlock("B")).Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        Assert.Empty(_warnings);
    }

    [Fact]
    public void HStack_InAutoColumn_WithExplicitWidth_DoesNotWarn()
    {
        var grid = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star() },
            rows: new[] { GridSize.Star() },
            HStack(TextBlock("A"), TextBlock("B")).Width(200).Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        Assert.Empty(_warnings);
    }

    [Fact]
    public void HStack_InAutoColumn_WithExplicitlySizedChild_DoesNotWarn()
    {
        var grid = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star() },
            rows: new[] { GridSize.Star() },
            HStack(TextBlock("A").Width(120), TextBlock("B")).Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        Assert.Empty(_warnings);
    }

    [Fact]
    public void BorderWrappedHStack_BorderHasExplicitWidth_DoesNotWarn()
    {
        var grid = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star() },
            rows: new[] { GridSize.Star() },
            Border(HStack(TextBlock("A"), TextBlock("B"))).Width(200).Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        Assert.Empty(_warnings);
    }

    [Fact]
    public void HStack_InAutoRow_StarColumn_DoesNotWarn()
    {
        // A horizontal stack only collapses on its main (horizontal) axis. An Auto *row*
        // with a Star column does not trigger the HStack width-collapse footgun.
        var grid = Grid(
            columns: new[] { GridSize.Star() },
            rows: new[] { GridSize.Auto },
            HStack(TextBlock("A"), TextBlock("B")).Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        Assert.Empty(_warnings);
    }

    [Fact]
    public void EmptyHStack_InAutoColumn_DoesNotWarn()
    {
        var grid = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star() },
            rows: new[] { GridSize.Star() },
            HStack().Grid(row: 0, column: 0));

        LayoutFootgunDetector.InspectGrid(grid);

        Assert.Empty(_warnings);
    }

    // ── Emit-once ──────────────────────────────────────────────────────────

    [Fact]
    public void SameOffendingPlacement_WarnsOnlyOnce_AcrossRenders()
    {
        var grid = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star() },
            rows: new[] { GridSize.Star() },
            HStack(TextBlock("A"), TextBlock("B")).Grid(row: 0, column: 0));

        // Simulate two render/mount passes of the same logical placement.
        LayoutFootgunDetector.InspectGrid(grid);
        LayoutFootgunDetector.InspectGrid(grid);

        Assert.Single(_warnings);
    }
}
