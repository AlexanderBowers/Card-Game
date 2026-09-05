# Running Card Game on Android (Galaxy S25 Ultra)

There are two update loops. Use both:

| Loop | Trigger | Latency | Good for |
|---|---|---|---|
| **One-click deploy** (Godot editor) | Press the Android ▶ button in the editor toolbar | ~20 s | Iterating while you code |
| **GitHub Actions → Release → Obtainium** | `git push` to `main` | ~5–8 min | Keeping the phone current without the PC |

---

## 1. One-time setup on the PC (Godot editor)

1. **.NET 9 SDK** – Godot needs .NET 9 to export C# to Android (desktop targets still use .NET 8).
   `winget install Microsoft.DotNet.SDK.9`
2. **OpenJDK 17** – `winget install EclipseAdoptium.Temurin.17.JDK`
3. **Android SDK command-line tools** – easiest is Android Studio → SDK Manager, or:
   - Download "Command line tools only" from developer.android.com/studio, unzip to `%LOCALAPPDATA%\Android\Sdk\cmdline-tools\latest`
   - `sdkmanager "platform-tools" "build-tools;35.0.0" "platforms;android-35" "cmdline-tools;latest"`
4. **Export templates** – Godot: *Editor → Manage Export Templates → Download and Install* (must match 4.7.2 .NET).
5. **Editor settings** – *Editor → Editor Settings → Export → Android*:
   - `Java SDK Path` → JDK 17 folder
   - `Android SDK Path` → `%LOCALAPPDATA%\Android\Sdk` (must contain `platform-tools\adb.exe`)
   - Debug keystore: Godot auto-generates one; leave it, or point it at the shared keystore from §3 so USB builds and CI builds can update each other.
6. Open *Project → Export*. The **Android** preset from `export_presets.cfg` is already there and marked *Runnable*. Godot will fill in any options it considers missing; save if prompted.

## 2. Phone setup

1. *Settings → About phone → Software information* → tap **Build number** 7× to enable Developer options.
2. *Settings → Developer options* → enable **USB debugging** (and optionally **Wireless debugging**).
3. Plug in via USB (or `adb pair <ip>:<port>` for wireless). Accept the "Allow USB debugging" prompt on the phone.
4. In Godot, an Android icon appears in the top-right toolbar. Click it → the game is exported, installed and launched on the phone. Repeat any time.

## 3. Commit-based updates (GitHub Actions)

`.github/workflows/android.yml` runs on every push to `main`:
downloads Godot 4.7.2 .NET + templates, exports a debug APK, stamps `versionCode` with the run number, and publishes a GitHub Release tagged `android-v<N>` with `CardGame2.apk` attached.

### 3a. Shared signing key (do this once — important)
Android refuses to install an update unless it is signed with the same key as the installed app. Generate one key and store it as a repo secret so every CI build uses it:

```powershell
# on the PC, in any folder
keytool -genkeypair -v -keystore cardgame-debug.keystore -alias androiddebugkey `
  -storepass android -keypass android -keyalg RSA -keysize 2048 -validity 10000 `
  -dname "CN=Android Debug,O=Android,C=US"
[Convert]::ToBase64String([IO.File]::ReadAllBytes("cardgame-debug.keystore")) | Set-Clipboard
```

Then on GitHub: *repo → Settings → Secrets and variables → Actions → New repository secret*:
- `ANDROID_DEBUG_KEYSTORE_B64` = (paste clipboard)
- `ANDROID_DEBUG_KEYSTORE_USER` = `androiddebugkey` (optional, this is the default)
- `ANDROID_DEBUG_KEYSTORE_PASSWORD` = `android` (optional, this is the default)

Also point the Godot editor at this same file (*Editor Settings → Export → Android → Debug Keystore*) so USB-deployed builds and CI builds can overwrite each other on the phone. Keep the `.keystore` file out of git (already ignored).

### 3b. Auto-install on the phone with Obtainium
[Obtainium](https://github.com/ImranR98/Obtainium) is an open-source app that watches a GitHub repo's Releases and installs new APKs.

1. Install Obtainium (F-Droid, or the APK from its GitHub releases).
2. Obtainium → **+** → App source URL: `https://github.com/AlexanderBowers/Card-Game`
3. It will pick up the newest `android-v<N>` release and install `CardGame2.apk`. Allow "Install unknown apps" for Obtainium when prompted.
4. Obtainium checks for new releases in the background (interval configurable in its settings) and shows an install prompt when a new build lands. Pull-to-refresh forces a check.

(Alternative without Obtainium: open the repo's **Releases** page in the phone browser and tap the APK.)

### 3c. Triggering a build
- `git push` to `main`, **or**
- GitHub → *Actions → Android APK → Run workflow*.

Progress: GitHub → *Actions*. The first run is slower (downloads Godot, ~1 GB); later runs hit the cache.

## Notes
- `export_presets.cfg` is committed on purpose — CI needs it. The package id is `com.alexbowers.cardgame2`; change it in the preset if you want something else **before** the first install (changing it later creates a second app).
- Display settings in `project.godot` were set to `canvas_items` stretch / `expand` aspect / sensor orientation: the 2-player table (vertical layout) reads best in portrait, the vs-bot table (side-by-side) in landscape — rotate the phone.
- The Android back gesture and the in-game **Exit** button both quit the app; **Restart** reloads the current scene (fresh match, same mode).
- C# on Android is still flagged "experimental" by Godot; if an export fails, the error log in *Actions* (or the editor Output panel) is the first place to look.
