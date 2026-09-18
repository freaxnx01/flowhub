using System.Net;
using FlowHub.Core.Captures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Api.IntegrationTests.Captures;

public sealed class CaptureRetryWithheldTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public CaptureRetryWithheldTests(IntegrationTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Retry_WithheldCapture_IsRejectedAsNotRetryable()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Create a capture and mark it as withheld using the scoped service
        Guid captureId;
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICaptureService>();
            var capture = await service.SubmitAsync("anything", ChannelKind.Web, default);
            await service.MarkWithheldAsync(capture.Id, "sensitive — test fixture", default);
            captureId = capture.Id;
        }

        // Act
        var response = await client.PostAsync($"/api/v1/captures/{captureId}/retry", content: null);

        // Assert — this is the assertion that keeps Withheld out of RetryableStages
        // if someone extends that array later.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
