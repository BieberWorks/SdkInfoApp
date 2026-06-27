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

// Map of elementId → { cy, persistKey, layoutName }
const instances = new Map();

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
    const nodeId = evt.target.id();
    dotNetRef.invokeMethodAsync('NodeClickedAsync', nodeId);
    cy.elements().removeClass('highlighted dimmed');
    evt.target.addClass('highlighted');
    evt.target.neighborhood().addClass('highlighted');
    // Also highlight edges between highlighted nodes
    cy.edges().forEach(e => {
      if (e.source().hasClass('highlighted') && e.target().hasClass('highlighted'))
        e.addClass('highlighted');
    });
    cy.elements().not('.highlighted').addClass('dimmed');
  });

  cy.on('tap', (evt) => {
    if (evt.target === cy) {
      cy.elements().removeClass('highlighted dimmed');
      dotNetRef.invokeMethodAsync('BackgroundTappedAsync');
    }
  });

  instances.set(elementId, { cy, persistKey, layoutName: layoutName || 'dagre' });
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

// ─── Internal helpers ─────────────────────────────────────────────────────────

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
      style: { 'opacity': 0.25 },
    },
  ];
}
