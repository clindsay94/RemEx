using Remex.Agent.Services.Routines;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>RoutineStepRequestIdempotencyTests</c> (§7.3.3): a duplicate
/// (clientId, runId, stepIndex) returns the cached result, or <c>in_progress</c> while the first is
/// still running, and never executes twice; 60 new requests per minute per client.
/// </summary>
public sealed class RoutineStepRequestIdempotencyTests
{
    private static RoutineStepRequestPayload Request(string runId, RoutineStep step, int index = 0) => new()
    {
        RunId = runId, RoutineId = "routine-1", RoutineName = "Evening", StepIndex = index, Step = step,
        Source = RoutineRunSources.ManualApp,
    };

    private static (List<RoutineStepResultPayload> Sent, Func<RemexMessage, Task> Send) Sink()
    {
        var sent = new List<RoutineStepResultPayload>();
        return (sent, message =>
        {
            Assert.Equal(MessageTypes.RoutineStepResult, message.Type);
            lock (sent)
            {
                sent.Add(message.RoutineStepResult!);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ADuplicateGetsTheCachedResultAndDoesNotRunAgain()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (sent, send) = Sink();
        var request = Request("run-1", RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock));

        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, request, send);
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, request, send);

        Assert.Single(bench.Power.Issued);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, r => Assert.Equal(RoutineStepOutcomes.Succeeded, r.Outcome));
    }

    [Fact]
    public async Task ADuplicateWhileTheFirstIsCountingDownIsInProgress()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (sent, send) = Sink();
        var request = Request("run-1", RoutineTestBench.PowerStep(RoutinePowerVerbs.Shutdown));

        var first = handler.HandleStepRequestAsync(RoutineTestBench.Owner, request, send);
        await bench.WaitForCountdownAsync();
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, request, send);

        var inProgress = Assert.Single(sent);
        Assert.Equal(RoutineStepOutcomes.InProgress, inProgress.Outcome);

        bench.ElapseCountdown();
        await first;

        Assert.Single(bench.Power.Issued);
        Assert.Single(bench.Ui.Shown);
        Assert.Equal(RoutineStepOutcomes.Succeeded, sent[^1].Outcome);
    }

    [Fact]
    public async Task TheKeyIncludesTheClientAndTheStepIndex()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (_, send) = Sink();
        var step = RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock);

        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, Request("run-1", step, 0), send);
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, Request("run-1", step, 1), send);
        await handler.HandleStepRequestAsync(RoutineTestBench.OtherOwner, Request("run-1", step, 0), send);

        Assert.Equal(3, bench.Power.Issued.Count);
    }

    [Fact]
    public async Task TheCacheExpiresAfterTenMinutes()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (_, send) = Sink();
        var request = Request("run-1", RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock));

        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, request, send);
        bench.Time.Advance(RoutineStepRequestHandler.CacheLifetime - TimeSpan.FromSeconds(1));
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, request, send);
        Assert.Single(bench.Power.Issued);

        bench.Time.Advance(TimeSpan.FromSeconds(2));
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, request, send);
        Assert.Equal(2, bench.Power.Issued.Count);
    }

    [Fact]
    public async Task SixtyNewRequestsPerMinuteThenRateLimited()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (sent, send) = Sink();
        var step = RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock);

        for (var i = 0; i < RoutineStepRequestHandler.MaxRequestsPerMinute; i++)
        {
            await handler.HandleStepRequestAsync(RoutineTestBench.Owner, Request("run-" + i, step), send);
        }

        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, Request("run-over", step), send);
        Assert.Equal(RoutineReasonCodes.RateLimited, sent[^1].ReasonCode);
        Assert.Equal(RoutineStepRequestHandler.MaxRequestsPerMinute, bench.Power.Issued.Count);

        // A duplicate of an already-answered request is not a new request and is still answered.
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, Request("run-0", step), send);
        Assert.Equal(RoutineStepOutcomes.Succeeded, sent[^1].Outcome);

        // Another phone has its own budget, and the window slides.
        await handler.HandleStepRequestAsync(RoutineTestBench.OtherOwner, Request("run-x", step), send);
        Assert.Equal(RoutineStepOutcomes.Succeeded, sent[^1].Outcome);

        bench.Time.Advance(TimeSpan.FromMinutes(1));
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, Request("run-later", step), send);
        Assert.Equal(RoutineStepOutcomes.Succeeded, sent[^1].Outcome);
    }

    [Fact]
    public async Task AMalformedRequestIsAnsweredNotDropped()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (sent, send) = Sink();

        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, null, send);
        await handler.HandleStepRequestAsync(RoutineTestBench.Owner, new RoutineStepRequestPayload { StepIndex = 2 }, send);

        Assert.Equal(2, sent.Count);
        Assert.All(sent, r => Assert.Equal(RoutineReasonCodes.InvalidField, r.ReasonCode));
    }

    [Fact]
    public async Task ASendThatThrowsDoesNotEscape()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();

        await handler.HandleStepRequestAsync(
            RoutineTestBench.Owner,
            Request("run-1", RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock)),
            _ => throw new InvalidOperationException("socket gone"));

        Assert.Single(bench.Power.Issued);
    }

    [Fact]
    public async Task ADestructiveStepIsAnsweredOnceBeforeTheVerb()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (sent, send) = Sink();

        var run = handler.HandleStepRequestAsync(
            RoutineTestBench.Owner, Request("run-1", RoutineTestBench.PowerStep(RoutinePowerVerbs.Restart)), send);
        await bench.WaitForCountdownAsync();
        bench.ElapseCountdown();
        await run;

        var only = Assert.Single(sent);
        Assert.Equal(RoutineStepOutcomes.Succeeded, only.Outcome);
        Assert.True(only.CountdownShown);
    }

    [Fact]
    public async Task RoutineCancelIsScopedToTheOwner()
    {
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();
        var (sent, send) = Sink();

        var run = handler.HandleStepRequestAsync(
            RoutineTestBench.Owner, Request("run-1", RoutineTestBench.PowerStep(RoutinePowerVerbs.Shutdown)), send);
        await bench.WaitForCountdownAsync();

        Assert.False(handler.HandleCancel(RoutineTestBench.OtherOwner, new RoutineCancelPayload { RunId = "run-1", Reason = "user" }));
        Assert.False(handler.HandleCancel(RoutineTestBench.Owner, null));
        Assert.True(handler.HandleCancel(RoutineTestBench.Owner, new RoutineCancelPayload { RunId = "run-1", Reason = "user" }));
        await run;

        var result = Assert.Single(sent);
        Assert.Equal(RoutineStepOutcomes.Cancelled, result.Outcome);
        Assert.Equal(RoutineReasonCodes.CancelledOnPhone, result.ReasonCode);
        Assert.Equal(RoutineCancelledBy.Phone, result.CancelledBy);
        Assert.Empty(bench.Power.Issued);
    }
}
