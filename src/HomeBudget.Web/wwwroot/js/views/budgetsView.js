import {
  state,
  apiFetch,
  formatCurrency,
  openModal,
  closeModal,
  showToast,
  openCategorizeExpenseModal,
  openCategoryManagerModal
} from '../app.js';

export async function renderBudgetsView(container) {
  const data = await apiFetch(`/api/budgets/${state.selectedYearMonth}`);
  const uncatTxs = data.uncategorizedTransactions || [];

  const sourceBadge = data.incomeSourceType === 'ActualGoogleDeposits'
    ? 'Google Bi-Weekly Direct Deposits'
    : data.incomeSourceType === 'MonthlyOverride'
      ? 'Monthly Manual Override'
      : 'Persistent Monthly Fallback';

  container.innerHTML = `
    <div class="page-header">
      <div>
        <h1 class="page-title">Monthly Budgets</h1>
        <p class="page-subtitle">Fixed calendar-month targets (no rollover) &bull; Income source: <strong>${sourceBadge}</strong></p>
      </div>
      <div style="display:flex;flex-wrap:wrap;align-items:center;gap:8px;">
        <button class="btn btn-primary btn-sm" id="budgetsAddCatBtn">+ Add Category</button>
        <button class="btn btn-outline btn-sm" id="prevMonthBtn">&larr;</button>
        <input type="month" id="budgetMonthPicker" class="input-control" value="${data.yearMonth}" />
        <button class="btn btn-outline btn-sm" id="nextMonthBtn">&rarr;</button>
      </div>
    </div>

    <div class="kpi-grid">
      <div class="kpi-card">
        <div class="kpi-label">Month Income</div>
        <div class="kpi-value">${formatCurrency(data.resolvedIncome)}</div>
        <div class="kpi-sub">${sourceBadge}</div>
      </div>
      <div class="kpi-card">
        <div class="kpi-label">Total Budgeted</div>
        <div class="kpi-value">${formatCurrency(data.totalBudgeted)}</div>
        <div class="kpi-sub">Across ${data.categories.length} categories</div>
      </div>
      <div class="kpi-card">
        <div class="kpi-label">Net Spent (${data.yearMonth})</div>
        <div class="kpi-value">${formatCurrency(data.totalNetSpent)}</div>
        <div class="kpi-sub">Refunds automatically netted</div>
      </div>
      <div class="kpi-card">
        <div class="kpi-label">Remaining Budget</div>
        <div class="kpi-value" style="color:${data.totalRemaining >= 0 ? '#059669' : '#DC2626'}">
          ${formatCurrency(data.totalRemaining)}
        </div>
        <div class="kpi-sub">Resets on the 1st of next month</div>
      </div>
    </div>

    ${uncatTxs.length > 0 ? `
      <div class="card-panel" style="border:2px solid #F59E0B;background:#FFFBEB;">
        <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px;">
          <strong style="color:#B45309;">&#9888;&#65039; ${uncatTxs.length} Uncategorized Expense(s) in ${data.yearMonth}</strong>
          <span style="font-size:12px;color:#92400E;">Categorize below (prompts to apply to similar future/past expenses)</span>
        </div>
        <div style="display:flex;flex-direction:column;gap:6px;">
          ${uncatTxs.map(t => `
            <div style="display:flex;justify-content:space-between;align-items:center;background:#FFFFFF;padding:8px 12px;border-radius:8px;border:1px solid #FDE68A;font-size:13px;">
              <div>
                <strong>${t.cleanMerchantName}</strong>
                <span style="color:#64748B;font-size:11px;margin-left:6px;">${t.transactionDate} &bull; ${t.accountName}</span>
              </div>
              <div style="display:flex;align-items:center;gap:10px;">
                <strong>${formatCurrency(t.amount)}</strong>
                <button class="btn btn-primary btn-sm budget-recat-btn" data-tx-id="${t.transactionId}">Categorize...</button>
              </div>
            </div>
          `).join('')}
        </div>
      </div>
    ` : ''}

    <div class="budget-cards-grid" id="budgetCardsGrid">
      ${data.categories.map(cat => renderCategoryCardHtml(cat)).join('')}
    </div>

    <div class="payoff-callout">
      <span>&#128179; <strong>Credit Card Payoffs Recorded (${data.yearMonth}):</strong> Excluded from spending &amp; budgets</span>
      <strong>${formatCurrency(data.creditCardPayoffsTotal)}</strong>
    </div>
  `;

  const shiftMonth = (delta) => {
    const [y, m] = state.selectedYearMonth.split('-').map(Number);
    const dt = new Date(y, m - 1 + delta, 1);
    const nextYm = `${dt.getFullYear()}-${String(dt.getMonth() + 1).padStart(2, '0')}`;
    state.selectedYearMonth = nextYm;
    renderBudgetsView(container);
  };

  container.querySelector('#prevMonthBtn').onclick = () => shiftMonth(-1);
  container.querySelector('#nextMonthBtn').onclick = () => shiftMonth(1);
  container.querySelector('#budgetMonthPicker').onchange = (e) => {
    if (e.target.value) {
      state.selectedYearMonth = e.target.value;
      renderBudgetsView(container);
    }
  };

  container.querySelector('#budgetsAddCatBtn').onclick = () => {
    openCategoryManagerModal(null, () => renderBudgetsView(container));
  };

  container.querySelectorAll('[data-manage-cat]').forEach(btn => {
    btn.onclick = () => {
      const catId = Number(btn.dataset.manageCat);
      const cat = data.categories.find(c => c.categoryId === catId);
      if (cat) {
        openCategoryManagerModal(cat, () => renderBudgetsView(container));
      }
    };
  });

  container.querySelectorAll('[data-edit-budget]').forEach(btn => {
    btn.onclick = () => {
      const catId = Number(btn.dataset.editBudget);
      const cat = data.categories.find(c => c.categoryId === catId);
      if (!cat) return;

      openModal(`
        <h3 style="margin-top:0;">Set Monthly Budget — ${cat.name}</h3>
        <p style="font-size:13px;color:#64748B;">Fixed monthly target for <strong>${cat.name}</strong> (triggers automatic SQLite backup):</p>
        <div style="margin:16px 0;">
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:6px;">Monthly Budget ($)</label>
          <input type="number" step="10" min="0" inputmode="decimal" id="modalBudgetInput" class="input-control" style="width:100%;font-size:18px;font-weight:700;" value="${cat.monthlyBudget}" />
        </div>
        <div style="display:flex;justify-content:flex-end;gap:10px;">
          <button class="btn btn-outline" id="cancelModalBtn">Cancel</button>
          <button class="btn btn-primary" id="saveBudgetBtn">Save Target</button>
        </div>
      `, (modalEl) => {
        const input = modalEl.querySelector('#modalBudgetInput');
        input.focus();
        input.select();
        modalEl.querySelector('#cancelModalBtn').onclick = closeModal;
        modalEl.querySelector('#saveBudgetBtn').onclick = async () => {
          const val = Number(input.value || 0);
          await apiFetch(`/api/categories/${catId}/budget`, {
            method: 'PUT',
            body: JSON.stringify({ monthlyBudget: val })
          });
          closeModal();
          showToast(`Updated ${cat.name} budget to ${formatCurrency(val)} (Backup created)`);
          renderBudgetsView(container);
        };
      });
    };
  });

  container.querySelectorAll('[data-toggle-drill]').forEach(btn => {
    btn.onclick = () => {
      const catId = btn.dataset.toggleDrill;
      const listEl = container.querySelector(`#drill_${catId}`);
      if (listEl) {
        listEl.style.display = listEl.style.display === 'none' ? 'flex' : 'none';
      }
    };
  });

  const allMonthTxs = [...uncatTxs, ...data.categories.flatMap(c => c.transactions)];
  container.querySelectorAll('.budget-recat-btn').forEach(btn => {
    btn.onclick = () => {
      const txId = Number(btn.dataset.txId);
      const tx = allMonthTxs.find(x => x.transactionId === txId);
      if (tx) {
        openCategorizeExpenseModal(tx, () => renderBudgetsView(container));
      }
    };
  });
}

