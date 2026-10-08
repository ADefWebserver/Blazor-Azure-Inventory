// Knowledge graph renderer (vis-network). Graph data is tenant-controlled: tooltips are built with
// textContent and innerHTML is never used with graph data.
import { Network, DataSet } from '../lib/vis-network/standalone/esm/vis-network.min.js';

// vis-network sizes 'database' shapes as a square around the label, so long names produce huge cylinders.
// A fixed-size SVG cylinder with the label underneath keeps database nodes compact.
function cylinder(fill, top) {
    const svg = `<svg xmlns='http://www.w3.org/2000/svg' width='48' height='56' viewBox='0 0 48 56'>`
        + `<path d='M4 10 V46 A20 8 0 0 0 44 46 V10 Z' fill='${fill}'/>`
        + `<path d='M4 24 A20 8 0 0 0 44 24 M4 36 A20 8 0 0 0 44 36' fill='none' stroke='#ffffff' stroke-opacity='0.35' stroke-width='1.5'/>`
        + `<ellipse cx='24' cy='10' rx='20' ry='8' fill='${top}' stroke='${fill}' stroke-width='1.5'/></svg>`;
    return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
}

const NODE_STYLES = {
    WebApp: { shape: 'box', color: '#0078D4' },
    FunctionApp: { shape: 'box', color: '#F2A900' },
    WebAppSlot: { shape: 'box', color: '#69AFE5', size: 'small' },
    ContainerApp: { shape: 'hexagon', color: '#008575' },
    ContainerRegistry: { shape: 'diamond', color: '#004E46' },
    SqlServer: { shape: 'image', color: '#2E7D32', image: cylinder('#2E7D32', '#4CAF50') },
    SqlDatabase: { shape: 'image', color: '#66BB6A', size: 'small', image: cylinder('#66BB6A', '#A5D6A7') },
    StorageAccount: { shape: 'ellipse', color: '#7B1FA2' },
    BlobContainer: { shape: 'dot', color: '#CE93D8' },
    CosmosAccount: { shape: 'ellipse', color: '#5C6BC0' },
    RedisCache: { shape: 'ellipse', color: '#D32F2F' },
    ServiceBusNamespace: { shape: 'ellipse', color: '#455A64' },
    KeyVault: { shape: 'triangle', color: '#C8A400' },
    External: { shape: 'ellipse', color: '#9E9E9E', dashed: true },
    Tenant: { shape: 'box', color: '#BDBDBD', outline: true },
    Subscription: { shape: 'box', color: '#BDBDBD', outline: true },
    ResourceGroup: { shape: 'box', color: '#BDBDBD', outline: true }
};

const LABEL_INSIDE = new Set(['box', 'ellipse', 'database', 'circle', 'text']);
const STRUCTURAL = new Set(['CONTAINS', 'HAS_SLOT', 'HOSTS_DATABASE', 'HAS_CONTAINER']);
// Node types that have no properties popup.
const NO_DETAILS = new Set(['Tenant']);
const INFO_ICON_RADIUS = 8;
const INFO_ICON_MIN_SCALE = 0.35;
const MAX_LABEL_LENGTH = 28;
// Drop shadows are costly to redraw, so only use them on smaller graphs.
const SHADOW_NODE_LIMIT = 500;
const FONT_FACE = "'Segoe UI', system-ui, -apple-system, Arial, sans-serif";

let network = null;
let container = null;
let nodes = null;
let edges = null;
let dotNet = null;
let rawNodes = [];
let rawEdges = [];
let lastFilter = null;

export function init(containerId, dotNetRef) {
    dotNet = dotNetRef;
    container = document.getElementById(containerId);
    nodes = new DataSet([]);
    edges = new DataSet([]);
    network = new Network(container, { nodes, edges }, baseOptions(false));
    network.on('afterDrawing', drawInfoIcons);
    network.on('click', e => {
        const id = infoIconAt(e.pointer.canvas);
        if (id) dotNet?.invokeMethodAsync('OnNodeOpen', id);
    });
    container.addEventListener('mousemove', onMouseMove);
    network.on('selectNode', e => {
        const id = e.nodes[0];
        highlight(id);
        dotNet?.invokeMethodAsync('OnNodeSelected', id);
    });
    network.on('deselectNode', () => {
        highlight(null);
        dotNet?.invokeMethodAsync('OnNodeSelected', null);
    });
    network.on('doubleClick', e => {
        if (e.nodes.length > 0) dotNet?.invokeMethodAsync('OnNodeOpen', e.nodes[0]);
    });
    network.on('selectEdge', e => {
        // Clicking a node also selects its edges; only report direct edge clicks.
        if (e.nodes.length === 0 && e.edges.length > 0) dotNet?.invokeMethodAsync('OnEdgeSelected', e.edges[0]);
    });
    network.on('deselectEdge', e => {
        if ((e.nodes ?? []).length === 0) dotNet?.invokeMethodAsync('OnEdgeSelected', null);
    });
}

