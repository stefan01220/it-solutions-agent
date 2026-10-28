# IT-Solutions Asset-Collector - Flotten-Installer (alle Server einer Windows-Domaene)
# ====================================================================================
# Auf einem DOMAENENCONTROLLER (oder Domaenenmitglied mit AD-Modul) in einer ERHOEHTEN PowerShell als Domaenen-Admin
# ausfuehren. Es werden KEINE Passwoerter eingegeben: die Verbindung zu den Servern laeuft mit deinem angemeldeten
# Domaenen-Konto (Kerberos, PowerShell-Remoting/WinRM).
#
# Den fertigen Befehl (Kundennummer + Installationscode) erzeugt das CRM unter managed.it-villach.at/agents bzw.
# Einstellungen -> Asset-Collector. Ohne Datei, direkt aus dem Speicher:
# 1) Trockenlauf (Standard) - listet nur, aendert nichts:
#      [Net.ServicePointManager]::SecurityProtocol='Tls12'; & ([scriptblock]::Create((irm https://managed.it-villach.at/agent/install-fleet.ps1))) -CustomerNo 10012 -Code K7M2-9QXA
# 2) Installieren (auf allen erreichbaren Servern, die noch keinen Collector haben): dieselbe Zeile mit -Install am Ende.
# Der Code gilt fuer 25 Installationen; statt -Code geht auch der alte Weg -EnrollmentKey enr_...
# Optional: -Computers SRV1,SRV2 (nur diese), -Exclude SRV3 (diese auslassen), -IncludeClients (auch Arbeitsplaetze),
#           -EnableSsh (richtet zusaetzlich den passwortlosen SSH-Wartungszugang ein, nur aus dem HQ erreichbar).
#
# Je Server wird der normale Installer geladen (https://managed.it-villach.at, Signaturpruefung des Collectors inklusive)
# und mit Kundennummer + Enrollment-Key ausgefuehrt; jeder Server bekommt sein eigenes Token.
# Server ausserhalb der Domaene (Workgroup) und ohne Internet werden am Ende aufgelistet - dort manuell installieren.
param(
  [Parameter(Mandatory = $true)][string]$CustomerNo,
  [string]$Code = "",
  [string]$EnrollmentKey = "",
  [switch]$Install,
  [string[]]$Computers = @(),
  [string[]]$Exclude = @(),
  [switch]$IncludeClients,
  # zusaetzlich den SSH-Wartungszugang (Schluessel, nur aus dem HQ) einrichten - siehe enable-ssh-access.ps1
  [switch]$EnableSsh,
  # Quellnetze fuer den SSH-Zugang (Standard siehe enable-ssh-access.ps1); noetig, wenn die Standort-Firewall NAT macht
  [string]$SshAllowFrom = ""
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$CrmBase = "https://managed.it-villach.at"
if (-not $Code -and -not $EnrollmentKey) { throw "Bitte -Code (Installationscode, siehe managed.it-villach.at/agents) angeben." }
if ($Code -and $Code -notmatch '^[2-9A-HJKMNP-Za-hjkmnp-z]{4}-?[2-9A-HJKMNP-Za-hjkmnp-z]{4}$') { throw "Der Installationscode '$Code' hat nicht das Format XXXX-XXXX." }
if ($CustomerNo -notmatch '^\d{3,8}$') { throw "Die Kundennummer '$CustomerNo' ist keine Zahl (Platzhalter nicht ersetzt?)." }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Bitte in einer ERHOEHTEN PowerShell (Als Administrator ausfuehren) starten." }

Write-Host ""
Write-Host "=== IT-Solutions Asset-Collector - Flotten-Installer ===" -ForegroundColor Cyan
if ($Install) { Write-Host "Modus: INSTALLIEREN" -ForegroundColor Yellow } else { Write-Host "Modus: Trockenlauf (es wird nichts installiert; mit -Install ausfuehren)" -ForegroundColor Green }

# --- Kandidaten aus dem Active Directory ---
if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) { throw "Das ActiveDirectory-PowerShell-Modul fehlt (auf einem Domaenencontroller vorhanden)." }
Import-Module ActiveDirectory
$all = @(Get-ADComputer -Filter { Enabled -eq $true -and OperatingSystem -like "*Windows*" } -Properties OperatingSystem, LastLogonDate)
if (-not $IncludeClients) { $all = @($all | Where-Object { $_.OperatingSystem -like "*Server*" }) }
if ($Computers.Count -gt 0) { $all = @($all | Where-Object { $Computers -contains $_.Name }) }
if ($Exclude.Count -gt 0)   { $all = @($all | Where-Object { $Exclude -notcontains $_.Name }) }
# Lange nicht angemeldete Konten (verwaiste Objekte) ueberspringen
$all = @($all | Where-Object { -not $_.LastLogonDate -or $_.LastLogonDate -gt (Get-Date).AddDays(-60) } | Sort-Object Name)
if ($all.Count -eq 0) { throw "Keine passenden Computer im Active Directory gefunden." }

