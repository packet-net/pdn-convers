using Convers.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Convers.Host.Tests;

/// <summary>
/// Boots the exact production wiring (<see cref="HostComposition.Build"/>) on an ephemeral port and
/// asserts the composed host comes up and answers /healthz — the W0 liveness contract the deploy
/// script and the node supervisor rely on. Later waves extend this against a fake RHP node.
/// </summary>
public class HostCompositionTests
{
    [Fact]
    public async Task Build_ComposedHost_Serves_Healthz()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("convers-host-compose-");
        string? prev = Environment.GetEnvironmentVariable("PDN_APP_STATE");
        try
        {
            // Ephemeral web port so the test never collides with the configured 18091.
            File.WriteAllText(
                Path.Combine(dir.FullName, ConversHostConfigFile.FileName),
                "callsign: G0ABC\nweb:\n  bind: 127.0.0.1\n  port: 0\n");
            Environment.SetEnvironmentVariable("PDN_APP_STATE", dir.FullName);

            WebApplication app = HostComposition.Build([]);
            await app.StartAsync();
            try
            {
                string baseUrl = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.First();

                using var client = new HttpClient();
                HttpResponseMessage resp = await client.GetAsync(new Uri($"{baseUrl}/healthz"));

                Assert.True(resp.IsSuccessStatusCode);
                Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDN_APP_STATE", prev);
            dir.Delete(recursive: true);
        }
    }
}
