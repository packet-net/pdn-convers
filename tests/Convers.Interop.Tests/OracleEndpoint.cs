using System.Globalization;

namespace Convers.Interop.Tests;

/// <summary>
/// Where the conversd-saupp oracle (docker/compose.oracle.yml) listens. Defaults to the
/// loopback-published port; overridable for a private oracle copy via environment variables
/// (the pdn-bbs OracleFixture precedent).
/// </summary>
internal static class OracleEndpoint
{
    /// <summary>Oracle host — <c>CONVERS_ORACLE_HOST</c> or 127.0.0.1.</summary>
    public static string Host =>
        Environment.GetEnvironmentVariable("CONVERS_ORACLE_HOST") is { Length: > 0 } h ? h : "127.0.0.1";

    /// <summary>Oracle convers port — <c>CONVERS_ORACLE_PORT</c> or 3600.</summary>
    public static int Port =>
        int.TryParse(
            Environment.GetEnvironmentVariable("CONVERS_ORACLE_PORT"),
            NumberStyles.None, CultureInfo.InvariantCulture, out int p)
            ? p
            : 3600;
}
