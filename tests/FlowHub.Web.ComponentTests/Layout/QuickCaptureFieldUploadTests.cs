using FlowHub.Web.Components.Layout;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FlowHub.Web.ComponentTests.Layout;

public class QuickCaptureFieldUploadTests : TestContext
{
    public QuickCaptureFieldUploadTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task StagingValidFile_AndSubmitting_PassesAttachmentToCaptureService()
    {
        var capture = Substitute.For<ICaptureService>();
        capture.SubmitAsync(Arg.Any<string?>(), ChannelKind.Web, Arg.Any<AttachmentInput?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ChannelKind.Web, ci.ArgAt<AttachmentInput>(2)!.FileName,
                DateTimeOffset.UtcNow, LifecycleStage.Raw, null)));
        var policy = Substitute.For<IUploadPolicy>();
        policy.MaxBytes.Returns(2_097_152);
        policy.AllowedContentTypes.Returns(new[] { "application/pdf" });
        policy.AcceptAttribute.Returns("application/pdf");

        Services.AddSingleton(capture);
        Services.AddSingleton(policy);

        var cut = RenderComponent<QuickCaptureField>();
        var file = InputFileContent.CreateFromBinary(new byte[8], "invoice.pdf", null, "application/pdf");
        cut.FindComponent<InputFile>().UploadFiles(file);

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Submit capture']").Click());

        await capture.Received(1).SubmitAsync(
            Arg.Any<string?>(), ChannelKind.Web,
            Arg.Is<AttachmentInput?>(a => a != null && a.FileName == "invoice.pdf" && a.SizeBytes == 8),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void StagingFileExceedingPolicy_DisablesSubmitAndShowsError()
    {
        var capture = Substitute.For<ICaptureService>();
        var policy = Substitute.For<IUploadPolicy>();
        policy.MaxBytes.Returns(4L);
        policy.AllowedContentTypes.Returns(new[] { "application/pdf" });
        policy.AcceptAttribute.Returns("application/pdf");
        Services.AddSingleton(capture);
        Services.AddSingleton(policy);

        var cut = RenderComponent<QuickCaptureField>();
        var file = InputFileContent.CreateFromBinary(new byte[5], "big.pdf", null, "application/pdf");
        cut.FindComponent<InputFile>().UploadFiles(file);

        cut.Markup.Should().Contain("too large");
        capture.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default, default);
    }

    [Fact]
    public void StagingFileWithDisallowedContentType_ShowsTypeNotAllowedError_AndBlocksSubmit()
    {
        // Drives the second arm of ValidateFile — Size is OK but ContentType isn't
        // in AllowedContentTypes — so we hit the "Type X not allowed" return.
        var capture = Substitute.For<ICaptureService>();
        var policy = Substitute.For<IUploadPolicy>();
        policy.MaxBytes.Returns(1_048_576L);
        policy.AllowedContentTypes.Returns(new[] { "application/pdf" });
        policy.AcceptAttribute.Returns("application/pdf");
        Services.AddSingleton(capture);
        Services.AddSingleton(policy);

        var cut = RenderComponent<QuickCaptureField>();
        var file = InputFileContent.CreateFromBinary(new byte[8], "evil.exe", null, "application/octet-stream");
        cut.FindComponent<InputFile>().UploadFiles(file);

        cut.Markup.Should().Contain("Type application/octet-stream not allowed");
        capture.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task StagingFileWithTypedText_SubmitsTheTextAsCaption()
    {
        // The quick-add field is the other Web submit path. Typing a note and then
        // dropping a file must not discard the note — same bug as #31's NewCapture case.
        var capture = Substitute.For<ICaptureService>();
        capture.SubmitAsync(Arg.Any<string?>(), ChannelKind.Web, Arg.Any<AttachmentInput?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ChannelKind.Web, ci.ArgAt<string?>(0) ?? "",
                DateTimeOffset.UtcNow, LifecycleStage.Raw, null)));
        var policy = Substitute.For<IUploadPolicy>();
        policy.MaxBytes.Returns(2_097_152);
        policy.AllowedContentTypes.Returns(new[] { "application/pdf" });
        policy.AcceptAttribute.Returns("application/pdf");

        Services.AddSingleton(capture);
        Services.AddSingleton(policy);

        var cut = RenderComponent<QuickCaptureField>();
        cut.Find("input[type='text']").Input("boiler service invoice");
        var file = InputFileContent.CreateFromBinary(new byte[8], "invoice.pdf", null, "application/pdf");
        cut.FindComponent<InputFile>().UploadFiles(file);

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Submit capture']").Click());

        await capture.Received(1).SubmitAsync(
            "boiler service invoice", ChannelKind.Web, Arg.Any<AttachmentInput?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StagingFileWithNoTypedText_SubmitsNullCaption()
    {
        var capture = Substitute.For<ICaptureService>();
        capture.SubmitAsync(Arg.Any<string?>(), ChannelKind.Web, Arg.Any<AttachmentInput?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ChannelKind.Web, "invoice.pdf",
                DateTimeOffset.UtcNow, LifecycleStage.Raw, null)));
        var policy = Substitute.For<IUploadPolicy>();
        policy.MaxBytes.Returns(2_097_152);
        policy.AllowedContentTypes.Returns(new[] { "application/pdf" });
        policy.AcceptAttribute.Returns("application/pdf");

        Services.AddSingleton(capture);
        Services.AddSingleton(policy);

        var cut = RenderComponent<QuickCaptureField>();
        var file = InputFileContent.CreateFromBinary(new byte[8], "invoice.pdf", null, "application/pdf");
        cut.FindComponent<InputFile>().UploadFiles(file);

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Submit capture']").Click());

        await capture.Received(1).SubmitAsync(
            null, ChannelKind.Web, Arg.Any<AttachmentInput?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void StagingFile_KeepsTheCaptionFieldVisible()
    {
        // The caption must stay on screen while a file is staged — submitting text
        // the user can no longer see is the behaviour #31's D5 rejected.
        var policy = Substitute.For<IUploadPolicy>();
        policy.MaxBytes.Returns(2_097_152);
        policy.AllowedContentTypes.Returns(new[] { "application/pdf" });
        policy.AcceptAttribute.Returns("application/pdf");
        Services.AddSingleton(Substitute.For<ICaptureService>());
        Services.AddSingleton(policy);

        var cut = RenderComponent<QuickCaptureField>();
        var file = InputFileContent.CreateFromBinary(new byte[8], "invoice.pdf", null, "application/pdf");
        cut.FindComponent<InputFile>().UploadFiles(file);

        cut.FindAll("input[type='text']").Should().HaveCount(1);
        cut.Markup.Should().Contain("Caption (optional)");
        cut.Markup.Should().Contain("invoice.pdf");
    }


}
