"""Build the Android runner library used by local F5, from the runtime's Java sources."""
from pathlib import Path
import os, shutil, subprocess

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'tools/apk-template/work'
WORK = Path(__file__).resolve().parent / 'work'
OUT = ROOT / 'native/out-runner'
module = WORK / 'runner'
module.mkdir(parents=True, exist_ok=True)
OUT.mkdir(parents=True, exist_ok=True)
for name in ('java', 'res'):
    shutil.copytree(SOURCE / 'io.gmide.yyrunner/src/main' / name, module / 'src/main' / name, dirs_exist_ok=True)
shutil.copytree(SOURCE / 'io.gmide.yyrunner/libs', module / 'libs', dirs_exist_ok=True)
for name in ('gradlew.bat', 'gradlew', 'build.gradle', 'gradle.properties', 'local.properties'):
    if (SOURCE / name).exists(): shutil.copy2(SOURCE / name, WORK / name)
shutil.copytree(SOURCE / 'gradle', WORK / 'gradle', dirs_exist_ok=True)
(WORK / 'settings.gradle').write_text("include ':runner'\n")

java = module / 'src/main/java/io/gmide/yyrunner'
def replace(file, old, new):
    path = java / file
    text = path.read_text()
    if text.count(old) != 1: raise RuntimeError(f'{file}: expected one anchor for {old[:60]}')
    path.write_text(text.replace(old, new))

replace('RunnerActivity.java', 'super.onCreate(savedInstanceState);', '''super.onCreate(savedInstanceState);
        String previewZip = getIntent().getStringExtra("gmide.game_zip");
        if (previewZip != null) {
            if (Build.VERSION.SDK_INT < 30) throw new IllegalStateException("Local preview requires Android 11 or later");
            try (android.os.ParcelFileDescriptor fd = android.os.ParcelFileDescriptor.open(new java.io.File(previewZip), android.os.ParcelFileDescriptor.MODE_READ_ONLY)) {
                android.content.res.loader.ResourcesLoader loader = new android.content.res.loader.ResourcesLoader();
                loader.addProvider(android.content.res.loader.ResourcesProvider.loadFromApk(fd));
                getResources().addLoaders(loader);
            } catch (Exception e) { throw new IllegalStateException("Unable to attach preview assets", e); }
        }
        if (getIntent().getBooleanExtra("gmide.debug", false)) {
            gmidePipReceiver = new android.content.BroadcastReceiver() {
                public void onReceive(android.content.Context context, android.content.Intent intent) {
                    if (intent.getAction().endsWith(".DEBUG_STOP")) {
                        gmideShowEditor("stop");
                        finishAndRemoveTask();
                    } else gmideEnterDebugPip();
                }
            };
            android.content.IntentFilter filter = new android.content.IntentFilter(getPackageName() + ".DEBUG_PIP");
            filter.addAction(getPackageName() + ".DEBUG_STOP");
            if (Build.VERSION.SDK_INT >= 33) registerReceiver(gmidePipReceiver, filter, android.content.Context.RECEIVER_NOT_EXPORTED);
            else registerReceiver(gmidePipReceiver, filter);
        }''')

replace('DemoRenderer.java', 'm_apkFilePath = appInfo.sourceDir;', '''m_apkFilePath = appInfo.sourceDir;
        android.content.Intent local = RunnerActivity.CurrentActivity.getIntent();
        if (local.hasExtra("gmide.game_zip")) {
            m_apkFilePath = local.getStringExtra("gmide.game_zip");
            m_saveFilesDir = local.getStringExtra("gmide.save_dir") + "/";
            m_packageName = local.getStringExtra("gmide.project_id");
        }''')
replace('DemoRenderer.java', 'android.content.res.AssetManager assetManager = m_context.getResources().getAssets();', '''String localZip = RunnerActivity.CurrentActivity.getIntent().getStringExtra("gmide.game_zip");
            if (localZip != null) {
                final java.util.zip.ZipFile zip = new java.util.zip.ZipFile(localZip);
                java.util.zip.ZipEntry entry = zip.getEntry("assets/" + path);
                if (entry == null) { zip.close(); return null; }
                return new java.io.FilterInputStream(zip.getInputStream(entry)) {
                    public void close() throws IOException { try { super.close(); } finally { zip.close(); } }
                };
            }
            android.content.res.AssetManager assetManager = m_context.getResources().getAssets();''')
