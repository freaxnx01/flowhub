using FluentAssertions;
using FlowHub.Core.Captures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.AI.IntegrationTests;

public class AiSpeechRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowHubSpeech(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddFlowHubSpeech_WithoutApiKey_RegistersNothing()
    {
        var sp = Build(("Speech:Model", "whisper-1"));

        sp.GetService<ISpeechToText>().Should().BeNull();
    }

    [Fact]
    public void AddFlowHubSpeech_WithApiKey_RegistersTheService()
    {
        var sp = Build(("Speech:ApiKey", "sk-test"), ("Speech:BaseUrl", "http://localhost:8000/v1"));

        sp.GetService<ISpeechToText>().Should().NotBeNull();
    }

    [Fact]
    public void AddFlowHubSpeech_MaxSecondsDefaultsTo300()
    {
        var sp = Build(("Speech:ApiKey", "sk-test"));

        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SpeechOptions>>()
          .Value.MaxSeconds.Should().Be(300);
    }
}
