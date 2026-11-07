import { state, apiFetch, formatCurrency } from '../app.js';
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

  container.innerHTML = `
    <div class="portrait-rotate-banner">
      <span>&#128241; Rotate your iPhone to <strong>Landscape</strong> for full-width Sankey view, or scroll below for the Tabular Breakdown.</span>
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
    </div>

    <div class="card-panel sankey-card-panel">
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;">
        <h2 style="margin:0;font-size:16px;font-weight:700;">Cashflow Sankey Diagram</h2>
        <span style="font-size:12px;color:#64748B;">Tap or hover any flow ribbon or node for details</span>
      </div>
      <div id="sankeyMount" class="sankey-wrapper"></div>
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
            </tr>
          </thead>
          <tbody>
            ${data.tableRows.length === 0 ? `
              <tr><td colspan="${4 + data.accounts.length}" style="text-align:center;color:#64748B;">No expenses recorded in this window.</td></tr>
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
              </tr>
              <tr id="cat_detail_${idx}" style="display:none;background:#F8FAFC;">
                <td colspan="${4 + data.accounts.length}" style="padding:12px 20px;">
                  ${row.subcategories.length > 0 ? `
                    <div style="margin-bottom:10px;font-size:12px;">
                      <strong>Subcategories:</strong>
                      ${row.subcategories.map(s => `<span class="bigbox-pill" style="margin-left:6px;">${s.subcategoryName}: ${formatCurrency(s.netSpend)}</span>`).join('')}
                    </div>
                  ` : ''}
                  <div style="max-height:220px;overflow-y:auto;">
                    ${row.transactions.map(t => `
                      <div style="display:flex;justify-content:space-between;padding:4px 0;border-bottom:1px dashed #E2E8F0;font-size:12px;">
                        <span><strong>${t.transactionDate}</strong> &bull; ${t.cleanMerchantName} (${t.accountName})${t.subcategory ? ` [${t.subcategory}]` : ''}</span>
                        <span class="${t.amount < 0 ? 'amount-refund' : 'amount-expense'}">${formatCurrency(t.amount)}</span>
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

  // Render SVG Sankey
  const sankeyMount = container.querySelector('#sankeyMount');
  renderSankeyDiagram(sankeyMount, data.sankey, data.totalIncome);

  // Window selector buttons
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

  // Expandable table rows
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
