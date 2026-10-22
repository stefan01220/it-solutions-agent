# IT-Solutions Asset-Collector - Setup-Installer
# ================================================
# Einmal pro Kundenrechner ALS ADMINISTRATOR ausfuehren (Windows PowerShell 5.1 oder neuer).
# Richtet alles selbst ein:
#   1. Enrollment beim CRM  -> Agent-Token   (Kundennummer + kurzlebiger Installationscode)
#   2. Collector + Config nach C:\ProgramData\IT-Solutions\   (Signatur wird vorher geprueft)
#   3. Scheduled Task (SYSTEM, alle 2 h, ueberlebt Reboot)
#   4. Erster Scan sofort - und Kontrolle, dass der Bericht im CRM angekommen ist
#
# Der Collector liest Systemdaten und sendet sie ans CRM. Fernbefehle (Updates) fuehrt er nur aus,
# wenn sie im CRM fuer dieses Geraet freigegeben sind (Opt-in, Default aus).
#
# Kurzform (ohne Datei, Admin-PowerShell) - den fertigen Befehl liefert crm.it-villach.at/agents:
#   [Net.ServicePointManager]::SecurityProtocol='Tls12'; & ([scriptblock]::Create((irm https://crm.it-villach.at/agent/it-solutions-agent-setup.ps1))) -CustomerNo 10033 -Code K7M2-9QXA
# Altweg mit Enrollment-Key bzw. vorab angelegtem Agent-Token:
#   ... -CustomerNo 10033 -EnrollmentKey enr_...      oder      ... -Token ita_...
# -Force installiert neu und legt ein NEUES Token an (sonst bleibt ein vorhandenes Token erhalten).
param([string]$CustomerNo, [string]$Code, [string]$EnrollmentKey, [string]$Token, [switch]$Force)

$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 nutzt per Default TLS 1.0/1.1 - moderne Server lehnen
# das ab. TLS 1.2 erzwingen, sonst scheitert jeder HTTPS-Call.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$CrmBase     = "https://crm.it-villach.at"
# Oeffentlicher Schluessel zur Pruefung der Collector-Signatur (identisch mit dem im Collector).
$SigningKeyXml = '<RSAKeyValue><Modulus>uAxspiYovBEcA7viyD9y1pDu+gw4n/ASAGAgLZHCURTOYeD6Az/Udz8JavYAf7SQmISWfe28nVhzskliDyYyy23RiS9NpEnS0tqMXBNr+tKz29N4wOhTdyhqsIyAulxN9rK+VA+WyF9lNBmTI94OvUJ3qeebgrJpHThdVILVVhqQVwUjTJvq2ZQDoCZcofdGYVWowBRyCewoW9KMnDsoEGXy/1/nuxKRB4GicQMo4P5+HMWbNQFf+c5n+qGKE3N4TdPUHQOE38i25y5zgJ8e88Olqhla2cKfhU5wwJhZ5JgvR/R6BSt0QAxdXmkOLtfjH8SWIyHLdaUXRQyWPo4cb2bDaUsEbL3R7IapkqejvGwQJMsamoGU9xVUP0+Cj/UlpVYWQ1wSmFnIZS02LG7bpAIfkeUEhc/3/iu2nJ/PV2dROIHZ1rlxNBwcmWb0QnaYEYHd1IGHdVCMBpx2ggmgChphkbhhaGd3r84ZRt3SmIefwmx025R/CgSX/6Fvsqih</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'
$InstallDir  = "C:\ProgramData\IT-Solutions"
$TaskName    = "IT-Solutions Asset-Collector"
$LogFile     = Join-Path $InstallDir "setup.log"

function Log($msg) { try { if (Test-Path $InstallDir) { Add-Content -Path $LogFile -Value ("{0} {1}" -f (Get-Date -Format s), $msg) } } catch {} }
function Fail($msg) {
  Write-Host ""
  Write-Host "FEHLER: $msg" -ForegroundColor Red
  Log "FEHLER: $msg"
  exit 1
}

