// Knowledge graph renderer (vis-network). Graph data is tenant-controlled: tooltips are built with
// textContent and innerHTML is never used with graph data.
import { Network, DataSet } from '../lib/vis-network/standalone/esm/vis-network.min.js';

const NODE_STYLES = {
    WebApp: { shape: 'box', color: '#0078D4' },
    FunctionApp: { shape: 'box', color: '#F2A900' },
    WebAppSlot: { shape: 'box', color: '#69AFE5', size: 'small' },
    ContainerApp: { shape: 'hexagon', color: '#008575' },
    ContainerRegistry: { shape: 'diamond', color: '#004E46' },
    SqlServer: { shape: 'database', color: '#2E7D32' },
    SqlDatabase: { shape: 'database', color: '#66BB6A', size: 'small' },
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

let network = null;
let nodes = null;
let edges = null;
let dotNet = null;
let rawNodes = [];
let rawEdges = [];
let lastFilter = null;

export function init(containerId, dotNetRef) {
    dotNet = dotNetRef;
    const container = document.getElementById(containerId);
    nodes = new DataSet([]);
    edges = new DataSet([]);
    network = new Network(container, { nodes, edges }, baseOptions(false));
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

    network.setOptions(baseOptions(large));
    nodes.clear();
    edges.clear();
    nodes.add(rawNodes.map(toVisNode));
    edges.add(rawEdges.map(toVisEdge));
    if (lastFilter) applyFilter(lastFilter);

    const done = () => {
        if (large) network.setOptions({ physics: false });
        network.fit({ animation: false });
        dotNet?.invokeMethodAsync('OnRenderComplete', rawNodes.length, rawEdges.length);
    };
    if (rawNodes.length === 0) {
        done();
    } else {
        network.once('stabilizationIterationsDone', done);
        network.stabilize(200);
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
            barnesHut: { gravitationalConstant: -6000, springLength: 140, avoidOverlap: 0.2 },
            stabilization: { iterations: 200 }
        },
        nodes: { font: { face: 'Segoe UI, Arial, sans-serif', size: 13 }, borderWidth: 1 },
        edges: { smooth: { type: 'continuous' }, font: { size: 9, color: '#555', strokeWidth: 3, strokeColor: '#ffffff', align: 'middle' } }
    };
}

function tooltip(lines) {
    const el = document.createElement('div');
    el.style.whiteSpace = 'pre';
    el.textContent = lines.filter(Boolean).join('\n');
    return el;
}

function toVisNode(n) {
    const style = NODE_STYLES[n.type] ?? NODE_STYLES.External;
    const inside = LABEL_INSIDE.has(style.shape);
    const node = {
        id: n.id,
        label: n.label,
        title: tooltip([n.label, n.type]),
        shape: style.shape,
        color: style.outline
            ? { background: '#ffffff', border: style.color, highlight: { background: '#f5f5f5', border: '#616161' } }
            : { background: style.color, border: style.color, highlight: { background: style.color, border: '#1f2937' } },
        font: { color: inside && !style.outline ? '#ffffff' : '#1f2937', size: style.size === 'small' ? 11 : 13 },
        margin: style.size === 'small' ? 6 : 10,
        size: style.size === 'small' ? 14 : 20,
        borderWidth: style.outline ? 2 : 1
    };
    if (style.dashed) {
        node.color = { background: '#f3f4f6', border: style.color };
        node.font = { color: '#374151' };
        node.shapeProperties = { borderDashes: [5, 5] };
    }
    return node;
}

function toVisEdge(e) {
    const low = e.data?.confidence === 'Low';
    const edge = { id: e.id, from: e.source, to: e.target, arrows: { to: { enabled: true, scaleFactor: 0.6 } } };
    switch (e.type) {
        case 'CONNECTS_TO':
            Object.assign(edge, { color: { color: '#0078D4' }, width: 2, label: 'CONNECTS_TO' });
            break;
        case 'REFERENCES_SECRET':
            Object.assign(edge, { color: { color: '#C8A400' }, width: 2, dashes: true, label: 'REFERENCES_SECRET' });
            break;
        case 'PULLS_IMAGE_FROM':
            Object.assign(edge, { color: { color: '#008575' }, width: 2, dashes: [2, 4], label: 'PULLS_IMAGE_FROM' });
            break;
        default:
            Object.assign(edge, { color: { color: '#BDBDBD' }, width: 1 });
    }
    if (low) {
        edge.dashes = true;
        edge.color = { ...edge.color, opacity: 0.5 };
    }
    return edge;
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