replace('RunnerActivity.java', 'mYYPrefs = new IniBundle( bundle, null , "Android");', '''mYYPrefs = new IniBundle(bundle == null ? new Bundle() : bundle, null, "Android");
        String localZip = getIntent().getStringExtra("gmide.game_zip");
        if (localZip != null) {
            try (java.util.zip.ZipFile zip = new java.util.zip.ZipFile(localZip)) {
                java.util.zip.ZipEntry entry = zip.getEntry("assets/options.ini");
                if (entry == null) throw new java.io.IOException("Preview missing assets/options.ini");
                try (InputStream ini = zip.getInputStream(entry)) {
                    mYYPrefs = new IniBundle(bundle == null ? new Bundle() : bundle, ini, "Android");
                }
            } catch (Exception e) { throw new IllegalStateException("Unable to load preview options", e); }
        }''')
# An external compiled game is a packaged game, not the download/debug runner.
replace('RunnerActivity.java', 'private boolean checkIsYoYoRunner() {', '''private boolean checkIsYoYoRunner() {
        if (getIntent().hasExtra("gmide.game_zip")) return false;''')
replace('DemoRenderer.java', 'm_state = eState.Process;', '''m_state = eState.Process;
                Log.i("yoyo", "[GMIDE] Native preview ready");''')
replace('RunnerActivity.java', 'setContentView( R.layout.main );', '''setContentView( R.layout.main );
        if (getIntent().hasExtra("gmide.game_zip")) {
            try {
                Class.forName("io.gmide.ShortcutBar").getMethod("installPreview", android.app.Activity.class).invoke(null, this);
            } catch (Exception e) { throw new IllegalStateException("Unable to attach preview controls", e); }
        }''')
replace('RunnerActivity.java', 'super.onPause();', '''super.onPause();
        if (getIntent().getBooleanExtra("gmide.debug", false) && isInPictureInPictureMode()) {
            Log.i("yoyo", "[GMIDE] Debug preview remains active in PiP");
            return;
        }''')
replace('RunnerActivity.java', 'super.onDestroy();', '''if (gmidePipReceiver != null) unregisterReceiver(gmidePipReceiver);
        super.onDestroy();''')
replace('RunnerActivity.java', 'super.onStop();', '''super.onStop();
        // A dismissed debug window must also release its build job.
        if (getIntent().getBooleanExtra("gmide.debug", false) && !isInPictureInPictureMode() && !isChangingConfigurations())
            finishAndRemoveTask();''')
replace('RunnerActivity.java', 'public void setupView() {', '''private android.content.BroadcastReceiver gmidePipReceiver;

    private void gmideShowEditor(String action) {
        android.content.Intent editor = new android.content.Intent();
        editor.setClassName(getPackageName(), "io.gmide.GmIdeActivity");
        editor.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK | android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP);
        if (action != null) editor.putExtra("gmide.preview_action", action);
        startActivity(editor);
    }

    public void gmideEnterDebugPip() {
        if (!getIntent().getBooleanExtra("gmide.debug", false)) return;
        try {
            if (!getPackageManager().hasSystemFeature(android.content.pm.PackageManager.FEATURE_PICTURE_IN_PICTURE))
                throw new IllegalStateException("Picture-in-Picture nao suportado neste aparelho");
            int w = Math.max(1, mGLView.getWidth()), h = Math.max(1, mGLView.getHeight());
            android.util.Rational ratio = new android.util.Rational(w, h);
            if (ratio.floatValue() > 2.39f) ratio = new android.util.Rational(16, 9);
            if (ratio.floatValue() < 1f / 2.39f) ratio = new android.util.Rational(9, 16);
            android.content.Intent stop = new android.content.Intent(getPackageName() + ".DEBUG_STOP").setPackage(getPackageName());
            android.app.PendingIntent pending = android.app.PendingIntent.getBroadcast(this, 0, stop,
                android.app.PendingIntent.FLAG_IMMUTABLE | android.app.PendingIntent.FLAG_UPDATE_CURRENT);
            android.app.RemoteAction action = new android.app.RemoteAction(
                android.graphics.drawable.Icon.createWithResource(this, android.R.drawable.ic_menu_close_clear_cancel),
                "Parar", "Parar o preview e voltar a IDE", pending);
            android.app.PictureInPictureParams params = new android.app.PictureInPictureParams.Builder()
                .setAspectRatio(ratio).setActions(java.util.Collections.singletonList(action)).build();
            if (!enterPictureInPictureMode(params)) throw new IllegalStateException("Habilite Picture-in-Picture para esta IDE nas configuracoes do Android");
            Log.i("yoyo", "[GMIDE] Debug preview entered PiP");
            gmideShowEditor(null);
        } catch (Exception e) {
            Log.e("yoyo", "[GMIDE] Debug PiP failed", e);
            android.widget.Toast.makeText(this, e.getMessage(), android.widget.Toast.LENGTH_LONG).show();
            gmideShowEditor("stop");
            finishAndRemoveTask();
        }
    }

    @Override public void onPictureInPictureModeChanged(boolean pip, android.content.res.Configuration config) {
        super.onPictureInPictureModeChanged(pip, config);
        try { Class.forName("io.gmide.ShortcutBar").getMethod("setPreviewPip", boolean.class).invoke(null, pip); }
        catch (Exception e) { Log.e("yoyo", "Unable to update preview controls", e); }
    }

    public void setupView() {''')
