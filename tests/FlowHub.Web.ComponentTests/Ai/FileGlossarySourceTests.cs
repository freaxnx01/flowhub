using FlowHub.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class FileGlossarySourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("glossary").FullName;

    private static FileGlossarySource Build(string? path, MutableTimeProvider time) =>
        new(Options.Create(new GlossaryOptions { Path = path, RefreshInterval = TimeSpan.FromMinutes(5) }),
            NullLogger<FileGlossarySource>.Instance,
            time);

    private string WriteFile(string json)
    {
        var path = Path.Combine(_dir, "glossary.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task GetAsync_ValidFile_ParsesAllSections()
    {
        var path = WriteFile("""
            {"people":{"aa":"Person A"},"acronyms":{"ZZZ":"Zed Zed Zed"},"prefixes":{"Thing:":"things"}}
            """);

        var snapshot = await Build(path, new MutableTimeProvider(DateTimeOffset.UtcNow)).GetAsync(default);

        snapshot.People["aa"].Should().Be("Person A");
        snapshot.Acronyms["ZZZ"].Should().Be("Zed Zed Zed");
        snapshot.Prefixes["Thing:"].Should().Be("things");
    }

    [Fact]
    public async Task GetAsync_PathNotConfigured_ReturnsEmpty()
    {
        var snapshot = await Build(null, new MutableTimeProvider(DateTimeOffset.UtcNow)).GetAsync(default);
        snapshot.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_FileMissing_ReturnsEmptyAndDoesNotThrow()
    {
        var snapshot = await Build(Path.Combine(_dir, "nope.json"), new MutableTimeProvider(DateTimeOffset.UtcNow)).GetAsync(default);
        snapshot.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_MalformedJson_ReturnsEmptyAndDoesNotThrow()
    {
        var snapshot = await Build(WriteFile("{ not json"), new MutableTimeProvider(DateTimeOffset.UtcNow)).GetAsync(default);
        snapshot.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_WithinRefreshInterval_DoesNotRereadTheFile()
    {
        var path = WriteFile("""{"people":{"aa":"Person A"}}""");
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(path, time);

        await sut.GetAsync(default);
        File.WriteAllText(path, """{"people":{"bb":"Person B"}}""");
        var second = await sut.GetAsync(default);

        second.People.Should().ContainKey("aa").And.NotContainKey("bb");
    }

    [Fact]
    public async Task GetAsync_AfterRefreshInterval_RereadsTheFile()
    {
        var path = WriteFile("""{"people":{"aa":"Person A"}}""");
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(path, time);

        await sut.GetAsync(default);
        File.WriteAllText(path, """{"people":{"bb":"Person B"}}""");
        time.Advance(TimeSpan.FromMinutes(6));
        var second = await sut.GetAsync(default);

        second.People.Should().ContainKey("bb");
    }

    [Fact]
    public async Task GetAsync_RefreshFails_KeepsTheLastGoodSnapshot()
    {
        var path = WriteFile("""{"people":{"aa":"Person A"}}""");
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(path, time);

        await sut.GetAsync(default);
        File.WriteAllText(path, "{ not json");
        time.Advance(TimeSpan.FromMinutes(6));
        var second = await sut.GetAsync(default);

        second.People.Should().ContainKey("aa");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Hand-rolled controllable <see cref="TimeProvider"/>. The repo does not reference
    /// Microsoft.Extensions.TimeProvider.Testing, so we model a mutable "now" here.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
