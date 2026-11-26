# Privacy Policy — StereoKeeper

Last updated: 2026-11-26

StereoKeeper is a Windows tray app that shows whether Bluetooth headphones are in call mode (HFP) and brings stereo back. This policy covers every edition: the GitHub release, the WinGet package, and the Microsoft Store app.

## Data we collect

None. StereoKeeper has no account, no analytics, no telemetry, no advertising, and no crash reporting service. It does not send anything to the developer or to anyone else.

## What the app reads on your PC

To show its status, the app reads, locally and without changing anything:

- the state and startup type of the Windows services `BTAGService` and `BthHFSrv` (Bluetooth hands-free);
- the name and connection state of your Bluetooth audio devices;
- which apps are recording from the headset microphone right now (the app name only; the app never opens the microphone and never hears any audio);
- the Windows version, to show it in the diagnostics.

This information stays on your PC.

## What the app changes

Only what you ask for:

- **Bring back stereo** restarts the running hands-free service.
- **Always stereo** disables the hands-free services. Before that, it saves their original state in `%ProgramData%\BluetoothHandsFreeToggle\backup.json` and puts it back when you turn the switch off.

Windows asks for administrator permission each time.

## What the app stores

- Your settings (language, notifications) in `%AppData%\StereoKeeper`.
- An error log in `%LocalAppData%\StereoKeeper\logs`, which never grows past 512 KB.
- The Microsoft Store app keeps these two folders inside its own package folder, and Windows deletes them when you uninstall it.

## Diagnostics you copy yourself

**Copy info for the developer** in Settings puts the app and Windows version, the headphone mode, and the end of the error log on the clipboard, with your user folder replaced by `%USERPROFILE%`. Nothing is sent: you decide whether to paste it into a bug report.

## Network

The app makes no network connections. Links to GitHub and to the support page open in your browser only when you click them.

## Children

The app collects no data from anyone, including children.

## Changes

Changes to this policy are published in this file in the [StereoKeeper repository](https://github.com/Avazbek22/StereoKeeper), with the date above updated.

## Contact

Questions: [open an issue](https://github.com/Avazbek22/StereoKeeper/issues/new).
