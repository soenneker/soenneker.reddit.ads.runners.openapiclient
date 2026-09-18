using Soenneker.Tests.HostedUnit;

namespace Soenneker.Reddit.Ads.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class RedditAdsOpenApiClientRunnerTests : HostedUnitTest
{
    public RedditAdsOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
