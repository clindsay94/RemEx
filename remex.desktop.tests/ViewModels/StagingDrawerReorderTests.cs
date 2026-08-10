using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The staging drawer reorders itself around what the host is still reporting (RemEx-yqpa).
/// </summary>
/// <remarks>
/// <para>
/// **THESE DRIVE THE VIEW MODEL, NOT THE RULE.** The rule is pure and tested next door; what is left
/// is the part that can actually corrupt the drawer — a selection sort issuing
/// <c>ObservableCollection.Move</c> against a collection that also holds cards the rule declines to
/// classify. The first version of that loop cleared the collection and refilled it from the ordered
/// list, which silently deleted every unclassified card, and no test of the pure rule could have
/// noticed.
/// </para>
/// <para>
/// Reference identity is asserted rather than content, because the decision this implements is
/// non-destructive: the same <c>SensorViewModel</c> instance a placed card binds to has to survive
/// every reorder, and a rebuilt card carrying equal values would satisfy a content comparison while
/// breaking exactly that.
/// </para>
/// </remarks>
public class StagingDrawerReorderTests
{
    private static CanvasDashboardViewModel NewDashboard() =>
        // layoutService and shell are stored and never dereferenced by this path (RemEx-w9ui).
        new(new ConnectionViewModel(), null!, null!);

    private static TelemetryPayload Tick(params (string Id, string Name)[] sensors) =>
        new()
        {
            Sensors = sensors
                .Select(s => new SensorReading { Id = s.Id, Name = s.Name, Value = 1 })
                .ToList(),
        };

    [Fact]
    public void LiveSensorsRiseAboveTheOnesTheHostStoppedReporting()
    {
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(("zulu", "Zulu"), ("alpha", "Alpha"), ("mike", "Mike")));

        // Mike goes quiet. Alpha and Zulu are still live and sort above it; nothing is dropped.
        vm.ApplyTelemetry(Tick(("zulu", "Zulu"), ("alpha", "Alpha")));

        vm.StagedCards.Select(c => c.CardTitle).Should().Equal("Alpha", "Zulu", "Mike");
        vm.StagedCards.Single(c => c.CardTitle == "Mike").IsStale.Should().BeTrue();
        vm.StagedCards.Where(c => c.CardTitle != "Mike").Should().OnlyContain(c => !c.IsStale);
    }

    [Fact]
    public void ASensorComingBackStopsBeingStaleAndRisesAgain()
    {
        // The half that eviction could never do. Under a delete-on-absence rule the card would be
        // gone and would come back as a NEW view model, dropping whatever a placed card was bound to.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(("alpha", "Alpha"), ("mike", "Mike")));
        var mike = vm.StagedCards.Single(c => c.CardTitle == "Mike");
        var mikeSensor = mike.Sensor;

        vm.ApplyTelemetry(Tick(("alpha", "Alpha")));
        mike.IsStale.Should().BeTrue();

        vm.ApplyTelemetry(Tick(("alpha", "Alpha"), ("mike", "Mike")));

        mike.IsStale.Should().BeFalse();
        vm.StagedCards.Should().Contain(c => ReferenceEquals(c, mike), "the card must survive, not be rebuilt");
        mike.Sensor.Should().BeSameAs(mikeSensor, "a placed card may still be bound to this instance");
    }

    [Fact]
    public void ASteadyHostLeavesTheDrawerCompletelyUNTOUCHED()
    {
        // **THE CHURN CLAIM, ASSERTED ON THE REAL COLLECTION.** Telemetry lands about once a second;
        // a reorder that fired every tick would reset the item containers and cancel a drag out of
        // the drawer mid-gesture. Element-by-element reference equality, because an order that
        // happened to come out the same after a rebuild would satisfy a content comparison.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(("zulu", "Zulu"), ("alpha", "Alpha")));
        var before = vm.StagedCards.ToList();

        vm.ApplyTelemetry(Tick(("zulu", "Zulu"), ("alpha", "Alpha")));

        vm.StagedCards.Should().HaveSameCount(before);
        vm.StagedCards.Zip(before).Should().OnlyContain(p => ReferenceEquals(p.First, p.Second));
    }

    [Fact]
    public void ACardThatIsNotASensorSURVIVESAReorderAndIsNeverMarkedStale()
    {
        // **THE BUG THE FIRST VERSION OF THE REORDER HAD.** ReturnToStaging puts every NON-sensor
        // card in this collection, and those have no sensor to resolve an identity from - so they are
        // absent from the ordered list the reorder is built on. Clearing and refilling from that list
        // deleted them outright. Nothing in the pure rule's tests could see it, because the rule is
        // never handed them.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(("zulu", "Zulu"), ("alpha", "Alpha")));

        // ADDED DIRECTLY RATHER THAN THROUGH ReturnToStaging, which is where it comes from in the
        // app. That path ends in TriggerSave, and this harness passes a null layoutService on the
        // stated grounds that the tick path never dereferences it - true, but ReturnToStaging does.
        // The state under test is "a card with no Sensor is in this collection", and that is what is
        // built here; routing through the save path would be testing the harness.
        var widget = new CanvasCardViewModel { CardType = "Clock", CardTitle = "Clock" };
        vm.StagedCards.Add(widget);
        var countWithWidget = vm.StagedCards.Count;

        // **ALPHA IS THE ONE DROPPED, AND WHICH ONE MATTERS.** The drawer is already [Alpha, Zulu];
        // dropping Zulu leaves the desired order identical, the no-change guard returns early, and no
        // reorder happens at all - which is how the first version of this test passed against a
        // reorder that deleted the widget. Dropping Alpha makes it stale, so Zulu must rise above it
        // and the collection is genuinely rewritten.
        vm.ApplyTelemetry(Tick(("zulu", "Zulu")));

        vm.StagedCards.Should().HaveCount(countWithWidget, "the reorder must not drop anything");
        vm.StagedCards.Should().Contain(c => ReferenceEquals(c, widget));
        widget.IsStale.Should().BeFalse("a card with no reading cannot have staleness claimed about it");
    }

    [Fact]
    public void AnEmptyTickMarksEverythingStaleWithoutLosingIt()
    {
        // A host that stops reporting entirely is exactly when the drawer should say so - and exactly
        // when a delete-on-absence rule would empty it.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(("alpha", "Alpha"), ("zulu", "Zulu")));

        vm.ApplyTelemetry(new TelemetryPayload { Sensors = new List<SensorReading>() });

        vm.StagedCards.Should().HaveCount(2);
        vm.StagedCards.Should().OnlyContain(c => c.IsStale);
    }
}
