import {
  state,
  apiFetch,
  formatCurrency,
  escapeHtml,
  sortTransactions,
  renderTransactionSortOptions,
  setSortPreference,
  openModal,
  closeModal,
  showToast,
  openCategorizeExpenseModal,
  openCategoryManagerModal
} from '../app.js';

export function sortBudgetCards(categories, sortMode = 'custom_order') {
  const list = Array.isArray(categories) ? [...categories] : [];
  return list.sort((a, b) => {
    const nameA = String(a.name || '');
    const nameB = String(b.name || '');
    switch (sortMode) {
      case 'alpha_asc':
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      case 'alpha_desc':
        return nameB.localeCompare(nameA, undefined, { sensitivity: 'base' });
      case 'recent': {
        const tsA = String(a.lastActivityAt || a.updatedAt || '');
        const tsB = String(b.lastActivityAt || b.updatedAt || '');
        if (tsB !== tsA) return tsB.localeCompare(tsA);
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      }
      case 'pct_used_desc': {
        const diff = Number(b.pctUsed || 0) - Number(a.pctUsed || 0);
        if (diff !== 0) return diff;
        return Number(b.netSpent || 0) - Number(a.netSpent || 0);
      }
      case 'spend_desc': {
        const diff = Number(b.netSpent || 0) - Number(a.netSpent || 0);
        if (diff !== 0) return diff;
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      }
      case 'remaining_asc': {
        const diff = Number(a.remaining || 0) - Number(b.remaining || 0);
        if (diff !== 0) return diff;
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      }
      case 'custom_order':
      default: {
        const orderDiff = Number(a.sortOrder ?? 100) - Number(b.sortOrder ?? 100);
        if (orderDiff !== 0) return orderDiff;
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      }
    }
  });
}

