// The OS clipboard, from the offscreen document only. An offscreen document never has the focus, so the async
// Clipboard API refuses it ("Document is not focused"); the old execCommand route works there with the clipboardRead
// and clipboardWrite permissions. Both events fire synchronously inside execCommand.

let editor;
function target() {
  if (!editor) {
    editor = document.createElement('div');
    editor.contentEditable = 'true';
    document.body.append(editor);
  }
  editor.focus();
  return editor;
}

/**
 * Reads plain text and the first PNG image.
 * @returns {Promise<{text?: string, png?: Uint8Array} | null>} `{}` when the clipboard holds neither (empty, or files
 * copied in Explorer), `null` when it couldn't be read.
 */
export function readClipboard() {
  return new Promise(resolve => {
    const el = target();
    let handled = false;
    const onPaste = e => {
      handled = true;
      e.preventDefault();
      const data = e.clipboardData;
      const item = {};
      const text = data.getData('text/plain');
      if (text) item.text = text;
      const image = [...data.items].find(i => i.kind === 'file' && i.type === 'image/png')?.getAsFile();
      if (!image) return resolve(item);
      image.arrayBuffer().then(
        buf => resolve({ ...item, png: new Uint8Array(buf) }),
        () => resolve(item));
    };
    el.addEventListener('paste', onPaste);
    let ok = false;
    try {
      ok = document.execCommand('paste');
    } finally {
      el.removeEventListener('paste', onPaste);
    }
    if (!ok || !handled) resolve(null);
  });
}

/** Puts plain text on the clipboard. */
export async function writeText(text) {
  let filled = false;
  const onCopy = e => {
    e.preventDefault();
    e.clipboardData.setData('text/plain', text);
    filled = true;
  };
  document.addEventListener('copy', onCopy);
  try {
    target();
    return document.execCommand('copy') && filled;
  } catch {
    return false;
  } finally {
    document.removeEventListener('copy', onCopy);
  }
}

/**
 * Always false: measured in Edge on 2026-11-02, no route writes an image from an offscreen document (a File added to
 * the copy event's items is dropped, navigator.clipboard.write needs the focus, and an HTML <img> isn't an image to
 * other apps). Incoming images go to the history, and the popup's Copy writes them while it has the focus.
 */
export async function writeImage(_png) {
  return false;
}
