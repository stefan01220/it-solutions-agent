# IT-Solutions Asset-Collector
# collector-version: 12
# Infrastruktur-Scan (nur lesend) -> POST an das CRM. Zusaetzlich fuehrt er die FESTEN Befehle
# windows-update, winget-upgrade, rescan und reboot aus, wenn das CRM sie fuer dieses Geraet
# freigegeben hat (Opt-in) und der Nutzer sie ausgeloest hat. Kein freies Skript, kein Fernzugriff.
#
# Wird von der Scheduled Task "IT-Solutions Asset-Collector" als SYSTEM
# ausgefuehrt. Konfiguration liegt in agent-config.json daneben.
#
# Self-Update (signiert): holt bei jedem Lauf collector.ps1 + collector.ps1.sig
# vom CRM und installiert die neue Version NUR, wenn die RSA-Signatur zum
# eingebetteten oeffentlichen Schluessel passt, die Version groesser ist als die
# laufende (kein Rollback) und das Skript fehlerfrei parst. Greift beim NAECHSTEN Lauf.
#
# Test ohne Senden:  powershell -ExecutionPolicy Bypass -File collector.ps1 -DryRun
# Befehle abholen:   collector.ps1 -Poll   (Scheduled Task "IT-Solutions Agent Poll", alle 10 Min.)
# Nur ASCII in dieser Datei (Windows PowerShell 5.1 liest sie ohne BOM als ANSI).
param([switch]$DryRun, [switch]$Poll)

$ErrorActionPreference = "Stop"
# TLS 1.2 erzwingen (Windows PowerShell 5.1 nutzt sonst veraltetes TLS).
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$CollectorVersion = 12
$SigningKeyXml = '<RSAKeyValue><Modulus>uAxspiYovBEcA7viyD9y1pDu+gw4n/ASAGAgLZHCURTOYeD6Az/Udz8JavYAf7SQmISWfe28nVhzskliDyYyy23RiS9NpEnS0tqMXBNr+tKz29N4wOhTdyhqsIyAulxN9rK+VA+WyF9lNBmTI94OvUJ3qeebgrJpHThdVILVVhqQVwUjTJvq2ZQDoCZcofdGYVWowBRyCewoW9KMnDsoEGXy/1/nuxKRB4GicQMo4P5+HMWbNQFf+c5n+qGKE3N4TdPUHQOE38i25y5zgJ8e88Olqhla2cKfhU5wwJhZ5JgvR/R6BSt0QAxdXmkOLtfjH8SWIyHLdaUXRQyWPo4cb2bDaUsEbL3R7IapkqejvGwQJMsamoGU9xVUP0+Cj/UlpVYWQ1wSmFnIZS02LG7bpAIfkeUEhc/3/iu2nJ/PV2dROIHZ1rlxNBwcmWb0QnaYEYHd1IGHdVCMBpx2ggmgChphkbhhaGd3r84ZRt3SmIefwmx025R/CgSX/6Fvsqih</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'

$cfgPath = Join-Path $PSScriptRoot "agent-config.json"
if (-not (Test-Path $cfgPath)) { Write-Error "agent-config.json fehlt"; exit 1 }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
if (-not $cfg.token -or -not $cfg.reportUrl) { Write-Error "Config unvollstaendig"; exit 1 }

function Safe($block, $default = $null) {
  try { & $block } catch { return $default }
}

# Wie Safe, misst aber die Dauer je Block (landet als "timing" im Report - so
# sieht man im CRM, welcher Block auf einem Kundenserver langsam ist).
$timing = [ordered]@{}
function Timed($name, $block, $default = $null) {
  $t0 = Get-Date
  $r = Safe $block $default
  $timing[$name] = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
  return $r
}

