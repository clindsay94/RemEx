using Remex.Core.Routines;
using Remex.Core.Services.Command;

namespace Remex.Core.Tests.Routines;

/// <summary>
/// The rows of routines spec §6.3-§6.5 that the shared fixtures do not pin on their own (§13.1).
/// </summary>
public class RoutineValidatorTests
{
    private const string Host = "9f2c4be07a1d33e5";

    private static Routine Make(RoutineTrigger? trigger = null, params RoutineStep[] steps) => new()
    {
        Id = "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10",
        Name = "Test",
        HostIdentity = Host,
        Enabled = true,
        Revision = 1,
        Trigger = trigger ?? new RoutineTrigger { Type = RoutineTriggerTypes.Manual },
        Steps = steps.Length == 0 ? [new RoutineStep { Type = RoutineStepTypes.Power, Verb = RoutinePowerVerbs.Lock }] : [.. steps],
    };

    private static readonly RoutineTrigger PcIdle = new() { Type = RoutineTriggerTypes.PcIdle, IdleMinutes = 10 };

    private static RoutineStep Power(string verb, int? delay = null) =>
        new() { Type = RoutineStepTypes.Power, Verb = verb, DelaySeconds = delay };

    private static RoutineStep Delay(int seconds) => new() { Type = RoutineStepTypes.Delay, Seconds = seconds };

    private static string Code(Routine routine) => RoutineValidator.ValidateRoutine(routine).ReasonCode;

    [Fact]
    public void TheDestructiveSetIsExactlyD1()
    {
        Assert.Equal(
            new[] { "SHUTDOWN", "FORCESHUTDOWN", "RESTART", "FORCERESTART", "RESTARTTOUEFI", "SIGNOUT", "SLEEP", "HIBERNATE" }
                .Order(StringComparer.Ordinal),
            RoutinePowerVerbs.Destructive.Order(StringComparer.Ordinal));
        Assert.False(RoutinePowerVerbs.IsDestructive(RoutinePowerVerbs.Lock));
        Assert.False(RoutinePowerVerbs.IsDestructive(RoutinePowerVerbs.MonitorOff));
        Assert.All(RoutinePowerVerbs.Destructive, v => Assert.True(RoutinePowerVerbs.IsDestructive(v)));
    }

    [Fact]
    public void TheRoutineVerbsAreTheScriptIngressVerbsWithoutWakeOnLan()
    {
        // D5: WAKEONLAN is reserved. Everything else the 8338 listener takes is a routine verb.
        var expected = CommandVerbs.ScriptIngress.Where(v => v != "WAKEONLAN").Order(StringComparer.Ordinal);
        Assert.Equal(expected, RoutinePowerVerbs.All.Order(StringComparer.Ordinal));
        Assert.Equal(10, RoutinePowerVerbs.All.Count);
        Assert.DoesNotContain(RoutinePowerVerbs.ReservedWakeOnLan, RoutinePowerVerbs.All);
    }

    [Fact]
    public void WakeOnLanIsRejectedAsInvalidField()
    {
        Assert.Equal(RoutineReasonCodes.InvalidField, Code(Make(null, Power(RoutinePowerVerbs.ReservedWakeOnLan))));
    }

    [Theory]
    [InlineData("SHUTDOWN")]
    [InlineData("SIGNOUT")]
    [InlineData("SLEEP")]
    [InlineData("HIBERNATE")]
    public void ADestructiveStepMustBeLastInAPcRunRoutine(string verb)
    {
        Assert.Equal(RoutineReasonCodes.DestructiveNotLast, Code(Make(PcIdle, Power(verb), Delay(1))));
        Assert.Equal(RoutineReasonCodes.Ok, Code(Make(PcIdle, Delay(1), Power(verb))));
    }

    [Fact]
    public void APhoneRunRoutineMayContinueAfterItsDestructiveStep()
    {
        Assert.Equal(RoutineReasonCodes.Ok, Code(Make(null, Power("RESTART"), new RoutineStep { Type = RoutineStepTypes.WaitOnline }, Delay(1))));
    }

    [Fact]
    public void LockAndMonitorOffAreNotDestructiveAndMayRepeatAnywhere()
    {
        Assert.Equal(RoutineReasonCodes.Ok, Code(Make(PcIdle, Power("LOCK"), Power("MONITOROFF"), Power("LOCK"), Delay(1))));
    }

