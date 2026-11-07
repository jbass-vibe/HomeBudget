import { renderBudgetsView } from './views/budgetsView.js';
import { renderCashflowView } from './views/cashflowView.js';
import { renderUploadView } from './views/uploadView.js';
import { renderInspectorView } from './views/inspectorView.js';
import { renderTopologyView } from './views/topologyView.js';

export const state = {
  currentView: 'budgets',
  selectedYearMonth: new Date().toISOString().slice(0, 7),
  cashflowWindow: 'mtd',
  cashflowMonths: 3,
  cashflowStart: '',
  cashflowEnd: '',
  includeSubcategories: false,
  accounts: [],
  categories: []
};

export async function apiFetch(url, options = {}) {
  const res = await fetch(url, {
    headers: options.body && !(options.body instanceof FormData)
      ? { 'Content-Type': 'application/json', ...(options.headers || {}) }
      : options.headers,
    ...options
  });

  const rawText = await res.text();

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
  setTimeout(() => toast.remove(), 3500);
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
    apiFetch('/api/categories')
  ]);
  state.accounts = accounts;
  state.categories = categories;
}

const viewTitles = {
  budgets: 'Monthly Budgets',
  cashflow: 'Cashflow & Sankey',
  upload: 'Upload CSVs',
  inspector: 'Transaction Inspector',
  topology: 'Categories, Rules & Backups'
};

export async function navigateTo(viewName) {
  state.currentView = viewName;

  document.querySelectorAll('[data-view]').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.view === viewName);
  });

  const mobileTitle = document.getElementById('mobileHeaderTitle');
  if (mobileTitle) {
    mobileTitle.textContent = viewTitles[viewName] || 'HomeBudget';
  }

  const container = document.getElementById('viewContainer');
  container.innerHTML = `<div class="card-panel">Loading ${viewTitles[viewName] || viewName}...</div>`;

  try {
    await refreshReferenceData();
    if (viewName === 'budgets') await renderBudgetsView(container);
    else if (viewName === 'cashflow') await renderCashflowView(container);
    else if (viewName === 'upload') await renderUploadView(container);
    else if (viewName === 'inspector') await renderInspectorView(container);
    else if (viewName === 'topology') await renderTopologyView(container);
  } catch (err) {
    container.innerHTML = `<div class="card-panel" style="color:#DC2626;">Error loading view: ${err.message}</div>`;
  }
}

document.addEventListener('DOMContentLoaded', () => {
  document.querySelectorAll('[data-view]').forEach(btn => {
    btn.addEventListener('click', () => navigateTo(btn.dataset.view));
  });

  window.addEventListener('orientationchange', () => {
    if (state.currentView === 'cashflow') {
      setTimeout(() => navigateTo('cashflow'), 200);
    }
  });

  navigateTo('budgets');
});