// Keeps the client copy of a node's tags in sync after a tag write, so the tag filter stays accurate.
export function updateNodeTags(id, tags) {
    const node = rawNodes.find(n => n.id === id);
    if (!node) return;
    node.data = { ...(node.data ?? {}), tags: tags ?? {} };
    if (lastFilter) applyFilter(lastFilter);
}

function matchesTag(n, tagName, tagValue) {
    const tags = n.data?.tags;
    if (!tags) return false;
    const wanted = tagName.toLowerCase();
    const overflow = new RegExp('^' + wanted.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '-\\d+$');
    const value = (tagValue ?? '').toLowerCase();
    for (const [k, v] of Object.entries(tags)) {
        const key = k.toLowerCase();
        if (key !== wanted && !overflow.test(key)) continue;
        if (!value || String(v).toLowerCase().includes(value)) return true;
    }
    return false;
}

export async function renderFromStream(streamRef) {
    const buffer = await streamRef.arrayBuffer();
    const graph = JSON.parse(new TextDecoder().decode(buffer));
    rawNodes = graph.nodes ?? [];
    rawEdges = graph.edges ?? [];
    const large = rawNodes.length > 1500;
    const shadow = rawNodes.length <= SHADOW_NODE_LIMIT;

    network.setOptions(baseOptions(large));
    nodes.clear();
    edges.clear();
    nodes.add(rawNodes.map(n => toVisNode(n, shadow)));
    edges.add(rawEdges.map(toVisEdge));
    if (lastFilter) applyFilter(lastFilter);

    // Physics is frozen once the initial layout settles; the user can resume it from the toolbar.
    const done = () => {
        network.setOptions({ physics: { enabled: false } });
        network.fit({ animation: false });
        dotNet?.invokeMethodAsync('OnRenderComplete', rawNodes.length, rawEdges.length);
    };
    if (rawNodes.length === 0) {
        done();
    } else {
        network.once('stabilizationIterationsDone', done);
        network.stabilize(large ? 200 : 400);
    }
}

export function applyFilter(filter) {
    lastFilter = filter;
    if (!nodes) return;
    const types = new Set(filter.nodeTypes ?? []);
    const subs = new Set((filter.subscriptions ?? []).map(s => s.toLowerCase()));
    const groups = new Set((filter.resourceGroups ?? []).map(g => g.toLowerCase()));
    const tagName = (filter.tagName ?? '').trim();

    const visible = new Set();
    for (const n of rawNodes) {
        const d = n.data ?? {};
        let show = types.has(n.type);
        if (show && subs.size > 0 && d.subscriptionId) show = subs.has(String(d.subscriptionId).toLowerCase());
        if (show && groups.size > 0 && d.resourceGroup) show = groups.has(String(d.resourceGroup).toLowerCase());
        if (show && filter.hideExternal && n.type === 'External') show = false;
        if (show && filter.hideEmulator && d.isLocalEmulator === true) show = false;
        if (show && tagName) show = matchesTag(n, tagName, filter.tagValue);
        if (show) visible.add(n.id);
    }

    const visibleEdges = new Set();
    for (const e of rawEdges) {
        let show = visible.has(e.source) && visible.has(e.target);
        if (show && !filter.showLowConfidence && e.data?.confidence === 'Low') show = false;
        if (show) visibleEdges.add(e.id);
    }

    if (filter.onlyConnected) {
        const connected = new Set();
        for (const e of rawEdges) {
            if (visibleEdges.has(e.id) && !STRUCTURAL.has(e.type)) {
                connected.add(e.source);
                connected.add(e.target);
            }
        }
        for (const id of [...visible]) if (!connected.has(id)) visible.delete(id);
        for (const e of rawEdges) if (!visible.has(e.source) || !visible.has(e.target)) visibleEdges.delete(e.id);
    }

    nodes.update(rawNodes.map(n => ({ id: n.id, hidden: !visible.has(n.id) })));
    edges.update(rawEdges.map(e => ({ id: e.id, hidden: !visibleEdges.has(e.id) })));
}

