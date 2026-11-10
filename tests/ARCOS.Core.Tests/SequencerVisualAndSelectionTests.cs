using System;
using System.Collections.Generic;
using System.Linq;
using ARCOS.App.ViewModels;
using Xunit;

namespace ARCOS.Core.Tests;

public class SequencerVisualAndSelectionTests
{
    [Fact]
    public void VisualDecoupling_PCIndexAndRangeSelectionAreIndependent()
    {
        // 驗證：指令 INDEX (PC) 僅由第 1 欄 (i 欄) 控制，Command/Parameters 欄不受 PC 影響染紅
        var row = new SequencerRowViewModel
        {
            LineNumber = 2,
            Command = "System@Delay",
            Parameter = "0.1"
        };

        // 初始狀態：皆為預設
        Assert.True(row.IsDefaultState);
        Assert.Equal("Transparent", row.CellBackground);
        Assert.Equal("Transparent", row.CellBorderBrush);
        Assert.Equal("0", row.CellBorderThickness);

        // 設為目前 PC 行：Command/Parameters 依然保持 Transparent (僅 i 欄以 IsCurrentPC 顯示粉紅膠囊)
        row.IsCurrentPC = true;
        Assert.True(row.IsCurrentPC);
        Assert.False(row.IsDefaultState);
        Assert.Equal("Transparent", row.CellBackground);
        Assert.Equal("Transparent", row.CellBorderBrush);
        Assert.Equal("0", row.CellBorderThickness);

        // 當此行同時被納入範圍選取 (IsRangeSelected = true)
        // Command/Parameters 欄顯示選取天空藍 #E0F2FE，而 i 欄依然維持 IsCurrentPC = true，互不干擾
        row.IsRangeSelected = true;
        Assert.True(row.IsCurrentPC);
        Assert.True(row.IsRangeSelected);
        Assert.Equal("#E0F2FE", row.CellBackground);
        Assert.Equal("#BAE6FD", row.CellBorderBrush);
        Assert.Equal("1", row.CellBorderThickness);
    }

    [Fact]
    public void RangeSelection_ShowsBlueHighlightOnCommandAndParameters()
    {
        var row = new SequencerRowViewModel
        {
            LineNumber = 3,
            Command = "System@Delay",
            Parameter = "0.2"
        };

        row.IsRangeSelected = true;
        row.IsCurrentPC = false;

        Assert.True(row.IsRangeSelected);
        Assert.False(row.IsCurrentPC);
        Assert.Equal("#E0F2FE", row.CellBackground);
        Assert.Equal("#BAE6FD", row.CellBorderBrush);
        Assert.Equal("1", row.CellBorderThickness);
    }

    [Fact]
    public void MultiRowSelection_RangeSelectedCellsTurnBlueWhilePCIndexIsIndependent()
    {
        // 模擬 5 行程式碼中，PC 位於 Line 2，使用者從 Line 1 選到 Line 4
        var rows = Enumerable.Range(0, 5).Select(i => new SequencerRowViewModel
        {
            LineNumber = i,
            Command = $"Command_{i}",
            Parameter = "0.1"
        }).ToList();

        // PC 目前位於 Line 2
        rows[2].IsCurrentPC = true;

        // 範圍選取 Line 1 到 Line 4
        int start = 1;
        int end = 4;
        foreach (var r in rows)
        {
            r.IsRangeSelected = (r.LineNumber >= start && r.LineNumber <= end);
        }

        // Line 0 未選取：透明待命，非 PC
        Assert.Equal("Transparent", rows[0].CellBackground);
        Assert.Equal("Transparent", rows[0].CellBorderBrush);
        Assert.False(rows[0].IsCurrentPC);

        // Line 1 選取但非 PC：Command/Parameters 藍底藍框，非 PC
        Assert.Equal("#E0F2FE", rows[1].CellBackground);
        Assert.Equal("#BAE6FD", rows[1].CellBorderBrush);
        Assert.False(rows[1].IsCurrentPC);

        // Line 2 選取且為 PC：Command/Parameters 顯示選取藍底，同時 i 欄維持 IsCurrentPC = true (粉色膠囊)，兩者獨立共存！
        Assert.Equal("#E0F2FE", rows[2].CellBackground);
        Assert.Equal("#BAE6FD", rows[2].CellBorderBrush);
        Assert.Equal("1", rows[2].CellBorderThickness);
        Assert.True(rows[2].IsCurrentPC);

        // Line 3、4 選取但非 PC：Command/Parameters 藍底藍框
        Assert.Equal("#E0F2FE", rows[3].CellBackground);
        Assert.Equal("#E0F2FE", rows[4].CellBackground);
        Assert.False(rows[3].IsCurrentPC);
        Assert.False(rows[4].IsCurrentPC);
    }

    [Fact]
    public void PropertyChanged_FiresCorrectlyWhenPCToggled()
    {
        var row = new SequencerRowViewModel { LineNumber = 1 };
        var changedProps = new List<string>();
        row.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        row.IsCurrentPC = true;

        Assert.Contains(nameof(row.IsCurrentPC), changedProps);
        Assert.Contains(nameof(row.IsRangeSelectedAndNotPC), changedProps);
        Assert.Contains(nameof(row.IsDefaultState), changedProps);
    }

    [Fact]
    public void PropertyChanged_FiresCorrectlyWhenSelectionToggled()
    {
        var row = new SequencerRowViewModel { LineNumber = 1 };
        var changedProps = new List<string>();
        row.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        row.IsRangeSelected = true;

        Assert.Contains(nameof(row.IsRangeSelected), changedProps);
        Assert.Contains(nameof(row.IsRangeSelectedAndNotPC), changedProps);
        Assert.Contains(nameof(row.IsDefaultState), changedProps);
        Assert.Contains(nameof(row.CellBackground), changedProps);
        Assert.Contains(nameof(row.CellBorderBrush), changedProps);
        Assert.Contains(nameof(row.CellBorderThickness), changedProps);
    }
}