# --- Wiederverwendete Bausteine (Datenerfassung + Befehlsausfuehrung) -------------
# Neustart ausstehend? (Windows Update / Komponentenwartung)
function Test-PendingReboot {
  (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
  (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
}

# Rollen (ueber Dienste erkannt)
function Get-Roles {
  $r = New-Object System.Collections.ArrayList
  $names = @(Get-Service | Select-Object -ExpandProperty Name)
  if ($names -contains 'NTDS')        { [void]$r.Add('AD-DC') }
  if ($names -contains 'vmms')        { [void]$r.Add('Hyper-V') }
  if ($names -contains 'DHCPServer')  { [void]$r.Add('DHCP') }
  if ($names -contains 'DNS')         { [void]$r.Add('DNS') }
  if ($names -contains 'W3SVC')       { [void]$r.Add('IIS') }
  if (@($names | Where-Object { $_ -match '^MSSQL(\$|SERVER)' }).Count -gt 0) { [void]$r.Add('SQL') }
  if (@($names | Where-Object { $_ -match '^Veeam' }).Count -gt 0) { [void]$r.Add('Veeam') }
  if ($names -contains 'wbengine')    { [void]$r.Add('WinServerBackup') }
  @($r)
}

# Backup-Nachweis: Windows Server Backup und Veeam (Ereignisprotokolle), best-effort, rein lesend.
# "product" bleibt leer, wenn nichts erkannt wird - andere Sicherungsprogramme sieht der Collector nicht.
function Get-BackupInfo($roles) {
  $product = $null; $lastOk = $null; $lastFail = $null; $lastResult = $null; $samples = @(); $anyJobFailed = $false
  if (($roles -contains 'WinServerBackup') -or (Get-WinEvent -ListLog 'Microsoft-Windows-Backup' -ErrorAction SilentlyContinue | Where-Object { $_.RecordCount -gt 0 })) {
    $ok = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Backup'; Id = 4 } -MaxEvents 1 -ErrorAction SilentlyContinue)
    $bad = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Backup'; Id = 5,9,14,17,22,49,50,52,100 } -MaxEvents 1 -ErrorAction SilentlyContinue)
    if ($ok.Count -gt 0 -or $bad.Count -gt 0) {
      $product = 'WindowsServerBackup'
      if ($ok.Count -gt 0)  { $lastOk = $ok[0].TimeCreated }
      if ($bad.Count -gt 0) { $lastFail = $bad[0].TimeCreated }
    }
  }
  if ($roles -contains 'Veeam') {
    # Veeam B&R schreibt "Backup job 'X' finished with Success|Warning|Failed" als Ereignis 190
    # (Backup-Copy: 490) des Anbieters "Veeam MP"; der Text steht in den Eigenschaften, nicht in Message.
    $ve = @(Get-WinEvent -FilterHashtable @{ LogName = 'Veeam Backup'; ProviderName = 'Veeam MP'; Id = 190, 490 } -MaxEvents 100 -ErrorAction SilentlyContinue)
    if ($ve.Count -gt 0) {
      $product = 'Veeam'
      $jobs = @{}
      foreach ($e in $ve) {
        $msg = [string]$e.Message
        if ($msg.Length -lt 5 -and $e.Properties.Count -gt 0) { $msg = [string]$e.Properties[$e.Properties.Count - 1].Value }
        if ($msg -match "[Jj]ob '(.+?)' finished with (\w+)") {
          $jn = $Matches[1]; $res = $Matches[2]
          if (-not $jobs.ContainsKey($jn)) { $jobs[$jn] = [pscustomobject]@{ job = $jn; result = $res; time = $e.TimeCreated } }
          if ($res -match 'Success|Warning') { if (-not $lastOk -or $e.TimeCreated -gt $lastOk) { $lastOk = $e.TimeCreated } }
        }
      }
      # Ein Job, dessen LETZTER Lauf fehlgeschlagen ist, zaehlt als Fehler - auch wenn ein anderer Job spaeter erfolgreich war.
      foreach ($j in $jobs.Values) {
        if ($j.result -match 'Fail|Error') { $anyJobFailed = $true; if (-not $lastFail -or $j.time -gt $lastFail) { $lastFail = $j.time } }
      }
      $samples = @($jobs.Values | ForEach-Object { [pscustomobject]@{ job = $_.job; result = $_.result; time = $_.time.ToUniversalTime().ToString("o") } })
    }
  }
  if ($lastOk -and $lastFail) { $lastResult = $(if ($lastOk -ge $lastFail) { 'success' } else { 'failed' }) }
  elseif ($lastOk) { $lastResult = 'success' } elseif ($lastFail) { $lastResult = 'failed' }
  if ($anyJobFailed) { $lastResult = 'failed' }
  [pscustomobject]@{
    product = $product
    lastSuccess = $(if ($lastOk) { $lastOk.ToUniversalTime().ToString("o") } else { $null })
    lastFailure = $(if ($lastFail) { $lastFail.ToUniversalTime().ToString("o") } else { $null })
    lastResult = $lastResult
    samples = $samples
  }
}

# Winget: Pfad suchen (als SYSTEM ist winget nicht im PATH) und verfuegbare Updates lesen.
function Get-WingetInfo {
  $exe = $null
  $cmd = Get-Command winget.exe -ErrorAction SilentlyContinue
  if ($cmd) { $exe = $cmd.Source }
  if (-not $exe) {
    # Als SYSTEM ist winget nicht im PATH - die App-Installer-Datei direkt suchen.
    $c = @(Resolve-Path 'C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe\winget.exe' -ErrorAction SilentlyContinue | Sort-Object Path)
    if ($c.Count -gt 0) { $exe = $c[$c.Count - 1].Path }
  }
  if (-not $exe) { return [pscustomobject]@{ available = $false } }
  $ver = ((& $exe --version 2>&1 | Out-String)).Trim()
  $lines = @(((& $exe upgrade --accept-source-agreements --disable-interactivity 2>&1 | Out-String) -split "`r?`n"))
  $sep = -1
  for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^-{5,}\s*$') { $sep = $i; break } }
  $list = New-Object System.Collections.ArrayList
  if ($sep -ge 1) {
    # Spalten nach den Positionen der Kopfzeile schneiden (Namen koennen Leerzeichen enthalten).
    $starts = @([regex]::Matches($lines[$sep - 1], '(?<=^|\s{2,})\S+(?:\s\S+)*') | ForEach-Object { $_.Index })
    if ($starts.Count -ge 4) {
      for ($i = $sep + 1; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i].TrimEnd()
        if (-not $ln) { break }
        $cut = @(); for ($k = 0; $k -lt $starts.Count; $k++) {
          $from = $starts[$k]; $to = if ($k + 1 -lt $starts.Count) { $starts[$k + 1] } else { $ln.Length }
          if ($from -ge $ln.Length) { $cut += '' } else { $cut += $ln.Substring($from, [math]::Min($to, $ln.Length) - $from).Trim() }
        }
        # Gueltige Paket-ID: ohne Leerzeichen, mit Buchstaben, keine reine Versionsnummer (Fusstext der Ausgabe ausfiltern).
        if ($cut[1] -match '^[A-Za-z0-9][A-Za-z0-9._+-]*$' -and $cut[1] -match '[A-Za-z]' -and $cut[1] -notmatch '^[\d.]+$' -and $list.Count -lt 60) {
          [void]$list.Add([pscustomobject]@{ name = $cut[0]; id = $cut[1]; version = $cut[2]; available = $cut[3] })
        }
      }
    }
  }
  [pscustomobject]@{ available = $true; version = $ver; upgrades = @($list) }
}

function Get-WingetExe {
  $cmd = Get-Command winget.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $c = @(Resolve-Path 'C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe\winget.exe' -ErrorAction SilentlyContinue | Sort-Object Path)
  if ($c.Count -gt 0) { return $c[$c.Count - 1].Path }
  return $null
}

# --- Fernbefehle (Update jetzt starten) ------------------------------------------
# NUR diese festen Befehle werden ausgefuehrt - kein freies Skript, kein Invoke-Expression:
#   windows-update | winget-upgrade | rescan | reboot
# Das CRM liefert sie nur aus, wenn fuer dieses Geraet freigegeben (Opt-in) und per Klick ausgeloest.
# Die lokalen Voraussetzungen werden hier nochmals geprueft. Ein Neustart passiert NIE von selbst.
$RiskyRoles = 'AD-DC', 'Hyper-V', 'SQL'

function New-CmdResult($status, $summary, $details = $null) {
  return [pscustomobject]@{ status = $status; summary = $summary; details = $details }
}

# Liefert $null, wenn der Befehl laufen darf, sonst den Ablehnungsgrund.
function Get-CommandRefusal($command, $p) {
  $osCaption = [string](Get-CimInstance Win32_OperatingSystem).Caption
  $isServer = ($osCaption -match 'Server')
  $myRoles = @(Safe { Get-Roles } @())
  $risky = (@($myRoles | Where-Object { $RiskyRoles -contains $_ }).Count -gt 0)
  switch ($command) {
    'rescan' { return $null }
    'windows-update' {
      if (Test-PendingReboot) { return "Neustart ausstehend - zuerst neu starten." }
      $c = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'"
      if ($c -and ($c.FreeSpace / 1GB) -lt 5) { return "Zu wenig freier Platz auf C: (unter 5 GB)." }
      if ($isServer -and ($p.ignoreBackup -ne $true)) {
        $b = Safe { Get-BackupInfo $myRoles } $null
        $fresh = $false
        if ($b -and $b.product -and $b.lastSuccess) {
          $ok = [datetime]::Parse([string]$b.lastSuccess, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
          $failedAfter = $false
          if ($b.lastResult -eq 'failed' -and $b.lastFailure) {
            $failedAfter = ([datetime]::Parse([string]$b.lastFailure, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) -gt $ok)
          }
          $fresh = (-not $failedAfter) -and (((Get-Date).ToUniversalTime() - $ok.ToUniversalTime()).TotalHours -le 24)
        }
        if (-not $fresh) { return "Keine erfolgreiche Sicherung der letzten 24 Stunden nachweisbar." }
      }
    }
    default {
      if ($command -ne 'winget-upgrade' -and $command -ne 'reboot') { return "Unbekannter Befehl." }
      if ($isServer -and ($p.serverConfirmed -ne $true)) { return "Server-Bestaetigung fehlt." }
    }
  }
  if ($risky -and ($p.roleConfirmed -ne $true)) { return "Kritische Rolle (AD-DC/Hyper-V/SQL) nicht bestaetigt." }
  return $null
}

# $whatIf = $true: nur auflisten, was installiert wuerde (fuer lokale Tests, wird vom CRM nie gesetzt).
function Invoke-WindowsUpdateInstall($whatIf = $false) {
  $session  = New-Object -ComObject Microsoft.Update.Session
  $searcher = $session.CreateUpdateSearcher()
  $found = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'").Updates
  $coll = New-Object -ComObject Microsoft.Update.UpdateColl
  foreach ($u in $found) {
    $cats = @(); foreach ($c in $u.Categories) { $cats += $c.Name }
    # Nur Sicherheits-/Qualitaets-Updates: keine Treiber, Feature-Updates/Upgrades, Vorschau.
    $skip = ($cats -contains 'Upgrades') -or ($cats -contains 'Feature Packs') -or ($cats -contains 'Drivers') -or
            ($u.Title -match 'Feature update|Upgrade to|Preview') -or $u.BrowseOnly
    if ($skip) { continue }
    if (-not $whatIf -and -not $u.EulaAccepted) { $u.AcceptEula() }
    [void]$coll.Add($u)
  }
  if ($whatIf) {
    return New-CmdResult "done" "WhatIf: $($coll.Count) Updates wuerden installiert." ([pscustomobject]@{ titles = @(foreach ($u in $coll) { [string]$u.Title }) })
  }
  if ($coll.Count -eq 0) { return New-CmdResult "done" "Keine passenden Updates (Sicherheit/Qualitaet) offen." ([pscustomobject]@{ installed = 0 }) }

  $dl = $session.CreateUpdateDownloader(); $dl.Updates = $coll; [void]$dl.Download()
  $inst = $session.CreateUpdateInstaller(); $inst.Updates = $coll; $inst.AllowSourcePrompts = $false
  $r = $inst.Install()

  $ok = 0; $bad = 0; $items = New-Object System.Collections.ArrayList
  for ($i = 0; $i -lt $coll.Count; $i++) {
    $code = $r.GetUpdateResult($i).ResultCode
    $t = [string]$coll.Item($i).Title; if ($t.Length -gt 90) { $t = $t.Substring(0, 90) }
    if ($code -eq 2) { $ok++ } else { $bad++ }
    if ($items.Count -lt 30) { [void]$items.Add([pscustomobject]@{ title = $t; result = [int]$code }) }
  }
  $reboot = [bool]$r.RebootRequired
  $summary = "$ok installiert, $bad mit Fehler" + $(if ($reboot) { ", Neustart erforderlich" } else { "" })
  $status = if ($ok -gt 0 -or $bad -eq 0) { "done" } else { "failed" }
  return New-CmdResult $status $summary ([pscustomobject]@{ installed = $ok; failed = $bad; rebootRequired = $reboot; items = @($items) })
}

function Invoke-WingetUpgradeAll($whatIf = $false) {
  $exe = Get-WingetExe
  if (-not $exe) { return New-CmdResult "failed" "winget ist auf diesem Geraet nicht verfuegbar." $null }
  $info = Get-WingetInfo
  $todo = @($info.upgrades)
  # Sicherheits-/Infrastruktursoftware wird nie automatisch angefasst (eigene Update-Wege).
  $never = '(^ESET\.|^Veeam\.|^Microsoft\.SQLServer|^VMware\.|^Docker\.|^Microsoft\.HyperV|Backup|Acronis|Sophos|Kaspersky|Bitdefender|Malwarebytes|Defender)'
  $upgraded = @(); $failed = @(); $skipped = @()
  foreach ($u in ($todo | Select-Object -First 25)) {
    if ($u.id -match $never) { $skipped += $u.id; continue }
    if ($whatIf) { $upgraded += $u.id; continue }
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
      $proc = Start-Process -FilePath $exe -PassThru -NoNewWindow -RedirectStandardOutput $tmp -ArgumentList @(
        'upgrade', '--id', $u.id, '--exact', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
      if (-not $proc.WaitForExit(900000)) { try { $proc.Kill() } catch {}; $failed += "$($u.id) (Zeitueberschreitung)"; continue }
      if ($proc.ExitCode -eq 0) { $upgraded += $u.id } else { $failed += "$($u.id) (Code $($proc.ExitCode))" }
    } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
  }
  $status = if ($failed.Count -gt 0 -and $upgraded.Count -eq 0) { "failed" } else { "done" }
  return New-CmdResult $status "$($upgraded.Count) aktualisiert, $($failed.Count) fehlgeschlagen, $($skipped.Count) ausgenommen" ([pscustomobject]@{ upgraded = $upgraded; failed = $failed; skipped = $skipped })
}

function Invoke-AgentCommand($command, $p) {
  $why = Get-CommandRefusal $command $p
  if ($why) { return New-CmdResult "refused" $why $null }
  switch ($command) {
    'rescan' { return New-CmdResult "done" "Neu eingelesen." $null }   # der Bericht folgt direkt nach dem Befehl
    'windows-update' { return Invoke-WindowsUpdateInstall }
    'winget-upgrade' { return Invoke-WingetUpgradeAll }
    'reboot' {
      & shutdown.exe /r /t 300 /c "Neustart durch IT-Solutions (Wartung) in 5 Minuten. Abbrechen: shutdown /a"
      return New-CmdResult "done" "Neustart in 5 Minuten geplant." $null
    }
  }
  return New-CmdResult "refused" "Unbekannter Befehl." $null
}

# Eigene Scheduled Task "IT-Solutions Agent Poll": alle 10 Minuten nur nachsehen, ob Befehle warten
# (klein und schnell). Der grosse Scan laeuft alle 2 Stunden.
function Register-PollTask {
  $name = "IT-Solutions Agent Poll"
  $taskArgs = "-NonInteractive -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Poll"
  $existing = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
  if ($existing -and ($existing.Actions[0].Arguments -eq $taskArgs)) { return }
  $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $taskArgs
  $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) `
    -RepetitionInterval (New-TimeSpan -Minutes 10) -RepetitionDuration (New-TimeSpan -Days 3650)
  $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 3)
  Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
  Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description "IT-Solutions Agent - holt freigegebene Wartungsbefehle vom CRM ab" | Out-Null
}

# Haupt-Task auf 2-Stunden-Takt bringen (aeltere Installationen laufen noch alle 4 Stunden). Aendert die laufende
# Aufgabe direkt (Set-ScheduledTask), ohne sie zu stoppen oder neu anzulegen.
function Set-MainTaskInterval($taskName = "IT-Solutions Asset-Collector", $interval = "PT2H") {
  $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
  $changed = $false
  foreach ($tr in $task.Triggers) {
    if ($tr.Repetition -and $tr.Repetition.Interval -and $tr.Repetition.Interval -ne $interval) { $tr.Repetition.Interval = $interval; $changed = $true }
  }
  if ($changed) { $task | Set-ScheduledTask | Out-Null }
}

# Angemeldete Benutzer duerfen den Collector-Task STARTEN (Tray: "Jetzt melden"), aber nicht aendern.
# DACL des Tasks um "Authenticated Users: Lesen + Ausfuehren" ergaenzen (idempotent).
function Set-TaskUserRunnable($taskName = "IT-Solutions Asset-Collector") {
  $svc = New-Object -ComObject Schedule.Service
  $svc.Connect()
  $t = $svc.GetFolder("\").GetTask($taskName)
  $sd = [string]$t.GetSecurityDescriptor(4)
  # Windows speichert das Recht als Hexmaske (0x1200a9) - daher jede Erlauben-Regel fuer AU als "schon vorhanden" werten.
  if ($sd -and $sd -notmatch '\(A;[^;)]*;[^;)]*;;;AU\)') { $t.SetSecurityDescriptor($sd + "(A;;GRGX;;;AU)", 0) }
}

# Tray-Programm (Symbol im Infobereich) installieren bzw. aktualisieren - nur mit gueltiger Signatur.
# Abschaltbar je Geraet mit "tray": false in agent-config.json. Server Core (kein explorer.exe) wird uebersprungen.
function Install-Tray {
  $runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
  $runName = "IT-Solutions Agent"
  if ($cfg.PSObject.Properties["tray"] -and $cfg.tray -eq $false) {
    Remove-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue
    return "abgeschaltet"
  }
  if (-not (Test-Path (Join-Path $env:windir "explorer.exe"))) { return "kein Desktop" }
  $base = $cfg.reportUrl -replace '/api/agent/report$', ''
  $dir = Join-Path $env:ProgramFiles "IT-Solutions"
  $exe = Join-Path $dir "IT-Solutions-Agent.exe"
  $sigLocal = "$exe.sig"
  $wc = New-Object System.Net.WebClient
  $sigText = ([System.Text.Encoding]::ASCII.GetString($wc.DownloadData("$base/agent/IT-Solutions-Agent.exe.sig"))).Trim()
  $have = ""
  if (Test-Path $sigLocal) { $have = ([string](Get-Content $sigLocal -Raw)).Trim() }
  $result = "aktuell"
  if ($sigText -ne $have -or -not (Test-Path $exe)) {
    $bytes = $wc.DownloadData("$base/agent/IT-Solutions-Agent.exe")
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.FromXmlString($SigningKeyXml)
    if (-not $rsa.VerifyData($bytes, "SHA256", [Convert]::FromBase64String($sigText))) { return "Signatur ungueltig - nicht installiert" }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    # Laufende Instanzen sperren die Datei: umbenennen geht trotzdem, die neue Version gilt ab der naechsten Anmeldung.
    if (Test-Path $exe) {
      Remove-Item "$exe.old" -Force -ErrorAction SilentlyContinue
      try { Move-Item -Path $exe -Destination "$exe.old" -Force } catch {}
    }
    [System.IO.File]::WriteAllBytes($exe, $bytes)
    Set-Content -Path $sigLocal -Value $sigText -Encoding ASCII
    $result = "installiert"
    # Als SYSTEM: Symbol gleich in allen angemeldeten Sitzungen starten (sonst erst bei der naechsten Anmeldung).
    if ([System.Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) {
      try { $p = Start-Process -FilePath $exe -ArgumentList "--start-sessions" -PassThru -Wait -WindowStyle Hidden; $result = "installiert, in $($p.ExitCode) Sitzung(en) gestartet" } catch {}
    }
  }
  $want = '"' + $exe + '"'
  $cur = (Get-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue).$runName
  if ($cur -ne $want) { New-ItemProperty -Path $runKey -Name $runName -Value $want -PropertyType String -Force | Out-Null }
  return $result
}

# Statusdatei fuer das Tray-Programm: liegt AUSSERHALB des geschuetzten Installationsordners, enthaelt kein Token.
# Lesen: alle Benutzer; Schreiben: nur SYSTEM und Administratoren.
$StatusDir = Join-Path $env:ProgramData "IT-Solutions-Status"
function Write-AgentStatus($result, $message, $resp) {
  if (-not (Test-Path $StatusDir)) {
    New-Item -ItemType Directory -Force -Path $StatusDir | Out-Null
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inh = "ContainerInherit,ObjectInherit"
    foreach ($r in @(@("*S-1-5-18", "FullControl"), @("*S-1-5-32-544", "FullControl"), @("*S-1-5-32-545", "ReadAndExecute"))) {
      $sid = New-Object System.Security.Principal.SecurityIdentifier($r[0].TrimStart("*"))
      $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, $r[1], $inh, "None", "Allow")))
    }
    Set-Acl -Path $StatusDir -AclObject $acl
  }
  $file = Join-Path $StatusDir "status.json"
  $old = $null
  try { $old = Get-Content $file -Raw -ErrorAction Stop | ConvertFrom-Json } catch {}
  $ip = $null
  try {
    $ip = @(Get-NetIPConfiguration -ErrorAction Stop | Where-Object { $_.IPv4DefaultGateway } |
      ForEach-Object { $_.IPv4Address.IPAddress } | Where-Object { $_ })[0]
  } catch {}
  $now = (Get-Date).ToUniversalTime().ToString("o")
  $st = [ordered]@{
    agentVersion  = $CollectorVersion
    hostname      = $env:COMPUTERNAME
    ip            = $ip
    crmHost       = ([uri]$cfg.reportUrl).Host
    lastRunAt     = $now
    lastResult    = $result
    lastMessage   = $message
    lastSuccessAt = $(if ($result -eq "ok") { $now } elseif ($old) { $old.lastSuccessAt } else { $null })
    customerName  = $(if ($resp -and $resp.customerName) { [string]$resp.customerName } elseif ($old) { $old.customerName } else { $null })
    provider      = $(if ($resp -and $resp.provider) { $resp.provider } elseif ($old) { $old.provider } else { $null })
  }
  $tmp = "$file.tmp"
  [System.IO.File]::WriteAllText($tmp, ($st | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
  Move-Item -Force -Path $tmp -Destination $file
}

# --- Poll-Modus (-Poll): Befehle abholen, ausfuehren, Ergebnis melden ---------------
if ($Poll) {
  $api = $cfg.reportUrl -replace '/report$', '/commands'
  $hdr = @{ Authorization = "Bearer $($cfg.token)" }
  $resp = $null
  try { $resp = Invoke-RestMethod -Uri ($api + "?hostname=" + [uri]::EscapeDataString($env:COMPUTERNAME)) -Headers $hdr -TimeoutSec 20 } catch { exit 0 }
  $ranAny = $false
  foreach ($c in @($resp.commands)) {
    $ranAny = $true
    $res = $null
    try { $res = Invoke-AgentCommand ([string]$c.command) $c.params }
    catch { $res = New-CmdResult "failed" ("Fehler: " + $_.Exception.Message) $null }
    $body = @{ status = $res.status; summary = [string]$res.summary; details = $res.details } | ConvertTo-Json -Depth 6 -Compress
    for ($try = 0; $try -lt 3; $try++) {
      try {
        Invoke-RestMethod -Method Post -Uri ($api + "/" + $c.id) -Headers $hdr -ContentType "application/json; charset=utf-8" `
          -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30 | Out-Null
        break
      } catch { Start-Sleep -Seconds 5 }
    }
  }
  # Nach einem Befehl sofort einen frischen Bericht senden, damit das CRM den neuen Stand zeigt.
  if ($ranAny) { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath | Out-Null }
  exit 0
}

