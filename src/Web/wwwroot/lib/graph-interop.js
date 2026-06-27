// ES module — loaded via IJSRuntime.InvokeAsync("import", "./lib/graph-interop.js")
// Requires cytoscape, dagre, cytoscape-dagre to be loaded first via <script> tags in index.html.

const TIER_COLORS = {
  0: '#4caf50',
  1: '#2196f3',
  2: '#9c27b0',
  3: '#ff9800',
  4: '#f44336',
  5: '#795548',
};

// Map of elementId → { cy, persistKey, layoutName, clickDirection, clickTransitive, lastTappedId, baseIds, combineMode }
const instances = new Map();

const DIM_OPACITY_KEY = 'sdkinfo.graph.dimOpacity';

function loadDimOpacity() {
  try {
    const v = parseFloat(localStorage.getItem(DIM_OPACITY_KEY));
    return isNaN(v) ? 0.25 : v;
  } catch { return 0.25; }
}

let dimOpacity = loadDimOpacity();

// ─── localStorage helpers ────────────────────────────────────────────────────

function storageKey(persistKey) {
  return `sdkinfo.graph.pos.${persistKey}`;
}

function layoutStorageKey(persistKey) {
  return `sdkinfo.graph.layout.${persistKey}`;
}

function loadPositions(persistKey) {
  try {
    const raw = localStorage.getItem(storageKey(persistKey));
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function savePositions(persistKey, cy) {
  try {
    const pos = {};
    cy.nodes().forEach(n => { pos[n.id()] = n.position(); });
    localStorage.setItem(storageKey(persistKey), JSON.stringify(pos));
  } catch { /* quota exceeded etc. — silently ignore */ }
}

function clearPositions(persistKey) {
  try { localStorage.removeItem(storageKey(persistKey)); } catch { }
}

// ─── Public API ──────────────────────────────────────────────────────────────

export function initGraph(elementId, dotNetRef, persistKey, layoutName) {
  const container = document.getElementById(elementId);
  if (!container) {
    console.warn(`[graph-interop] Element #${elementId} not found`);
    return;
  }

  const cy = cytoscape({
    container,
    style: buildStyle(),
    layout: { name: 'preset' },
    userZoomingEnabled: true,
    userPanningEnabled: true,
    boxSelectionEnabled: false,
  });

  // Persist node positions after dragging
  cy.on('dragfree', 'node', () => {
    const inst2 = instances.get(elementId);
    if (inst2) savePositions(inst2.persistKey, cy);
  });

  cy.on('tap', 'node', (evt) => {
    const inst2 = instances.get(elementId);
    if (!inst2) return;
    const t = evt.target;
    inst2.lastTappedId = t.id();
    dotNetRef.invokeMethodAsync('NodeClickedAsync', t.id());
    applyNodeTapHighlight(cy, t, inst2);
  });

  cy.on('tap', (evt) => {
    if (evt.target !== cy) return;
    const inst2 = instances.get(elementId);
    if (!inst2) return;
    inst2.lastTappedId = null;
    dotNetRef.invokeMethodAsync('BackgroundTappedAsync');
    if (inst2.baseIds.size > 0) {
      // Restore base highlight; keep chips in C#
      applyBaseHighlight(cy, inst2.baseIds);
    } else {
      cy.elements().removeClass('highlighted dimmed');
    }
  });

  const resolvedLayout = layoutName || 'dagre';
  instances.set(elementId, {
    cy,
    persistKey,
    layoutName: resolvedLayout,
    clickDirection: 'both',
    clickTransitive: false,
    lastTappedId: null,
    baseIds: new Set(),
    combineMode: 'base',
  });
}

export function setData(elementId, nodes, edges) {
  const inst = instances.get(elementId);
  if (!inst) return;
  const { cy, persistKey } = inst;

  const elements = [
    ...nodes.map(n => ({
      data: {
        id: n.id,
        label: n.label,
        tier: n.tier,
        ciStatus: n.ciStatus,
        hasLocalDev: n.hasLocalDev,
      }
    })),
    ...edges.map(e => ({
      data: {
        id: `${e.source}->${e.target}`,
        source: e.source,
        target: e.target,
        kind: e.kind,
      }
    })),
  ];

  cy.elements().remove();
  cy.add(elements);

  const saved = loadPositions(persistKey);
  const nodeIds = nodes.map(n => n.id);
  const allCovered = saved !== null && nodeIds.every(id => saved[id] !== undefined);

  if (allCovered) {
    cy.nodes().forEach(n => {
      const p = saved[n.id()];
      if (p) n.position(p);
    });
    cy.layout({ name: 'preset' }).run();
  } else {
    const layout = buildLayout(cy, inst.layoutName);
    layout.one('layoutstop', () => savePositions(persistKey, cy));
    layout.run();
  }
}

export function highlightNodes(elementId, ids) {
  const inst = instances.get(elementId);
  if (!inst) return;
  applyIdSetHighlight(inst.cy, new Set(ids));
}

export function resetHighlight(elementId) {
  const inst = instances.get(elementId);
  if (!inst) return;
  inst.cy.elements().removeClass('highlighted dimmed');
}

export function resetLayout(elementId) {
  const inst = instances.get(elementId);
  if (!inst) return;
  const { cy, persistKey, layoutName } = inst;
  clearPositions(persistKey);
  const layout = buildLayout(cy, layoutName);
  layout.one('layoutstop', () => savePositions(persistKey, cy));
  layout.run();
}

export function applyLayout(elementId, mode) {
  const inst = instances.get(elementId);
  if (!inst) return;
  inst.layoutName = mode;
  try { localStorage.setItem(layoutStorageKey(inst.persistKey), mode); } catch { }
  clearPositions(inst.persistKey);
  const layout = buildLayout(inst.cy, mode);
  layout.one('layoutstop', () => savePositions(inst.persistKey, inst.cy));
  layout.run();
}

export function setClickMode(elementId, direction, transitive) {
  const inst = instances.get(elementId);
  if (!inst) return;
  inst.clickDirection = direction;
  inst.clickTransitive = transitive;
  if (inst.lastTappedId) {
    const node = inst.cy.getElementById(inst.lastTappedId);
    if (node && node.length > 0) {
      applyNodeTapHighlight(inst.cy, node, inst);
    }
  }
}

export function setDimOpacity(elementId, value) {
  dimOpacity = value;
  try { localStorage.setItem(DIM_OPACITY_KEY, String(value)); } catch { }
  const inst = instances.get(elementId);
  if (inst) {
    inst.cy.style().selector('.dimmed').style('opacity', value).update();
  }
}

export function setBaseHighlight(elementId, ids) {
  const inst = instances.get(elementId);
  if (!inst) return;
  inst.baseIds = new Set(ids);
  inst.lastTappedId = null;
  if (inst.baseIds.size === 0) {
    inst.cy.elements().removeClass('highlighted dimmed');
  } else {
    applyBaseHighlight(inst.cy, inst.baseIds);
  }
}

export function setCombineMode(elementId, mode) {
  const inst = instances.get(elementId);
  if (!inst) return;
  inst.combineMode = mode;
  if (inst.lastTappedId) {
    const node = inst.cy.getElementById(inst.lastTappedId);
    if (node && node.length > 0) {
      applyNodeTapHighlight(inst.cy, node, inst);
    }
  }
}

export function getPersistedLayout(persistKey) {
  try { return localStorage.getItem(layoutStorageKey(persistKey)) || null; } catch { return null; }
}

export function exportPng(elementId, filename) {
  const inst = instances.get(elementId);
  const cy = inst && inst.cy;
  if (!cy) return;
  const bg = getComputedStyle(document.body).backgroundColor || '#ffffff';
  const data = cy.png({ full: true, scale: 2, bg });
  const a = document.createElement('a');
  a.href = data;
  a.download = filename || 'graph.png';
  document.body.appendChild(a);
  a.click();
  a.remove();
}

// ─── Internal helpers ─────────────────────────────────────────────────────────

function computeDirectedSet(cy, node, direction, transitive) {
  if (direction === 'depends') {
    return transitive ? node.union(node.successors()) : node.union(node.outgoers());
  } else if (direction === 'used') {
    return transitive ? node.union(node.predecessors()) : node.union(node.incomers());
  } else {
    return transitive
      ? node.union(node.successors()).union(node.predecessors())
      : node.closedNeighborhood();
  }
}

function applyNodeTapHighlight(cy, node, inst) {
  const focus = computeDirectedSet(cy, node, inst.clickDirection, inst.clickTransitive);

  if (inst.baseIds.size === 0) {
    // No base — just show focus
    applyCollectionHighlight(cy, focus);
    return;
  }

  // Have base: combine modes
  const baseCol = cy.collection(
    [...inst.baseIds].map(id => cy.getElementById(id)).filter(el => el.length > 0)
  );

  let display;
  if (inst.combineMode === 'union') {
    display = focus.union(baseCol);
  } else if (inst.combineMode === 'intersection') {
    // intersection of node sets; keep edges between survivors
    const focusNodeIds = new Set(focus.nodes().map(n => n.id()));
    const baseNodeIds = new Set(baseCol.nodes().map(n => n.id()));
    const intersect = cy.nodes().filter(n => focusNodeIds.has(n.id()) && baseNodeIds.has(n.id()));
    display = intersect;
  } else {
    // 'base' mode: focus only (base is just the fallback on background-tap)
    display = focus;
  }

  applyCollectionHighlight(cy, display);
}

function applyCollectionHighlight(cy, col) {
  cy.elements().removeClass('highlighted dimmed');
  const idSet = new Set(col.nodes().map(n => n.id()));
  cy.nodes().forEach(n => {
    if (idSet.has(n.id())) n.addClass('highlighted');
    else n.addClass('dimmed');
  });
  cy.edges().forEach(e => {
    if (idSet.has(e.source().id()) && idSet.has(e.target().id()))
      e.addClass('highlighted');
    else
      e.addClass('dimmed');
  });
}

function applyBaseHighlight(cy, baseIds) {
  applyIdSetHighlight(cy, baseIds);
}

function applyIdSetHighlight(cy, idSet) {
  cy.elements().removeClass('highlighted dimmed');
  cy.nodes().forEach(n => {
    if (idSet.has(n.id())) n.addClass('highlighted');
    else n.addClass('dimmed');
  });
  cy.edges().forEach(e => {
    if (idSet.has(e.source().id()) && idSet.has(e.target().id()))
      e.addClass('highlighted');
    else
      e.addClass('dimmed');
  });
}

function buildLayout(cy, layoutName) {
  switch (layoutName) {
    case 'cose':
      return cy.layout({
        name: 'cose',
        animate: false,
        nodeRepulsion: 8000,
        idealEdgeLength: 80,
        nodeOverlap: 20,
        gravity: 0.3,
        numIter: 1000,
        padding: 30,
        fit: true,
      });
    case 'grid-tier':
      return cy.layout({
        name: 'grid',
        avoidOverlap: true,
        padding: 20,
        fit: true,
        sort: (a, b) =>
          (a.data('tier') - b.data('tier')) ||
          String(a.data('label')).localeCompare(String(b.data('label'))),
      });
    case 'grid-name':
      return cy.layout({
        name: 'grid',
        avoidOverlap: true,
        padding: 20,
        fit: true,
        sort: (a, b) => String(a.data('label')).localeCompare(String(b.data('label'))),
      });
    case 'grid-dependents': {
      // Pre-compute in-degree (number of nodes that depend on each node)
      cy.nodes().forEach(n => n.data('_deg', n.indegree(false)));
      return cy.layout({
        name: 'grid',
        avoidOverlap: true,
        padding: 20,
        fit: true,
        sort: (a, b) => b.data('_deg') - a.data('_deg'),
      });
    }
    case 'circle':
      return cy.layout({ name: 'circle', fit: true, padding: 20 });
    default: // 'dagre'
      return cy.layout({
        name: 'dagre',
        rankDir: 'TB',
        nodeSep: 60,
        rankSep: 80,
        padding: 20,
        animate: true,
        animationDuration: 300,
      });
  }
}

function buildStyle() {
  const tierStyles = Object.entries(TIER_COLORS).map(([tier, color]) => ({
    selector: `node[tier = ${tier}]`,
    style: { 'background-color': color },
  }));

  return [
    {
      selector: 'node',
      style: {
        'label': 'data(label)',
        'font-size': '11px',
        'text-valign': 'center',
        'text-halign': 'center',
        'width': 'label',
        'height': 'label',
        'padding': '10px',
        'shape': 'round-rectangle',
        'color': '#fff',
        'text-wrap': 'wrap',
        'text-max-width': '120px',
        'border-width': 0,
      },
    },
    ...tierStyles,
    {
      selector: 'node[ciStatus = "failure"]',
      style: { 'border-width': 3, 'border-color': '#f44336' },
    },
    {
      selector: 'node[?hasLocalDev]',
      style: { 'border-width': 2, 'border-color': '#ffc107', 'border-style': 'dashed' },
    },
    {
      selector: 'edge',
      style: {
        'width': 1.5,
        'line-color': '#90a4ae',
        'target-arrow-color': '#90a4ae',
        'target-arrow-shape': 'triangle',
        'curve-style': 'bezier',
        'arrow-scale': 0.8,
      },
    },
    {
      selector: 'edge[kind = "contracts"]',
      style: { 'line-color': '#42a5f5', 'target-arrow-color': '#42a5f5', 'line-style': 'dashed' },
    },
    {
      selector: '.highlighted',
      style: { 'opacity': 1, 'border-width': 3, 'border-color': '#ffeb3b' },
    },
    {
      selector: '.dimmed',
      style: { 'opacity': dimOpacity },
    },
  ];
}
