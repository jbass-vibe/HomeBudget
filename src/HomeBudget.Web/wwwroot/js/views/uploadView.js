import {
  state,
  apiFetch,
  formatCurrency,
  escapeHtml,
  formatBackupStatus,
  sortCategories,
  sortTransactions,
  renderCategoryDropdownSortOptions,
  setSortPreference,
  showToast,
  openModal,
  closeModal,
  refreshReferenceData
} from '../app.js';

let currentStagingSession = null;

export function sortStagingCandidates(candidates, sortMode = 'status_priority') {
  const list = Array.isArray(candidates) ? [...candidates] : [];
  if (sortMode === 'status_priority') {
    const rank = (row) => {
      if (row.dedupStatus === 'PotentialCollision') return 0;
      if (row.requiresManualReview && row.dedupStatus === 'AutoApprove') return 1;
      if (row.dedupStatus === 'AutoApprove') return 2;
      return 3;
    };
    return list.sort((a, b) => {
      const rDiff = rank(a) - rank(b);
      if (rDiff !== 0) return rDiff;
      return String(b.transactionDate || '').localeCompare(String(a.transactionDate || '')) ||
        (Math.abs(Number(b.amount || 0)) - Math.abs(Number(a.amount || 0)));
    });
  }
  return sortTransactions(list, sortMode);
}

export async function renderUploadView(container) {
  container.innerHTML = `
    <div class="page-header">
      <div>
        <h1 class="page-title">Upload Bank &amp; Credit Card CSVs</h1>
        <p class="page-subtitle">Chase, American Express, Citibank &amp; Wells Fargo CSV ingestion with two-stage de-duplication and auto-categorization</p>
      </div>
      <button class="btn btn-outline btn-sm" id="addAccountBtn">+ Add Account / Card</button>
    </div>

    <div class="card-panel">
      <div style="display:flex;flex-wrap:wrap;gap:16px;align-items:flex-end;margin-bottom:18px;">
        <div style="flex:1;min-width:240px;">
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:6px;">1. Select Source Account / Credit Card</label>
          <select id="uploadAccountSelect" class="select-control" style="width:100%;">
            ${state.accounts.map(a => `
              <option value="${a.accountId}">${escapeHtml(a.accountName)} (${escapeHtml(a.institution)} - ${escapeHtml(a.accountType)})</option>
            `).join('')}
          </select>
        </div>
      </div>

      <div class="dropzone" id="csvDropzone">
        <div style="font-size:28px;margin-bottom:8px;">&#128196;</div>
        <div style="font-weight:700;font-size:15px;">Drag &amp; drop Chase, American Express, Citibank, or Wells Fargo CSV files here</div>
        <div style="font-size:12px;color:#64748B;margin-top:4px;">Supports mid-month activity exports and full monthly statements</div>
        <input type="file" id="csvFileInput" accept=".csv" multiple style="display:none;" />
        <button class="btn btn-primary btn-sm" style="margin-top:14px;" id="browseFilesBtn">Select CSV Files</button>
      </div>
    </div>

    <div id="stagingReviewContainer"></div>
  `;

  const fileInput = container.querySelector('#csvFileInput');
  const dropzone = container.querySelector('#csvDropzone');

  container.querySelector('#browseFilesBtn').onclick = (e) => {
    e.stopPropagation();
    fileInput.click();
  };
  dropzone.onclick = () => fileInput.click();

  dropzone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropzone.classList.add('dragover');
  });
  dropzone.addEventListener('dragleave', () => dropzone.classList.remove('dragover'));
  dropzone.addEventListener('drop', async (e) => {
    e.preventDefault();
    dropzone.classList.remove('dragover');
    if (e.dataTransfer.files.length > 0) {
      await handleStageFiles(container, e.dataTransfer.files);
    }
  });

  fileInput.onchange = async () => {
    if (fileInput.files.length > 0) {
      await handleStageFiles(container, fileInput.files);
    }
  };

  container.querySelector('#addAccountBtn').onclick = () => {
    openModal(`
      <h3 style="margin-top:0;">Add Credit Card or Bank Account</h3>
      <div style="display:flex;flex-direction:column;gap:12px;margin:16px 0;">
        <div>
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Account Display Name</label>
          <input type="text" id="newAcctName" class="input-control" style="width:100%;" placeholder="e.g., Amex Gold Card, Citi Double Cash, Chase Sapphire" />
        </div>
        <div>
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Institution CSV Format</label>
          <select id="newAcctInst" class="select-control" style="width:100%;">
            <option value="Chase">Chase (7-Column Credit Card CSV)</option>
            <option value="Amex">American Express (Date, Description, Amount Credit Card CSV)</option>
            <option value="Citibank">Citibank (Status, Date, Description, Debit, Credit CSV)</option>
            <option value="WellsFargo">Wells Fargo (5-Column Headerless Checking CSV)</option>
          </select>
        </div>
        <div>
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Account Type</label>
          <select id="newAcctType" class="select-control" style="width:100%;">
            <option value="CreditCard">Credit Card</option>
            <option value="Checking">Checking Account</option>
          </select>
        </div>
      </div>
      <div style="display:flex;justify-content:flex-end;gap:10px;">
        <button class="btn btn-outline" id="cancelAcctBtn">Cancel</button>
        <button class="btn btn-primary" id="saveAcctBtn">Create Account</button>
      </div>
    `, (modalEl) => {
      const instSel = modalEl.querySelector('#newAcctInst');
      const typeSel = modalEl.querySelector('#newAcctType');
      if (instSel && typeSel) {
        instSel.onchange = () => {
          typeSel.value = instSel.value === 'WellsFargo' ? 'Checking' : 'CreditCard';
        };
      }
      modalEl.querySelector('#cancelAcctBtn').onclick = closeModal;
      modalEl.querySelector('#saveAcctBtn').onclick = async () => {
        const accountName = modalEl.querySelector('#newAcctName').value.trim();
        const institution = modalEl.querySelector('#newAcctInst').value;
        const accountType = modalEl.querySelector('#newAcctType').value;
        if (!accountName) return;

        await apiFetch('/api/accounts', {
          method: 'POST',
          body: JSON.stringify({ accountName, institution, accountType })
        });
        await refreshReferenceData();
        closeModal();
        showToast(`Created account ${accountName}`);
        renderUploadView(container);
      };
    });
  };
}

