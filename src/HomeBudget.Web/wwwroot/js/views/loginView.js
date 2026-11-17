import { apiFetch, escapeHtml } from '../app.js';

export function attachPasswordToggle(inputEl, toggleBtn) {
  if (!inputEl || !toggleBtn) return;
  toggleBtn.addEventListener('click', () => {
    const isPassword = inputEl.type === 'password';
    inputEl.type = isPassword ? 'text' : 'password';
    toggleBtn.textContent = isPassword ? 'Hide' : 'Show';
    toggleBtn.setAttribute('aria-pressed', isPassword ? 'true' : 'false');
  });
}

export function renderLoginView(container, authStatus, onAuthenticated) {
  const isSetup = Boolean(authStatus?.isSetupRequired);
  const noticeMessage = authStatus?.notice || '';

  if (isSetup) {
    container.innerHTML = `
      <div class="auth-card" style="max-width:540px;">
        <div style="display:flex;align-items:center;gap:12px;margin-bottom:16px;">
          <div class="brand-logo">HB</div>
          <div>
            <h1 style="margin:0;font-size:20px;font-weight:800;">HomeBudget Initial Setup</h1>
            <div style="font-size:12px;color:#64748B;">Create passphrases for your Admin (<code>admin</code>) and Household User (<code>user</code>) accounts</div>
          </div>
        </div>

        <div style="background:#EFF6FF;border:1px solid #BFDBFE;border-radius:10px;padding:12px;font-size:12px;color:#1E3A8A;margin-bottom:16px;line-height:1.5;">
          <strong>1-Time Bootstrap Token Required:</strong> Open <code>setup-token.txt</code> in your HomeBudget database folder on the Windows host (default: <code>C:\\ProgramData\\HomeBudget\\setup-token.txt</code>), or run in PowerShell:<br/>
          <code style="display:inline-block;margin-top:4px;background:#DBEAFE;padding:2px 6px;border-radius:4px;user-select:all;">Get-Content C:\\ProgramData\\HomeBudget\\setup-token.txt | Set-Clipboard</code>
        </div>

        <div id="authErrorBanner" role="alert" aria-live="polite" style="display:none;background:#FEF2F2;border:1px solid #FECACA;color:#B91C1C;border-radius:8px;padding:10px 12px;font-size:13px;font-weight:600;margin-bottom:14px;"></div>

        <form id="setupForm" style="display:flex;flex-direction:column;gap:12px;">
          <div>
            <label for="setupTokenInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Bootstrap Setup Token</label>
            <div style="display:flex;gap:6px;">
              <input type="password" id="setupTokenInput" class="input-control" style="flex:1;" autocomplete="one-time-code" required placeholder="Paste token from setup-token.txt" />
              <button type="button" class="btn btn-outline btn-sm" id="toggleSetupTokenBtn" aria-pressed="false">Show</button>
            </div>
          </div>

          <hr style="border:none;border-top:1px solid #E2E8F0;margin:4px 0;" />

          <div style="font-size:13px;font-weight:800;color:#0F172A;">1. Admin Account (Username: <code>admin</code>)</div>
          <div>
            <label for="setupAdminDisplayInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Admin Display Name</label>
            <input type="text" id="setupAdminDisplayInput" class="input-control" style="width:100%;" value="Admin" maxlength="64" required />
          </div>
          <div>
            <label for="setupAdminPassInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Admin Passphrase (12–128 characters, spaces allowed)</label>
            <div style="display:flex;gap:6px;">
              <input type="password" id="setupAdminPassInput" class="input-control" style="flex:1;" autocomplete="new-password" minlength="12" maxlength="128" required placeholder="At least 12 characters" />
              <button type="button" class="btn btn-outline btn-sm" id="toggleAdminPassBtn" aria-pressed="false">Show</button>
            </div>
          </div>
          <div>
            <label for="setupAdminConfirmInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Confirm Admin Passphrase</label>
            <div style="display:flex;gap:6px;">
              <input type="password" id="setupAdminConfirmInput" class="input-control" style="flex:1;" autocomplete="new-password" minlength="12" maxlength="128" required placeholder="Re-enter admin passphrase" />
              <button type="button" class="btn btn-outline btn-sm" id="toggleAdminConfirmBtn" aria-pressed="false">Show</button>
            </div>
          </div>

          <hr style="border:none;border-top:1px solid #E2E8F0;margin:4px 0;" />

          <div style="font-size:13px;font-weight:800;color:#0F172A;">2. Household User Account (Username: <code>user</code>)</div>
          <div>
            <label for="setupUserDisplayInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">User Display Name</label>
            <input type="text" id="setupUserDisplayInput" class="input-control" style="width:100%;" value="Household User" maxlength="64" required />
          </div>
          <div>
            <label for="setupUserPassInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">User Passphrase (12–128 characters, spaces allowed)</label>
            <div style="display:flex;gap:6px;">
              <input type="password" id="setupUserPassInput" class="input-control" style="flex:1;" autocomplete="new-password" minlength="12" maxlength="128" required placeholder="At least 12 characters" />
              <button type="button" class="btn btn-outline btn-sm" id="toggleUserPassBtn" aria-pressed="false">Show</button>
            </div>
          </div>
          <div>
            <label for="setupUserConfirmInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Confirm User Passphrase</label>
            <div style="display:flex;gap:6px;">
              <input type="password" id="setupUserConfirmInput" class="input-control" style="flex:1;" autocomplete="new-password" minlength="12" maxlength="128" required placeholder="Re-enter user passphrase" />
              <button type="button" class="btn btn-outline btn-sm" id="toggleUserConfirmBtn" aria-pressed="false">Show</button>
            </div>
          </div>

          <button type="submit" class="btn btn-primary" id="setupSubmitBtn" style="margin-top:6px;width:100%;">
            Complete Initial Setup &amp; Sign In as Admin
          </button>
        </form>
      </div>
    `;

    attachPasswordToggle(container.querySelector('#setupTokenInput'), container.querySelector('#toggleSetupTokenBtn'));
    attachPasswordToggle(container.querySelector('#setupAdminPassInput'), container.querySelector('#toggleAdminPassBtn'));
    attachPasswordToggle(container.querySelector('#setupAdminConfirmInput'), container.querySelector('#toggleAdminConfirmBtn'));
    attachPasswordToggle(container.querySelector('#setupUserPassInput'), container.querySelector('#toggleUserPassBtn'));
    attachPasswordToggle(container.querySelector('#setupUserConfirmInput'), container.querySelector('#toggleUserConfirmBtn'));

    const form = container.querySelector('#setupForm');
    const errBanner = container.querySelector('#authErrorBanner');
    const submitBtn = container.querySelector('#setupSubmitBtn');

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      errBanner.style.display = 'none';

      const bootstrapToken = container.querySelector('#setupTokenInput').value;
      const adminDisplayName = container.querySelector('#setupAdminDisplayInput').value.trim();
      const adminPassword = container.querySelector('#setupAdminPassInput').value;
      const adminConfirm = container.querySelector('#setupAdminConfirmInput').value;
      const userDisplayName = container.querySelector('#setupUserDisplayInput').value.trim();
      const userPassword = container.querySelector('#setupUserPassInput').value;
      const userConfirm = container.querySelector('#setupUserConfirmInput').value;

      if (adminPassword !== adminConfirm) {
        errBanner.textContent = 'Admin passphrases do not match.';
        errBanner.style.display = 'block';
        return;
      }
      if (userPassword !== userConfirm) {
        errBanner.textContent = 'Household User passphrases do not match.';
        errBanner.style.display = 'block';
        return;
      }

      submitBtn.disabled = true;
      submitBtn.textContent = 'Initializing Accounts...';

      try {
        await apiFetch('/api/auth/setup', {
          method: 'POST',
          body: JSON.stringify({
            bootstrapToken,
            adminDisplayName,
            adminPassword,
            userDisplayName,
            userPassword
          })
        });
        const loginRes = await apiFetch('/api/auth/login', {
          method: 'POST',
          body: JSON.stringify({
            username: 'admin',
            password: adminPassword,
            rememberMe: true
          })
        });
        if (onAuthenticated) await onAuthenticated(loginRes.user);
      } catch (err) {
        errBanner.textContent = err.message || 'Initial setup failed.';
        errBanner.style.display = 'block';
        submitBtn.disabled = false;
        submitBtn.textContent = 'Complete Initial Setup & Sign In as Admin';
      }
    });
    return;
  }

  container.innerHTML = `
    <div class="auth-card">
      <div style="display:flex;align-items:center;gap:12px;margin-bottom:18px;">
        <div class="brand-logo">HB</div>
        <div>
          <h1 style="margin:0;font-size:20px;font-weight:800;">Sign in to HomeBudget</h1>
          <div style="font-size:12px;color:#64748B;">Local Household Cashflow &amp; Budgeting</div>
        </div>
      </div>

      ${noticeMessage ? `
        <div id="authNoticeBanner" role="status" aria-live="polite" style="background:#FFFBEB;border:1px solid #FDE68A;color:#92400E;border-radius:8px;padding:10px 12px;font-size:13px;font-weight:600;margin-bottom:14px;">
          ${escapeHtml(noticeMessage)}
        </div>
      ` : ''}

      <div id="authErrorBanner" role="alert" aria-live="polite" style="display:none;background:#FEF2F2;border:1px solid #FECACA;color:#B91C1C;border-radius:8px;padding:10px 12px;font-size:13px;font-weight:600;margin-bottom:14px;"></div>

      <form id="loginForm" style="display:flex;flex-direction:column;gap:12px;">
        <div>
          <label for="loginUsernameInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Username</label>
          <input type="text" id="loginUsernameInput" class="input-control" style="width:100%;" autocomplete="username" required placeholder="admin or user" />
        </div>

        <div>
          <label for="loginPasswordInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Passphrase</label>
          <div style="display:flex;gap:6px;">
            <input type="password" id="loginPasswordInput" class="input-control" style="flex:1;" autocomplete="current-password" required placeholder="Enter passphrase" />
            <button type="button" class="btn btn-outline btn-sm" id="toggleLoginPassBtn" aria-pressed="false">Show</button>
          </div>
        </div>

        <label style="display:flex;align-items:center;gap:8px;font-size:13px;color:#334155;cursor:pointer;margin:2px 0;">
          <input type="checkbox" id="loginRememberCheck" checked />
          <span>Remember this device (90 days)</span>
        </label>

        <button type="submit" class="btn btn-primary" id="loginSubmitBtn" style="margin-top:4px;width:100%;">
          Sign In
        </button>
      </form>
    </div>
  `;

  attachPasswordToggle(container.querySelector('#loginPasswordInput'), container.querySelector('#toggleLoginPassBtn'));

  const form = container.querySelector('#loginForm');
  const errBanner = container.querySelector('#authErrorBanner');
  const submitBtn = container.querySelector('#loginSubmitBtn');

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    errBanner.style.display = 'none';

    const username = container.querySelector('#loginUsernameInput').value.trim();
    const password = container.querySelector('#loginPasswordInput').value;
    const rememberMe = container.querySelector('#loginRememberCheck').checked;

    submitBtn.disabled = true;
    submitBtn.textContent = 'Signing In...';

    try {
      const res = await apiFetch('/api/auth/login', {
        method: 'POST',
        body: JSON.stringify({ username, password, rememberMe })
      });
      if (onAuthenticated) await onAuthenticated(res.user);
    } catch (err) {
      errBanner.textContent = err.message || 'Sign in failed.';
      errBanner.style.display = 'block';
      submitBtn.disabled = false;
      submitBtn.textContent = 'Sign In';
    }
  });
}
