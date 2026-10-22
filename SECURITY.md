# Sicherheit

## Lücken melden

Bitte Sicherheitslücken **nicht** als öffentliches Issue melden, sondern per Mail an `support@it-villach.at` (Betreff „Security IT-Solutions Agent"). Wir antworten in der Regel innerhalb von zwei Werktagen.

## Wie Updates geschützt sind

1. Jede Collector-Datei (`collector.ps1`, `collector.py`) wird von IT-Solutions mit einem privaten RSA-3072-Schlüssel signiert (PKCS#1 v1.5, SHA-256). Die Signatur liegt als Base64 in der `.sig`-Datei daneben.
2. Der Agent lädt ein Update nur, wenn die Versionsnummer höher ist **und** die Signatur mit dem im Code eingebetteten öffentlichen Schlüssel übereinstimmt.
3. Die Installer prüfen dieselbe Signatur, bevor sie etwas einrichten.

Prüfen von Hand (Linux):

```sh
openssl base64 -d -A -in collector.py.sig -out sig.bin
openssl dgst -sha256 -verify key.pem -signature sig.bin collector.py
```

`key.pem` ist der öffentliche Schlüssel aus `linux/install-linux.sh`.

## Was der Agent nicht tut

Er öffnet keine eingehenden Ports, liest keine Benutzerdaten und führt keine frei formulierten Befehle aus. Fernbefehle sind auf eine feste Liste beschränkt (Windows-Updates, Programm-Updates, neu einlesen, Neustart) und müssen im CRM je Gerät freigegeben sein.