export async function renderBudgetsView(container) {
  const data = await apiFetch(`/api/budgets/${state.selectedYearMonth}`);
  const uncatTxs = sortTransactions(data.uncategorizedTransactions || [], state.budgetsTransactionSort);
  const sortedCategories = sortBudgetCards(data.categories, state.budgetsCategorySort);

  const sourceBadge = data.incomeSourceType === 'ActualGoogleDeposits'
    ? 'Google Bi-Weekly Direct Deposits'
    : data.incomeSourceType === 'MonthlyOverride'
      ? 'Monthly Manual Override'
      : 'Persistent Monthly Fallback';

  container.innerHTML = `
    <div class="page-header">
      <div>
        <h1 class="page-title">Monthly Budgets</h1>
        <p class="page-subtitle">Fixed calendar-month targets (no rollover) &bull; Income source: <strong>${escapeHtml(sourceBadge)}</strong></p>
      </div>
      <div style="display:flex;flex-wrap:wrap;align-items:center;gap:8px;">
        <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
          Sort Categories:
          <select id="budgetsSortSelect" class="select-control" style="font-size:12px;padding:4px 8px;">
            <option value="custom_order" ${state.budgetsCategorySort === 'custom_order' ? 'selected' : ''}>Custom Order</option>
            <option value="alpha_asc" ${state.budgetsCategorySort === 'alpha_asc' ? 'selected' : ''}>Alphabetical (A-Z)</option>
            <option value="alpha_desc" ${state.budgetsCategorySort === 'alpha_desc' ? 'selected' : ''}>Alphabetical (Z-A)</option>
            <option value="recent" ${state.budgetsCategorySort === 'recent' ? 'selected' : ''}>Recent Changes</option>
            <option value="pct_used_desc" ${state.budgetsCategorySort === 'pct_used_desc' ? 'selected' : ''}>% Budget Used (High-Low)</option>
            <option value="spend_desc" ${state.budgetsCategorySort === 'spend_desc' ? 'selected' : ''}>Highest Spend ($)</option>
            <option value="remaining_asc" ${state.budgetsCategorySort === 'remaining_asc' ? 'selected' : ''}>Remaining Budget (Low-High)</option>
          </select>
        </label>
        <label style="font-size:12px;font-weight:700;color:#475569;display:inline-flex;align-items:center;gap:6px;">
          Sort Transactions:
          <select id="budgetsTxSortSelect" class="select-control" style="font-size:12px;padding:4px 8px;">
            ${renderTransactionSortOptions(state.budgetsTransactionSort)}
          </select>
        </label>
        <button class="btn btn-primary btn-sm" id="budgetsAddCatBtn">+ Add Category</button>
        <button class="btn btn-outline btn-sm" id="prevMonthBtn">&larr;</button>
        <input type="month" id="budgetMonthPicker" class="input-control" value="${escapeHtml(data.yearMonth)}" />
        <button class="btn btn-outline btn-sm" id="nextMonthBtn">&rarr;</button>
      </div>
    </div>

    <div class="kpi-grid">
      <div class="kpi-card">
        <div class="kpi-label">Month Income</div>
        <div class="kpi-value">${formatCurrency(data.resolvedIncome)}</div>
        <div class="kpi-sub">${escapeHtml(sourceBadge)}</div>
      </div>
      <div class="kpi-card">
        <div class="kpi-label">Total Budgeted</div>
        <div class="kpi-value">${formatCurrency(data.totalBudgeted)}</div>
        <div class="kpi-sub">Across ${data.categories.length} categories</div>
      </div>
      <div class="kpi-card">
        <div class="kpi-label">Net Spent (${escapeHtml(data.yearMonth)})</div>
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
        <div style="display:flex;flex-wrap:wrap;justify-content:space-between;align-items:center;gap:8px;margin-bottom:10px;">
          <div>
            <strong style="color:#B45309;">&#9888;&#65039; ${uncatTxs.length} Uncategorized Expense(s) in ${escapeHtml(data.yearMonth)}</strong>
            <div style="font-size:12px;color:#92400E;">Categorize below (prompts to apply to similar future/past expenses)</div>
          </div>
          <label style="font-size:11px;font-weight:700;color:#92400E;display:inline-flex;align-items:center;gap:6px;">
            Sort Transactions:
            <select class="select-control budget-drill-tx-sort" style="font-size:11px;padding:3px 6px;">
              ${renderTransactionSortOptions(state.budgetsTransactionSort)}
            </select>
          </label>
        </div>
        <div style="display:flex;flex-direction:column;gap:6px;">
          ${uncatTxs.map(t => `
            <div style="display:flex;justify-content:space-between;align-items:center;background:#FFFFFF;padding:8px 12px;border-radius:8px;border:1px solid #FDE68A;font-size:13px;">
              <div>
                <strong>${escapeHtml(t.cleanMerchantName)}</strong>
                <span style="color:#64748B;font-size:11px;margin-left:6px;">${escapeHtml(t.transactionDate)} &bull; ${escapeHtml(t.accountName)}</span>
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
      ${sortedCategories.map(cat => renderCategoryCardHtml(cat)).join('')}
    </div>

    <div class="payoff-callout">
      <span>&#128179; <strong>Credit Card Payoffs Recorded (${escapeHtml(data.yearMonth)}):</strong> Excluded from spending &amp; budgets</span>
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

  const budgetsSortSelect = container.querySelector('#budgetsSortSelect');
  if (budgetsSortSelect) {
    budgetsSortSelect.onchange = (e) => {
      setSortPreference('budgetsCategorySort', e.target.value);
      renderBudgetsView(container);
    };
  }

  const handleTxSortChange = (e) => {
    setSortPreference('budgetsTransactionSort', e.target.value);
    renderBudgetsView(container);
  };

  const budgetsTxSortSelect = container.querySelector('#budgetsTxSortSelect');
  if (budgetsTxSortSelect) {
    budgetsTxSortSelect.onchange = handleTxSortChange;
  }

  container.querySelectorAll('.budget-drill-tx-sort').forEach(sel => {
    sel.onchange = handleTxSortChange;
  });

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
        <h3 style="margin-top:0;">Set Monthly Budget — ${escapeHtml(cat.name)}</h3>
        <p style="font-size:13px;color:#64748B;">Fixed monthly target for <strong>${escapeHtml(cat.name)}</strong> (triggers automatic SQLite backup):</p>
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
          if (val < 0) {
            alert('Monthly budget cannot be negative.');
            return;
          }
          await apiFetch(`/api/categories/${catId}/budget`, {
            method: 'PUT',
            body: JSON.stringify({ monthlyBudget: val })
          });
          closeModal();
          showToast(`Updated ${cat.name} budget to ${formatCurrency(val)}`);
          renderBudgetsView(container);
        };
      });
    };
  });

  container.querySelectorAll('[data-toggle-drill]').forEach(btn => {
    btn.onclick = () => {
      const catId = Number(btn.dataset.toggleDrill);
      const listEl = container.querySelector(`#drill_${catId}`);
      if (listEl) {
        const willExpand = listEl.style.display === 'none';
        listEl.style.display = willExpand ? 'flex' : 'none';
        if (willExpand && !state.expandedBudgetCategories.includes(catId)) {
          state.expandedBudgetCategories.push(catId);
        } else if (!willExpand) {
          state.expandedBudgetCategories = state.expandedBudgetCategories.filter(id => id !== catId);
        }
        const countMatch = btn.textContent.match(/\d+\s+Transactions?/);
        const countLabel = countMatch ? countMatch[0] : 'Transactions';
        btn.innerHTML = `${countLabel} ${willExpand ? '&#9652;' : '&#9662;'}`;
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
  const isExpanded = state.expandedBudgetCategories.includes(cat.categoryId);
  const sortedCatTxs = sortTransactions(cat.transactions, state.budgetsTransactionSort);

  return `
    <div class="budget-card">
      <div class="budget-card-header">
        <div class="cat-badge">
          <span class="cat-dot" style="background:${escapeHtml(cat.colorHex)}"></span>
          <span>${escapeHtml(cat.name)}</span>
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
        <div class="progress-fill ${escapeHtml(cat.statusLevel)}" style="width:${pctWidth}%"></div>
      </div>

      <div class="budget-metrics-row">
        <span>Spent: <strong>${formatCurrency(cat.netSpent)}</strong> (${cat.pctUsed}%)</span>
        <span class="${remClass}">${remLabel}</span>
      </div>

      <button class="drilldown-toggle" data-toggle-drill="${cat.categoryId}">
        ${sortedCatTxs.length} Transaction${sortedCatTxs.length === 1 ? '' : 's'} ${isExpanded ? '&#9652;' : '&#9662;'}
      </button>

      <div class="drilldown-list" id="drill_${cat.categoryId}" style="display:${isExpanded ? 'flex' : 'none'};">
        ${sortedCatTxs.length === 0
          ? '<div style="color:#94A3B8;font-size:12px;text-align:center;">No transactions this month</div>'
          : `
            <div style="display:flex;justify-content:space-between;align-items:center;padding-bottom:6px;margin-bottom:4px;border-bottom:1px solid #E2E8F0;">
              <span style="font-size:11px;font-weight:700;color:#64748B;">Sort Drill-down:</span>
              <select class="select-control budget-drill-tx-sort" style="font-size:11px;padding:2px 6px;">
                ${renderTransactionSortOptions(state.budgetsTransactionSort)}
              </select>
            </div>
            ${sortedCatTxs.map(t => `
              <div class="drilldown-item">
                <div>
                  <div style="font-weight:600;">${escapeHtml(t.cleanMerchantName)}</div>
                  <div style="font-size:11px;color:#64748B;">${escapeHtml(t.transactionDate)} &bull; ${escapeHtml(t.accountName)}${t.subcategory ? ` &bull; ${escapeHtml(t.subcategory)}` : ''}</div>
                </div>
                <div style="display:flex;align-items:center;gap:8px;">
                  <span class="${t.amount < 0 ? 'amount-refund' : 'amount-expense'}">
                    ${formatCurrency(t.amount)}
                  </span>
                  <button class="btn btn-outline btn-sm budget-recat-btn" data-tx-id="${t.transactionId}" title="Recategorize">Recat</button>
                </div>
              </div>
            `).join('')}
          `}
      </div>
    </div>
  `;
}
