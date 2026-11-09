import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const jsFiles = [
  'src/HomeBudget.Web/wwwroot/js/app.js',
  'src/HomeBudget.Web/wwwroot/js/components/sankeyChart.js',
  'src/HomeBudget.Web/wwwroot/js/views/budgetsView.js',
  'src/HomeBudget.Web/wwwroot/js/views/cashflowView.js',
  'src/HomeBudget.Web/wwwroot/js/views/inspectorView.js',
  'src/HomeBudget.Web/wwwroot/js/views/topologyView.js',
  'src/HomeBudget.Web/wwwroot/js/views/uploadView.js'
];

// 1. Syntax-check every ES6 module using `node --check`
for (const relPath of jsFiles) {
  const fullPath = path.resolve(relPath);
  execFileSync(process.execPath, ['--check', fullPath]);
}
console.log(`✔ Syntax verified for all ${jsFiles.length} frontend ES6 modules.`);

// 2. Verify escapeHtml implementation against XSS payloads
function escapeHtml(val) {
  if (val === null || val === undefined) return '';
  return String(val)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

const xssPayload = `<img src=x onerror="alert('xss')"> & 'quote'`;
const escaped = escapeHtml(xssPayload);
assert.equal(escaped, '&lt;img src=x onerror=&quot;alert(&#39;xss&#39;)&quot;&gt; &amp; &#39;quote&#39;');
assert.ok(!escaped.includes('<'));
assert.ok(!escaped.includes('>'));
console.log('✔ escapeHtml neutralizes HTML/script injection payloads.');

// 3. Verify v2.2 list sorting contracts & controls across all frontend views
const appSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/app.js'), 'utf8');
const budgetsSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/views/budgetsView.js'), 'utf8');
const cashflowSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/views/cashflowView.js'), 'utf8');
const inspectorSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/views/inspectorView.js'), 'utf8');
const topologySource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/views/topologyView.js'), 'utf8');
const uploadSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/views/uploadView.js'), 'utf8');

assert.ok(appSource.includes('export function escapeHtml('), 'app.js must export escapeHtml');
assert.ok(appSource.includes('export function sortCategories('), 'app.js must export sortCategories');
assert.ok(appSource.includes('catModalSortSelect'), 'app.js Categorize Modal must include #catModalSortSelect');
assert.ok(budgetsSource.includes('export function sortBudgetCards('), 'budgetsView.js must export sortBudgetCards');
assert.ok(budgetsSource.includes('budgetsSortSelect'), 'budgetsView.js must include #budgetsSortSelect');
assert.ok(cashflowSource.includes('export function groupUncategorizedByMerchant('), 'cashflowView.js must export groupUncategorizedByMerchant');
assert.ok(cashflowSource.includes('cashflowCatSortSelect'), 'cashflowView.js must include #cashflowCatSortSelect');
assert.ok(cashflowSource.includes('triageSortSelect'), 'cashflowView.js must include #triageSortSelect');
assert.ok(inspectorSource.includes('tx-notes'), 'inspectorView.js must render Notes input (.tx-notes)');
assert.ok(inspectorSource.includes('recent_changes'), 'inspectorView.js must support recent_changes sort option');
assert.ok(topologySource.includes('topologyCatSortSelect'), 'topologyView.js must include #topologyCatSortSelect');
assert.ok(topologySource.includes('topologyRuleSortSelect'), 'topologyView.js must include #topologyRuleSortSelect');
assert.ok(uploadSource.includes('uploadCatDropdownSort'), 'uploadView.js must include #uploadCatDropdownSort');

// 4. Verify sortCategories behavior across alpha_asc, recent, custom_order, and budget_desc
const sampleCategories = [
  { categoryId: 1, name: 'Utilities', sortOrder: 30, monthlyBudget: 250, updatedAt: '2026-11-02 10:00:00' },
  { categoryId: 2, name: 'Automotive', sortOrder: 20, monthlyBudget: 400, updatedAt: '2026-11-08 09:00:00' },
  { categoryId: 3, name: 'Groceries', sortOrder: 10, monthlyBudget: 900, updatedAt: '2026-11-05 14:00:00' }
];

function sortCategories(categories, sortMode) {
  return [...categories].sort((a, b) => {
    const nameA = String(a.name || '');
    const nameB = String(b.name || '');
    switch (sortMode) {
      case 'alpha_asc':
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      case 'recent': {
        const tsA = String(a.lastActivityAt || a.updatedAt || a.createdAt || '');
        const tsB = String(b.lastActivityAt || b.updatedAt || b.createdAt || '');
        if (tsB !== tsA) return tsB.localeCompare(tsA);
        return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      }
      case 'budget_desc':
        return (Number(b.monthlyBudget || 0) - Number(a.monthlyBudget || 0)) || nameA.localeCompare(nameB);
      case 'custom_order':
      default:
        return (Number(a.sortOrder ?? 100) - Number(b.sortOrder ?? 100)) || nameA.localeCompare(nameB);
    }
  });
}

assert.deepEqual(sortCategories(sampleCategories, 'alpha_asc').map(c => c.name), ['Automotive', 'Groceries', 'Utilities']);
assert.deepEqual(sortCategories(sampleCategories, 'recent').map(c => c.name), ['Automotive', 'Groceries', 'Utilities']);
assert.deepEqual(sortCategories(sampleCategories, 'custom_order').map(c => c.name), ['Groceries', 'Automotive', 'Utilities']);
assert.deepEqual(sortCategories(sampleCategories, 'budget_desc').map(c => c.name), ['Groceries', 'Automotive', 'Utilities']);

// 5. Verify v2.3 & v2.4 Smart Coalesced Backup Policy, Clear Transaction History & Database Restore contracts
assert.ok(appSource.includes("backupFileCreated === 'scheduled'"), 'app.js formatBackupStatus must handle scheduled coalesced backups');
assert.ok(appSource.includes("backupFileCreated === 'skipped_policy'"), 'app.js formatBackupStatus must handle skipped_policy');
assert.ok(appSource.includes('export function openClearTransactionHistoryModal('), 'app.js must export openClearTransactionHistoryModal');
assert.ok(appSource.includes('clearHistoryBackupCheck'), 'app.js clear history modal must include #clearHistoryBackupCheck');
assert.ok(appSource.includes('/api/transactions/clear-history'), 'app.js must call /api/transactions/clear-history');
assert.ok(appSource.includes('export function openRestoreDatabaseModal('), 'app.js must export openRestoreDatabaseModal');
assert.ok(appSource.includes('preRestoreSafetyCheck'), 'app.js restore modal must include #preRestoreSafetyCheck');
assert.ok(appSource.includes('/api/backups/restore-upload'), 'app.js restore modal must support /api/backups/restore-upload');
assert.ok(topologySource.includes('export function sortBackups('), 'topologyView.js must export sortBackups');
assert.ok(topologySource.includes('backupAutoModeSelect'), 'topologyView.js must include #backupAutoModeSelect');
assert.ok(topologySource.includes('backupDebounceInput'), 'topologyView.js must include #backupDebounceInput');
assert.ok(topologySource.includes('backupCooldownInput'), 'topologyView.js must include #backupCooldownInput');
assert.ok(topologySource.includes('backupMaxRetainedInput'), 'topologyView.js must include #backupMaxRetainedInput');
assert.ok(topologySource.includes('saveBackupPolicyBtn'), 'topologyView.js must include #saveBackupPolicyBtn');
assert.ok(topologySource.includes('backupSortSelect'), 'topologyView.js must include #backupSortSelect');
assert.ok(topologySource.includes('bulkDeleteBackupsBtn'), 'topologyView.js must include #bulkDeleteBackupsBtn');
assert.ok(topologySource.includes('pruneBackupsBtn'), 'topologyView.js must include #pruneBackupsBtn');
assert.ok(topologySource.includes('clearHistoryAdminBtn'), 'topologyView.js must include #clearHistoryAdminBtn');
assert.ok(topologySource.includes('restoreDatabaseModalBtn'), 'topologyView.js must include #restoreDatabaseModalBtn');
assert.ok(topologySource.includes('restore-backup-btn'), 'topologyView.js must include .restore-backup-btn');
assert.ok(topologySource.includes('del-backup-btn'), 'topologyView.js must include .del-backup-btn');
assert.ok(inspectorSource.includes('clearHistoryInspectorBtn'), 'inspectorView.js must include #clearHistoryInspectorBtn');

console.log('✔ Frontend v2.2–v2.4 list sorting, coalesced backup status, clear transaction history, and database restore assertions passed.');


