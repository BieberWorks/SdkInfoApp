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

// Map of elementId → instance state
// { cy, persistKey, currentArrangement, livePositions (Map<id,{x,y}>),
//   clickDirection, clickTransitive, lastTappedId, baseIds, combineMode }
const instances = new Map();

const DIM_OPACITY_KEY = 'sdkinfo.graph.dimOpacity';

function loadDimOpacity() {
  try {
    const v = parseFloat(localStorage.getItem(DIM_OPACITY_KEY));
    return isNaN(v) ? 0.25 : v;
  } catch { return 0.25; }
}

let dimOpacity = loadDimOpacity();

// ─── localStorage key helpers ────────────────────────────────────────────────

function layoutStorageKey(persistKey) {
  return `sdkinfo.graph.layout.${persistKey}`;
}

// Per-arrangement position key
function posKey(persistKey, arrangement) {
  return `sdkinfo.graph.pos.${persistKey}.${arrangement}`;
}

function loadPositionsForArrangement(persistKey, arrangement) {
  try {
    const raw = localStorage.getItem(posKey(persistKey, arrangement));
    return raw ? JSON.parse(raw) : null;
  } catch { return null; }
}

function savePositionsToStorage(persistKey, arrangement, livePositions) {
  try {
    const pos = {};
    livePositions.forEach((p, id) => { pos[id] = p; });
    localStorage.setItem(posKey(persistKey, arrangement), JSON.stringify(pos));
  } catch { /* quota exceeded — silently ignore */ }
}

// ─── Internal: capture live positions from cy into inst.livePositions ────────

function captureLivePositions(inst) {
  inst.livePositions = new Map();
  inst.cy.nodes().forEach(n => {
    inst.livePositions.set(n.id(), { ...n.position() });
  });
}

// ─── Internal: apply livePositions as preset (does not run a layout algo) ───

function applyPresetFromLive(inst) {
  inst.cy.nodes().forEach(n => {
    const p = inst.livePositions.get(n.id());
    if (p) n.position(p);
  });
  inst.cy.layout({ name: 'preset' }).run();
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
    boxSelectionEnabled: true,
    selectionType: 'additive',
  });

  // Read persisted arrangement choice
  let resolvedLayout;
  try { resolvedLayout = localStorage.getItem(layoutStorageKey(persistKey)) || layoutName || 'dagre'; }
  catch { resolvedLayout = layoutName || 'dagre'; }

  // Pre-load saved positions for this arrangement into livePositions (if available)
  const savedForArrangement = loadPositionsForArrangement(persistKey, resolvedLayout);
  const initialLive = new Map();
  if (savedForArrangement) {
    Object.entries(savedForArrangement).forEach(([id, p]) => initialLive.set(id, p));
  }

  const inst = {
    cy,
    persistKey,
    currentArrangement: resolvedLayout,
    livePositions: initialLive,
    clickDirection: 'both',
    clickTransitive: false,
    lastTappedId: null,
    baseIds: new Set(),
    combineMode: 'base',
  };
  instances.set(elementId, inst);

  // fullscreenchange: resize cy so it fills / exits correctly.
  // The fullscreen element is the wrapper (cyw-*); find the cy-div inside it.
  const onFullscreenChange = () => {
    const wrapper = document.fullscreenElement;
    let target;
    if (wrapper) {
      const cyDiv = wrapper.querySelector('div[id^="cy-"]');
      target = cyDiv ? instances.get(cyDiv.id) : null;
    } else {
      // Exiting fullscreen — use this instance
      target = inst;
    }
    if (target) {
      setTimeout(() => {
        target.cy.resize();
        target.cy.animate({ fit: { padding: 30 } }, { duration: 150 });
      }, 80);
    }
  };
  document.addEventListener('fullscreenchange', onFullscreenChange);

  // dragfree: capture ALL node positions so a group-move is fully recorded
  cy.on('dragfree', 'node', () => {
    const i = instances.get(elementId);
    if (!i) return;
    cy.nodes().forEach(n => i.livePositions.set(n.id(), { ...n.position() }));
  });

  cy.on('tap', 'node', (evt) => {
    const i = instances.get(elementId);
    if (!i) return;
    const t = evt.target;
    i.lastTappedId = t.id();
    dotNetRef.invokeMethodAsync('NodeClickedAsync', t.id());
    applyNodeTapHighlight(cy, t, i);
  });

  cy.on('tap', (evt) => {
    if (evt.target !== cy) return;
    const i = instances.get(elementId);
    if (!i) return;
    i.lastTappedId = null;
    dotNetRef.invokeMethodAsync('BackgroundTappedAsync');
    if (i.baseIds.size > 0) {
      applyBaseHighlight(cy, i.baseIds);
    } else {
      cy.elements().removeClass('highlighted dimmed');
    }
  });
}

