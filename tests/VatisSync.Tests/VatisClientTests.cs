using System.Text.Json.Nodes;
using VatisSync.Core;

namespace VatisSync.Tests;

public class VatisClientTests
{
    /// <summary>A scripted vATIS: records what was sent and replays queued replies per connection.</summary>
    private sealed class FakeVatis : IVatisSocketFactory
    {
        public List<JsonNode> Sent { get; } = [];
        public Func<JsonNode, string[]> Reply { get; set; } = _ => [];
        public bool Refuse { get; set; }
        public int Connections { get; private set; }

        public Task<IVatisSocket> ConnectAsync(CancellationToken ct)
        {
            Connections++;
            if (Refuse) throw new System.Net.WebSockets.WebSocketException("refused");
            return Task.FromResult<IVatisSocket>(new Socket(this));
        }

        private sealed class Socket(FakeVatis owner) : IVatisSocket
        {
            private readonly Queue<string> _replies = new();

            public Task SendAsync(string text, CancellationToken ct)
            {
                var node = JsonNode.Parse(text)!;
                owner.Sent.Add(node);
                foreach (var r in owner.Reply(node)) _replies.Enqueue(r);
                return Task.CompletedTask;
            }

            public Task<string?> ReceiveAsync(TimeSpan timeout, CancellationToken ct) =>
                Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private const string StationsReply = """
        {"type":"stations","stations":[
          {"id":"a1","name":"KSFO","atisType":"Combined","presets":["D-ATIS","Other"]},
          {"id":"a2","name":"KATL","atisType":"Arrival","presets":["D-ATIS"]},
          {"id":"a3","name":"KATL","atisType":"Departure","presets":["D-ATIS"]},
          {"id":"a4","name":"KOAK","atisType":"Combined","presets":["Manual"]}
        ]}
        """;

    [Fact]
    public async Task Stations_get_suffixes_and_datis_flag()
    {
        var fake = new FakeVatis { Reply = _ => [StationsReply] };
        var stations = await new VatisClient(fake).GetStationsAsync();
        Assert.Equal(["KSFO", "KATL_A", "KATL_D", "KOAK"], stations.Select(s => s.Key));
        Assert.Equal([true, true, true, false], stations.Select(s => s.HasDatisPreset));
        Assert.Equal("getStations", (string?)fake.Sent[0]["type"]);
    }

    [Fact]
    public async Task Stations_skips_unrelated_messages_before_the_stations_reply()
    {
        var fake = new FakeVatis { Reply = _ => ["""{"type":"hello"}""", "garbage", StationsReply] };
        Assert.Equal(4, (await new VatisClient(fake).GetStationsAsync()).Count);
    }

    [Fact]
    public async Task Stations_throws_friendly_exception_when_vatis_is_not_listening()
    {
        var fake = new FakeVatis { Refuse = true };
        await Assert.ThrowsAsync<VatisUnavailableException>(() => new VatisClient(fake).GetStationsAsync());
    }

    [Fact]
    public async Task Stations_throws_when_vatis_never_sends_a_stations_reply()
    {
        var fake = new FakeVatis { Reply = _ => ["""{"type":"loading"}"""] };
        await Assert.ThrowsAsync<VatisUnavailableException>(() => new VatisClient(fake).GetStationsAsync());
    }

    [Fact]
    public async Task GetAtis_reads_status_and_text()
    {
        var fake = new FakeVatis
        {
            Reply = _ => ["""{"type":"atis","value":{"networkConnectionStatus":"Connected","airportConditions":"RWY 28","notams":"TWY B CLSD"}}"""],
        };
        var s = await new VatisClient(fake).GetAtisAsync("a1");
        Assert.True(s.IsConnected);
        Assert.Equal(("RWY 28", "TWY B CLSD"), (s.AirportConditions, s.Notams));
        Assert.Equal("a1", (string?)fake.Sent[0]["value"]!["id"]);
    }

    [Fact]
    public async Task Contractions_parse_into_variable_text_pairs()
    {
        var fake = new FakeVatis
        {
            Reply = _ => ["""{"stations":[{"contractions":{"RWY":{"text":"RUNWAY"},"TWY":{"text":"TAXIWAY"}}}]}"""],
        };
        var c = await new VatisClient(fake).GetContractionsAsync("KATL_D");
        Assert.Equal([new("RWY", "RUNWAY"), new("TWY", "TAXIWAY")], c);
        var sent = fake.Sent[0]["value"]!;
        Assert.Equal("KATL", (string?)sent["station"]);
        Assert.Equal("Departure", (string?)sent["atisType"]);
    }

