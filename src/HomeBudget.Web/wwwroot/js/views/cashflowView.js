import {
  state,
  apiFetch,
  formatCurrency,
  escapeHtml,
  sortCategories,
  setSortPreference,
  openCategorizeExpenseModal,
  openCategoryManagerModal
} from '../app.js';
import { renderSankeyDiagram } from '../components/sankeyChart.js';

export function groupUncategorizedByMerchant(uncatTxs, sortMode = 'amount_desc') {
  const map = new Map();
  for (const tx of uncatTxs) {
    const key = (tx.cleanMerchantName || tx.rawDescription || 'Unknown').trim().toUpperCase();
    if (!map.has(key)) {
      map.set(key, {
        merchantKey: key,
        merchantName: tx.cleanMerchantName || tx.rawDescription || 'Unknown',
        representativeTx: tx,
        transactions: [],
        totalAmount: 0,
        minDate: tx.transactionDate,
        maxDate: tx.transactionDate,
        lastUpdatedAt: tx.updatedAt || tx.createdAt || tx.transactionDate,
        accounts: new Set()
      });
    }
    const group = map.get(key);
    group.transactions.push(tx);
    group.totalAmount = Math.round((group.totalAmount + Number(tx.amount || 0)) * 100) / 100;
    if (tx.transactionDate < group.minDate) group.minDate = tx.transactionDate;
    if (tx.transactionDate > group.maxDate) group.maxDate = tx.transactionDate;
    const txUpd = tx.updatedAt || tx.createdAt || tx.transactionDate;
    if (txUpd > group.lastUpdatedAt) group.lastUpdatedAt = txUpd;
    if (tx.accountName) group.accounts.add(tx.accountName);
  }

  const groups = Array.from(map.values());
  return groups.sort((a, b) => {
    switch (sortMode) {
      case 'date_desc':
        return String(b.maxDate || '').localeCompare(String(a.maxDate || '')) || (Math.abs(b.totalAmount) - Math.abs(a.totalAmount));
      case 'recent':
        return String(b.lastUpdatedAt || '').localeCompare(String(a.lastUpdatedAt || '')) || (Math.abs(b.totalAmount) - Math.abs(a.totalAmount));
      case 'alpha_asc':
        return String(a.merchantName || '').localeCompare(String(b.merchantName || ''), undefined, { sensitivity: 'base' });
      case 'count_desc':
        return (b.transactions.length - a.transactions.length) || (Math.abs(b.totalAmount) - Math.abs(a.totalAmount));
      case 'amount_desc':
      default:
        return (Math.abs(b.totalAmount) - Math.abs(a.totalAmount)) || String(a.merchantName || '').localeCompare(String(b.merchantName || ''));
    }
  });
}

export function sortUncategorizedTransactions(uncatTxs, sortMode = 'amount_desc') {
  const list = Array.isArray(uncatTxs) ? [...uncatTxs] : [];
  return list.sort((a, b) => {
    switch (sortMode) {
      case 'date_desc':
        return String(b.transactionDate || '').localeCompare(String(a.transactionDate || '')) || (Math.abs(b.amount) - Math.abs(a.amount));
      case 'recent': {
        const tsA = String(a.updatedAt || a.createdAt || a.transactionDate || '');
        const tsB = String(b.updatedAt || b.createdAt || b.transactionDate || '');
        return tsB.localeCompare(tsA) || (Math.abs(b.amount) - Math.abs(a.amount));
      }
      case 'alpha_asc':
        return String(a.cleanMerchantName || a.rawDescription || '').localeCompare(
          String(b.cleanMerchantName || b.rawDescription || ''),
          undefined,
          { sensitivity: 'base' }
        );
      case 'amount_desc':
      case 'count_desc':
      default:
        return (Math.abs(Number(b.amount || 0)) - Math.abs(Number(a.amount || 0))) ||
          String(b.transactionDate || '').localeCompare(String(a.transactionDate || ''));
    }
  });
}