async function handleStageFiles(container, fileList) {
  const accountId = container.querySelector('#uploadAccountSelect').value;
  const formData = new FormData();
  formData.append('accountId', accountId);
  for (const f of fileList) {
    formData.append('files', f);
  }

  const stagingMount = container.querySelector('#stagingReviewContainer');
  stagingMount.innerHTML = `<div class="card-panel">Parsing CSVs &amp; checking de-duplication hashes...</div>`;

  try {
    currentStagingSession = await apiFetch('/api/uploads/stage', {
      method: 'POST',
      body: formData
    });
    renderStagingTable(stagingMount, currentStagingSession);
  } catch (err) {
    stagingMount.innerHTML = `<div class="card-panel" style="color:#DC2626;">Upload staging failed: ${escapeHtml(err.message)}</div>`;
  }
}

function captureCurrentEditsToSession(stagingMount, session) {
  if (!stagingMount || !session || !Array.isArray(session.candidates)) return;
  stagingMount.querySelectorAll('tr[data-row-id]').forEach(tr => {
    const rowId = tr.dataset.rowId;
    const candidate = session.candidates.find(c => String(c.rowId) === String(rowId));
    if (!candidate) return;
    const actionSel = tr.querySelector('.row-action-select');
    const merchantInput = tr.querySelector('.row-merchant-input');
    const typeSel = tr.querySelector('.row-type-select');
    const catSel = tr.querySelector('.row-cat-select');
    const subcatInput = tr.querySelector('.row-subcat-input');

    if (actionSel) candidate._selectedAction = actionSel.value;
    if (merchantInput) candidate.cleanMerchantName = merchantInput.value.trim();
    if (typeSel) candidate.transactionType = typeSel.value;
    if (catSel) candidate.categoryId = catSel.value ? Number(catSel.value) : null;
    if (subcatInput) candidate.subcategory = subcatInput.value.trim() || null;
  });
}

