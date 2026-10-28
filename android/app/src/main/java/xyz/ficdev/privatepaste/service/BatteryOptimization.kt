package xyz.ficdev.privatepaste.service

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.os.PowerManager
import android.provider.Settings
import androidx.core.net.toUri

/** The battery-optimization exemption the app asks for after the first sign-in (a persistent socket needs it). */
object BatteryOptimization {

    fun isExempt(context: Context): Boolean =
        context.getSystemService(PowerManager::class.java)?.isIgnoringBatteryOptimizations(context.packageName) == true

    /**
     * The system dialog asking to exempt this package. Sideloaded (no Play policy), and the app's whole job is a
     * long-lived connection the owner wants, which is what this intent is for.
     */
    @SuppressLint("BatteryLife")
    fun requestIntent(context: Context): Intent =
        Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, "package:${context.packageName}".toUri())
}
