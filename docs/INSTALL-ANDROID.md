```text
 >R_  RemEx
 your PC, from your phone
```

# Installing RemEx on Android

## Which way to install

There are two ways to get RemEx on your phone: Google Play open testing, or sideloading the APK
from GitHub Releases. Both install the same app, built from the same source, signed under the same
verified Play developer account. The difference is just how updates reach you: through Play
automatically, or by downloading the next release yourself.

Requirement either way: **Android 14 or newer**.

## Google Play (open testing)

1. Open the listing: https://play.google.com/store/apps/details?id=com.clindsay94.remex
2. Join the open test from the card on the listing page.
3. Install it like any other Play Store app.

That's it. Updates arrive through Play from here on.

> [!NOTE]
> Open-testing builds on Play are the same builds published to GitHub Releases, not a separate
> track. If you run into a problem, file it as a GitHub issue:
> https://github.com/clindsay94/RemEx/issues/new/choose

## Sideloading the APK

1. Download `RemEx-V2.5.0-release.apk` from [GitHub Releases](https://github.com/clindsay94/RemEx/releases).
2. Open the downloaded file. Android will ask, once, to allow installs from whichever app opened it
   (your browser or Files). Allow it, then install.
3. You'll get updates by coming back to Releases for the next version; sideloading doesn't wire up
   automatic updates the way Play does.

> [!NOTE]
> On Samsung phones, Auto Blocker can refuse the install outright. Go to Settings > Security and
> privacy > Auto Blocker, turn it off for the install (or allow the app specifically), then turn it
> back on afterward if you'd like. Play Protect may also scan the APK before installing; that's
> expected and not a sign anything is wrong.

## First pairing

The PC side needs to be running `remex.agent` already. See
[README: Windows host](../README.md#windows-host) or [README: Linux host](../README.md#linux-host),
or [LINUX_INSTALL.md](LINUX_INSTALL.md) for the full Linux walkthrough.

1. Get your phone and PC on the same Wi-Fi / LAN.
2. Open the app on your phone. On a fresh install, a short first-run tutorial walks you through the
   basics; you can watch it again later. The in-app FAQ (the More tab on the phone, the About page
   on the PC) covers that under "Can I watch the tutorial again?".
3. Your PC should show up in the list on its own through auto-discovery. If it doesn't, the PC's
   dashboard displays its own address, and you can add it by IP instead (FAQ: "How do I find my
   PC's IP address?", "Auto-discovery isn't finding my PC").
4. Tap the PC. The PC shows a **6-digit PIN** on its screen. Type that PIN into your phone within
   about **2 minutes**, before it expires.
5. Once that's done, your phone remembers the PC's certificate. Reconnecting after that is just
   tapping the PC in the list, no PIN required.

## Remote access away from home

By default this is a same-LAN tool: your phone and PC need to be able to reach each other directly.
To reach your PC from elsewhere, run a VPN of your own, such as Tailscale, and connect both devices
to it (FAQ: "How do I set up Tailscale?"). The Linux host setup guide covers the host side of that:
see the Tailscale section in [LINUX_INSTALL.md](LINUX_INSTALL.md). RemEx itself has no relay server
of its own.

## If something goes wrong

If your phone refuses to connect, or the PC suddenly asks it to re-pair (FAQ: "Phone refuses to
connect / asks to re-pair"), unpair and pair again. The PC's Settings lists paired phones with
rename and unpair controls; the phone lists its paired PCs the same way.

To report a problem, open a GitHub issue:
https://github.com/clindsay94/RemEx/issues/new/choose

For anything security-related, see [SECURITY.md](SECURITY.md).
