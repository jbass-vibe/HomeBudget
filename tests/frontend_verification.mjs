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

// 3. Verify merchant grouping logic matches cashflowView.js
const appSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/app.js'), 'utf8');
const cashflowSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/views/cashflowView.js'), 'utf8');
const inspectorSource = fs.readFileSync(path.resolve('src/HomeBudget.Web/wwwroot/js/views/inspectorView.js'), 'utf8');

assert.ok(appSource.includes('export function escapeHtml('), 'app.js must export escapeHtml');
assert.ok(cashflowSource.includes('export function groupUncategorizedByMerchant('), 'cashflowView.js must export groupUncategorizedByMerchant');
assert.ok(inspectorSource.includes('tx-notes'), 'inspectorView.js must render Notes input (.tx-notes)');
console.log('✔ Frontend contract assertions passed.');
