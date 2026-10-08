# Privacy policy draft for the online release

**Do not publish this yet.** It replaces `PRIVACY.md` (the live policy URL) in the same release
that ships the online features, and not before. `PRIVACY.md` §9 promises the policy will be
updated before such a change ships.

When it ships:

- Copy the text below over `PRIVACY.md`.
- Set the effective date.
- Update **Play Console → App content → Data safety** to match (see the end of this file).

---

# Privacy Policy - Critical Count

**Effective date:** _(release date)_
**Developer:** Cloudy Day Games
**Contact:** cloudydaygamesllc@gmail.com

This policy explains what information is handled when you play **Critical Count** (Android
package `com.cloudydaygames.criticalcount`), who handles it, and the choices you have.

## 1. The short version

- **Offline play never needs the internet.** Everything except the online features works fully
  offline.
- **Online is optional.** That means online matches, friends and the Endless leaderboard. If you
  use them, our game server keeps a small anonymous account for you: a random ID, the display
  name you choose, your friend list, and your best Endless streak.
- **No email, phone number, real name or password, ever.** We never ask for them.
- **We never store your IP address.** Other players never see your IP address or any other
  information about your device.
- **All online traffic is encrypted.**
- **You can delete your online account at any time**, from inside the game.
- **Ads:** the free version shows ads served by **Google AdMob**. Google collects device and
  usage information to show those ads, as described below.
- **Purchases:** if you buy **Remove Ads**, the payment is handled entirely by **Google Play**.

## 2. Information stored on your device

The game saves the following on your device:

- your progress
- your settings
- whether you own Remove Ads
- if you have used the online features, the random key that signs your device in to your
  anonymous online account

Uninstalling the game deletes this data from the device.

## 3. Online features

The online features are run on a game server operated by Cloudy Day Games. They are:

- online matches against other players
- friends
- the Endless leaderboard

### What the server stores

- A randomly generated account ID. Your device keeps a random secret key; the server keeps only
  a one-way hash of it.
- The display name you choose and a four-digit number added to it (for example, Alex#4821).
- A friend code that you can give to others.
- Your friends, friend requests you sent or received, and players you have blocked.
- Your best Endless streak and when you set it.
- Reports you make about another player's name, or that others make about yours (which player,
  and a reason chosen from a list).

### What other players can see

- Your display name.
- Whether you are online or in a match. Only your accepted friends see this.
- Your best Endless streak, if it is on the leaderboard.
- During a match, the cards you play.

Other players never see your IP address, your device or any other information about you.

### What the server does not store

- IP addresses
- device identifiers
- email addresses
- real names
- locations
- match histories

To limit abuse (for example, mass account creation), the server counts recent requests per IP
address in memory for a few minutes, then forgets them. These counts are never written to disk
or logged.

### Encryption and the network

All online traffic is encrypted with TLS. It travels through **Cloudflare**, which protects the
server and hides its address. Cloudflare processes your IP address to deliver the traffic, under
its own privacy policy: https://www.cloudflare.com/privacypolicy/

### Names and moderation

- Display names are checked automatically against a list of offensive words. There is no chat.
- You can block any player, and report a player's name.
- A name reported by several different players is reset automatically.

### Deleting your account

**Options → Online → Delete Online Account** permanently deletes your account, name, friends,
blocks, reports and leaderboard entry from the server, immediately. Your offline progress on the
device is not affected.

If you can no longer open the game (for example, after uninstalling it), the account cannot be
linked to you and is never associated with your identity. You can still write to us and we will
help where we can.

## 4. Advertising (Google AdMob)

The free version of Critical Count shows ads through Google AdMob, a service of Google LLC. Ads
are only requested when your device is online; without a connection the game simply plays
without them.

When an ad is requested or shown, Google may collect and process:

- your device's advertising ID (the Android Advertising ID)
- your IP address, and an approximate location derived from it
- device information such as model, operating system version and language
- information about ad interactions, such as ads shown and tapped
- diagnostic information, such as crash and performance data from the ad software

Google uses this information to serve and measure ads, limit how often you see the same ad, and
detect fraud and abuse. Depending on your choices and where you live, ads may be personalized or
non-personalized. We limit ads in Critical Count to content rated PG and block sensitive ad
categories such as gambling.

- How Google uses this information: https://policies.google.com/technologies/partner-sites
- Google Privacy Policy: https://policies.google.com/privacy

## 5. Your choices

- **Consent (EEA, UK and Switzerland):** before any ad is requested, Google's consent form asks
  whether you agree to personalized advertising. You may decline.
- **US state privacy rights:** where applicable, you can opt out of the sale or sharing of your
  personal information for targeted advertising.
- **Changing your mind:** open **Options → Privacy Choices** at any time.
- **Advertising ID:** you can reset or delete it in your device settings.
- **No ads at all:** buy Remove Ads.
- **No online account at all:** don't use the online features; the rest of the game works
  without one.

## 6. In-app purchases (Google Play)

Remove Ads is sold through Google Play Billing. Google processes the payment; we never see your
payment details, name or email address.

## 7. Children

Critical Count is not directed at children under 13, and we do not knowingly collect personal
information from children under 13. If you believe a child under 13 has created an online
account, contact us and we will delete it.

## 8. Security and retention

- Online account data is kept until you delete the account.
- Name reports are cleared when the name they were about is reset.
- Data on your device stays there until you uninstall the game.
- Information collected by Google or Cloudflare is kept according to their own policies.

## 9. Your rights

Depending on where you live (for example under the GDPR or US state privacy laws), you may have
the right to:

- access, correct or delete your personal information
- restrict how it is used

You can delete your online account yourself in the game, or contact us.

## 10. Changes to this policy

We will update this policy, and the effective date above, before any change that affects your
privacy ships.

## 11. Contact

**cloudydaygamesllc@gmail.com**

---

## Data safety form changes (Play Console), when this ships

### Data collected

- **App activity → Other user-generated content.** This is the display name: collected, shared
  with other users (that is the point of it), not optional for online play, purpose App
  functionality.
- **Personal info → User IDs.** This is the random account ID: collected, not shared, purpose
  App functionality and Account management.
- **App activity → App interactions.** This covers friends and leaderboard: collected, purpose
  App functionality.

### Security answers

- **Data encrypted in transit:** Yes (unchanged).
- **Can users request deletion:** now **Yes**, from inside the app.
- **Account creation:** the app creates an anonymous account. Google's account-deletion
  requirement is met in-app (Options → Online → Delete Online Account).
  - Play also asks for a web link for deletion requests. Point it at a short page on the
    developer site that explains in-app deletion, and offers email for anyone who has
    uninstalled.