export function setData(elementId, nodes, edges) {
  const inst = instances.get(elementId);
  if (!inst) return;
  const { cy } = inst;

  const elements = [
    ...nodes.map(n => ({
      data: {
        id: n.id,
        label: n.label,
        tier: n.tier,
        releaseOrder: n.releaseOrder ?? null,
        ciStatus: n.ciStatus,
        hasLocalDev: n.hasLocalDev,
        group: n.group,
        isDangling: n.isDangling ?? false,
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

  // If livePositions covers all current nodes → restore from livePositions (no layout run)
  const nodeIds = nodes.map(n => n.id);
  const allCoveredByLive = nodeIds.length > 0 &&
    nodeIds.every(id => inst.livePositions.has(id));

  if (allCoveredByLive) {
    applyPresetFromLive(inst);
  } else {
    // Run the current arrangement layout; capture results into livePositions
    const layout = buildLayout(cy, inst.currentArrangement);
    layout.one('layoutstop', () => captureLivePositions(inst));
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
  // Re-run current arrangement layout fresh; update livePositions (no localStorage write)
  const layout = buildLayout(inst.cy, inst.currentArrangement);
  layout.one('layoutstop', () => captureLivePositions(inst));
  layout.run();
}

export function applyLayout(elementId, mode) {
  const inst = instances.get(elementId);
  if (!inst) return;
  inst.currentArrangement = mode;
  // Persist the chosen arrangement name
  try { localStorage.setItem(layoutStorageKey(inst.persistKey), mode); } catch { }

  // Try to restore saved positions for this arrangement
  const saved = loadPositionsForArrangement(inst.persistKey, mode);
  const nodeIds = new Set(inst.cy.nodes().map(n => n.id()));
  const allCovered = saved !== null &&
    nodeIds.size > 0 &&
    [...nodeIds].every(id => saved[id] !== undefined);

  if (allCovered) {
    // Restore from saved
    inst.livePositions = new Map(Object.entries(saved));
    applyPresetFromLive(inst);
  } else {
    // Run fresh layout; capture into livePositions (no auto-save)
    const layout = buildLayout(inst.cy, mode);
    layout.one('layoutstop', () => captureLivePositions(inst));
    layout.run();
  }
}

export function saveLayout(elementId) {
  const inst = instances.get(elementId);
  if (!inst) return;
  savePositionsToStorage(inst.persistKey, inst.currentArrangement, inst.livePositions);
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

export function fitGraph(elementId) {
  const inst = instances.get(elementId); const cy = inst && inst.cy; if (!cy) return;
  cy.animate({ fit: { padding: 30 } }, { duration: 200 });
}

export function zoomByFactor(elementId, factor) {
  const inst = instances.get(elementId); const cy = inst && inst.cy; if (!cy) return;
  cy.animate({ zoom: { level: cy.zoom() * factor, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } } }, { duration: 120 });
}

export function toggleFullscreen(wrapperId) {
  const el = document.getElementById(wrapperId); if (!el) return;
  if (document.fullscreenElement) {
    document.exitFullscreen();
  } else {
    (el.requestFullscreen ? el.requestFullscreen() : (el.webkitRequestFullscreen && el.webkitRequestFullscreen()));
  }
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
    applyCollectionHighlight(cy, focus);
    return;
  }

  const baseCol = cy.collection(
    [...inst.baseIds].map(id => cy.getElementById(id)).filter(el => el.length > 0)
  );

  let display;
  if (inst.combineMode === 'union') {
    display = focus.union(baseCol);
  } else if (inst.combineMode === 'intersection') {
    const focusNodeIds = new Set(focus.nodes().map(n => n.id()));
    const baseNodeIds = new Set(baseCol.nodes().map(n => n.id()));
    const intersect = cy.nodes().filter(n => focusNodeIds.has(n.id()) && baseNodeIds.has(n.id()));
    display = intersect;
  } else {
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
    case 'grid-release': {
      const releaseOrderOf = n => {
        const ro = n.data('releaseOrder');
        return ro !== null && ro !== undefined ? ro : 9999;
      };
      return cy.layout({
        name: 'grid',
        avoidOverlap: true,
        padding: 20,
        fit: true,
        sort: (a, b) =>
          (releaseOrderOf(a) - releaseOrderOf(b)) ||
          String(a.data('label')).localeCompare(String(b.data('label'))),
      });
    }
    case 'grid-name':
      return cy.layout({
        name: 'grid',
        avoidOverlap: true,
        padding: 20,
        fit: true,
        sort: (a, b) => String(a.data('label')).localeCompare(String(b.data('label'))),
      });
    case 'grid-dependents': {
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
    case 'concentric-tier':
      return cy.layout({
        name: 'concentric',
        animate: false,
        fit: true,
        padding: 30,
        minNodeSpacing: 25,
        levelWidth: () => 1,
        concentric: n => 10 - (n.data('tier') || 0),
      });
    case 'breadthfirst':
      return cy.layout({
        name: 'breadthfirst',
        directed: true,
        animate: false,
        fit: true,
        padding: 30,
        spacingFactor: 1.0,
      });
    case 'dagre-lr':
      return cy.layout({
        name: 'dagre',
        rankDir: 'LR',
        nodeSep: 60,
        rankSep: 80,
        padding: 20,
        animate: true,
        animationDuration: 300,
      });
    case 'grid-module': {
      // ── Row: one row per group, sorted by (tier, groupName) ──────────────
      const groupMeta = new Map();
      cy.nodes().forEach(n => {
        const g = n.data('group') || '';
        if (!groupMeta.has(g)) groupMeta.set(g, { tier: n.data('tier') || 0, name: g });
      });
      const sortedGroups = [...groupMeta.values()]
        .sort((a, b) => (a.tier - b.tier) || a.name.localeCompare(b.name))
        .map(m => m.name);
      const rowOf = {};
      sortedGroups.forEach((g, i) => { rowOf[g] = i; });

      // ── Col: role-based, most-specific suffix first ────────────────────────
      const roleOf = (label) => {
        if (label.endsWith('UI.Blazor.MudBlazor')) return 'UI.Blazor.MudBlazor';
        if (label.endsWith('UI.Blazor'))           return 'UI.Blazor';
        if (label.endsWith('UI.Contracts'))        return 'UI.Contracts';
        if (label.endsWith('Presentation'))        return 'Presentation';
        if (label.endsWith('Contracts'))           return 'Contracts';
        if (label.endsWith('Client'))              return 'Client';
        if (label.endsWith('Management'))          return 'Management';
        if (label.endsWith('Aws'))                 return 'Aws';
        if (label.endsWith('Azure'))               return 'Azure';
        if (label.endsWith('SharedKernel'))        return 'SharedKernel';
        if (label.endsWith('.Postgres'))           return 'Postgres';
        if (label.endsWith('.Web'))                return 'Web';
        return 'impl';
      };
      const COL_ORDER = ['SharedKernel', 'impl', 'Postgres', 'Web', 'Contracts', 'Client', 'Management', 'Aws', 'Azure', 'Presentation', 'UI.Contracts', 'UI.Blazor', 'UI.Blazor.MudBlazor'];
      const baseColOf = (label) => { const i = COL_ORDER.indexOf(roleOf(label)); return i < 0 ? COL_ORDER.length : i; };

      // ── Collision guard: per-row track used cols, shift duplicates ────────
      const usedCols = {}; // rowIndex -> Set<col>
      sortedGroups.forEach((_, i) => { usedCols[i] = new Set(); });
      // Assign cols deterministically: sort nodes per row by baseCol then label
      const nodeColAssignment = {}; // nodeId -> col
      const byRow = {};
      cy.nodes().forEach(n => {
        const r = rowOf[n.data('group')] ?? 0;
        (byRow[r] = byRow[r] || []).push(n);
      });
      Object.entries(byRow).forEach(([rowStr, nodes]) => {
        const row = parseInt(rowStr, 10);
        nodes.sort((a, b) => {
          const ca = baseColOf(a.data('label')), cb = baseColOf(b.data('label'));
          return ca !== cb ? ca - cb : String(a.data('label')).localeCompare(String(b.data('label')));
        });
        nodes.forEach(n => {
          let col = baseColOf(n.data('label'));
          while (usedCols[row].has(col)) col++;
          usedCols[row].add(col);
          nodeColAssignment[n.id()] = col;
        });
      });

      return cy.layout({
        name: 'grid',
        fit: true,
        padding: 20,
        avoidOverlap: true,
        rows: sortedGroups.length,
        position: (node) => ({ row: rowOf[node.data('group')] ?? 0, col: nodeColAssignment[node.id()] ?? 0 }),
      });
    }
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
      selector: 'edge[kind = "dangling"]',
      style: {
        'line-color': '#f44336',
        'target-arrow-color': '#f44336',
        'line-style': 'dotted',
        'width': 2.5,
      },
    },
    {
      selector: 'node[?isDangling]',
      style: {
        'border-width': 3,
        'border-color': '#f44336',
        'border-style': 'dashed',
        'background-color': '#b71c1c',
      },
    },
    {
      selector: 'node:selected',
      style: { 'border-width': 4, 'border-color': '#ffeb3b', 'border-opacity': 1, 'overlay-opacity': 0.1 },
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
