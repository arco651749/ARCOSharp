using MVCON.Core.Engines;
using MVCON.Core.Model;
using Xunit;

namespace MVCON.Core.Tests;

public class PointEngineTests
{
    [Fact]
    public void PointExpression_BasicAndIndex_ShouldWork()
    {
        var io = new IoEngine();
        var points = new PointEngine(io);

        points.SetPoint("P1", new MachinePoint(10, 20, 30));
        var pt = points.GetPoint("P1");
        Assert.Equal(10, pt.X);
        Assert.Equal(20, pt.Y);
        Assert.Equal(30, pt.Z);

        // 陣列追加與索引存取
        points.AppendPoint("Tray", new MachinePoint(100, 200, 300));
        points.AppendPoint("Tray", new MachinePoint(110, 210, 310));

        var pt0 = points.GetPoint("Tray[0]");
        var pt1 = points.GetPoint("Tray[1]");
        Assert.Equal(100, pt0.X);
        Assert.Equal(110, pt1.X);
    }

    [Fact]
    public void AxisFilterAndOverride_ShouldSetUnspecifiedAxesToInf()
    {
        var points = new PointEngine();
        points.SetPoint("PBase", new MachinePoint(50, 60, 70, 10, 20, 30));

        // PBase@XY：僅保留 X, Y，其餘設為 inf
        var xyPt = points.GetPoint("PBase@XY");
        Assert.Equal(50, xyPt.X);
        Assert.Equal(60, xyPt.Y);
        Assert.True(double.IsInfinity(xyPt.Z));
        Assert.True(double.IsInfinity(xyPt.U));

        // PBase@Z(99)：覆寫 Z 為 99，其餘設為 inf
        var zPt = points.GetPoint("PBase@Z(99)");
        Assert.Equal(99, zPt.Z);
        Assert.True(double.IsInfinity(zPt.X));
        Assert.True(double.IsInfinity(zPt.Y));
    }

    [Fact]
    public void Pallet1D_ShouldGenerateInterpolatedPoints_AndSyncLastFlag()
    {
        var io = new IoEngine();
        var points = new PointEngine(io);

        points.SetPoint("Line1D", new MachinePoint(0, 0, 0));
        points.AppendPoint("Line1D", new MachinePoint(100, 200, 0));

        // 產生 5 個等距點位 (0, 25, 50, 75, 100)
        points.GeneratePallet1D("Line1D", 5);

        var list = points.GetPointArray("Line1D");
        Assert.Equal(5, list.Count);
        Assert.Equal(0, list[0].X);
        Assert.Equal(50, list[2].X);
        Assert.Equal(100, list[4].X);
        Assert.False(io.GetPort("Line1D#Last?"));

        // 取點並前進料盤游標
        for (int i = 0; i < 4; i++)
        {
            points.GetAndMoveNextPallet("Line1D");
        }
        // 第 5 點到達終點，應觸發 Last?
        Assert.True(io.GetPort("Line1D#Last?"));
    }

    [Fact]
    public void Pallet2D_ShouldGenerateMatrixPoints()
    {
        var points = new PointEngine();

        // [0] 原點 (0,0), [1] X終點 (20,0), [2] Y終點 (0,10)
        points.SetPoint("Grid", new MachinePoint(0, 0, 0));
        points.AppendPoint("Grid", new MachinePoint(20, 0, 0));
        points.AppendPoint("Grid", new MachinePoint(0, 10, 0));

        // 3x2 陣列 (X方向3點：0,10,20；Y方向2點：0,10)
        points.GeneratePallet2D("Grid", countX: 3, countY: 2);

        var list = points.GetPointArray("Grid");
        Assert.Equal(6, list.Count);
        Assert.Equal(0, list[0].X);
        Assert.Equal(0, list[0].Y);
        Assert.Equal(20, list[2].X);
        Assert.Equal(0, list[2].Y);
        Assert.Equal(20, list[5].X);
        Assert.Equal(10, list[5].Y);
    }

    [Fact]
    public void SelfMotion_ShouldApplyRigidBodyOffset()
    {
        var points = new PointEngine();
        points.SetPoint("PBase", new MachinePoint(10, 10, 0));
        points.SetPoint("PNew", new MachinePoint(15, 12, 0)); // 偏移 dx=5, dy=2

        points.SetPoint("TargetPt", new MachinePoint(100, 200, 0));

        points.SelfMotion("TargetPt", "PBase", "PNew");

        var modified = points.GetPoint("TargetPt");
        Assert.Equal(105, modified.X);
        Assert.Equal(202, modified.Y);
    }

    [Fact]
    public void CheckInRangeXY_ShouldUpdateIoFlag()
    {
        var io = new IoEngine();
        var points = new PointEngine(io);

        points.SetPoint("LimitBox", new MachinePoint(50, 50, 0));

        points.SetPoint("InsidePt", new MachinePoint(30, 40, 0));
        bool inside = points.CheckInRangeXY("InsidePt", "LimitBox");
        Assert.True(inside);
        Assert.True(io.GetPort("InsidePt#InRange?"));

        points.SetPoint("OutsidePt", new MachinePoint(60, 40, 0));
        bool outside = points.CheckInRangeXY("OutsidePt", "LimitBox");
        Assert.False(outside);
        Assert.False(io.GetPort("OutsidePt#InRange?"));
    }
}
