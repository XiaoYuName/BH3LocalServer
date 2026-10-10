using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Server.Configuration;
using BH3.Server.Hosting;
using BH3.Tests;

namespace BH3.Server.Tests;

public sealed class GmApiTests
{
    [Fact]public async Task GmWritesRequireLocalSameOriginHeaderAndPersistToGameState()
    {
        using var temp=new TestDirectory();await using var runtime=new ServerRuntime(new ServerConfig{BaseDirectory=temp.Path,DatabasePath="gm.db",LogDirectory="logs",GamePort=0,HealthPort=0,TransportMode="kcp"},TextWriter.Null);
        var ct=TestContext.Current.CancellationToken;await runtime.StartAsync(ct,allowEphemeralPorts:true);
        var store=new LobbyStore(runtime.Composition.Database);store.EnsurePlayer(10001,"舰长",new(88,80,101,20001,59101,1,[]));
        using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false}){BaseAddress=new(runtime.HealthAddress)};
        var command=new GmCommand(Guid.NewGuid().ToString(),"grant",[new("hcoin",0,500)]);
        using var blocked=await http.PostAsJsonAsync("/api/gm/player/10001",command,ct);Assert.Equal(HttpStatusCode.Forbidden,blocked.StatusCode);
        http.DefaultRequestHeaders.Add("X-BH3-GM","1");http.DefaultRequestHeaders.Add("Origin","https://example.invalid");
        using var cross=await http.PostAsJsonAsync("/api/gm/player/10001",command,ct);Assert.Equal(HttpStatusCode.Forbidden,cross.StatusCode);http.DefaultRequestHeaders.Remove("Origin");
        using var success=await http.PostAsJsonAsync("/api/gm/player/10001",command,ct);Assert.Equal(HttpStatusCode.OK,success.StatusCode);
        using var replay=await http.PostAsJsonAsync("/api/gm/player/10001",command,ct);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(500u,store.Read(10001).Hcoin);
        using var snapshot=JsonDocument.Parse(await http.GetStringAsync("/api/gm/player/10001",ct));Assert.Equal(500,snapshot.RootElement.GetProperty("lobby").GetProperty("hcoin").GetInt32());
        using var catalog=JsonDocument.Parse(await http.GetStringAsync("/api/gm/catalog?kind=weapon&q=20001",ct));Assert.True(catalog.RootElement.GetProperty("total").GetInt32()>0);
        using var unknown=await http.GetAsync("/api/gm/player/10002",ct);Assert.Equal(HttpStatusCode.NotFound,unknown.StatusCode);
        http.DefaultRequestHeaders.Host="untrusted.invalid";using var rebound=await http.GetAsync("/api/gm/accounts",ct);Assert.Equal(HttpStatusCode.Forbidden,rebound.StatusCode);
    }
}