export async function renderCashflowView(container) {
  const params = new URLSearchParams({
    windowType: state.cashflowWindow,
    includeSubcategories: String(state.includeSubcategories),
    categorySort: state.cashflowCategorySort || 'spend_desc'
  });

  if (state.cashflowWindow === 'last_n_months') {
    params.set('months', String(state.cashflowMonths));
  } else if (state.cashflowWindow === 'custom' && state.cashflowStart && state.cashflowEnd) {
    params.set('startDate', state.cashflowStart);
    params.set('endDate', state.cashflowEnd);
  }

  const data = await apiFetch(`/api/cashflow?${params.toString()}`);
  const uncatRow = data.tableRows.find(r => r.categoryId === null);
  const rawUncatTxs = data.uncategorizedTransactions || (uncatRow ? uncatRow.transactions : []);
  const uncatTxs = sortUncategorizedTransactions(rawUncatTxs, state.triageSort);
  const merchantGroups = groupUncategorizedByMerchant(rawUncatTxs, state.triageSort);
  const sortedDropdownCategories = sortCategories(state.categories, state.categoryDropdownSort);

  container.innerHTML = `
    <div class="portrait-rotate-banner">
      <span>&#128241; Rotate your iPhone to <strong>Landscape</strong> for full-width Sankey view, or scroll below to categorize uncategorized expenses.</span>
    </div>

    <div class="page-header">
      <div>
        <h1 class="page-title">Household Cashflow &amp; Sankey</h1>
        <p class="page-subtitle">
          Window: <strong>${escapeHtml(data.startDate)}</strong> to <strong>${escapeHtml(data.endDate)}</strong>
          &bull; Income Source: <strong>${escapeHtml(data.primaryIncomeSourceLabel)}</strong>
        </p>
      </div>

      <div class="toolbar-row" style="margin-bottom:0;">
        <button class="btn btn-outline btn-sm" id="cashflowAddCatBtn">+ Add Category</button>
        <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
          Sort Categories:
          <select id="cashflowCatSortSelect" class="select-control" style="font-size:12px;padding:4px 8px;">
            <option value="spend_desc" ${state.cashflowCategorySort === 'spend_desc' ? 'selected' : ''}>Highest Spend ($)</option>
            <option value="spend_asc" ${state.cashflowCategorySort === 'spend_asc' ? 'selected' : ''}>Lowest Spend ($)</option>
            <option value="alpha_asc" ${state.cashflowCategorySort === 'alpha_asc' ? 'selected' : ''}>Alphabetical (A-Z)</option>
            <option value="recent" ${state.cashflowCategorySort === 'recent' ? 'selected' : ''}>Recent Changes</option>
            <option value="custom_order" ${state.cashflowCategorySort === 'custom_order' ? 'selected' : ''}>Custom Order</option>
          </select>
        </label>
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
          <input type="date" id="customStart" class="input-control" value="${escapeHtml(state.cashflowStart || data.startDate)}" />
        </label>
        <label style="font-size:13px;font-weight:600;">End Date:
          <input type="date" id="customEnd" class="input-control" value="${escapeHtml(state.cashflowEnd || data.endDate)}" />
        </label>
        <button class="btn btn-primary btn-sm" id="applyCustomRangeBtn">Apply Range</button>
      </div>
    ` : ''}

    <div class="kpi-grid">
      <div class="kpi-card">
        <div class="kpi-label">Resolved Window Income</div>
        <div class="kpi-value">${formatCurrency(data.totalIncome)}</div>
        <div class="kpi-sub">${escapeHtml(data.primaryIncomeSourceLabel)}</div>
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
              ? `&#9888;&#65039; Uncategorized Expenses Triage (${uncatTxs.length} item${uncatTxs.length === 1 ? '' : 's'} across ${merchantGroups.length} merchant${merchantGroups.length === 1 ? '' : 's'} &bull; ${formatCurrency(uncatRow ? uncatRow.netSpend : 0)})`
              : '&#10003; All Expenses in This Window Are Categorized!'}
          </h2>
          ${uncatTxs.length > 0 ? `
            <p style="margin:3px 0 0 0;font-size:12px;color:#92400E;">
              Categorize by merchant group or individual item below—you will be prompted to apply rules moving forward and/or to historical matches.
            </p>
          ` : ''}
        </div>
        <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;">
          ${uncatTxs.length > 0 ? `
            <label style="font-size:11px;font-weight:700;color:#92400E;display:inline-flex;align-items:center;gap:4px;">
              Sort Triage:
              <select id="triageSortSelect" class="select-control" style="font-size:12px;padding:3px 6px;">
                <option value="amount_desc" ${state.triageSort === 'amount_desc' ? 'selected' : ''}>Highest Amount ($)</option>
                <option value="date_desc" ${state.triageSort === 'date_desc' ? 'selected' : ''}>Newest Date</option>
                <option value="recent" ${state.triageSort === 'recent' ? 'selected' : ''}>Recent Changes</option>
                <option value="alpha_asc" ${state.triageSort === 'alpha_asc' ? 'selected' : ''}>Merchant (A-Z)</option>
                <option value="count_desc" ${state.triageSort === 'count_desc' ? 'selected' : ''}>Most Frequent</option>
              </select>
            </label>
            <label style="font-size:11px;font-weight:700;color:#92400E;display:inline-flex;align-items:center;gap:4px;">
              Dropdown Order:
              <select id="triageCatDropdownSort" class="select-control" style="font-size:12px;padding:3px 6px;">
                <option value="alpha_asc" ${state.categoryDropdownSort === 'alpha_asc' ? 'selected' : ''}>Alphabetical (A-Z)</option>
                <option value="recent" ${state.categoryDropdownSort === 'recent' ? 'selected' : ''}>Recent Changes</option>
                <option value="custom_order" ${state.categoryDropdownSort === 'custom_order' ? 'selected' : ''}>Custom Order</option>
                <option value="budget_desc" ${state.categoryDropdownSort === 'budget_desc' ? 'selected' : ''}>Highest Budget ($)</option>
              </select>
            </label>
            <div class="pill-group">
              <button class="pill-btn ${state.triageGroupByMerchant ? 'active' : ''}" id="triageModeGroupBtn">Group by Merchant (${merchantGroups.length})</button>
              <button class="pill-btn ${!state.triageGroupByMerchant ? 'active' : ''}" id="triageModeChronoBtn">Chronological List (${uncatTxs.length})</button>
            </div>
          ` : ''}
          <button class="btn btn-outline btn-sm" id="triageNewCatBtn">+ New Category</button>
        </div>
      </div>

      ${uncatTxs.length > 0 ? (state.triageGroupByMerchant ? `
        <div class="table-responsive" style="background:#FFFFFF;border-radius:10px;border:1px solid #FDE68A;">
          <table class="data-table">
            <thead>
              <tr>
                <th>Merchant Group</th>
                <th>Count &amp; Date Span</th>
                <th>Accounts</th>
                <th>Group Total</th>
                <th>Quick Categorize Merchant</th>
              </tr>
            </thead>
            <tbody>
              ${merchantGroups.map(g => `
                <tr data-uncat-id="${g.representativeTx.transactionId}">
                  <td>
                    <strong>${escapeHtml(g.merchantName)}</strong>
                    <div style="font-size:11px;color:#64748B;">Sample: <code>${escapeHtml(g.representativeTx.rawDescription)}</code></div>
                  </td>
                  <td style="white-space:nowrap;">
                    <span class="bigbox-pill">${g.transactions.length} item${g.transactions.length === 1 ? '' : 's'}</span>
                    <div style="font-size:11px;color:#64748B;margin-top:2px;">
                      ${g.minDate === g.maxDate ? escapeHtml(g.minDate) : `${escapeHtml(g.minDate)} &ndash; ${escapeHtml(g.maxDate)}`}
                    </div>
                  </td>
                  <td>${escapeHtml(Array.from(g.accounts).join(', '))}</td>
                  <td class="${g.totalAmount < 0 ? 'amount-refund' : 'amount-expense'}"><strong>${formatCurrency(g.totalAmount)}</strong></td>
                  <td style="white-space:nowrap;">
                    <div style="display:flex;gap:6px;align-items:center;">
                      <select class="select-control quick-uncat-select" data-tx-id="${g.representativeTx.transactionId}">
                        <option value="">Choose Category...</option>
                        ${sortedDropdownCategories.map(c => `<option value="${c.categoryId}">${escapeHtml(c.name)}</option>`).join('')}
                        <option value="__NEW__">+ Create New Category...</option>
                      </select>
                      <button class="btn btn-primary btn-sm quick-uncat-btn" data-tx-id="${g.representativeTx.transactionId}">Categorize (${g.transactions.length})...</button>
                    </div>
                  </td>
                </tr>
              `).join('')}
            </tbody>
          </table>
        </div>
      ` : `
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
                  <td style="white-space:nowrap;">${escapeHtml(t.transactionDate)}</td>
                  <td>
                    <strong>${escapeHtml(t.cleanMerchantName)}</strong>
                    <div style="font-size:11px;color:#64748B;">${escapeHtml(t.rawDescription)}</div>
                  </td>
                  <td>${escapeHtml(t.accountName)}</td>
                  <td class="${t.amount < 0 ? 'amount-refund' : 'amount-expense'}">${formatCurrency(t.amount)}</td>
                  <td style="white-space:nowrap;">
                    <div style="display:flex;gap:6px;align-items:center;">
                      <select class="select-control quick-uncat-select" data-tx-id="${t.transactionId}">
                        <option value="">Choose Category...</option>
                        ${sortedDropdownCategories.map(c => `<option value="${c.categoryId}">${escapeHtml(c.name)}</option>`).join('')}
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
      `) : ''}
    </div>

    <div class="card-panel">
      <h2 style="margin:0 0 14px 0;font-size:16px;font-weight:700;">Tabular Cashflow &amp; Account Source Breakdown</h2>
      <div class="table-responsive">
        <table class="data-table">
          <thead>
            <tr>
              <th>Category</th>
              ${data.accounts.map(a => `<th>${escapeHtml(a.accountName)}</th>`).join('')}
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
                  <span class="cat-dot" style="background:${escapeHtml(row.colorHex)};display:inline-block;margin-right:8px;"></span>
                  <strong>${escapeHtml(row.categoryName)}</strong>
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
                        <span><strong>${escapeHtml(t.transactionDate)}</strong> &bull; ${escapeHtml(t.cleanMerchantName)} (${escapeHtml(t.accountName)})${t.subcategory ? ` [${escapeHtml(t.subcategory)}]` : ''}</span>
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

  const cashflowCatSortSelect = container.querySelector('#cashflowCatSortSelect');
  if (cashflowCatSortSelect) {
    cashflowCatSortSelect.onchange = (e) => {
      setSortPreference('cashflowCategorySort', e.target.value);
      renderCashflowView(container);
    };
  }

  const triageSortSelect = container.querySelector('#triageSortSelect');
  if (triageSortSelect) {
    triageSortSelect.onchange = (e) => {
      setSortPreference('triageSort', e.target.value);
      renderCashflowView(container);
    };
  }

  const triageCatDropdownSort = container.querySelector('#triageCatDropdownSort');
  if (triageCatDropdownSort) {
    triageCatDropdownSort.onchange = (e) => {
      setSortPreference('categoryDropdownSort', e.target.value);
      renderCashflowView(container);
    };
  }

  const groupBtn = container.querySelector('#triageModeGroupBtn');
  const chronoBtn = container.querySelector('#triageModeChronoBtn');
  if (groupBtn) {
    groupBtn.onclick = () => {
      state.triageGroupByMerchant = true;
      renderCashflowView(container);
    };
  }
  if (chronoBtn) {
    chronoBtn.onclick = () => {
      state.triageGroupByMerchant = false;
      renderCashflowView(container);
    };
  }

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
