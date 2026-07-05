# RemEx — Known Limitations

RemEx 2.0 is stable and feature-complete for everyday use. The items below are **known, intentional
boundaries** of this release — not bugs. Anything that *is* a bug is tracked in the **beads** issue
tracker (`bd ready`).

## By design in 2.0

- **The PC must be signed in.** RemEx starts *after* you log in (via a logon task on Windows / an
  autostart entry on Linux), so it cannot control or stream your PC while it sits at the lock or
  sign-in screen. Pre-login remote control was an intentional non-goal for 2.0.
- **Same network, or a VPN.** There is no cloud relay. Your phone and PC must be able to reach each
  other on the same local network. For access from outside your home, put both on a VPN such as
  **Tailscale** or **WireGuard** (see [`LINUX_INSTALL.md`](LINUX_INSTALL.md#remote-access-from-outside-your-home-tailscale)).
- **One monitor at a time.** Remote desktop streams a single display; you can switch which monitor is
  shown, but simultaneous multi-display streaming is on the roadmap, not in 2.0.
- **"Keep session unlocked" is Windows-only and off by default.** This security-sensitive convenience
  feature (keeps the signed-in session usable for the life of a connection) exists only on Windows and
  must be turned on deliberately, with an on-screen warning while it is active.

## Platform notes

- **Linux screen capture needs PipeWire / a desktop portal.** On Wayland, capture goes through the
  desktop portal; on first use you may be asked to grant screen-sharing permission. Run
  `remex-agent --doctor` to check PipeWire / X11 / VAAPI prerequisites before pairing.
- **External automation on port 8338 must pair first.** The optional TCP command port is default-deny:
  any external script must complete pairing and send its paired `ClientId` on every command (see
  [`API_CONTRACTS.md`](API_CONTRACTS.md) §4).

If you hit something that looks like a genuine defect, please file it — see
[`CONTRIBUTING.md`](CONTRIBUTING.md) and the [security policy](SECURITY.md) for how to report issues
(including vulnerabilities).