# --- Administrator? (Selbst-Erhoehung nur, wenn als Datei gestartet; aus dem Speicher gibt es keinen Pfad) ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  if ($PSCommandPath) {
    Write-Host "Starte mit Administrator-Rechten neu..." -ForegroundColor Yellow
    $passArgs = ""
    if ($CustomerNo)    { $passArgs += " -CustomerNo `"$CustomerNo`"" }
    if ($Code)          { $passArgs += " -Code `"$Code`"" }
    if ($EnrollmentKey) { $passArgs += " -EnrollmentKey `"$EnrollmentKey`"" }
    if ($Token)         { $passArgs += " -Token `"$Token`"" }
    if ($Force)         { $passArgs += " -Force" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"$passArgs"
    exit
  }
  Fail "Bitte in einer ADMINISTRATOR-PowerShell ausfuehren (Start -> PowerShell -> Rechtsklick -> Als Administrator ausfuehren)."
}

Write-Host ""
Write-Host "=== IT-Solutions Asset-Collector - Setup ===" -ForegroundColor Cyan
Write-Host ""

# --- Vorab-Pruefungen ---
if ($PSVersionTable.PSVersion.Major -lt 5) { Fail "Windows PowerShell 5.1 oder neuer wird benoetigt (gefunden: $($PSVersionTable.PSVersion))." }
try {
  $cs = Get-CimInstance Win32_ComputerSystem
  if ($cs.DomainRole -ge 4) {
    Write-Host "Hinweis: Das ist ein Domaenencontroller. Mit install-fleet.ps1 (Befehl unter crm.it-villach.at/agents) kommen alle Server der Domaene auf einmal." -ForegroundColor Yellow
  }
} catch {}

if (-not $Token) {
  if (-not $CustomerNo) { $CustomerNo = Read-Host "Kundennummer" }
  if (-not $Code -and -not $EnrollmentKey) { $Code = Read-Host "Installationscode (XXXX-XXXX)" }
  $CustomerNo = "$CustomerNo".Trim()
  if ($CustomerNo -notmatch '^\d{3,8}$') { Fail "Die Kundennummer '$CustomerNo' ist keine Zahl (Platzhalter nicht ersetzt?)." }
  if ($Code) {
    $Code = "$Code".Trim().ToUpper()
    if ($Code -notmatch '^[2-9A-HJKMNP-Z]{4}-?[2-9A-HJKMNP-Z]{4}$') { Fail "Der Installationscode '$Code' hat nicht das Format XXXX-XXXX (ohne 0, O, 1, I, L)." }
  } else {
    $EnrollmentKey = "$EnrollmentKey".Trim()
    if ($EnrollmentKey -notmatch '^enr_[0-9a-f]{20,}$') { Fail "Der Enrollment-Key sieht nicht richtig aus (erwartet enr_ + Zeichen). Besser den Installationscode verwenden: crm.it-villach.at/agents" }
  }
}

Write-Host "Pruefe Verbindung zum CRM ..." -ForegroundColor Cyan
try {
  $null = Invoke-WebRequest -Uri "$CrmBase/agent/collector.ps1.sig" -UseBasicParsing -TimeoutSec 20
} catch {
  Fail "Das CRM ($CrmBase) ist von diesem Rechner nicht erreichbar: $($_.Exception.Message). Internet/Proxy/Firewall (ausgehend HTTPS 443) pruefen."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Log "Setup gestartet auf $($env:COMPUTERNAME) (PowerShell $($PSVersionTable.PSVersion))"

# --- 1. Enrollment ---
Write-Host "`n[1/4] Anmeldung beim CRM..." -ForegroundColor Cyan
$cfgPath = Join-Path $InstallDir "agent-config.json"
$resp = $null
if ($Token) {
  # Token wurde vorab im CRM angelegt - kein Enrollment noetig.
  $resp = [pscustomobject]@{ ok = $true; token = $Token; reportUrl = "/api/agent/report"; customerName = "(Token vorab im CRM angelegt)" }
} elseif ((Test-Path $cfgPath) -and -not $Force) {
  # Schon installiert: vorhandenes Token behalten (sonst entsteht bei jedem Lauf ein doppelter Eintrag im CRM).
  try {
    $old = Get-Content $cfgPath -Raw | ConvertFrom-Json
    if ($old.token) {
      $u = [uri]$old.reportUrl
      $resp = [pscustomobject]@{ ok = $true; token = $old.token; reportUrl = $u.AbsolutePath; customerName = "(bereits angemeldet)" }
      Write-Host "      Dieser Rechner ist schon angemeldet - vorhandenes Token bleibt (mit -Force neu anmelden)." -ForegroundColor Yellow
    }
  } catch {}
}
if (-not $resp) {
  $payload = @{ customerNo = $CustomerNo; hostname = $env:COMPUTERNAME }
  if ($Code) { $payload.installCode = $Code } else { $payload.enrollmentKey = $EnrollmentKey }
  $lastErr = $null
  foreach ($try in 1..3) {
    try {
      $resp = Invoke-RestMethod -Method Post -Uri "$CrmBase/api/agent/enroll" -ContentType "application/json" -TimeoutSec 30 -Body ($payload | ConvertTo-Json)
      break
    } catch {
      $lastErr = $_
      $status = 0
      try { $status = [int]$_.Exception.Response.StatusCode } catch {}
      if ($status -ge 400 -and $status -lt 500) {
        # Inhaltliche Ablehnung (falscher/abgelaufener Code ...): nicht wiederholen, Meldung des CRM zeigen
        $detail = $_.Exception.Message
        try { $detail = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() | ConvertFrom-Json | Select-Object -ExpandProperty error } catch {}
        Fail "Anmeldung abgelehnt: $detail"
      }
      Write-Host "      Versuch $try von 3 fehlgeschlagen ($($_.Exception.Message))" -ForegroundColor Yellow
      Start-Sleep -Seconds (3 * $try)
    }
  }
  if (-not $resp) { Fail "Anmeldung beim CRM nicht moeglich: $($lastErr.Exception.Message)" }
}
if (-not $resp.ok -or -not $resp.token) { Fail "Das CRM lieferte keinen Token." }
Write-Host "      Kunde: $($resp.customerName)" -ForegroundColor Green
Log "Enrollment ok ($($resp.customerName))"

# --- 2. Dateien ablegen ---
Write-Host "[2/4] Collector installieren nach $InstallDir ..." -ForegroundColor Cyan
# Collector + Signatur laden und pruefen, bevor irgendetwas installiert wird.
$wc = New-Object System.Net.WebClient
$collBytes = $wc.DownloadData("$CrmBase/agent/collector.ps1")
$sigText = ([System.Text.Encoding]::ASCII.GetString($wc.DownloadData("$CrmBase/agent/collector.ps1.sig"))).Trim()
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString($SigningKeyXml)
if (-not $rsa.VerifyData($collBytes, "SHA256", [Convert]::FromBase64String($sigText))) {
  Fail "Signatur des Collectors ungueltig - Installation abgebrochen."
}
[System.IO.File]::WriteAllBytes((Join-Path $InstallDir "collector.ps1"), $collBytes)

@{
  token     = $resp.token
  reportUrl = "$CrmBase$($resp.reportUrl)"
} | ConvertTo-Json | Set-Content -Path $cfgPath -Encoding UTF8

# ACL: nur SYSTEM + Administratoren duerfen lesen (Token schuetzen)
$acl = Get-Acl $InstallDir
$acl.SetAccessRuleProtection($true, $false)
foreach ($id in @("SYSTEM", "Administratoren", "Administrators")) {
  try {
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
      $id, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
  } catch {}
}
Set-Acl -Path $InstallDir -AclObject $acl

# --- 3. Scheduled Task ---
Write-Host "[3/4] Scheduled Task einrichten..." -ForegroundColor Cyan
$action  = New-ScheduledTaskAction -Execute "powershell.exe" `
  -Argument "-NonInteractive -ExecutionPolicy Bypass -File `"$InstallDir\collector.ps1`""
# RepetitionDuration: TimeSpan::MaxValue erzeugt P99999999D... - die Task-XML
# lehnt das ab. Endliche, grosse Dauer (10 Jahre) = praktisch dauerhaft.
$trigger1 = New-ScheduledTaskTrigger -Once -At (Get-Date) `
  -RepetitionInterval (New-TimeSpan -Hours 2) -RepetitionDuration (New-TimeSpan -Days 3650)
$trigger2 = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($trigger1, $trigger2) `
  -Principal $principal -Settings $settings `
  -Description "IT-Solutions Asset-Collector - sendet Server-Telemetrie ans CRM" | Out-Null
if (-not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) { Fail "Der geplante Task konnte nicht angelegt werden." }
Log "Task angelegt"

# --- 4. Erster Scan + Kontrolle ---
Write-Host "[4/4] Erster Scan..." -ForegroundColor Cyan
$sent = $false
try {
  $out = (& powershell.exe -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $InstallDir "collector.ps1") 2>&1 | Out-String)
  Log ("Erster Scan: " + $out.Trim())
  if ($out -match "Report gesendet") { $sent = $true } else { Write-Host $out.Trim() -ForegroundColor Yellow }
} catch {
  Log ("Erster Scan Fehler: " + $_.Exception.Message)
  Write-Host "      $($_.Exception.Message)" -ForegroundColor Yellow
}

$trayExe = Join-Path $env:ProgramFiles "IT-Solutions\IT-Solutions-Agent.exe"
if ($sent -and [Environment]::UserInteractive -and (Test-Path $trayExe) -and -not (Get-Process -Name "IT-Solutions-Agent" -ErrorAction SilentlyContinue)) {
  try { Start-Process -FilePath $trayExe; Write-Host "      Symbol im Infobereich gestartet." -ForegroundColor Green } catch {}
}

Write-Host ""
if ($sent) {
  Write-Host "=== Fertig: Bericht ist im CRM angekommen ===" -ForegroundColor Green
  Write-Host "Rechner '$($env:COMPUTERNAME)' ist jetzt im CRM unter Kunde '$($resp.customerName)' sichtbar (Server-Status)." -ForegroundColor Green
  Write-Host "Der Collector meldet sich alle 2 Stunden. Token-Verwaltung: $CrmBase/settings/agents"
  exit 0
}
Write-Host "=== Installiert, aber der erste Bericht ist NICHT angekommen ===" -ForegroundColor Red
Write-Host "Der Task laeuft im 2-Stunden-Takt weiter. Fehlerursache siehe oben bzw. $LogFile." -ForegroundColor Yellow
Write-Host "Haeufig: Proxy/Firewall blockiert ausgehend HTTPS zu crm.it-villach.at oder falsche Systemzeit." -ForegroundColor Yellow
exit 2
