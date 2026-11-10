using System;
using System.Collections.Generic;
using System.IO;
using ARCOS.Core.Common;
using ARCOS.Core.Execution;
using ARCOS.Core.Serialization;
using Xunit;

namespace ARCOS.Core.Tests;

public class ProgProjectFormatTests
{
    [Fact]
    public void SaveAndLoadProgFile_RoundtripPreservesSectionsAndNames()
    {
        string tempProg = Path.Combine(Path.GetTempPath(), $"test_project_{Guid.NewGuid():N}.prog");
        try
        {
            var p0 = new ProgramTable { ProgramNumber = 0, Name = "MainManager" };
            p0.Add(CommandItem.Parse("System@Log Message\tMain Manager Online", 1));
            p0.Add(CommandItem.Parse("Thread@Run\t1", 2));

            var p1 = new ProgramTable { ProgramNumber = 1, Name = "Feeder_Station" };
            p1.Add(CommandItem.Parse("System@ProgramSection\t1, Feeder_Station", 1));
            p1.Add(CommandItem.Parse("IO@SetPort\tP0.1,1", 2));
            p1.Add(CommandItem.Parse("System@Delay\t50", 3));

            var progs = new List<ProgramTable> { p0, p1 };
            ScriptSerializer.SaveProgFile(tempProg, progs);

            Assert.True(File.Exists(tempProg));
            string rawContent = File.ReadAllText(tempProg);
            Assert.Contains("[Program 0]", rawContent);
            Assert.Contains("Name=\"MainManager\"", rawContent);
            Assert.Contains("[Program 1]", rawContent);
            Assert.Contains("Name=\"Feeder_Station\"", rawContent);

            var loaded = ScriptSerializer.LoadProgFile(tempProg);
            Assert.Equal(2, loaded.Count);

            var loadedP0 = loaded.Find(p => p.ProgramNumber == 0);
            Assert.NotNull(loadedP0);
            Assert.Equal("MainManager", loadedP0.Name);
            Assert.Equal(2, loadedP0.Count);

            var loadedP1 = loaded.Find(p => p.ProgramNumber == 1);
            Assert.NotNull(loadedP1);
            Assert.Equal("Feeder_Station", loadedP1.Name);
            Assert.Equal(3, loadedP1.Count);
        }
        finally
        {
            if (File.Exists(tempProg)) File.Delete(tempProg);
        }
    }

    [Fact]
    public void ResolveProgPath_ResolvesProgAndProEqually()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"arcos_path_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string oldRoot = ArcosPaths.RootDirectory;
        ArcosPaths.RootDirectory = tempDir;

        try
        {
            string progFile = Path.Combine(tempDir, "Production.prog");
            File.WriteAllText(progFile, "[Program 0]\nName=\"Prod\"\n");

            string resolvedProg = ArcosPaths.ResolveProgPath("Production");
            Assert.Equal(progFile, resolvedProg);

            string legacyPro = Path.Combine(tempDir, "Legacy.pro");
            File.WriteAllText(legacyPro, "[Program 0]\nName=\"Legacy\"\n");

            string resolvedLegacy = ArcosPaths.ResolveProgPath("Legacy");
            Assert.Equal(legacyPro, resolvedLegacy);
        }
        finally
        {
            ArcosPaths.RootDirectory = oldRoot;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void InitFile_RemainsStandaloneSingleSection()
    {
        string tempInit = Path.Combine(Path.GetTempPath(), $"subthread_{Guid.NewGuid():N}.init");
        try
        {
            var p = new ProgramTable { ProgramNumber = 5, Name = "WorkerSubThread" };
            p.Add(CommandItem.Parse("System@Log Message\tWorker Active", 1));
            p.Add(CommandItem.Parse("System@Delay\t100", 2));

            ScriptSerializer.SaveInitFile(tempInit, p);
            Assert.True(File.Exists(tempInit));

            var loaded = ScriptSerializer.LoadInitFile(tempInit, 5, "WorkerSubThread");
            Assert.Equal(5, loaded.ProgramNumber);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("System@Log Message", loaded[1].Command);
            Assert.Equal("Worker Active", loaded[1].RawParameter);
        }
        finally
        {
            if (File.Exists(tempInit)) File.Delete(tempInit);
        }
    }
}
