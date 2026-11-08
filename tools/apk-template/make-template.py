#!/usr/bin/env python3
"""
Gera no PC o APK-modelo do runner Android do GameMaker (docs/apk-export.md).

No celular nao ha Gradle/aapt2/javac/d8. O que muda de um jogo para outro e so: pacote,
nome, versao, permissoes/meta-data do manifesto, icones e os assets (game.droid...). O codigo
Java do runner (classes*.dex) e o resources.arsc sao iguais para todos os jogos, desde que o
pacote Java seja fixo. Entao compilamos UMA vez, aqui, o projeto Gradle do runtime com o pacote
Java fixo io.gmide.yyrunner, e o empacotador do app (host/ApkBuilder.cs) monta o APK final a
partir deste modelo + o projeto Gradle expandido pelo Igor no aparelho.

Saida (tools/apk-template/out/):
  gmide-runner-template.apk   APK release sem assinatura util (o app reassina)
  template.json               runtime, pacote fixo, ids de recursos e o que as bibliotecas
                              androidx acrescentam ao manifesto (merged - proprio)

Uso: python tools/apk-template/make-template.py [--runtime DIR] [--skip-gradle]
"""
import argparse, json, os, re, shutil, subprocess, sys, xml.dom.minidom, zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_RT = Path(r"C:\ProgramData\GameMakerStudio2-LTS2026\Cache\runtimes\runtime-2026.0.0.23")
SDK = Path(os.environ.get("LOCALAPPDATA", "")) / "Android" / "Sdk"
JDK = Path(r"C:\Program Files\Eclipse Adoptium\jdk-21.0.12.8-hotspot")

PKG_DOMAIN, PKG_COMPANY, PKG_PRODUCT = "io", "gmide", "yyrunner"
PKG = f"{PKG_DOMAIN}.{PKG_COMPANY}.{PKG_PRODUCT}"
APP_NAME = "GMIDE_TEMPLATE_APP_NAME"   # trocado no arsc pelo nome do jogo
COMPILE_SDK = "35"
PLATFORM_NAME = "15"                    # versao do Android do compileSdk 35
TARGET_SDK = "35"
MIN_SDK = "24"                          # so assinatura v2 (sem v1/JAR)
AGP = "8.7.0"
GRADLE = "8.9"

# Manifesto do modelo: o mesmo que o Igor geraria com as opcoes padrao. No aparelho o
# manifesto e recompilado a partir do AndroidManifest.xml que o Igor expande para o projeto.
META = {
    "UseShaders": "True", "YYUse24Bit": "False", "OrientLandscape": "-1", "OrientPortrait": "0",
    "OrientLandscapeFlipped": "-1", "OrientPortraitFlipped": "0", "SplashscreenTime": "0",
    "SleepMargin": "4", "IsBuiltAsYoYoRunner": "Yes",
}


