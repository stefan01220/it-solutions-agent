#!/bin/sh
# IT-Solutions Asset-Collector (Linux) - Installer
# =================================================
# Als root ausfuehren. Richtet den Linux-Collector ein (nur lesend, meldet alle 2 Stunden ans CRM):
#   sudo sh install-linux.sh --token ita_...                          (Token vorab im CRM angelegt)
#   sudo sh install-linux.sh --customer-no 10057 --code K7M2-9QXA              (Installationscode, Standard - managed.it-villach.at/agents)
#   sudo sh install-linux.sh --customer-no 10057 --enrollment-key enr_...      (Altweg)
#   sudo sh install-linux.sh --uninstall
#
# Schnellstart:
#   curl -fsSL https://managed.it-villach.at/agent/install-linux.sh -o /tmp/its-install.sh && sudo sh /tmp/its-install.sh --customer-no 10057 --code K7M2-9QXA
#
# Voraussetzungen: python3, openssl, curl oder wget. Der Collector wird vor der Installation gegen den
# eingebetteten oeffentlichen Schluessel geprueft (RSA-SHA256), Updates ebenso.
set -eu

CRM_BASE="https://managed.it-villach.at"
INSTALL_DIR="/opt/it-solutions-agent"
CONF_DIR="/etc/it-solutions-agent"
UNIT_DIR="/etc/systemd/system"

TOKEN=""; CUSTOMER_NO=""; ENROLL_KEY=""; CODE=""; UNINSTALL=0; FORCE=0; REUSED=0
while [ $# -gt 0 ]; do
  case "$1" in
    --token) TOKEN="$2"; shift 2 ;;
    --customer-no) CUSTOMER_NO="$2"; shift 2 ;;
    --enrollment-key) ENROLL_KEY="$2"; shift 2 ;;
    --code) CODE="$2"; shift 2 ;;
    --force) FORCE=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    *) echo "Unbekannte Option: $1" >&2; exit 2 ;;
  esac
done

LOGFILE=/tmp/its-install.log
: > "$LOGFILE" 2>/dev/null || LOGFILE=/dev/null
say() { printf '%s\n' "$*"; printf '%s\n' "$*" >> "$LOGFILE" 2>/dev/null || true; }
# Fehler: Meldung + Exit-Code 1; zusaetzlich (soweit moeglich) Diagnosetext ans CRM, weil z.B. das Synology-CMS keine Ausgabe zeigt.
die() {
  printf 'FEHLER: %s\n' "$*" >&2; printf 'FEHLER: %s\n' "$*" >> "$LOGFILE" 2>/dev/null || true
  send_diag "$*" || true
  exit 1
}
# Diagnose ans CRM schicken (nur mit vorhandenem Token + python3 + erreichbarem CRM).
send_diag() {
  [ -n "${TOKEN:-}" ] && command -v python3 >/dev/null 2>&1 || return 1
  python3 - "$CRM_BASE/api/agent/report" "$TOKEN" "$LOGFILE" "$1" <<'PYDIAG' >/dev/null 2>&1 || return 1
import json, socket, sys, urllib.request
url, token, logf, why = sys.argv[1:5]
try:
    txt = open(logf, errors="replace").read()[-5000:]
except Exception:
    txt = ""
body = json.dumps({"hostname": socket.gethostname(), "osName": "DIAGNOSE (Installer)", "diagnostic": "Installer-Fehler: " + why + "\n" + txt}).encode()
req = urllib.request.Request(url, data=body, method="POST",
                             headers={"Authorization": "Bearer " + token, "Content-Type": "application/json; charset=utf-8"})
urllib.request.urlopen(req, timeout=30).read()
PYDIAG
}

[ "$(id -u)" -eq 0 ] || die "Bitte als root ausfuehren (sudo)."

if [ "$UNINSTALL" -eq 1 ]; then
  command -v systemctl >/dev/null 2>&1 && { systemctl disable --now it-solutions-agent.timer 2>/dev/null || true; }
  rm -f "$UNIT_DIR/it-solutions-agent.service" "$UNIT_DIR/it-solutions-agent.timer" /etc/cron.d/it-solutions-agent
  command -v systemctl >/dev/null 2>&1 && systemctl daemon-reload 2>/dev/null || true
  rm -rf "$INSTALL_DIR"
  say "Collector entfernt (Konfiguration und Token liegen noch in $CONF_DIR - bei Bedarf loeschen und das Token im CRM widerrufen)."
  exit 0
fi

