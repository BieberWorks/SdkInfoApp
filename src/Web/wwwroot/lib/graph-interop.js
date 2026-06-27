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

// Map of elementId → { cy, persistKey, layoutName, clickDirection, clickTransitive, lastTappedId }
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
  cy.on('dragfree', 'node', () => savePositions(persistKey, cy));

  cy.on('tap', 'node', (evt) => {
    const inst2 = instances.get(elementId);
    if (!inst2) return;
    const t = evt.target;
    inst2.lastTappedId = t.id();
    dotNetRef.invokeMethodAsync('NodeClickedAsync', t.id());
    applyDirectedHighlight(cy, t, inst2.clickDirection, inst2.clickTransitive);
  });

  cy.on('tap', (evt) => {
    if (evt.target === cy) {
      const inst2 = instances.get(elementId);
      if (inst2) inst2.lastTappedId = null;
      cy.elements().removeClass('highlighted dimmed');
      dotNetRef.invokeMethodAsync('BackgroundTappedAsync');
    }
  });

  instances.set(elementId, {
    cy,
    persistKey,
    layoutName: layoutName || 'dagre',
    clickDirection: 'both',
    clickTransitive: false,
    lastTappedId: null,
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
  const { cy } = inst;
  cy.elements().removeClass('highlighted dimmed');
  const idSet = new Set(ids);
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

export function setClickMode(elementId, direction, transitive) {
  const inst = instances.get(elementId);
  if (!inst) return;
  inst.clickDirection = direction;
  inst.clickTransitive = transitive;
  // Re-apply highlight if a node was previously tapped
  if (inst.lastTappedId) {
    const node = inst.cy.getElementById(inst.lastTappedId);
    if (node && node.length > 0) {
      applyDirectedHighlight(inst.cy, node, direction, transitive);
    }
  }
}

export function setDimOpacity(elementId, value) {
  dimOpacity = value;
  try { localStorage.setItem(DIM_OPACITY_KEY, String(value)); } catch { }
  // Apply live to the current instance
  const inst = instances.get(elementId);
  if (inst) {
    inst.cy.style().selector('.dimmed').style('opacity', value).update();
  }
}

// ─── Internal helpers ─────────────────────────────────────────────────────────

function applyDirectedHighlight(cy, node, direction, transitive) {
  cy.elements().removeClass('highlighted dimmed');
  let hl;
  if (direction === 'depends') {
    // Nodes that this node depends on (outgoing direction: successors)
    hl = transitive ? node.union(node.successors()) : node.union(node.outgoers());
  } else if (direction === 'used') {
    // Nodes that use/depend on this node (incoming direction: predecessors)
    hl = transitive ? node.union(node.predecessors()) : node.union(node.incomers());
  } else {
    // both: immediate neighborhood or full transitive in both directions
    hl = transitive
      ? node.union(node.successors()).union(node.predecessors())
      : node.closedNeighborhood();
  }
  hl.addClass('highlighted');
  cy.elements().not('.highlighted').addClass('dimmed');
}

function buildLayout(cy, layoutName) {
  if (layoutName === 'cose') {
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
  }
  // default: dagre
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
