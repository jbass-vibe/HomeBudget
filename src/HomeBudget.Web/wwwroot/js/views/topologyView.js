import {
  apiFetch,
  formatCurrency,
  escapeHtml,
  formatBackupStatus,
  showToast,
  openModal,
  closeModal,
  refreshReferenceData,
  openCategoryManagerModal
} from '../app.js';

export async function renderTopologyView(container) {
  const [topology, settings, backupInfo] = await Promise.all([
    apiFetch('/api/topology'),
    apiFetch('/api/settings'),
    apiFetch('/api/backups')
  ]);

  const categories = topology.categories;
  const rules = topology.rules;
  const catNameById = Object.fromEntries(categories.map(c => [c.categoryId, c.name]));

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
        <div style="display:flex;gap:8px;">
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
        <div style="display:flex;gap:8px;">
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

    <!-- 4. Event-Driven Local SQLite Backups -->
    <div class="card-panel">
      <div class="toolbar-row">
        <div>
          <h2 style="margin:0;font-size:17px;font-weight:800;">4. Event-Driven Local SQLite Backups</h2>
          <p style="margin:2px 0 0 0;font-size:12px;color:#64748B;">
            Path: <code>${escapeHtml(backupInfo.backupDirectoryPath)}</code> &bull; Automatically triggered on CSV upload or recategorization (retains 30 newest)
          </p>
        </div>
        <button class="btn btn-outline btn-sm" id="manualBackupBtn">Create Manual Snapshot Now</button>
      </div>

      <div class="table-responsive">
        <table class="data-table">
          <thead>
            <tr>
              <th>Snapshot Filename</th>
              <th>Created (UTC)</th>
              <th>Size</th>
              <th>Download (For Manual Google Drive Archive)</th>
            </tr>
          </thead>
          <tbody>
            ${backupInfo.backups.length === 0 ? `
              <tr><td colspan="4" style="text-align:center;color:#64748B;">No snapshots created yet.</td></tr>
            ` : backupInfo.backups.map(b => `
              <tr>
                <td><code>${escapeHtml(b.fileName)}</code></td>
                <td>${escapeHtml(new Date(b.createdAtUtc).toLocaleString())}</td>
                <td>${(b.sizeBytes / 1024).toFixed(1)} KB</td>
                <td>
                  <a class="btn btn-outline btn-sm" href="/api/backups/download/${encodeURIComponent(b.fileName)}" download>
                    &#11015; Download .db
                  </a>
                </td>
              </tr>
            `).join('')}
          </tbody>
        </table>
      </div>
    </div>
  `;

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
    };
  });

  // Delete Category Handler
  container.querySelectorAll('.del-cat-btn').forEach(btn => {
    btn.onclick = () => {
      const catId = Number(btn.dataset.catId);
      const catObj = categories.find(c => c.categoryId === catId);
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
            ${categories.map(c => `<option value="${c.categoryId}">${escapeHtml(c.name)}</option>`).join('')}
          </select>
        </div>
        <div>
          <label style="font-size:12px;font-weight:700;display:block;">Into Target Category</label>
          <select id="mergeTargetSel" class="select-control" style="width:100%;">
            ${categories.map((c, i) => `<option value="${c.categoryId}" ${i === 1 ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}
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
          ${categories.map(c => `<option value="${c.categoryId}">${escapeHtml(c.name)}</option>`).join('')}
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

  // Trigger Manual Snapshot
  container.querySelector('#manualBackupBtn').onclick = async () => {
    try {
      const res = await apiFetch('/api/backups/trigger', { method: 'POST' });
      showToast(`Created snapshot ${res.fileName}`);
      renderTopologyView(container);
    } catch (err) {
      showToast(`Backup failed: ${err.message}`);
    }
  };
}
