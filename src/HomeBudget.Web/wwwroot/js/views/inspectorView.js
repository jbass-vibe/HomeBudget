import { state, apiFetch, formatCurrency, showToast, openModal, closeModal } from '../app.js';

const filterState = {
  startDate: '',
  endDate: '',
  accountId: '',
  categoryId: '',
  uncategorizedOnly: false,
  transactionType: '',
  search: ''
};

export async function renderInspectorView(container) {
  container.innerHTML = `
    <div class="page-header">
      <div>
        <h1 class="page-title">Transaction Data Store Inspector</h1>
        <p class="page-subtitle">Inspect, recategorize, split multi-category purchases, or tombstone-delete irrelevant entries</p>
      </div>
    </div>

    <div class="card-panel">
      <div style="display:flex;flex-wrap:wrap;gap:10px;align-items:flex-end;">
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Start Date</label>
          <input type="date" id="fltStart" class="input-control" value="${filterState.startDate}" />
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">End Date</label>
          <input type="date" id="fltEnd" class="input-control" value="${filterState.endDate}" />
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Account</label>
          <select id="fltAccount" class="select-control">
            <option value="">All Accounts</option>
            ${state.accounts.map(a => `<option value="${a.accountId}" ${String(a.accountId) === filterState.accountId ? 'selected' : ''}>${a.accountName}</option>`).join('')}
          </select>
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Category</label>
          <select id="fltCategory" class="select-control">
            <option value="">All Categories</option>
            <option value="uncat" ${filterState.uncategorizedOnly ? 'selected' : ''}>-- Uncategorized Only --</option>
            ${state.categories.map(c => `<option value="${c.categoryId}" ${!filterState.uncategorizedOnly && String(c.categoryId) === filterState.categoryId ? 'selected' : ''}>${c.name}</option>`).join('')}
          </select>
        </div>
        <div>
          <label style="font-size:11px;font-weight:700;display:block;">Type</label>
          <select id="fltType" class="select-control">
            <option value="">All Types</option>
            <option value="Expense" ${filterState.transactionType === 'Expense' ? 'selected' : ''}>Expense</option>
            <option value="Refund" ${filterState.transactionType === 'Refund' ? 'selected' : ''}>Refund</option>
            <option value="IncomeDeposit" ${filterState.transactionType === 'IncomeDeposit' ? 'selected' : ''}>Google Income Deposit</option>
            <option value="CreditCardPayoff" ${filterState.transactionType === 'CreditCardPayoff' ? 'selected' : ''}>Credit Card Payoff</option>
          </select>
        </div>
        <div style="flex:1;min-width:180px;">
          <label style="font-size:11px;font-weight:700;display:block;">Search Merchant / Notes</label>
          <input type="text" id="fltSearch" class="input-control" style="width:100%;" placeholder="Search description..." value="${filterState.search}" />
        </div>
        <button class="btn btn-primary" id="applyFiltersBtn">Filter</button>
      </div>
    </div>

    <div id="inspectorTableMount" class="card-panel">Loading transactions...</div>
  `;

  const triggerReload = () => {
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
    filterState.search = container.querySelector('#fltSearch').value.trim();
    loadTransactionsTable(container.querySelector('#inspectorTableMount'));
  };

  container.querySelector('#applyFiltersBtn').onclick = triggerReload;
  await loadTransactionsTable(container.querySelector('#inspectorTableMount'));
}

