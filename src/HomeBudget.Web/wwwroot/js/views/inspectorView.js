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
  openCategorizeExpenseModal
} from '../app.js';

const filterState = {
  startDate: '',
  endDate: '',
  accountId: '',
  categoryId: '',
  uncategorizedOnly: false,
  transactionType: '',
  minAmount: '',
  maxAmount: '',
  sortBy: 'date_desc',
  search: ''
};

export async function renderInspectorView(container) {
  const sortedFilterCategories = sortCategories(state.categories, state.categoryDropdownSort);

  container.innerHTML = `
    <div class="page-header">
      <div>
        <h1 class="page-title">Transaction Inspector (Income &amp; Expenses)</h1>
        <p class="page-subtitle">Filter, recategorize with forward/past automation, edit fields &amp; notes, split, or delete income and expense entries</p>
      </div>
    </div>

    <div class="card-panel">
      <div style="display:flex;flex-wrap:wrap;justify-content:space-between;gap:10px;margin-bottom:14px;">
        <div class="pill-group">
          <button class="pill-btn ${!filterState.transactionType && !filterState.uncategorizedOnly ? 'active' : ''}" data-qtype="all">All Records</button>
          <button class="pill-btn ${filterState.transactionType === 'Expense' && !filterState.uncategorizedOnly ? 'active' : ''}" data-qtype="Expense">Expenses Only</button>
          <button class="pill-btn ${filterState.transactionType === 'IncomeDeposit' ? 'active' : ''}" data-qtype="IncomeDeposit">Income (Deposits)</button>
          <button class="pill-btn ${filterState.uncategorizedOnly ? 'active' : ''}" data-qtype="uncat">&#9888;&#65039; Uncategorized</button>
          <button class="pill-btn ${filterState.transactionType === 'Refund' ? 'active' : ''}" data-qtype="Refund">Refunds</button>
          <button class="pill-btn ${filterState.transactionType === 'CreditCardPayoff' ? 'active' : ''}" data-qtype="CreditCardPayoff">CC Payoffs</button>
        </div>

        <div class="pill-group">
          <button class="pill-btn" data-qdate="all">All Dates</button>
          <button class="pill-btn" data-qdate="mtd">This Month</button>
          <button class="pill-btn" data-qdate="prev">Last Month</button>
          <button class="pill-btn" data-qdate="3m">Last 3M</button>
          <button class="pill-btn" data-qdate="ytd">YTD</button>
        </div>
      </div>

      <div style="display:flex;flex-wrap:wrap;gap:10px;align-items:flex-end;">
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Start Date</label>
          <input type="date" id="fltStart" class="input-control" value="${escapeHtml(filterState.startDate)}" />
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">End Date</label>
          <input type="date" id="fltEnd" class="input-control" value="${escapeHtml(filterState.endDate)}" />
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Account</label>
          <select id="fltAccount" class="select-control">
            <option value="">All Accounts</option>
            ${state.accounts.map(a => `<option value="${a.accountId}" ${String(a.accountId) === filterState.accountId ? 'selected' : ''}>${escapeHtml(a.accountName)}</option>`).join('')}
          </select>
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Category</label>
          <select id="fltCategory" class="select-control">
            <option value="">All Categories</option>
            <option value="uncat" ${filterState.uncategorizedOnly ? 'selected' : ''}>-- Uncategorized Only --</option>
            ${sortedFilterCategories.map(c => `<option value="${c.categoryId}" ${!filterState.uncategorizedOnly && String(c.categoryId) === filterState.categoryId ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}
          </select>
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Category List Order</label>
          <select id="inspectorCatDropdownSort" class="select-control">
            <option value="alpha_asc" ${state.categoryDropdownSort === 'alpha_asc' ? 'selected' : ''}>Alphabetical (A-Z)</option>
            <option value="recent" ${state.categoryDropdownSort === 'recent' ? 'selected' : ''}>Recent Changes</option>
            <option value="custom_order" ${state.categoryDropdownSort === 'custom_order' ? 'selected' : ''}>Custom Order</option>
            <option value="budget_desc" ${state.categoryDropdownSort === 'budget_desc' ? 'selected' : ''}>Highest Budget ($)</option>
          </select>
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Type</label>
          <select id="fltType" class="select-control">
            <option value="">All Types</option>
            <option value="Expense" ${filterState.transactionType === 'Expense' ? 'selected' : ''}>Expense</option>
            <option value="IncomeDeposit" ${filterState.transactionType === 'IncomeDeposit' ? 'selected' : ''}>IncomeDeposit (Income)</option>
            <option value="Refund" ${filterState.transactionType === 'Refund' ? 'selected' : ''}>Refund</option>
            <option value="CreditCardPayoff" ${filterState.transactionType === 'CreditCardPayoff' ? 'selected' : ''}>Credit Card Payoff</option>
          </select>
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Min $</label>
          <input type="number" step="1" id="fltMinAmt" class="input-control" style="width:85px;" placeholder="0" value="${escapeHtml(filterState.minAmount)}" />
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Max $</label>
          <input type="number" step="1" id="fltMaxAmt" class="input-control" style="width:85px;" placeholder="Any" value="${escapeHtml(filterState.maxAmount)}" />
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Sort Transactions</label>
          <select id="fltSort" class="select-control">
            <option value="date_desc" ${filterState.sortBy === 'date_desc' ? 'selected' : ''}>Newest First</option>
            <option value="date_asc" ${filterState.sortBy === 'date_asc' ? 'selected' : ''}>Oldest First</option>
            <option value="recent_changes" ${filterState.sortBy === 'recent_changes' ? 'selected' : ''}>Recent Changes</option>
            <option value="amount_desc" ${filterState.sortBy === 'amount_desc' ? 'selected' : ''}>Highest Amount</option>
            <option value="amount_asc" ${filterState.sortBy === 'amount_asc' ? 'selected' : ''}>Lowest Amount</option>
            <option value="merchant_asc" ${filterState.sortBy === 'merchant_asc' ? 'selected' : ''}>Merchant (A-Z)</option>
            <option value="merchant_desc" ${filterState.sortBy === 'merchant_desc' ? 'selected' : ''}>Merchant (Z-A)</option>
          </select>
        </div>
        <div style="flex:1;min-width:180px;">
          <label style="font-size:11px;font-weight:700;display:block;">Search Merchant / Raw Description / Notes</label>
          <input type="text" id="fltSearch" class="input-control" style="width:100%;" placeholder="Search merchant, description, notes..." value="${escapeHtml(filterState.search)}" />
        </div>
        <button class="btn btn-primary" id="applyFiltersBtn">Apply Filters</button>
        <button class="btn btn-outline" id="resetFiltersBtn">Reset</button>
      </div>
    </div>

    <div id="inspectorTableMount" class="card-panel">Loading transactions...</div>
  `;

  const syncInputsAndReload = () => {
    filterState.startDate = container.querySelector('#fltStart').value;
    filterState.endDate = container.querySelector('#fltEnd').value;
    filterState.accountId = container.querySelector('#fltAccount').value;
    const catVal = container.querySelector('#fltCategory').value;
    if (catVal === 'uncat') {
      filterState.uncategorizedOnly = true;
      filterState.categoryId = '';
    } else {
      filterState.uncategorizedOnly = false;
      filterState.categoryId = catVal;
    }
    filterState.transactionType = container.querySelector('#fltType').value;
    filterState.minAmount = container.querySelector('#fltMinAmt').value;
    filterState.maxAmount = container.querySelector('#fltMaxAmt').value;
    filterState.sortBy = container.querySelector('#fltSort').value;
    filterState.search = container.querySelector('#fltSearch').value.trim();
    loadTransactionsTable(container.querySelector('#inspectorTableMount'));
  };

  container.querySelector('#applyFiltersBtn').onclick = syncInputsAndReload;
  container.querySelector('#fltSort').onchange = syncInputsAndReload;
  container.querySelector('#inspectorCatDropdownSort').onchange = (e) => {
    setSortPreference('categoryDropdownSort', e.target.value);
    renderInspectorView(container);
  };
  container.querySelector('#fltSearch').onkeydown = (e) => {
    if (e.key === 'Enter') syncInputsAndReload();
  };

  const clearHistoryBtn = container.querySelector('#clearHistoryInspectorBtn');
  if (clearHistoryBtn) {
    clearHistoryBtn.onclick = () => {
      openClearTransactionHistoryModal(() => renderInspectorView(container));
    };
  }

  container.querySelector('#resetFiltersBtn').onclick = () => {
    Object.assign(filterState, {
      startDate: '',
      endDate: '',
      accountId: '',
      categoryId: '',
      uncategorizedOnly: false,
      transactionType: '',
      minAmount: '',
      maxAmount: '',
      sortBy: 'date_desc',
      search: ''
    });
    renderInspectorView(container);
  };

  container.querySelectorAll('[data-qtype]').forEach(btn => {
    btn.onclick = () => {
      const qt = btn.dataset.qtype;
      if (qt === 'all') {
        filterState.transactionType = '';
        filterState.uncategorizedOnly = false;
      } else if (qt === 'uncat') {
        filterState.uncategorizedOnly = true;
        filterState.transactionType = '';
        filterState.categoryId = '';
      } else {
        filterState.uncategorizedOnly = false;
        filterState.transactionType = qt;
      }
      renderInspectorView(container);
    };
  });

  container.querySelectorAll('[data-qdate]').forEach(btn => {
    btn.onclick = () => {
      const qd = btn.dataset.qdate;
      const now = new Date();
      const iso = (d) => d.toISOString().slice(0, 10);
      if (qd === 'all') {
        filterState.startDate = '';
        filterState.endDate = '';
      } else if (qd === 'mtd') {
        filterState.startDate = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-01`;
        filterState.endDate = iso(now);
      } else if (qd === 'prev') {
        const firstPrev = new Date(now.getFullYear(), now.getMonth() - 1, 1);
        const lastPrev = new Date(now.getFullYear(), now.getMonth(), 0);
        filterState.startDate = iso(firstPrev);
        filterState.endDate = iso(lastPrev);
      } else if (qd === '3m') {
        const first3m = new Date(now.getFullYear(), now.getMonth() - 2, 1);
        filterState.startDate = iso(first3m);
        filterState.endDate = iso(now);
      } else if (qd === 'ytd') {
        filterState.startDate = `${now.getFullYear()}-01-01`;
        filterState.endDate = iso(now);
      }
      renderInspectorView(container);
    };
  });

  await loadTransactionsTable(container.querySelector('#inspectorTableMount'));
}

