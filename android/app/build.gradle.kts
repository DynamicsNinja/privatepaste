plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "xyz.ficdev.privatepaste"
    compileSdk = 37

    defaultConfig {
        applicationId = "xyz.ficdev.privatepaste"
        minSdk = 29
        targetSdk = 36
        // CI stamps the tag: -PppVersion=0.1.0 -PppVersionCode=<run number>.
        versionCode = (findProperty("ppVersionCode") as String?)?.toIntOrNull() ?: 1
        versionName = (findProperty("ppVersion") as String?)?.takeIf { it.isNotBlank() } ?: "0.1.0"
    }

    // The release key comes from the environment, so it never lives in the repo. Without all four variables the
    // release APK is built unsigned (and says so); install it only after signing it yourself.
    val releaseSigningEnv = listOf(
        "PP_ANDROID_KEYSTORE", "PP_ANDROID_KEYSTORE_PASSWORD", "PP_ANDROID_KEY_ALIAS", "PP_ANDROID_KEY_PASSWORD",
    ).associateWith { System.getenv(it)?.takeIf { v -> v.isNotEmpty() } }
    val releaseSigningComplete = releaseSigningEnv.values.all { it != null }

    signingConfigs {
        if (releaseSigningComplete) {
            create("release") {
                storeFile = file(releaseSigningEnv.getValue("PP_ANDROID_KEYSTORE")!!)
                storePassword = releaseSigningEnv.getValue("PP_ANDROID_KEYSTORE_PASSWORD")
                keyAlias = releaseSigningEnv.getValue("PP_ANDROID_KEY_ALIAS")
                keyPassword = releaseSigningEnv.getValue("PP_ANDROID_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (releaseSigningComplete) {
                signingConfig = signingConfigs.getByName("release")
            } else {
                logger.warn(
                    "WARNING: PP_ANDROID_KEYSTORE, PP_ANDROID_KEYSTORE_PASSWORD, PP_ANDROID_KEY_ALIAS and " +
                        "PP_ANDROID_KEY_PASSWORD are not all set; the release APK will be UNSIGNED.",
                )
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
    }

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
        }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.okhttp)

    debugImplementation(libs.androidx.compose.ui.tooling)
    debugImplementation(libs.androidx.compose.ui.test.manifest)

    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.okhttp.mockwebserver)
    testImplementation(libs.robolectric)
    testImplementation(libs.androidx.test.core)
    testImplementation(platform(libs.androidx.compose.bom))
    testImplementation(libs.androidx.compose.ui.test.junit4)
}
