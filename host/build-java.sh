#!/usr/bin/env bash
# Compila as classes Java (SDL + GmIdeActivity) num jar que o projeto .NET
# inclui como AndroidJavaLibrary (sem gerar bindings C#).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
JDK="${JDK:-/c/Program Files/Eclipse Adoptium/jdk-21.0.12.8-hotspot}"
ANDROID_JAR="$LOCALAPPDATA/Android/Sdk/platforms/android-34/android.jar"
OUT="$HERE/../native/out/java"
rm -rf "$OUT" && mkdir -p "$OUT/classes"
# javac do Windows precisa de caminhos C:\..., nao /c/...
# javac interpreta barras invertidas como escapes dentro do @argfile.
# Caminhos com / e aspas tambem preservam diretorios com espacos.
find "$HERE/Java" -name "*.java" | while IFS= read -r f; do
  printf '"%s"\n' "$(cygpath -m "$f")"
done > "$OUT/sources.txt"
"$JDK/bin/javac" -nowarn -source 8 -target 8 -Xlint:-options \
  -bootclasspath "$(cygpath -w "$ANDROID_JAR")" \
  -d "$(cygpath -w "$OUT/classes")" @"$(cygpath -w "$OUT/sources.txt")"
"$JDK/bin/jar" cf "$(cygpath -w "$OUT/gmide-java.jar")" -C "$(cygpath -w "$OUT/classes")" .
echo "jar: $OUT/gmide-java.jar"
