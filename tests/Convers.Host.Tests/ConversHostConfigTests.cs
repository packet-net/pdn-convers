using Convers.Host;

namespace Convers.Host.Tests;

public class ConversHostConfigTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void LoadOrCreate_WritesACommentedDefaultOnFirstRun()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("convers-host-test-");
        try
        {
            string path = Path.Combine(dir.FullName, ConversHostConfigFile.FileName);
            Assert.False(File.Exists(path));

            (ConversHostConfig config, bool created) = ConversHostConfigFile.LoadOrCreate(dir.FullName, NoEnv);

            Assert.True(created);
            Assert.True(File.Exists(path));
            Assert.Equal(ConversHostConfig.PlaceholderCallsign, config.Callsign);
            Assert.Equal(ConversHostConfig.PlaceholderDefaultChannel, config.DefaultChannel);
            Assert.Equal(18091, config.Web.Port);

            // Second load does not recreate.
            (_, bool createdAgain) = ConversHostConfigFile.LoadOrCreate(dir.FullName, NoEnv);
            Assert.False(createdAgain);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DefaultYaml_RoundTripsThroughTheParser()
    {
        ConversHostConfig config = ConversHostConfigFile.Parse(ConversHostConfigFile.DefaultYaml);

        Assert.Equal("N0CALL", config.Callsign);
        Assert.Equal(3333, config.DefaultChannel);
        Assert.Equal("127.0.0.1", config.Web.Bind);
        Assert.Null(config.Uplink.Provider);    // no parent yet — uplink unset
    }

    [Fact]
    public void ApplyEnvironment_FallsBackToSupervisorRhpEndpoint()
    {
        var env = new Dictionary<string, string?>
        {
            ["PDN_RHP_HOST"] = "10.1.2.3",
            ["PDN_RHP_PORT"] = "9100",
        };
        ConversHostConfig parsed = ConversHostConfigFile.Parse("callsign: G0ABC");

        ConversHostConfig resolved = ConversHostConfigFile.ApplyEnvironment(
            parsed, k => env.GetValueOrDefault(k));

        Assert.Equal("10.1.2.3", resolved.Rhp.Host);
        Assert.Equal(9100, resolved.Rhp.Port);
    }

    [Fact]
    public void ApplyEnvironment_DefaultsToLoopback9000WhenUnset()
    {
        ConversHostConfig resolved = ConversHostConfigFile.ApplyEnvironment(new ConversHostConfig(), NoEnv);

        Assert.Equal("127.0.0.1", resolved.Rhp.Host);
        Assert.Equal(9000, resolved.Rhp.Port);
    }
}