    [Fact]
    public void AtMostOneDestructiveStep()
    {
        Assert.Equal(RoutineReasonCodes.TooManyDestructive, Code(Make(PcIdle, Power("SLEEP"), Power("HIBERNATE"))));
    }

    [Theory]
    [InlineData("SHUTDOWN", true)]
    [InlineData("RESTARTTOUEFI", true)]
    [InlineData("SLEEP", false)]
    [InlineData("SIGNOUT", false)]
    public void DelaySecondsOnlyForTheDelayableVerbs(string verb, bool allowed)
    {
        var expected = allowed ? RoutineReasonCodes.Ok : RoutineReasonCodes.FieldNotAllowed;
        Assert.Equal(expected, Code(Make(null, Power(verb, delay: 10))));
    }

    [Fact]
    public void PowerDelayRangeIsZeroToSixHundred()
    {
        Assert.Equal(RoutineReasonCodes.Ok, Code(Make(null, Power("SHUTDOWN", 0))));
        Assert.Equal(RoutineReasonCodes.InvalidField, Code(Make(null, Power("SHUTDOWN", 601))));
        Assert.Equal(RoutineReasonCodes.InvalidField, Code(Make(null, Power("SHUTDOWN", -1))));
    }

    [Fact]
    public void WakeAndWaitOnlineAreForbiddenInPcRunRoutines()
    {
        var wake = new RoutineStep { Type = RoutineStepTypes.Wake, Mac = "0A:1B:2C:3D:4E:5F" };
        Assert.Equal(RoutineReasonCodes.StepNotAllowedOnPc, Code(Make(PcIdle, wake)));
        Assert.Equal(RoutineReasonCodes.StepNotAllowedOnPc, Code(Make(PcIdle, new RoutineStep { Type = RoutineStepTypes.WaitOnline })));
        Assert.Equal(RoutineReasonCodes.Ok, Code(Make(null, wake)));
    }

    [Fact]
    public void StepCountBoundsAreOneToTwelve()
    {
        Assert.Equal(RoutineReasonCodes.InvalidField, Code(Make() with { Steps = [] }));
        Assert.Equal(RoutineReasonCodes.Ok, Code(Make(null, Enumerable.Repeat(Delay(1), 12).ToArray())));
        Assert.Equal(RoutineReasonCodes.TooManySteps, Code(Make(null, Enumerable.Repeat(Delay(1), 13).ToArray())));
    }

    [Theory]
    [InlineData(RoutineStepTypes.Wake, 2)]
    [InlineData(RoutineStepTypes.WaitOnline, 3)]
    [InlineData(RoutineStepTypes.Notify, 4)]
    public void PerTypeStepCapsAreEnforced(string type, int count)
    {
        var step = type switch
        {
            RoutineStepTypes.Wake => new RoutineStep { Type = type, Mac = "0A:1B:2C:3D:4E:5F" },
            RoutineStepTypes.WaitOnline => new RoutineStep { Type = type, TimeoutSeconds = 30 },
            _ => new RoutineStep { Type = type, Target = RoutineNotifyTargets.Phone, Title = "x" },
        };
        Assert.Equal(RoutineReasonCodes.InvalidField, Code(Make(null, Enumerable.Repeat(step, count).ToArray())));
        Assert.Equal(RoutineReasonCodes.Ok, Code(Make(null, Enumerable.Repeat(step, count - 1).ToArray())));
    }

    [Fact]
    public void BudgetsFollowTheSpecFormulas()
    {
        var launch = new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = "c1d2e3f4-0000-4000-8000-00000000abcd" };
        var waitDefault = new RoutineStep { Type = RoutineStepTypes.WaitOnline };

        // Phone: Σdelay + Σwait (default 300) + 60 per host step + 15 with a destructive step.
        Assert.Equal(10 + 300 + 60 + 60 + 15, RoutineValidator.BudgetSeconds(Make(null, Delay(10), waitDefault, launch, Power("SLEEP"))));

