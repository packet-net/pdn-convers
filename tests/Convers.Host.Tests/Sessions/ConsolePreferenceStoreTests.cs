using Convers.Console;
using Convers.Host.Sessions;

namespace Convers.Host.Tests.Sessions;

/// <summary>
/// The per-callsign console-surface preference (design decision 9): plain by default, classic when set,
/// persisted across restarts.
/// </summary>
public sealed class ConsolePreferenceStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("convers-prefs-test-");

    [Fact]
    public void UnknownCallsign_DefaultsToPlain()
    {
        ConsolePreferenceStore store = ConsolePreferenceStore.Open(null);
        Assert.Equal(ConsoleInterface.Plain, store.GetInterface("G4ABC"));
    }

    [Fact]
    public void SetInterface_PersistsAndSurvivesReopen()
    {
        string path = Path.Combine(_dir.FullName, "prefs.json");

        ConsolePreferenceStore first = ConsolePreferenceStore.Open(path);
        first.SetInterface("g4abc", ConsoleInterface.Classic);

        // A returning user keeps their classic surface; an unknown one stays plain.
        ConsolePreferenceStore reopened = ConsolePreferenceStore.Open(path);
        Assert.Equal(ConsoleInterface.Classic, reopened.GetInterface("G4ABC"));
        Assert.Equal(ConsoleInterface.Plain, reopened.GetInterface("M0LTE"));
    }

    [Fact]
    public void Open_CorruptFile_StartsClean()
    {
        string path = Path.Combine(_dir.FullName, "corrupt.json");
        File.WriteAllText(path, "{ this is not valid json");

        ConsolePreferenceStore store = ConsolePreferenceStore.Open(path);
        Assert.Equal(ConsoleInterface.Plain, store.GetInterface("G4ABC"));
    }

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
