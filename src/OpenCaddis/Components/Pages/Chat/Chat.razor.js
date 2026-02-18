export function scrollToBottom(elementId) {
    const container = document.getElementById(elementId);
    if (!container) return;

    container.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
}
