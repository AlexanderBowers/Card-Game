# Critical Count — Play Store listing and beta setup

Everything to paste into Google Play Console and AdMob for the first **internal testing** release.
Package: `com.cloudydaygames.criticalcount` (permanent from the first upload).

---

## 1. Privacy policy URL

`https://cloudydaygames.com/privacy`

Public, not a PDF, not geofenced — what Play and AdMob need. The game server renders it from the
repo's `PRIVACY.md` (see `server/CriticalCount.Server/Site.cs`), so the repo no longer has to stay
public for the policy to be reachable. Set in Play and the AdMob EU message on 2026-10-14.

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

The live listing text below was copied from Play Console and updated on 2026-10-14 for the
50-stage ladder and online play.

**Short description** (80): `A fast-paced card game of luck and skill!`

**Full description**:

```
Critical Count is a fast-paced card duel where tactical deck-building meets the tension of the draw. Draw numbered cards toward the target, play game-changing Modifiers from your hand, and hit the count without busting.

Luck deals the hand, but your deck decides the outcome. Build a custom 12-card Deck of Modifiers to adjust totals, recover from devastating draws, flip polarity on the fly, or disrupt your opponent's board before they can lock in their score.

Features:

Offline Singleplayer and Local 2-Player: Jump into quick matches anytime with no connection needed. Everything takes just a few minutes, whether practicing solo or battling head-to-head on the couch.

Face-to-Face Table Layout: Duel on a single phone or tablet using a dedicated mirrored interface. Lay the device flat between both players and play simultaneously with no passing required.

Play Online: Jump into a Quick Match against players around the world, or add friends by friend code and challenge them directly. Your own Modifier deck comes with you, and the Endless leaderboard shows who has the longest streak.

Craft Your Modifier Deck: Collect and buy unique Modifier cards between matches. Build a strategic 12-card Deck designed to challenge bad luck and take control of high-risk boards.

Rule the Escalating Ladder: Battle through 50 stages in five tiers, from Bronze to Obsidian. Each tier adds a twist - face-down cards, shaped decks, Modifiers that refill, and targets that move every set - and checkpoints keep your progress.

Master Game-Changing Effects: Deploy high-impact cards like Copy to turn a bust around, Trade Totals to steal a lead, or Shave to punish an opponent who held early.

Unlock Custom Tables and Endless Mode: Win rounds to bank Medals, unlock stylish felts and card backs, and test your longest win streak in Endless Mode.

Optional Cloud Save: Keep your progress in your own Google Play Games account when you change phones.

Simple enough to learn in one hand, with the tactical depth to reward smart deck construction. Will you trust the draw, or bend the numbers in your favor?
```

**Graphics**
- App icon 512×512: `assets/aimfor20_art/store_icon_512.png` (the −1 card, Classic "20" card back and +2 card on cream; `tools/cardgen/logogen.py --square` then `icongen.py`)
- Feature graphic 1024×500: `store/feature_graphic_1024x500.png` (the light Bronze board; rebuild with `python tools/cardgen/featuregen.py`)
- Phone screenshots (2–8, exactly 9:16): `store/screenshots/` holds the live set (2026-10-14):
  eight captioned 1080×1920 shots, in upload order: Hit 20. Exactly / Over 20? Flip it / Read the
  bluff / Climb the ladder / Wild special cards / Build your deck / Collect all 24 / Play the world.
  They were captured from v0.9.11 on the S25 and framed with Poppins captions on the sky-blue
  gradient. App Store sizes (1320×2868 and 1206×2622) and the preview/promo videos are kept outside
  the repo, in `Pictures\CriticalCount-Marketing` on the msi PC.
- Older approach, still usable for plain uncaptioned shots: take new ones on the S25, then run
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