# --- Signiertes Self-Update --------------------------------------------------
# Best-effort: jeder Fehler laesst die aktuelle Version unveraendert weiterlaufen.
# $updateStatus landet im Report, damit das CRM sieht, ob Updates ankommen.
$updateStatus = "uptodate"
try {
  $base = $cfg.reportUrl -replace '/api/agent/report$', '/agent/'
  $wc = New-Object System.Net.WebClient
  $remoteBytes = $wc.DownloadData($base + "collector.ps1")
  $sigText = ([System.Text.Encoding]::ASCII.GetString($wc.DownloadData($base + "collector.ps1.sig"))).Trim()

  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
  $rsa.FromXmlString($SigningKeyXml)
  $sigOk = $rsa.VerifyData($remoteBytes, "SHA256", [Convert]::FromBase64String($sigText))

  if (-not $sigOk) {
    $updateStatus = "signature-invalid"
    Write-Warning "Self-Update abgelehnt: Signatur ungueltig."
  } else {
    $remoteText = [System.Text.Encoding]::UTF8.GetString($remoteBytes)
    $remoteVersion = 0
    if ($remoteText -match '(?m)^# collector-version:\s*(\d+)') { $remoteVersion = [int]$Matches[1] }
    if ($remoteVersion -le $CollectorVersion) {
      $updateStatus = "uptodate"
    } else {
      $errs = $null
      [void][System.Management.Automation.Language.Parser]::ParseInput($remoteText, [ref]$null, [ref]$errs)
      if ($errs -and $errs.Count -gt 0) {
        $updateStatus = "syntax-error"
        Write-Warning "Self-Update abgelehnt: neue Version hat Syntaxfehler."
      } elseif ($DryRun) {
        $updateStatus = "update-available-$remoteVersion"
        Write-Output "DryRun: Update auf Version $remoteVersion waere gueltig, wird nicht installiert."
      } else {
        $tmp = "$PSCommandPath.new"
        [System.IO.File]::WriteAllBytes($tmp, $remoteBytes)
        Copy-Item $PSCommandPath "$PSCommandPath.bak" -Force
        Move-Item $tmp $PSCommandPath -Force
        $updateStatus = "updated-to-$remoteVersion"
        Write-Output "Self-Update: collector.ps1 auf Version $remoteVersion aktualisiert - starte neue Version"
        # Sofort mit der neuen Version laufen, damit der Bericht aktuell ist (nicht erst beim naechsten Lauf).
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath | Out-Null
        exit 0
      }
    }
  }
} catch {
  $updateStatus = "check-failed"
}

