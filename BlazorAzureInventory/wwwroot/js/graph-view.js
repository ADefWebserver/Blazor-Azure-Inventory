import cytoscape from '../lib/cytoscape/dist/cytoscape.esm.min.mjs';
import { iconFor, legendEntries, loadIcons } from './graph-icons.js';

const TYPE_COLORS = {
    WebApp: '#0078D4',
    FunctionApp: '#F2A900',
    WebAppSlot: '#69AFE5',
    ContainerApp: '#008575',
    ContainerRegistry: '#004E46',
    SqlServer: '#2E7D32',
    SqlDatabase: '#66BB6A',
    StorageAccount: '#7B1FA2',
    BlobContainer: '#CE93D8',
    StorageQueue: '#AB47BC',
    CosmosAccount: '#5C6BC0',
    RedisCache: '#D32F2F',
    ServiceBusNamespace: '#455A64',
    KeyVault: '#C8A400',
    ExternalService: '#607D8B',
    External: '#9E9E9E'
};
const EDGE_COLORS = {
    CONNECTS_TO: '#0078D4',
    REFERENCES_SECRET: '#C8A400',
    PULLS_IMAGE_FROM: '#008575'
};
const MAX_LABEL_LENGTH = 28;
const HEX_COLOR = /^#[0-9a-f]{6}$/i;

let cy = null;
let container = null;
let dotNet = null;
let payload = null;
let lastFilter = null;
let tagMatches = null;
let selectedNode = null;
let highlighted = null;
let faded = null;
let hoverMax = 4000;
let logTimings = false;
let legendId = null;

export function init(containerId, dotNetRef, options) {
    container = document.getElementById(containerId);
    dotNet = dotNetRef;
    hoverMax = Number.isInteger(options?.hoverMax) ? options.hoverMax : hoverMax;
    logTimings = options?.timings === true;
    legendId = typeof options?.legendId === 'string' ? options.legendId : null;

    const wantsWebGl = options?.webgl === true;
    let usedWebGl = wantsWebGl;
    try {
        if (wantsWebGl && !webGlAvailable()) usedWebGl = false;
        cy = createCytoscape(usedWebGl);
    } catch (error) {
        if (!wantsWebGl || !usedWebGl) throw error;
        usedWebGl = false;
        cy = createCytoscape(false);
    }
    if (wantsWebGl && !usedWebGl) dotNet?.invokeMethodAsync('OnWebGlFallback');

    cy.on('tap', 'node', event => {
        const node = event.target;
        selectedNode = node.id();
        highlightNeighbourhood(node);
        dotNet?.invokeMethodAsync('OnNodeSelected', node.data('index'));
    });
    cy.on('dbltap', 'node', event => {
        dotNet?.invokeMethodAsync('OnNodeOpen', event.target.data('index'));
    });
    cy.on('tap', 'edge', event => {
        dotNet?.invokeMethodAsync('OnEdgeSelected', event.target.data('index'));
    });
    cy.on('tap', event => {
        if (event.target === cy) {
            selectedNode = null;
            clearHighlight();
            dotNet?.invokeMethodAsync('OnNodeSelected', null);
            dotNet?.invokeMethodAsync('OnEdgeSelected', null);
        }
    });
    cy.on('mouseover', 'node', event => {
        if (cy.elements(':visible').length > hoverMax) return;
        highlightNeighbourhood(event.target);
    });
    cy.on('mouseout', 'node', () => {
        if (selectedNode && cy.getElementById(selectedNode).length) highlightNeighbourhood(cy.getElementById(selectedNode));
        else clearHighlight();
    });
}