        // PC: Σdelay + 30 per step + 15 with a destructive step.
        Assert.Equal(10 + (30 * 2) + 15, RoutineValidator.BudgetSeconds(Make(PcIdle, Delay(10), Power("SLEEP"))));
    }

    [Fact]
    public void AllowedFieldsAreEnforcedPerTriggerType()
    {
        Assert.Equal(RoutineReasonCodes.FieldNotAllowed,
            Code(Make(new RoutineTrigger { Type = RoutineTriggerTypes.NfcTap, IdleMinutes = 5 })));
        Assert.Equal(RoutineReasonCodes.FieldNotAllowed,
            Code(Make(new RoutineTrigger { Type = RoutineTriggerTypes.PcIdle, IdleMinutes = 5, SessionState = "locked" })));
        Assert.Equal(RoutineReasonCodes.Ok,
            Code(Make(new RoutineTrigger { Type = RoutineTriggerTypes.PcIdle, IdleMinutes = 5, IgnoreWhileMediaPlaying = false })));
    }

    [Fact]
    public void HomeTriggersCheckTheKnownHomesWhenTheCallerHasThem()
    {
        const string home = "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11";
        var routine = Make(new RoutineTrigger { Type = RoutineTriggerTypes.HomeArrive, HomeId = home });

        Assert.True(RoutineValidator.ValidateRoutine(routine).IsValid);
        Assert.True(RoutineValidator.ValidateRoutine(routine, new RoutineValidationContext { KnownHomeIds = [home] }).IsValid);
        Assert.Equal(RoutineReasonCodes.HomeNotSet,
            RoutineValidator.ValidateRoutine(routine, new RoutineValidationContext { KnownHomeIds = [] }).ReasonCode);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(1800, true)]
    [InlineData(1801, false)]
    public void LeaveDebounceRange(int seconds, bool valid)
    {
        var routine = Make(new RoutineTrigger
        {
            Type = RoutineTriggerTypes.HomeLeave,
            HomeId = "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11",
            LeaveDebounceSeconds = seconds,
        });
        Assert.Equal(valid, RoutineValidator.ValidateRoutine(routine).IsValid);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    public void SensorSustainRange(int seconds, bool valid)
    {
        var routine = Make(new RoutineTrigger
        {
            Type = RoutineTriggerTypes.PcSensor, SensorId = "cpu", Direction = "below", Threshold = -3, SustainSeconds = seconds,
        });
        Assert.Equal(valid, RoutineValidator.ValidateRoutine(routine).IsValid);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void WaitOnlineRange(int seconds, bool valid)
    {
        var routine = Make(null, new RoutineStep { Type = RoutineStepTypes.WaitOnline, TimeoutSeconds = seconds });
        Assert.Equal(valid, RoutineValidator.ValidateRoutine(routine).IsValid);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void WakePortRange(int port, bool valid)
    {
        var routine = Make(null, new RoutineStep { Type = RoutineStepTypes.Wake, Mac = "0A:1B:2C:3D:4E:5F", Port = port });
        Assert.Equal(valid, RoutineValidator.ValidateRoutine(routine).IsValid);
    }

    [Fact]
    public void NotifyTextIsMeasuredAfterStrippingControlCharacters()
    {
        var title = new string('t', 40) + "\n\u0007";
        var routine = Make(null, new RoutineStep { Type = RoutineStepTypes.Notify, Target = "pc", Title = title });
        Assert.True(RoutineValidator.ValidateRoutine(routine).IsValid);
        Assert.Equal(new string('t', 40), RoutineText.Sanitize(title));
    }

    [Fact]
    public void NameLengthCountsUserPerceivedCharacters()
    {
        // 40 base letters each with a combining accent: 80 UTF-16 units, 40 user-perceived characters.
        var name = string.Concat(Enumerable.Repeat("e\u0301", 40));
        Assert.True(RoutineValidator.ValidateRoutine(Make() with { Name = name }).IsValid);
        Assert.False(RoutineValidator.ValidateRoutine(Make() with { Name = name + "e" }).IsValid);
    }

    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u0085b")]
    [InlineData("a\tb")]
    public void NamesRejectControlCharactersAndLineBreaks(string name)
    {
        Assert.Equal(RoutineReasonCodes.InvalidField, Code(Make() with { Name = name }));
    }

    [Fact]
    public void ADuplicateIdIsReportedOnTheSecondOccurrence()
    {
        var verdict = RoutineValidator.ValidateRoutines([Make(), Make() with { Name = "Copy" }]);
        Assert.True(verdict.Routines[0].IsValid);
        Assert.Equal(RoutineReasonCodes.DuplicateId, verdict.Routines[1].ReasonCode);
    }

    [Fact]
    public void ValidateStepChecksOneStepForTheHost()
    {
        Assert.True(RoutineValidator.ValidateStep(Power("SHUTDOWN", 5), hostRun: false).IsValid);
        Assert.Equal(RoutineReasonCodes.UnsupportedStep,
            RoutineValidator.ValidateStep(new RoutineStep { Type = "script" }, hostRun: false).ReasonCode);
        Assert.Equal(RoutineReasonCodes.InvalidField, RoutineValidator.ValidateStep(null, hostRun: false).ReasonCode);
    }

    [Fact]
    public void HostExecutedStepsAreThePowerLaunchMediaAndPcNotifySteps()
    {
        Assert.True(Power("LOCK").IsHostExecuted);
        Assert.True(new RoutineStep { Type = RoutineStepTypes.LaunchApp }.IsHostExecuted);
        Assert.True(new RoutineStep { Type = RoutineStepTypes.Media }.IsHostExecuted);
        Assert.True(new RoutineStep { Type = RoutineStepTypes.Notify, Target = "pc" }.IsHostExecuted);
        Assert.False(new RoutineStep { Type = RoutineStepTypes.Notify, Target = "phone" }.IsHostExecuted);
        Assert.False(new RoutineStep { Type = RoutineStepTypes.Wake }.IsHostExecuted);
        Assert.False(Delay(1).IsHostExecuted);
    }

    [Fact]
    public void TriggerTypesSplitIntoPhoneAndPcRunners()
    {
        Assert.All([RoutineTriggerTypes.PcSensor, RoutineTriggerTypes.PcIdle, RoutineTriggerTypes.PcSession],
            t => Assert.True(RoutineTriggerTypes.IsHostRun(t)));
        Assert.All([RoutineTriggerTypes.HomeArrive, RoutineTriggerTypes.HomeLeave, RoutineTriggerTypes.NfcTap, RoutineTriggerTypes.Manual],
            t => Assert.False(RoutineTriggerTypes.IsHostRun(t)));
        Assert.Equal(7, RoutineTriggerTypes.All.Count);
        Assert.Equal(7, RoutineStepTypes.All.Count);
    }
}

/// <summary>Schema migration (§6.7, RoutineMigrationTests in §13.1).</summary>
public class RoutineMigrationTests
{
    [Fact]
    public void AMissingDocumentIsAFallbackThatMustNotBePersisted()
    {
        var result = RoutineMigration.Migrate(null);

        Assert.True(result.IsFallback);
        Assert.Equal(RoutineSchema.CurrentVersion, result.Set.SchemaVersion);
        Assert.Empty(result.Set.Routines!);
    }

    [Fact]
    public void AnUnversionedDocumentIsStampedToV1()
    {
        var result = RoutineMigration.Migrate(new RoutineSet { SchemaVersion = 0 });

        Assert.False(result.IsFallback);
        Assert.Equal(1, result.Set.SchemaVersion);
        Assert.NotNull(result.Set.Routines);
    }

    [Fact]
    public void ANewerDocumentIsNeverStampedDown()
    {
        var newer = new RoutineSet { SchemaVersion = RoutineSchema.CurrentVersion + 1, Routines = [] };

        var result = RoutineMigration.Migrate(newer);

        Assert.True(result.IsNewerThanReader);
        Assert.Equal(RoutineReasonCodes.SchemaTooNew, result.Warning);
        Assert.Same(newer, result.Set);
    }

    [Fact]
    public void UnknownTypesArePreservedThroughMigration()
    {
        var odd = new Routine
        {
            Id = "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10",
            Name = "Later",
            HostIdentity = "9f2c4be07a1d33e5",
            Revision = 1,
            Trigger = new RoutineTrigger { Type = "pc.schedule" },
            Steps = [new RoutineStep { Type = "script" }],
        };

        var result = RoutineMigration.Migrate(new RoutineSet { SchemaVersion = 0, Routines = [odd] });

        Assert.Same(odd, Assert.Single(result.Set.Routines!));
        Assert.Equal(RoutineReasonCodes.UnsupportedTrigger, RoutineValidator.ValidateRoutine(odd).ReasonCode);
    }

    [Fact]
    public void ACurrentDocumentIsReturnedAsIs()
    {
        var current = new RoutineSet { SchemaVersion = RoutineSchema.CurrentVersion, Routines = [] };
        var result = RoutineMigration.Migrate(current);
        Assert.Same(current, result.Set);
        Assert.Null(result.Warning);
        Assert.False(result.IsFallback);
    }
}
