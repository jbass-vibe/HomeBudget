import { renderBudgetsView } from './views/budgetsView.js';
import { renderCashflowView } from './views/cashflowView.js';
import { renderUploadView } from './views/uploadView.js';
import { renderInspectorView } from './views/inspectorView.js';
import { renderTopologyView } from './views/topologyView.js';
import { renderLoginView } from './views/loginView.js';

const ALLOWED_VIEWS = new Set(['budgets', 'cashflow', 'upload', 'inspector', 'topology']);
const AUTH_SYNC_KEY = 'homebudget:auth-sync';

const SORT_STORAGE_KEYS = {
  categoryDropdownSort: 'hb_cat_dropdown_sort',
  cashflowCategorySort: 'hb_cashflow_cat_sort',
  cashflowTransactionSort: 'hb_cashflow_tx_sort',
  triageSort: 'hb_triage_sort',
  budgetsCategorySort: 'hb_budgets_cat_sort',
  budgetsTransactionSort: 'hb_budgets_tx_sort',
  uploadTransactionSort: 'hb_upload_tx_sort',
  inspectorTransactionSort: 'hb_inspector_tx_sort',
  topologyCategorySort: 'hb_topology_cat_sort',
  topologyRuleSort: 'hb_topology_rule_sort',
  backPropSort: 'hb_backprop_sort',
  backupsSort: 'hb_backups_sort'
};

export function getSavedSort(stateKey, fallback) {
  try {
    const storageKey = SORT_STORAGE_KEYS[stateKey] || stateKey;
    if (typeof localStorage !== 'undefined' && localStorage !== null) {
      return localStorage.getItem(storageKey) || fallback;
    }
  } catch {
    // Ignore storage errors in restricted contexts
  }
  return fallback;
}

export const state = {
  currentUser: null,
  pendingReturnView: null,
  currentView: 'budgets',
  selectedYearMonth: new Date().toISOString().slice(0, 7),
  cashflowWindow: 'mtd',
  cashflowMonths: 3,
  cashflowStart: '',
  cashflowEnd: '',
  includeSubcategories: false,
  triageGroupByMerchant: true,
  categoryDropdownSort: getSavedSort('categoryDropdownSort', 'alpha_asc'),
  cashflowCategorySort: getSavedSort('cashflowCategorySort', 'spend_desc'),
  cashflowTransactionSort: getSavedSort('cashflowTransactionSort', 'date_desc'),
  triageSort: getSavedSort('triageSort', 'amount_desc'),
  budgetsCategorySort: getSavedSort('budgetsCategorySort', 'custom_order'),
  budgetsTransactionSort: getSavedSort('budgetsTransactionSort', 'date_desc'),
  uploadTransactionSort: getSavedSort('uploadTransactionSort', 'status_priority'),
  inspectorTransactionSort: getSavedSort('inspectorTransactionSort', 'date_desc'),
  topologyCategorySort: getSavedSort('topologyCategorySort', 'custom_order'),
  topologyRuleSort: getSavedSort('topologyRuleSort', 'priority_asc'),
  backPropSort: getSavedSort('backPropSort', 'date_desc'),
  backupsSort: getSavedSort('backupsSort', 'date_desc'),
  expandedCashflowCategories: [],
  expandedCashflowMerchants: [],
  expandedBudgetCategories: [],
  accounts: [],
  categories: []
};

export function isAdmin() {
  return state.currentUser?.role === 'Admin';
}

export function isAllowedViewForCurrentUser(viewName) {
  if (!ALLOWED_VIEWS.has(viewName)) return false;
  if (viewName === 'upload' && !isAdmin()) return false;
  return true;
}

export function setSortPreference(stateKey, value) {
  state[stateKey] = value;
  try {
    const storageKey = SORT_STORAGE_KEYS[stateKey] || stateKey;
    if (typeof localStorage !== 'undefined' && localStorage !== null) {
      localStorage.setItem(storageKey, value);
    }
  } catch {
    // Ignore storage errors
  }
}

