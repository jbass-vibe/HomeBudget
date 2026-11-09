<#
.SYNOPSIS
  Provisions the HomeBudget ASP.NET Core (.NET 10) site in Windows 11 IIS on port 8080,
  sets folder ACLs for C:\ProgramData\HomeBudget, and scopes Windows Firewall to LocalSubnet.
#>
$ErrorActionPreference = "Stop"

$SiteName = "HomeBudget"
$AppPoolName = "HomeBudget"
$PublishPath = "C:\inetpub\HomeBudget"
$DataPath = "C:\ProgramData\HomeBudget"
$BackupPath = "C:\ProgramData\HomeBudget\Backups"
$Port = 8080

Write-Host "1. Creating data and publish directories..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $PublishPath | Out-Null
New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
New-Item -ItemType Directory -Force -Path $BackupPath | Out-Null

Import-Module WebAdministration

Write-Host "2. Configuring IIS Application Pool ($AppPoolName)..." -ForegroundColor Cyan
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "managedRuntimeVersion" -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "startMode" -Value "AlwaysRunning"

Write-Host "3. Configuring IIS Website ($SiteName) on Port $Port..." -ForegroundColor Cyan
if (Test-Path "IIS:\Sites\$SiteName") {
    Remove-Website -Name $SiteName
}
New-Website -Name $SiteName -Port $Port -PhysicalPath $PublishPath -ApplicationPool $AppPoolName -Force | Out-Null

Write-Host "4. Granting NTFS permissions to IIS AppPool\$AppPoolName..." -ForegroundColor Cyan
icacls $PublishPath /grant "IIS AppPool\${AppPoolName}:(OI)(CI)RX" /T | Out-Null
icacls $DataPath /grant "IIS AppPool\${AppPoolName}:(OI)(CI)M" /T | Out-Null

Write-Host "5. Configuring LAN-Only Windows Defender Firewall Rule on TCP Port $Port..." -ForegroundColor Cyan
$FirewallRuleName = "HomeBudget-LAN-Port-$Port"
Remove-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $FirewallRuleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -Profile Private `
    -RemoteAddress LocalSubnet | Out-Null

Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
Start-Website -Name $SiteName -ErrorAction SilentlyContinue

Write-Host "HomeBudget deployed and active at http://localhost:$Port" -ForegroundColor Green
