#!/usr/bin/env python3
"""Switch a Race AI server (or one of its rotation presets) to another vehicle class.

  set-class.py --list
  set-class.py gte                                   # cfg/ of the server in the current folder
  set-class.py gte --server /path/to/server --preset trialmountain
  set-class.py gte --all                             # cfg/ and every Race AI preset
  set-class.py lmp1 --from default --new-preset nordschleife-lmp1 --name "Nordschleife LMP1 vs Race AI"
                                                     # copy cfg/ as a new rotation preset

What it changes in the folder (cfg/ or presets/<name>/):
  entry_list.ini   MODEL/SKIN of every slot (AI=, player slots, ballast etc. stay as they are)
  server_cfg.ini   CARS= (all models of the class) and the class name in NAME=
  extra_cfg.yml    the class name in ServerDescription
  welcome.txt      the class name
  plugin_race_ai_cfg.yml  GridFile path when a preset is copied
Classes live in classes/classes.json next to this script. Backups: <file>.bak-<class> before the first change.
Every model of the class needs its folder in content/cars on the server (data.acd, ideally ui/ui_car.json).
"""
import argparse
import json
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LABEL_RE = re.compile(r"\b(GT3|GTE|GT2|LMP1|JDM)\b")


def load_classes():
    with open(os.path.join(HERE, "classes", "classes.json"), encoding="utf-8") as f:
        return json.load(f)["classes"]


def read(path):
    with open(path, "rb") as f:
        raw = f.read()
    text = raw.decode("utf-8-sig", errors="replace")
    nl = "\r\n" if "\r\n" in text else "\n"
    return text.replace("\r\n", "\n"), nl


def write(path, text, nl, backup_tag):
    bak = f"{path}.bak-{backup_tag}"
    if backup_tag and os.path.exists(path) and not os.path.exists(bak):
        shutil.copy2(path, bak)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text.replace("\n", nl))