export function sortCategories(categories = state.categories, sortMode = state.categoryDropdownSort) {
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
        const tsA = String(a.lastActivityAt || a.updatedAt || a.createdAt || '');
        const tsB = String(b.lastActivityAt || b.updatedAt || b.createdAt || '');
        if (tsB !== tsA) return tsB.localeCompare(tsA);
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      }
      case 'budget_desc': {
        const diff = Number(b.monthlyBudget || 0) - Number(a.monthlyBudget || 0);
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

export function sortTransactions(transactions = [], sortMode = 'date_desc') {
  const list = Array.isArray(transactions) ? [...transactions] : [];
  return list.sort((a, b) => {
    const dateA = String(a.transactionDate || '');
    const dateB = String(b.transactionDate || '');
    const idA = Number(a.transactionId || 0);
    const idB = Number(b.transactionId || 0);
    const amtA = Math.abs(Number(a.amount || 0));
    const amtB = Math.abs(Number(b.amount || 0));
    const merchA = String(a.cleanMerchantName || a.rawDescription || '');
    const merchB = String(b.cleanMerchantName || b.rawDescription || '');
    const acctA = String(a.accountName || '');
    const acctB = String(b.accountName || '');

    switch (sortMode) {
      case 'date_asc':
        return dateA.localeCompare(dateB) || (idA - idB);
      case 'recent':
      case 'recent_changes': {
        const tsA = String(a.updatedAt || a.createdAt || a.transactionDate || '');
        const tsB = String(b.updatedAt || b.createdAt || b.transactionDate || '');
        return tsB.localeCompare(tsA) || (idB - idA) || dateB.localeCompare(dateA);
      }
      case 'amount_desc':
        return (amtB - amtA) || dateB.localeCompare(dateA) || (idB - idA);
      case 'amount_asc':
        return (amtA - amtB) || dateB.localeCompare(dateA) || (idB - idA);
      case 'alpha_asc':
      case 'merchant_asc':
        return merchA.localeCompare(merchB, undefined, { sensitivity: 'base' }) || dateB.localeCompare(dateA);
      case 'alpha_desc':
      case 'merchant_desc':
        return merchB.localeCompare(merchA, undefined, { sensitivity: 'base' }) || dateB.localeCompare(dateA);
      case 'account_asc':
        return acctA.localeCompare(acctB, undefined, { sensitivity: 'base' }) || dateB.localeCompare(dateA);
      case 'date_desc':
      default:
        return dateB.localeCompare(dateA) || (idB - idA);
    }
  });
}

export function renderCategoryDropdownSortOptions(selectedMode = state.categoryDropdownSort) {
  return `
    <option value="alpha_asc" ${selectedMode === 'alpha_asc' ? 'selected' : ''}>Alphabetical (A-Z)</option>
    <option value="alpha_desc" ${selectedMode === 'alpha_desc' ? 'selected' : ''}>Alphabetical (Z-A)</option>
    <option value="recent" ${selectedMode === 'recent' ? 'selected' : ''}>Recent Changes</option>
    <option value="custom_order" ${selectedMode === 'custom_order' ? 'selected' : ''}>Custom Order</option>
    <option value="budget_desc" ${selectedMode === 'budget_desc' ? 'selected' : ''}>Highest Budget ($)</option>
  `;
}

export function renderTransactionSortOptions(selectedMode = 'date_desc') {
  const norm = selectedMode === 'recent_changes' ? 'recent'
    : selectedMode === 'merchant_asc' ? 'alpha_asc'
      : selectedMode === 'merchant_desc' ? 'alpha_desc'
        : selectedMode;
  return `
    <option value="date_desc" ${norm === 'date_desc' ? 'selected' : ''}>Newest Date First</option>
    <option value="date_asc" ${norm === 'date_asc' ? 'selected' : ''}>Oldest Date First</option>
    <option value="recent" ${norm === 'recent' ? 'selected' : ''}>Recent Changes</option>
    <option value="amount_desc" ${norm === 'amount_desc' ? 'selected' : ''}>Highest Amount ($)</option>
    <option value="amount_asc" ${norm === 'amount_asc' ? 'selected' : ''}>Lowest Amount ($)</option>
    <option value="alpha_asc" ${norm === 'alpha_asc' ? 'selected' : ''}>Merchant (A-Z)</option>
    <option value="alpha_desc" ${norm === 'alpha_desc' ? 'selected' : ''}>Merchant (Z-A)</option>
    <option value="account_asc" ${norm === 'account_asc' ? 'selected' : ''}>Account (A-Z)</option>
  `;
}

export function escapeHtml(val) {
  if (val === null || val === undefined) return '';
  return String(val)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export function formatBackupStatus(backupFileCreated) {
  if (backupFileCreated === 'scheduled') return 'Backup scheduled (coalesced)';
  if (backupFileCreated === 'skipped_policy') return 'Auto-backup skipped (policy)';
  return backupFileCreated ? 'DB backed up' : 'Backup skipped/failed';
}

export async function apiFetch(url, options = {}) {
  const { headers: customHeaders, ...restOptions } = options;
  const headers = {
    'X-HomeBudget-Request': '1',
    ...(restOptions.body && !(restOptions.body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}),
    ...(customHeaders || {})
  };

  const res = await fetch(url, {
    credentials: 'same-origin',
    ...restOptions,
    headers
  });

  const rawText = await res.text();

  if (res.status === 401 && !url.startsWith('/api/auth/login') && !url.startsWith('/api/auth/setup') && !url.startsWith('/api/auth/status')) {
    if (state.currentUser && isAllowedViewForCurrentUser(state.currentView)) {
      state.pendingReturnView = state.currentView;
    }
    state.currentUser = null;
    closeModal();
    initAuth();
  }

  if (!res.ok) {
    let errText = res.statusText;
    if (rawText) {
      try {
        const errJson = JSON.parse(rawText);
        errText = errJson.error || errJson.detail || errJson.title || rawText;
      } catch {
        errText = rawText;
      }
    }
    throw new Error(errText || `HTTP ${res.status}`);
  }

  if (res.status === 204 || !rawText) return null;
  return JSON.parse(rawText);
}

export function formatCurrency(val) {
  const num = Number(val || 0);
  return num.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
}

export function showToast(message) {
  const container = document.getElementById('toastContainer');
  const toast = document.createElement('div');
  toast.className = 'toast';
  toast.textContent = message;
  container.appendChild(toast);
  setTimeout(() => toast.remove(), 4000);
}

export function openModal(htmlContent, onMount) {
  const backdrop = document.getElementById('modalBackdrop');
  const content = document.getElementById('modalContent');
  content.innerHTML = htmlContent;
  backdrop.classList.remove('hidden');

  backdrop.onclick = (e) => {
    if (e.target === backdrop) closeModal();
  };
  if (onMount) onMount(content);
}

export function closeModal() {
  const backdrop = document.getElementById('modalBackdrop');
  backdrop.classList.add('hidden');
  document.getElementById('modalContent').innerHTML = '';
}

export async function refreshReferenceData() {
  const [accounts, categories] = await Promise.all([
    apiFetch('/api/accounts'),
    apiFetch(`/api/categories?sortBy=${encodeURIComponent(state.categoryDropdownSort || 'custom_order')}`)
  ]);
  state.accounts = accounts;
  state.categories = categories;
}

/**
 * Unified Categorization & Recategorization Modal (Requirement 1 & 3)
 * Prompts the user with non-mutually exclusive options:
 *   a) Categorize similar expenses moving forward (creates/updates rule)
 *   b) Recategorize similar expenses in the past
 * Also allows creating a new category inline.
 */
export function openCategorizeExpenseModal(tx, onSuccess, preselectedCatId = undefined) {
  const sortedCats = sortCategories(state.categories, state.categoryDropdownSort);
  const initialCatId = preselectedCatId !== undefined ? preselectedCatId : (tx.categoryId || sortedCats[0]?.categoryId || '');
  const startInNewCatMode = preselectedCatId === '__NEW__';
  const defaultPattern = tx.cleanMerchantName || tx.rawDescription || '';

  const buildCatSelectOptions = (selectedVal, sortMode) => {
    const ordered = sortCategories(state.categories, sortMode);
    return `
      <option value="">-- Uncategorized --</option>
      ${ordered.map(c => `
        <option value="${c.categoryId}" ${String(c.categoryId) === String(selectedVal) ? 'selected' : ''}>
          ${escapeHtml(c.name)} (Budget: ${formatCurrency(c.monthlyBudget)})
        </option>
      `).join('')}
    `;
  };

  openModal(`
    <h3 style="margin-top:0;">Categorize Expense</h3>
    <div style="background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;padding:10px 14px;margin-bottom:14px;font-size:13px;">
      <div style="display:flex;justify-content:space-between;align-items:center;">
        <strong>${escapeHtml(tx.cleanMerchantName || tx.rawDescription)}</strong>
        <strong class="${tx.amount < 0 ? 'amount-refund' : 'amount-expense'}">${formatCurrency(tx.amount)}</strong>
      </div>
      <div style="font-size:11px;color:#64748B;margin-top:3px;">
        ${escapeHtml(tx.transactionDate)} &bull; ${escapeHtml(tx.accountName || '')} &bull; Raw: <code>${escapeHtml(tx.rawDescription)}</code>
      </div>
    </div>

    <div style="display:flex;flex-direction:column;gap:12px;">
      <div>
        <div style="display:flex;flex-wrap:wrap;justify-content:space-between;align-items:center;gap:8px;margin-bottom:6px;">
          <label style="font-size:12px;font-weight:700;">1. Select Category</label>
          <div style="display:flex;align-items:center;gap:6px;">
            <label style="font-size:11px;color:#64748B;font-weight:600;">Sort List:
              <select id="catModalSortSelect" class="select-control" style="padding:3px 6px;font-size:11px;">
                ${renderCategoryDropdownSortOptions(state.categoryDropdownSort)}
              </select>
            </label>
            <button type="button" class="btn btn-outline btn-sm" id="toggleNewCatInlineBtn">
              ${startInNewCatMode ? 'Choose Existing Category' : '+ New Category'}
            </button>
          </div>
        </div>

        <div id="existingCatSelectWrap" style="display:${startInNewCatMode ? 'none' : 'block'};">
          <select id="catModalSelect" class="select-control" style="width:100%;font-size:14px;font-weight:600;">
            ${buildCatSelectOptions(initialCatId, state.categoryDropdownSort)}
          </select>
        </div>

        <div id="newCatInlineWrap" style="display:${startInNewCatMode ? 'flex' : 'none'};flex-direction:column;gap:8px;background:#EFF6FF;border:1px solid #BFDBFE;padding:12px;border-radius:10px;">
          <div style="font-size:12px;font-weight:700;color:#1E40AF;">Create New Category (Triggers automatic DB backup)</div>
          <input type="text" id="inlineNewCatName" class="input-control" placeholder="New Category Name (e.g., Pet Care)" />
          <div style="display:flex;gap:8px;align-items:center;">
            <input type="number" step="10" min="0" id="inlineNewCatBudget" class="input-control" placeholder="Monthly Budget ($)" value="200" style="flex:1;" />
            <input type="color" id="inlineNewCatColor" value="#2563EB" style="width:48px;height:36px;border:none;cursor:pointer;" />
          </div>
        </div>
      </div>

      <div>
        <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Subcategory (Optional)</label>
        <input type="text" id="catModalSubcat" class="input-control" style="width:100%;" placeholder="Optional subcategory..." value="${escapeHtml(tx.subcategory || '')}" />
      </div>

      <div style="background:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;padding:12px;">
        <div style="font-size:12px;font-weight:700;margin-bottom:8px;">
          2. Similar Expenses Automation (Check one, both, or neither)
        </div>
        <div style="margin-bottom:10px;">
          <label style="font-size:11px;font-weight:600;color:#64748B;display:block;margin-bottom:4px;">
            Merchant Match Keyword / Pattern:
          </label>
          <input type="text" id="catModalPattern" class="input-control" style="width:100%;" value="${escapeHtml(defaultPattern)}" />
        </div>

        <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;font-weight:600;margin-bottom:8px;cursor:pointer;">
          <input type="checkbox" id="optMovingForward" checked style="margin-top:3px;" />
          <span>
            <strong>a) Moving Forward:</strong> Automatically categorize similar expenses matching this pattern in future CSV uploads
          </span>
        </label>

        <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;font-weight:600;cursor:pointer;">
          <input type="checkbox" id="optInThePast" checked style="margin-top:3px;" />
          <span>
            <strong>b) In the Past:</strong> Recategorize similar expenses already in the database
            <span id="pastMatchCountBadge" style="font-size:11px;color:#2563EB;font-weight:700;">(checking...)</span>
          </span>
        </label>
      </div>
    </div>

    <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:16px;">
      <button class="btn btn-outline" id="cancelCatModalBtn">Cancel</button>
      <button class="btn btn-primary" id="saveCatModalBtn">Save &amp; Apply</button>
    </div>
  `, (modalEl) => {
    let isCreatingNew = startInNewCatMode;
    const toggleBtn = modalEl.querySelector('#toggleNewCatInlineBtn');
    const existingWrap = modalEl.querySelector('#existingCatSelectWrap');
    const newWrap = modalEl.querySelector('#newCatInlineWrap');
    const catSelectEl = modalEl.querySelector('#catModalSelect');
    const sortSelectEl = modalEl.querySelector('#catModalSortSelect');
    const patternInput = modalEl.querySelector('#catModalPattern');
    const badgeEl = modalEl.querySelector('#pastMatchCountBadge');

    if (sortSelectEl) {
      sortSelectEl.onchange = () => {
        const currentSelected = catSelectEl.value;
        setSortPreference('categoryDropdownSort', sortSelectEl.value);
        catSelectEl.innerHTML = buildCatSelectOptions(currentSelected, state.categoryDropdownSort);
      };
    }

    const updateSimilarCount = async () => {
      const p = patternInput.value.trim();
      if (!p) {
        badgeEl.textContent = '(0 past matches)';
        return;
      }
      try {
        const res = await apiFetch(`/api/transactions/similar-count?pattern=${encodeURIComponent(p)}&excludeTransactionId=${tx.transactionId}`);
        badgeEl.textContent = `(${res.matchingPastCount} similar past expense${res.matchingPastCount === 1 ? '' : 's'} found)`;
      } catch {
        badgeEl.textContent = '';
      }
    };

    updateSimilarCount();
    patternInput.oninput = updateSimilarCount;

    toggleBtn.onclick = () => {
      isCreatingNew = !isCreatingNew;
      existingWrap.style.display = isCreatingNew ? 'none' : 'block';
      newWrap.style.display = isCreatingNew ? 'flex' : 'none';
      toggleBtn.textContent = isCreatingNew ? 'Choose Existing Category' : '+ New Category';
    };

    modalEl.querySelector('#cancelCatModalBtn').onclick = closeModal;
    modalEl.querySelector('#saveCatModalBtn').onclick = async () => {
      const catVal = modalEl.querySelector('#catModalSelect').value;
      const newCatName = modalEl.querySelector('#inlineNewCatName').value.trim();
      const newCatBudget = Number(modalEl.querySelector('#inlineNewCatBudget').value || 0);
      const newCatColor = modalEl.querySelector('#inlineNewCatColor').value;

      if (isCreatingNew && !newCatName) {
        alert('Please enter a name for the new category.');
        return;
      }

      const payload = {
        categoryId: isCreatingNew ? null : (catVal ? Number(catVal) : null),
        newCategoryName: isCreatingNew ? newCatName : null,
        newCategoryMonthlyBudget: isCreatingNew ? newCatBudget : null,
        newCategoryColorHex: isCreatingNew ? newCatColor : null,
        newCategoryIsBigBox: false,
        subcategory: modalEl.querySelector('#catModalSubcat').value.trim() || null,
        cleanMerchantName: tx.cleanMerchantName,
        matchPattern: patternInput.value.trim(),
        applyMovingForward: modalEl.querySelector('#optMovingForward').checked,
        applyToPast: modalEl.querySelector('#optInThePast').checked,
        includeManuallyLockedInPast: true
      };

      const res = await apiFetch(`/api/transactions/${tx.transactionId}/categorize`, {
        method: 'POST',
        body: JSON.stringify(payload)
      });

      await refreshReferenceData();
      closeModal();

      const parts = ['Expense categorized'];
      if (res.createdCategory) parts.push(`created "${res.createdCategory.name}"`);
      if (res.ruleSaved) parts.push('saved forward rule');
      if (res.pastUpdatedCount > 0) parts.push(`updated ${res.pastUpdatedCount} past expense(s)`);
      parts.push(formatBackupStatus(res.backupFileCreated));
      showToast(parts.join(' • '));

      if (onSuccess) await onSuccess(res);
    };
  });
}

/**
 * Reusable Modal for Adding, Editing, or Deleting a Category (Accessible to both User and Admin)
 */
export function openCategoryManagerModal(categoryToEdit = null, onSuccess = null) {
  const isEdit = Boolean(categoryToEdit);
  const otherCategories = sortCategories(
    state.categories.filter(c => !isEdit || c.categoryId !== categoryToEdit.categoryId),
    state.categoryDropdownSort
  );

  openModal(`
    <h3 style="margin-top:0;">${isEdit ? `Edit / Delete Category: ${escapeHtml(categoryToEdit.name)}` : 'Add New Category'}</h3>
    <p style="font-size:12px;color:#64748B;margin-top:0;">All category changes automatically trigger a local SQLite database backup.</p>

    <div style="display:flex;flex-direction:column;gap:12px;margin:14px 0;">
      <div>
        <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Category Name</label>
        <input type="text" id="mgrCatName" class="input-control" style="width:100%;" value="${isEdit ? escapeHtml(categoryToEdit.name) : ''}" placeholder="e.g., Childcare, Home Improvement" />
      </div>
      <div style="display:flex;gap:10px;">
        <div style="flex:1;">
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Monthly Budget ($)</label>
          <input type="number" step="10" min="0" id="mgrCatBudget" class="input-control" style="width:100%;" value="${isEdit ? categoryToEdit.monthlyBudget : 200}" />
        </div>
        <div style="width:100px;">
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Sort Order</label>
          <input type="number" id="mgrCatOrder" class="input-control" style="width:100%;" value="${isEdit ? (categoryToEdit.sortOrder || 100) : 100}" />
        </div>
        <div>
          <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Color</label>
          <input type="color" id="mgrCatColor" value="${isEdit ? escapeHtml(categoryToEdit.colorHex) : '#2563EB'}" style="width:52px;height:36px;border:none;cursor:pointer;" />
        </div>
      </div>
      <label style="display:flex;align-items:center;gap:8px;font-size:13px;font-weight:600;">
        <input type="checkbox" id="mgrCatBigBox" ${isEdit && categoryToEdit.isBigBoxMerchantCategory ? 'checked' : ''} />
        <span>Big-Box Merchant Category (e.g., Costco, Amazon, Target)</span>
      </label>
    </div>

    ${isEdit ? `
      <div style="margin-top:16px;padding-top:14px;border-top:1px solid #FECACA;">
        <div style="font-size:12px;font-weight:700;color:#DC2626;margin-bottom:6px;">Delete This Category</div>
        <div style="display:flex;flex-wrap:wrap;gap:8px;align-items:center;">
          <select id="mgrDeleteReassign" class="select-control" style="flex:1;">
            <option value="">Mark existing expenses in this category as Uncategorized</option>
            ${otherCategories.map(c => `<option value="${c.categoryId}">Move existing expenses to: ${escapeHtml(c.name)}</option>`).join('')}
          </select>
          <button type="button" class="btn btn-danger btn-sm" id="mgrDeleteCatBtn">Delete Category</button>
        </div>
      </div>
    ` : ''}

    <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:18px;">
      <button class="btn btn-outline" id="mgrCancelBtn">Cancel</button>
      <button class="btn btn-primary" id="mgrSaveBtn">${isEdit ? 'Save Changes' : 'Create Category'}</button>
    </div>
  `, (modalEl) => {
    modalEl.querySelector('#mgrCancelBtn').onclick = closeModal;

    modalEl.querySelector('#mgrSaveBtn').onclick = async () => {
      const name = modalEl.querySelector('#mgrCatName').value.trim();
      if (!name) {
        alert('Category name is required.');
        return;
      }
      const monthlyBudget = Number(modalEl.querySelector('#mgrCatBudget').value || 0);
      if (monthlyBudget < 0) {
        alert('Monthly budget cannot be negative.');
        return;
      }
      const payload = {
        name,
        monthlyBudget,
        sortOrder: Number(modalEl.querySelector('#mgrCatOrder').value || 100),
        colorHex: modalEl.querySelector('#mgrCatColor').value,
        isBigBoxMerchantCategory: modalEl.querySelector('#mgrCatBigBox').checked
      };

      if (isEdit) {
        await apiFetch(`/api/categories/${categoryToEdit.categoryId}`, {
          method: 'PUT',
          body: JSON.stringify(payload)
        });
        showToast(`Updated category "${name}"`);
      } else {
        await apiFetch('/api/categories', {
          method: 'POST',
          body: JSON.stringify(payload)
        });
        showToast(`Created category "${name}"`);
      }

      await refreshReferenceData();
      closeModal();
      if (onSuccess) await onSuccess();
    };

    const delBtn = modalEl.querySelector('#mgrDeleteCatBtn');
    if (delBtn) {
      delBtn.onclick = async () => {
        const repVal = modalEl.querySelector('#mgrDeleteReassign').value;
        const msg = repVal
          ? `Delete "${categoryToEdit.name}" and move its expenses to the selected category?`
          : `Delete "${categoryToEdit.name}" and mark its existing expenses as Uncategorized?`;
        if (!confirm(msg)) return;

        const q = repVal ? `?replacementCategoryId=${encodeURIComponent(repVal)}` : '';
        const res = await apiFetch(`/api/categories/${categoryToEdit.categoryId}${q}`, {
          method: 'DELETE'
        });

        await refreshReferenceData();
        closeModal();
        showToast(`Deleted "${categoryToEdit.name}" (${res.affectedTransactionsCount} expenses updated • ${formatBackupStatus(res.backupFileCreated)})`);
        if (onSuccess) await onSuccess();
      };
    }
  });
}

/**
 * Reusable Modal for Adding, Renaming, or Editing a Source Account / Card
 */
export function openAccountModal(accountToEdit = null, onSuccess = null) {
  const isEdit = Boolean(accountToEdit);
  const currentName = accountToEdit?.accountName || '';
  const currentInst = accountToEdit?.institution || 'Chase';
  const currentType = accountToEdit?.accountType || 'CreditCard';

  openModal(`
    <h3 style="margin-top:0;">${isEdit ? `Rename / Edit Account: ${escapeHtml(currentName)}` : 'Add Credit Card or Bank Account'}</h3>
    <p style="font-size:12px;color:#64748B;margin-top:0;">Account changes update display names across visualizations, inspector, and budgets automatically.</p>

    <div style="display:flex;flex-direction:column;gap:12px;margin:16px 0;">
      <div>
        <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Account Display Name</label>
        <input type="text" id="acctModalName" class="input-control" style="width:100%;" value="${escapeHtml(currentName)}" placeholder="e.g., Amex Gold Card, Citi Double Cash, Chase Sapphire" />
      </div>
      <div>
        <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Institution CSV Format</label>
        <select id="acctModalInst" class="select-control" style="width:100%;">
          <option value="Chase" ${currentInst === 'Chase' ? 'selected' : ''}>Chase (7-Column Credit Card CSV)</option>
          <option value="Amex" ${currentInst === 'Amex' || currentInst === 'AmericanExpress' ? 'selected' : ''}>American Express (Date, Description, Amount Credit Card CSV)</option>
          <option value="Citibank" ${currentInst === 'Citibank' || currentInst === 'Citi' ? 'selected' : ''}>Citibank (Status, Date, Description, Debit, Credit CSV)</option>
          <option value="WellsFargo" ${currentInst === 'WellsFargo' ? 'selected' : ''}>Wells Fargo (5-Column Headerless Checking CSV)</option>
        </select>
      </div>
      <div>
        <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Account Type</label>
        <select id="acctModalType" class="select-control" style="width:100%;">
          <option value="CreditCard" ${currentType === 'CreditCard' ? 'selected' : ''}>Credit Card</option>
          <option value="Checking" ${currentType === 'Checking' ? 'selected' : ''}>Checking Account</option>
          <option value="Savings" ${currentType === 'Savings' ? 'selected' : ''}>Savings Account</option>
        </select>
      </div>
    </div>
    <div style="display:flex;justify-content:flex-end;gap:10px;">
      <button class="btn btn-outline" id="acctModalCancelBtn">Cancel</button>
      <button class="btn btn-primary" id="acctModalSaveBtn">${isEdit ? 'Save Changes' : 'Create Account'}</button>
    </div>
  `, (modalEl) => {
    const instSel = modalEl.querySelector('#acctModalInst');
    const typeSel = modalEl.querySelector('#acctModalType');
    if (instSel && typeSel && !isEdit) {
      instSel.onchange = () => {
        typeSel.value = instSel.value === 'WellsFargo' ? 'Checking' : 'CreditCard';
      };
    }

    modalEl.querySelector('#acctModalCancelBtn').onclick = closeModal;
    modalEl.querySelector('#acctModalSaveBtn').onclick = async () => {
      const accountName = modalEl.querySelector('#acctModalName').value.trim();
      const institution = modalEl.querySelector('#acctModalInst').value;
      const accountType = modalEl.querySelector('#acctModalType').value;
      if (!accountName) {
        alert('Account name is required.');
        return;
      }

      if (isEdit) {
        const res = await apiFetch(`/api/accounts/${accountToEdit.accountId}`, {
          method: 'PUT',
          body: JSON.stringify({ accountName, institution, accountType })
        });
        showToast(`Renamed account to "${accountName}" (${formatBackupStatus(res.backupFileCreated)})`);
      } else {
        const res = await apiFetch('/api/accounts', {
          method: 'POST',
          body: JSON.stringify({ accountName, institution, accountType })
        });
        showToast(`Created account "${accountName}" (${formatBackupStatus(res.backupFileCreated)})`);
      }

      await refreshReferenceData();
      closeModal();
      if (onSuccess) await onSuccess();
    };
  });
}

/**
 * Clear Transaction History Modal (v2.4)
 * Offers pre-clear database backup option and tombstone/staging cleanup options
 * while preserving Categories, Rules, Accounts, Monthly Budgets, and Settings.
 */
export function openClearTransactionHistoryModal(onSuccess) {
  openModal(`
    <h3 style="margin-top:0;color:#DC2626;">Clear Transaction History</h3>
    <div style="background:#FEF2F2;border:1px solid #FECACA;border-radius:10px;padding:12px;margin-bottom:14px;font-size:13px;color:#991B1B;">
      <strong>Design Iteration Reset:</strong> Clears imported transactions while preserving all of your <strong>Categories</strong>, <strong>Auto-Categorization Rules</strong>, <strong>Accounts</strong>, <strong>Monthly Budgets</strong>, and <strong>Income Settings</strong>.
    </div>

    <div style="display:flex;flex-direction:column;gap:12px;margin:14px 0;">
      <div>
        <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Scope to Clear</label>
        <select id="clearHistoryScopeSelect" class="select-control" style="width:100%;">
          <option value="">All Accounts (Clear Entire Transaction History)</option>
          ${state.accounts.map(a => `<option value="${a.accountId}">Only Account: ${escapeHtml(a.accountName)} (${escapeHtml(a.institution)})</option>`).join('')}
        </select>
      </div>

      <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;background:#EFF6FF;border:1px solid #BFDBFE;border-radius:10px;padding:10px 12px;cursor:pointer;">
        <input type="checkbox" id="clearHistoryBackupCheck" checked style="margin-top:3px;" />
        <div>
          <strong style="color:#1E40AF;">Back up database before clearing (<code>pre_clear_history</code> snapshot)</strong>
          <div style="font-size:11px;color:#475569;margin-top:2px;">
            Creates a verified SQLite snapshot first so you can restore your current transaction history at any time.
          </div>
        </div>
      </label>

      <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;cursor:pointer;">
        <input type="checkbox" id="clearHistoryTombstonesCheck" checked style="margin-top:3px;" />
        <div>
          <strong>Also reset deleted transaction tombstones (<code>DeletedTransactionHashes</code>)</strong>
          <div style="font-size:11px;color:#64748B;">Allows re-importing the same CSV statements without skipping previously deleted rows.</div>
        </div>
      </label>

      <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;cursor:pointer;">
        <input type="checkbox" id="clearHistoryStagingCheck" checked style="margin-top:3px;" />
        <div>
          <strong>Also clear uncommitted CSV upload staging sessions</strong>
        </div>
      </label>
    </div>

    <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:16px;">
      <button class="btn btn-outline" id="cancelClearHistoryBtn">Cancel</button>
      <button class="btn btn-danger" id="confirmClearHistoryBtn">Backup &amp; Clear Transaction History</button>
    </div>
  `, (m) => {
    const backupCheck = m.querySelector('#clearHistoryBackupCheck');
    const confirmBtn = m.querySelector('#confirmClearHistoryBtn');

    backupCheck.onchange = () => {
      confirmBtn.textContent = backupCheck.checked
        ? 'Backup & Clear Transaction History'
        : 'Clear Transaction History (No Backup)';
    };

    m.querySelector('#cancelClearHistoryBtn').onclick = closeModal;
    confirmBtn.onclick = async () => {
      const scopeVal = m.querySelector('#clearHistoryScopeSelect').value;
      const createBackupFirst = backupCheck.checked;
      const clearDeletedTombstones = m.querySelector('#clearHistoryTombstonesCheck').checked;
      const clearStagingSessions = m.querySelector('#clearHistoryStagingCheck').checked;

      confirmBtn.disabled = true;
      confirmBtn.textContent = 'Clearing...';

      try {
        const res = await apiFetch('/api/transactions/clear-history', {
          method: 'POST',
          body: JSON.stringify({
            createBackupFirst,
            clearDeletedTombstones,
            clearStagingSessions,
            accountId: scopeVal ? Number(scopeVal) : null
          })
        });
        closeModal();
        const backupMsg = res.preClearBackupFile
          ? `Backup saved: ${res.preClearBackupFile}`
          : 'No pre-clear backup requested';
        showToast(`Cleared ${res.deletedTransactionsCount} transaction(s) • ${backupMsg}`);
        if (onSuccess) await onSuccess(res);
      } catch (err) {
        confirmBtn.disabled = false;
        alert(`Failed to clear transaction history: ${err.message}`);
      }
    };
  });
}

/**
 * Reset All Information Modal — Start from Clean History (v2.5)
 * Resets all live database tables while strictly preserving all SQLite backup databases in Backups/.
 */
export function openResetAllDatabaseModal(onSuccess) {
  openModal(`
    <h3 style="margin-top:0;color:#DC2626;">&#9888;&#65039; Reset All Information (Start from Clean History)</h3>
    <div style="background:#FEF2F2;border:1px solid #FECACA;border-radius:10px;padding:12px;margin-bottom:12px;font-size:13px;color:#991B1B;">
      <strong>Complete Live Database Reset:</strong> Clears all <strong>Transactions</strong>, <strong>Deleted Transaction Tombstones</strong>, <strong>Staging Sessions</strong>, <strong>Income Overrides</strong>, <strong>Categories</strong>, and <strong>Auto-Categorization Rules</strong> so you can start from a clean history.
    </div>
    <div style="background:#ECFDF5;border:1px solid #A7F3D0;border-radius:10px;padding:10px 12px;margin-bottom:14px;font-size:12px;color:#065F46;">
      &#128737;&#65039; <strong>Backup Databases Preserved:</strong> Existing SQLite backup snapshots in <code>Backups/</code> are <strong>never</strong> deleted by this reset. You can restore any backup snapshot at any time.
    </div>

    <div style="display:flex;flex-direction:column;gap:12px;margin:14px 0;">
      <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;background:#EFF6FF;border:1px solid #BFDBFE;border-radius:10px;padding:10px 12px;cursor:pointer;">
        <input type="checkbox" id="resetAllBackupCheck" checked style="margin-top:3px;" />
        <div>
          <strong style="color:#1E40AF;">Back up current database before resetting (<code>pre_reset_all</code> safety snapshot)</strong>
          <div style="font-size:11px;color:#475569;margin-top:2px;">
            Saves a verified SQLite snapshot of your current state into <code>Backups/</code> before resetting.
          </div>
        </div>
      </label>

      <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;cursor:pointer;">
        <input type="checkbox" id="resetAllReseedCheck" checked style="margin-top:3px;" />
        <div>
          <strong>Re-seed default starter Categories (12) &amp; Auto-Categorization Rules (15)</strong>
          <div style="font-size:11px;color:#64748B;">Uncheck if you want to start with zero categories and zero rules. Default household accounts and settings are always initialized.</div>
        </div>
      </label>

      <div style="margin-top:4px;">
        <label for="resetAllConfirmPassword" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;color:#991B1B;">
          Admin Passphrase Confirmation (Required)
        </label>
        <input type="password" id="resetAllConfirmPassword" class="input-control" style="width:100%;" autocomplete="current-password" placeholder="Enter your admin passphrase to authorize reset" />
      </div>

      <label style="display:flex;align-items:flex-start;gap:8px;font-size:13px;background:#FFFBEB;border:1px solid #FDE68A;border-radius:10px;padding:10px 12px;cursor:pointer;">
        <input type="checkbox" id="resetAllConfirmCheck" style="margin-top:3px;" />
        <div>
          <strong style="color:#92400E;">I confirm I want to reset all live database information and start from a clean history (backup databases will NOT be deleted)</strong>
        </div>
      </label>
    </div>

    <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:16px;">
      <button class="btn btn-outline" id="cancelResetAllBtn">Cancel</button>
      <button class="btn btn-danger" id="confirmResetAllBtn" disabled>Backup &amp; Reset All Information</button>
    </div>
  `, (m) => {
    const backupCheck = m.querySelector('#resetAllBackupCheck');
    const reseedCheck = m.querySelector('#resetAllReseedCheck');
    const confirmCheck = m.querySelector('#resetAllConfirmCheck');
    const confirmPassInput = m.querySelector('#resetAllConfirmPassword');
    const confirmBtn = m.querySelector('#confirmResetAllBtn');

    function syncResetButtonState() {
      const hasPass = Boolean(confirmPassInput.value);
      confirmBtn.disabled = !(confirmCheck.checked && hasPass);
      confirmBtn.textContent = backupCheck.checked
        ? 'Backup & Reset All Information'
        : 'Reset All Information (Keep Existing Backups)';
    }

    backupCheck.onchange = syncResetButtonState;
    confirmCheck.onchange = syncResetButtonState;
    confirmPassInput.oninput = syncResetButtonState;

    m.querySelector('#cancelResetAllBtn').onclick = closeModal;
    confirmBtn.onclick = async () => {
      if (!confirmCheck.checked || !confirmPassInput.value) return;

      const createBackupFirst = backupCheck.checked;
      const reseedDefaultCategoriesAndRules = reseedCheck.checked;
      const confirmPassword = confirmPassInput.value;

      confirmBtn.disabled = true;
      confirmBtn.textContent = 'Resetting...';

      try {
        const res = await apiFetch('/api/admin/reset-all', {
          method: 'POST',
          body: JSON.stringify({
            createBackupFirst,
            reseedDefaultCategoriesAndRules,
            confirmPassword
          })
        });
        await refreshReferenceData();
        closeModal();
        const backupMsg = res.preResetBackupFile
          ? `Safety backup: ${res.preResetBackupFile}`
          : `${res.retainedBackupFilesCount} backup(s) preserved`;
        showToast(`Reset complete (cleared ${res.deletedTransactionsCount} tx, ${res.deletedCategoriesCount} categories, ${res.deletedRulesCount} rules • ${backupMsg})`);
        if (onSuccess) await onSuccess(res);
      } catch (err) {
        confirmBtn.disabled = false;
        syncResetButtonState();
        alert(`Failed to reset database: ${err.message}`);
      }
    };
  });
}


/**
 * Unified Database Restore Modal (v2.4)
 * Supports restoring either from a local snapshot in Backups/ or by uploading an external .db file,
 * with an option to create an automatic pre_restore safety snapshot first.
 */
export function openRestoreDatabaseModal(backups = [], preselectedFileName = '', onSuccess = null) {
  const hasLocalBackups = Array.isArray(backups) && backups.length > 0;
  const initialMode = preselectedFileName || hasLocalBackups ? 'local' : 'upload';
  const selectedFile = preselectedFileName || (hasLocalBackups ? backups[0].fileName : '');

  openModal(`
    <h3 style="margin-top:0;color:#B45309;">Restore SQLite Database</h3>
    <div style="background:#FFFBEB;border:1px solid #FDE68A;border-radius:10px;padding:12px;margin:12px 0;font-size:13px;color:#92400E;">
      <strong>Warning:</strong> Restoring a snapshot will replace the live database with the selected snapshot after verifying <code>PRAGMA integrity_check</code>.
    </div>

    <div style="display:flex;gap:8px;margin-bottom:12px;">
      <button type="button" class="pill-btn ${initialMode === 'local' ? 'active' : ''}" id="restoreModeLocalBtn">
        From Local Snapshot (${backups.length})
      </button>
      <button type="button" class="pill-btn ${initialMode === 'upload' ? 'active' : ''}" id="restoreModeUploadBtn">
        Upload External .db File
      </button>
    </div>

    <div id="restoreLocalWrap" style="display:${initialMode === 'local' ? 'block' : 'none'};margin-bottom:14px;">
      <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Select Snapshot from Backups Directory</label>
      ${hasLocalBackups ? `
        <select id="restoreSnapshotSelect" class="select-control" style="width:100%;">
          ${backups.map(b => `
            <option value="${escapeHtml(b.fileName)}" ${b.fileName === selectedFile ? 'selected' : ''}>
              ${escapeHtml(b.fileName)} (${escapeHtml(b.reasonLabel || 'Snapshot')} • ${(Number(b.sizeBytes || 0) / 1024).toFixed(1)} KB)
            </option>
          `).join('')}
        </select>
      ` : `
        <div style="padding:10px;background:#F8FAFC;border:1px solid #E2E8F0;border-radius:8px;font-size:12px;color:#64748B;">
          No local snapshots found in Backups directory. Switch to "Upload External .db File" to restore from a file.
        </div>
      `}
    </div>

    <div id="restoreUploadWrap" style="display:${initialMode === 'upload' ? 'block' : 'none'};margin-bottom:14px;">
      <label style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Select SQLite Snapshot File (.db)</label>
      <input type="file" id="restoreUploadFileInput" accept=".db,.sqlite,.sqlite3" class="input-control" style="width:100%;" />
    </div>

    <label style="display:flex;align-items:center;gap:8px;font-size:13px;font-weight:600;margin:14px 0;cursor:pointer;">
      <input type="checkbox" id="preRestoreSafetyCheck" checked />
      <span>Create an automatic <code>pre_restore</code> safety backup of the current live database before restoring (Recommended)</span>
    </label>

    <div style="margin:12px 0;">
      <label for="restoreConfirmPassword" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;color:#92400E;">
        Admin Passphrase Confirmation (Required)
      </label>
      <input type="password" id="restoreConfirmPassword" class="input-control" style="width:100%;" autocomplete="current-password" placeholder="Enter your admin passphrase to authorize restore" />
    </div>

    <div style="display:flex;justify-content:flex-end;gap:10px;">
      <button class="btn btn-outline" id="cancelRestoreBtn">Cancel</button>
      <button class="btn btn-danger" id="confirmRestoreBtn">Verify Integrity &amp; Restore Database</button>
    </div>
  `, (m) => {
    let activeMode = initialMode;
    const localBtn = m.querySelector('#restoreModeLocalBtn');
    const uploadBtn = m.querySelector('#restoreModeUploadBtn');
    const localWrap = m.querySelector('#restoreLocalWrap');
    const uploadWrap = m.querySelector('#restoreUploadWrap');

    localBtn.onclick = () => {
      activeMode = 'local';
      localBtn.classList.add('active');
      uploadBtn.classList.remove('active');
      localWrap.style.display = 'block';
      uploadWrap.style.display = 'none';
    };

    uploadBtn.onclick = () => {
      activeMode = 'upload';
      uploadBtn.classList.add('active');
      localBtn.classList.remove('active');
      uploadWrap.style.display = 'block';
      localWrap.style.display = 'none';
    };

    m.querySelector('#cancelRestoreBtn').onclick = closeModal;
    m.querySelector('#confirmRestoreBtn').onclick = async () => {
      const createPreRestoreBackup = m.querySelector('#preRestoreSafetyCheck').checked;
      const confirmPassword = m.querySelector('#restoreConfirmPassword').value;
      const confirmBtn = m.querySelector('#confirmRestoreBtn');

      if (!confirmPassword) {
        alert('Please enter your admin passphrase to authorize the restore.');
        return;
      }

      try {
        confirmBtn.disabled = true;
        confirmBtn.textContent = 'Restoring...';
        let res;

        if (activeMode === 'local') {
          const selectEl = m.querySelector('#restoreSnapshotSelect');
          const targetFile = selectEl ? selectEl.value : '';
          if (!targetFile) {
            alert('Please select a backup snapshot to restore.');
            confirmBtn.disabled = false;
            confirmBtn.textContent = 'Verify Integrity & Restore Database';
            return;
          }
          res = await apiFetch(`/api/backups/restore/${encodeURIComponent(targetFile)}`, {
            method: 'POST',
            body: JSON.stringify({ createPreRestoreBackup, confirmPassword })
          });
        } else {
          const fileInput = m.querySelector('#restoreUploadFileInput');
          const file = fileInput?.files?.[0];
          if (!file) {
            alert('Please choose a .db snapshot file to upload and restore.');
            confirmBtn.disabled = false;
            confirmBtn.textContent = 'Verify Integrity & Restore Database';
            return;
          }
          const formData = new FormData();
          formData.append('file', file);
          formData.append('createPreRestoreBackup', String(createPreRestoreBackup));
          formData.append('confirmPassword', confirmPassword);
          res = await apiFetch('/api/backups/restore-upload', {
            method: 'POST',
            body: formData
          });
        }

        closeModal();
        await refreshReferenceData();
        const safetyBackup = res.preRestoreBackupFile || res.preRestoreSnapshot;
        showToast(`Restored database from ${res.restoredFrom}${safetyBackup ? ` (Safety backup: ${safetyBackup})` : ''}`);
        if (onSuccess) await onSuccess(res);
      } catch (err) {
        confirmBtn.disabled = false;
        confirmBtn.textContent = 'Verify Integrity & Restore Database';
        alert(`Restore failed: ${err.message}`);
      }
    };
  });
}

const viewTitles = {
  budgets: 'Monthly Budgets',
  cashflow: 'Cashflow & Sankey',
  upload: 'Upload CSVs',
  inspector: 'Transaction Inspector',
  topology: 'Categories, Rules & Backups'
};

export function openChangePasswordModal() {
  openModal(`
    <h3 style="margin-top:0;">Change My Passphrase</h3>
    <p style="font-size:13px;color:#64748B;">
      Passphrases must be 12–128 characters. Changing your passphrase will invalidate other active sessions.
    </p>
    <div id="changePassError" role="alert" aria-live="polite" style="display:none;background:#FEF2F2;border:1px solid #FECACA;color:#B91C1C;border-radius:8px;padding:8px 10px;font-size:12px;font-weight:600;margin-bottom:10px;"></div>
    <div style="display:flex;flex-direction:column;gap:10px;margin:12px 0;">
      <div>
        <label for="currentPassInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Current Passphrase</label>
        <input type="password" id="currentPassInput" class="input-control" style="width:100%;" autocomplete="current-password" />
      </div>
      <div>
        <label for="newPassInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">New Passphrase (12–128 characters)</label>
        <input type="password" id="newPassInput" class="input-control" style="width:100%;" autocomplete="new-password" minlength="12" maxlength="128" />
      </div>
      <div>
        <label for="confirmNewPassInput" style="font-size:12px;font-weight:700;display:block;margin-bottom:4px;">Confirm New Passphrase</label>
        <input type="password" id="confirmNewPassInput" class="input-control" style="width:100%;" autocomplete="new-password" minlength="12" maxlength="128" />
      </div>
    </div>
    <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:14px;">
      <button class="btn btn-outline" id="cancelChangePassBtn">Cancel</button>
      <button class="btn btn-primary" id="saveChangePassBtn">Update Passphrase</button>
    </div>
  `, (m) => {
    const errEl = m.querySelector('#changePassError');
    const saveBtn = m.querySelector('#saveChangePassBtn');
    m.querySelector('#cancelChangePassBtn').onclick = closeModal;
    saveBtn.onclick = async () => {
      errEl.style.display = 'none';
      const currentPassword = m.querySelector('#currentPassInput').value;
      const newPassword = m.querySelector('#newPassInput').value;
      const confirmPass = m.querySelector('#confirmNewPassInput').value;

      if (!currentPassword || !newPassword) {
        errEl.textContent = 'Please fill in all fields.';
        errEl.style.display = 'block';
        return;
      }
      if (newPassword !== confirmPass) {
        errEl.textContent = 'New passphrases do not match.';
        errEl.style.display = 'block';
        return;
      }

      saveBtn.disabled = true;
      saveBtn.textContent = 'Updating...';
      try {
        await apiFetch('/api/auth/change-password', {
          method: 'PUT',
          body: JSON.stringify({ currentPassword, newPassword })
        });
        closeModal();
        showToast('Passphrase updated successfully.');
      } catch (err) {
        errEl.textContent = err.message || 'Failed to update passphrase.';
        errEl.style.display = 'block';
        saveBtn.disabled = false;
        saveBtn.textContent = 'Update Passphrase';
      }
    };
  });
}

function broadcastAuthSync() {
  try {
    if (typeof localStorage !== 'undefined' && localStorage !== null) {
      localStorage.setItem(AUTH_SYNC_KEY, String(Date.now()));
    }
  } catch {
    // Ignore storage errors
  }
}

export async function signOut(allDevices = false) {
  try {
    await apiFetch(allDevices ? '/api/auth/logout?allDevices=true' : '/api/auth/logout', { method: 'POST' });
  } catch {
    // Proceed with local sign-out even if session was already expired
  }
  state.currentUser = null;
  state.pendingReturnView = null;
  state.accounts = [];
  state.categories = [];
  closeModal();
  broadcastAuthSync();
  await initAuth();
}

export function renderUserChrome() {
  const shell = document.getElementById('appShell');
  const user = state.currentUser;

  if (shell) {
    shell.classList.toggle('auth-locked', !user);
  }

  const admin = isAdmin();
  document.querySelectorAll('[data-view="upload"]').forEach(btn => {
    btn.style.display = admin ? '' : 'none';
  });

  const navTopLabel = document.querySelector('.nav-topology-label');
  if (navTopLabel) {
    navTopLabel.textContent = admin ? 'Categories, Rules & Backups' : 'Categories & Rules';
  }
  const mobileTopTab = document.querySelector('#mobileBottomNav [data-view="topology"] .tab-label');
  if (mobileTopTab) {
    mobileTopTab.textContent = admin ? 'Admin' : 'Rules';
  }

  const sidebarUserEl = document.getElementById('sidebarUserSection');
  if (sidebarUserEl) {
    if (!user) {
      sidebarUserEl.innerHTML = '';
    } else {
      sidebarUserEl.innerHTML = `
        <div class="sidebar-user-card">
          <div style="display:flex;justify-content:space-between;align-items:center;gap:6px;">
            <strong style="font-size:13px;color:#F8FAFC;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(user.displayName || user.username)}</strong>
            <span class="auth-role-badge ${admin ? 'role-admin' : ''}">${escapeHtml(user.role)}</span>
          </div>
          <div style="font-size:11px;color:#94A3B8;margin-top:2px;">@${escapeHtml(user.username)}</div>
          <div class="sidebar-user-actions">
            <button type="button" class="sidebar-user-btn" id="changePassSidebarBtn">Passphrase</button>
            <button type="button" class="sidebar-user-btn" id="signOutSidebarBtn">Sign Out</button>
            <button type="button" class="sidebar-user-btn" id="signOutAllSidebarBtn" title="Revoke all sessions across devices">Sign Out All</button>
          </div>
        </div>
      `;
      sidebarUserEl.querySelector('#changePassSidebarBtn').onclick = openChangePasswordModal;
      sidebarUserEl.querySelector('#signOutSidebarBtn').onclick = () => signOut(false);
      sidebarUserEl.querySelector('#signOutAllSidebarBtn').onclick = () => signOut(true);
    }
  }

  const mobileUserEl = document.getElementById('mobileUserSection');
  if (mobileUserEl) {
    if (!user) {
      mobileUserEl.innerHTML = '';
    } else {
      mobileUserEl.innerHTML = `
        <div style="display:flex;align-items:center;gap:6px;">
          <button type="button" class="btn btn-outline btn-sm" id="mobilePassphraseBtn" style="padding:3px 7px;font-size:11px;">Key</button>
          <button type="button" class="btn btn-outline btn-sm" id="mobileSignOutBtn" style="padding:3px 7px;font-size:11px;">Sign Out</button>
        </div>
      `;
      mobileUserEl.querySelector('#mobilePassphraseBtn').onclick = openChangePasswordModal;
      mobileUserEl.querySelector('#mobileSignOutBtn').onclick = () => signOut(false);
    }
  }
}

export async function navigateTo(viewName) {
  if (!state.currentUser) {
    await initAuth();
    return;
  }

  const safeView = isAllowedViewForCurrentUser(viewName) ? viewName : 'budgets';
  state.currentView = safeView;

  document.querySelectorAll('[data-view]').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.view === safeView);
  });

  const mobileTitle = document.getElementById('mobileHeaderTitle');
  if (mobileTitle) {
    mobileTitle.textContent = (safeView === 'topology' && !isAdmin())
      ? 'Categories & Rules'
      : (viewTitles[safeView] || 'HomeBudget');
  }

  const container = document.getElementById('viewContainer');
  container.innerHTML = `<div class="card-panel">Loading ${escapeHtml(viewTitles[safeView] || safeView)}...</div>`;

  try {
    await refreshReferenceData();
    if (safeView === 'budgets') await renderBudgetsView(container);
    else if (safeView === 'cashflow') await renderCashflowView(container);
    else if (safeView === 'upload') await renderUploadView(container);
    else if (safeView === 'inspector') await renderInspectorView(container);
    else if (safeView === 'topology') await renderTopologyView(container);
  } catch (err) {
    container.innerHTML = `<div class="card-panel" style="color:#DC2626;">Error loading view: ${escapeHtml(err.message)}</div>`;
  }
}

