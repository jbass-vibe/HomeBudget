#Requires -RunAsAdministrator
param(
    [string]$SiteName = "HomeBudget",
    [int]$Port = 80,
    [string]$PublishDir = "C:\inetpub\HomeBudget",
    [string]$DataDir = "C:\ProgramData\HomeBudget",
    [string]$BackupDir = "C:\ProgramData\HomeBudget\Backups"
)

Write-Host "1. Enabling Windows 11 IIS Features..." -ForegroundColor Cyan
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-CommonHttpFeatures, IIS-StaticContent, IIS-DefaultDocument, IIS-HttpErrors, IIS-HealthAndDiagnostics, IIS-HttpLogging, IIS-Security, IIS-RequestFiltering, IIS-Performance, IIS-WebServerManagementTools, IIS-ManagementConsole -All -NoRestart

Write-Host "2. Creating Local Data and Backup Directories..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $PublishDir, $DataDir, $BackupDir | Out-Null

Write-Host "3. Configuring IIS Application Pool & Site..." -ForegroundColor Cyan
Import-Module WebAdministration

if (Test-Path "IIS:\Sites\Default Web Site") {
    Stop-Website -Name "Default Web Site" -ErrorAction SilentlyContinue
}

if (!(Test-Path "IIS:\AppPools\$SiteName")) {
    New-WebAppPool -Name $SiteName
}
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name "managedRuntimeVersion" -Value ""
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name "startMode" -Value "AlwaysRunning"

if (!(Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -Port $Port -PhysicalPath $PublishDir -ApplicationPool $SiteName
}

Write-Host "4. Granting Modify Permissions to IIS AppPool Identity on Local Data & Backup Folders..." -ForegroundColor Cyan
$AppPoolSid = "IIS AppPool\$SiteName"
$acl = Get-Acl $DataDir
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($AppPoolSid, "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl -Path $DataDir -AclObject $acl

Write-Host "5. Creating Strictly Scoped LAN-Only Windows Defender Firewall Rule..." -ForegroundColor Cyan
$FirewallRuleName = "HomeBudget-IIS-LAN-Only"
Remove-NetFirewallRule -DisplayName $FirewallRuleName -ErrorAction SilentlyContinue

New-NetFirewallRule -DisplayName $FirewallRuleName `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort $Port `
    -Action Allow `
    -Profile Private `
    -RemoteAddress LocalSubnet `
    -Description "Allows inbound HTTP access to HomeBudget IIS site strictly from devices on the local subnet over Private Wi-Fi/LAN."

$HostName = $env:COMPUTERNAME.ToLower()
$LocalIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notmatch 'Loopback' -and $_.IPAddress -match '^(192\.168\.|10\.|172\.)' } | Select-Object -First 1).IPAddress

Write-Host "`nDeployment Provisioning Complete!" -ForegroundColor Green
Write-Host "Local Database:   $DataDir\budget.db"
Write-Host "Local Backups:    $BackupDir"
Write-Host "PC Admin URL:     http://localhost:$Port"
Write-Host "Wife's iPhone URL: http://$HostName.local:$Port  (or http://$LocalIp`:$Port)"
