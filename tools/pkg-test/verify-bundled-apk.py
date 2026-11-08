"""Verify the actual stored APK asset and run the production extractor against it."""
from datetime import datetime
from pathlib import Path
import hashlib
import struct
import subprocess
import sys
import zipfile

root = Path(__file__).resolve().parents[2]
apk = Path(sys.argv[1]).resolve()
out = (Path(sys.argv[2]) if len(sys.argv) > 2 else
       root / "logs" / ("bundled-install-" + datetime.now().strftime("%Y%m%d-%H%M%S"))).resolve()
if out.exists():
    raise SystemExit("Choose a new output directory; existing files will not be deleted.")

with zipfile.ZipFile(apk) as archive:
    entry = archive.getinfo("assets/bootstrap/gmide-package.zip")
    if entry.compress_type != zipfile.ZIP_STORED:
        raise SystemExit("Bootstrap asset is compressed: Android OpenFd cannot open it.")
    with archive.open(entry) as asset, (root / "dist/gmide-package.zip").open("rb") as base:
        digest = hashlib.file_digest(asset, "sha256").hexdigest()
        if digest != hashlib.file_digest(base, "sha256").hexdigest():
            raise SystemExit("Bundled package does not match the build input.")
with apk.open("rb") as stream:
    stream.seek(entry.header_offset)
    header = stream.read(30)
    if header[:4] != b"PK\x03\x04":
        raise SystemExit("Invalid ZIP local header.")
    name_length, extra_length = struct.unpack_from("<HH", header, 26)
offset = entry.header_offset + 30 + name_length + extra_length
print(f"Stored package: {entry.file_size} bytes, offset={offset}, sha256={digest}", flush=True)
subprocess.run(["dotnet", "run", "--project", str(root / "tools/pkg-test/pkg-test.csproj"),
                "-c", "Release", "--", str(apk), str(out), str(offset), str(entry.file_size)],
               cwd=root, check=True)