$started = Get-Date

$os  = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$cs  = Safe { Get-CimInstance Win32_ComputerSystem } $null
$bios = Safe { Get-CimInstance Win32_BIOS } $null

$lastBoot = $os.LastBootUpTime
$uptimeH  = [math]::Round(((Get-Date) - $lastBoot).TotalHours, 1)

# Laufwerke (nur lokale Festplatten, DriveType=3)
$disks = @(Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object {
  [pscustomobject]@{
    drive    = $_.DeviceID
    totalGb  = [math]::Round($_.Size / 1GB, 1)
    freeGb   = [math]::Round($_.FreeSpace / 1GB, 1)
    usedPct  = if ($_.Size -gt 0) { [math]::Round((($_.Size - $_.FreeSpace) / $_.Size) * 100, 1) } else { 0 }
  }
})

# Gestoppte Auto-Start-Dienste (das CRM filtert bekannte harmlose heraus)
$svc = @(Safe { Get-Service | Where-Object { $_.StartType -eq "Automatic" -and $_.Status -ne "Running" } |
       Select-Object -ExpandProperty Name } @())

# --- Windows Update: offene Updates, Details, letzte Installation -------------
$updInfo = Timed "windowsUpdate" {
  $session  = New-Object -ComObject Microsoft.Update.Session
  $searcher = $session.CreateUpdateSearcher()
  $found = $searcher.Search("IsInstalled=0 and IsHidden=0").Updates
  $list = New-Object System.Collections.ArrayList
  $security = 0; $drivers = 0
  foreach ($u in $found) {
    $cats = @(); foreach ($c in $u.Categories) { $cats += $c.Name }
    $isSec = ($cats -contains 'Security Updates') -or ($cats -contains 'Critical Updates')
    $isDrv = ($u.Type -eq 2)
    if ($isSec) { $security++ }
    if ($isDrv) { $drivers++ }
    if ($list.Count -lt 40) {
      $title = [string]$u.Title
      if ($title.Length -gt 110) { $title = $title.Substring(0, 110) }
      $kb = ''; foreach ($k in $u.KBArticleIDs) { $kb = [string]$k }
      [void]$list.Add([pscustomobject]@{ title = $title; kb = $kb; security = $isSec; driver = $isDrv; severity = [string]$u.MsrcSeverity })
    }
  }
  $lastInstalled = $null
  $total = $searcher.GetTotalHistoryCount()
  if ($total -gt 0) {
    $hist = $searcher.QueryHistory(0, [math]::Min($total, 50))
    foreach ($h in $hist) {
      if ($h.ResultCode -eq 2 -and $h.Title -notmatch 'Defender|Security Intelligence|Malicious Software Removal') {
        if (-not $lastInstalled -or $h.Date -gt $lastInstalled) { $lastInstalled = $h.Date }
      }
    }
  }
  [pscustomobject]@{
    count = $found.Count; security = $security; drivers = $drivers
    list = @($list); lastInstalled = $(if ($lastInstalled) { $lastInstalled.ToUniversalTime().ToString("o") } else { $null })
  }
} $null
$pendingUpdates = if ($updInfo) { $updInfo.count } else { $null }