async function loadTransactionsTable(mountEl) {
  const params = new URLSearchParams({ pageSize: '300', sortBy: filterState.sortBy });
  if (filterState.startDate) params.set('startDate', filterState.startDate);
  if (filterState.endDate) params.set('endDate', filterState.endDate);
  if (filterState.accountId) params.set('accountId', filterState.accountId);
  if (filterState.uncategorizedOnly) params.set('uncategorizedOnly', 'true');
  else if (filterState.categoryId) params.set('categoryId', filterState.categoryId);
  if (filterState.transactionType) params.set('transactionType', filterState.transactionType);
  if (filterState.minAmount) params.set('minAmount', filterState.minAmount);
  if (filterState.maxAmount) params.set('maxAmount', filterState.maxAmount);
  if (filterState.search) params.set('search', filterState.search);

  const data = await apiFetch(`/api/transactions?${params.toString()}`);
  const sortedDropdownCats = sortCategories(state.categories, state.categoryDropdownSort);

  const catOptions = (selectedId) => `
    <option value="">-- Uncategorized --</option>
    ${sortedDropdownCats.map(c => `<option value="${c.categoryId}" ${c.categoryId === selectedId ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}
    <option value="__NEW__">+ Add New Category...</option>
  `;

  mountEl.innerHTML = `
    <div style="display:flex;flex-wrap:wrap;justify-content:space-between;align-items:center;gap:10px;margin-bottom:12px;">
      <div>
        <strong>Showing ${data.items.length} of ${data.totalCount} matching income &amp; expense records</strong>
      </div>
      <div style="display:flex;gap:8px;align-items:center;">
        <button class="btn btn-danger btn-sm" id="bulkDeleteSelectedBtn" style="display:none;">
          &#128465; Delete Selected (<span id="selectedTxCount">0</span>)
        </button>
      </div>
    </div>

    <div class="table-responsive">
      <table class="data-table">
        <thead>
          <tr>
            <th><input type="checkbox" id="selectAllTxCheck" title="Select all visible rows" /></th>
            <th>Date</th>
            <th>Account</th>
            <th>Merchant / Description</th>
            <th>Type</th>
            <th>Amount ($)</th>
            <th>Category &amp; Source</th>
            <th>Subcategory</th>
            <th>Notes</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          ${data.items.length === 0 ? `
            <tr><td colspan="10" style="text-align:center;color:#64748B;">No matching income or expense transactions found.</td></tr>
          ` : data.items.map(tx => `
            <tr data-tx-id="${tx.transactionId}">
              <td><input type="checkbox" class="tx-row-check" value="${tx.transactionId}" /></td>
              <td>
                <input type="date" class="input-control tx-date" value="${escapeHtml(tx.transactionDate)}" style="width:132px;" />
              </td>
              <td>${escapeHtml(tx.accountName)}</td>
              <td>
                <input type="text" class="input-control tx-merchant" value="${escapeHtml(tx.cleanMerchantName)}" style="width:165px;" />
                <div style="font-size:11px;color:#64748B;">${escapeHtml(tx.rawDescription)}</div>
              </td>
              <td>
                <select class="select-control tx-type">
                  <option value="Expense" ${tx.transactionType === 'Expense' ? 'selected' : ''}>Expense</option>
                  <option value="IncomeDeposit" ${tx.transactionType === 'IncomeDeposit' ? 'selected' : ''}>IncomeDeposit (Income)</option>
                  <option value="Refund" ${tx.transactionType === 'Refund' ? 'selected' : ''}>Refund</option>
                  <option value="CreditCardPayoff" ${tx.transactionType === 'CreditCardPayoff' ? 'selected' : ''}>CreditCardPayoff</option>
                </select>
              </td>
              <td>
                <input type="number" step="0.01" class="input-control tx-amount" value="${tx.amount}" style="width:95px;font-weight:700;" />
              </td>
              <td>
                <select class="select-control tx-cat" data-orig-cat="${tx.categoryId || ''}">
                  ${catOptions(tx.categoryId)}
                </select>
                <div style="font-size:10px;color:#64748B;margin-top:2px;">
                  ${tx.isCategoryManuallyLocked ? '&#128274; Manual' : (tx.categorizationSource === 'Rule' ? '&#9889; Rule' : 'Uncategorized')}
                </div>
              </td>
              <td>
                <input type="text" class="input-control tx-subcat" value="${escapeHtml(tx.subcategory || '')}" placeholder="None" style="width:95px;" />
              </td>
              <td>
                <input type="text" class="input-control tx-notes" value="${escapeHtml(tx.notes || '')}" placeholder="Add note..." style="width:130px;" />
              </td>
              <td style="white-space:nowrap;">
                <button class="btn btn-primary btn-sm tx-save-btn" title="Save field edits">Save</button>
                <button class="btn btn-outline btn-sm tx-cat-modal-btn" title="Categorize with Forward/Past options">Categorize</button>
                <button class="btn btn-outline btn-sm tx-split-btn" title="Split Transaction">Split</button>
                <button class="btn btn-danger btn-sm tx-del-btn" title="Delete Income or Expense from DB">&#128465;</button>
              </td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    </div>
  `;

  const selectAllCheck = mountEl.querySelector('#selectAllTxCheck');
  const rowChecks = Array.from(mountEl.querySelectorAll('.tx-row-check'));
  const bulkDelBtn = mountEl.querySelector('#bulkDeleteSelectedBtn');
  const selCountSpan = mountEl.querySelector('#selectedTxCount');

  const updateBulkBar = () => {
    const selectedIds = rowChecks.filter(c => c.checked).map(c => Number(c.value));
    selCountSpan.textContent = String(selectedIds.length);
    bulkDelBtn.style.display = selectedIds.length > 0 ? 'inline-flex' : 'none';
  };

  if (selectAllCheck) {
    selectAllCheck.onchange = () => {
      rowChecks.forEach(c => { c.checked = selectAllCheck.checked; });
      updateBulkBar();
    };
  }
  rowChecks.forEach(c => { c.onchange = updateBulkBar; });

  bulkDelBtn.onclick = () => {
    const selectedIds = rowChecks.filter(c => c.checked).map(c => Number(c.value));
    if (selectedIds.length === 0) return;

    openModal(`
      <h3 style="margin-top:0;color:#DC2626;">Delete ${selectedIds.length} Selected Transaction(s)?</h3>
      <p style="font-size:13px;color:#475569;">
        This will permanently remove the selected income and/or expense records from your SQLite database and create a backup snapshot.
      </p>
      <label style="display:flex;align-items:center;gap:8px;font-size:13px;font-weight:600;margin:14px 0;">
        <input type="checkbox" id="bulkTombstoneCheck" checked />
        <span>Add to Tombstone list so future CSV uploads do not re-import these deleted records</span>
      </label>
      <div style="display:flex;justify-content:flex-end;gap:10px;">
        <button class="btn btn-outline" id="cancelBulkDelBtn">Cancel</button>
        <button class="btn btn-danger" id="confirmBulkDelBtn">Delete ${selectedIds.length} Record(s)</button>
      </div>
    `, (modalEl) => {
      modalEl.querySelector('#cancelBulkDelBtn').onclick = closeModal;
      modalEl.querySelector('#confirmBulkDelBtn').onclick = async () => {
        const addToTombstone = modalEl.querySelector('#bulkTombstoneCheck').checked;
        const res = await apiFetch('/api/transactions/bulk-delete', {
          method: 'POST',
          body: JSON.stringify({ transactionIds: selectedIds, addToTombstone })
        });
        closeModal();
        showToast(`Deleted ${res.deletedCount} transaction(s) • ${formatBackupStatus(res.backupFileCreated)}`);
        loadTransactionsTable(mountEl);
      };
    });
  };

  mountEl.querySelectorAll('.tx-cat').forEach(sel => {
    sel.onchange = () => {
      const tr = sel.closest('tr');
      const txId = Number(tr.dataset.txId);
      const tx = data.items.find(x => x.transactionId === txId);
      if (!tx) return;
      openCategorizeExpenseModal(tx, () => loadTransactionsTable(mountEl), sel.value);
    };
  });

  mountEl.querySelectorAll('.tx-cat-modal-btn').forEach(btn => {
    btn.onclick = () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      const tx = data.items.find(x => x.transactionId === txId);
      if (tx) {
        openCategorizeExpenseModal(tx, () => loadTransactionsTable(mountEl));
      }
    };
  });

  mountEl.querySelectorAll('.tx-save-btn').forEach(btn => {
    btn.onclick = async () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      const transactionDate = tr.querySelector('.tx-date').value;
      const cleanMerchantName = tr.querySelector('.tx-merchant').value.trim();
      const transactionType = tr.querySelector('.tx-type').value;
      const amount = Number(tr.querySelector('.tx-amount').value || 0);
      const catVal = tr.querySelector('.tx-cat').value;
      const subcategory = tr.querySelector('.tx-subcat').value.trim();
      const notes = tr.querySelector('.tx-notes').value.trim();

      const res = await apiFetch(`/api/transactions/${txId}`, {
        method: 'PUT',
        body: JSON.stringify({
          transactionDate,
          amount,
          categoryId: (catVal && catVal !== '__NEW__') ? Number(catVal) : null,
          clearCategory: !catVal,
          subcategory,
          cleanMerchantName,
          transactionType,
          notes
        })
      });
      showToast(`Saved transaction #${txId} (${formatBackupStatus(res?.backupFileCreated)})`);
      loadTransactionsTable(mountEl);
    };
  });

  mountEl.querySelectorAll('.tx-split-btn').forEach(btn => {
    btn.onclick = () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      const tx = data.items.find(x => x.transactionId === txId);
      const half1 = Math.round((tx.amount / 2) * 100) / 100;
      const half2 = Math.round((tx.amount - half1) * 100) / 100;

      openModal(`
        <h3 style="margin-top:0;">Split Transaction (${formatCurrency(tx.amount)})</h3>
        <p style="font-size:13px;color:#64748B;">${escapeHtml(tx.transactionDate)} &bull; ${escapeHtml(tx.rawDescription)}</p>
        <div id="splitRowsContainer" style="display:flex;flex-direction:column;gap:10px;margin:14px 0;">
          ${[half1, half2].map((amt, i) => `
            <div class="split-item-row" style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;">
              <input type="number" step="0.01" class="input-control split-amt" value="${amt}" style="width:95px;" />
              <select class="select-control split-cat" style="flex:1;min-width:140px;">
                ${sortedDropdownCats.map(c => `<option value="${c.categoryId}" ${c.categoryId === tx.categoryId ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}
              </select>
              <input type="text" class="input-control split-sub" placeholder="Subcategory ${i + 1}" style="width:115px;" />
              <input type="text" class="input-control split-notes" placeholder="Note (opt)" value="${escapeHtml(tx.notes || '')}" style="width:130px;" />
            </div>
          `).join('')}
        </div>
        <div style="display:flex;justify-content:space-between;align-items:center;margin-top:14px;">
          <button class="btn btn-outline btn-sm" id="addSplitLegBtn">+ Add Split Leg</button>
          <div style="display:flex;gap:10px;">
            <button class="btn btn-outline" id="cancelSplitBtn">Cancel</button>
            <button class="btn btn-primary" id="confirmSplitBtn">Commit Split</button>
          </div>
        </div>
      `, (modalEl) => {
        modalEl.querySelector('#cancelSplitBtn').onclick = closeModal;
        modalEl.querySelector('#addSplitLegBtn').onclick = () => {
          const row = document.createElement('div');
          row.className = 'split-item-row';
          row.style.cssText = 'display:flex;gap:8px;align-items:center;flex-wrap:wrap;';
          row.innerHTML = `
            <input type="number" step="0.01" class="input-control split-amt" value="0.00" style="width:95px;" />
            <select class="select-control split-cat" style="flex:1;min-width:140px;">
              ${sortedDropdownCats.map(c => `<option value="${c.categoryId}">${escapeHtml(c.name)}</option>`).join('')}
            </select>
            <input type="text" class="input-control split-sub" placeholder="Subcategory" style="width:115px;" />
            <input type="text" class="input-control split-notes" placeholder="Note (opt)" style="width:130px;" />
          `;
          modalEl.querySelector('#splitRowsContainer').appendChild(row);
        };

        modalEl.querySelector('#confirmSplitBtn').onclick = async () => {
          const splits = [];
          modalEl.querySelectorAll('.split-item-row').forEach(r => {
            splits.push({
              amount: Number(r.querySelector('.split-amt').value || 0),
              categoryId: Number(r.querySelector('.split-cat').value),
              subcategory: r.querySelector('.split-sub').value.trim() || null,
              notes: r.querySelector('.split-notes').value.trim() || null
            });
          });

          try {
            await apiFetch(`/api/transactions/${txId}/split`, {
              method: 'POST',
              body: JSON.stringify({ splits })
            });
            closeModal();
            showToast('Transaction split committed & backed up!');
            loadTransactionsTable(mountEl);
          } catch (err) {
            alert(`Split error: ${err.message}`);
          }
        };
      });
    };
  });

  mountEl.querySelectorAll('.tx-del-btn').forEach(btn => {
    btn.onclick = () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      const tx = data.items.find(x => x.transactionId === txId);
      if (!tx) return;

      openModal(`
        <h3 style="margin-top:0;color:#DC2626;">Delete ${tx.transactionType === 'IncomeDeposit' ? 'Income Deposit' : 'Expense'}?</h3>
        <div style="background:#FEF2F2;border:1px solid #FECACA;border-radius:10px;padding:12px;font-size:13px;margin-bottom:12px;">
          <div><strong>${escapeHtml(tx.cleanMerchantName)}</strong> (${escapeHtml(tx.transactionType)})</div>
          <div>Date: ${escapeHtml(tx.transactionDate)} &bull; Amount: <strong>${formatCurrency(tx.amount)}</strong></div>
          <div style="font-size:11px;color:#64748B;">Raw: ${escapeHtml(tx.rawDescription)}</div>
        </div>
        <label style="display:flex;align-items:center;gap:8px;font-size:13px;font-weight:600;margin:14px 0;">
          <input type="checkbox" id="singleDelTombstoneCheck" checked />
          <span>Add hash to Tombstone table so future CSV uploads never re-import this record</span>
        </label>
        <div style="display:flex;justify-content:flex-end;gap:10px;">
          <button class="btn btn-outline" id="cancelSingleDelBtn">Cancel</button>
          <button class="btn btn-danger" id="confirmSingleDelBtn">Delete from Database</button>
        </div>
      `, (modalEl) => {
        modalEl.querySelector('#cancelSingleDelBtn').onclick = closeModal;
        modalEl.querySelector('#confirmSingleDelBtn').onclick = async () => {
          const tomb = modalEl.querySelector('#singleDelTombstoneCheck').checked;
          await apiFetch(`/api/transactions/${txId}?addToTombstone=${tomb}`, { method: 'DELETE' });
          closeModal();
          showToast(`Deleted ${tx.transactionType} #${txId}`);
          loadTransactionsTable(mountEl);
        };
      });
    };
  });
}
