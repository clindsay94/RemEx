namespace Remex.Agent.Services.Input.Linux;

/// <summary>
/// The portal calls <see cref="LinuxInputSimulationService"/> makes, behind a seam so the values it
/// sends can be asserted without a D-Bus session.
/// </summary>
/// <remarks>
/// <para>
/// THIS EXISTS FOR THE SAME REASON THE <c>InputToolLauncher</c> DELEGATE DOES, and for a defect that
/// had already shipped. RemEx-y45x was this service handing the portal a raw scroll delta — a value
/// in Windows' 120-per-notch <c>WHEEL_DELTA</c> units — as the <c>steps</c> argument of
/// <c>NotifyPointerAxisDiscrete</c>, which the portal documents as "the number of steps scrolled".
/// One notch from the phone asked the compositor for 120. The two shell backends both divided by 120
/// and were fine, so the bug lived only on the branch that nothing could observe.
/// </para>
/// <para>
/// The constructor remark on <see cref="LinuxInputSimulationService"/> already states the rule this
/// follows: the defect class is not "the mapping is wrong", it is "the argv is wrong", and only the
/// call site is worth pinning. The shell backends got that seam; the portal backend did not, and the
/// gap was exactly wide enough for a 120x error. An interface rather than a delegate here because
/// there are seven operations and one gate (<see cref="IsActive"/>) that decide the branch together.
/// </para>
/// <para>
/// Deliberately narrower than <see cref="LinuxPortalInputInjector"/>'s public surface: it carries the
/// members the service actually calls, not everything the injector can do. <c>NotifyPointerMotionAbsolute</c>
/// and <c>SessionHandle</c> are absent because no branch here uses them.
/// </para>
/// <para>
/// Disposal is absent for a narrower reason than "the service does not own it" — the service does
/// construct it. It is absent because the service never disposed it and lives for the process, so
/// omitting <see cref="IAsyncDisposable"/> here narrows the type without changing any lifetime. If
/// that ever stops being true, this interface is the wrong place to fix it: the service would need a
/// disposal path first.
/// </para>
/// <para>
/// One asymmetry worth knowing about: injecting a sink bypasses the <c>IsWaylandSession</c> check, so
/// a test can construct a portal path production would not produce. Tests here pass
/// <c>IsWaylandSession: true</c> to stay faithful to the real arrangement.
/// </para>
/// </remarks>
internal interface IPortalInputSink
{
    /// <summary>Whether the portal session is up. Every branch is gated on this.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Brings the portal session up, prompting the user on first use. Never reached once
    /// <see cref="IsActive"/> is true, which is what lets a test fake skip it entirely.
    /// </summary>
    Task<bool> EnsureStartedAsync(CancellationToken ct = default);

    /// <summary>Relative pointer motion, in the stream's logical coordinate space.</summary>
    void NotifyPointerMotionRelative(double dx, double dy);

    /// <summary>Pointer button transition, taking a Linux <c>BTN_*</c> code.</summary>
    void NotifyPointerButton(int linuxButtonCode, bool pressed);

    /// <summary>
    /// Scroll in whole detents — NOT in the 120-per-notch wire unit. See the type remark.
    /// </summary>
    void NotifyPointerScrollDiscrete(int dx, int dy);

    /// <summary>Key transition, taking a Linux keycode.</summary>
    void NotifyKeyboardKeycode(int keycode, bool pressed);

    /// <summary>Key transition, taking an X11 keysym.</summary>
    void NotifyKeyboardKeysym(int keysym, bool pressed);
}