# GLSurfaceView's UI-thread resize waits for a rendered frame. A debugger breakpoint
# deliberately prevents that frame, so dispatch resize on its own GL thread instead.
# Android explicitly handles that reentrant path without waiting; coalesce resize events.
replace('DemoGLSurfaceView.java', 'public DemoRenderer mRenderer;', '''public DemoRenderer mRenderer;
    private final Object gmideResizeLock = new Object();
    private boolean gmideResizeQueued;
    private int gmideFormat, gmideWidth, gmideHeight;
    @Override public void surfaceChanged(final android.view.SurfaceHolder holder, int format, int width, int height) {
        boolean debug = RunnerActivity.CurrentActivity != null &&
            RunnerActivity.CurrentActivity.getIntent().getBooleanExtra("gmide.debug", false);
        if (!debug) { super.surfaceChanged(holder, format, width, height); return; }
        synchronized (gmideResizeLock) {
            gmideFormat = format; gmideWidth = width; gmideHeight = height;
            if (gmideResizeQueued) return;
            gmideResizeQueued = true;
        }
        queueEvent(new Runnable() { public void run() {
            int f, w, h;
            synchronized (gmideResizeLock) {
                f = gmideFormat; w = gmideWidth; h = gmideHeight; gmideResizeQueued = false;
            }
            DemoGLSurfaceView.super.surfaceChanged(holder, f, w, h);
        } });
        requestRender();
    }''')

(module / 'src/main/AndroidManifest.xml').write_text('''<manifest xmlns:android="http://schemas.android.com/apk/res/android">
<uses-permission android:name="android.permission.INTERNET"/>
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE"/>
<application>
<activity android:name="io.gmide.yyrunner.RunnerActivity" android:process=":game" android:exported="false"
 android:taskAffinity="io.gmide.android.game" android:excludeFromRecents="true" android:supportsPictureInPicture="true" android:resizeableActivity="true"
 android:theme="@style/Theme.AppCompat.NoActionBar" android:configChanges="orientation|keyboard|keyboardHidden|screenSize|screenLayout|smallestScreenSize|uiMode|density|fontScale|layoutDirection|locale|mcc|mnc|navigation|touchscreen"/>
<meta-data android:name="IsBuiltAsYoYoRunner" android:value="Yes"/>
</application></manifest>''')
(module / 'build.gradle').write_text('''apply plugin: 'com.android.library'
android {
 namespace 'io.gmide.yyrunner'
 compileSdk 35
 defaultConfig { minSdk 26; targetSdk 35 }
 buildTypes { release { minifyEnabled false } }
 lint { abortOnError false }
}
dependencies {
 implementation 'androidx.legacy:legacy-support-v4:1.0.0'
 implementation 'androidx.multidex:multidex:2.0.1'
 implementation files('libs/ini4j-0.5.2.jar')
 implementation 'androidx.activity:activity:1.9.3'
 implementation 'androidx.appcompat:appcompat:1.7.0'
 constraints {
  implementation('org.jetbrains.kotlin:kotlin-stdlib-jdk7:1.8.0')
  implementation('org.jetbrains.kotlin:kotlin-stdlib-jdk8:1.8.0')
 }
}
tasks.register('exportRunner', Copy) {
 dependsOn 'assembleRelease'
 from(layout.buildDirectory.file('outputs/aar/runner-release.aar')) { rename { 'gmide-runner.aar' } }
 from({ configurations.releaseRuntimeClasspath.resolvedConfiguration.resolvedArtifacts.collect { it.file } })
 into(project.findProperty('runnerOutput'))
}
''')
env = os.environ.copy()
env['JAVA_HOME'] = r'C:\Program Files\Eclipse Adoptium\jdk-21.0.12.8-hotspot'
subprocess.run([str(WORK/'gradle/gradlew.bat'), '-p', str(WORK), '--no-daemon', '--max-workers=2',
                ':runner:exportRunner', '-PrunnerOutput='+str(OUT/'libs')], cwd=WORK, env=env, check=True)
# Local file dependencies are already packaged inside the AAR.
ini = OUT / 'libs/ini4j-0.5.2.jar'
if ini.exists(): ini.unlink()
native = OUT / 'arm64-v8a'
native.mkdir(exist_ok=True)
shutil.copy2(SOURCE/'io.gmide.yyrunner/src/main/jniLibs/arm64-v8a/libyoyo.so', native/'libyoyo.so')
print('Runner library and resolved dependencies:', OUT)
