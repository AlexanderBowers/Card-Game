package com.cloudydaygames.store;

import android.app.Activity;
import android.content.Intent;

import androidx.annotation.NonNull;

import com.google.android.play.core.review.ReviewInfo;
import com.google.android.play.core.review.ReviewManager;
import com.google.android.play.core.review.ReviewManagerFactory;

import org.godotengine.godot.Godot;
import org.godotengine.godot.plugin.GodotPlugin;
import org.godotengine.godot.plugin.UsedByGodot;

/**
 * Critical Count's store helpers (2026-11-08, from the closed-test report: rate and share from
 * inside the game).
 *
 * share(subject, text) opens Android's share sheet with a plain-text message.
 * requestReview() asks Google Play for its in-app review card. Play decides whether the card
 * actually shows (it has its own quota), and never says whether the player rated, so the game
 * treats every call as "asked" and moves on.
 */
public class CloudyDayStore extends GodotPlugin {

    public CloudyDayStore(Godot godot) {
        super(godot);
    }

    @NonNull
    @Override
    public String getPluginName() {
        return "CloudyDayStore";
    }

    @UsedByGodot
    public void share(String subject, String text) {
        final Activity activity = getActivity();
        if (activity == null) return;
        activity.runOnUiThread(() -> {
            Intent send = new Intent(Intent.ACTION_SEND);
            send.setType("text/plain");
            if (subject != null && !subject.isEmpty()) send.putExtra(Intent.EXTRA_SUBJECT, subject);
            send.putExtra(Intent.EXTRA_TEXT, text);
            activity.startActivity(Intent.createChooser(send, null));
        });
    }

    @UsedByGodot
    public void requestReview() {
        final Activity activity = getActivity();
        if (activity == null) return;
        activity.runOnUiThread(() -> {
            final ReviewManager manager = ReviewManagerFactory.create(activity);
            manager.requestReviewFlow().addOnCompleteListener(request -> {
                if (!request.isSuccessful()) return;
                ReviewInfo info = request.getResult();
                manager.launchReviewFlow(activity, info);
            });
        });
    }
}