export async function initAuth() {
  const container = document.getElementById('viewContainer');
  try {
    const status = await apiFetch('/api/auth/status');
    if (!status?.isSetupRequired && status?.isAuthenticated && status?.currentUser) {
      state.currentUser = status.currentUser;
      renderUserChrome();
      const targetView = (state.pendingReturnView && isAllowedViewForCurrentUser(state.pendingReturnView))
        ? state.pendingReturnView
        : (isAllowedViewForCurrentUser(state.currentView) ? state.currentView : 'budgets');
      state.pendingReturnView = null;
      await navigateTo(targetView);
      return;
    }

    state.currentUser = null;
    renderUserChrome();
    renderLoginView(container, status, async (authenticatedUser) => {
      state.currentUser = authenticatedUser;
      renderUserChrome();
      const targetView = (state.pendingReturnView && isAllowedViewForCurrentUser(state.pendingReturnView))
        ? state.pendingReturnView
        : 'budgets';
      state.pendingReturnView = null;
      await navigateTo(targetView);
    });
  } catch (err) {
    state.currentUser = null;
    renderUserChrome();
    if (container) {
      container.innerHTML = `<div class="card-panel" style="color:#DC2626;">Unable to verify authentication status: ${escapeHtml(err.message)}</div>`;
    }
  }
}

document.addEventListener('DOMContentLoaded', () => {
  document.querySelectorAll('[data-view]').forEach(btn => {
    btn.addEventListener('click', () => navigateTo(btn.dataset.view));
  });

  window.addEventListener('orientationchange', () => {
    if (state.currentUser && state.currentView === 'cashflow') {
      setTimeout(() => navigateTo('cashflow'), 200);
    }
  });

  window.addEventListener('storage', (e) => {
    if (e.key === AUTH_SYNC_KEY) {
      state.currentUser = null;
      closeModal();
      initAuth();
    }
  });

  initAuth();
});