def assign(models, slots):
    """Slot i gets model i % n; each further round uses the next skin of that model."""
    names = list(models)
    out = []
    for i in range(slots):
        m = names[i % len(names)]
        skins = models[m] or [""]
        out.append((m, skins[(i // len(names)) % len(skins)]))
    return out


def apply_entry_list(path, cls, tag):
    text, nl = read(path)
    blocks = re.split(r"(?m)^(?=\[CAR_\d+\])", text)
    cars = [b for b in blocks if b.startswith("[CAR_")]
    # player slots and AI slots each cycle through all models (they are often interleaved)
    is_ai = [bool(re.search(r"(?m)^AI=(fixed|auto)", b)) for b in cars]
    n_ai, n_pl = sum(is_ai), len(cars) - sum(is_ai)
    n = len(cls["models"])
    skip = -(-n_pl // n) * n  # AI cars continue with the next skins, so they don't wear the players' liveries
    full = assign(cls["models"], skip + n_ai)
    plans = {False: full[:n_pl], True: full[skip:]}
    used = {True: 0, False: 0}
    k = 0
    out = []
    for b in blocks:
        if b.startswith("[CAR_"):
            ai = is_ai[k]
            m, s = plans[ai][used[ai]]
            used[ai] += 1
            k += 1
            b = re.sub(r"(?m)^MODEL=.*$", f"MODEL={m}", b)
            b = re.sub(r"(?m)^SKIN=.*$", f"SKIN={s}", b)
        out.append(b)
    write(path, "".join(out), nl, tag)
    return len(cars)


def relabel(s, label):
    return LABEL_RE.sub(label, s)


def apply_folder(folder, key, cls, name=None, backup=True):
    label = cls["label"]
    tag = key if backup else None
    el = os.path.join(folder, "entry_list.ini")
    if not os.path.exists(el):
        sys.exit(f"{el} not found")
    n = apply_entry_list(el, cls, tag)

    sc = os.path.join(folder, "server_cfg.ini")
    if os.path.exists(sc):
        text, nl = read(sc)
        text = re.sub(r"(?m)^CARS=.*$", "CARS=" + ";".join(cls["models"]), text, count=1)
        if name:
            text = re.sub(r"(?m)^NAME=.*$", lambda m: "NAME=" + name, text, count=1)
        else:
            text = re.sub(r"(?m)^(NAME=.*)$", lambda m: relabel(m.group(1), label), text, count=1)
        write(sc, text, nl, tag)

    ex = os.path.join(folder, "extra_cfg.yml")
    if os.path.exists(ex):
        text, nl = read(ex)
        text = re.sub(r"(?m)^(ServerDescription:.*)$", lambda m: relabel(m.group(1), label), text, count=1)
        write(ex, text, nl, tag)

    wt = os.path.join(folder, "welcome.txt")
    if os.path.exists(wt):
        text, nl = read(wt)
        write(wt, relabel(text, label), nl, tag)
    print(f"{folder}: {n} slots -> {label} ({', '.join(cls['models'])})")


def preset_dir(server, name):
    return os.path.join(server, "cfg") if name == "default" else os.path.join(server, "presets", name)


def copy_preset(server, src, dst):
    s = preset_dir(server, src)
    d = os.path.join(server, "presets", dst)
    if os.path.exists(d):
        sys.exit(f"{d} exists already")
    os.makedirs(d)
    for f in os.listdir(s):
        p = os.path.join(s, f)
        if os.path.isfile(p) and ".bak" not in f:
            shutil.copy2(p, os.path.join(d, f))
    rc = os.path.join(d, "plugin_race_ai_cfg.yml")
    if os.path.exists(rc):
        text, nl = read(rc)

        def fix(m):
            return "GridFile: " + "presets/" + dst + "/" + os.path.basename(m.group(1).strip().strip("'\""))
        text = re.sub(r"(?m)^GridFile:\s*(\S.*)$", fix, text, count=1)
        with open(rc, "w", encoding="utf-8", newline="") as f:
            f.write(text.replace("\n", nl))
    return d


def is_race_ai(folder):
    return os.path.exists(os.path.join(folder, "plugin_race_ai_cfg.yml"))


def main():
    classes = load_classes()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cls", nargs="?", help="class: " + ", ".join(classes))
    ap.add_argument("--server", default=".", help="server folder (contains cfg/ and presets/)")
    ap.add_argument("--preset", default="default", help="preset to change (default = cfg/)")
    ap.add_argument("--all", action="store_true", help="cfg/ and every Race AI preset")
    ap.add_argument("--from", dest="src", help="copy this preset first (default = cfg/) ...")
    ap.add_argument("--new-preset", help="... to presets/<name> and change that one")
    ap.add_argument("--name", help="server name (NAME= in server_cfg.ini), e.g. \"Nordschleife GTE vs Race AI\"")
    ap.add_argument("--list", action="store_true")
    a = ap.parse_args()

    if a.list or not a.cls:
        for k, c in classes.items():
            print(f"{k:6} {c['label']:5} {c['description']}")
        return
    if a.cls not in classes:
        sys.exit(f"unknown class {a.cls}; known: {', '.join(classes)}")
    cls = classes[a.cls]

    missing = [m for m in cls["models"] if not os.path.exists(os.path.join(a.server, "content", "cars", m, "data.acd"))]
    if os.path.isdir(os.path.join(a.server, "content", "cars")) and missing:
        print("WARNING: no content/cars/<model>/data.acd for: " + ", ".join(missing) + " (upload these car folders)")

    if a.new_preset:
        d = copy_preset(a.server, a.src or "default", a.new_preset)
        apply_folder(d, a.cls, cls, a.name, backup=False)
        print(f"Add '{a.new_preset}' to Tracks: (and Titles:) in rotation.yml to use it.")
        return
    if a.all:
        folders = [os.path.join(a.server, "cfg")]
        pd = os.path.join(a.server, "presets")
        if os.path.isdir(pd):
            folders += [os.path.join(pd, p) for p in sorted(os.listdir(pd)) if is_race_ai(os.path.join(pd, p))]
        for f in folders:
            apply_folder(f, a.cls, cls)
        return
    apply_folder(preset_dir(a.server, a.preset), a.cls, cls, a.name)


if __name__ == "__main__":
    main()