# Neustart ausstehend? (Windows Update / Komponentenwartung)
$pendingReboot = Safe { Test-PendingReboot } $null

$ramTotalGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
$ramUsedGb  = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB, 1)

# --- Windows-Version fuer Support-Ende-Pruefung ------------------------------
$osReg = Safe { Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' } $null
$osInfo = [pscustomobject]@{
  displayVersion = if ($osReg) { [string]$osReg.DisplayVersion } else { $null }
  releaseId      = if ($osReg) { [string]$osReg.ReleaseId } else { $null }
  edition        = if ($osReg) { [string]$osReg.EditionID } else { $null }
  ubr            = if ($osReg) { $osReg.UBR } else { $null }
  installDate    = $(if ($os.InstallDate) { $os.InstallDate.ToUniversalTime().ToString("o") } else { $null })
}

# --- Hardware / Virtualisierung ------------------------------------------------
$modelText = if ($cs) { "$($cs.Manufacturer) $($cs.Model)" } else { "" }
$hardware = [pscustomobject]@{
  manufacturer = if ($cs) { [string]$cs.Manufacturer } else { $null }
  model        = if ($cs) { [string]$cs.Model } else { $null }
  serial       = if ($bios) { [string]$bios.SerialNumber } else { $null }
  biosVersion  = if ($bios) { [string]$bios.SMBIOSBIOSVersion } else { $null }
  biosDate     = $(if ($bios -and $bios.ReleaseDate) { $bios.ReleaseDate.ToUniversalTime().ToString("o") } else { $null })
  isVirtual    = [bool]($modelText -match 'Virtual|VMware|KVM|QEMU|VirtualBox|HVM|Xen')
  domain       = if ($cs) { [string]$cs.Domain } else { $null }
  partOfDomain = if ($cs) { [bool]$cs.PartOfDomain } else { $null }
}

# Physische Datentraeger: Gesundheit (SMART-Zusammenfassung von Windows)
$physicalDisks = @(Timed "physicalDisks" {
  Get-PhysicalDisk -ErrorAction Stop | ForEach-Object {
    [pscustomobject]@{
      name = [string]$_.FriendlyName; media = [string]$_.MediaType
      health = [string]$_.HealthStatus; status = [string]($_.OperationalStatus -join ',')
      sizeGb = [math]::Round($_.Size / 1GB, 0)
    }
  }
} @())

# --- Installierte Programme (Name, Version, Hersteller) ------------------------
# Quelle: Uninstall-Registry (64+32 Bit). Wird im CRM pro Geraet als Inventar abgelegt
# (eine Zeile je Geraet, nicht je Report).
$software = @(Timed "software" {
  $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
  Get-ItemProperty $keys -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -and -not $_.SystemComponent -and -not $_.ParentKeyName -and $_.DisplayName -notmatch '^(Update for|Security Update|Hotfix|KB\d+)' } |
    ForEach-Object {
      $n = ([string]$_.DisplayName).Trim(); if ($n.Length -gt 100) { $n = $n.Substring(0, 100) }
      [pscustomobject]@{ name = $n; version = [string]$_.DisplayVersion; publisher = [string]$_.Publisher }
    } | Sort-Object name -Unique | Select-Object -First 400
} @())

