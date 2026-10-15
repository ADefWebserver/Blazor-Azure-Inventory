const ICON_NAMES = {
    WebApp: 'globe',
    WebAppSlot: 'globe',
    FunctionApp: 'flash',
    ContainerApp: 'cube',
    ContainerRegistry: 'box_multiple',
    SqlServer: 'server',
    SqlDatabase: 'database',
    StorageAccount: 'storage',
    BlobContainer: 'folder',
    StorageQueue: 'list',
    CosmosAccount: 'data_usage',
    RedisCache: 'top_speed',
    ServiceBusNamespace: 'arrow_routing',
    KeyVault: 'key',
    ExternalService: 'cloud',
    External: 'link',
    Tenant: 'building',
    Subscription: 'receipt',
    ResourceGroup: 'group'
};

const PRODUCT_NAMES = {
    WebApp: 'App Service web app',
    WebAppSlot: 'App Service slot',
    FunctionApp: 'Azure Functions',
    ContainerApp: 'Container Apps',
    ContainerRegistry: 'Container Registry',
    SqlServer: 'SQL server',
    SqlDatabase: 'SQL database',
    StorageAccount: 'Storage account',
    BlobContainer: 'Blob container',
    StorageQueue: 'Storage queue',
    CosmosAccount: 'Cosmos DB',
    RedisCache: 'Cache for Redis',
    ServiceBusNamespace: 'Service Bus',
    KeyVault: 'Key Vault',
    ExternalService: 'External service',
    External: 'External',
    Tenant: 'Tenant',
    Subscription: 'Subscription',
    ResourceGroup: 'Resource group'
};

const ICONS = new Map();
let loadTask = null;

export function loadIcons() {
    if (!loadTask) {
        loadTask = Promise.all([...new Set(Object.values(ICON_NAMES)), 'circle'].map(async name => {
            const path = new URL(`../lib/fluent-icons/icons/${name}_24_regular.svg`, import.meta.url);
            try {
                const response = await fetch(path);
                if (!response.ok) throw new Error(`HTTP ${response.status}`);
                const svg = await response.text();
                const recolored = svg.replace(/<svg\b([^>]*)>/, (_, attributes) => {
                    const withoutFill = attributes.replace(/\sfill=(["']).*?\1/i, '');
                    return `<svg${withoutFill} fill="${name === 'link' ? '#374151' : '#ffffff'}">`;
                });
                ICONS.set(name, `data:image/svg+xml;charset=utf-8,${encodeURIComponent(recolored)}`);
            } catch (error) {
                console.warn(`Graph icon "${name}" could not be loaded; using a plain node`, error);
            }
        }));
    }
    return loadTask;
}

export function iconFor(type, color = '#ffffff') {
    const name = ICON_NAMES[type] ?? 'circle';
    const uri = ICONS.get(name) ?? ICONS.get('circle');
    if (!uri || color === '#ffffff' || name === 'link') return uri ?? '';
    return uri.replaceAll(encodeURIComponent('#ffffff'), encodeURIComponent(color));
}

export function legendEntries() {
    return Object.entries(ICON_NAMES).map(([type, name]) => ({
        type,
        label: PRODUCT_NAMES[type] ?? type,
        uri: ICONS.get(name) ?? ICONS.get('circle') ?? ''
    }));
}
