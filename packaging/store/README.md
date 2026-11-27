# Microsoft Store package

The Store app is the same exe as the GitHub release, wrapped in an MSIX package. At run time it
notices the package (`App/PackageIdentity.cs`) and differs in these places only:

- **Start with Windows** switches the package startup task instead of the `Run` registry key,
  because Windows keeps a packaged app's registry writes private (`Gui/Services/StartupTaskAutostart.cs`).
  At sign-in the app waits in the tray: the task passes `--tray`, and because Windows does not
  document that it always does, the app also asks Windows whether the startup task started it
  (`App/StoreStartup.cs`). The WinRT calls behind both are checked against the Windows metadata
  by `StartupTaskInteropTests`.
- Paths shown to the user (the log in a startup error and in the diagnostics) point into the
  package folder, `%LocalAppData%\Packages\<package family name>\LocalCache`, where Windows
  redirects the app's `%AppData%` and `%LocalAppData%` writes.
- The help says that Windows deletes the settings and the log on uninstall, instead of naming
  the folders to delete by hand (`gui.help.details.storeBody`).

Everything else works as in the portable exe. Bring back stereo and Always stereo start the
packaged exe as the elevated worker through the usual UAC prompt; no `allowElevation`
capability is needed. The Always stereo backup goes to the real
`%ProgramData%\BluetoothHandsFreeToggle\backup.json`, shared with the portable exe and the
WinGet package.

| Path | What it is |
|:--|:--|
| `Package.appxmanifest` | Package manifest. The build fills in `Version` and `ProcessorArchitecture`. |
| `Assets/` | Package logos, drawn by `scripts/New-StoreAssets.ps1` from the app icon. |
| `listing/` | Store logo for the listing, drawn by the same script. |
| `listing/Screenshots/<language>/` | Two screenshots per language, drawn by `scripts/New-StoreScreenshots.ps1` from the real panel. |
| `listing-schema.csv` | Rows of the Partner Center listing import file. |

The listing texts come from `scripts/winget-locales.json`, so the Store and WinGet say the same
thing. The Store lists the app in the languages of the `Resources` element of
`Package.appxmanifest`: all 30 interface languages, en-us first. Latin American Spanish is
`es-mx` in the Store, which has no `es-419`.

## Every release

1. Bump the version in `StereoKeeper/StereoKeeper.csproj` and `App/AppInfo.cs` as usual. Store
   versions must end in `.0`; `3.1` becomes `3.1.0.0`. Each new package needs a higher version
   than the packages already in the Store.
2. Build the submission from a clean, pushed `master`:

   ```powershell
   .\scripts\Publish-Store.ps1
   ```

   It runs the tests, builds x64 and arm64, and writes to `artifacts\store\v<version>\`:
   - `StereoKeeper_<version>_x64_arm64.msixupload`, the package to upload;
   - `listing\`, the listing import folder (`listingData.csv`, the Store logo, screenshots).
   - `listingData-texts.csv`, the same listing without the image paths.
3. In Partner Center, open the app and start a new submission:
   - **Packages**: remove the previous upload and add the `.msixupload`.
   - **Store listings**: *Import* `listingData-texts.csv` (Partner Center often fails on the
     whole `listing` folder, and on the image paths of `listing\listingData.csv` imported alone),
     then add the Store logo and the screenshots of each language from `listing\` by hand, and
     write the release notes for this version in each language (the field is left empty on
     purpose).
   - Submit. Certification usually takes one to three business days.

## Test on this PC before uploading

The Store signs the package itself, so the upload is unsigned. To install a build locally, sign a
copy with a test certificate whose subject is the manifest `Publisher`:

```powershell
# Once: create the certificate and trust it (the second command needs an elevated console).
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=1BD76FFF-E0E6-4C9D-AD30-0248861C72C8" `
    -KeyUsage DigitalSignature -FriendlyName "StereoKeeper local test" -CertStoreLocation Cert:\CurrentUser\My `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
Export-Certificate -Cert $cert -FilePath .\artifacts\stereokeeper-test.cer
Import-Certificate -FilePath .\artifacts\stereokeeper-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

# Every build:
.\scripts\Publish-Store.ps1 -SkipRepositoryChecks -TestCertificateThumbprint $cert.Thumbprint
Add-AppxPackage .\artifacts\store\v3.0\local-test\StereoKeeper_3.0.0.0_x64_arm64.msixbundle
```

