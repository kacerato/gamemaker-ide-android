# uso: python typecode.py arquivo.gml  -> digita linha a linha no editor de codigo (sem indentacao; a IDE indenta)
import subprocess, sys, os, time
adb = os.path.expandvars(r"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe")
def sh(cmd): subprocess.run([adb, "shell", cmd], check=True)
lines = open(sys.argv[1], encoding="utf-8").read().splitlines()
for n, line in enumerate(lines):
    s = line.strip()
    if s:
        t = s.replace("%", "\\%").replace(" ", "%s").replace("'", "'\''")
        sh(f"input text '{t}'")
    if n < len(lines) - 1:
        sh("input keyevent 111 66")  # ESC (fecha autocomplete) + ENTER
    time.sleep(0.15)