command -v python3 >/dev/null 2>&1 || die "python3 fehlt."
command -v openssl >/dev/null 2>&1 || die "openssl fehlt."
if command -v curl >/dev/null 2>&1; then
  fetch() { curl -fsSL --max-time 60 "$1" -o "$2"; }
elif command -v wget >/dev/null 2>&1; then
  fetch() { wget -q -T 60 -O "$2" "$1"; }
else
  die "curl oder wget fehlt."
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

cat > "$TMP/key.pem" <<'PEMEOF'
-----BEGIN PUBLIC KEY-----
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
PEMEOF

say ""
say "=== IT-Solutions Asset-Collector (Linux) - Setup ==="

# --- 1. Token ---
# Schon installiert? Dann Token behalten (sonst entsteht bei jedem Lauf ein doppelter Eintrag im CRM); --force meldet neu an.
if [ -z "$TOKEN" ] && [ "$FORCE" -eq 0 ] && [ -f "$CONF_DIR/config.json" ]; then
  OLD_TOKEN="$(python3 -c "import json,sys; print(json.load(open(sys.argv[1])).get('token',''))" "$CONF_DIR/config.json" 2>/dev/null || true)"
  if [ -n "$OLD_TOKEN" ]; then
    TOKEN="$OLD_TOKEN"; REUSED=1
    say "[1/4] Dieser Rechner ist schon angemeldet - vorhandenes Token bleibt (mit --force neu anmelden)."
  fi
fi
if [ -z "$TOKEN" ]; then
  [ -n "$CUSTOMER_NO" ] && { [ -n "$CODE" ] || [ -n "$ENROLL_KEY" ]; } || die "Bitte --customer-no und --code angeben (Code: managed.it-villach.at/agents), alternativ --token."
  case "$CUSTOMER_NO" in *[!0-9]*|"") die "Die Kundennummer '$CUSTOMER_NO' ist keine Zahl." ;; esac
  say "[1/4] Anmeldung beim CRM ..."
  python3 - "$CRM_BASE" "$CUSTOMER_NO" "$CODE" "$ENROLL_KEY" "$(hostname)" > "$TMP/enroll.json" 2>"$TMP/enroll.err" <<'PYENROLL' || ENROLL_FAIL=1
import json, sys, urllib.request, urllib.error
base, no, code, key, host = sys.argv[1:6]
payload = {"customerNo": no, "hostname": host}
if code:
    payload["installCode"] = code
else:
    payload["enrollmentKey"] = key
req = urllib.request.Request(base + "/api/agent/enroll", data=json.dumps(payload).encode(),
                             headers={"Content-Type": "application/json"}, method="POST")
try:
    print(urllib.request.urlopen(req, timeout=30).read().decode())
except urllib.error.HTTPError as e:
    try:
        msg = json.loads(e.read().decode()).get("error", "")
    except Exception:
        msg = ""
    sys.stderr.write("CRM lehnt ab (HTTP %d): %s\n" % (e.code, msg))
    sys.exit(1)
PYENROLL
  if [ "${ENROLL_FAIL:-0}" -eq 1 ]; then
    ERRTXT="$(tail -n 3 "$TMP/enroll.err" 2>/dev/null)"
    die "Anmeldung beim CRM fehlgeschlagen: ${ERRTXT:-keine Antwort (Internet/Firewall pruefen)}"
  fi
  TOKEN="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['token'] if d.get('ok') else '')" "$TMP/enroll.json")"
  [ -n "$TOKEN" ] || die "Das CRM hat keinen Token geliefert."
else
  [ "${REUSED:-0}" -eq 1 ] || say "[1/4] Vorab angelegtes Token wird verwendet."
fi

# --- 2. Collector laden und pruefen ---
say "[2/4] Collector laden und Signatur pruefen ..."
fetch "$CRM_BASE/agent/collector.py" "$TMP/collector.py" || die "Download collector.py fehlgeschlagen."
fetch "$CRM_BASE/agent/collector.py.sig" "$TMP/collector.py.sig" || die "Download collector.py.sig fehlgeschlagen."
openssl base64 -d -A -in "$TMP/collector.py.sig" -out "$TMP/sig.bin" || die "Signatur nicht lesbar."
openssl dgst -sha256 -verify "$TMP/key.pem" -signature "$TMP/sig.bin" "$TMP/collector.py" >/dev/null 2>&1 \
  || die "Signatur des Collectors ungueltig - Installation abgebrochen."
python3 -c "import sys; compile(open(sys.argv[1]).read(), 'collector.py', 'exec')" "$TMP/collector.py" || die "collector.py ist kein gueltiges Python."

