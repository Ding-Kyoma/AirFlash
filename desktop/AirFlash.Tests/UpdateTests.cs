using System.Net;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class UpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private static HttpResponseMessage Release(string version) => new(HttpStatusCode.OK)
    { Content = new StringContent($$"""{"tag_name":"{{version}}","draft":false,"prerelease":false,"html_url":"https://untrusted.example/"}""") };
    [Theory]
    [InlineData("v0.2.10", true)]
    [InlineData("0.2.9", false)]
    [InlineData("v0.2.8", false)]
    public async Task ComparesNumericallyAndUsesTrustedDestination(string tag, bool newer)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(UpdateService.LatestApi, request.RequestUri!.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(Release(tag));
        }));
        using var state = new UpdateCheckState(new(client), new(0, 2, 9));
        await state.CheckAsync();
        Assert.Equal(newer, state.HasUpdate);
        if (newer) Assert.Equal($"{UpdateService.RepositoryUrl}/releases/tag/{tag}", state.DownloadPage!.AbsoluteUri);
        Assert.False(state.IsChecking);
    }
    [Theory]
    [InlineData(404, "No stable release is available yet.")]
    [InlineData(403, "Could not check for updates. Please try again later.")]
    [InlineData(429, "Could not check for updates. Please try again later.")]
    [InlineData(500, "Could not check for updates. Please try again later.")]
    public async Task ReportsMissingReleaseAndHttpFailures(int status, string message)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        using var state = new UpdateCheckState(new(client), new(0, 2, 9));
        await state.CheckAsync();
        Assert.Equal(L.Get(message), state.Status); Assert.False(state.HasUpdate);
    }
    [Theory]
    [InlineData("bad")]
    [InlineData("{\"tag_name\":\"v0.2.10-beta\",\"draft\":false,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"v0.2.10.1\",\"draft\":false,\"prerelease\":false}")]
    [InlineData("{}")]
    public async Task RejectsInvalidResponses(string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        using var state = new UpdateCheckState(new(client), new(0, 2, 9));
        await state.CheckAsync(); Assert.False(state.HasUpdate);
        Assert.Equal(L.Get("Could not check for updates. Please try again later."), state.Status);
    }
    [Fact]
    public async Task ReportsTimeout()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new TaskCanceledException()));
        using var state = new UpdateCheckState(new(client), new(0, 2, 9));
        await state.CheckAsync(); Assert.Equal(L.Get("Update check timed out. Please try again."), state.Status);
    }
    [Fact]
    public async Task PreventsDuplicateRequestsAndCancelsOnDispose()
    {
        var count = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            count++; entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Release("v1.0.0");
        }));
        var state = new UpdateCheckState(new(client), new(0, 2, 9));
        var first = state.CheckAsync(); await entered.Task;
        await state.CheckAsync(); Assert.Equal(1, count);
        state.Dispose(); await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsChecking); Assert.False(state.HasUpdate);
        await state.CheckAsync(); Assert.Equal(1, count);
    }
}