export function focusNode(id) {
    if (!network || !nodes.get(id)) return false;
    nodes.update({ id, hidden: false });
    network.selectNodes([id]);
    network.focus(id, { scale: 1.2, animation: { duration: 600, easingFunction: 'easeInOutQuad' } });
    highlight(id);
    return true;
}

export function fit() {
    network?.fit({ animation: { duration: 400 } });
}

export function setPhysics(enabled) {
    network?.setOptions({ physics: { enabled } });
}

export function dispose() {
    container?.removeEventListener('mousemove', onMouseMove);
    container = null;
    network?.destroy();
    network = null;
    nodes = null;
    edges = null;
    dotNet = null;
    rawNodes = [];
    rawEdges = [];
}

function baseOptions(large) {
    return {
        autoResize: true,
        layout: { improvedLayout: !large },
        interaction: { hover: true, tooltipDelay: 200, navigationButtons: false },
        physics: {
            enabled: true,
            solver: 'barnesHut',
            barnesHut: { gravitationalConstant: -9000, centralGravity: 0.25, springLength: 170, springConstant: 0.03, avoidOverlap: 0.6 },
            stabilization: { iterations: large ? 200 : 400 }
        },
        nodes: {
            font: { face: FONT_FACE, size: 13 },
            borderWidth: 1,
            // Hide labels once they become too small to read when zoomed out.
            scaling: { label: { drawThreshold: 7 } }
        },
        edges: {
            smooth: large ? false : { type: 'continuous' },
            selectionWidth: 1.5,
            hoverWidth: 0.5,
            font: { face: FONT_FACE, size: 9, color: '#4b5563', strokeWidth: 3, strokeColor: '#ffffff', align: 'middle' },
            scaling: { label: { drawThreshold: 8 } }
        }
    };
}

function tooltip(label, type, hint) {
    const el = document.createElement('div');
    el.className = 'graph-tip';
    const parts = [[label, 'graph-tip-title'], [type, 'graph-tip-type'], [hint, 'graph-tip-hint']];
    for (const [text, cls] of parts) {
        if (!text) continue;
        const line = document.createElement('div');
        line.className = cls;
        line.textContent = text;
        el.appendChild(line);
    }
    return el;
}

function shortLabel(label) {
    const text = String(label ?? '');
    return text.length > MAX_LABEL_LENGTH ? text.slice(0, MAX_LABEL_LENGTH - 1) + '\u2026' : text;
}

function toVisNode(n, shadow) {
    const style = NODE_STYLES[n.type] ?? NODE_STYLES.External;
    const inside = LABEL_INSIDE.has(style.shape);
    const small = style.size === 'small';
    const node = {
        id: n.id,
        label: shortLabel(n.label),
        title: tooltip(n.label, n.type, NO_DETAILS.has(n.type) ? null : 'Click the (i) icon or double-click for properties'),
        shape: style.shape,
        color: style.outline
            ? { background: '#ffffff', border: style.color, highlight: { background: '#f5f5f5', border: '#616161' }, hover: { background: '#fafafa', border: '#616161' } }
            : { background: style.color, border: style.color, highlight: { background: style.color, border: '#1f2937' }, hover: { background: style.color, border: '#1f2937' } },
        font: inside && !style.outline
            ? { color: '#ffffff', size: small ? 11 : 13 }
            // Labels drawn outside the shape get a halo so they stay readable over edges.
            : { color: '#1f2937', size: small ? 11 : 12, strokeWidth: 3, strokeColor: 'rgba(255,255,255,0.9)' },
        margin: small ? { top: 5, bottom: 5, left: 8, right: 8 } : { top: 8, bottom: 8, left: 12, right: 12 },
        size: small ? 14 : 20,
        borderWidth: style.outline ? 2 : 1,
        borderWidthSelected: 3,
        shapeProperties: { borderRadius: 6 },
        shadow: shadow ? { enabled: true, color: 'rgba(15,23,42,0.18)', size: 6, x: 0, y: 2 } : false
    };
    if (style.image) node.image = style.image;
    if (style.dashed) {
        node.color = { background: '#f3f4f6', border: style.color, highlight: { background: '#e5e7eb', border: '#4b5563' }, hover: { background: '#e5e7eb', border: '#4b5563' } };
        node.font = { color: '#374151', size: 12 };
        node.shapeProperties = { borderDashes: [5, 5], borderRadius: 6 };
    }
    return node;
}

