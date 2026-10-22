#!/usr/bin/env python3
# IT-Solutions Asset-Collector (Linux)
# collector-version: 4
# Infrastruktur-Scan (nur lesend) -> POST an das CRM (/api/agent/report). Dieselben Schnittstellen,
# Tokens und dieselbe Ampel wie der Windows-Collector. Fuehrt KEINE Fernbefehle aus und aendert nichts am System.
#
# Laeuft als root per systemd-Timer (alle 2 Stunden): /opt/it-solutions-agent/collector.py
# Konfiguration: /etc/it-solutions-agent/config.json  {"token": "ita_...", "reportUrl": "https://.../api/agent/report"}
# Optional in der Config: "backupMarkers": [{"name": "...", "path": "/var/log/x.log", "successPattern": "...", "failPattern": "..."}]
#
# Self-Update (signiert): laedt collector.py + collector.py.sig vom CRM und installiert die neue Version NUR, wenn die
# RSA-SHA256-Signatur (openssl) zum eingebetteten oeffentlichen Schluessel passt, die Version groesser ist und das
# Skript fehlerfrei kompiliert. Kein Rollback, kein freies Skript.
#
# Profile: generic (Debian/Ubuntu), proxmox, synology (DSM), raspberrypi. Weitere Geraetetypen kommen als Profil dazu.
# Test ohne Senden:  python3 collector.py --dry-run
# Nur ASCII in dieser Datei.
import base64
import glob
import json
import os
import platform
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

COLLECTOR_VERSION = 4
CONFIG_PATH = os.environ.get("ITS_AGENT_CONFIG", "/etc/it-solutions-agent/config.json")
PUBLIC_KEY_PEM = """-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAuAxspiYovBEcA7viyD9y
1pDu+gw4n/ASAGAgLZHCURTOYeD6Az/Udz8JavYAf7SQmISWfe28nVhzskliDyYy
y23RiS9NpEnS0tqMXBNr+tKz29N4wOhTdyhqsIyAulxN9rK+VA+WyF9lNBmTI94O
vUJ3qeebgrJpHThdVILVVhqQVwUjTJvq2ZQDoCZcofdGYVWowBRyCewoW9KMnDso
EGXy/1/nuxKRB4GicQMo4P5+HMWbNQFf+c5n+qGKE3N4TdPUHQOE38i25y5zgJ8e
88Olqhla2cKfhU5wwJhZ5JgvR/R6BSt0QAxdXmkOLtfjH8SWIyHLdaUXRQyWPo4c
b2bDaUsEbL3R7IapkqejvGwQJMsamoGU9xVUP0+Cj/UlpVYWQ1wSmFnIZS02LG7b
pAIfkeUEhc/3/iu2nJ/PV2dROIHZ1rlxNBwcmWb0QnaYEYHd1IGHdVCMBpx2ggmg
ChphkbhhaGd3r84ZRt3SmIefwmx025R/CgSX/6FvsqihAgMBAAE=
-----END PUBLIC KEY-----
"""

TIMING = {}


# --- Hilfsfunktionen ----------------------------------------------------------------------

def run(args, timeout=20):
    """Befehl ohne Shell ausfuehren. Liefert (returncode, stdout) oder (None, '') bei Fehler/Zeitueberschreitung."""
    env = dict(os.environ)
    env["LC_ALL"] = "C"
    env["LANG"] = "C"
    try:
        p = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=timeout, env=env)
        return p.returncode, p.stdout.decode("utf-8", "replace")
    except Exception:
        return None, ""


def which(name):
    return shutil.which(name)


def read(path, limit=2000000):
    try:
        with open(path, "r", errors="replace") as f:
            return f.read(limit)
    except Exception:
        return ""


