# Critical Count — Play Store listing and beta setup

Everything to paste into Google Play Console and AdMob for the first **internal testing** release.
Package: `com.cloudydaygames.criticalcount` (permanent from the first upload).

---

## 1. Privacy policy URL

`https://github.com/AlexanderBowers/Card-Game/blob/master/PRIVACY.md`

Public, not a PDF, not geofenced — what Play and AdMob need. (The repository is public, so the
game's source is too. If that's not intended, host PRIVACY.md somewhere else first — a GitHub Pages
site from a separate small public repo, or Google Sites — then make this repo private.)

## 2. AdMob → Privacy & messaging

1. **European regulations** message: app Critical Count · English · "Do not consent" on · Countries
   subject to GDPR · paste the privacy policy URL · **Publish**.
2. **US state regulations** message: app Critical Count · English · all current and future states ·
   **Publish**.

## 3. Play Console — create the app

- App name **Critical Count** · default language English (United States) · **Game** · **Free**.
- Accept the declarations (Developer Program Policies, US export laws).

## 4. Store listing

**App name** (30): `Critical Count`

**Short description** (80): `A quick, clever card duel: build your score to 20 without going over.`

**Full description**:

```
Critical Count is a fast, friendly card duel. Draw from your deck, play Modifiers from your hand,
and land as close to 20 as you can - without going over.

Every set is a little bet with yourself: draw one more card, or hold? Your Modifiers let you fix
a bad draw or steal a win at the last moment, and your opponent has a hand of their own.

- Climb ten stages, from Bronze to Obsidian, each with its own table, deck and rules
- Collect and buy Modifiers, then build the twelve-card side deck you take into each match
- Meet special cards that change the game: Copy, Trade Totals, Shave, Trade Hands, Recall, Veto
- Clear the ladder to unlock Endless mode and chase your best streak
- Unlock new decks and boards with the medals you win
- Play a friend face to face on one phone in local 2-player
- Plays fully offline. Matches take a few minutes.

Easy to learn in one match, with a short guided tutorial, and plenty to master.
```

**Graphics**
- App icon 512×512: `assets/aimfor20_art/store_icon_512.png` (the −1 card, Classic "20" card back and +2 card on cream; `tools/cardgen/logogen.py --square` then `icongen.py`)
- Feature graphic 1024×500: `store/feature_graphic_1024x500.png` (the light Bronze board; rebuild with `python tools/cardgen/featuregen.py`)
- Phone screenshots (2–8, exactly 9:16): `store/screenshots/` holds the beta set. Take new ones on the S25, then run
  `python tools/cardgen/storeshots.py <folder>`. Play only accepts phone screenshots at exactly
  9:16 or 16:9, and the S25's 19.5:9 isn't; the script pads to an exact 9:16 with the
  game's near-white room colour (instead of cropping off the score or hand) and writes the results
  to `<folder>/play`. Suggested set: (1) a stage mid-set with a Modifier picked up and the green
  on-target glow; (2) the Market; (3) a special card being played; (4) the Collection; (5) the Shop;
  (6) Endless mode on its night table.

**Category**: Games → Card · **Tags**: Card, Casual, Strategy, Single player, Offline
**Contact email**: cloudydaygamesllc@gmail.com

## 5. App content (Policy → App content)

| Section | Answer |
|---|---|
| Privacy policy | the URL in §1 |
| App access | All functionality is available without special access |
| Ads | **Yes, my app contains ads** |
| Content rating | IARC questionnaire, category **Games** (see below) |
| Target audience | **13–15, 16–17, 18+**. Does not appeal to children. |
| News app | No |
| Government app | No |
| Financial features | None |
| Health | None |
| Data safety | see §6 |

**Content rating answers**: no violence, fear, sexual content, bad language, drugs, crude humour.
Gambling: the game has **no betting and no real or virtual currency wagered on outcomes** — medals
are earned by winning, never staked — so answer **No** to simulated gambling. Users don't interact
or share content, no location sharing. **Digital purchases: Yes** (Remove Ads).
Expected rating: Everyone / PEGI 3.

## 6. Data safety

Only Google's AdMob SDK sends anything off the phone. The developer receives nothing.

- Does your app collect or share user data? **Yes**
- Is all user data encrypted in transit? **Yes**
- Do you provide a way for users to request deletion? **No** (the developer stores nothing; the ad ID
  is reset/deleted in Android settings)

| Data type | Collected | Shared | Purposes | Optional? |
|---|---|---|---|---|
| Location → Approximate location (from IP) | Yes | Yes | Advertising, Analytics, Fraud prevention | Required |
| App activity → App interactions | Yes | Yes | Advertising, Analytics, Fraud prevention | Required |
| App info and performance → Diagnostics | Yes | Yes | Advertising, Analytics, Fraud prevention | Required |
| Device or other IDs → Device or other IDs (ad ID, app set ID) | Yes | Yes | Advertising, Analytics, Fraud prevention | Required |

Ephemeral: No. Remove Ads purchases are processed by Google Play, not collected by the app.
Source: Google's AdMob Play data disclosure guidance.

## 7. Internal testing release

1. Testing → **Internal testing** → Testers: create a list, add the testers' Google-account emails.
2. Create release → accept **Play App Signing** (Google holds the app signing key; the
   `cloudyday_release.keystore` becomes the upload key).
3. Upload the AAB from the latest **Android Release AAB** run (release branch).
4. Release name `0.9.<run>` · notes: "First beta. Thanks for playing — send feedback to
   cloudydaygamesllc@gmail.com."
5. Save → Review → **Start rollout to Internal testing**. Internal testing has no review wait.
6. Copy the **opt-in link** and send it to testers; they accept, then install from Play.

After it's live: in AdMob, **link the app to the Play listing**, then wait for AdMob's app review
before live ads serve (test devices keep getting test ads).

## Later, before production

- New personal developer accounts must run a **closed test with at least 12 testers for 14 days**
  before applying for production access.
- app-ads.txt on a developer website listed in Play:
  `google.com, pub-9876378155655051, DIRECT, f08c47fec0942fa0`
- Moving the app to the Cloudy Day Games organisation account is an app transfer; the package
  name stays.
