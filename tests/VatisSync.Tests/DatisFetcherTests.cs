using System.Net;
using VatisSync.Core;

namespace VatisSync.Tests;

public class DatisFetcherTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    private const string Good =
        """[{"airport":"KSFO","type":"combined","code":"A","datis":"X. Y. RWY 28.","updatedAt":"2026-11-07T17:50:41.2437236Z"}]""";

    [Fact]
    public async Task Parses_entries()
    {
        var f = new DatisFetcher(new HttpClient(new FakeHandler(_ => Task.FromResult(Json(Good)))));
        var r = await f.FetchAsync();
        Assert.True(r.Succeeded);
        Assert.False(r.FromCache);
        var e = Assert.Single(r.Entries);
        Assert.Equal(("KSFO", "combined", "2026-11-07T17:50:41.2437236Z"), (e.Airport, e.Type, e.UpdatedAt));
    }

    [Fact]
    public async Task Http_failure_without_cache_returns_empty_and_error()
    {
        var f = new DatisFetcher(new HttpClient(new FakeHandler(_ => Task.FromResult(Json("", HttpStatusCode.BadGateway)))));
        var r = await f.FetchAsync();
        Assert.False(r.Succeeded);
        Assert.Empty(r.Entries);
        Assert.False(r.FromCache);
        Assert.Contains("502", r.Error);
    }

    [Fact]
    public async Task Failure_after_success_returns_last_good_data_marked_cached()
    {
        var fail = false;
        var f = new DatisFetcher(new HttpClient(new FakeHandler(_ =>
            fail ? throw new HttpRequestException("offline") : Task.FromResult(Json(Good)))));
        await f.FetchAsync();
        fail = true;
        var r = await f.FetchAsync();
        Assert.False(r.Succeeded);
        Assert.True(r.FromCache);
        Assert.Single(r.Entries);
    }

    [Fact]
    public async Task Error_object_is_a_failure_not_a_crash()
    {
        var f = new DatisFetcher(new HttpClient(new FakeHandler(_ =>
            Task.FromResult(Json("""{"error":"rate limited"}""")))));
        var r = await f.FetchAsync();
        Assert.False(r.Succeeded);
        Assert.Contains("rate limited", r.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("42")]
    public async Task Malformed_body_is_a_failure(string body)
    {
        var f = new DatisFetcher(new HttpClient(new FakeHandler(_ => Task.FromResult(Json(body)))));
        var r = await f.FetchAsync();
        Assert.False(r.Succeeded);
        Assert.Empty(r.Entries);
    }

    [Fact]
    public async Task Timeout_is_reported_as_an_error()
    {
        var f = new DatisFetcher(new HttpClient(new FakeHandler(_ => throw new TaskCanceledException())));
        var r = await f.FetchAsync();
        Assert.Contains("Timed out", r.Error);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var f = new DatisFetcher(new HttpClient(new FakeHandler(_ => Task.FromResult(Json(Good)))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.FetchAsync(cts.Token));
    }

    [Fact]
    public void Entries_missing_fields_are_skipped_or_defaulted()
    {
        var list = DatisFetcher.Parse(
            """[{"airport":"KSFO"},{"airport":"KOAK","type":"combined"},{"airport":"KSJC","type":"arr","datis":null}, 7]""",
            out var error)!;
        Assert.Null(error);
        Assert.Equal(["KOAK", "KSJC"], list.Select(e => e.Airport));
        Assert.All(list, e => Assert.Equal("", e.Datis));
    }

    [Fact]
    public void Full_real_capture_parses_all_entries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "atis-info-all-2026-11-07.json");
        var list = DatisFetcher.Parse(File.ReadAllText(path), out var error)!;
        Assert.Null(error);
        Assert.Equal(88, list.Count);
    }
}