def iso(ts):
    return datetime.fromtimestamp(ts, tz=timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def safe(name, fn, default=None):
    """Block fehlertolerant ausfuehren und die Dauer messen (landet als 'timing' im Bericht)."""
    t0 = time.time()
    try:
        return fn()
    except Exception:
        return default
    finally:
        TIMING[name] = round(time.time() - t0, 1)


def to_float(s, default=None):
    try:
        return float(s)
    except Exception:
        return default


# --- Parser (rein, ohne Systemzugriff - per Test abgedeckt) ----------------------------------

def parse_os_release(text):
    out = {}
    for line in text.splitlines():
        if "=" in line and not line.startswith("#"):
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip().strip('"').strip("'")
    return out


def parse_meminfo(text):
    vals = {}
    for line in text.splitlines():
        m = re.match(r"(\w+):\s+(\d+)\s*kB", line)
        if m:
            vals[m.group(1)] = int(m.group(2))
    total = vals.get("MemTotal")
    avail = vals.get("MemAvailable", vals.get("MemFree"))
    if not total:
        return None, None
    return round(total / 1048576.0, 1), round((total - (avail or 0)) / 1048576.0, 1)


SKIP_FS = ("tmpfs", "devtmpfs", "overlay", "shm", "udev", "none", "efivarfs", "squashfs", "/dev/loop", "cgroup", "fuse")
SKIP_MOUNT = ("/run", "/sys", "/proc", "/dev", "/snap", "/var/lib/docker", "/var/lib/containers", "/var/lib/lxcfs")


def parse_df(text):
    """df -P -B1 -> [{drive, totalGb, freeGb, usedPct}]"""
    rows, seen = [], set()
    for line in text.splitlines()[1:]:
        p = line.split()
        if len(p) < 6:
            continue
        fs, total, used, avail, mount = p[0], p[1], p[2], p[3], " ".join(p[5:])
        if fs.startswith(SKIP_FS) or mount.startswith(SKIP_MOUNT):
            continue
        try:
            total, used, avail = int(total), int(used), int(avail)
        except ValueError:
            continue
        if total <= 0 or fs in seen:
            continue
        seen.add(fs)
        denom = used + avail
        rows.append({
            "drive": mount, "totalGb": round(total / 1073741824.0, 1), "freeGb": round(avail / 1073741824.0, 1),
            "usedPct": round(used / denom * 100.0, 1) if denom > 0 else 0,
        })
    return rows


def parse_df_inodes(text):
    """df -Pi -> Mounts mit >= 80 % Inode-Belegung"""
    out, seen = [], set()
    for line in text.splitlines()[1:]:
        p = line.split()
        if len(p) < 6 or p[0].startswith(SKIP_FS) or " ".join(p[5:]).startswith(SKIP_MOUNT) or p[0] in seen:
            continue
        seen.add(p[0])
        m = re.match(r"(\d+)%", p[4])
        if m and int(m.group(1)) >= 80:
            out.append({"mount": " ".join(p[5:]), "usedPct": int(m.group(1))})
    return out


def parse_mdstat(text):
    """/proc/mdstat -> [{name, level, status, degraded, syncing}]"""
    arrays = []
    cur = None
    for line in text.splitlines():
        m = re.match(r"^(md\d+)\s*:\s*(\w+)\s+(\w+)?", line)
        if m:
            cur = {"name": m.group(1), "level": m.group(3) or "", "status": "", "degraded": False, "syncing": False}
            arrays.append(cur)
            continue
        if cur is not None:
            s = re.search(r"\[(\d+)/(\d+)\]\s+\[([U_]+)\]", line)
            if s:
                cur["status"] = s.group(3)
                cur["degraded"] = "_" in s.group(3)
            if re.search(r"(resync|recovery|reshape|check)\s*=", line):
                cur["syncing"] = True
    return arrays


def parse_apt_upgradable(text):
    """apt list --upgradable -> (anzahl, sicherheitsanzahl, liste)"""
    items, sec = [], 0
    for line in text.splitlines():
        if "[upgradable" not in line:
            continue
        m = re.match(r"^([^/\s]+)/(\S+)\s+(\S+)", line)
        if not m:
            continue
        is_sec = "security" in m.group(2).lower()
        if is_sec:
            sec += 1
        items.append({"title": "%s %s" % (m.group(1), m.group(3)), "kb": "", "security": is_sec, "driver": False, "severity": ""})
    return len(items), sec, items[:40]


def parse_failed_units(text):
    out = []
    for line in text.splitlines():
        p = line.replace("\u25cf", " ").split()
        if p and "." in p[0]:
            out.append(re.sub(r"\.service$", "", p[0]))
    return out


def parse_smart(text):
    """smartctl -H -i -A -> Kennzahlen eines Datentraegers"""
    d = {"model": None, "health": None, "criticalWarning": None, "percentUsed": None, "tempC": None,
         "powerOnHours": None, "reallocated": None, "pending": None, "spare": None}
    for line in text.splitlines():
        m = re.match(r"^(Device Model|Model Number|Product|Model Family):\s+(.+)$", line)
        if m and (d["model"] is None or m.group(1) in ("Device Model", "Model Number")):
            d["model"] = m.group(2).strip()
        m = re.search(r"overall-health self-assessment test result:\s*(\w+)", line)
        if m:
            d["health"] = m.group(1).upper()
        m = re.search(r"SMART Health Status:\s*(\w+)", line)
        if m and d["health"] is None:
            d["health"] = "PASSED" if m.group(1).upper() == "OK" else m.group(1).upper()
        m = re.match(r"^Critical Warning:\s+(0x[0-9a-fA-F]+)", line)
        if m:
            d["criticalWarning"] = m.group(1).lower()
        m = re.match(r"^Percentage Used:\s+(\d+)%", line)
        if m:
            d["percentUsed"] = int(m.group(1))
        m = re.match(r"^Available Spare:\s+(\d+)%", line)
        if m:
            d["spare"] = int(m.group(1))
        m = re.match(r"^Temperature:\s+(\d+)\s*Celsius", line)
        if m:
            d["tempC"] = int(m.group(1))
        m = re.match(r"^Power On Hours:\s+([\d,\.]+)", line)
        if m:
            d["powerOnHours"] = int(re.sub(r"[^\d]", "", m.group(1)) or 0)
        # SATA-Attributtabelle: ID NAME FLAG VALUE WORST THRESH TYPE UPDATED WHEN_FAILED RAW
        m = re.match(r"^\s*\d+\s+(\S+)\s+0x[0-9a-fA-F]+\s+\d+\s+\d+\s+\d+\s+\S+\s+\S+\s+\S+\s+(\d+)", line)
        if m:
            name, raw = m.group(1), int(m.group(2))
            if name == "Reallocated_Sector_Ct":
                d["reallocated"] = raw
            elif name == "Current_Pending_Sector":
                d["pending"] = raw
            elif name == "Power_On_Hours" and d["powerOnHours"] is None:
                d["powerOnHours"] = raw
            elif name in ("Temperature_Celsius", "Airflow_Temperature_Cel") and d["tempC"] is None:
                d["tempC"] = raw & 0xFF
    return d


def parse_docker_ps(text):
    """docker ps -a --format '{{.Names}}|{{.State}}|{{.Status}}'"""
    c = {"running": 0, "exited": 0, "restarting": 0, "unhealthy": 0, "unhealthyNames": [], "restartingNames": [], "exitedNames": []}
    for line in text.splitlines():
        p = line.split("|", 2)
        if len(p) < 3:
            continue
        name, state, status = p[0], p[1].lower(), p[2]
        if state == "running":
            c["running"] += 1
        elif state == "restarting":
            c["restarting"] += 1
            c["restartingNames"].append(name)
        elif state == "exited":
            c["exited"] += 1
            if not status.startswith("Exited (0)"):
                c["exitedNames"].append(name)
        if "(unhealthy)" in status:
            c["unhealthy"] += 1
            c["unhealthyNames"].append(name)
    for k in ("unhealthyNames", "restartingNames", "exitedNames"):
        c[k] = c[k][:10]
    return c


def parse_openssl_enddate(text):
    m = re.search(r"notAfter=(\w{3})\s+(\d+)\s+(\d\d):(\d\d):(\d\d)\s+(\d{4})", text)
    if not m:
        return None
    months = {"Jan": 1, "Feb": 2, "Mar": 3, "Apr": 4, "May": 5, "Jun": 6, "Jul": 7, "Aug": 8, "Sep": 9, "Oct": 10, "Nov": 11, "Dec": 12}
    try:
        return datetime(int(m.group(6)), months[m.group(1)], int(m.group(2)), int(m.group(3)), int(m.group(4)), int(m.group(5)), tzinfo=timezone.utc)
    except Exception:
        return None


def parse_sshd_t(text):
    out = {}
    for line in text.splitlines():
        p = line.split(None, 1)
        if len(p) == 2 and p[0] in ("permitrootlogin", "passwordauthentication"):
            out[p[0]] = p[1].strip()
    return out


def parse_dpkg_last_upgrade(text):
    """Neuester 'upgrade'-Zeitpunkt aus dpkg.log (UTC-Annahme nicht noetig, Tagesgenauigkeit reicht)."""
    last = None
    for line in text.splitlines():
        m = re.match(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) upgrade ", line)
        if m:
            last = m.group(1)
    if not last:
        return None
    try:
        return datetime.strptime(last, "%Y-%m-%d %H:%M:%S").replace(tzinfo=timezone.utc)
    except Exception:
        return None


def parse_throttled(text):
    m = re.search(r"throttled=(0x[0-9a-fA-F]+)", text)
    if not m:
        return None
    v = int(m.group(1), 16)
    flags = []
    names = {0: "unterspannung-jetzt", 1: "takt-begrenzt-jetzt", 2: "gedrosselt-jetzt", 3: "temperaturgrenze-jetzt",
             16: "unterspannung-aufgetreten", 17: "takt-begrenzt-aufgetreten", 18: "gedrosselt-aufgetreten", 19: "temperaturgrenze-aufgetreten"}
    for bit, n in names.items():
        if v & (1 << bit):
            flags.append(n)
    return {"raw": m.group(1).lower(), "flags": flags}


def parse_cpu_model(text):
    """/proc/cpuinfo -> Modellname. x86: 'model name'; ARM/Raspberry Pi: 'Model' (Text) bzw. 'Hardware'.
    Die x86-Zeile 'model : 17' ist nur eine Nummer und wird nicht verwendet."""
    vals = {}
    for line in text.splitlines():
        if ":" in line:
            k, v = line.split(":", 1)
            k = k.strip().lower()
            if k not in vals:
                vals[k] = v.strip()
    model = vals.get("model")
    if model and not re.match(r"^\d+$", model):
        return model
    return vals.get("model name") or vals.get("hardware") or vals.get("cpu model")


def parse_synology_version(text):
    d = {}
    for line in text.splitlines():
        m = re.match(r'^(\w+)="([^"]*)"', line)
        if m:
            d[m.group(1)] = m.group(2)
    return d


def parse_package_info(text):
    n = re.search(r'^package="([^"]+)"', text, re.M)
    v = re.search(r'^version="([^"]+)"', text, re.M)
    return (n.group(1), v.group(1)) if n and v else None


# --- Datenerfassung ----------------------------------------------------------------------

def detect_profile():
    if os.path.exists("/etc/synoinfo.conf") or os.path.exists("/etc.defaults/synoinfo.conf"):
        return "synology"
    model = read("/proc/device-tree/model").replace("\x00", "")
    if "Raspberry Pi" in model:
        return "raspberrypi"
    if which("pveversion"):
        return "proxmox"
    return "generic"


def collect_os(profile):
    osr = parse_os_release(read("/etc/os-release"))
    name = osr.get("PRETTY_NAME") or platform.platform()
    version_id, osid = osr.get("VERSION_ID"), osr.get("ID")
    if profile == "synology":
        v = parse_synology_version(read("/etc/VERSION"))
        if v:
            name = "Synology DSM %s-%s" % (v.get("productversion", "?"), v.get("buildnumber", "?"))
            version_id, osid = v.get("productversion"), "dsm"
    if profile == "proxmox":
        rc, out = run(["pveversion"], 10)
        m = re.search(r"pve-manager/([\d\.]+)", out or "")
        if m:
            name = "Proxmox VE %s (%s)" % (m.group(1), osr.get("PRETTY_NAME", "Debian"))
            version_id = m.group(1)
    return {"osName": name, "osBuild": platform.release(), "versionId": version_id, "id": osid}


def collect_uptime():
    up = to_float(read("/proc/uptime").split()[0] if read("/proc/uptime") else None)
    if up is None:
        return None, None
    return round(up / 3600.0, 1), iso(time.time() - up)


def collect_cpu():
    model = parse_cpu_model(read("/proc/cpuinfo"))
    n = os.cpu_count() or 1
    try:
        load = os.getloadavg()[0]
    except Exception:
        load = None
    return model, (round(min(100.0, load / n * 100.0), 1) if load is not None else None)


def collect_disks(profile="generic"):
    rc, out = run(["df", "-P", "-B1"], 20)
    rows = parse_df(out or "")
    if profile == "synology":
        # Die kleine DSM-Systempartition steht dauerhaft bei ca. 75-80 % und waere ein Dauer-Fehlalarm.
        rows = [r for r in rows if r["drive"] != "/"]
    return rows


def collect_inodes():
    rc, out = run(["df", "-Pi"], 20)
    return parse_df_inodes(out or "")


def collect_failed_units():
    if not which("systemctl"):
        return []
    rc, out = run(["systemctl", "--failed", "--no-legend", "--plain", "--no-pager"], 20)
    return parse_failed_units(out or "")


def collect_roles():
    if not which("systemctl"):
        return []
    rc, out = run(["systemctl", "list-unit-files", "--type=service", "--no-legend", "--no-pager"], 20)
    names = set(l.split()[0].replace(".service", "") for l in (out or "").splitlines() if l.strip())
    roles = []

    def has(pat):
        return any(re.match(pat, n) for n in names)

    if "docker" in names or which("docker"):
        roles.append("Docker")
    if "pveproxy" in names:
        roles.append("Proxmox")
    if has(r"^(postgresql|mysql|mariadb|mysqld)"):
        roles.append("SQL")
    if has(r"^(nginx|apache2|httpd)$"):
        roles.append("Web")
    if has(r"^(smbd|samba)"):
        roles.append("Samba")
    return roles


def collect_updates(profile):
    """Offene Updates (Debian-Familie). Synology/ohne apt: None."""
    if not which("apt"):
        return None
    rc, out = run(["apt", "list", "--upgradable"], 60)
    if rc is None:
        return None
    count, sec, items = parse_apt_upgradable(out)
    stamp = "/var/lib/apt/periodic/update-success-stamp"
    age = None
    if os.path.exists(stamp):
        age = round((time.time() - os.path.getmtime(stamp)) / 86400.0, 1)
    last = None
    for f in ("/var/log/dpkg.log", "/var/log/dpkg.log.1"):
        d = parse_dpkg_last_upgrade(read(f))
        if d:
            last = d
            break
    return {"count": count, "security": sec, "drivers": 0, "list": items,
            "lastInstalled": last.strftime("%Y-%m-%dT%H:%M:%SZ") if last else None, "cacheAgeDays": age}


def collect_reboot():
    req = os.path.exists("/var/run/reboot-required") or os.path.exists("/run/reboot-required")
    pk = read("/var/run/reboot-required.pkgs").split()[:10] if req else []
    return {"required": req, "packages": pk}


def collect_raid():
    return parse_mdstat(read("/proc/mdstat"))


def collect_smart():
    if not which("smartctl"):
        return None
    rc, out = run(["smartctl", "--scan"], 20)
    devs = []
    for line in (out or "").splitlines():
        m = re.match(r"^(/dev/\S+)\s+-d\s+(\S+)", line)
        if m:
            devs.append((m.group(1), m.group(2)))
    result = []
    for dev, typ in devs[:16]:
        # Erst mit Auto-Erkennung (die Scan-Typangabe "scsi" liefert bei SATA-Platten keine SMART-Daten),
        # nur wenn das nichts ergibt mit dem vom Scan genannten Typ (z.B. Platten hinter einem RAID-Controller).
        rc, txt = run(["smartctl", "-H", "-i", "-A", dev], 30)
        d = parse_smart(txt or "")
        if d["health"] is None and typ not in ("auto", "scsi"):
            rc, txt = run(["smartctl", "-H", "-i", "-A", "-d", typ, dev], 30)
            d = parse_smart(txt or "")
        if d["health"] is None and d["model"] is None:
            continue
        d["device"] = dev
        result.append(d)
    return result


def collect_docker():
    if not which("docker"):
        return None
    rc, out = run(["docker", "ps", "-a", "--format", "{{.Names}}|{{.State}}|{{.Status}}"], 30)
    if rc is None or rc != 0:
        return None
    return parse_docker_ps(out)


def collect_certs():
    out = []
    now = datetime.now(timezone.utc)
    if not which("openssl"):
        return out
    for path in sorted(glob.glob("/etc/letsencrypt/live/*/fullchain.pem"))[:30]:
        rc, txt = run(["openssl", "x509", "-noout", "-enddate", "-in", path], 10)
        end = parse_openssl_enddate(txt or "")
        if end:
            out.append({"name": os.path.basename(os.path.dirname(path)), "days": int((end - now).total_seconds() // 86400)})
    return out


def collect_security(profile):
    sec = {"firewall": "unbekannt", "fail2ban": None, "sshRootLogin": None, "sshPasswordAuth": None, "unattendedUpgrades": None}
    if profile == "synology":
        return sec   # DSM hat eigene Firewall/Update-Verwaltung; Debian-Pruefungen waeren Fehlalarme
    # "keine" nur melden, wenn der Befehl erfolgreich lief (ohne root schlagen sie fehl -> "unbekannt", kein Fehlalarm)
    if which("ufw"):
        rc, out = run(["ufw", "status"], 10)
        if rc == 0:
            sec["firewall"] = "ufw-aktiv" if "Status: active" in (out or "") else ("ufw-inaktiv" if "inactive" in (out or "") else "ufw-unbekannt")
    elif which("nft"):
        rc, out = run(["nft", "list", "ruleset"], 10)
        if rc == 0:
            sec["firewall"] = "nftables" if (out or "").strip() else "keine"
    elif which("iptables"):
        rc, out = run(["iptables", "-S"], 10)
        if rc == 0:
            sec["firewall"] = "iptables" if len((out or "").splitlines()) > 3 else "keine"
    if which("systemctl"):
        rc, out = run(["systemctl", "is-active", "fail2ban"], 10)
        if rc is not None:
            sec["fail2ban"] = (out or "").strip() == "active"
        rc, out = run(["systemctl", "is-enabled", "unattended-upgrades"], 10)
        if rc is not None:
            sec["unattendedUpgrades"] = (out or "").strip() == "enabled"
    sshd = which("sshd") or ("/usr/sbin/sshd" if os.path.exists("/usr/sbin/sshd") else None)
    if sshd:
        rc, out = run([sshd, "-T"], 10)
        t = parse_sshd_t(out or "")
        sec["sshRootLogin"] = t.get("permitrootlogin")
        sec["sshPasswordAuth"] = t.get("passwordauthentication")
    return sec


def collect_backup(cfg):
    """Backup-Nachweis ueber konfigurierte Marker-Dateien (Log-Datei + Muster). Ohne Konfiguration: nichts erkannt."""
    best = None
    for mk in cfg.get("backupMarkers", []) or []:
        path = mk.get("path")
        if not path or not os.path.exists(path):
            continue
        tail = "\n".join(read(path, 400000).splitlines()[-300:])
        mtime = os.path.getmtime(path)
        ok_re, fail_re = mk.get("successPattern"), mk.get("failPattern")
        ok = bool(ok_re and re.search(ok_re, tail))
        bad = bool(fail_re and re.search(fail_re, tail))
        last_line = [l for l in tail.splitlines() if l.strip()][-1:] or [""]
        success_last = bool(ok_re and re.search(ok_re, last_line[0]))
        res = {"product": mk.get("name", "Backup"),
               "lastSuccess": iso(mtime) if ok and (success_last or not bad) else None,
               "lastFailure": iso(mtime) if bad and not success_last else None,
               "lastResult": "success" if (ok and (success_last or not bad)) else ("failed" if bad else None),
               "samples": [{"job": mk.get("name", "Backup"), "result": "Success" if ok and not bad else ("Failed" if bad else "unbekannt"), "time": iso(mtime)}]}
        if best is None or (res["lastSuccess"] or "") > (best["lastSuccess"] or ""):
            best = res
    return best


def collect_software(profile):
    out = []
    if profile == "synology":
        for info in sorted(glob.glob("/var/packages/*/INFO"))[:200]:
            pv = parse_package_info(read(info, 20000))
            if pv:
                out.append({"name": pv[0], "version": pv[1], "publisher": "Synology"})
        return out
    if which("dpkg-query"):
        rc, txt = run(["dpkg-query", "-W", "-f=${Package}\t${Version}\n"], 60)
        for line in (txt or "").splitlines():
            p = line.split("\t")
            if len(p) == 2:
                out.append({"name": p[0][:100], "version": p[1][:60], "publisher": ""})
    return out[:1500]


def collect_hardware():
    def dmi(n):
        return read("/sys/class/dmi/id/" + n).strip() or None
    rc, virt = run(["systemd-detect-virt"], 10) if which("systemd-detect-virt") else (None, "")
    virt = (virt or "").strip()
    manufacturer, model = dmi("sys_vendor"), dmi("product_name")
    pi = read("/proc/device-tree/model").replace("\x00", "").strip()
    if pi:
        manufacturer, model = "Raspberry Pi", pi
    syno = read("/proc/sys/kernel/syno_hw_version").strip()
    if syno:
        manufacturer, model = "Synology", syno
    text = "%s %s" % (manufacturer or "", model or "")
    is_virtual = (virt not in ("", "none")) or bool(re.search(r"Virtual|VMware|KVM|QEMU|VirtualBox|HVM|Xen", text))
    return {"manufacturer": manufacturer, "model": model, "serial": dmi("product_serial"), "isVirtual": is_virtual,
            "virtualization": virt or None, "biosVersion": dmi("bios_version"), "domain": None, "partOfDomain": None}


def collect_temperature():
    t = to_float(read("/sys/class/thermal/thermal_zone0/temp").strip())
    return round(t / 1000.0, 1) if t is not None else None


def collect_pi():
    vc = which("vcgencmd") or ("/opt/vc/bin/vcgencmd" if os.path.exists("/opt/vc/bin/vcgencmd") else None)
    out = {"model": read("/proc/device-tree/model").replace("\x00", "").strip()}
    if vc:
        rc, txt = run([vc, "get_throttled"], 10)
        out["throttled"] = parse_throttled(txt or "")
    out["rootDevice"] = "sd" if os.path.exists("/sys/block/mmcblk0") else "other"
    return out


def collect_synology():
    v = parse_synology_version(read("/etc/VERSION"))
    return {"dsmVersion": v.get("productversion"), "build": v.get("buildnumber"), "buildDate": v.get("builddate"),
            "model": read("/proc/sys/kernel/syno_hw_version").strip() or None}


# --- Self-Update ----------------------------------------------------------------------------

def http_get(url, timeout=30):
    req = urllib.request.Request(url, headers={"User-Agent": "its-linux-collector/%d" % COLLECTOR_VERSION})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def verify_signature(data, sig_b64):
    """RSA-SHA256 (PKCS#1 v1.5) per openssl gegen den eingebetteten oeffentlichen Schluessel."""
    if not which("openssl"):
        return None
    d = tempfile.mkdtemp(prefix="its-")
    try:
        pem, sig, src = os.path.join(d, "k.pem"), os.path.join(d, "s.bin"), os.path.join(d, "c.py")
        with open(pem, "w") as f:
            f.write(PUBLIC_KEY_PEM)
        with open(sig, "wb") as f:
            f.write(base64.b64decode(sig_b64.strip()))
        with open(src, "wb") as f:
            f.write(data)
        rc, out = run(["openssl", "dgst", "-sha256", "-verify", pem, "-signature", sig, src], 20)
        return rc == 0 and "Verified OK" in (out or "")
    finally:
        shutil.rmtree(d, ignore_errors=True)


def self_update(cfg, dry_run):
    try:
        base = re.sub(r"/api/agent/report$", "/agent/", cfg["reportUrl"])
        src = http_get(base + "collector.py")
        sig = http_get(base + "collector.py.sig").decode("ascii", "replace")
        ok = verify_signature(src, sig)
        if ok is None:
            return "no-openssl"
        if not ok:
            return "signature-invalid"
        text = src.decode("utf-8", "replace")
        m = re.search(r"^# collector-version:\s*(\d+)", text, re.M)
        remote = int(m.group(1)) if m else 0
        if remote <= COLLECTOR_VERSION:
            return "uptodate"
        try:
            compile(text, "collector.py", "exec")
        except SyntaxError:
            return "syntax-error"
        if dry_run:
            return "update-available-%d" % remote
        me = os.path.abspath(__file__)
        tmp = me + ".new"
        with open(tmp, "wb") as f:
            f.write(src)
        os.chmod(tmp, 0o700)
        try:
            shutil.copy2(me, me + ".bak")
        except Exception:
            pass
        os.replace(tmp, me)
        # sofort mit der neuen Version laufen (Bericht nicht erst beim naechsten Lauf)
        os.execv(sys.executable, [sys.executable, me])
    except SystemExit:
        raise
    except Exception:
        return "check-failed"
    return "uptodate"


# --- Hauptprogramm -------------------------------------------------------------------------

def build_payload(cfg, update_status, started):
    profile = detect_profile()
    osd = safe("os", lambda: collect_os(profile), {"osName": platform.platform(), "osBuild": platform.release(), "versionId": None, "id": None})
    uptime_h, last_boot = safe("uptime", collect_uptime, (None, None))
    cpu_model, cpu_load = safe("cpu", collect_cpu, (None, None))
    ram_total, ram_used = parse_meminfo(read("/proc/meminfo"))
    disks = safe("disks", lambda: collect_disks(profile), [])
    failed = safe("units", collect_failed_units, [])
    roles = safe("roles", collect_roles, []) if profile != "synology" else []
    updates = safe("updates", lambda: collect_updates(profile), None)
    reboot = safe("reboot", collect_reboot, {"required": False, "packages": []})
    raid = safe("raid", collect_raid, [])
    smart = safe("smart", collect_smart, None)
    docker = safe("docker", collect_docker, None)
    certs = safe("certs", collect_certs, [])
    security = safe("security", lambda: collect_security(profile), {})
    backup = safe("backup", lambda: collect_backup(cfg), None)
    software = safe("software", lambda: collect_software(profile), [])
    hardware = safe("hardware", collect_hardware, {})
    inodes = safe("inodes", collect_inodes, [])
    temp = safe("temperature", collect_temperature, None)

    linux = {"profile": profile, "raid": raid, "smart": smart, "docker": docker, "certs": certs, "security": security,
             "inodes": inodes, "cpuTempC": temp, "reboot": reboot, "roles": roles,
             "aptCacheAgeDays": updates.get("cacheAgeDays") if updates else None}
    if profile == "raspberrypi":
        linux["pi"] = safe("pi", collect_pi, None)
    if profile == "synology":
        linux["synology"] = safe("synology", collect_synology, None)

    return {
        "collectorVersion": COLLECTOR_VERSION, "osFamily": "linux", "updateStatus": update_status,
        "collectSeconds": round(time.time() - started, 0), "timing": TIMING,
        "hostname": socket.gethostname(),
        "osName": osd["osName"], "osBuild": osd["osBuild"],
        "osInfo": {"displayVersion": osd["versionId"], "edition": osd["id"], "releaseId": None, "ubr": None, "installDate": None},
        "uptimeHours": uptime_h, "lastBoot": last_boot, "cpuModel": cpu_model, "cpuLoadPct": cpu_load,
        "ramTotalGb": ram_total, "ramUsedGb": ram_used, "disks": disks,
        "pendingUpdates": updates["count"] if updates else None, "updates": updates,
        "pendingReboot": reboot.get("required") if reboot else None,
        "servicesCritical": failed, "roles": roles, "hardware": hardware, "backup": backup,
        "linux": linux, "software": software, "network": [],
    }


def migrate_host(cfg):
    """Umzug auf den Agent-Host (2026-10-22): crm.it-villach.at -> managed.it-villach.at, nur wenn erreichbar."""
    old = cfg.get("reportUrl", "")
    if not old.startswith("https://crm.it-villach.at/"):
        return
    new = "https://managed.it-villach.at/" + old[len("https://crm.it-villach.at/"):]
    try:
        probe = re.sub(r"/api/agent/report$", "/agent/collector.py.sig", new)
        with urllib.request.urlopen(probe, timeout=15) as r:
            r.read(64)
        cfg["reportUrl"] = new
        fd = os.open(CONFIG_PATH + ".tmp", os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            json.dump(cfg, f, indent=2)
        os.replace(CONFIG_PATH + ".tmp", CONFIG_PATH)
    except Exception:
        pass


def main():
    dry_run = "--dry-run" in sys.argv
    started = time.time()
    try:
        with open(CONFIG_PATH) as f:
            cfg = json.load(f)
    except Exception as e:
        print("Config nicht lesbar: %s (%s)" % (CONFIG_PATH, e))
        return 1
    if not cfg.get("token") or not cfg.get("reportUrl"):
        print("Config unvollstaendig")
        return 1
    migrate_host(cfg)

    status = self_update(cfg, dry_run) if "--no-update" not in sys.argv else "skipped"
    payload = build_payload(cfg, status, started)

    if dry_run:
        print(json.dumps(payload, indent=2, sort_keys=True))
        return 0

    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(cfg["reportUrl"], data=body, method="POST", headers={
        "Authorization": "Bearer " + cfg["token"], "Content-Type": "application/json; charset=utf-8",
        "User-Agent": "its-linux-collector/%d" % COLLECTOR_VERSION})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            r.read()
    except urllib.error.HTTPError as e:
        print("Report abgelehnt: HTTP %d" % e.code)
        return 2
    except Exception as e:
        print("Report nicht gesendet: %s" % e)
        return 2
    print("Report gesendet: %s -> %s" % (payload["hostname"], cfg["reportUrl"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