function renderCategoryCardHtml(cat) {
  const pctWidth = Math.min(100, Math.max(0, Number(cat.pctUsed || 0)));
  const remClass = cat.remaining >= 0 ? 'rem-positive' : 'rem-negative';
  const remLabel = cat.remaining >= 0
    ? `${formatCurrency(cat.remaining)} left`
    : `${formatCurrency(Math.abs(cat.remaining))} over`;

  return `
    <div class="budget-card">
      <div class="budget-card-header">
        <div class="cat-badge">
          <span class="cat-dot" style="background:${cat.colorHex}"></span>
          <span>${cat.name}</span>
          ${cat.isBigBoxMerchantCategory ? '<span class="bigbox-pill">Merchant</span>' : ''}
        </div>
        <div style="display:flex;gap:6px;align-items:center;">
          <button class="budget-target-btn" data-edit-budget="${cat.categoryId}" title="Tap to edit monthly target">
            ${formatCurrency(cat.monthlyBudget)} &#9998;
          </button>
          <button class="btn btn-outline btn-sm" data-manage-cat="${cat.categoryId}" title="Edit or Delete Category">&#9881;</button>
        </div>
      </div>

      <div class="progress-track">
        <div class="progress-fill ${cat.statusLevel}" style="width:${pctWidth}%"></div>
      </div>

      <div class="budget-metrics-row">
        <span>Spent: <strong>${formatCurrency(cat.netSpent)}</strong> (${cat.pctUsed}%)</span>
        <span class="${remClass}">${remLabel}</span>
      </div>

      <button class="drilldown-toggle" data-toggle-drill="${cat.categoryId}">
        ${cat.transactions.length} Transaction${cat.transactions.length === 1 ? '' : 's'} &#9662;
      </button>

      <div class="drilldown-list" id="drill_${cat.categoryId}" style="display:none;">
        ${cat.transactions.length === 0
          ? '<div style="color:#94A3B8;font-size:12px;text-align:center;">No transactions this month</div>'
          : cat.transactions.map(t => `
              <div class="drilldown-item">
                <div>
                  <div style="font-weight:600;">${t.cleanMerchantName}</div>
                  <div style="font-size:11px;color:#64748B;">${t.transactionDate} &bull; ${t.accountName}${t.subcategory ? ` &bull; ${t.subcategory}` : ''}</div>
                </div>
                <div style="display:flex;align-items:center;gap:8px;">
                  <span class="${t.amount < 0 ? 'amount-refund' : 'amount-expense'}">
                    ${formatCurrency(t.amount)}
                  </span>
                  <button class="btn btn-outline btn-sm budget-recat-btn" data-tx-id="${t.transactionId}" title="Recategorize">Recat</button>
                </div>
              </div>
            `).join('')}
      </div>
    </div>
  `;
}
