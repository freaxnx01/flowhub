using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using FlowHub.Web.Components.Pages;

namespace FlowHub.Web.ComponentTests.Pages;

public class NewCaptureUploadTests : TestContext
{
    private readonly ICaptureService _captureService = Substitute.For<ICaptureService>();

    public NewCaptureUploadTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_captureService);
        var skills = Substitute.For<ISkillRegistry>();
        skills.GetHealthAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<SkillHealth>>([]));
        Services.AddSingleton(skills);
        var policy = Substitute.For<IUploadPolicy>();
        policy.MaxBytes.Returns(2_097_152);
        policy.AllowedContentTypes.Returns(new[] { "application/pdf" });
        policy.AcceptAttribute.Returns("application/pdf");
        Services.AddSingleton(policy);
        RenderComponent<MudPopoverProvider>();
    }

    [Fact]
    public void StagingFile_KeepsTextAreaEnabledAndOffersACaption()
    {
        var cut = RenderComponent<NewCapture>();
        var file = InputFileContent.CreateFromBinary(new byte[2], "doc.pdf", null, "application/pdf");
        cut.FindComponent<InputFile>().UploadFiles(file);

        cut.Markup.Should().Contain("Caption (optional)");
        cut.Markup.Should().NotContain("File overrides text");
        cut.Find("textarea").GetAttribute("disabled").Should().BeNull();
    }

    [Fact]
    public void StagingFileAndTypingACaption_SubmitsTheCaption()
    {
        _captureService.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Capture(
                Guid.NewGuid(), ChannelKind.Web, "boiler service invoice", DateTimeOffset.UtcNow,
                LifecycleStage.Raw, null)));

        var cut = RenderComponent<NewCapture>();
        var file = InputFileContent.CreateFromBinary(new byte[2], "doc.pdf", null, "application/pdf");
        cut.FindComponent<InputFile>().UploadFiles(file);
        cut.Find("textarea").Change("boiler service invoice");
        cut.FindAll("button").First(b => b.TextContent.Contains("Submit", StringComparison.Ordinal)).Click();

        _captureService.Received(1).SubmitAsync(
            "boiler service invoice", ChannelKind.Web, Arg.Any<AttachmentInput>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