export async function render(streamRef) {
    const start = performance.now();
    const buffer = await streamRef.arrayBuffer();
    const transferEnd = performance.now();
    const parsed = JSON.parse(new TextDecoder().decode(buffer));
    const parseEnd = performance.now();
    await loadIcons();
    payload = parsed;
    buildLegend(parsed);
    selectedNode = null;
    highlighted = cy.collection();
    faded = cy.collection();

    const elements = toElements(parsed);
    cy.batch(() => {
        cy.elements().remove();
        cy.add(elements);
    });
    if (lastFilter) applyFilter(lastFilter, tagMatches);

    if (parsed.nodes.length === 0) {
        dotNet?.invokeMethodAsync('OnRenderComplete', 0, 0, performance.now() - start);
        return;
    }

    if (parsed.mode === 'Focus') {
        const positions = focusPositions(parsed);
        cy.batch(() => {
            cy.nodes().forEach(node => {
                const position = positions[node.data('index')];
                if (position && !node.isParent()) node.position(position);
            });
        });
    }
    else {
        const layout = cy.layout({ name: 'preset', fit: false });
        await new Promise(resolve => {
            layout.one('layoutstop', resolve);
            layout.run();
        });
    }
    const layoutEnd = performance.now();
    cy.fit(cy.elements(':visible'), 40);
    if (logTimings) {
        console.info(`[graph] ${parsed.nodes.length} nodes, ${parsed.edges.length} edges, ${buffer.byteLength} bytes: `
            + `transfer ${ms(transferEnd - start)}, parse ${ms(parseEnd - transferEnd)}, `
            + `add ${ms(layoutEnd - parseEnd)}, layout ${ms(performance.now() - layoutEnd)}, `
            + `total ${ms(performance.now() - start)}`);
    }
    dotNet?.invokeMethodAsync('OnRenderComplete', parsed.nodes.length, parsed.edges.length, performance.now() - start);
}

export function applyFilter(filter, matchingTagIndexes = null) {
    lastFilter = filter;
    tagMatches = matchingTagIndexes;
    if (!cy || !payload) return;
    const start = performance.now();
    const types = new Set(filter.nodeTypes ?? []);
    const subscriptions = new Set((filter.subscriptions ?? []).map(value => value.toLowerCase()));
    const groups = new Set((filter.resourceGroups ?? []).map(value => value.toLowerCase()));
    const matchingTags = matchingTagIndexes === null ? null : new Set(matchingTagIndexes);
    const hasTagFilter = String(filter.tagName ?? '').trim().length > 0;
    const visible = new Set();

    for (let i = 0; i < payload.nodes.length; i++) {
        const node = payload.nodes[i];
        let show = true;
        if (payload.mode !== 'Focus') {
            show = types.has(node.t);
            if (show && subscriptions.size && node.s != null)
                show = subscriptions.has(String(payload.subs[node.s]).toLowerCase());
            if (show && groups.size && node.g != null)
                show = groups.has(String(payload.rgs[node.g]).toLowerCase());
            if (show && filter.hideExternal && node.t === 'External') show = false;
            if (show && filter.hideEmulator && node.emu === true) show = false;
            if (show && hasTagFilter) show = matchingTags?.has(i) === true;
        }
        if (show) visible.add(i);
    }

    const visibleEdges = new Set();
    for (let i = 0; i < payload.edges.length; i++) {
        const edge = payload.edges[i];
        let show = visible.has(edge.s) && visible.has(edge.t);
        if (show && !filter.showLowConfidence && edge.lo === true) show = false;
        if (show) visibleEdges.add(i);
    }

    if (payload.mode !== 'Focus' && filter.onlyConnected) {
        const connected = new Set();
        for (let i = 0; i < payload.edges.length; i++) {
            const edge = payload.edges[i];
            if (visibleEdges.has(i)) {
                connected.add(edge.s);
                connected.add(edge.t);
            }
        }
        for (const index of visible) if (!connected.has(index)) visible.delete(index);
        for (let i = 0; i < payload.edges.length; i++) {
            const edge = payload.edges[i];
            if (!visible.has(edge.s) || !visible.has(edge.t)) visibleEdges.delete(i);
        }
    }

    cy.batch(() => {
        cy.nodes().forEach(node => node.toggleClass('off', !visible.has(node.data('index'))));
        cy.edges().forEach(edge => edge.toggleClass('off', !visibleEdges.has(edge.data('index'))));
    });
    dotNet?.invokeMethodAsync('OnVisibleCounts', visible.size, visibleEdges.size);
    const elapsed = performance.now() - start;
    if (logTimings) console.info(`[graph] applyFilter ${visible.size}/${payload.nodes.length} nodes visible in ${ms(elapsed)}`);
}