def env():
    meta = "".join(f'<meta-data android:name="{k}" android:value="{v}"/>\n' for k, v in META.items())
    perms = ('<uses-feature android:glEsVersion="0x00020000"  android:required="true" />\n'
             '<uses-permission android:name="android.permission.INTERNET"/>\n'
             '<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE"/>\n')
    deps = ("implementation 'androidx.activity:activity:1.9.3'\n"
            "implementation 'androidx.appcompat:appcompat:1.7.0'\n")
    e = {
        "applicationId": "${applicationId}",
        "YYAndroidPackageName": PKG, "YYAndroidPackageDomain": PKG_DOMAIN,
        "YYAndroidPackageCompany": PKG_COMPANY, "YYAndroidPackageProduct": PKG_PRODUCT,
        "YYAndroidDisplayName": APP_NAME,
        "YYAndroidMajorVersion": "1", "YYAndroidMinorVersion": "0", "YYAndroidBuildVersion": "0",
        "YYAndroidVersionCode": "1000000", "YYAndroidInstallLocation": "auto",
        "YYAndroidMinSDKVersion": MIN_SDK, "YYAndroidTargetSDKVersion": TARGET_SDK,
        "YYAndroidCompileSDKVersion": COMPILE_SDK,
        "YYAndroidGradleVersion": GRADLE, "YYAndroidGradlePluginVersion": AGP,
        "YYAndroidProGuardMinifyEnabled": "false", "YYAndroidProGuardShrinkResources": "false",
        "YYAndroidGradleRunnerDependencies": deps,
        "YYAndroidGradleMaxHeapSize": "org.gradle.jvmargs=-Xmx2560m",
        "YYAndroidGradleProperties": "org.gradle.java.home = " + str(JDK).replace("\\", "/"),
        "YYAndroidManifestPermission": perms, "YYAndroidManifestMetadata": meta,
        "YYAndroidManifestApplicationAttributes": 'android:allowBackup="false"',
        "YYAndroidIsGame": "", "YYAndroidTVBanner": "",
        "YYAndroidKeyStoreFile": str(HERE / "work" / "template.keystore").replace("\\", "/"),
        "YYAndroidKeyStoreFilePassword": "gmide-template", "YYAndroidKeyAlias": "template",
        "YYAndroidKeyPassword": "gmide-template",
        "YYAndroidSplits": "lintOptions {\ncheckReleaseBuilds false\nabortOnError false\n}\n",
        "YYDynamicAssetDeliveryPacks": "",
    }
    return e


def expand_text(s, e):
    return re.sub(r"\$\{[A-Za-z0-9_]+\}", lambda m: e.get(m.group(0)[2:-1], ""), s)


def is_binary(p):
    return p.suffix.lower() in {".png", ".jar", ".so", ".jpg", ".ogg", ".wav", ".zip", ".keystore"}


def copy_expand(src: Path, dst: Path, e):
    for f in src.rglob("*"):
        if not f.is_file():
            continue
        rel = [e.get(part, part) for part in f.relative_to(src).parts[:-1]] + [f.name]
        out = dst.joinpath(*rel)
        out.parent.mkdir(parents=True, exist_ok=True)
        if is_binary(f):
            shutil.copyfile(f, out)
        else:
            text = f.read_text(encoding="utf-8", errors="surrogateescape")
            out.write_text(expand_text(text, e), encoding="utf-8", errors="surrogateescape")


def run(cmd, cwd, extra_env=None):
    print("+", " ".join(map(str, cmd)), flush=True)
    en = dict(os.environ, **(extra_env or {}))
    subprocess.run(cmd, cwd=cwd, env=en, check=True)


def attrs(el):
    return {k: v for k, v in el.attributes.items()}


def element_key(el):
    a = attrs(el)
    return (el.tagName, a.get("android:name") or a.get("android:authorities") or a.get("android:glEsVersion") or "")


def lib_contributions(own_xml: Path, merged_xml: Path):
    """Elementos que o merger do Gradle acrescentou (bibliotecas), com ${applicationId}."""
    own = xml.dom.minidom.parse(str(own_xml))
    merged = xml.dom.minidom.parse(str(merged_xml))
    def children(doc, parent_tag):
        root = doc.documentElement if parent_tag == "manifest" else doc.getElementsByTagName(parent_tag)[0]
        return [c for c in root.childNodes if c.nodeType == c.ELEMENT_NODE]
    out = {}
    for parent in ("manifest", "application"):
        mine = {element_key(c) for c in children(own, parent)}
        extra = []
        for c in children(merged, parent):
            if c.tagName in ("application", "uses-sdk"):
                continue
            if element_key(c) in mine:
                continue
            extra.append(c.toxml().replace(PKG + ".", "${applicationId}.").replace('"' + PKG + '"', '"${applicationId}"'))
        out[parent] = extra
    app = merged.getElementsByTagName("application")[0]
    out["application_attributes"] = {k: v for k, v in attrs(app).items()
                                     if k in ("android:appComponentFactory", "android:extractNativeLibs")}
    # O aapt2 link (depois do merge) acrescenta estes na raiz; valores do compileSdk do modelo.
    out["manifest_attributes"] = {"android:compileSdkVersion": COMPILE_SDK, "android:compileSdkVersionCodename": PLATFORM_NAME,
                                  "platformBuildVersionCode": COMPILE_SDK, "platformBuildVersionName": PLATFORM_NAME}
    sdk = merged.getElementsByTagName("uses-sdk")[0]
    out["uses_sdk"] = attrs(sdk)
    return out


