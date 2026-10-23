using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using RaidActions.Protocol;
using PRunMode = RaidActions.Protocol.RunMode;

namespace RaidActions.Tests;

public class ProtocolTests
{
    public static TheoryData<string, Message> AllMessages() => new()
    {
        { "hello", new Hello("raid-extractor", "1.2.3") { Id = "1" } },
        { "loadPlan", new LoadPlan(@"F:\plans\p.json") { Id = "2" } },
        { "validate", new Validate { Id = "3" } },
        { "start", new Start(PRunMode.Dry, "abc", "00ff") { Id = "4" } },
        { "pause", new Pause() },
        { "resume", new Resume() },
        { "abort", new Abort("user pressed Abort") },
        { "welcome", new Welcome("RaidActions", "0.1.0", "fake", false) { ReplyTo = "1" } },
        { "planLoaded", new PlanLoaded("abc", "Plan", "00ff", "11.85.5",
            [new StepInfo(1, 100, "Helmet", "Helmet: x", null, "the inventory", 1, "Aria")], [new IssueInfo("warning", "TicksIgnored", "ticks", null)]) { ReplyTo = "2" } },
        { "validated", new Validated(true, [new IssueInfo("error", "ArtifactUpgraded", "changed", 2)], [new StepStatus(1, "pending", 201)]) { ReplyTo = "3" } },
        { "started", new Started(PRunMode.Real, "run-1", @"C:\log.jsonl") { ReplyTo = "4" } },
        { "progress", new Progress(2, "Equip", "Equip #200", "ok", "fake: no input sent", new DateTimeOffset(2026, 10, 23, 14, 0, 0, TimeSpan.FromHours(11))) },
        { "paused", new Paused() },
        { "resumed", new Resumed() },
        { "result", new Result("failed", [1], 2, "Verification failed", "log", "snap", null) },
        { "error", new Error("planMismatch", "not the loaded plan") { ReplyTo = "4" } },
    };

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void Every_message_round_trips(string type, Message m)
    {
        var json = MessageCodec.Serialize(m);
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(type, (string)node["type"]!);
        Assert.Equal(1, (int)node["v"]!);
        var back = MessageCodec.Deserialize(json);
        Assert.IsType(m.GetType(), back);
        Assert.Equal(json, MessageCodec.Serialize(back));
    }

    [Fact]
    public void Enums_are_camelCase_strings_and_nulls_are_left_out()
    {
        var json = MessageCodec.Serialize(new Start(PRunMode.Dry, "abc", "00ff"));
        Assert.Contains("\"mode\":\"dry\"", json);
        Assert.DoesNotContain("replyTo", json);
    }

    [Fact]
    public void Type_can_come_anywhere_in_the_object()
    {
        var m = Assert.IsType<Abort>(MessageCodec.Deserialize("""{"reason":"x","v":1,"type":"abort"}"""));
        Assert.Equal("x", m.Reason);
    }

    [Theory]
    [InlineData("""{"type":"hello","client":"a","clientVersion":"1"}""", "badMessage")]           // no version
    [InlineData("""{"type":"hello","v":2,"client":"a","clientVersion":"1"}""", "versionMismatch")]
    [InlineData("""{"type":"format-disk","v":1}""", "badMessage")]
    [InlineData("""{"v":1}""", "badMessage")]
    [InlineData("""{"type":"start","v":1,"mode":"turbo","planId":"a","sha256":"b"}""", "badMessage")]
    [InlineData("""{"type":"start","v":1,"mode":0,"planId":"a","sha256":"b"}""", "badMessage")]
    [InlineData("not json", "badMessage")]
    public void Bad_messages_are_refused_with_a_code(string line, string code) =>
        Assert.Equal(code, Assert.Throws<ProtocolException>(() => MessageCodec.Deserialize(line)).Code);

    /// <summary>A stream that hands out one byte per read, to exercise line framing across reads.</summary>
    private sealed class Trickle(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }

    [Fact]
    public async Task Lines_are_framed_across_reads_with_crlf_and_blank_lines()
    {
        var text = MessageCodec.Serialize(new Pause()) + "\r\n\n" + MessageCodec.Serialize(new Abort("now")) + "\n" + MessageCodec.Serialize(new Resume());
        using var ch = new MessageChannel(new Trickle(Encoding.UTF8.GetBytes(text)));
        Assert.IsType<Pause>(await ch.ReadAsync());
        Assert.Equal("now", Assert.IsType<Abort>(await ch.ReadAsync()).Reason);
        Assert.IsType<Resume>(await ch.ReadAsync()); // last line without a newline
        Assert.Null(await ch.ReadAsync());
    }

    [Fact]
    public async Task An_oversized_line_is_refused_and_reading_continues()
    {
        var huge = new string('x', ProtocolInfo.MaxLineBytes + 10);
        var text = $$"""{"type":"abort","v":1,"reason":"{{huge}}"}""" + "\n" + MessageCodec.Serialize(new Pause()) + "\n";
        using var ch = new MessageChannel(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("badMessage", (await Assert.ThrowsAsync<ProtocolException>(() => ch.ReadAsync())).Code);
        Assert.IsType<Pause>(await ch.ReadAsync());
    }

    [Fact]
    public async Task Round_trip_over_a_named_pipe()
    {
        var name = "raid-actions-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
        using var s = new MessageChannel(server);
        using var c = new MessageChannel(client);

        await c.WriteAsync(new Hello("planner", "1.0") { Id = "h1" });
        var hello = Assert.IsType<Hello>(await s.ReadAsync());
        await s.WriteAsync(new Welcome("RaidActions", "0.1.0", "fake", false) { ReplyTo = hello.Id });
        Assert.Equal("h1", Assert.IsType<Welcome>(await c.ReadAsync()).ReplyTo);

        await c.WriteAsync(new Start(PRunMode.Real, "abc", "00ff") { Id = "s1" });
        Assert.Equal(PRunMode.Real, Assert.IsType<Start>(await s.ReadAsync()).Mode);
    }
}
