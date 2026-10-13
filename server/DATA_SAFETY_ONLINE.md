# Data safety notes for the online release

The online privacy policy was published as `PRIVACY.md` on 2026-10-13 (online play and cloud save were
already in testers' hands through the master builds). The account deletion page is `ACCOUNT_DELETION.md`
in the repo root:

- Privacy policy URL (unchanged in Play and AdMob): https://github.com/AlexanderBowers/Card-Game/blob/master/PRIVACY.md
- Delete account URL (for Play Console → App content → Data safety): https://github.com/AlexanderBowers/Card-Game/blob/master/ACCOUNT_DELETION.md

What is left is the Play Console form, in the same submission as the next release:

### Data collected

- **App activity → Other user-generated content.** This is the display name: collected, shared
  with other users (that is the point of it), not optional for online play, purpose App
  functionality.
- **Personal info → User IDs.** This is the random account ID: collected, not shared, purpose
  App functionality and Account management.
- **App activity → App interactions.** This covers friends and leaderboard: collected, purpose
  App functionality.
- **Cloud save, decide before submitting.** The save goes to the player's own Play Games account
  through Google's Saved Games API, and we never receive it. Play counts data an app sends off
  the device, including to Google services, so the cautious answer is to declare it:
  **App activity → Other actions** (game progress), optional, purpose App functionality, not
  shared. Re-check Google's current Data safety help for Play Games Services before choosing.
- **Play log: not collected.** It never leaves the device unless the player copies and pastes it
  themselves.

### Security answers

- **Data encrypted in transit:** Yes (unchanged).
- **Can users request deletion:** now **Yes**, from inside the app.
- **Account creation:** the app creates an anonymous account. Google's account-deletion
  requirement is met in-app (Options → Online → Delete Online Account).
- **Delete account URL:** https://github.com/AlexanderBowers/Card-Game/blob/master/ACCOUNT_DELETION.md