# --- Winget: Programm-Updates, die verfuegbar waeren (nur Anzeige, installiert wird nichts) --
$winget = Timed "winget" { Get-WingetInfo } $null

# --- Sicherheit: Virenschutz, Firewall, BitLocker, RDP, SMB1 -------------------
$security = [pscustomobject]@{
  defender = Safe {
    $m = Get-MpComputerStatus -ErrorAction Stop
    [pscustomobject]@{
      enabled = [bool]$m.AntivirusEnabled; realtime = [bool]$m.RealTimeProtectionEnabled
      productVersion = [string]$m.AMProductVersion; engineVersion = [string]$m.AMEngineVersion
      signatureVersion = [string]$m.AntivirusSignatureVersion
      signatureUpdated = $(if ($m.AntivirusSignatureLastUpdated) { $m.AntivirusSignatureLastUpdated.ToUniversalTime().ToString("o") } else { $null })
    }
  } $null
  eset = Safe {
    # ESET (Server Security / Endpoint / Mail Security): auf Servern gibt es kein Windows Security Center,
    # daher direkt aus Registry + Dateien lesen (nur lesend). Engine = em000_64.dll im neuesten Modulordner,
    # Pruefzeit/Modulzeit = Schreibzeit von lastupd.ver bzw. mod.ver im Updfiles-Ordner.
    $info = Get-ItemProperty 'HKLM:\SOFTWARE\ESET\ESET Security\CurrentVersion\Info' -ErrorAction Stop
    $appData = [string]$info.AppDataDir
    $upd = Join-Path $appData 'Updfiles'
    $stamp = { param($f) $i = Get-Item -LiteralPath (Join-Path $upd $f) -ErrorAction SilentlyContinue; if ($i) { $i.LastWriteTime.ToUniversalTime().ToString("o") } else { $null } }
    $engine = $null
    $em = Get-ChildItem -LiteralPath (Join-Path ([string]$info.ModuleDir) 'em000_64') -Recurse -Filter 'em000_64.dll' -ErrorAction SilentlyContinue |
      Sort-Object { try { [int]$_.Directory.Name } catch { 0 } } -Descending | Select-Object -First 1
    if ($em) { $engine = ([string]$em.VersionInfo.FileVersion).Trim() }
    $svc = { param($n) $x = Get-Service -Name $n -ErrorAction SilentlyContinue; if ($x) { [string]$x.Status } else { $null } }
    [pscustomobject]@{
      productName = [string]$info.ProductName; productVersion = [string]$info.ProductVersion
      engineVersion = $engine
      updateCheckAt = (& $stamp 'lastupd.ver'); modulesUpdatedAt = (& $stamp 'mod.ver')
      serviceState = (& $svc 'ekrn'); agentState = (& $svc 'EraAgentSvc')
    }
  } $null
  antivirusProducts = @(Safe {
    # Windows Security Center (nur Arbeitsplaetze). Ein Produkt ist oft mehrfach registriert
    # (alte + neue Version): pro Name den juengsten Eintrag nehmen. "state" = Rohwert (hex) zum
    # Kalibrieren; "enabled" gilt bei Statusbyte 1x/2x als an (ESET nutzt 0x20).
    Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop |
      Group-Object displayName | ForEach-Object {
        $p = $_.Group | Sort-Object { try { [datetime]::Parse([string]$_.timestamp, [Globalization.CultureInfo]::InvariantCulture) } catch { [datetime]::MinValue } } -Descending | Select-Object -First 1
        $nm = [string]$p.displayName
        $hex = '{0:X6}' -f [int]$p.productState
        $first = ($nm -split ' ')[0]
        $sw = $software | Where-Object { $_.name -like "$first*" -and $_.name -match 'Secur|Antivir|Endpoint|Protect|Defend' } | Select-Object -First 1
        [pscustomobject]@{
          name = $nm
          state = $hex
          enabled = ($hex.Substring(2, 1) -in '1', '2')
          upToDate = ($hex.Substring(4, 2) -eq '00')
          version = $(if ($sw) { $sw.version } else { $null })
        }
      }
  } @())
  firewall = @(Safe { Get-NetFirewallProfile -ErrorAction Stop | ForEach-Object { [pscustomobject]@{ profile = [string]$_.Name; enabled = [bool]$_.Enabled } } } @())
  bitlocker = @(Timed "bitlocker" { Get-BitLockerVolume -ErrorAction Stop | ForEach-Object {
    [pscustomobject]@{ mount = [string]$_.MountPoint; protection = [string]$_.ProtectionStatus; status = [string]$_.VolumeStatus } } } @())
  rdpEnabled = Safe { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -ErrorAction Stop).fDenyTSConnections -eq 0 } $null
  rdpNla = Safe { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -ErrorAction Stop).UserAuthentication -eq 1 } $null
  smb1 = Safe { [bool](Get-SmbServerConfiguration -ErrorAction Stop).EnableSMB1Protocol } $null
}

