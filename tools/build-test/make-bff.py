# Gera build.bff + macros.json para um build de teste isolado (copia do projeto).
import json, os, sys
here = os.path.abspath(os.path.dirname(__file__))
target = sys.argv[1] if len(sys.argv) > 1 else "windows"   # windows | operagx
rt = r"C:\ProgramData\GameMakerStudio2-LTS2026\Cache\runtimes\runtime-2026.0.0.23"
proj_dir = os.path.join(here, "project", "Idle Game Template")
yyp = os.path.join(proj_dir, "Idle Game Template.yyp")
cache_root = os.path.join(here, "cache")
cache_name = "Idle_Game__687E6499"
cache = os.path.join(cache_root, cache_name)
temp = os.path.join(here, "temp")
out = os.path.join(here, os.environ.get("OUTROOT", "out"), target)
os.makedirs(out, exist_ok=True); os.makedirs(temp, exist_ok=True)
m = json.load(open(os.path.join(cache, "macros.json"), encoding="utf-8-sig"))
m.update({"project_full_filename": yyp, "project_dir": proj_dir, "options_dir": os.path.join(proj_dir, "options"),
          "temp_directory": temp, "asset_compiler_cache_directory": cache_root, "runtimeLocation": rt})
json.dump(m, open(os.path.join(cache, "macros.json"), "w", encoding="utf-8"), indent=4)
mask = {"windows": "64", "operagx": "17179869184"}[target]
ext = {"windows": ".win", "operagx": ".win"}[target]
env = {
 "targetFile": "", "assetCompiler": "", "debug": "False", "rerun": "False",
 "compile_output_file_name": os.path.join(out, "Idle Game Template" + ext),
 "useShaders": "True", "steamOptions": os.path.join(cache, "steam_options.yy"),
 "config": "Default", "configParents": "", "outputFolder": out,
 "projectName": "Idle Game Template", "macros": os.path.join(cache, "macros.json"),
 "projectDir": proj_dir, "preferences": os.path.join(cache, "preferences.json"),
 "projectPath": yyp, "tempFolder": temp, "tempFolderUnmapped": temp,
 "userDir": os.path.join(here, "user"), "runtimeLocation": rt,
 "targetOptions": os.path.join(cache, "targetoptions.json"), "targetMask": mask,
 "applicationPath": r"C:\Program Files\GameMaker-LTS2026\GameMaker.exe",
 "verbose": "False", "helpPort": "51290", "debuggerPort": "6509", "prefabs": "C:/ProgramData/GameMakerStudio2-LTS2026/Prefabs",
}
os.makedirs(env["userDir"], exist_ok=True)
json.dump(env, open(os.path.join(temp, "build.bff"), "w", encoding="utf-8"), indent=2)
print(os.path.join(temp, "build.bff"))
