using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Core.Tests.Routines;

/// <summary>
/// A malformed routine message never becomes a null envelope (routines spec T18, §13.1).
/// </summary>
/// <remarks>
/// <c>MessageSerializer.Deserialize</c> returning null is what makes <c>PingPongHandler</c> drop the
/// whole session, so every case here asserts the ENVELOPE survives: missing fields default, wrong JSON
/// types become a malformed routine (rejected alone) or a null slot (answerable), and unknown
/// discriminators deserialize for the validator to refuse.
/// </remarks>
public class RoutinesSyncMalformedPayloadTests
{
    private static RemexMessage Parse(string json)
    {
        var message = MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(message);
        return message;
    }

    private const string GoodRoutine =
        """{"id":"3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10","name":"Bedtime","hostIdentity":"9f2c4be07a1d33e5","enabled":true,"revision":1,"trigger":{"type":"pc.idle","idleMinutes":5},"steps":[{"type":"power","verb":"SLEEP"}],"createdAtUnixMs":0,"updatedAtUnixMs":0}""";

    private static string SyncOf(string routinesJson) =>
        """{"type":"routines_sync","routinesSync":{"routines":[""" + routinesJson + "]}}";

    [Fact]
    public void AnEmptyPayloadDeserializesWithDefaults()
    {
        var message = Parse("""{"type":"routines_sync","routinesSync":{}}""");

        Assert.NotNull(message.RoutinesSync);
        Assert.Equal(0, message.RoutinesSync.Revision);
        Assert.Null(message.RoutinesSync.Routines);
    }

    [Fact]
    public void AMissingSlotIsNullNotAThrow()
    {
        var message = Parse("""{"type":"routines_sync"}""");
        Assert.Null(message.RoutinesSync);
    }

    [Fact]
    public void AWrongTypedTopLevelFieldNullsTheSlotButKeepsTheEnvelope()
    {
        var message = Parse("""{"type":"routines_sync","routinesSync":{"revision":"seven","routines":[]}}""");

        Assert.Equal(MessageTypes.RoutinesSync, message.Type);
        Assert.Null(message.RoutinesSync);
    }

    [Fact]
    public void ASlotThatIsNotAnObjectIsNull()
    {
        Assert.Null(Parse("""{"type":"routines_sync","routinesSync":[1,2]}""").RoutinesSync);
        Assert.Null(Parse("""{"type":"routine_cancel","routineCancel":"now"}""").RoutineCancel);
        Assert.Null(Parse("""{"type":"routine_run_report","routineRunReport":5}""").RoutineRunReport);
    }

    [Fact]
    public void OneMalformedRoutineIsRejectedAloneAndTheRestSurvive()
    {
        var json = """{"type":"routines_sync","routinesSync":{"revision":3,"routines":["""
            + GoodRoutine
            + ""","""
            + """{"id":"7c2e1b90-4d5f-4a3b-b6c7-8d9e0f1a2b3c","name":5,"enabled":"yes"},17,null]}}""";

        var sync = Parse(json).RoutinesSync!;

        Assert.Equal(3, sync.Revision);
        Assert.Equal(4, sync.Routines!.Count);
        Assert.False(sync.Routines[0].IsMalformed);
        Assert.True(sync.Routines[1].IsMalformed);
        Assert.Equal("7c2e1b90-4d5f-4a3b-b6c7-8d9e0f1a2b3c", sync.Routines[1].Id);
        Assert.True(sync.Routines[2].IsMalformed);
        Assert.True(sync.Routines[3].IsMalformed);

        var verdict = RoutineValidator.ValidateRoutines(sync.Routines);
        Assert.True(verdict.Routines[0].IsValid);
        Assert.Equal(RoutineReasonCodes.InvalidField, verdict.Routines[1].ReasonCode);
        Assert.Equal(RoutineReasonCodes.InvalidField, verdict.Routines[2].ReasonCode);
    }

