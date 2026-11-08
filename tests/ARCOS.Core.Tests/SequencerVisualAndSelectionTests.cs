using System;
using System.Collections.Generic;
using System.Linq;
using ARCOS.App.ViewModels;
using Xunit;

namespace ARCOS.Core.Tests;

public class SequencerVisualAndSelectionTests
{
    [Fact]
    public void VisualPriority_PCIndexTakesPrecedenceOverRangeSelection()
    {
        // 規則 1 驗證：選取區域高亮絕不能蓋住 PC Index 紅框與粉紅標記
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

        // 設為目前 PC 行
        row.IsCurrentPC = true;
        Assert.False(row.IsDefaultState);
        Assert.Equal("#FFE4E6", row.CellBackground);
        Assert.Equal("#EF4444", row.CellBorderBrush);
        Assert.Equal("1.5", row.CellBorderThickness);

        // 當此行同時被納入範圍選取 (IsRangeSelected = true)
        row.IsRangeSelected = true;

        // 核心驗證：CellBackground 與 CellBorderBrush 必須依然維持 PC 紅框與粉色指示，絕不可變為天空藍 #E0F2FE！
        Assert.Equal("#FFE4E6", row.CellBackground);
        Assert.Equal("#EF4444", row.CellBorderBrush);
        Assert.Equal("1.5", row.CellBorderThickness);
        Assert.False(row.IsRangeSelectedAndNotPC);
    }

    [Fact]
    public void RangeSelection_WhenNotPC_ShowsBlueHighlightAndBorder()
    {
        var row = new SequencerRowViewModel
        {
            LineNumber = 3,
            Command = "System@Delay",
            Parameter = "0.2"
        };

        row.IsRangeSelected = true;
        row.IsCurrentPC = false;

        Assert.True(row.IsRangeSelectedAndNotPC);
        Assert.Equal("#E0F2FE", row.CellBackground);
        Assert.Equal("#BAE6FD", row.CellBorderBrush);
        Assert.Equal("1", row.CellBorderThickness);
    }

    [Fact]
    public void MultiRowSelection_PCLinesStayRedWhileOtherLinesTurnBlue()
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

        // Line 0 未選取：透明待命
        Assert.Equal("Transparent", rows[0].CellBackground);
        Assert.Equal("Transparent", rows[0].CellBorderBrush);

        // Line 1 選取但非 PC：藍底藍框
        Assert.Equal("#E0F2FE", rows[1].CellBackground);
        Assert.Equal("#BAE6FD", rows[1].CellBorderBrush);
        Assert.True(rows[1].IsRangeSelectedAndNotPC);

        // Line 2 選取且為 PC：紅框粉底，絕不被藍底遮蔽！
        Assert.Equal("#FFE4E6", rows[2].CellBackground);
        Assert.Equal("#EF4444", rows[2].CellBorderBrush);
        Assert.Equal("1.5", rows[2].CellBorderThickness);
        Assert.False(rows[2].IsRangeSelectedAndNotPC);

        // Line 3、4 選取但非 PC：藍底藍框
        Assert.Equal("#E0F2FE", rows[3].CellBackground);
        Assert.Equal("#E0F2FE", rows[4].CellBackground);
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
        Assert.Contains(nameof(row.CellBackground), changedProps);
        Assert.Contains(nameof(row.CellBorderBrush), changedProps);
        Assert.Contains(nameof(row.CellBorderThickness), changedProps);
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
