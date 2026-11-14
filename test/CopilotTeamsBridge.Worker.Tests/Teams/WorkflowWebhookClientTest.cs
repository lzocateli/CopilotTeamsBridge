using System.Net;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Teams;
using Moq;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Teams;

[Trait("Category", "Unit")]
public sealed class WorkflowWebhookClientTest : IDisposable
{
    private readonly StubHandler _handler = new();
    private readonly Mock<ISecretFileReader> _secrets = new();

    public WorkflowWebhookClientTest()
    {
        _ = _secrets.Setup(reader => reader.ReadTextAsync("/secrets/workflow.bearer", It.IsAny<CancellationToken>())).ReturnsAsync("bearer-value");
    }

    [Fact]
    public async Task PostSendsBearerAndReturnsApprovedStatusLocation()
    {
        _handler.Respond = _ => Accepted("https://poll.example.test/runs/1");

        var delivery = await Client().PostAsync(new JsonObject { ["type"] = "message" }, CancellationToken.None);

        delivery.Status.ShouldBe(WebhookDeliveryStatus.AwaitingResponse);
        delivery.StatusLocation!.Host.ShouldBe("poll.example.test");
        _handler.Requests.Single().Headers.Authorization!.ToString().ShouldBe("Bearer bearer-value");
        _handler.Requests.Single().Method.ShouldBe(HttpMethod.Post);
        _handler.Requests.Single().RequestUri.ShouldBe(new Uri(TestData.WebhookUrl));
    }

    [Theory]
    [InlineData("https://evil.example.test/runs/1")]
    [InlineData("http://poll.example.test/runs/1")]
    [InlineData("https://user:pw@poll.example.test/runs/1")]
    public async Task UnapprovedStatusLocationIsNotFollowed(string location)
    {
        _handler.Respond = _ => Accepted(location);

        var delivery = await Client().PostAsync([], CancellationToken.None);

        delivery.Status.ShouldBe(WebhookDeliveryStatus.AcceptedWithoutReturn);
        delivery.StatusLocation.ShouldBeNull();
    }

    [Theory]
    [InlineData(429, "Unavailable")]
    [InlineData(502, "Unavailable")]
    [InlineData(401, "Rejected")]
    [InlineData(302, "Rejected")]
    [InlineData(200, "AcceptedWithoutReturn")]
    public async Task MapsHttpStatusToDeliveryStatus(int status, string expected)
    {
        _handler.Respond = _ => new HttpResponseMessage((HttpStatusCode)status);

        (await Client().PostAsync([], CancellationToken.None)).Status.ShouldBe(Enum.Parse<WebhookDeliveryStatus>(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://workflow.example.test/trigger")]
    [InlineData("https://user:pw@workflow.example.test/trigger")]
    public async Task InvalidWebhookUrlIsNeverCalled(string webhookUrl)
    {
        _ = await Should.ThrowAsync<InvalidOperationException>(
            () => Client(options => options.Workflow.WebhookUrl = webhookUrl).PostAsync([], CancellationToken.None));
        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyPollingAllowlistAcceptsOnlyWebhookHost()
    {
        _handler.Respond = _ => Accepted("https://workflow.example.test/runs/1");
        var sameHost = await Client(options => options.Workflow.AllowedPollingHosts = []).PostAsync([], CancellationToken.None);

        _handler.Respond = _ => Accepted("https://poll.example.test/runs/1");
        var otherHost = await Client(options => options.Workflow.AllowedPollingHosts = []).PostAsync([], CancellationToken.None);

        sameHost.Status.ShouldBe(WebhookDeliveryStatus.AwaitingResponse);
        otherHost.Status.ShouldBe(WebhookDeliveryStatus.AcceptedWithoutReturn);
    }

    [Fact]
    public async Task UrlSecretModeDoesNotSendAuthorization()
    {
        _handler.Respond = _ => Accepted("https://poll.example.test/runs/1");

        _ = await Client(options => options.Workflow.AuthMode = WorkflowAuthModes.UrlSecret).PostAsync([], CancellationToken.None);

        _handler.Requests.Single().Headers.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task PollReportsRunningWithRetryAfterThenCompletedBody()
    {
        var location = new Uri("https://poll.example.test/runs/1");
        _handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return response;
        };

        var running = await Client().PollAsync(location, CancellationToken.None);
        running.Status.ShouldBe(WorkflowPollStatus.Running);
        running.RetryAfter.ShouldBe(TimeSpan.FromSeconds(20));
        _handler.Requests.Single().Headers.Authorization.ShouldBeNull();

        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":{}}""") };
        var completed = await Client().PollAsync(location, CancellationToken.None);
        completed.Status.ShouldBe(WorkflowPollStatus.Completed);
        completed.Body.ShouldBe("""{"data":{}}""");
    }

    [Theory]
    [InlineData("nao-json")]
    [InlineData("[1,2]")]
    public async Task PollRejectsNonObjectBodies(string body)
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

        (await Client().PollAsync(new Uri("https://poll.example.test/r"), CancellationToken.None)).Status.ShouldBe(WorkflowPollStatus.Failed);
    }

    [Fact]
    public async Task PollRejectsOversizedBodies()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"data":"{{new string('x', 70 * 1024)}}"}"""),
        };

        (await Client().PollAsync(new Uri("https://poll.example.test/r"), CancellationToken.None)).Status.ShouldBe(WorkflowPollStatus.Failed);
    }

    public void Dispose() => _handler.Dispose();

    private WorkflowWebhookClient Client(Action<TeamsBridgeOptions>? configure = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        _ = factory.Setup(item => item.CreateClient(WorkflowWebhookClient.HttpClientName)).Returns(() => new HttpClient(_handler, disposeHandler: false));
        return new WorkflowWebhookClient(factory.Object, _secrets.Object, TestData.Wrap(TestData.Options(configure)));
    }

    private static HttpResponseMessage Accepted(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }
}
