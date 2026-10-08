#!/usr/bin/env python3
"""One-time move of a Race AI server to the layered presets: cfg/ -> presets/tracks/<track>/ -> presets/classes/<class>/.

  migrate-presets.py <server folder> [--dry-run]

- the old presets/ is kept as presets.bak-<date>/
- presets/<track>-<class>/ (made for a class) are dropped: the class is a layer of its own now
- presets/<track>/ become presets/tracks/<track>/ with only what differs from cfg/ (no cars, no name, no copies of cfg/ files)
- presets/classes/<class>/ come from ServerToolsPlugin/presets/classes/ (entry_list.ini with the cars, CARS= and the title)
- the track of cfg/ gets presets/tracks/<track>/ too, so rotation.yml lists real tracks ("default" -> its name)
- cfg/: NAME, welcome.txt and ServerDescription get {class} instead of the class name (a NAME without one gets " [{class}]")
- rotation.yml: "default" -> the track of cfg/, Titles: -> [PRESET] TRACK_TITLE of the tracks; current-preset "a-gte" -> "a+gte"
"""
import datetime
import filecmp
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHIPPED_CLASSES = os.path.join(HERE, "..", "presets", "classes")
LABELS = r"\b(GT3|GTE|GT2|LMP1|JDM)\b"


def read(p):
    with open(p, encoding="utf-8-sig", errors="replace") as f:
        return f.read().replace("\r\n", "\n")


