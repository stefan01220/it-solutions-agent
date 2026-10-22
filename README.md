# IT-Solutions Agent

Der Agent, mit dem **IT-Solutions – Stefan Regenfelder IT, Villach** die Rechner und Server seiner Wartungskunden im Blick behält.
Er liest den Zustand eines Systems (Updates, Speicher, Virenschutz, Sicherung, Dienste) und meldet ihn alle 2 Stunden an das IT-Solutions-CRM unter `crm.it-villach.at`.

Der Quellcode ist offen, damit jeder Kunde nachsehen kann, was auf seinem Rechner läuft.

## Was der Agent tut

- **Liest** Systemdaten: Betriebssystem und Patchstand, offene Updates, Festplatten und Speicherplatz, RAID/SMART (Linux), Virenschutz (ESET, Defender), Sicherung (Windows Server Backup, Veeam), gestoppte Dienste, installierte Programme, Firewall-, RDP- und SMB-Einstellungen.
- **Meldet** diese Daten per HTTPS an das CRM. Jeder Rechner hat ein eigenes Token, das im CRM jederzeit widerrufen werden kann.
- **Ändert nichts** am System, mit einer Ausnahme: Fernbefehle (Windows-Updates installieren, Programme aktualisieren, Neustart) führt er nur aus, wenn sie im CRM **für genau dieses Gerät freigegeben** wurden. Standard ist aus, und jede Auslösung wird protokolliert.
- **Liest keine** Dokumente, Mails, Passwörter oder Browserdaten.

## Aufbau

| Pfad | Inhalt |
|---|---|
| `windows/collector.ps1` | Collector für Windows (PowerShell 5.1+), läuft als geplanter Task unter SYSTEM |
| `windows/it-solutions-agent-setup.ps1` | Installer für einen Rechner |
| `windows/install-fleet.ps1` | Installer für alle Server einer Windows-Domäne (vom Domänencontroller aus, per Kerberos, ohne Passwörter) |
| `linux/collector.py` | Collector für Linux, Synology DSM und Raspberry Pi (Python 3, nur Standardbibliothek) |
| `linux/install-linux.sh` | Installer für Linux und Synology |
| `*.sig` | Signaturen der Collector-Dateien (siehe unten) |
| `tray/` | Windows-Programm mit Symbol im Infobereich (in Arbeit) |

## Installation

Installiert wird mit einem **Installationscode**, den IT-Solutions im CRM für einen Kunden erzeugt (24 Stunden gültig).
Kundennummer und Code auf **https://crm.it-villach.at/agents** eingeben; die Seite zeigt den fertigen Befehl für Windows, Windows-Domäne oder Linux/Synology.

## Sicherheit

- **Signierte Updates:** Der Collector aktualisiert sich selbst, aber nur mit einer gültigen RSA-3072-Signatur (SHA-256). Der öffentliche Schlüssel steht im Code; der private Schlüssel liegt ausschließlich bei IT-Solutions. Eine manipulierte Datei wird nicht installiert.
- **Token-Schutz:** Die Konfiguration mit dem Token ist unter Windows nur für SYSTEM und Administratoren lesbar, unter Linux nur für root.
- **Nur ausgehend:** Der Agent öffnet keine Ports. Er spricht ausschließlich per HTTPS mit `crm.it-villach.at`.
- Sicherheitslücken bitte an `support@it-villach.at` melden (siehe [SECURITY.md](SECURITY.md)).

## Lizenz

[MIT](LICENSE) – © 2026 Stefan Regenfelder, IT-Solutions, Villach.

## Kontakt

IT-Solutions – Stefan Regenfelder IT
Dr.-Schärf-Straße 7a, 9524 Villach-St. Magdalen
+43 676 521 67 75 · support@it-villach.at · www.it-villach.at
