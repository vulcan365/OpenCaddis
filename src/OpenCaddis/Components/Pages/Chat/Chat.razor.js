export function scrollToBottom(elementId) {
    const container = document.getElementById(elementId);
    if (!container) return;

    container.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
}

let keepAliveIntervalId = null;

export function startKeepAlive(dotNetRef, intervalMs) {
    stopKeepAlive();
    keepAliveIntervalId = setInterval(() => {
        dotNetRef.invokeMethodAsync('OnKeepAliveTick');
    }, intervalMs);
}

export function stopKeepAlive() {
    if (keepAliveIntervalId !== null) {
        clearInterval(keepAliveIntervalId);
        keepAliveIntervalId = null;
    }
}