$self = $env:COMPUTERNAME
$rows = @()
foreach ($c in $all) {
  $name = $c.Name
  $isSelf = ($name -eq $self)
  $reach = $isSelf
  if (-not $isSelf) { $reach = [bool](Test-WSMan -ComputerName $name -ErrorAction SilentlyContinue) }
  $installed = $false
  if ($isSelf) {
    $installed = (Test-Path "C:\ProgramData\IT-Solutions\Agent\collector.ps1") -or (Test-Path "C:\ProgramData\IT-Solutions\collector.ps1")     # lokal pruefen, kein WinRM noetig (Agent\ ab v19, davor direkt)
  } elseif ($reach) {
    try {
      $installed = [bool](Invoke-Command -ComputerName $name -ScriptBlock { (Test-Path "C:\ProgramData\IT-Solutions\Agent\collector.ps1") -or (Test-Path "C:\ProgramData\IT-Solutions\collector.ps1") } -ErrorAction Stop)
    } catch { $reach = $false }
  }
  $rows += [pscustomobject]@{
    Name = $name; System = ($c.OperatingSystem -replace "^Windows ", ""); Erreichbar = $reach
    Installiert = $installed; Self = $isSelf
  }
}

Write-Host ""
$rows | Select-Object Name, System, Erreichbar, Installiert | Format-Table -AutoSize

$todo = @($rows | Where-Object { $_.Erreichbar -and (-not $_.Installiert -or $EnableSsh) })
$noreach = @($rows | Where-Object { -not $_.Erreichbar })
Write-Host ("Zu installieren: {0} | bereits installiert: {1} | nicht erreichbar: {2}" -f $todo.Count, @($rows | Where-Object { $_.Installiert }).Count, $noreach.Count)
if (-not $Install) {
  Write-Host ""
  Write-Host "Trockenlauf beendet. Zum Installieren denselben Befehl mit -Install ausfuehren." -ForegroundColor Green
  exit 0
}

# --- Installation ---
$block = {
  param($base, $no, $code, $key, $doInstall, $doSsh, $allowFrom)
  $ErrorActionPreference = "Stop"
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $parts = @()
  if ($doInstall) {
    $f = Join-Path $env:TEMP "its-setup.ps1"
    try {
      (New-Object System.Net.WebClient).DownloadFile("$base/agent/it-solutions-agent-setup.ps1", $f)
      $setupArgs = @("-CustomerNo", $no)
      if ($code) { $setupArgs += @("-Code", $code) } else { $setupArgs += @("-EnrollmentKey", $key) }
      $out = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $f @setupArgs 2>&1 | Out-String
      if ($out -match "Bericht ist im CRM angekommen") { $parts += "Collector OK" }
      else { $tail = (($out.Trim() -split "`r?`n") | Select-Object -Last 3) -join " | "; $parts += "FEHLER Collector: $tail" }
    } catch { $parts += "FEHLER Collector: " + $_.Exception.Message }
    finally { Remove-Item $f -Force -ErrorAction SilentlyContinue }
  }
  if ($doSsh) {
    $g = Join-Path $env:TEMP "its-ssh.ps1"
    try {
      (New-Object System.Net.WebClient).DownloadFile("$base/agent/enable-ssh-access.ps1", $g)
      $sshArgs = @("-Apply"); if ($allowFrom) { $sshArgs += @("-AllowFrom", $allowFrom) }
      $out = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $g @sshArgs 2>&1 | Out-String
      if ($out -match "Fertig") { $parts += "SSH OK" }
      elseif ($out -match "UEBERSPRUNGEN") { $parts += "FEHLER SSH: OpenSSH nicht installierbar (z.B. Server 2016)" }
      else { $tail = (($out.Trim() -split "`r?`n") | Select-Object -Last 2) -join " | "; $parts += "FEHLER SSH: $tail" }
    } catch { $parts += "FEHLER SSH: " + $_.Exception.Message }
    finally { Remove-Item $g -Force -ErrorAction SilentlyContinue }
  }
  if ($parts.Count -eq 0) { "nichts zu tun" } else { $parts -join " | " }
}

$result = @()
foreach ($t in $todo) {
  Write-Host ("Installiere auf {0} ..." -f $t.Name) -NoNewline
  try {
    $doInstall = -not $t.Installiert
    if ($t.Self) { $r = & $block $CrmBase $CustomerNo $Code $EnrollmentKey $doInstall ([bool]$EnableSsh) $SshAllowFrom }
    else { $r = Invoke-Command -ComputerName $t.Name -ScriptBlock $block -ArgumentList $CrmBase, $CustomerNo, $Code, $EnrollmentKey, $doInstall, ([bool]$EnableSsh), $SshAllowFrom -ErrorAction Stop }
  } catch { $r = "FEHLER: " + $_.Exception.Message }
  $r = ($r | Out-String).Trim()
  if ($r -notlike "*FEHLER*") { Write-Host " ok" -ForegroundColor Green } else { Write-Host " $r" -ForegroundColor Red }
  $result += [pscustomobject]@{ Name = $t.Name; Ergebnis = $r }
}

Write-Host ""
Write-Host "=== Ergebnis ===" -ForegroundColor Cyan
$result | Format-Table -AutoSize -Wrap
if ($noreach.Count -gt 0) {
  Write-Host "Nicht erreichbar (manuell installieren, z.B. Workgroup-Server oder WinRM aus): $(($noreach | ForEach-Object Name) -join ', ')" -ForegroundColor Yellow
}
Write-Host "Die Server erscheinen nach wenigen Minuten im CRM (Server-Status)." -ForegroundColor Green
