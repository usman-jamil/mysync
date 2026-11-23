using MySync.Cli.Hosting;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;
using Xunit;

namespace MySync.EndToEndTests.Cli;

/// <summary>While <c>mysync tui</c> has the screen, the log writes nothing to stdout or stderr; its warnings go to the activity list (CLAUDE.md §10d).</summary>
public class SwitchableLogSinkTests
{
    private sealed class Recording : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static LogEvent Event(LogEventLevel level, string text) =>
        new(DateTimeOffset.UnixEpoch, level, null, new MessageTemplateParser().Parse(text), []);

    [Fact]
    public void WhileHeld_Should_WriteNothing_AndHandOverWarningsAndErrors()
    {
        Recording console = new();
        SwitchableLogSink sink = new(console);

        sink.Emit(Event(LogEventLevel.Warning, "before"));
        using (SwitchableLogSink.Capture held = sink.Hold())
        {
            sink.Emit(Event(LogEventLevel.Information, "chatter"));
            sink.Emit(Event(LogEventLevel.Warning, "the share is slow"));
            sink.Emit(Event(LogEventLevel.Error, "a failure"));

            console.Events.Count.ShouldBe(1);
            held.Drain().ShouldBe(["the share is slow", "a failure"]);
            held.Drain().ShouldBeEmpty();
        }

        sink.Emit(Event(LogEventLevel.Warning, "after"));
        console.Events.Select(e => e.MessageTemplate.Text).ShouldBe(["before", "after"]);
    }
}