export function center(index) {
    const node = cy?.getElementById(nodeId(index));
    if (!node?.length) return false;
    node.removeClass('off');
    selectedNode = node.id();
    node.select();
    highlightNeighbourhood(node);
    cy.animate({ center: { eles: node }, zoom: Math.max(cy.zoom(), 1.1), duration: 350 });
    dotNet?.invokeMethodAsync('OnNodeSelected', index);
    return true;
}

export function highlightChanges(addedIndexes, changedIndexes) {
    if (!cy) return;
    const added = new Set(addedIndexes ?? []);
    const changed = new Set(changedIndexes ?? []);
    cy.batch(() => cy.nodes().forEach(node => {
        const index = node.data('index');
        node.toggleClass('added', added.has(index));
        node.toggleClass('changed', !added.has(index) && changed.has(index));
    }));
}

export function fit() {
    if (cy) cy.animate({ fit: { eles: cy.elements(':visible'), padding: 40 }, duration: 300 });
}

export function copyText(text) {
    return navigator.clipboard?.writeText(text).then(() => true, () => false) ?? Promise.resolve(false);
}

export function dispose() {
    cy?.destroy();
    cy = null;
    container = null;
    dotNet = null;
    payload = null;
    lastFilter = null;
    tagMatches = null;
    selectedNode = null;
    highlighted = null;
    faded = null;
}

function createCytoscape(webgl) {
    return cytoscape({
        container,
        renderer: { name: 'canvas', webgl },
        textureOnViewport: false,
        hideEdgesOnViewport: !webgl,
        pixelRatio: 'auto',
        minZoom: 0.05,
        maxZoom: 3,
        wheelSensitivity: 0.2,
        style: buildStyle()
    });
}

function buildLegend(view) {
    const list = legendId ? document.getElementById(legendId) : null;
    if (!list) return;
    const present = new Set(view.nodes.map(node => node.t));
    list.replaceChildren(...legendEntries()
        .filter(entry => present.has(entry.type))
        .map(entry => {
            const item = document.createElement('li');
            const badge = document.createElement('span');
            badge.className = 'graph-legend-badge';
            badge.style.background = TYPE_COLORS[entry.type] ?? '#9E9E9E';
            if (entry.uri) {
                const image = document.createElement('img');
                image.src = entry.uri;
                image.alt = '';
                image.width = 12;
                image.height = 12;
                badge.append(image);
            }
            const label = document.createElement('span');
            label.textContent = entry.label;
            item.append(badge, label);
            return item;
        }));
}

function webGlAvailable() {
    const probe = document.createElement('canvas');
    const context = probe.getContext('webgl2') || probe.getContext('webgl');
    if (!context) return false;
    context.getExtension('WEBGL_lose_context')?.loseContext();
    return true;
}

