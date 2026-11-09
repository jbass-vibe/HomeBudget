import { formatCurrency } from '../app.js';

/**
 * Renders a multi-column interactive Cashflow Sankey diagram using pure SVG (zero external CDN required).
 * Columns:
 *   Col 0: Resolved Income (+ Over-Income / Deficit if applicable)
 *   Col 1: Total Spending + Unspent / Remaining Cashflow
 *   Col 2: Primary Spending Categories
 *   Col 3 (Optional): Subcategories (strictly for categories with non-null subcategory data)
 */
export function renderSankeyDiagram(containerEl, sankeyData, totalIncome, onNodeClick = null) {
  containerEl.innerHTML = '';

  if (!sankeyData || !sankeyData.nodes || sankeyData.nodes.length === 0 || sankeyData.links.length === 0) {
    containerEl.innerHTML = `
      <div style="padding:40px;text-align:center;color:#64748B;">
        No spending or cashflow transactions recorded for this time window yet.
      </div>`;
    return;
  }

  const nodes = sankeyData.nodes.map(n => ({ ...n }));
  const links = sankeyData.links.map(l => ({ ...l }));

  // Assign columns based on NodeType
  const hasSubcategories = nodes.some(n => n.nodeType === 'Subcategory');
  nodes.forEach(n => {
    if (n.nodeType === 'Income' || n.nodeType === 'Deficit') n.col = 0;
    else if (n.nodeType === 'Summary') n.col = 1;
    else if (n.nodeType === 'Category') n.col = 2;
    else n.col = 3;
  });

  const maxCol = hasSubcategories ? 3 : 2;
  const width = Math.max(containerEl.clientWidth || 860, 740);
  const categoryCount = nodes.filter(n => n.col === 2).length;
  const subcatCount = nodes.filter(n => n.col === 3).length;
  const maxRowsInAnyCol = Math.max(categoryCount, subcatCount, 4);
  const height = Math.max(440, maxRowsInAnyCol * 46 + 80);

  const padLeft = 24;
  const padRight = 210;
  const padTop = 28;
  const padBottom = 28;
  const nodeWidth = 18;
  const nodeGap = 14;

  const usableWidth = width - padLeft - padRight - nodeWidth;
  const colStep = maxCol > 0 ? usableWidth / maxCol : usableWidth;

  // Group nodes by column and compute vertical positions
  const cols = {};
  for (let c = 0; c <= maxCol; c++) cols[c] = [];
  nodes.forEach(n => cols[n.col].push(n));

  const maxColumnTotal = Math.max(
    ...Object.values(cols).map(colNodes => colNodes.reduce((sum, n) => sum + Number(n.amount || 0), 0)),
    1
  );

  const maxAvailableHeight = height - padTop - padBottom - Math.max(0, maxRowsInAnyCol - 1) * nodeGap;
  const scaleY = maxAvailableHeight / maxColumnTotal;

  Object.entries(cols).forEach(([colIdxStr, colNodes]) => {
    const colIdx = Number(colIdxStr);
    const totalColHeight = colNodes.reduce((acc, n) => acc + Math.max(6, Number(n.amount) * scaleY), 0)
      + Math.max(0, colNodes.length - 1) * nodeGap;

    let currentY = padTop + Math.max(0, (height - padTop - padBottom - totalColHeight) / 2);

    colNodes.forEach(n => {
      n.x0 = padLeft + colIdx * colStep;
      n.x1 = n.x0 + nodeWidth;
      n.h = Math.max(6, Number(n.amount) * scaleY);
      n.y0 = currentY;
      n.y1 = currentY + n.h;
      n.outOffset = 0;
      n.inOffset = 0;
      currentY = n.y1 + nodeGap;
    });
  });

  // Compute link vertical thickness and Bezier coordinates
  const nodeByIdx = {};
  nodes.forEach(n => { nodeByIdx[n.index] = n; });

  links.forEach(l => {
    const src = nodeByIdx[l.source];
    const tgt = nodeByIdx[l.target];
    if (!src || !tgt) return;

    const srcTotalLinks = links.filter(x => x.source === src.index).reduce((a, b) => a + Number(b.value), 0) || 1;
    const tgtTotalLinks = links.filter(x => x.target === tgt.index).reduce((a, b) => a + Number(b.value), 0) || 1;

    const wSrc = (Number(l.value) / srcTotalLinks) * src.h;
    const wTgt = (Number(l.value) / tgtTotalLinks) * tgt.h;
    l.width = Math.max(2, (wSrc + wTgt) / 2);

    l.sy = src.y0 + src.outOffset + wSrc / 2;
    src.outOffset += wSrc;

    l.ty = tgt.y0 + tgt.inOffset + wTgt / 2;
    tgt.inOffset += wTgt;

    l.sx = src.x1;
    l.tx = tgt.x0;
    l.sourceNode = src;
    l.targetNode = tgt;
  });

  const svgNs = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNs, 'svg');
  svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
  svg.setAttribute('class', 'sankey-svg');

  // Tooltip element
  let tooltip = document.querySelector('.sankey-tooltip');
  if (!tooltip) {
    tooltip = document.createElement('div');
    tooltip.className = 'sankey-tooltip';
    tooltip.style.display = 'none';
    document.body.appendChild(tooltip);
  }

  const showTip = (evt, html) => {
    tooltip.innerHTML = html;
    tooltip.style.display = 'block';
    const x = Math.min(window.innerWidth - 240, (evt.clientX || 100) + 12);
    const y = Math.max(12, (evt.clientY || 100) - 36);
    tooltip.style.left = `${x}px`;
    tooltip.style.top = `${y}px`;
  };

  const hideTip = () => {
    tooltip.style.display = 'none';
  };

  // Draw links
  const linksGroup = document.createElementNS(svgNs, 'g');
  links.forEach(l => {
    if (!l.sourceNode || !l.targetNode) return;
    const path = document.createElementNS(svgNs, 'path');
    const mx = (l.sx + l.tx) / 2;
    const d = `M ${l.sx} ${l.sy} C ${mx} ${l.sy}, ${mx} ${l.ty}, ${l.tx} ${l.ty}`;
    path.setAttribute('d', d);
    path.setAttribute('class', 'sankey-link');
    path.setAttribute('stroke', l.colorHex || '#64748B');
    path.setAttribute('stroke-width', String(l.width));

    const pctIncome = totalIncome > 0 ? ((Number(l.value) / totalIncome) * 100).toFixed(1) : '0.0';
    const tipHtml = `${l.sourceNode.name} &rarr; ${l.targetNode.name}<br/><strong>${formatCurrency(l.value)}</strong> (${pctIncome}% of Income)`;

    path.addEventListener('mousemove', e => showTip(e, tipHtml));
    path.addEventListener('mouseleave', hideTip);
    path.addEventListener('touchstart', e => {
      const touch = e.touches[0];
      showTip(touch, tipHtml);
    }, { passive: true });

    linksGroup.appendChild(path);
  });
  svg.appendChild(linksGroup);

  // Draw nodes & labels
  const nodesGroup = document.createElementNS(svgNs, 'g');
  nodes.forEach(n => {
    const g = document.createElementNS(svgNs, 'g');
    g.setAttribute('class', 'sankey-node');

    const rect = document.createElementNS(svgNs, 'rect');
    rect.setAttribute('x', String(n.x0));
    rect.setAttribute('y', String(n.y0));
    rect.setAttribute('width', String(nodeWidth));
    rect.setAttribute('height', String(n.h));
    rect.setAttribute('fill', n.colorHex || '#3B82F6');

    const pctIncome = totalIncome > 0 ? ((Number(n.amount) / totalIncome) * 100).toFixed(1) : '0.0';
    const tipHtml = `<strong>${n.name}</strong><br/>${formatCurrency(n.amount)} (${pctIncome}% of Income)`;
    rect.addEventListener('mousemove', e => showTip(e, tipHtml));
    rect.addEventListener('mouseleave', hideTip);
    if (onNodeClick) {
      g.style.cursor = 'pointer';
      g.addEventListener('click', () => onNodeClick(n));
    }
    rect.addEventListener('touchstart', e => {
      const touch = e.touches[0];
      showTip(touch, tipHtml);
    }, { passive: true });

    g.appendChild(rect);

    const labelX = n.x1 + 8;
    const centerY = n.y0 + n.h / 2;

    const titleText = document.createElementNS(svgNs, 'text');
    titleText.setAttribute('x', String(labelX));
    titleText.setAttribute('y', String(centerY - 2));
    titleText.setAttribute('class', 'sankey-node-label');
    titleText.textContent = n.name;
    g.appendChild(titleText);

    const amountText = document.createElementNS(svgNs, 'text');
    amountText.setAttribute('x', String(labelX));
    amountText.setAttribute('y', String(centerY + 12));
    amountText.setAttribute('class', 'sankey-node-amount');
    amountText.textContent = `${formatCurrency(n.amount)} (${pctIncome}%)`;
    g.appendChild(amountText);

    nodesGroup.appendChild(g);
  });
  svg.appendChild(nodesGroup);

  containerEl.appendChild(svg);
}