# --- Ereignisprotokoll: Fehler der letzten 24 h (Datentraeger-/Dateisystemfehler) --
$eventInfo = Timed "events" {
  $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Level = 1,2; StartTime = (Get-Date).AddHours(-24) } -MaxEvents 400 -ErrorAction Stop)
  $diskProviders = 'disk','Ntfs','Microsoft-Windows-Ntfs','volmgr','stornvme','storahci','Microsoft-Windows-WHEA-Logger','Microsoft-Windows-Disk'
  $top = $ev | Group-Object ProviderName | Sort-Object Count -Descending | Select-Object -First 5 |
    ForEach-Object { [pscustomobject]@{ source = [string]$_.Name; count = $_.Count } }
  [pscustomobject]@{
    errors24h = $ev.Count
    diskErrors24h = @($ev | Where-Object { $diskProviders -contains $_.ProviderName }).Count
    top = @($top)
  }
} ([pscustomobject]@{ errors24h = 0; diskErrors24h = 0; top = @() })

# --- Rollen (ueber Dienste erkannt) ---------------------------------------------
$roles = @(Safe { Get-Roles } @())

# --- Hyper-V: VMs und Checkpoints (Snapshots) -----------------------------------
$hyperv = $null
if ($roles -contains 'Hyper-V') {
  $hyperv = Timed "hyperv" {
    $vms = @(Get-VM -ErrorAction Stop | ForEach-Object {
      $snaps = @(Get-VMSnapshot -VMName $_.Name -ErrorAction SilentlyContinue)
      $oldest = $null
      foreach ($s in $snaps) { if (-not $oldest -or $s.CreationTime -lt $oldest) { $oldest = $s.CreationTime } }
      [pscustomobject]@{
        name = [string]$_.Name; state = [string]$_.State
        checkpoints = $snaps.Count
        oldestCheckpointDays = $(if ($oldest) { [math]::Round(((Get-Date) - $oldest).TotalDays, 0) } else { $null })
      }
    })
    [pscustomobject]@{ vms = $vms }
  } $null
}