function buildStyle() {
    return [
        {
            selector: 'node',
            style: {
                shape: 'ellipse',
                width: 'data(size)',
                height: 'data(size)',
                'background-color': 'data(color)',
                'background-image': 'data(icon)',
                'background-fit': 'contain',
                'background-width': '55%',
                'background-height': '55%',
                'background-clip': 'node',
                'border-width': 1,
                'border-color': '#ffffff',
                label: 'data(label)',
                color: '#1f2937',
                'font-family': '"Segoe UI", system-ui, sans-serif',
                'font-size': 11,
                'font-weight': 500,
                'text-valign': 'bottom',
                'text-halign': 'center',
                'text-margin-y': 8,
                'text-wrap': 'ellipsis',
                'text-max-width': 150,
                'min-zoomed-font-size': 9,
                'text-outline-width': 2,
                'text-outline-color': 'rgba(255,255,255,0.9)'
            }
        },
        {
            selector: 'node:parent',
            style: {
                shape: 'round-rectangle',
                padding: 24,
                'background-color': '#e2e8f0',
                'background-opacity': 0.4,
                'background-image': 'none',
                'border-color': '#94a3b8',
                'border-width': 1,
                'text-valign': 'top',
                'text-halign': 'center',
                'text-margin-y': -6
            }
        },
        {
            selector: 'node:child',
            style: { 'text-max-width': 120 }
        },
        {
            selector: 'node[?aggregate]',
            style: {
                'background-color': '#64748b',
                'border-style': 'dashed',
                'font-size': 10
            }
        },
        {
            selector: 'edge',
            style: {
                width: 'data(width)',
                'line-color': 'data(edgeColor)',
                'target-arrow-color': 'data(edgeColor)',
                'target-arrow-shape': 'triangle',
                'arrow-scale': 0.8,
                'curve-style': 'straight',
                opacity: 'data(opacity)'
            }
        },
        { selector: '.off', style: { display: 'none' } },
        {
            selector: '.faded',
            style: { opacity: 0.12, 'text-opacity': 0.12 }
        },
        {
            selector: '.hl',
            style: { opacity: 1, 'text-opacity': 1, 'z-index': 10 }
        },
        {
            selector: '.show-label',
            style: { 'font-size': 12, 'min-zoomed-font-size': 0 }
        },
        { selector: '.added', style: { 'border-width': 4, 'border-color': '#16a34a' } },
        { selector: '.changed', style: { 'border-width': 4, 'border-color': '#ea580c' } }
    ];
}

// Focus layout: the focused node in the centre and every other top-level unit (a node, or a parent with its children
// packed into a grid) on concentric rings sized so units and their labels never overlap. Children are positioned
// relative to their parent; the parent's box then follows its children.
function focusPositions(view) {
    const FOCUS_SIZE = 72;
    const NODE_SIZE = 40;
    const CELL_W = 130;
    const CELL_H = 70;
    const PARENT_PADDING = 24;
    const GAP = 40;
    const positions = new Array(view.nodes.length);
    const childrenOf = new Map();
    view.nodes.forEach((node, index) => {
        if (Number.isInteger(node.p) && node.p !== index) {
            if (!childrenOf.has(node.p)) childrenOf.set(node.p, []);
            childrenOf.get(node.p).push(index);
        }
    });
    const isChild = index => Number.isInteger(view.nodes[index].p) && view.nodes[index].p !== index;

    const units = [];
    for (let index = 0; index < view.nodes.length; index++) {
        if (index === view.focus || isChild(index)) continue;
        const children = childrenOf.get(index) ?? [];
        let width = Math.max(NODE_SIZE, 110);
        let height = NODE_SIZE + 30;
        let columns = 0;
        if (children.length) {
            columns = Math.ceil(Math.sqrt(children.length));
            width = columns * CELL_W + 2 * PARENT_PADDING;
            height = Math.ceil(children.length / columns) * CELL_H + 2 * PARENT_PADDING + 20;
        }
        units.push({ index, children, columns, diameter: Math.hypot(width, height) * 0.8 + GAP });
    }

    positions[view.focus] = { x: 0, y: 0 };
    let centreRadius = FOCUS_SIZE / 2;
    const focusChildren = childrenOf.get(view.focus) ?? [];
    if (focusChildren.length) {
        const columns = Math.ceil(Math.sqrt(focusChildren.length));
        const rows = Math.ceil(focusChildren.length / columns);
        focusChildren.forEach((child, i) => {
            positions[child] = {
                x: ((i % columns) - (columns - 1) / 2) * CELL_W,
                y: (Math.floor(i / columns) - (rows - 1) / 2) * CELL_H + 10
            };
        });
        centreRadius = Math.hypot(columns * CELL_W, rows * CELL_H) / 2 + PARENT_PADDING;
    }
    let radius = centreRadius + (units[0]?.diameter ?? 0) / 2 + 60;
    let ring = 0;
    for (let next = 0; next < units.length; ring++) {
        const members = [];
        let used = 0;
        let thickness = 0;
        while (next < units.length && (members.length === 0 || used + units[next].diameter <= 2 * Math.PI * radius)) {
            members.push(units[next]);
            used += units[next].diameter;
            thickness = Math.max(thickness, units[next].diameter);
            next++;
        }
        radius = Math.max(radius, used / (2 * Math.PI));
        let angle = -Math.PI / 2 + ring * 0.6;
        for (const unit of members) {
            const share = (unit.diameter / used) * 2 * Math.PI;
            const theta = angle + share / 2;
            const x = radius * Math.cos(theta);
            const y = radius * Math.sin(theta);
            angle += share;
            positions[unit.index] = { x, y };
            unit.children.forEach((child, i) => {
                const rows = Math.ceil(unit.children.length / unit.columns);
                positions[child] = {
                    x: x + ((i % unit.columns) - (unit.columns - 1) / 2) * CELL_W,
                    y: y + (Math.floor(i / unit.columns) - (rows - 1) / 2) * CELL_H + 10
                };
            });
        }
        radius += thickness;
    }
    return positions;
}

