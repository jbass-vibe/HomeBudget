# HomeBudget HTTPS Setup Runbook (Windows 11 + iPhone LAN)

This runbook configures trusted LAN-only HTTPS for HomeBudget using `mkcert` and IIS on port `8443`, so authentication cookies (`Secure` + `HttpOnly` + `SameSite=Lax`) work seamlessly on both the Windows 11 host and the household iPhone.

## 1. Prerequisites
- Windows 11 host on a private home LAN (e.g., `192.168.1.50` or `homebudget.local`).
- Administrator PowerShell on Windows 11.
- IIS with **Application Request Routing (ARR)** and **URL Rewrite** installed (or Kestrel HTTPS binding if running standalone).

## 2. Install `mkcert` and Generate Local CA + Server Certificate
1. Install `mkcert`:
   ```powershell
   winget install FiloSottile.mkcert
   ```
2. Install the local Root CA into the Windows Trusted Root store:
   ```powershell
   mkcert -install
   ```
3. Generate a PFX certificate for your Windows host's LAN IP and mDNS hostname (valid ~825 days):
   ```powershell
   mkdir C:\ProgramData\HomeBudget\certs -Force
   cd C:\ProgramData\HomeBudget\certs
   mkcert -pkcs12 -p12-file homebudget-lan.pfx localhost 127.0.0.1 192.168.1.50 homebudget.local
   ```
   *(Replace `192.168.1.50` with your host's actual static/DHCP-reserved LAN IP.)*

## 3. Configure IIS Reverse Proxy on HTTPS `:8443`
1. Import `C:\ProgramData\HomeBudget\certs\homebudget-lan.pfx` into **Local Machine -> Personal -> Certificates** (`certlm.msc`).
2. In **IIS Manager**:
   - Create/update the `HomeBudget` site with an **https** binding on port **8443**, selecting the imported `mkcert` certificate.
   - Proxy requests to Kestrel (e.g., `http://127.0.0.1:5000`) with **ARR** proxy enabled and `X-Forwarded-Proto` / `X-Forwarded-For` preserved.
3. Ensure `HomeBudget:RequireHttps` in `appsettings.json` remains `true` (default).

## 4. Restrict Windows Firewall to `LocalSubnet`
Run in Administrator PowerShell so port `8443` is reachable only from your home LAN:
```powershell
New-NetFirewallRule `
  -DisplayName "HomeBudget HTTPS (LAN Only)" `
  -Direction Inbound `
  -Protocol TCP `
  -LocalPort 8443 `
  -RemoteAddress LocalSubnet `
  -Action Allow `
  -Profile Private
```

## 5. Trust the `mkcert` Root CA on iPhone (iOS)
1. Locate the `mkcert` Root CA file on Windows:
   ```powershell
   mkcert -CAROOT
   ```
   Copy `rootCA.pem` from that folder to your iPhone (via AirDrop, local share, or email).
2. On iPhone:
   - Tap `rootCA.pem` -> **Allow** downloading the configuration profile.
   - Open **Settings -> General -> VPN & Device Management** -> select the `mkcert` profile -> **Install**.
   - **Critical iOS Step:** Open **Settings -> General -> About -> Certificate Trust Settings** and toggle **ON** full trust for the `mkcert` Root Certificate.
3. Open Safari on iPhone and navigate to `https://192.168.1.50:8443` (or `https://homebudget.local:8443`). Verify the padlock icon appears with no certificate warnings, then **Share -> Add to Home Screen**.

## 6. Certificate Renewal (~825 Days)
`mkcert` leaf certificates expire after ~825 days. Set a calendar reminder at ~2 years to re-run:
```powershell
cd C:\ProgramData\HomeBudget\certs
mkcert -pkcs12 -p12-file homebudget-lan.pfx localhost 127.0.0.1 192.168.1.50 homebudget.local
```
Re-import into `certlm.msc` and update the IIS `:8443` binding. Your iPhone does **not** need to re-install `rootCA.pem` as long as the same `mkcert` Root CA is used.