def write(p, text):
    if DRY:
        print(f"  write {p}")
        return
    os.makedirs(os.path.dirname(p) or ".", exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def ini(text):
    """{section: {key: value}} (upper-case names)."""
    out, sec = {}, None
    for line in text.split("\n"):
        t = line.strip()
        if t.startswith("[") and t.endswith("]"):
            sec = t[1:-1].upper()
            out.setdefault(sec, {})
        elif sec and "=" in t and not t.startswith((";", "#")):
            k, v = t.split("=", 1)
            out[sec][k.strip().upper()] = v.strip()
    return out


def set_key(text, section, key, value):
    """KEY=value in [section] (added when missing, section too)."""
    m = re.search(rf"(?mi)^\[{re.escape(section)}\]\s*$", text)
    if not m:
        return text.rstrip("\n") + f"\n\n[{section}]\n{key}={value}\n"
    end = re.search(r"(?m)^\[", text[m.end():])
    end = m.end() + end.start() if end else len(text)
    body = text[m.end():end]
    if re.search(rf"(?mi)^{re.escape(key)}\s*=", body):
        body = re.sub(rf"(?mi)^{re.escape(key)}\s*=.*$", lambda _: f"{key}={value}", body, count=1)
    else:
        body = f"\n{key}={value}" + body
    return text[:m.end()] + body + text[end:]


def drop_key(text, section, key):
    m = re.search(rf"(?mi)^\[{re.escape(section)}\]\s*$", text)
    if not m:
        return text
    end = re.search(r"(?m)^\[", text[m.end():])
    end = m.end() + end.start() if end else len(text)
    body = re.sub(rf"(?mi)^{re.escape(key)}\s*=.*\n?", "", text[m.end():end])
    return text[:m.end()] + body + text[end:]


def main():
    srv = os.path.abspath(sys.argv[1])
    os.chdir(srv)
    cfg = ini(read("cfg/server_cfg.ini"))
    rot_path = "rotation.yml"
    rot = read(rot_path) if os.path.exists(rot_path) else ""
    titles = dict(re.findall(r"(?m)^  ([\w.+-]+):\s*(.+?)\s*$", rot.split("Titles:", 1)[1])) if "Titles:" in rot else {}
    classes = sorted(os.listdir(SHIPPED_CLASSES))

    # the track of cfg/
    main_track = cfg["SERVER"]["TRACK"].split("/")[-1]
    main_name = main_track[3:] if main_track.startswith("ks_") else main_track

    old = sorted(d for d in os.listdir("presets") if os.path.isdir(f"presets/{d}") and d not in ("tracks", "classes")) if os.path.isdir("presets") else []
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M")
    if old and not DRY:
        shutil.copytree("presets", f"presets.bak-{stamp}")
        print(f"backup: presets.bak-{stamp}/")

    # classes
    for c in classes:
        dst = f"presets/classes/{c}"
        if os.path.exists(dst):
            continue
        print(f"class {c}")
        if not DRY:
            shutil.copytree(os.path.join(SHIPPED_CLASSES, c), dst)

    # tracks
    tracks = {"default": main_name}
    for name in old:
        src = f"presets/{name}"
        if not os.path.exists(f"{src}/plugin_race_ai_cfg.yml"):
            print(f"skip {src} (no Race AI preset)")
            continue
        base, _, cls = name.rpartition("-")
        if cls in classes and base:
            print(f"drop {src} (class {cls} of {base}, a layer now)")
            if not DRY:
                shutil.rmtree(src)
            continue
        dst = f"presets/tracks/{name}"
        print(f"track {src} -> {dst}")
        if not DRY:
            shutil.move(src, dst)
        else:
            dst = src
        tracks[name] = name
        for f in sorted(os.listdir(dst)):
            p = f"{dst}/{f}"
            if f in ("entry_list.ini", "welcome.txt") or ".bak" in f or (os.path.exists(f"cfg/{f}") and filecmp.cmp(p, f"cfg/{f}", shallow=False)):
                print(f"  remove {f}")
                if not DRY:
                    os.remove(p)
        if os.path.exists(f"{dst}/extra_cfg.yml"):
            # only keys that differ from cfg/extra_cfg.yml (single-line keys; the class/track text comes from cfg/ now)
            main_lines = set(read("cfg/extra_cfg.yml").split("\n")) if os.path.exists("cfg/extra_cfg.yml") else set()
            own = [l for l in read(f"{dst}/extra_cfg.yml").split("\n")
                   if l.strip() and not l.lstrip().startswith(("#", "-")) and ":" in l and not l.startswith(" ")
                   and not l.startswith("ServerDescription:") and l not in main_lines and not l.endswith(":")]
            if own:
                write(f"{dst}/extra_cfg.yml", "# only what differs from cfg/extra_cfg.yml\n" + "\n".join(own) + "\n")
            else:
                print("  remove extra_cfg.yml (same as cfg/)")
                if not DRY:
                    os.remove(f"{dst}/extra_cfg.yml")
        sp = f"{dst}/server_cfg.ini"
        if os.path.exists(sp):
            text = read(sp)
            own = ini(text)
            for sec, keys in own.items():
                for k, v in keys.items():
                    if sec == "SERVER" and k in ("NAME", "CARS", "WELCOME_MESSAGE") or (not sec.startswith("WEATHER_") and cfg.get(sec, {}).get(k) == v):
                        text = drop_key(text, sec, k)
            el = f"presets.bak-{stamp}/{name}/entry_list.ini" if not DRY else f"{src}/entry_list.ini"
            if os.path.exists(el):
                slots = len(re.findall(r"(?m)^\[CAR_\d+\]", read(el)))
                if slots < int(cfg["SERVER"].get("MAX_CLIENTS", "0") or 0):
                    text = set_key(text, "SERVER", "MAX_CLIENTS", str(slots))
            text = set_key(text, "PRESET", "TRACK_TITLE", titles.get(name, name))
            text = re.sub(r"\A(?:\s*;[^\n]*\n)+", "", text)  # old header comment
            write(sp, "; only what differs from cfg/server_cfg.ini\n" + text.lstrip("\n"))
        pc = f"{dst}/plugin_race_ai_cfg.yml"
        if os.path.exists(pc):
            write(pc, read(pc).replace(f"presets/{name}/", f"{dst}/"))

    # the track of cfg/ as a track of its own
    nd = f"presets/tracks/{main_name}"
    if not os.path.exists(nd):
        print(f"track {nd} (from cfg/)")
        write(f"{nd}/server_cfg.ini", "; only what differs from cfg/server_cfg.ini\n[SERVER]\n"
              f"TRACK={cfg['SERVER']['TRACK']}\nCONFIG_TRACK={cfg['SERVER'].get('CONFIG_TRACK', '')}\n\n"
              f"[PRESET]\nTRACK_TITLE={titles.get('default', main_name)}\n")

    # cfg/: placeholders
    sc = read("cfg/server_cfg.ini")
    # the server's own name stays: a class label in it becomes {class}, else " [{class}]" is added (like the old class presets did)
    name = cfg["SERVER"].get("NAME", "")
    if "{class}" not in name:
        name = re.sub(LABELS, "{class}", name) if re.search(LABELS, name) else (name + " [{class}]" if name else "{track} {class} vs Race AI")
    sc = set_key(sc, "SERVER", "NAME", name)
    sc = set_key(sc, "PRESET", "TRACK_TITLE", titles.get("default", main_name))
    m = re.search(LABELS, read("cfg/entry_list.ini") + cfg["SERVER"].get("NAME", "")) or re.search(LABELS, read("cfg/welcome.txt") if os.path.exists("cfg/welcome.txt") else "")
    sc = set_key(sc, "PRESET", "CLASS_TITLE", m.group(1) if m else "GT3")
    write("cfg/server_cfg.ini", sc)
    if os.path.exists("cfg/welcome.txt"):
        write("cfg/welcome.txt", re.sub(LABELS, "{class}", read("cfg/welcome.txt")))
    if os.path.exists("cfg/extra_cfg.yml"):
        write("cfg/extra_cfg.yml", re.sub(r"(?m)^(ServerDescription:.*)$", lambda x: re.sub(LABELS, "{class}", x.group(1)), read("cfg/extra_cfg.yml")))

    # rotation.yml, current-preset
    if rot:
        rot = re.sub(r"(?m)^(\s*-\s*)default\s*$", lambda x: x.group(1) + main_name, rot)
        rot = re.sub(r"(?ms)^# Names for chat and welcome message\n", "", rot)
        rot = re.sub(r"(?ms)^Titles:.*?(?=^\S|\Z)", "", rot)
        rot = rot.replace('# Tracks: preset folders in presets/ ("default" = the cfg/ folder), in this order',
                          '# Tracks: folders in presets/tracks/, in this order ("nordschleife+lmp1" = always with that class)')
        write(rot_path, rot)
    if os.path.exists("current-preset"):
        cur = read("current-preset").strip()
        base, _, cls = cur.rpartition("-")
        new = f"{base}+{cls}" if cls in classes and base else (cur or main_name)
        if new != cur:
            write("current-preset", new)
    print("done" + (" (dry run, nothing changed)" if DRY else ""))


DRY = "--dry-run" in sys.argv
if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main()