    [Fact]
    public async Task Contractions_omit_atisType_for_combined_and_use_Arrival_for_arr()
    {
        var fake = new FakeVatis { Reply = _ => [] };
        var client = new VatisClient(fake);
        await client.GetContractionsAsync("KSFO");
        await client.GetContractionsAsync("KATL_A");
        Assert.Null(fake.Sent[0]["value"]!["atisType"]);
        Assert.Equal("Arrival", (string?)fake.Sent[1]["value"]!["atisType"]);
    }

    [Theory]
    [InlineData("""{"type":"error","value":{"message":"duplicate variable"}}""")]
    [InlineData("""{"stations":[]}""")]
    public async Task Contractions_error_or_empty_reply_gives_empty_list(string reply)
    {
        var fake = new FakeVatis { Reply = _ => [reply] };
        Assert.Empty(await new VatisClient(fake).GetContractionsAsync("KSFO"));
    }

    [Fact]
    public async Task Contractions_no_reply_gives_empty_list()
    {
        Assert.Empty(await new VatisClient(new FakeVatis()).GetContractionsAsync("KSFO"));
    }

    [Fact]
    public async Task ConfigureAtis_sends_the_expected_message()
    {
        var fake = new FakeVatis();
        await new VatisClient(fake).ConfigureAtisAsync("a1", "RWY 28", "TWY B CLSD", syncAtisLetter: true);
        var m = fake.Sent.Single();
        Assert.Equal("configureAtis", (string?)m["type"]);
        var v = m["value"]!;
        Assert.Equal("a1", (string?)v["id"]);
        Assert.Equal("D-ATIS", (string?)v["preset"]);
        Assert.True((bool)v["syncAtisLetter"]!);
        Assert.Equal("RWY 28", (string?)v["airportConditionsFreeText"]);
        Assert.Equal("TWY B CLSD", (string?)v["notamsFreeText"]);
    }

    [Fact]
    public async Task ConfigureAtis_wraps_connection_failure()
    {
        await Assert.ThrowsAsync<VatisUnavailableException>(() =>
            new VatisClient(new FakeVatis { Refuse = true }).ConfigureAtisAsync("a1", "", "", false));
    }

    [Fact]
    public async Task ConnectAtis_true_when_acknowledged_false_when_silent()
    {
        var acked = new FakeVatis { Reply = _ => ["""{"type":"ok"}"""] };
        Assert.True(await new VatisClient(acked).ConnectAtisAsync("a1"));
        Assert.False(await new VatisClient(new FakeVatis()).ConnectAtisAsync("a1"));
        Assert.Equal("connectAtis", (string?)acked.Sent[0]["type"]);
    }

    [Fact]
    public async Task DisconnectAtis_sends_the_expected_message()
    {
        var fake = new FakeVatis();
        await new VatisClient(fake).DisconnectAtisAsync("a9");
        Assert.Equal("disconnectAtis", (string?)fake.Sent[0]["type"]);
        Assert.Equal("a9", (string?)fake.Sent[0]["value"]!["id"]);
    }

    [Fact]
    public async Task WaitUntilReady_succeeds_after_vatis_comes_up()
    {
        var fake = new FakeVatis { Refuse = true, Reply = _ => [StationsReply] };
        var client = new VatisClient(fake);
        var task = client.WaitUntilReadyAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        fake.Refuse = false;
        Assert.True(await task);
        Assert.True(fake.Connections > 1);
    }

    [Fact]
    public async Task WaitUntilReady_gives_up_at_the_limit()
    {
        var fake = new FakeVatis { Refuse = true };
        Assert.False(await new VatisClient(fake).WaitUntilReadyAsync(TimeSpan.FromMilliseconds(700)));
    }

    [Fact]
    public async Task WaitUntilReady_honors_cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VatisClient(new FakeVatis { Refuse = true }).WaitUntilReadyAsync(TimeSpan.FromSeconds(30), cts.Token));
    }
}
