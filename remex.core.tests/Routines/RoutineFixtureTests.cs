using System.Text.Json.Nodes;
using Remex.Core.Messages;
using Remex.Core.Routines;
using Remex.Core.Security;
using Remex.Core.Serialization;

namespace Remex.Core.Tests.Routines;

/// <summary>
/// The fixtures are byte-identical in both trees (routines spec §13.1, RoutineFixtureParityTests).
/// </summary>
/// <remarks>
/// The Kotlin mirror reads <c>remex.android/app/src/test/resources/routines</c>; this project reads its
/// own copy. If the two drift, each side passes against a different truth and the mirror stops being
/// a mirror. Regenerate with <c>scripts/generate-routine-fixtures.py</c>, which writes both.
/// </remarks>
public class RoutineFixtureParityTests
{
    [Fact]
    public void BothCopiesHoldTheSameFilesWithTheSameBytes()
    {
        Assert.True(Directory.Exists(RoutineFixtures.SourceDir), $"{RoutineFixtures.SourceDir} is missing");
        Assert.True(Directory.Exists(RoutineFixtures.AndroidDir), $"{RoutineFixtures.AndroidDir} is missing");

        var csFiles = Directory.GetFiles(RoutineFixtures.SourceDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var ktFiles = Directory.GetFiles(RoutineFixtures.AndroidDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(csFiles, ktFiles);
        Assert.True(csFiles.Count > 20, $"only {csFiles.Count} fixtures - was the directory emptied?");

        foreach (var file in csFiles)
        {
            var cs = File.ReadAllBytes(Path.Combine(RoutineFixtures.SourceDir, file!));
            var kt = File.ReadAllBytes(Path.Combine(RoutineFixtures.AndroidDir, file!));
            Assert.True(cs.AsSpan().SequenceEqual(kt), $"{file} differs between the C# and Android fixture copies");
        }
    }

    [Fact]
    public void TheManifestNamesEveryFixtureAndNothingElse()
    {
        var named = RoutineFixtures.Validation.Select(v => v.File)
            .Concat(RoutineFixtures.Wire.Select(w => w.File))
            .Concat(RoutineFixtures.Rejected.Select(r => r.File))
            .Append(RoutineFixtures.RoutineRunsFile)
            .Append("reason-codes.json")
            .Append("host-identity-vectors.json")
            .Append("manifest.json")
            .Order(StringComparer.Ordinal)
            .ToList();
        var onDisk = Directory.GetFiles(RoutineFixtures.Dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(named, onDisk!);
    }
}

/// <summary>Each fixture's verdict matches the C# validator (RoutineFixtureValidationTests, §13.1).</summary>
public class RoutineFixtureValidationTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (file, expected) in RoutineFixtures.Validation)
        {
            data.Add(file, expected);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheValidatorReachesTheFixturesVerdict(string file, string expected)
    {
        var set = RemexJson.Deserialize(RoutineFixtures.ReadText(file), RemexJsonSerializerContext.Default.RoutineSet);

        var verdict = RoutineValidator.ValidateSet(set);

        Assert.Equal(expected, verdict.FirstFailure);
    }

    [Fact]
    public void BothValidAndEveryKindOfInvalidAreCovered()
    {
        var expected = RoutineFixtures.Validation.Select(v => v.Expected).ToHashSet(StringComparer.Ordinal);
        foreach (var code in new[]
                 {
                     RoutineReasonCodes.Ok, RoutineReasonCodes.InvalidField, RoutineReasonCodes.FieldNotAllowed,
                     RoutineReasonCodes.UnsupportedTrigger, RoutineReasonCodes.UnsupportedStep,
                     RoutineReasonCodes.StepNotAllowedOnPc, RoutineReasonCodes.TooManySteps,
                     RoutineReasonCodes.TooManyDestructive, RoutineReasonCodes.DestructiveNotLast,
                     RoutineReasonCodes.BudgetExceeded, RoutineReasonCodes.DuplicateId,
                     RoutineReasonCodes.TooManyRoutines, RoutineReasonCodes.SchemaTooNew,
                     RoutineReasonCodes.HomeNotSet, RoutineReasonCodes.WakeNoMac,
                 })
        {
            Assert.Contains(code, expected);
        }
    }
}

/// <summary>The C# code list equals the Kotlin list, through the shared fixture (§13.1).</summary>
public class RoutineReasonCodeParityTests
{
    [Fact]
    public void TheCodeListEqualsTheSharedFixtureInOrder()
    {
        var fixture = RoutineFixtures.ReadNode("reason-codes.json")["codes"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToList();

        Assert.Equal(fixture, RoutineReasonCodes.All);
    }

    [Fact]
    public void EveryConstantIsInTheListExactlyOnce()
    {
        var constants = typeof(RoutineReasonCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(constants, RoutineReasonCodes.All.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(RoutineReasonCodes.All.Count, RoutineReasonCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(RoutineReasonCodes.All, code => Assert.Matches("^[a-z][a-z0-9_]*$", code));
    }

    [Fact]
    public void TheCodesTheSpecNamesExist()
    {
        // Spot checks against routines spec §10.1 (73 codes).
        Assert.Equal(73, RoutineReasonCodes.All.Count);
        Assert.Equal("ok", RoutineReasonCodes.All[0]);
        Assert.Equal("internal_error", RoutineReasonCodes.All[^1]);
    }
}

/// <summary>C# HostIdentity.KeyFor equals Kotlin HostIdentity.keyFor on shared vectors (§13.1).</summary>
public class HostIdentityVectorTests
{
    public static TheoryData<string?, string?> Vectors()
    {
        var data = new TheoryData<string?, string?>();
        foreach (var vector in RoutineFixtures.ReadNode("host-identity-vectors.json")["vectors"]!.AsArray())
        {
            data.Add(vector!["pin"]?.GetValue<string>(), vector["key"]?.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void KeyForMatchesTheSharedVector(string? pin, string? key)
    {
        Assert.Equal(key, HostIdentity.KeyFor(pin));
    }

    [Fact]
    public void TheVectorsCoverThePrefixCaseAndBlankRules()
    {
        Assert.True(Vectors().Count() >= 8);
        Assert.True(HostIdentity.IsSameHost("abc", " sha256/abc "));
        Assert.False(HostIdentity.IsSameHost("abc", "ABC"));
        Assert.False(HostIdentity.IsSameHost(null, null));
    }

    [Fact]
    public void AKeyIsSixteenLowerCaseHexCharacters()
    {
        var key = HostIdentity.KeyFor("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=");
        Assert.NotNull(key);
        Assert.Matches("^[0-9a-f]{16}$", key);
    }
}

/// <summary>
/// Every wire fixture reads into its slot and re-emits as the same JSON tree (RoutineWireRoundTripTests).
/// </summary>
/// <remarks>
/// The fixtures are the envelopes as the Kotlin builders emit them (<c>{"type", "&lt;slot&gt;"}</c>, no
/// <c>protocolVersion</c>) with every payload field populated - the PhoneThemeSnapshotRoundTripTests
/// "JSON exactly as the phone sends it" pattern, applied to all nine types. The Kotlin
/// RoutineWireFixtureTest reads the same files and asserts its builders emit the same trees.
/// </remarks>
public class RoutineWireRoundTripTests
{
    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (file, type, slot) in RoutineFixtures.Wire)
        {
            data.Add(file, type, slot);
        }

        return data;
    }

    [Fact]
    public void EveryRoutineMessageTypeHasAFixture()
    {
        var types = RoutineFixtures.Wire.Select(w => w.Type).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                MessageTypes.RoutineCancel, MessageTypes.RoutineNotify, MessageTypes.RoutineNotifyAck,
                MessageTypes.RoutineRunReport, MessageTypes.RoutineRunRequest, MessageTypes.RoutineStepRequest,
                MessageTypes.RoutineStepResult, MessageTypes.RoutineSyncResult, MessageTypes.RoutinesSync,
            }.Order(StringComparer.Ordinal),
            types);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFixtureReadsAndReEmitsAsTheSameTree(string file, string type, string slot)
    {
        var json = RoutineFixtures.ReadText(file);

        var message = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);
        Assert.NotNull(message);
        Assert.Equal(type, message.Type);

        var emitted = JsonNode.Parse(RemexJson.Serialize(message, RemexJsonSerializerContext.Default.RemexMessage))!;
        var expected = JsonNode.Parse(json)![slot];
        Assert.NotNull(emitted[slot]);
        Assert.True(
            JsonNode.DeepEquals(expected, emitted[slot]),
            $"{file}: C# re-emitted a different {slot}:\n{emitted[slot]!.ToJsonString()}\nexpected:\n{expected!.ToJsonString()}");

        // And through the Relaxed context the JNI router uses to hand envelopes to Kotlin.
        var relaxed = JsonNode.Parse(RemexJson.Serialize(message, RemexJsonSerializerContext.Relaxed.RemexMessage))!;
        Assert.True(JsonNode.DeepEquals(expected, relaxed[slot]), $"{file}: the Relaxed context emits a different {slot}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AFullyPopulatedPayloadSurvivesTheEnvelopeRoundTrip(string file, string type, string slot)
    {
        // Model -> wire -> model -> wire is stable: nothing is dropped or renamed by the second pass.
        var first = RemexJson.Deserialize(RoutineFixtures.ReadText(file), RemexJsonSerializerContext.Default.RemexMessage)!;
        var once = RemexJson.Serialize(first, RemexJsonSerializerContext.Default.RemexMessage);
        var second = RemexJson.Deserialize(once, RemexJsonSerializerContext.Default.RemexMessage)!;
        var twice = RemexJson.Serialize(second, RemexJsonSerializerContext.Default.RemexMessage);

        Assert.Equal(once, twice);
        Assert.Equal(type, second.Type);
        Assert.NotNull(JsonNode.Parse(twice)![slot]);
    }

    [Fact]
    public void TheSyncFixtureCarriesTypedRoutines()
    {
        var message = RemexJson.Deserialize(
            RoutineFixtures.ReadText("wire.routines_sync.json"),
            RemexJsonSerializerContext.Default.RemexMessage)!;

        var sync = message.RoutinesSync!;
        Assert.Equal(7, sync.Revision);
        Assert.True(sync.Paused);
        Assert.Equal(42, sync.RunCursor);
        Assert.Equal(2, sync.Routines!.Count);
        Assert.Equal(RoutineTriggerTypes.PcIdle, sync.Routines[0].Trigger!.Type);
        Assert.Equal(85.5, sync.Routines[1].Trigger!.Threshold);
        Assert.Equal(RoutinePowerVerbs.Sleep, sync.Routines[0].Steps![1].Verb);
        Assert.All(sync.Routines, r => Assert.True(RoutineValidator.ValidateRoutine(r).IsValid));
    }

    public static TheoryData<string, string, string> RejectedCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (file, type, slot) in RoutineFixtures.Rejected)
        {
            data.Add(file, type, slot);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RejectedCases))]
    public void ANullListElementRejectsThePayloadButKeepsTheEnvelope(string file, string type, string slot)
    {
        // The Kotlin RoutineWireFixtureTest refuses the same files; the two mirrors must agree.
        var message = Remex.Core.Messages.MessageSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(RoutineFixtures.ReadText(file)));

        Assert.NotNull(message);
        Assert.Equal(type, message.Type);
        var payload = slot switch
        {
            "routineNotifyAck" => (object?)message.RoutineNotifyAck,
            "routineSyncResult" => message.RoutineSyncResult,
            "routineRunReport" => message.RoutineRunReport,
            _ => throw new InvalidOperationException($"no accessor for {slot}"),
        };
        Assert.Null(payload);
    }

    [Fact]
    public void RunRecordsWithEveryNestedKeyReEmitAsTheSameTree()
    {
        var json = RoutineFixtures.ReadText(RoutineFixtures.RoutineRunsFile);

        var runs = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.ListRoutineRun)!;
        var emitted = JsonNode.Parse(RemexJson.Serialize(runs, RemexJsonSerializerContext.Default.ListRoutineRun));

        Assert.Equal(3, runs.Count);
        Assert.Equal("client-7f3a9c", runs[1].OwnerClientId);
        Assert.Equal("Home (router 192.168.1.1)", runs[0].SourceDetail!.HomeLabel);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), emitted), $"C# re-emitted:\n{emitted!.ToJsonString()}");
    }

    [Fact]
    public void AnOmittedOwnerClientIdNeverReachesTheWire()
    {
        var run = new RoutineRun { RunId = "r", OwnerClientId = null, Seq = 5 };
        var json = RemexJson.Serialize(run, RemexJsonSerializerContext.Default.RoutineRun);
        Assert.DoesNotContain("ownerClientId", json, StringComparison.Ordinal);
        Assert.Contains("\"seq\":5", json, StringComparison.Ordinal);
    }
}
