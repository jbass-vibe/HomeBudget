import {
  state,
  apiFetch,
  formatCurrency,
  escapeHtml,
  formatBackupStatus,
  sortCategories,
  setSortPreference,
  showToast,
  openModal,
  closeModal,
  refreshReferenceData,
  openCategoryManagerModal,
  openClearTransactionHistoryModal,
  openResetAllDatabaseModal,
  openRestoreDatabaseModal
} from '../app.js';

export function sortRules(rules, catNameById = {}, sortMode = 'priority_asc') {
  const list = Array.isArray(rules) ? [...rules] : [];
  return list.sort((a, b) => {
    const patA = String(a.matchPattern || '');
    const patB = String(b.matchPattern || '');
    switch (sortMode) {
      case 'alpha_asc':
        return patA.localeCompare(patB, undefined, { sensitivity: 'base' });
      case 'category_asc': {
        const catA = String(catNameById[a.categoryId] || a.categoryId || '');
        const catB = String(catNameById[b.categoryId] || b.categoryId || '');
        return catA.localeCompare(catB, undefined, { sensitivity: 'base' }) ||
          (Number(a.priority ?? 100) - Number(b.priority ?? 100));
      }
      case 'recent': {
        const tsA = String(a.updatedAt || a.createdAt || '');
        const tsB = String(b.updatedAt || b.createdAt || '');
        if (tsB !== tsA) return tsB.localeCompare(tsA);
        return Number(b.ruleId || 0) - Number(a.ruleId || 0);
      }
      case 'priority_asc':
      default: {
        const prioDiff = Number(a.priority ?? 100) - Number(b.priority ?? 100);
        if (prioDiff !== 0) return prioDiff;
        const lenDiff = patB.length - patA.length;
        if (lenDiff !== 0) return lenDiff;
        return Number(a.ruleId || 0) - Number(b.ruleId || 0);
      }
    }
  });
}

export function sortBackups(backups, sortMode = 'date_desc') {
  const list = Array.isArray(backups) ? [...backups] : [];
  return list.sort((a, b) => {
    const tsA = new Date(a.createdAtUtc || 0).getTime();
    const tsB = new Date(b.createdAtUtc || 0).getTime();
    const sizeA = Number(a.sizeBytes || 0);
    const sizeB = Number(b.sizeBytes || 0);
    const reasonA = String(a.reasonLabel || a.fileName || '');
    const reasonB = String(b.reasonLabel || b.fileName || '');

    switch (sortMode) {
      case 'date_asc':
        return tsA - tsB;
      case 'size_desc':
        return sizeB - sizeA || (tsB - tsA);
      case 'size_asc':
        return sizeA - sizeB || (tsB - tsA);
      case 'reason_asc':
        return reasonA.localeCompare(reasonB, undefined, { sensitivity: 'base' }) || (tsB - tsA);
      case 'date_desc':
      default:
        return tsB - tsA;
    }
  });
}

function formatByteSize(bytes) {
  const n = Number(bytes || 0);
  if (n >= 1024 * 1024) {
    return `${(n / (1024 * 1024)).toFixed(2)} MB`;
  }
  return `${(n / 1024).toFixed(1)} KB`;
}

