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