async function loadTransactionsTable(mountEl) {
  const params = new URLSearchParams({ pageSize: '200' });
  if (filterState.startDate) params.set('startDate', filterState.startDate);
  if (filterState.endDate) params.set('endDate', filterState.endDate);
  if (filterState.accountId) params.set('accountId', filterState.accountId);
  if (filterState.uncategorizedOnly) params.set('uncategorizedOnly', 'true');
  else if (filterState.categoryId) params.set('categoryId', filterState.categoryId);
  if (filterState.transactionType) params.set('transactionType', filterState.transactionType);
  if (filterState.search) params.set('search', filterState.search);

  const data = await apiFetch(`/api/transactions?${params.toString()}`);

  const catOptions = (selectedId) => `
    <option value="">-- Uncategorized --</option>
    ${state.categories.map(c => `<option value="${c.categoryId}" ${c.categoryId === selectedId ? 'selected' : ''}>${c.name}</option>`).join('')}
  `;

  mountEl.innerHTML = `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;">
      <strong>Showing ${data.items.length} of ${data.totalCount} transactions</strong>
      <span style="font-size:12px;color:#64748B;">Inline edits trigger a 60s coalesced SQLite backup automatically</span>
    </div>
    <div class="table-responsive">
      <table class="data-table">
        <thead>
          <tr>
            <th>Date</th>
            <th>Account</th>
            <th>Merchant / Raw Description</th>
            <th>Type</th>
            <th>Amount</th>
            <th>Category</th>
            <th>Subcategory</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          ${data.items.length === 0 ? `
            <tr><td colspan="8" style="text-align:center;color:#64748B;">No matching transactions found.</td></tr>
          ` : data.items.map(tx => `
            <tr data-tx-id="${tx.transactionId}">
              <td style="white-space:nowrap;">${tx.transactionDate}</td>
              <td>${tx.accountName}</td>
              <td>
                <input type="text" class="input-control tx-merchant" value="${tx.cleanMerchantName.replace(/"/g, '&quot;')}" style="width:180px;" />
                <div style="font-size:11px;color:#64748B;">${tx.rawDescription}</div>
              </td>
              <td>
                <select class="select-control tx-type">
                  <option value="Expense" ${tx.transactionType === 'Expense' ? 'selected' : ''}>Expense</option>
                  <option value="Refund" ${tx.transactionType === 'Refund' ? 'selected' : ''}>Refund</option>
                  <option value="IncomeDeposit" ${tx.transactionType === 'IncomeDeposit' ? 'selected' : ''}>IncomeDeposit</option>
                  <option value="CreditCardPayoff" ${tx.transactionType === 'CreditCardPayoff' ? 'selected' : ''}>CreditCardPayoff</option>
                </select>
              </td>
              <td class="${tx.amount < 0 ? 'amount-refund' : 'amount-expense'}">${formatCurrency(tx.amount)}</td>
              <td>
                <select class="select-control tx-cat">
                  ${catOptions(tx.categoryId)}
                </select>
              </td>
              <td>
                <input type="text" class="input-control tx-subcat" value="${tx.subcategory || ''}" placeholder="None" style="width:110px;" />
              </td>
              <td style="white-space:nowrap;">
                <button class="btn btn-primary btn-sm tx-save-btn">Save</button>
                <button class="btn btn-outline btn-sm tx-rule-btn" title="Create Rule & Back-Propagate">+Rule</button>
                <button class="btn btn-outline btn-sm tx-split-btn" title="Split Transaction">Split</button>
                <button class="btn btn-danger btn-sm tx-del-btn" title="Delete & Tombstone">&#128465;</button>
              </td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    </div>
  `;

  // Inline Save Handler
  mountEl.querySelectorAll('.tx-save-btn').forEach(btn => {
    btn.onclick = async () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      const cleanMerchantName = tr.querySelector('.tx-merchant').value.trim();
      const transactionType = tr.querySelector('.tx-type').value;
      const catVal = tr.querySelector('.tx-cat').value;
      const subcategory = tr.querySelector('.tx-subcat').value.trim();

      await apiFetch(`/api/transactions/${txId}`, {
        method: 'PUT',
        body: JSON.stringify({
          categoryId: catVal ? Number(catVal) : null,
          subcategory,
          cleanMerchantName,
          transactionType
        })
      });
      showToast(`Saved transaction #${txId}`);
    };
  });

  // Create Permanent Rule & Back-Propagate Modal
  mountEl.querySelectorAll('.tx-rule-btn').forEach(btn => {
    btn.onclick = () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      const tx = data.items.find(x => x.transactionId === txId);
      const selectedCatId = Number(tr.querySelector('.tx-cat').value || tx.categoryId || state.categories[0]?.categoryId);
      const subcat = tr.querySelector('.tx-subcat').value.trim();

      openModal(`
        <h3 style="margin-top:0;">Create Auto-Categorization Rule &amp; Back-Propagate</h3>
        <p style="font-size:13px;color:#64748B;">Raw description: <code>${tx.rawDescription}</code></p>
        <div style="display:flex;flex-direction:column;gap:12px;margin:14px 0;">
          <div>
            <label style="font-size:12px;font-weight:700;display:block;">Match Substring Keyword</label>
            <input type="text" id="ruleKeywordInput" class="input-control" style="width:100%;" value="${tx.cleanMerchantName}" />
          </div>
          <div>
            <label style="font-size:12px;font-weight:700;display:block;">Target Category</label>
            <select id="ruleCatSelect" class="select-control" style="width:100%;">
              ${state.categories.map(c => `<option value="${c.categoryId}" ${c.categoryId === selectedCatId ? 'selected' : ''}>${c.name}</option>`).join('')}
            </select>
          </div>
          <div>
            <label style="font-size:12px;font-weight:700;display:block;">Default Subcategory (Optional)</label>
            <input type="text" id="ruleSubcatInput" class="input-control" style="width:100%;" value="${subcat}" />
          </div>
          <label style="display:flex;align-items:center;gap:8px;font-size:13px;font-weight:600;">
            <input type="checkbox" id="ruleBackPropCheck" checked />
            <span>Retroactively back-propagate this rule to all historical transactions</span>
          </label>
        </div>
        <div style="display:flex;justify-content:flex-end;gap:10px;">
          <button class="btn btn-outline" id="cancelRuleModal">Cancel</button>
          <button class="btn btn-primary" id="confirmRuleModal">Save &amp; Back-Propagate</button>
        </div>
      `, (modalEl) => {
        modalEl.querySelector('#cancelRuleModal').onclick = closeModal;
        modalEl.querySelector('#confirmRuleModal').onclick = async () => {
          const pattern = modalEl.querySelector('#ruleKeywordInput').value.trim();
          const catId = Number(modalEl.querySelector('#ruleCatSelect').value);
          const defaultSub = modalEl.querySelector('#ruleSubcatInput').value.trim();
          const backProp = modalEl.querySelector('#ruleBackPropCheck').checked;

          const res = await apiFetch(`/api/transactions/${txId}`, {
            method: 'PUT',
            body: JSON.stringify({
              categoryId: catId,
              subcategory: defaultSub,
              createRulePattern: pattern,
              backPropagateRule: backProp
            })
          });
          closeModal();
          showToast(`Rule created! Back-propagated to ${res.backPropagatedCount} historical transactions.`);
          loadTransactionsTable(mountEl);
        };
      });
    };
  });

  // Split Transaction Modal
  mountEl.querySelectorAll('.tx-split-btn').forEach(btn => {
    btn.onclick = () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      const tx = data.items.find(x => x.transactionId === txId);
      const half1 = Math.round((tx.amount / 2) * 100) / 100;
      const half2 = Math.round((tx.amount - half1) * 100) / 100;

      openModal(`
        <h3 style="margin-top:0;">Split Transaction (${formatCurrency(tx.amount)})</h3>
        <p style="font-size:13px;color:#64748B;">${tx.transactionDate} &bull; ${tx.rawDescription}</p>
        <div id="splitRowsContainer" style="display:flex;flex-direction:column;gap:10px;margin:14px 0;">
          ${[half1, half2].map((amt, i) => `
            <div class="split-item-row" style="display:flex;gap:8px;align-items:center;">
              <input type="number" step="0.01" class="input-control split-amt" value="${amt}" style="width:100px;" />
              <select class="select-control split-cat" style="flex:1;">
                ${state.categories.map(c => `<option value="${c.categoryId}" ${c.categoryId === tx.categoryId ? 'selected' : ''}>${c.name}</option>`).join('')}
              </select>
              <input type="text" class="input-control split-sub" placeholder="Subcategory ${i + 1}" style="width:130px;" />
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
          row.style.cssText = 'display:flex;gap:8px;align-items:center;';
          row.innerHTML = `
            <input type="number" step="0.01" class="input-control split-amt" value="0.00" style="width:100px;" />
            <select class="select-control split-cat" style="flex:1;">
              ${state.categories.map(c => `<option value="${c.categoryId}">${c.name}</option>`).join('')}
            </select>
            <input type="text" class="input-control split-sub" placeholder="Subcategory" style="width:130px;" />
          `;
          modalEl.querySelector('#splitRowsContainer').appendChild(row);
        };

        modalEl.querySelector('#confirmSplitBtn').onclick = async () => {
          const splits = [];
          modalEl.querySelectorAll('.split-item-row').forEach(r => {
            splits.push({
              amount: Number(r.querySelector('.split-amt').value || 0),
              categoryId: Number(r.querySelector('.split-cat').value),
              subcategory: r.querySelector('.split-sub').value.trim() || null
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

  // Tombstone Delete Handler
  mountEl.querySelectorAll('.tx-del-btn').forEach(btn => {
    btn.onclick = async () => {
      const tr = btn.closest('tr');
      const txId = Number(tr.dataset.txId);
      if (!confirm('Delete this transaction and add its hash to the Tombstone store so future CSV uploads never re-import it?')) {
        return;
      }
      await apiFetch(`/api/transactions/${txId}?addToTombstone=true`, { method: 'DELETE' });
      showToast(`Deleted & tombstoned transaction #${txId}`);
      loadTransactionsTable(mountEl);
    };
  });
}