function toElements(view) {
    const nodes = view.nodes.map((node, index) => {
        const color = validColor(node.c) ? node.c : (TYPE_COLORS[node.t] ?? TYPE_COLORS.External);
        const focus = view.mode === 'Focus';
        const size = focus
            ? (index === view.focus ? 72 : 40)
            : node.t === 'ExternalService' ? 72 : Math.max(24, Math.min(64, 24 + 6 * Math.sqrt(node.d ?? 0)));
        let label = node.l ?? '';
        if (node.k) label += ` (+${node.k})`;
        if (node.m) label += ` (+${node.m})`;
        if (node.agg) label = label || '+ more';
        return {
            group: 'nodes',
            data: {
                id: nodeId(index),
                index,
                type: node.t,
                label: shortLabel(label),
                color,
                size,
                icon: iconFor(node.t, node.t === 'External' ? '#374151' : '#ffffff'),
                category: node.cat,
                aggregate: node.agg === true,
                parent: Number.isInteger(node.p) && node.p !== index ? nodeId(node.p) : undefined
            },
            position: {
                x: Number.isFinite(node.x) ? node.x : Math.cos(index * 2.39996) * 400,
                y: Number.isFinite(node.y) ? node.y : Math.sin(index * 2.39996) * 400
            }
        };
    });
    const edges = view.edges.flatMap((edge, index) => {
        // A parent box already shows containment; Cytoscape draws edges between a compound parent and its own child as loops.
        if (view.nodes[edge.s]?.p === edge.t || view.nodes[edge.t]?.p === edge.s) return [];
        return [{
            group: 'edges',
            data: {
                id: `edge-${index}`,
                index,
                source: nodeId(edge.s),
                target: nodeId(edge.t),
                edgeColor: EDGE_COLORS[edge.y] ?? '#94a3b8',
                width: view.mode === 'Focus'
                    ? 2
                    : Math.max(1, Math.min(4, 1 + Math.log2(Math.max(1, edge.n ?? 1)))),
                opacity: edge.lo ? 0.35 : 0.85
            }
        }];
    });
    return [...nodes, ...edges];
}

function highlightNeighbourhood(node) {
    if (!cy || !node?.length) return;
    const nearby = node.closedNeighborhood().filter(':visible').union(node);
    const nextFaded = cy.elements(':visible').not(nearby);
    cy.batch(() => {
        faded.not(nextFaded).removeClass('faded');
        nextFaded.not(faded).addClass('faded');
        highlighted.not(nearby).removeClass('hl show-label');
        nearby.not(highlighted).addClass('hl show-label');
    });
    faded = nextFaded;
    highlighted = nearby;
}

function clearHighlight() {
    if (!cy) return;
    cy.batch(() => {
        faded.removeClass('faded');
        highlighted.removeClass('hl show-label');
    });
    faded = cy.collection();
    highlighted = cy.collection();
}

function nodeId(index) {
    return `node-${index}`;
}

function validColor(value) {
    return typeof value === 'string' && HEX_COLOR.test(value);
}

function shortLabel(value) {
    const text = String(value ?? '');
    return text.length > MAX_LABEL_LENGTH ? `${text.slice(0, MAX_LABEL_LENGTH - 1)}\u2026` : text;
}

function ms(value) {
    return `${value.toFixed(0)} ms`;
}