# --- 3. Installieren ---
say "[3/4] Installieren nach $INSTALL_DIR ..."
mkdir -p "$INSTALL_DIR" "$CONF_DIR"
chmod 700 "$INSTALL_DIR" "$CONF_DIR"
cp "$TMP/collector.py" "$INSTALL_DIR/collector.py"
chmod 700 "$INSTALL_DIR/collector.py"
if [ "${REUSED:-0}" -eq 0 ]; then
  python3 - "$CONF_DIR/config.json" "$TOKEN" "$CRM_BASE/api/agent/report" <<'PYEOF'
import json, os, sys
path, token, url = sys.argv[1:4]
old = {}
try:
    old = json.load(open(path))
except Exception:
    pass
old.update({"token": token, "reportUrl": url})
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, "w") as f:
    json.dump(old, f, indent=2)
PYEOF
fi
chmod 600 "$CONF_DIR/config.json"

# --- 4. Zeitplan ---
say "[4/4] Zeitplan einrichten (alle 2 Stunden) ..."
SCHEDULE="systemd"
if command -v systemctl >/dev/null 2>&1 && [ -d "$UNIT_DIR" ]; then
  cat > "$UNIT_DIR/it-solutions-agent.service" <<'UNITEOF'
[Unit]
Description=IT-Solutions Asset-Collector (Linux, nur lesend)
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/bin/env python3 /opt/it-solutions-agent/collector.py
Nice=10
TimeoutStartSec=15min
UNITEOF
  cat > "$UNIT_DIR/it-solutions-agent.timer" <<'UNITEOF'
[Unit]
Description=IT-Solutions Asset-Collector (alle 2 Stunden)

[Timer]
OnBootSec=3min
OnUnitActiveSec=2h
RandomizedDelaySec=5min

[Install]
WantedBy=timers.target
UNITEOF
  if systemctl daemon-reload >>"$LOGFILE" 2>&1 && systemctl enable --now it-solutions-agent.timer >>"$LOGFILE" 2>&1; then
    say "   systemd-Timer aktiv."
  else
    SCHEDULE="cron"; say "   (systemd-Timer nicht aktivierbar - verwende cron)"
  fi
else
  SCHEDULE="cron"; say "   (kein systemd gefunden - verwende cron)"
fi
if [ "$SCHEDULE" = "cron" ]; then
  # Synology DSM liest /etc/crontab (Felder mit TABULATOR), normale Systeme /etc/cron.d.
  if [ -f /etc/synoinfo.conf ] && [ -f /etc/crontab ]; then
    grep -v 'it-solutions-agent' /etc/crontab > "$TMP/crontab.new" 2>/dev/null || true
    printf '17\t*/2\t*\t*\t*\troot\t/usr/bin/env python3 /opt/it-solutions-agent/collector.py >/dev/null 2>&1 # it-solutions-agent\n' >> "$TMP/crontab.new"
    cat "$TMP/crontab.new" > /etc/crontab
    for svc in crond synocrond; do { synoservice --restart "$svc" || systemctl restart "$svc"; } >/dev/null 2>&1 && break; done
    say "   Zeitplan in /etc/crontab eingetragen (Synology)."
  else
    printf '17 */2 * * * root /usr/bin/env python3 /opt/it-solutions-agent/collector.py >/dev/null 2>&1\n' > /etc/cron.d/it-solutions-agent
    say "   Zeitplan in /etc/cron.d eingetragen."
  fi
fi

say ""
say "Erster Scan ..."
SCAN_OUT="$(/usr/bin/env python3 "$INSTALL_DIR/collector.py" 2>&1)" && SCAN_RC=0 || SCAN_RC=$?
say "$SCAN_OUT"
say ""
if [ "$SCAN_RC" -eq 0 ] && printf '%s' "$SCAN_OUT" | grep -q "Report gesendet"; then
  say "=== Fertig: Bericht ist im CRM angekommen ==="
  say "Host '$(hostname)' meldet sich alle 2 Stunden ($SCHEDULE). Verwaltung: $CRM_BASE/settings/agents"
  exit 0
fi
printf 'FEHLER: Installiert, aber der erste Bericht ist NICHT angekommen (Exit-Code %s).\n' "$SCAN_RC" >&2
printf 'FEHLER: erster Scan fehlgeschlagen (Exit %s): %s\n' "$SCAN_RC" "$SCAN_OUT" >> "$LOGFILE" 2>/dev/null || true
send_diag "Erster Scan fehlgeschlagen (Exit $SCAN_RC)" || true
exit 2
