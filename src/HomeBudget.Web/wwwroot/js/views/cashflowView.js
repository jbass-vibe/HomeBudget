import {
  state,
  apiFetch,
  formatCurrency,
  openCategorizeExpenseModal,
  openCategoryManagerModal
} from '../app.js';
import { renderSankeyDiagram } from '../components/sankeyChart.js';

export async function renderCashflowView(container) {
  const params = new URLSearchParams({
    windowType: state.cashflowWindow,
    includeSubcategories: String(state.includeSubcategories)
  });

  if (state.cashflowWindow === 'last_n_months') {
    params.set('months', String(state.cashflowMonths));
  } else if (state.cashflowWindow === 'custom' && state.cashflowStart && state.cashflowEnd) {
    params.set('startDate', state.cashflowStart);
    params.set('endDate', state.cashflowEnd);
  }

  const data = await apiFetch(`/api/cashflow?${params.toString()}`);
  const uncatRow = data.tableRows.find(r => r.categoryId === null);
  const uncatTxs = data.uncategorizedTransactions || (uncatRow ? uncatRow.transactions : []);

  container.innerHTML = `
    <div class="portrait-rotate-banner">
      <span>&#128241; Rotate your iPhone to <strong>Landscape</strong> for full-width Sankey view, or scroll below to categorize uncategorized expenses.</span>
    </div>

    <div class="page-header">
      <div>
        <h1 class="page-title">Household Cashflow &amp; Sankey</h1>
        <p class="page-subtitle">
          Window: <strong>${data.startDate}</strong> to <strong>${data.endDate}</strong>
          &bull; Income Source: <strong>${data.primaryIncomeSourceLabel}</strong>
        </p>
      </div>

      <div class="toolbar-row" style="margin-bottom:0;">
        <button class="btn btn-outline btn-sm" id="cashflowAddCatBtn">+ Add Category</button>
        <div class="pill-group">
          <button class="pill-btn ${state.cashflowWindow === 'mtd' ? 'active' : ''}" data-win="mtd">Current MTD</button>
          <button class="pill-btn ${state.cashflowWindow === 'prior_month' ? 'active' : ''}" data-win="prior_month">Prior Month</button>
          <button class="pill-btn ${state.cashflowWindow === 'last_n_months' && state.cashflowMonths === 3 ? 'active' : ''}" data-win="3m">Last 3M</button>
          <button class="pill-btn ${state.cashflowWindow === 'last_n_months' && state.cashflowMonths === 6 ? 'active' : ''}" data-win="6m">Last 6M</button>
          <button class="pill-btn ${state.cashflowWindow === 'ytd' ? 'active' : ''}" data-win="ytd">Current Year</button>
          <button class="pill-btn ${state.cashflowWindow === 'prior_year' ? 'active' : ''}" data-win="prior_year">Prior Year</button>
          <button class="pill-btn ${state.cashflowWindow === 'custom' ? 'active' : ''}" data-win="custom">Custom</button>
        </div>

        ${data.hasAnySubcategories ? `
          <label class="btn btn-outline btn-sm" style="cursor:pointer;">
            <input type="checkbox" id="subcatToggle" ${state.includeSubcategories ? 'checked' : ''} />
            <span>Show Subcategories</span>
          </label>
        ` : ''}
      </div>
    </div>

    ${state.cashflowWindow === 'custom' ? `
      <div class="card-panel" style="display:flex;flex-wrap:wrap;gap:10px;align-items:center;">
        <label style="font-size:13px;font-weight:600;">Start Date:
          <input type="date" id="customStart" class="input-control" value="${state.cashflowStart || data.startDate}" />
        </label>
        <label style="font-size:13px;font-weight:600;">End Date:
          <input type="date" id="customEnd" class="input-control" value="${state.cashflowEnd || data.endDate}" />
        </label>
        <button class="btn btn-primary btn-sm" id="applyCustomRangeBtn">Apply Range</button>
      </div>
    ` : ''}

    <div class="kpi-grid">
      <div class="kpi-card">
        <div class="kpi-label">Resolved Window Income</div>
        <div class="kpi-value">${formatCurrency(data.totalIncome)}</div>
        <div class="kpi-sub">${data.primaryIncomeSourceLabel}</div>
      </div>
      <div class="kpi-card">
        <div class="kpi-label">Total Net Spending</div>
        <div class="kpi-value">${formatCurrency(data.totalNetSpend)}</div>
        <div class="kpi-sub">${data.totalIncome > 0 ? ((data.totalNetSpend / data.totalIncome) * 100).toFixed(1) : '0.0'}% of Income</div>
      </div>
      <div class="kpi-card">
        <div class="kpi-label">${data.overIncomeDeficit > 0 ? 'Over-Income Deficit' : 'Unspent Cashflow'}</div>
        <div class="kpi-value" style="color:${data.overIncomeDeficit > 0 ? '#DC2626' : '#059669'}">
          ${formatCurrency(data.overIncomeDeficit > 0 ? data.overIncomeDeficit : data.unspentCashflow)}
        </div>
        <div class="kpi-sub">Net household balance for period</div>
      </div>
      <div class="kpi-card" style="border-color:${uncatTxs.length > 0 ? '#F59E0B' : '#10B981'};">
        <div class="kpi-label">Uncategorized Expenses</div>
        <div class="kpi-value" style="color:${uncatTxs.length > 0 ? '#D97706' : '#059669'}">
          ${uncatTxs.length} (${formatCurrency(uncatRow ? uncatRow.netSpend : 0)})
        </div>
        <div class="kpi-sub">${uncatTxs.length > 0 ? 'Categorize below to update Sankey' : 'All expenses categorized!'}</div>
      </div>
    </div>

    <div class="card-panel sankey-card-panel">
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;">
        <h2 style="margin:0;font-size:16px;font-weight:700;">Cashflow Sankey Diagram</h2>
        <span style="font-size:12px;color:#64748B;">Tap any flow ribbon or the Uncategorized node to categorize or inspect</span>
      </div>
      <div id="sankeyMount" class="sankey-wrapper"></div>
    </div>

    <!-- Requirement 3: Uncategorized Expenses Triage Panel on Visualization Page -->
    <div id="uncategorizedTriagePanel" class="card-panel" style="border:2px solid ${uncatTxs.length > 0 ? '#F59E0B' : '#E2E8F0'};background:${uncatTxs.length > 0 ? '#FFFBEB' : '#FFFFFF'};">
      <div style="display:flex;flex-wrap:wrap;justify-content:space-between;align-items:center;gap:10px;margin-bottom:${uncatTxs.length > 0 ? '12px' : '0'};">
        <div>
          <h2 style="margin:0;font-size:16px;font-weight:800;color:${uncatTxs.length > 0 ? '#B45309' : '#059669'};">
            ${uncatTxs.length > 0
              ? `&#9888;&#65039; Uncategorized Expenses Triage (${uncatTxs.length} item${uncatTxs.length === 1 ? '' : 's'} &bull; ${formatCurrency(uncatRow ? uncatRow.netSpend : 0)})`
              : '&#10003; All Expenses in This Window Are Categorized!'}
          </h2>
          ${uncatTxs.length > 0 ? `
            <p style="margin:3px 0 0 0;font-size:12px;color:#92400E;">
              Select a category (or create a new category) for any item below—you will be prompted to apply it to similar future and/or past expenses automatically.
            </p>
          ` : ''}
        </div>
        <button class="btn btn-outline btn-sm" id="triageNewCatBtn">+ New Category</button>
      </div>

      ${uncatTxs.length > 0 ? `
        <div class="table-responsive" style="background:#FFFFFF;border-radius:10px;border:1px solid #FDE68A;">
          <table class="data-table">
            <thead>
              <tr>
                <th>Date</th>
                <th>Merchant / Description</th>
                <th>Account</th>
                <th>Amount</th>
                <th>Quick Categorize (Prompts for Future / Past)</th>
              </tr>
            </thead>
            <tbody>
              ${uncatTxs.map(t => `
                <tr data-uncat-id="${t.transactionId}">
                  <td style="white-space:nowrap;">${t.transactionDate}</td>
                  <td>
                    <strong>${t.cleanMerchantName}</strong>
                    <div style="font-size:11px;color:#64748B;">${t.rawDescription}</div>
                  </td>
                  <td>${t.accountName}</td>
                  <td class="${t.amount < 0 ? 'amount-refund' : 'amount-expense'}">${formatCurrency(t.amount)}</td>
                  <td style="white-space:nowrap;">
                    <div style="display:flex;gap:6px;align-items:center;">
                      <select class="select-control quick-uncat-select" data-tx-id="${t.transactionId}">
                        <option value="">Choose Category...</option>
                        ${state.categories.map(c => `<option value="${c.categoryId}">${c.name}</option>`).join('')}
                        <option value="__NEW__">+ Create New Category...</option>
                      </select>
                      <button class="btn btn-primary btn-sm quick-uncat-btn" data-tx-id="${t.transactionId}">Categorize...</button>
                    </div>
                  </td>
                </tr>
              `).join('')}
            </tbody>
          </table>
        </div>
      ` : ''}
    </div>

    <div class="card-panel">
      <h2 style="margin:0 0 14px 0;font-size:16px;font-weight:700;">Tabular Cashflow &amp; Account Source Breakdown</h2>
      <div class="table-responsive">
        <table class="data-table">
          <thead>
            <tr>
              <th>Category</th>
              ${data.accounts.map(a => `<th>${a.accountName}</th>`).join('')}
              <th>Net Spend</th>
              <th>% of Spend</th>
              <th>% of Income</th>
              <th>Manage</th>
            </tr>
          </thead>
          <tbody>
            ${data.tableRows.length === 0 ? `
              <tr><td colspan="${5 + data.accounts.length}" style="text-align:center;color:#64748B;">No expenses recorded in this window.</td></tr>
            ` : data.tableRows.map((row, idx) => `
              <tr class="cat-summary-row" data-expand-row="${idx}" style="cursor:pointer;">
                <td>
                  <span class="cat-dot" style="background:${row.colorHex};display:inline-block;margin-right:8px;"></span>
                  <strong>${row.categoryName}</strong>
                  <span style="font-size:11px;color:#64748B;margin-left:6px;">(${row.transactions.length}) &#9662;</span>
                </td>
                ${data.accounts.map(a => `<td>${formatCurrency(row.spendByAccount[a.accountId] || 0)}</td>`).join('')}
                <td class="amount-expense">${formatCurrency(row.netSpend)}</td>
                <td>${row.pctOfTotalSpend}%</td>
                <td>${row.pctOfIncome}%</td>
                <td>
                  ${row.categoryId ? `<button class="btn btn-outline btn-sm edit-cat-inline-btn" data-cat-id="${row.categoryId}">Edit / Delete</button>` : ''}
                </td>
              </tr>
              <tr id="cat_detail_${idx}" style="display:none;background:#F8FAFC;">
                <td colspan="${5 + data.accounts.length}" style="padding:12px 20px;">
                  <div style="max-height:260px;overflow-y:auto;">
                    ${row.transactions.map(t => `
                      <div style="display:flex;justify-content:space-between;align-items:center;padding:6px 0;border-bottom:1px dashed #E2E8F0;font-size:12px;">
                        <span><strong>${t.transactionDate}</strong> &bull; ${t.cleanMerchantName} (${t.accountName})${t.subcategory ? ` [${t.subcategory}]` : ''}</span>
                        <div style="display:flex;align-items:center;gap:10px;">
                          <span class="${t.amount < 0 ? 'amount-refund' : 'amount-expense'}">${formatCurrency(t.amount)}</span>
                          <button class="btn btn-outline btn-sm recat-tx-btn" data-tx-id="${t.transactionId}">Recategorize</button>
                        </div>
                      </div>
                    `).join('')}
                  </div>
                </td>
              </tr>
            `).join('')}
          </tbody>
        </table>
      </div>

      <div class="payoff-callout">
        <span>&#128179; <strong>Credit Card Payoffs in Period:</strong> Excluded from expense totals to prevent double-counting</span>
        <strong>${formatCurrency(data.creditCardPayoffsTotal)}</strong>
      </div>
    </div>
  `;

  const sankeyMount = container.querySelector('#sankeyMount');
  renderSankeyDiagram(sankeyMount, data.sankey, data.totalIncome, (clickedNode) => {
    if (clickedNode.id === 'cat_uncat') {
      const panel = container.querySelector('#uncategorizedTriagePanel');
      if (panel) panel.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
  });

  const openAddCat = () => openCategoryManagerModal(null, () => renderCashflowView(container));
  container.querySelector('#cashflowAddCatBtn').onclick = openAddCat;
  container.querySelector('#triageNewCatBtn').onclick = openAddCat;

  container.querySelectorAll('.edit-cat-inline-btn').forEach(btn => {
    btn.onclick = (e) => {
      e.stopPropagation();
      const catId = Number(btn.dataset.catId);
      const catObj = state.categories.find(c => c.categoryId === catId);
      if (catObj) {
        openCategoryManagerModal(catObj, () => renderCashflowView(container));
      }
    };
  });

  const allWindowTxs = data.tableRows.flatMap(r => r.transactions);

  container.querySelectorAll('.quick-uncat-select').forEach(sel => {
    sel.onchange = () => {
      if (!sel.value) return;
      const txId = Number(sel.dataset.txId);
      const tx = uncatTxs.find(x => x.transactionId === txId) || allWindowTxs.find(x => x.transactionId === txId);
      if (tx) {
        openCategorizeExpenseModal(tx, () => renderCashflowView(container), sel.value);
      }
    };
  });

  container.querySelectorAll('.quick-uncat-btn').forEach(btn => {
    btn.onclick = () => {
      const txId = Number(btn.dataset.txId);
      const tx = uncatTxs.find(x => x.transactionId === txId) || allWindowTxs.find(x => x.transactionId === txId);
      if (tx) {
        openCategorizeExpenseModal(tx, () => renderCashflowView(container));
      }
    };
  });

  container.querySelectorAll('.recat-tx-btn').forEach(btn => {
    btn.onclick = () => {
      const txId = Number(btn.dataset.txId);
      const tx = allWindowTxs.find(x => x.transactionId === txId);
      if (tx) {
        openCategorizeExpenseModal(tx, () => renderCashflowView(container));
      }
    };
  });

  container.querySelectorAll('[data-win]').forEach(btn => {
    btn.onclick = () => {
      const w = btn.dataset.win;
      if (w === '3m') {
        state.cashflowWindow = 'last_n_months';
        state.cashflowMonths = 3;
      } else if (w === '6m') {
        state.cashflowWindow = 'last_n_months';
        state.cashflowMonths = 6;
      } else {
        state.cashflowWindow = w;
      }
      renderCashflowView(container);
    };
  });

  const subToggle = container.querySelector('#subcatToggle');
  if (subToggle) {
    subToggle.onchange = (e) => {
      state.includeSubcategories = e.target.checked;
      renderCashflowView(container);
    };
  }

  const applyCustomBtn = container.querySelector('#applyCustomRangeBtn');
  if (applyCustomBtn) {
    applyCustomBtn.onclick = () => {
      state.cashflowStart = container.querySelector('#customStart').value;
      state.cashflowEnd = container.querySelector('#customEnd').value;
      renderCashflowView(container);
    };
  }

  container.querySelectorAll('[data-expand-row]').forEach(rowEl => {
    rowEl.onclick = () => {
      const idx = rowEl.dataset.expandRow;
      const detailEl = container.querySelector(`#cat_detail_${idx}`);
      if (detailEl) {
        detailEl.style.display = detailEl.style.display === 'none' ? 'table-row' : 'none';
      }
    };
  });
}