def resource_ids(apk: Path):
    aapt2 = sorted((SDK / "build-tools").glob("*/aapt2.exe"))[-1]
    out = subprocess.run([str(aapt2), "dump", "resources", str(apk)], capture_output=True, encoding="utf-8", errors="replace", check=True).stdout
    ids = {}
    files = {}  # "drawable-hdpi/icon.png" (pasta res do projeto Gradle) -> "res/8p.png" (no APK)
    cur_type = None
    cur_res = None
    for line in out.splitlines():
        m = re.match(r"\s*type (\S+) id=([0-9a-f]+)", line)
        if m:
            cur_type = m.group(1); continue
        m = re.match(r"\s*resource (0x[0-9a-f]+) (\S+)", line)
        if m and cur_type:
            cur_res = m.group(2) if "/" in m.group(2) else f"{cur_type}/{m.group(2)}"
            ids[cur_res] = int(m.group(1), 16)
            continue
        m = re.match(r"\s*\(([^)]*)\) \(file\) (\S+) type=PNG", line)
        if m and cur_res in ("drawable/icon", "drawable/adaptive_icon", "drawable/adaptive_bg"):
            t, n = cur_res.split("/")
            files[f"{t}-{m.group(1)}/{n}.png" if m.group(1) else f"{t}/{n}.png"] = m.group(2)
    return ids, files


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runtime", type=Path, default=DEFAULT_RT)
    ap.add_argument("--skip-gradle", action="store_true")
    a = ap.parse_args()
    runner = a.runtime / "android" / "runner"
    if not runner.is_dir():
        sys.exit(f"{runner} nao existe (modulo android do runtime nao instalado)")
    work = HERE / "work"
    out = HERE / "out"
    e = env()
    if not a.skip_gradle:
        if work.exists():
            shutil.rmtree(work)
        work.mkdir(parents=True)
        copy_expand(runner / "RootFiles", work, e)
        copy_expand(runner / "ProjectFiles", work / PKG, e)
        copy_expand(runner / "gradle", work / "gradle", e)
        # Icones em todas as densidades que o Igor preenche (AndroidUtils.DoIcons): o arsc do
        # modelo precisa ter essas entradas para o empacotador so trocar os bytes dos PNG.
        icons = a.runtime / "BaseProject" / "options" / "android" / "icons"
        res = work / PKG / "src" / "main" / "res"
        for d in ("ldpi", "mdpi", "hdpi", "xhdpi", "xxhdpi", "xxxhdpi"):
            (res / f"drawable-{d}").mkdir(parents=True, exist_ok=True)
            shutil.copyfile(icons / f"{d}.png", res / f"drawable-{d}" / "icon.png")
        (res / "drawable").mkdir(exist_ok=True)
        shutil.copyfile(icons / "xhdpi.png", res / "drawable" / "icon.png")
        # O pacote do APK final nao e o pacote Java do modelo: onde o runner usa o nome do pacote
        # como applicationId (nao como prefixo de classe), troca pelo pacote em tempo de execucao.
        java = work / PKG / "src" / "main" / "java"
        fixes = [(java / PKG.replace(".", "/") / "DemoRenderer.java",
                  f'packMgmr.getApplicationInfo("{PKG}", 0)', "packMgmr.getApplicationInfo(m_context.getPackageName(), 0)")]
        for f, old, new in fixes:
            t = f.read_text(encoding="utf-8")
            if old not in t:
                sys.exit(f"{f}: trecho esperado nao encontrado ({old}); revise make-template.py para este runtime")
            f.write_text(t.replace(old, new), encoding="utf-8")
        left = [str(f) for f in java.rglob("*.java") if f'"{PKG}"' in f.read_text(encoding="utf-8")]
        if left:
            sys.exit(f'pacote do modelo ainda usado como literal em: {left}')
        # So arm64-v8a e armeabi-v7a; o empacotador do app copia o libyoyo.so do runtime instalado.
        shutil.rmtree(work / PKG / "src" / "main" / "jniLibs" / "x86_64", ignore_errors=True)
        (work / "local.properties").write_text("sdk.dir=" + str(SDK).replace("\\", "/") + "\n")
        keystore = work / "template.keystore"
        run([str(JDK / "bin" / "keytool.exe"), "-genkeypair", "-keystore", str(keystore), "-storepass",
             "gmide-template", "-keypass", "gmide-template", "-alias", "template", "-keyalg", "RSA",
             "-keysize", "2048", "-validity", "10000", "-dname", "CN=gmide template"], work)
        gradlew = work / "gradle" / "gradlew.bat"
        run([str(gradlew), "-p", str(work), "assembleRelease", "-x", "lint", "--no-daemon", "--stacktrace"],
            work, {"JAVA_HOME": str(JDK), "ANDROID_HOME": str(SDK)})
    built = work / PKG / "build" / "outputs" / "apk" / "release"
    apks = sorted(built.glob("*.apk"))
    if not apks:
        sys.exit(f"nenhum APK em {built}")
    out.mkdir(exist_ok=True)
    apk = out / "gmide-runner-template.apk"
    shutil.copyfile(apks[0], apk)
    merged = next((work / PKG / "build" / "intermediates").glob("merged_manifest*/release/**/AndroidManifest.xml"))
    shutil.copyfile(merged, out / "merged-AndroidManifest.xml")
    with zipfile.ZipFile(apk) as z:
        names = z.namelist()
        dex = sorted(n for n in names if re.fullmatch(r"classes\d*\.dex", n))
    ids, res_files = resource_ids(apk)
    info = {
        "runtime": a.runtime.name,
        "runtime_version": json.loads((a.runtime / "receipt.json").read_text()).get("version") if (a.runtime / "receipt.json").exists() else None,
        "package": PKG,
        "app_name_placeholder": APP_NAME,
        "min_sdk": int(MIN_SDK), "target_sdk": int(TARGET_SDK), "compile_sdk": int(COMPILE_SDK),
        "dex": dex,
        "resource_ids": ids,
        "res_files": res_files,
        "manifest_additions": lib_contributions(work / PKG / "src" / "main" / "AndroidManifest.xml", merged),
        "files": names,
    }
    (out / "template.json").write_text(json.dumps(info, indent=1), encoding="utf-8")
    # Copia sem lib/ para embutir no app (o libyoyo.so vem do runtime instalado no aparelho).
    slim = out / "gmide-runner-template-slim.apk"
    with zipfile.ZipFile(apk) as zi, zipfile.ZipFile(slim, "w") as zo:
        for it in zi.infolist():
            if not it.filename.startswith("lib/"):
                zo.writestr(it, zi.read(it.filename), compress_type=it.compress_type)
    # Modulo android do runtime para o app instalar no runtime do aparelho (RuntimeImport.EnsureAndroidModule):
    # o runtime importado no celular pode ser anterior ao modulo. Leva android/ e o "android" do receipt.json.
    mod = out / "runtime-android.zip"
    receipt = json.loads((a.runtime / "receipt.json").read_text(encoding="utf-8"))
    with zipfile.ZipFile(mod, "w", zipfile.ZIP_DEFLATED) as zo:
        # BaseProject/options/android (splash, icones e tv_banner padrao) tambem vem com o modulo;
        # o Igor copia esses arquivos no DoSplash/DoIcon. yyc/android fica de fora (YYC nao roda no aparelho).
        for sub in ("android", "BaseProject/options/android"):
            for f in sorted((a.runtime / sub).rglob("*")):
                if f.is_file():
                    zo.write(f, f.relative_to(a.runtime).as_posix())
        zo.writestr("receipt-android.json", json.dumps({"android": receipt["android"]}, indent=2))
    print(f"ok: {apk} ({apk.stat().st_size} bytes), {len(info['resource_ids'])} recursos, dex {dex}; modulo {mod.stat().st_size} bytes")


if __name__ == "__main__":
    main()