    [Fact]
    public void AMalformedRoutineIsWrittenBackExactlyAsItArrived()
    {
        // A store round trip must never replace the user's routine with the lossy placeholder.
        const string original =
            """{"schemaVersion":1,"routines":[{"id":"7c2e1b90-4d5f-4a3b-b6c7-8d9e0f1a2b3c","name":5,"futureField":{"a":[1,2]}},17,null]}""";

        var set = Remex.Core.Serialization.RemexJson.Deserialize(original, Remex.Core.Serialization.RemexJsonSerializerContext.Default.RoutineSet)!;
        Assert.All(set.Routines!, r => Assert.True(r.IsMalformed));

        var written = Remex.Core.Serialization.RemexJson.Serialize(set, Remex.Core.Serialization.RemexJsonSerializerContext.Default.RoutineSet);
        Assert.True(
            System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(original),
                System.Text.Json.Nodes.JsonNode.Parse(written)),
            written);
    }

    [Fact]
    public void AMalformedRoutineWithNoOriginalJsonCannotBeWrittenSilently()
    {
        var set = new RoutineSet { SchemaVersion = 1, Routines = [new Routine { Id = "x", IsMalformed = true }] };

        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            Remex.Core.Serialization.RemexJson.Serialize(set, Remex.Core.Serialization.RemexJsonSerializerContext.Default.RoutineSet));
    }

    [Fact]
    public void AMalformedStepInsideARoutineMarksTheRoutine()
    {
        var bad = GoodRoutine.Replace("\"verb\":\"SLEEP\"", "\"verb\":7", StringComparison.Ordinal);
        var sync = Parse(SyncOf(bad)).RoutinesSync!;

        Assert.True(sync.Routines![0].IsMalformed);
        Assert.Equal("3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10", sync.Routines[0].Id);
    }

    [Fact]
    public void UnknownDiscriminatorsDeserializeAndAreRefusedByTheValidator()
    {
        var unknownTrigger = GoodRoutine.Replace("\"type\":\"pc.idle\",\"idleMinutes\":5", "\"type\":\"pc.schedule\",\"cron\":\"* * *\"", StringComparison.Ordinal);
        var unknownStep = GoodRoutine.Replace("{\"type\":\"power\",\"verb\":\"SLEEP\"}", "{\"type\":\"script\",\"body\":\"rm\"}", StringComparison.Ordinal);
        var sync = Parse(SyncOf(unknownTrigger + "," + unknownStep)).RoutinesSync!;

        Assert.False(sync.Routines![0].IsMalformed);
        Assert.Equal("pc.schedule", sync.Routines[0].Trigger!.Type);
        Assert.Equal(RoutineReasonCodes.UnsupportedTrigger, RoutineValidator.ValidateRoutine(sync.Routines[0]).ReasonCode);
        Assert.Equal(RoutineReasonCodes.UnsupportedStep, RoutineValidator.ValidateRoutine(sync.Routines[1]).ReasonCode);
    }

    [Fact]
    public void AMalformedStepRequestStepKeepsTheCorrelationFields()
    {
        var message = Parse("""
            {"type":"routine_step_request","routineStepRequest":{"runId":"run-1","stepIndex":3,
              "step":{"type":"power","verb":["SHUTDOWN"]}}}
            """);

        var request = message.RoutineStepRequest!;
        Assert.Equal("run-1", request.RunId);
        Assert.Equal(3, request.StepIndex);
        Assert.True(request.Step!.IsMalformed);
        Assert.Equal("power", request.Step.Type);
        Assert.Equal(RoutineReasonCodes.InvalidField, RoutineValidator.ValidateStep(request.Step, hostRun: false).ReasonCode);
    }

    [Fact]
    public void EveryRoutineSlotIsLenient()
    {
        foreach (var (type, slot) in new[]
                 {
                     ("routines_sync", "routinesSync"), ("routine_sync_result", "routineSyncResult"),
                     ("routine_step_request", "routineStepRequest"), ("routine_step_result", "routineStepResult"),
                     ("routine_notify", "routineNotify"), ("routine_notify_ack", "routineNotifyAck"),
                     ("routine_run_report", "routineRunReport"), ("routine_cancel", "routineCancel"),
                     ("routine_run_request", "routineRunRequest"),
                 })
        {
            // Every payload has at least one member that cannot hold an object.
            var json = "{\"type\":\"" + type + "\",\"" + slot + "\":"
                + """{"runId":{},"revision":{},"notifyIds":{},"runs":{},"stepIndex":{}}}""";
            var message = MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));
            Assert.True(message is not null, $"{type}: a malformed payload produced a null envelope");
        }
    }
}

/// <summary>No routine wire or model type has a <c>required</c> member (spec T18, §14 S1 guard).</summary>
public class RoutineNoRequiredMembersTests
{
    private static IEnumerable<Type> RoutineTypes() =>
        typeof(RoutineValidator).Assembly.GetTypes().Where(t =>
            t.Namespace is "Remex.Core.Routines" or "Remex.Core.Messages.Routines"
            && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));

    [Fact]
    public void NoRoutineTypeDeclaresARequiredMember()
    {
        var types = RoutineTypes().ToList();
        Assert.Contains(typeof(RoutinesSyncPayload), types);
        Assert.Contains(typeof(Routine), types);

        var offenders = types
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "a required member makes System.Text.Json throw on a missing field, which drops the whole " +
            "session (spec T18): " + string.Join(", ", offenders));
        Assert.DoesNotContain(types, t => t.IsDefined(typeof(RequiredMemberAttribute), inherit: false));
    }

    [Fact]
    public void TheEnvelopeRoutineSlotsAreNotRequired()
    {
        var slots = typeof(RemexMessage).GetProperties()
            .Where(p => p.Name.StartsWith("Routine", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(9, slots.Count);
        Assert.All(slots, p => Assert.False(p.IsDefined(typeof(RequiredMemberAttribute), inherit: false), p.Name));
    }
}
