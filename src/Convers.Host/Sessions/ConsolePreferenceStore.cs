using System.Collections.Concurrent;
using System.Text.Json;
using Convers.Console;
using Convers.Core;

namespace Convers.Host.Sessions;

/// <summary>
/// The per-callsign console-surface preference (design decision 9): which input surface — plain
/// (default) or classic — a user's RF sessions present. Persisted as a small JSON map in the package
/// state dir so a user's choice survives restarts, mirroring pdn-bbs's <c>JsonUserSettingsStore</c>.
/// Thread-safe; reads default to <see cref="ConsoleInterface.Plain"/> for an unknown callsign.
/// </summary>
public sealed class ConsolePreferenceStore : IConsolePreferences
{
    private readonly string? _path;
    private readonly ConcurrentDictionary<string, ConsoleInterface> _prefs;
    private readonly object _writeGate = new();

    private ConsolePreferenceStore(string? path, ConcurrentDictionary<string, ConsoleInterface> prefs)
    {
        _path = path;
        _prefs = prefs;
    }

    /// <summary>
    /// Opens the store at <paramref name="path"/>, loading any persisted preferences. A missing or
    /// unreadable file starts empty (every user defaults to plain). When <paramref name="path"/> is
    /// null the store is purely in-memory (tests).
    /// </summary>
    public static ConsolePreferenceStore Open(string? path)
    {
        var prefs = new ConcurrentDictionary<string, ConsoleInterface>(StringComparer.Ordinal);
        if (path is { Length: > 0 } && File.Exists(path))
        {
            try
            {
                Dictionary<string, string>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    foreach ((string call, string surface) in loaded)
                    {
                        if (Enum.TryParse(surface, ignoreCase: true, out ConsoleInterface parsed))
                        {
                            prefs[Callsigns.Normalize(call)] = parsed;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Corrupt/unreadable preference file: start clean rather than fail startup.
            }
        }

        return new ConsolePreferenceStore(path, prefs);
    }

    /// <inheritdoc/>
    public ConsoleInterface GetInterface(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        return _prefs.GetValueOrDefault(Callsigns.Normalize(callsign), ConsoleInterface.Plain);
    }

    /// <summary>Sets and persists a callsign's surface preference.</summary>
    public void SetInterface(string callsign, ConsoleInterface surface)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        _prefs[Callsigns.Normalize(callsign)] = surface;
        Persist();
    }

    private void Persist()
    {
        if (_path is not { Length: > 0 })
        {
            return;
        }

        lock (_writeGate)
        {
            try
            {
                Dictionary<string, string> snapshot =
                    _prefs.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
                File.WriteAllText(_path, JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a failed persist must not crash a session.
            }
        }
    }
}

/// <summary>
/// The per-callsign console-surface lookup the demux uses to pick a session's surface. Abstracted so the
/// demux tests can supply a fixed preference without touching disk.
/// </summary>
public interface IConsolePreferences
{
    /// <summary>The surface a user's sessions present; <see cref="ConsoleInterface.Plain"/> by default.</summary>
    ConsoleInterface GetInterface(string callsign);
}