function toVisEdge(e) {
    const low = e.data?.confidence === 'Low';
    const edge = { id: e.id, from: e.source, to: e.target, arrows: { to: { enabled: true, scaleFactor: 0.6 } } };
    switch (e.type) {
        case 'CONNECTS_TO':
            Object.assign(edge, { color: { color: '#0078D4', highlight: '#005A9E', hover: '#005A9E', opacity: 0.7 }, width: 1.5, label: 'CONNECTS_TO' });
            break;
        case 'REFERENCES_SECRET':
            Object.assign(edge, { color: { color: '#C8A400', highlight: '#8A7100', hover: '#8A7100', opacity: 0.8 }, width: 1.5, dashes: true, label: 'REFERENCES_SECRET' });
            break;
        case 'PULLS_IMAGE_FROM':
            Object.assign(edge, { color: { color: '#008575', highlight: '#004E46', hover: '#004E46', opacity: 0.8 }, width: 1.5, dashes: [2, 4], label: 'PULLS_IMAGE_FROM' });
            break;
        default:
            Object.assign(edge, { color: { color: '#CBD5E1', highlight: '#64748B', hover: '#94A3B8' }, width: 1 });
    }
    if (low) {
        edge.dashes = true;
        edge.color = { ...edge.color, opacity: 0.5 };
    }
    return edge;
}

// Canvas-space centre of the info icon for every visible node that has a properties popup.
function infoIconPositions() {
    if (!network || !nodes || network.getScale() < INFO_ICON_MIN_SCALE) return [];
    const result = [];
    for (const n of rawNodes) {
        if (NO_DETAILS.has(n.type)) continue;
        const vis = nodes.get(n.id);
        if (!vis || vis.hidden) continue;
        const style = NODE_STYLES[n.type] ?? NODE_STYLES.External;
        let x, y;
        if (LABEL_INSIDE.has(style.shape)) {
            const box = network.getBoundingBox(n.id);
            if (!box) continue;
            x = box.right - 2;
            y = box.top + 2;
        } else {
            // Bounding boxes of these shapes include the label below them, so anchor to the shape itself.
            const pos = network.getPositions([n.id])[n.id];
            if (!pos) continue;
            const r = vis.size ?? 20;
            x = pos.x + r * 0.85;
            y = pos.y - r * 0.85;
        }
        result.push({ id: n.id, x, y, opacity: vis.opacity ?? 1 });
    }
    return result;
}

function drawInfoIcons(ctx) {
    const icons = infoIconPositions();
    if (icons.length === 0) return;
    ctx.save();
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.font = `bold ${INFO_ICON_RADIUS * 1.4}px Georgia, 'Times New Roman', serif`;
    for (const icon of icons) {
        ctx.globalAlpha = icon.opacity;
        ctx.beginPath();
        ctx.arc(icon.x, icon.y, INFO_ICON_RADIUS, 0, Math.PI * 2);
        ctx.fillStyle = '#ffffff';
        ctx.fill();
        ctx.lineWidth = 1.5;
        ctx.strokeStyle = '#0078D4';
        ctx.stroke();
        ctx.fillStyle = '#0078D4';
        ctx.fillText('i', icon.x, icon.y + 1);
    }
    ctx.restore();
}

function infoIconAt(point) {
    if (!point) return null;
    const hit = INFO_ICON_RADIUS + 2;
    const icons = infoIconPositions();
    // Topmost (last drawn) icon wins when icons overlap.
    for (let i = icons.length - 1; i >= 0; i--) {
        const dx = point.x - icons[i].x;
        const dy = point.y - icons[i].y;
        if (dx * dx + dy * dy <= hit * hit) return icons[i].id;
    }
    return null;
}

function onMouseMove(e) {
    if (!network || !container) return;
    const rect = container.getBoundingClientRect();
    const point = network.DOMtoCanvas({ x: e.clientX - rect.left, y: e.clientY - rect.top });
    const canvas = container.querySelector('canvas');
    if (canvas) canvas.style.cursor = infoIconAt(point) ? 'pointer' : '';
}

function highlight(id) {
    if (!nodes) return;
    if (!id) {
        nodes.update(rawNodes.map(n => ({ id: n.id, opacity: 1 })));
        return;
    }
    const keep = new Set([id, ...network.getConnectedNodes(id)]);
    nodes.update(rawNodes.map(n => ({ id: n.id, opacity: keep.has(n.id) ? 1 : 0.25 })));
}
