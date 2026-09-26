// @ts-check
// Small browser helpers for saving builds.

/**
 * Offers text to the user as a file download.
 * @param {string} filename @param {string} text @param {string} mimeType
 */
export function downloadText(filename, text, mimeType) {
    const url = URL.createObjectURL(new Blob([text], { type: mimeType }));
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}

/** @param {string} key @returns {string | null} null when absent or storage is unavailable */
export function load(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

/** @param {string} key @param {string} value @returns {boolean} false when storage is full or unavailable */
export function save(key, value) {
    try {
        localStorage.setItem(key, value);
        return true;
    } catch {
        return false;
    }
}