export async function renderTopologyView(container) {
  const [topology, settings, backupInfo] = await Promise.all([
    apiFetch('/api/topology'),
    apiFetch('/api/settings'),
    apiFetch('/api/backups')
  ]);

  const rawCategories = topology.categories;
  const catNameById = Object.fromEntries(rawCategories.map(c => [c.categoryId, c.name]));
  const categories = sortCategories(rawCategories, state.topologyCategorySort);
  const dropdownCategories = sortCategories(rawCategories, state.categoryDropdownSort);
  const rules = sortRules(topology.rules, catNameById, state.topologyRuleSort);

  const backupConfig = backupInfo.config || {
    autoBackupMode: 'Coalesced',
    debounceSeconds: 30,
    minIntervalMinutes: 10,
    maxRetainedBackups: 15
  };
  const backupStatus = backupInfo.status || {
    totalSnapshotCount: (backupInfo.backups || []).length,
    totalBytesOnDisk: (backupInfo.backups || []).reduce((acc, b) => acc + (b.sizeBytes || 0), 0),
    pendingChangesCount: 0,
    pendingReasonSummary: '',
    nextScheduledBackupUtc: null,
    lastSnapshotUtc: backupInfo.backups?.[0]?.createdAtUtc || null
  };
  const sortedBackups = sortBackups(backupInfo.backups || [], state.backupsSort);

  container.innerHTML = `
    <div class="page-header">
      <div>
        <h1 class="page-title">Categories, Rules, Income &amp; Backups</h1>
        <p class="page-subtitle">Manage visualization category topology, retroactive rule back-propagation, and local SQLite backups</p>
      </div>
    </div>

    <!-- 1. Category Topology & Merge Manager -->
    <div class="card-panel">
      <div class="toolbar-row">
        <div>
          <h2 style="margin:0;font-size:17px;font-weight:800;">1. Spending Category Topology</h2>
          <p style="margin:2px 0 0 0;font-size:12px;color:#64748B;">Rename, reorder, or merge categories (merging automatically reassigns historical transactions)</p>
        </div>
        <div style="display:flex;flex-wrap:wrap;align-items:center;gap:8px;">
          <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
            Sort Table:
            <select id="topologyCatSortSelect" class="select-control" style="font-size:12px;padding:4px 8px;">
              <option value="custom_order" ${state.topologyCategorySort === 'custom_order' ? 'selected' : ''}>Custom Order</option>
              <option value="alpha_asc" ${state.topologyCategorySort === 'alpha_asc' ? 'selected' : ''}>Alphabetical (A-Z)</option>
              <option value="recent" ${state.topologyCategorySort === 'recent' ? 'selected' : ''}>Recent Changes</option>
              <option value="budget_desc" ${state.topologyCategorySort === 'budget_desc' ? 'selected' : ''}>Highest Budget ($)</option>
            </select>
          </label>
          <button class="btn btn-outline btn-sm" id="mergeCatModalBtn">Merge Categories...</button>
          <button class="btn btn-primary btn-sm" id="addCatModalBtn">+ New Category</button>
        </div>
      </div>

      <div class="table-responsive">
        <table class="data-table">
          <thead>
            <tr>
              <th>Order</th>
              <th>Color</th>
              <th>Category Name</th>
              <th>Big-Box Merchant?</th>
              <th>Fixed Monthly Budget</th>
              <th>Actions (Save / Delete)</th>
            </tr>
          </thead>
          <tbody>
            ${categories.map(c => `
              <tr data-cat-id="${c.categoryId}">
                <td><input type="number" class="input-control cat-order" value="${c.sortOrder}" style="width:70px;" /></td>
                <td><input type="color" class="cat-color" value="${escapeHtml(c.colorHex)}" style="width:40px;height:32px;border:none;background:none;cursor:pointer;" /></td>
                <td><input type="text" class="input-control cat-name" value="${escapeHtml(c.name)}" style="width:200px;font-weight:700;" /></td>
                <td><input type="checkbox" class="cat-bigbox" ${c.isBigBoxMerchantCategory ? 'checked' : ''} /></td>
                <td><input type="number" step="10" min="0" class="input-control cat-budget" value="${c.monthlyBudget}" style="width:120px;" /></td>
                <td style="white-space:nowrap;">
                  <button class="btn btn-primary btn-sm save-cat-btn">Save</button>
                  <button class="btn btn-danger btn-sm del-cat-btn" data-cat-id="${c.categoryId}">Delete</button>
                </td>
              </tr>
            `).join('')}
          </tbody>
        </table>
      </div>
    </div>

    <!-- 2. Auto-Categorization Rules & Historical Back-Propagation Engine -->
    <div class="card-panel">
      <div class="toolbar-row">
        <div>
          <h2 style="margin:0;font-size:17px;font-weight:800;">2. Auto-Categorization Rules &amp; Historical Back-Propagation</h2>
          <p style="margin:2px 0 0 0;font-size:12px;color:#64748B;">Evaluated in ascending Priority order during CSV upload and retroactive back-propagation</p>
        </div>
        <div style="display:flex;flex-wrap:wrap;align-items:center;gap:8px;">
          <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
            Sort Rules:
            <select id="topologyRuleSortSelect" class="select-control" style="font-size:12px;padding:4px 8px;">
              <option value="priority_asc" ${state.topologyRuleSort === 'priority_asc' ? 'selected' : ''}>Evaluation Priority</option>
              <option value="alpha_asc" ${state.topologyRuleSort === 'alpha_asc' ? 'selected' : ''}>Match Pattern (A-Z)</option>
              <option value="category_asc" ${state.topologyRuleSort === 'category_asc' ? 'selected' : ''}>Target Category (A-Z)</option>
              <option value="recent" ${state.topologyRuleSort === 'recent' ? 'selected' : ''}>Recent Changes</option>
            </select>
          </label>
          <button class="btn btn-outline btn-sm" id="previewBackPropBtn">&#9889; Preview &amp; Back-Propagate Rules</button>
          <button class="btn btn-primary btn-sm" id="addRuleModalBtn">+ New Rule</button>
        </div>
      </div>

      <div id="backPropPreviewMount"></div>

      <div class="table-responsive">
        <table class="data-table">
          <thead>
            <tr>
              <th>Priority</th>
              <th>Match Pattern</th>
              <th>Regex?</th>
              <th>Target Category</th>
              <th>Default Subcategory</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            ${rules.map(r => `
              <tr data-rule-id="${r.ruleId}">
                <td>${r.priority}</td>
                <td><code>${escapeHtml(r.matchPattern)}</code></td>
                <td>${r.isRegex ? 'Yes' : 'No'}</td>
                <td><strong>${escapeHtml(catNameById[r.categoryId] || r.categoryId)}</strong></td>
                <td>${r.defaultSubcategory ? escapeHtml(r.defaultSubcategory) : '<span style="color:#94A3B8;">None</span>'}</td>
                <td><button class="btn btn-danger btn-sm del-rule-btn" data-id="${r.ruleId}">Delete</button></td>
              </tr>
            `).join('')}
          </tbody>
        </table>
      </div>
    </div>

    <!-- 3. Tiered Income Configuration -->
    <div class="card-panel">
      <h2 style="margin:0 0 12px 0;font-size:17px;font-weight:800;">3. Household Income Configuration (Google Direct Deposit + Persistent Fallback)</h2>
      <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px;align-items:end;">
        <div>
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Google Payroll Direct Deposit Keyword</label>
          <input type="text" id="setGooglePattern" class="input-control" style="width:100%;" value="${escapeHtml(settings.googlePayrollMatchPattern)}" />
        </div>
        <div>
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Persistent Monthly Income Fallback ($)</label>
          <input type="number" step="100" min="0" id="setPersistentIncome" class="input-control" style="width:100%;" value="${settings.persistentMonthlyIncome}" />
        </div>
        <div>
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Current Month-to-Date Income Mode</label>
          <select id="setCurrentMonthMode" class="select-control" style="width:100%;">
            <option value="Auto" ${settings.currentMonthIncomeMode === 'Auto' ? 'selected' : ''}>Auto (Use Posted Google Deposits if &gt; 0, else Fallback)</option>
            <option value="ForceFullExpected" ${settings.currentMonthIncomeMode === 'ForceFullExpected' ? 'selected' : ''}>Use Full Persistent Monthly Fallback for Current MTD</option>
          </select>
        </div>
        <div>
          <button class="btn btn-primary" id="saveSettingsBtn">Save Income Settings</button>
        </div>
      </div>
    </div>

    <!-- 4. Local SQLite Backup, Restore & Database Management -->
    <div class="card-panel">
      <div class="toolbar-row" style="margin-bottom:14px;">
        <div>
          <h2 style="margin:0;font-size:17px;font-weight:800;">4. Local SQLite Backup, Restore &amp; Database Management</h2>
          <p style="margin:2px 0 0 0;font-size:12px;color:#64748B;">
            Path: <code>${escapeHtml(backupInfo.backupDirectoryPath)}</code> &bull; Smart Coalesced Policy prevents excessive backup churn during rapid edits
          </p>
        </div>
        <div style="display:flex;flex-wrap:wrap;gap:8px;align-items:center;">
          ${backupStatus.pendingChangesCount > 0 ? `
            <button class="btn btn-outline btn-sm" id="flushPendingBackupBtn" style="border-color:#2563EB;color:#1D4ED8;">
              &#9201; Flush ${backupStatus.pendingChangesCount} Pending Edit(s) Now
            </button>
          ` : ''}
          <button class="btn btn-outline btn-sm" id="clearHistoryAdminBtn" style="border-color:#DC2626;color:#DC2626;">
            &#128465; Clear Transaction History...
          </button>
          <button class="btn btn-danger btn-sm" id="resetAllDatabaseAdminBtn">
            &#9888;&#65039; Reset All Information...
          </button>
          <button class="btn btn-outline btn-sm" id="restoreDatabaseModalBtn" style="border-color:#D97706;color:#B45309;">
            &#8634; Restore Database...
          </button>
          <button class="btn btn-primary btn-sm" id="manualBackupBtn">+ Create Manual Snapshot Now</button>
        </div>
      </div>

      <!-- Storage & Coalesced Queue KPI Summary -->
      <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin-bottom:16px;">
        <div style="background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;padding:10px 14px;">
          <div style="font-size:11px;font-weight:700;color:#64748B;text-transform:uppercase;">Total Snapshots</div>
          <div style="font-size:18px;font-weight:800;color:#0F172A;margin-top:2px;">
            ${backupStatus.totalSnapshotCount} <span style="font-size:12px;font-weight:600;color:#64748B;">/ max ${backupConfig.maxRetainedBackups}</span>
          </div>
        </div>
        <div style="background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;padding:10px 14px;">
          <div style="font-size:11px;font-weight:700;color:#64748B;text-transform:uppercase;">Disk Usage</div>
          <div style="font-size:18px;font-weight:800;color:#0F172A;margin-top:2px;">
            ${formatByteSize(backupStatus.totalBytesOnDisk)}
          </div>
        </div>
        <div style="background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;padding:10px 14px;">
          <div style="font-size:11px;font-weight:700;color:#64748B;text-transform:uppercase;">Latest Snapshot</div>
          <div style="font-size:13px;font-weight:700;color:#0F172A;margin-top:4px;">
            ${backupStatus.lastSnapshotUtc ? escapeHtml(new Date(backupStatus.lastSnapshotUtc).toLocaleString()) : 'None'}
          </div>
        </div>
        <div style="background:${backupStatus.pendingChangesCount > 0 ? '#EFF6FF' : '#F8FAFC'};border:1px solid ${backupStatus.pendingChangesCount > 0 ? '#BFDBFE' : '#E2E8F0'};border-radius:10px;padding:10px 14px;">
          <div style="font-size:11px;font-weight:700;color:${backupStatus.pendingChangesCount > 0 ? '#1E40AF' : '#64748B'};text-transform:uppercase;">Coalesced Queue</div>
          <div style="font-size:13px;font-weight:700;color:${backupStatus.pendingChangesCount > 0 ? '#1D4ED8' : '#0F172A'};margin-top:4px;">
            ${backupStatus.pendingChangesCount > 0
              ? `${backupStatus.pendingChangesCount} edit(s) pending (${escapeHtml(backupStatus.pendingReasonSummary)})`
              : 'Idle (0 pending)'}
          </div>
        </div>
      </div>

      <!-- Backup Policy Configuration Form -->
      <div style="background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;padding:14px;margin-bottom:16px;">
        <div style="font-size:13px;font-weight:800;color:#0F172A;margin-bottom:10px;">
          Automatic Snapshot Frequency &amp; Retention Policy
        </div>
        <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:12px;align-items:end;">
          <div>
            <label style="font-size:11px;font-weight:700;color:#475569;display:block;margin-bottom:4px;">Auto-Backup Mode</label>
            <select id="backupAutoModeSelect" class="select-control" style="width:100%;">
              <option value="Coalesced" ${backupConfig.autoBackupMode === 'Coalesced' ? 'selected' : ''}>Smart Coalesced (Recommended)</option>
              <option value="MajorOnly" ${backupConfig.autoBackupMode === 'MajorOnly' ? 'selected' : ''}>Major Operations Only (CSV / Bulk / Merge)</option>
              <option value="ManualOnly" ${backupConfig.autoBackupMode === 'ManualOnly' ? 'selected' : ''}>Manual Snapshots Only</option>
              <option value="EveryChange" ${backupConfig.autoBackupMode === 'EveryChange' ? 'selected' : ''}>Every Mutation (Legacy)</option>
            </select>
          </div>
          <div>
            <label style="font-size:11px;font-weight:700;color:#475569;display:block;margin-bottom:4px;">Quiet Debounce Window (sec)</label>
            <input type="number" min="5" max="600" id="backupDebounceInput" class="input-control" style="width:100%;" value="${backupConfig.debounceSeconds}" />
          </div>
          <div>
            <label style="font-size:11px;font-weight:700;color:#475569;display:block;margin-bottom:4px;">Min Cooldown Interval (min)</label>
            <input type="number" min="0" max="240" id="backupCooldownInput" class="input-control" style="width:100%;" value="${backupConfig.minIntervalMinutes}" />
          </div>
          <div>
            <label style="font-size:11px;font-weight:700;color:#475569;display:block;margin-bottom:4px;">Max Retained Snapshots</label>
            <input type="number" min="3" max="200" id="backupMaxRetainedInput" class="input-control" style="width:100%;" value="${backupConfig.maxRetainedBackups}" />
          </div>
          <div>
            <button class="btn btn-primary" id="saveBackupPolicyBtn" style="width:100%;">Save Policy &amp; Enforce Retention</button>
          </div>
        </div>
      </div>

      <!-- Snapshot List Toolbar -->
      <div class="toolbar-row" style="margin-bottom:10px;">
        <div style="display:flex;flex-wrap:wrap;align-items:center;gap:10px;">
          <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
            Sort Snapshots:
            <select id="backupSortSelect" class="select-control" style="font-size:12px;padding:4px 8px;">
              <option value="date_desc" ${state.backupsSort === 'date_desc' ? 'selected' : ''}>Newest First</option>
              <option value="date_asc" ${state.backupsSort === 'date_asc' ? 'selected' : ''}>Oldest First</option>
              <option value="size_desc" ${state.backupsSort === 'size_desc' ? 'selected' : ''}>Largest Size</option>
              <option value="size_asc" ${state.backupsSort === 'size_asc' ? 'selected' : ''}>Smallest Size</option>
              <option value="reason_asc" ${state.backupsSort === 'reason_asc' ? 'selected' : ''}>Trigger Reason (A-Z)</option>
            </select>
          </label>
        </div>
        <div style="display:flex;flex-wrap:wrap;align-items:center;gap:8px;">
          <button class="btn btn-danger btn-sm" id="bulkDeleteBackupsBtn" disabled>Delete Selected (0)</button>
          <button class="btn btn-outline btn-sm" id="pruneBackupsBtn">Prune Old Snapshots...</button>
        </div>
      </div>

      <div class="table-responsive">
        <table class="data-table">
          <thead>
            <tr>
              <th style="width:36px;"><input type="checkbox" id="selectAllBackupsCheck" title="Select all snapshots" /></th>
              <th>Snapshot Filename</th>
              <th>Trigger / Reason</th>
              <th>Created (Local Time)</th>
              <th>Size</th>
              <th>Actions (Download / Restore / Delete)</th>
            </tr>
          </thead>
          <tbody>
            ${sortedBackups.length === 0 ? `
              <tr><td colspan="6" style="text-align:center;color:#64748B;">No snapshots created yet.</td></tr>
            ` : sortedBackups.map(b => `
              <tr data-backup-file="${escapeHtml(b.fileName)}">
                <td><input type="checkbox" class="backup-row-check" value="${escapeHtml(b.fileName)}" /></td>
                <td><code>${escapeHtml(b.fileName)}</code></td>
                <td>
                  <span style="display:inline-block;background:#EFF6FF;color:#1D4ED8;border:1px solid #BFDBFE;border-radius:999px;padding:2px 8px;font-size:11px;font-weight:700;">
                    ${escapeHtml(b.reasonLabel || 'Snapshot')}
                  </span>
                </td>
                <td>${escapeHtml(new Date(b.createdAtUtc).toLocaleString())}</td>
                <td>${formatByteSize(b.sizeBytes)}</td>
                <td style="white-space:nowrap;display:flex;gap:6px;align-items:center;">
                  <a class="btn btn-outline btn-sm" href="/api/backups/download/${encodeURIComponent(b.fileName)}" download>
                    &#11015; Download
                  </a>
                  <button class="btn btn-outline btn-sm restore-backup-btn" data-file="${escapeHtml(b.fileName)}" style="border-color:#D97706;color:#B45309;">
                    &#8634; Restore DB...
                  </button>
                  <button class="btn btn-danger btn-sm del-backup-btn" data-file="${escapeHtml(b.fileName)}">
                    Delete
                  </button>
                </td>
              </tr>
            `).join('')}
          </tbody>
        </table>
      </div>
    </div>
  `;

  const topologyCatSortSelect = container.querySelector('#topologyCatSortSelect');
  if (topologyCatSortSelect) {
    topologyCatSortSelect.onchange = (e) => {
      setSortPreference('topologyCategorySort', e.target.value);
      renderTopologyView(container);
    };
  }

  const topologyRuleSortSelect = container.querySelector('#topologyRuleSortSelect');
  if (topologyRuleSortSelect) {
    topologyRuleSortSelect.onchange = (e) => {
      setSortPreference('topologyRuleSort', e.target.value);
      renderTopologyView(container);
    };
  }

  // Save inline Category edits
  container.querySelectorAll('.save-cat-btn').forEach(btn => {
    btn.onclick = async () => {
      const tr = btn.closest('tr');
      const catId = Number(tr.dataset.catId);
      const sortOrder = Number(tr.querySelector('.cat-order').value || 100);
      const colorHex = tr.querySelector('.cat-color').value;
      const name = tr.querySelector('.cat-name').value.trim();
      const isBigBoxMerchantCategory = tr.querySelector('.cat-bigbox').checked;
      const monthlyBudget = Number(tr.querySelector('.cat-budget').value || 0);

      await apiFetch(`/api/categories/${catId}`, {
        method: 'PUT',
        body: JSON.stringify({ name, isBigBoxMerchantCategory, monthlyBudget, sortOrder, colorHex })
      });
      await refreshReferenceData();
      showToast(`Updated category ${name}`);
      renderTopologyView(container);
    };
  });

  // Delete Category Handler
  container.querySelectorAll('.del-cat-btn').forEach(btn => {
    btn.onclick = () => {
      const catId = Number(btn.dataset.catId);
      const catObj = rawCategories.find(c => c.categoryId === catId);
      if (catObj) {
        openCategoryManagerModal(catObj, () => renderTopologyView(container));
      }
    };
  });

  // Add Category Modal
  container.querySelector('#addCatModalBtn').onclick = () => {
    openModal(`
      <h3 style="margin-top:0;">Add New Spending Category</h3>
      <div style="display:flex;flex-direction:column;gap:12px;margin:14px 0;">
        <input type="text" id="newCatName" class="input-control" placeholder="Category Name (e.g., Childcare)" />
        <input type="number" step="10" min="0" id="newCatBudget" class="input-control" placeholder="Monthly Budget ($)" value="200" />
        <input type="color" id="newCatColor" value="#2563EB" style="width:60px;height:36px;" />
        <label style="display:flex;align-items:center;gap:8px;font-size:13px;">
          <input type="checkbox" id="newCatBigBox" />
          <span>Mark as Big-Box Merchant Category</span>
        </label>
      </div>
      <div style="display:flex;justify-content:flex-end;gap:10px;">
        <button class="btn btn-outline" id="cancelNewCat">Cancel</button>
        <button class="btn btn-primary" id="saveNewCat">Create</button>
      </div>
    `, (m) => {
      m.querySelector('#cancelNewCat').onclick = closeModal;
      m.querySelector('#saveNewCat').onclick = async () => {
        await apiFetch('/api/categories', {
          method: 'POST',
          body: JSON.stringify({
            name: m.querySelector('#newCatName').value.trim(),
            monthlyBudget: Number(m.querySelector('#newCatBudget').value || 0),
            colorHex: m.querySelector('#newCatColor').value,
            isBigBoxMerchantCategory: m.querySelector('#newCatBigBox').checked,
            sortOrder: 100
          })
        });
        closeModal();
        showToast('Category created');
        renderTopologyView(container);
      };
    });
  };

  // Merge Categories Modal
  container.querySelector('#mergeCatModalBtn').onclick = () => {
    openModal(`
      <h3 style="margin-top:0;">Merge Category Topology</h3>
      <p style="font-size:13px;color:#64748B;">Reassigns all historical transactions and rules from Source into Target, then deletes Source.</p>
      <div style="display:flex;flex-direction:column;gap:12px;margin:14px 0;">
        <div>
          <label style="font-size:12px;font-weight:700;display:block;">Merge Source Category (Will be removed)</label>
          <select id="mergeSourceSel" class="select-control" style="width:100%;">
            ${dropdownCategories.map(c => `<option value="${c.categoryId}">${escapeHtml(c.name)}</option>`).join('')}
          </select>
        </div>
        <div>
          <label style="font-size:12px;font-weight:700;display:block;">Into Target Category</label>
          <select id="mergeTargetSel" class="select-control" style="width:100%;">
            ${dropdownCategories.map((c, i) => `<option value="${c.categoryId}" ${i === 1 ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}
          </select>
        </div>
      </div>
      <div style="display:flex;justify-content:flex-end;gap:10px;">
        <button class="btn btn-outline" id="cancelMergeBtn">Cancel</button>
        <button class="btn btn-primary" id="confirmMergeBtn">Merge &amp; Back-Propagate</button>
      </div>
    `, (m) => {
      m.querySelector('#cancelMergeBtn').onclick = closeModal;
      m.querySelector('#confirmMergeBtn').onclick = async () => {
        const srcId = Number(m.querySelector('#mergeSourceSel').value);
        const tgtId = Number(m.querySelector('#mergeTargetSel').value);
        if (srcId === tgtId) {
          alert('Select two different categories.');
          return;
        }
        const res = await apiFetch(`/api/categories/${srcId}/merge-into/${tgtId}`, { method: 'POST' });
        closeModal();
        showToast(`Merged! Reassigned ${res.reassignedTransactionsCount} transactions • ${formatBackupStatus(res.backupFileCreated)}`);
        renderTopologyView(container);
      };
    });
  };

  // Add Rule Modal
  container.querySelector('#addRuleModalBtn').onclick = () => {
    openModal(`
      <h3 style="margin-top:0;">Add Auto-Categorization Rule</h3>
      <div style="display:flex;flex-direction:column;gap:12px;margin:14px 0;">
        <input type="text" id="newRulePattern" class="input-control" placeholder="Substring or Regex (e.g., WHOLEFDS, TRADER JOE)" />
        <select id="newRuleCat" class="select-control">
          ${dropdownCategories.map(c => `<option value="${c.categoryId}">${escapeHtml(c.name)}</option>`).join('')}
        </select>
        <input type="text" id="newRuleSubcat" class="input-control" placeholder="Default Subcategory (Optional)" />
        <input type="number" id="newRulePriority" class="input-control" value="50" placeholder="Priority (Lower = Earlier)" />
        <label style="display:flex;align-items:center;gap:8px;font-size:13px;">
          <input type="checkbox" id="newRuleRegex" />
          <span>Evaluate pattern as Regular Expression</span>
        </label>
      </div>
      <div style="display:flex;justify-content:flex-end;gap:10px;">
        <button class="btn btn-outline" id="cancelNewRule">Cancel</button>
        <button class="btn btn-primary" id="saveNewRule">Create Rule</button>
      </div>
    `, (m) => {
      m.querySelector('#cancelNewRule').onclick = closeModal;
      m.querySelector('#saveNewRule').onclick = async () => {
        try {
          await apiFetch('/api/rules', {
            method: 'POST',
            body: JSON.stringify({
              matchPattern: m.querySelector('#newRulePattern').value.trim(),
              categoryId: Number(m.querySelector('#newRuleCat').value),
              defaultSubcategory: m.querySelector('#newRuleSubcat').value.trim() || null,
              priority: Number(m.querySelector('#newRulePriority').value || 50),
              isRegex: m.querySelector('#newRuleRegex').checked
            })
          });
          closeModal();
          showToast('Rule added');
          renderTopologyView(container);
        } catch (err) {
          alert(`Rule error: ${err.message}`);
        }
      };
    });
  };

  // Delete Rule
  container.querySelectorAll('.del-rule-btn').forEach(btn => {
    btn.onclick = async () => {
      const id = btn.dataset.id;
      await apiFetch(`/api/rules/${id}`, { method: 'DELETE' });
      showToast('Rule deleted');
      renderTopologyView(container);
    };
  });

  // Preview & Commit Historical Back-Propagation
  container.querySelector('#previewBackPropBtn').onclick = async () => {
    const previewMount = container.querySelector('#backPropPreviewMount');
    previewMount.innerHTML = `<div style="padding:12px;">Running dry-run across historical transactions...</div>`;
    const res = await apiFetch('/api/rules/back-propagate', {
      method: 'POST',
      body: JSON.stringify({ includeManuallyLocked: false, dryRun: true })
    });

    if (res.affectedTransactionsCount === 0) {
      previewMount.innerHTML = `
        <div style="padding:12px;margin-bottom:14px;background:#ECFDF5;border-radius:8px;color:#059669;font-weight:600;">
          &#10003; All ${res.evaluatedCount} historical transactions already match your current rule topology!
        </div>`;
      return;
    }

    previewMount.innerHTML = `
      <div style="padding:14px;margin-bottom:14px;background:#EFF6FF;border:1px solid #BFDBFE;border-radius:10px;">
        <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px;">
          <strong>Dry-Run Preview: ${res.affectedTransactionsCount} of ${res.evaluatedCount} historical transactions will be updated</strong>
          <button class="btn btn-primary btn-sm" id="commitBackPropBtn">Commit Back-Propagation &amp; Backup</button>
        </div>
        <div style="max-height:200px;overflow-y:auto;font-size:12px;">
          ${res.changes.map(ch => `
            <div style="padding:4px 0;border-bottom:1px dashed #CBD5E1;">
              ${escapeHtml(ch.transactionDate)} &bull; <strong>${escapeHtml(ch.rawDescription)}</strong> (${formatCurrency(ch.amount)}):
              <span style="color:#64748B;">${escapeHtml(ch.oldCategoryName)}</span> &rarr;
              <strong style="color:#2563EB;">${escapeHtml(ch.newCategoryName)}</strong> (Rule: <code>${escapeHtml(ch.matchedRulePattern)}</code>)
            </div>
          `).join('')}
        </div>
      </div>
    `;

    previewMount.querySelector('#commitBackPropBtn').onclick = async () => {
      const committed = await apiFetch('/api/rules/back-propagate', {
        method: 'POST',
        body: JSON.stringify({ includeManuallyLocked: false, dryRun: false })
      });
      showToast(`Back-propagated ${committed.affectedTransactionsCount} transactions • ${formatBackupStatus(committed.backupFileCreated)}`);
      renderTopologyView(container);
    };
  };

  // Save Income Settings
  container.querySelector('#saveSettingsBtn').onclick = async () => {
    const res = await apiFetch('/api/settings', {
      method: 'PUT',
      body: JSON.stringify({
        googlePayrollMatchPattern: container.querySelector('#setGooglePattern').value.trim(),
        persistentMonthlyIncome: Number(container.querySelector('#setPersistentIncome').value || 10000),
        currentMonthIncomeMode: container.querySelector('#setCurrentMonthMode').value,
        geminiApiKey: ''
      })
    });
    showToast(`Household income settings saved • ${formatBackupStatus(res?.backupFileCreated)}`);
  };

  // ============================================================================
  // 4. BACKUP MANAGEMENT HANDLERS
  // ============================================================================

  const backupSortSelect = container.querySelector('#backupSortSelect');
  if (backupSortSelect) {
    backupSortSelect.onchange = (e) => {
      setSortPreference('backupsSort', e.target.value);
      renderTopologyView(container);
    };
  }

  // Save Backup Policy Config
  const saveBackupPolicyBtn = container.querySelector('#saveBackupPolicyBtn');
  if (saveBackupPolicyBtn) {
    saveBackupPolicyBtn.onclick = async () => {
      const autoBackupMode = container.querySelector('#backupAutoModeSelect').value;
      const debounceSeconds = Number(container.querySelector('#backupDebounceInput').value || 30);
      const minIntervalMinutes = Number(container.querySelector('#backupCooldownInput').value || 10);
      const maxRetainedBackups = Number(container.querySelector('#backupMaxRetainedInput').value || 15);

      await apiFetch('/api/backups/config', {
        method: 'PUT',
        body: JSON.stringify({
          autoBackupMode,
          debounceSeconds,
          minIntervalMinutes,
          maxRetainedBackups
        })
      });
      showToast(`Saved backup policy (${autoBackupMode}, max ${maxRetainedBackups} snapshots)`);
      renderTopologyView(container);
    };
  }

  // Flush Pending Coalesced Edits Now
  const flushPendingBtn = container.querySelector('#flushPendingBackupBtn');
  if (flushPendingBtn) {
    flushPendingBtn.onclick = async () => {
      const res = await apiFetch('/api/backups/trigger?reason=coalesced_flush', { method: 'POST' });
      showToast(`Flushed pending edits to ${res.fileName}`);
      renderTopologyView(container);
    };
  }

  // Trigger Manual Snapshot
  container.querySelector('#manualBackupBtn').onclick = async () => {
    try {
      const res = await apiFetch('/api/backups/trigger?reason=manual', { method: 'POST' });
      showToast(`Created snapshot ${res.fileName}`);
      renderTopologyView(container);
    } catch (err) {
      showToast(`Backup failed: ${err.message}`);
    }
  };

  // Multi-select checkboxes for bulk backup deletion
  const selectAllBackupsCheck = container.querySelector('#selectAllBackupsCheck');
  const rowChecks = Array.from(container.querySelectorAll('.backup-row-check'));
  const bulkDeleteBackupsBtn = container.querySelector('#bulkDeleteBackupsBtn');

  function updateSelectedBackupsState() {
    const selectedFiles = rowChecks.filter(ch => ch.checked).map(ch => ch.value);
    if (bulkDeleteBackupsBtn) {
      bulkDeleteBackupsBtn.disabled = selectedFiles.length === 0;
      bulkDeleteBackupsBtn.textContent = `Delete Selected (${selectedFiles.length})`;
    }
    if (selectAllBackupsCheck && rowChecks.length > 0) {
      selectAllBackupsCheck.checked = selectedFiles.length === rowChecks.length;
    }
    return selectedFiles;
  }

  if (selectAllBackupsCheck) {
    selectAllBackupsCheck.onchange = () => {
      rowChecks.forEach(ch => { ch.checked = selectAllBackupsCheck.checked; });
      updateSelectedBackupsState();
    };
  }

  rowChecks.forEach(ch => {
    ch.onchange = updateSelectedBackupsState;
  });

  if (bulkDeleteBackupsBtn) {
    bulkDeleteBackupsBtn.onclick = async () => {
      const fileNames = updateSelectedBackupsState();
      if (fileNames.length === 0) return;
      if (!confirm(`Delete ${fileNames.length} selected backup snapshot(s)?`)) return;

      const res = await apiFetch('/api/backups/bulk-delete', {
        method: 'POST',
        body: JSON.stringify({ fileNames })
      });
      showToast(`Deleted ${res.deletedCount} snapshot(s)`);
      renderTopologyView(container);
    };
  }

  // Prune Old Snapshots Modal
  const pruneBackupsBtn = container.querySelector('#pruneBackupsBtn');
  if (pruneBackupsBtn) {
    pruneBackupsBtn.onclick = () => {
      openModal(`
        <h3 style="margin-top:0;">Prune Old Backup Snapshots</h3>
        <p style="font-size:13px;color:#64748B;">
          Keep only the newest <strong>N</strong> snapshots and permanently delete all older snapshot files from disk.
        </p>
        <div style="margin:14px 0;">
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Number of Newest Snapshots to Keep</label>
          <input type="number" min="0" max="200" id="pruneKeepCountInput" class="input-control" style="width:100%;" value="5" />
        </div>
        <div style="display:flex;justify-content:flex-end;gap:10px;">
          <button class="btn btn-outline" id="cancelPruneBtn">Cancel</button>
          <button class="btn btn-danger" id="confirmPruneBtn">Prune Snapshots</button>
        </div>
      `, (m) => {
        m.querySelector('#cancelPruneBtn').onclick = closeModal;
        m.querySelector('#confirmPruneBtn').onclick = async () => {
          const keepCount = Math.max(0, Number(m.querySelector('#pruneKeepCountInput').value || 0));
          const res = await apiFetch('/api/backups/prune', {
            method: 'POST',
            body: JSON.stringify({ keepCount })
          });
          closeModal();
          showToast(`Pruned ${res.deletedCount} old snapshot(s) (kept ${keepCount} newest)`);
          renderTopologyView(container);
        };
      });
    };
  }

  // Single Delete Snapshot
  container.querySelectorAll('.del-backup-btn').forEach(btn => {
    btn.onclick = async () => {
      const fileName = btn.dataset.file;
      if (!confirm(`Delete backup snapshot "${fileName}"?`)) return;
      await apiFetch(`/api/backups/${encodeURIComponent(fileName)}`, { method: 'DELETE' });
      showToast(`Deleted ${fileName}`);
      renderTopologyView(container);
    };
  });

  // Clear Transaction History (with Pre-Clear Backup option)
  const clearHistoryAdminBtn = container.querySelector('#clearHistoryAdminBtn');
  if (clearHistoryAdminBtn) {
    clearHistoryAdminBtn.onclick = () => {
      openClearTransactionHistoryModal(() => renderTopologyView(container));
    };
  }

  // Reset All Information — Start from Clean History (preserves backup databases)
  const resetAllDatabaseAdminBtn = container.querySelector('#resetAllDatabaseAdminBtn');
  if (resetAllDatabaseAdminBtn) {
    resetAllDatabaseAdminBtn.onclick = () => {
      openResetAllDatabaseModal(() => renderTopologyView(container));
    };
  }

  // Top-level Restore Database Modal (Local Snapshot or Upload External .db)
  const restoreDatabaseModalBtn = container.querySelector('#restoreDatabaseModalBtn');
  if (restoreDatabaseModalBtn) {
    restoreDatabaseModalBtn.onclick = () => {
      openRestoreDatabaseModal(sortedBackups, '', () => renderTopologyView(container));
    };
  }

  // Per-row Restore Snapshot Modal
  container.querySelectorAll('.restore-backup-btn').forEach(btn => {
    btn.onclick = () => {
      const fileName = btn.dataset.file;
      openRestoreDatabaseModal(sortedBackups, fileName, () => renderTopologyView(container));
    };
  });
}


