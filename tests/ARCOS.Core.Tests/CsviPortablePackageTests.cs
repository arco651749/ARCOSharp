using System.Security.Cryptography;
using System.Text.Json;
using ARCOS.Core.Csvi;
using Xunit;

namespace ARCOS.Core.Tests;

public class CsviPortablePackageTests
{
    [Fact]
    public void PortableOpen_PackAndUnpack_ShouldMatchOriginalFiles()
    {
        var sources = new Dictionary<string, string>
        {
            ["Logic.cs"] = "public class Logic { public int Run() => 42; }",
            ["Wizard.axaml"] = "<UserControl xmlns=\"https://github.com/avaloniaui\"></UserControl>"
        };

        byte[] bundle = CsviSourceBundle.Pack(sources, CsviPackagingMode.PortableOpen);
        Assert.NotEmpty(bundle);

        var unpacked = CsviSourceBundle.Unpack(bundle, CsviPackagingMode.PortableOpen);
        Assert.Equal(2, unpacked.Count);
        Assert.Equal(sources["Logic.cs"], unpacked["Logic.cs"]);
        Assert.Equal(sources["Wizard.axaml"], unpacked["Wizard.axaml"]);
    }

    [Fact]
    public void PasswordLocked_PackAndUnpack_WithCorrectPassword_ShouldSucceed()
    {
        var sources = new Dictionary<string, string>
        {
            ["Secret.cs"] = "// Confidential industrial algorithm"
        };
        string password = "SecretMasterPassword123!";

        byte[] encryptedBundle = CsviSourceBundle.Pack(sources, CsviPackagingMode.PasswordLocked, password);
        Assert.NotEmpty(encryptedBundle);

        var unpacked = CsviSourceBundle.Unpack(encryptedBundle, CsviPackagingMode.PasswordLocked, password);
        Assert.Single(unpacked);
        Assert.Equal(sources["Secret.cs"], unpacked["Secret.cs"]);
    }

    [Fact]
    public void PasswordLocked_Unpack_WithWrongPassword_ShouldThrow()
    {
        var sources = new Dictionary<string, string>
        {
            ["Secret.cs"] = "// Confidential code"
        };
        byte[] encrypted = CsviSourceBundle.Pack(sources, CsviPackagingMode.PasswordLocked, "pass1");

        Assert.ThrowsAny<CryptographicException>(() =>
        {
            CsviSourceBundle.Unpack(encrypted, CsviPackagingMode.PasswordLocked, "wrong-pass");
        });
    }

    [Fact]
    public void StrippedRelease_ShouldReturnEmptyByteArray()
    {
        var sources = new Dictionary<string, string>
        {
            ["Any.cs"] = "..."
        };

        byte[] bundle = CsviSourceBundle.Pack(sources, CsviPackagingMode.StrippedRelease);
        Assert.Empty(bundle);

        var unpacked = CsviSourceBundle.Unpack(bundle, CsviPackagingMode.StrippedRelease);
        Assert.Empty(unpacked);
    }

    [Fact]
    public void Manifest_Serialization_ShouldWorkCorrectly()
    {
        var manifest = new CsviManifest
        {
            Module = "IO",
            CommandKey = "IO@SetPort",
            ComponentType = CsviComponentType.Command,
            PackagingMode = CsviPackagingMode.PortableOpen,
            RevisionHistory = new List<CsviRevisionEntry>
            {
                new() { Revision = 1, Summary = "Initial release" }
            }
        };

        string json = JsonSerializer.Serialize(manifest);
        var deserialized = JsonSerializer.Deserialize<CsviManifest>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("IO@SetPort", deserialized.CommandKey);
        Assert.Equal(CsviPackagingMode.PortableOpen, deserialized.PackagingMode);
        Assert.Single(deserialized.RevisionHistory);
    }
}
