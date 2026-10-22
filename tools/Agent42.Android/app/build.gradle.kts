plugins {
  alias(libs.plugins.android.application)
  alias(libs.plugins.compose.compiler)
  alias(libs.plugins.kotlin.serialization)
}

// Release identity, passed by .github/workflows/android.yml from the android-v* tag. Android refuses an update whose
// versionCode does not increase, so the workflow derives it from the SemVer core; a local build is code 1.
val releaseVersionName = providers.gradleProperty("agent42.versionName").orElse("0.0.0-dev")
val releaseVersionCode = providers.gradleProperty("agent42.versionCode").map(String::toInt).orElse(1)

// Release signing, from the keystore the workflow decodes out of the ANDROID_* secrets. Without it a release build
// is unsigned (and the workflow fails before that). The key must never change, or installed apps can't update.
val keystorePath = providers.environmentVariable("ANDROID_KEYSTORE_PATH")

android {
    namespace = "nl.agent42.android"
    compileSdk = 37
    defaultConfig {
        applicationId = "nl.agent42.android"
        minSdk = 34
        targetSdk = 37
        versionCode = releaseVersionCode.get()
        versionName = releaseVersionName.get()
    }

    signingConfigs {
        if (keystorePath.isPresent) {
            create("release") {
                storeFile = file(keystorePath.get())
                storePassword = providers.environmentVariable("ANDROID_KEYSTORE_PASSWORD").get()
                keyAlias = providers.environmentVariable("ANDROID_KEY_ALIAS").get()
                keyPassword = providers.environmentVariable("ANDROID_KEY_PASSWORD").get()
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("release")
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
      compose = true
      aidl = false
      // BuildConfig.DEBUG gates conversation text out of the release build's voice trace (ADR 0114).
      buildConfig = true
      shaders = false
    }

    packaging {
      resources {
        excludes += "/META-INF/{AL2.0,LGPL2.1}"
      }
    }
}

kotlin {
    jvmToolchain(21)
}

// The NDJSON golden file shared with the server's AndroidStreamContractTests.
tasks.withType<Test>().configureEach {
    systemProperty("agent42.contract", rootProject.file("contract/stream-events.ndjson").absolutePath)
}

dependencies {
  val composeBom = platform(libs.androidx.compose.bom)
  implementation(composeBom)
  androidTestImplementation(composeBom)

  // Core Android dependencies
  implementation(libs.androidx.core.ktx)
  implementation(libs.androidx.lifecycle.runtime.ktx)
  implementation(libs.androidx.activity.compose)

  // Arch Components
  implementation(libs.androidx.lifecycle.runtime.compose)
  implementation(libs.androidx.lifecycle.viewmodel.compose)

  // Compose
  implementation(libs.androidx.compose.ui)
  implementation(libs.androidx.compose.ui.tooling.preview)
  implementation(libs.androidx.compose.material3)
  // Tooling
  debugImplementation(libs.androidx.compose.ui.tooling)
  // Instrumented tests
  androidTestImplementation(libs.androidx.compose.ui.test.junit4)
  debugImplementation(libs.androidx.compose.ui.test.manifest)

  // Agent42 API: HTTP + the NDJSON chat stream
  implementation(libs.okhttp)
  implementation(libs.kotlinx.serialization.json)

  // Local tests: jUnit, coroutines, Android runner
  testImplementation(libs.junit)
  testImplementation(libs.kotlinx.coroutines.test)
  testImplementation(libs.okhttp.mockwebserver)

  // Instrumented tests: jUnit rules and runners
  androidTestImplementation(libs.androidx.test.core)
  androidTestImplementation(libs.androidx.test.ext.junit)
  androidTestImplementation(libs.androidx.test.runner)
  androidTestImplementation(libs.androidx.test.espresso.core)

  // Reply rendering: plain markdown natively; mermaid/math/artifacts stay in the web app (ADR 0114)
  implementation(libs.markdown.renderer.m3)

  // Voice call as a self-managed Telecom call: system call UI, lock screen, car/headset hang-up (ADR 0114)
  implementation(libs.androidx.core.telecom)
}
