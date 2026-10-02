using Remex.Agent.Services.Home;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The host's in-memory pinned-sensor snapshot (RemEx-wqo7a.5): a publish that changes nothing is not
/// news, a phone request is always answered by the next publish, and the revision only goes up.
/// </summary>
public class HomePinnedSensorsStoreTests
{
    [Fact]
    public void BeforeAnyPublishTheListIsEmptyAtRevisionZero()
    {
        var store = new HomePinnedSensorsStore();

        Assert.Empty(store.Current.SensorNames);
        Assert.Empty(store.Current.PinnableSensorNames);
        Assert.Equal(0, store.Current.Revision);
    }

    [Fact]
    public void TheFirstPublishIsAlwaysNewsEvenWhenEmpty()
    {
        var store = new HomePinnedSensorsStore();
        var raised = 0;
        store.Changed += _ => raised++;

        Assert.True(store.PublishFromPc([], []));
        Assert.Equal(1, raised);
        Assert.Equal(1, store.Current.Revision);
    }

    [Fact]
    public void AnEqualRepublishIsNotRebroadcast()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp", "GPU Temp"]);
        var raised = 0;
        store.Changed += _ => raised++;

        Assert.False(store.PublishFromPc(["CPU Temp"], ["CPU Temp", "GPU Temp"]));
        Assert.Equal(0, raised);
        Assert.Equal(1, store.Current.Revision);
    }

    [Fact]
    public void AChangedListBumpsTheRevisionAndRaisesTheNewSnapshot()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp", "GPU Temp"]);
        HomePinnedSensors? raised = null;
        store.Changed += s => raised = s;

        Assert.True(store.PublishFromPc(["CPU Temp", "GPU Temp"], ["CPU Temp", "GPU Temp"]));
        Assert.Same(store.Current, raised);
        Assert.Equal(2, raised!.Revision);
        Assert.Equal(["CPU Temp", "GPU Temp"], raised.SensorNames);
    }

    [Fact]
    public void NamesAreNormalizedBeforeTheyAreCompared()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp", "cpu temp", "  "], ["CPU Temp"]);

        Assert.Equal(["CPU Temp"], store.Current.SensorNames);
        Assert.False(store.PublishFromPc(["CPU Temp"], ["CPU Temp", "CPU TEMP"]));
    }

    [Fact]
    public void APhoneRequestIsAnsweredByTheNextPublishEvenWhenNothingChanged()
    {
        // The phone toggles optimistically. When the desktop refuses (no card on the canvas), the
        // lists do not change, and without this the phone would keep showing a pin the PC never made.
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp"]);
        var raised = 0;
        store.Changed += _ => raised++;

        store.RequestFromPhone(new HomePinChange { SensorName = "Fan 3", Pinned = true }, "phone-1");

        Assert.True(store.PublishFromPc(["CPU Temp"], ["CPU Temp"]));
        Assert.Equal(1, raised);
        Assert.Equal(2, store.Current.Revision);

        // Answered once: the publish after that is an ordinary unchanged one again.
        Assert.False(store.PublishFromPc(["CPU Temp"], ["CPU Temp"]));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void APhoneRequestIsPassedOnWithItsClientAndChangesNothingByItself()
    {
        var store = new HomePinnedSensorsStore();
        store.PublishFromPc(["CPU Temp"], ["CPU Temp", "GPU Temp"]);
        (HomePinChange Change, string ClientId)? seen = null;
        store.PhoneChangeRequested += (change, clientId) => seen = (change, clientId);

        store.RequestFromPhone(new HomePinChange { SensorName = "GPU Temp", Pinned = true }, "phone-1");

        Assert.Equal("GPU Temp", seen!.Value.Change.SensorName);
        Assert.Equal("phone-1", seen.Value.ClientId);
        Assert.Equal(["CPU Temp"], store.Current.SensorNames);
    }

    [Fact]
    public void AnInvalidRequestIsNotPassedOn()
    {
        var store = new HomePinnedSensorsStore();
        var raised = 0;
        store.PhoneChangeRequested += (_, _) => raised++;

        store.RequestFromPhone(new HomePinChange { SensorName = " ", Pinned = true }, "phone-1");

        Assert.Equal(0, raised);
    }
}