function renderStagingTable(stagingMount, session) {
  const s = session.summary;
  const sortedCats = sortCategories(state.categories, state.categoryDropdownSort);
  const sortedCandidates = sortStagingCandidates(session.candidates, state.uploadTransactionSort);

  const categoryOptionsHtml = (selectedId) => `
    <option value="">-- Uncategorized --</option>
    ${sortedCats.map(c => `
      <option value="${c.categoryId}" ${c.categoryId === selectedId ? 'selected' : ''}>${escapeHtml(c.name)}</option>
    `).join('')}
  `;

  stagingMount.innerHTML = `
    <div class="card-panel">
      <div style="display:flex;flex-wrap:wrap;justify-content:space-between;align-items:center;gap:12px;margin-bottom:16px;">
        <div>
          <h2 style="margin:0;font-size:17px;font-weight:800;">Staging Review: ${escapeHtml(session.fileNames.join(', '))}</h2>
          <p style="margin:4px 0 0 0;font-size:13px;color:#64748B;">
            Parsed <strong>${s.totalParsed}</strong> rows &bull;
            <span style="color:#059669;font-weight:700;">${s.autoApproveCount} Ready to Import</span> &bull;
            <span style="color:#D97706;font-weight:700;">${s.potentialCollisionCount} Same-Day Collisions</span> &bull;
            <span style="color:#2563EB;font-weight:700;">${s.manualReviewCount} Manual Review</span> &bull;
            <span>${s.skippedDuplicateCount + s.skippedDeletedCount} Duplicates/Tombstoned Skipped</span>
          </p>
        </div>
        <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">
          <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
            Sort Staged Rows:
            <select id="uploadTxSortSelect" class="select-control" style="font-size:12px;padding:4px 8px;">
              <option value="status_priority" ${state.uploadTransactionSort === 'status_priority' ? 'selected' : ''}>Review Priority (Collisions First)</option>
              <option value="date_desc" ${state.uploadTransactionSort === 'date_desc' ? 'selected' : ''}>Newest Date</option>
              <option value="date_asc" ${state.uploadTransactionSort === 'date_asc' ? 'selected' : ''}>Oldest Date</option>
              <option value="amount_desc" ${state.uploadTransactionSort === 'amount_desc' ? 'selected' : ''}>Highest Amount ($)</option>
              <option value="amount_asc" ${state.uploadTransactionSort === 'amount_asc' ? 'selected' : ''}>Lowest Amount ($)</option>
              <option value="alpha_asc" ${state.uploadTransactionSort === 'alpha_asc' ? 'selected' : ''}>Merchant (A-Z)</option>
              <option value="alpha_desc" ${state.uploadTransactionSort === 'alpha_desc' ? 'selected' : ''}>Merchant (Z-A)</option>
            </select>
          </label>
          <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
            Category List Order:
            <select id="uploadCatDropdownSort" class="select-control" style="font-size:12px;padding:4px 8px;">
              ${renderCategoryDropdownSortOptions(state.categoryDropdownSort)}
            </select>
          </label>
          <button class="btn btn-primary" id="commitUploadBtn">&#10003; Commit Approved Rows to SQLite</button>
        </div>
      </div>

      <div class="table-responsive">
        <table class="data-table">
          <thead>
            <tr>
              <th>Action</th>
              <th>Status</th>
              <th>Date</th>
              <th>Description</th>
              <th>Type</th>
              <th>Amount</th>
              <th>Category</th>
              <th>Subcategory (Opt)</th>
            </tr>
          </thead>
          <tbody>
            ${sortedCandidates.map(row => {
              const defaultAction = row._selectedAction || (row.dedupStatus === 'AutoApprove' ? 'Import' : 'Skip');
              const rowClass = row.dedupStatus === 'PotentialCollision'
                ? 'row-collision'
                : row.requiresManualReview && row.dedupStatus === 'AutoApprove'
                  ? 'row-manual'
                  : '';

              return `
                <tr class="${rowClass}" data-row-id="${escapeHtml(row.rowId)}">
                  <td>
                    <select class="select-control row-action-select" style="font-weight:700;">
                      <option value="Import" ${defaultAction === 'Import' ? 'selected' : ''}>Import</option>
                      <option value="Skip" ${defaultAction === 'Skip' ? 'selected' : ''}>Skip</option>
                      <option value="TombstoneDelete" ${defaultAction === 'TombstoneDelete' ? 'selected' : ''}>Delete &amp; Ignore Forever</option>
                    </select>
                  </td>
                  <td>
                    <span class="bigbox-pill">${escapeHtml(row.dedupStatus)}${row.requiresManualReview ? ' (Review)' : ''}</span>
                  </td>
                  <td style="white-space:nowrap;">${escapeHtml(row.transactionDate)}</td>
                  <td>
                    <input type="text" class="input-control row-merchant-input" value="${escapeHtml(row.cleanMerchantName)}" style="width:190px;" />
                    <div style="font-size:11px;color:#64748B;">${escapeHtml(row.rawDescription)}</div>
                  </td>
                  <td>
                    <select class="select-control row-type-select">
                      <option value="Expense" ${row.transactionType === 'Expense' ? 'selected' : ''}>Expense</option>
                      <option value="Refund" ${row.transactionType === 'Refund' ? 'selected' : ''}>Refund / Credit</option>
                      <option value="IncomeDeposit" ${row.transactionType === 'IncomeDeposit' ? 'selected' : ''}>Income Deposit</option>
                      <option value="CreditCardPayoff" ${row.transactionType === 'CreditCardPayoff' ? 'selected' : ''}>Credit Card Payoff</option>
                    </select>
                  </td>
                  <td class="${row.amount < 0 ? 'amount-refund' : 'amount-expense'}">${formatCurrency(row.amount)}</td>
                  <td>
                    <select class="select-control row-cat-select">
                      ${categoryOptionsHtml(row.categoryId)}
                    </select>
                  </td>
                  <td>
                    <input type="text" class="input-control row-subcat-input" placeholder="Optional" value="${escapeHtml(row.subcategory || '')}" style="width:120px;" />
                  </td>
                </tr>
              `;
            }).join('')}
          </tbody>
        </table>
      </div>
    </div>
  `;

  const uploadTxSortSelect = stagingMount.querySelector('#uploadTxSortSelect');
  if (uploadTxSortSelect) {
    uploadTxSortSelect.onchange = (e) => {
      captureCurrentEditsToSession(stagingMount, session);
      setSortPreference('uploadTransactionSort', e.target.value);
      renderStagingTable(stagingMount, session);
    };
  }

  const uploadSortSel = stagingMount.querySelector('#uploadCatDropdownSort');
  if (uploadSortSel) {
    uploadSortSel.onchange = (e) => {
      setSortPreference('categoryDropdownSort', e.target.value);
      const reordered = sortCategories(state.categories, state.categoryDropdownSort);
      stagingMount.querySelectorAll('.row-cat-select').forEach(sel => {
        const curVal = sel.value;
        sel.innerHTML = `
          <option value="">-- Uncategorized --</option>
          ${reordered.map(c => `
            <option value="${c.categoryId}" ${String(c.categoryId) === String(curVal) ? 'selected' : ''}>${escapeHtml(c.name)}</option>
          `).join('')}
        `;
      });
    };
  }

  stagingMount.querySelector('#commitUploadBtn').onclick = async () => {
    const decisions = [];
    stagingMount.querySelectorAll('tr[data-row-id]').forEach(tr => {
      const rowId = tr.dataset.rowId;
      const action = tr.querySelector('.row-action-select').value;
      const cleanMerchantName = tr.querySelector('.row-merchant-input').value.trim();
      const transactionType = tr.querySelector('.row-type-select').value;
      const catVal = tr.querySelector('.row-cat-select').value;
      const subcategory = tr.querySelector('.row-subcat-input').value.trim();

      decisions.push({
        rowId,
        action,
        categoryId: catVal ? Number(catVal) : null,
        subcategory: subcategory || null,
        cleanMerchantName,
        transactionType
      });
    });

    const result = await apiFetch('/api/uploads/commit', {
      method: 'POST',
      body: JSON.stringify({
        stagingSessionId: session.stagingSessionId,
        decisions
      })
    });

    showToast(`Imported ${result.importedCount} transactions (${result.skippedCount} skipped, ${result.tombstonedCount} tombstoned) • ${formatBackupStatus(result.backupFileCreated)}`);
    stagingMount.innerHTML = `
      <div class="card-panel" style="border-color:#10B981;background:#ECFDF5;">
        <strong>&#10003; Upload Committed Successfully!</strong><br/>
        Imported: <strong>${result.importedCount}</strong> &bull;
        Skipped Duplicates: <strong>${result.skippedCount}</strong> &bull;
        Tombstoned: <strong>${result.tombstonedCount}</strong><br/>
        Snapshot created: <code>${escapeHtml(result.backupFileCreated || 'None (no changes)')}</code>
      </div>
    `;
  };
}