Check the app from the Start menu: the tray icon, Bring back stereo, Always stereo on and off,
and Start with Windows (Task Manager → Startup apps lists StereoKeeper). Remove it afterwards with
`Get-AppxPackage *StereoKeeper* | Remove-AppxPackage`.

Run the Windows App Certification Kit, the same checks the Store runs first, from an elevated
console:

```powershell
.\scripts\Publish-Store.ps1 -SkipRepositoryChecks -RunWack
```

## First submission only

1. **Reserve the name** StereoKeeper in Partner Center (Apps and games → New product → MSIX or PWA
   app), in the same account as DevProjex.
2. **Product identity** (Product management → Product identity): `Package/Identity/Name`,
   `Publisher` and `PublisherDisplayName` must equal `Package.appxmanifest`. The manifest uses
   `StarkIndustriesDev.StereoKeeper`, the account's publisher ID, and `Stark Industries Dev`;
   correct the manifest if Partner Center shows something else.
3. **Properties**:
   - Category: *Utilities & tools*.
   - Privacy policy URL: `https://github.com/Avazbek22/StereoKeeper/blob/master/PRIVACY.md`
   - Website: `https://github.com/Avazbek22/StereoKeeper`
   - Support contact: `https://github.com/Avazbek22/StereoKeeper/issues`
   - Product declarations: the app does not access the network, does not collect data, and
     depends on no non-Microsoft drivers or NT services.
4. **Age ratings**: the questionnaire (utility, no user content, no purchases) gives the lowest
   rating.
5. **Pricing and availability**: free, all markets.
6. **Submission options → restricted capabilities**, `runFullTrust`:

   > StereoKeeper is a Win32 desktop app (.NET, Avalonia). It needs full trust to show its
   > notification-area icon, read the state of the Windows Bluetooth hands-free services through
   > the Service Control Manager, list the apps that record from the headset microphone through
   > Core Audio, and start its own exe as an elevated worker through the standard UAC prompt.

7. **Submission options → notes for certification**:

   > StereoKeeper is a free, open-source tray app (https://github.com/Avazbek22/StereoKeeper)
   > for Bluetooth headphones that Windows leaves in the Hands-Free (HFP) call profile, where
   > games and music sound mono and muffled. Testing needs a Bluetooth Classic headset; without
   > one the panel says that no headphones are connected.
   >
   > The app runs as a standard user. Only when the user clicks "Bring back stereo" or flips the
   > "Always stereo" switch does it start its own packaged exe as an elevated worker through the
   > standard UAC prompt (ShellExecute "runas"; no allowElevation capability). The worker runs
   > that one operation on two Windows services, BTAGService and BthHFSrv, through the Service
   > Control Manager API, reports progress over a named pipe limited to the current user and
   > Administrators, and exits. No driver or service is installed.
   >
   > User consent and restore (policy 10.2.8): "Bring back stereo" only restarts a service that
   > is already running and changes no settings. "Always stereo" first saves the original
   > service state to %ProgramData%\BluetoothHandsFreeToggle\backup.json, then disables the
   > hands-free services; turning the switch off restores exactly that state. The panel shows
   > which state the user is in, and the built-in help explains how to undo everything. If an
   > HFP service hangs in the Stopping state, the app may end its svchost.exe, and only when
   > that process hosts nothing but the HFP service.
   >
   > Because an MSIX uninstall runs no app code, the built-in help tells users to turn "Always
   > stereo" off before uninstalling; reinstalling the app and turning it off restores the saved
   > state at any time.
   >
   > The app makes no network connections and collects no data.