# --- Backup-Nachweis: Windows Server Backup und Veeam (Ereignisprotokolle) ------
# Best-effort und rein lesend. "product" bleibt leer, wenn nichts erkannt wird -
# andere Sicherungsprogramme sieht der Collector nicht.
$backup = Timed "backup" { Get-BackupInfo $roles } $null

# --- Netzwerk-Scan: lokale /24-Bereiche per Ping-Sweep aufwecken, dann ARP ---
# Leichter Scan (nur Ping + ARP), KEIN Portscan. Best-effort.
$network = Timed "network" {
  # Alle lokalen privaten IPv4-Adressen - pro /24 ein Ping-Sweep.
  $locals = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -match '^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.)' }
  $bases = $locals | ForEach-Object { ($_.IPAddress -split '\.')[0..2] -join '.' } | Sort-Object -Unique

  # Ping-Sweep: pro Host EIN eigenes Ping-Objekt (ein einzelnes Objekt 254x
  # parallel wirft "already in progress"). Tasks sammeln, dann WaitAll in
  # try/catch (eine gefaultete Task wuerfe sonst eine AggregateException).
  # Ergebnis bewusst in eine Variable -> KEINE Pipeline-Ausgabe (sonst landen
  # null-Werte im Block-Output).
  $pingTasks = New-Object System.Collections.ArrayList
  foreach ($b in $bases) {
    foreach ($i in 1..254) {
      Safe { [void]$pingTasks.Add((New-Object System.Net.NetworkInformation.Ping).SendPingAsync("$b.$i", 800)) }
    }
  }
  try { [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$pingTasks, 9000) } catch {}

  # ARP-Cache auslesen: alle privaten Reachable/Stale-Eintraege mit MAC.
  $neigh = @(Get-NetNeighbor -AddressFamily IPv4 -ErrorAction Stop |
    Where-Object { $_.State -in 'Reachable','Stale' -and $_.LinkLayerAddress `
      -and $_.IPAddress -match '^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.)' })

  # Reverse-DNS parallel mit Gesamt-Timeout (sequentiell haengt jeder tote Eintrag
  # mehrere Sekunden - auf Netzen mit vielen Geraeten dauerte das Minuten).
  $dnsTasks = @{}
  foreach ($n in $neigh) {
    Safe { $dnsTasks[$n.IPAddress] = [System.Net.Dns]::GetHostEntryAsync($n.IPAddress) }
  }
  try { [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($dnsTasks.Values), 8000) } catch {}

  foreach ($n in $neigh) {
    $t = $dnsTasks[$n.IPAddress]
    $name = $null
    if ($t -and $t.Status -eq 'RanToCompletion') { $name = $t.Result.HostName }
    [pscustomobject]@{
      ip       = $n.IPAddress
      mac      = $n.LinkLayerAddress
      hostname = $name
    }
  }
} @()

# Abhol-Task fuer Fernbefehle sicherstellen (best-effort).
$trayState = $null
if (-not $DryRun) { Safe { Register-PollTask }; Safe { Set-MainTaskInterval }; Safe { Set-TaskUserRunnable }; $trayState = Safe { Install-Tray } "Fehler" }

$payload = [pscustomobject]@{
  collectorVersion = $CollectorVersion
  updateStatus     = $updateStatus
  collectSeconds   = [math]::Round(((Get-Date) - $started).TotalSeconds, 0)
  timing           = $timing
  hostname         = $env:COMPUTERNAME
  osName           = $os.Caption
  osBuild          = $os.BuildNumber
  osInfo           = $osInfo
  uptimeHours      = $uptimeH
  lastBoot         = $lastBoot.ToString("o")
  cpuModel         = if ($cpu) { $cpu.Name.Trim() } else { $null }
  cpuLoadPct       = if ($cpu) { $cpu.LoadPercentage } else { $null }
  ramTotalGb       = $ramTotalGb
  ramUsedGb        = $ramUsedGb
  disks            = $disks
  physicalDisks    = $physicalDisks
  pendingUpdates   = $pendingUpdates
  updates          = $updInfo
  pendingReboot    = $pendingReboot
  servicesCritical = $svc
  roles            = $roles
  hardware         = $hardware
  security         = $security
  software         = $software
  winget           = $winget
  events           = $eventInfo
  hyperv           = $hyperv
  backup           = $backup
  network          = @($network | Where-Object { $_ -ne $null })
  tray             = $trayState
} | ConvertTo-Json -Depth 7 -Compress

if ($DryRun) {
  Write-Output $payload
  Write-Output "DryRun: nichts gesendet (Sammeldauer $([math]::Round(((Get-Date) - $started).TotalSeconds, 1)) s, $($payload.Length) Zeichen)."
  exit 0
}

try {
  $resp = Invoke-RestMethod -Method Post -Uri $cfg.reportUrl `
    -Headers @{ Authorization = "Bearer $($cfg.token)" } `
    -ContentType "application/json; charset=utf-8" `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 30
} catch {
  $err = $_.Exception.Message
  Safe { Write-AgentStatus "error" ("Bericht nicht gesendet: " + $err) $null }
  throw
}
Safe { Write-AgentStatus "ok" "Bericht gesendet" $resp }

Write-Output "Report gesendet: $($env:COMPUTERNAME) -> $($cfg.reportUrl)"
