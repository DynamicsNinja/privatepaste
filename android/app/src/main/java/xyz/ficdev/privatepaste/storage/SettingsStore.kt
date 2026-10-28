package xyz.ficdev.privatepaste.storage

import android.content.Context
import androidx.core.content.edit

/** How a clip from another device reaches this phone's clipboard. */
enum class DeliveryMode {
    /** The service calls `setPrimaryClip` directly. */
    AUTOMATIC,

    /** A heads-up notification per clip with a Copy action. */
    NOTIFICATION,
}

/** Non-secret preferences, in plain SharedPreferences "settings". */
class SettingsStore(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    /** Defaults to [DeliveryMode.AUTOMATIC], also for an unknown stored value. */
    var deliveryMode: DeliveryMode
        get() = prefs.getString(KEY_DELIVERY_MODE, null)
            ?.let { stored -> DeliveryMode.entries.firstOrNull { it.name == stored } }
            ?: DeliveryMode.AUTOMATIC
        set(value) = prefs.edit { putString(KEY_DELIVERY_MODE, value.name) }

    var onboardingDone: Boolean
        get() = prefs.getBoolean(KEY_ONBOARDING_DONE, false)
        set(value) = prefs.edit { putBoolean(KEY_ONBOARDING_DONE, value) }

    private companion object {
        const val NAME = "settings"
        const val KEY_DELIVERY_MODE = "deliveryMode"
        const val KEY_ONBOARDING_DONE = "onboardingDone"
    }
}
