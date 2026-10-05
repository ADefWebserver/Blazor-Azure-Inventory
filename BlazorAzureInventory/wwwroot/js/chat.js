// Chat helpers: keep the message list scrolled and send on Enter (Shift+Enter inserts a new line).
export function scrollToBottom(element) {
    if (element) element.scrollTop = element.scrollHeight;
}

export function attachEnterToSend(textareaId, dotNetRef) {
    const textarea = document.getElementById(textareaId);
    if (!textarea || textarea.dataset.enterToSend) return;
    textarea.dataset.enterToSend = 'true';
    textarea.addEventListener('keydown', e => {
        if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('SendFromKeyboard', textarea.value);
        }
    });
}

// Resource links in assistant messages carry an encoded data-resource-id. The server re-validates the ID against the
// user's scan before opening anything.
export function attachResourceLinks(element, dotNetRef) {
    if (!element || element.dataset.resourceLinks) return;
    element.dataset.resourceLinks = 'true';
    element.addEventListener('click', e => {
        const link = e.target.closest('a.resource-link[data-resource-id]');
        if (!link || !element.contains(link)) return;
        e.preventDefault();
        dotNetRef.invokeMethodAsync('OpenResource', link.dataset.resourceId, link.textContent ?? '');
    });
}
