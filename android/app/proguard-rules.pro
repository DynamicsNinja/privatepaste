# kotlinx.serialization: keep the generated serializers of @Serializable classes (the library's consumer rules cover
# most of this; these lines make it explicit for the app's own protocol and storage classes).
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt

-keep,includedescriptorclasses class xyz.ficdev.privatepaste.**$$serializer { *; }
-keepclassmembers class xyz.ficdev.privatepaste.** {
    *** Companion;
}
-keepclasseswithmembers class xyz.ficdev.privatepaste.** {
    kotlinx.serialization.KSerializer serializer(...);
}

# OkHttp: platform lookups are optional and absent on Android.
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
